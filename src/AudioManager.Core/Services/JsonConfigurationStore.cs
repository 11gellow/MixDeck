using System.Text;
using System.Security.Cryptography;
using AudioManager.Core.Configuration;
using AudioManager.Core.Interfaces;

namespace AudioManager.Core.Services;

/// <summary>同目录临时文件 + Flush(true) + 原子替换；损坏配置不会被普通保存覆盖。</summary>
public sealed class JsonConfigurationStore(string filePath) : IConfigurationStore
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? loadedFingerprint;
    private bool loaded;
    private bool blocked;
    private bool primaryValid;
    public string FilePath { get; } = Path.GetFullPath(filePath);
    public string BackupPath => FilePath + ".bak";

    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            loaded = true;
            blocked = false;
            primaryValid = false;
            loadedFingerprint = null;
            try
            {
                loadedFingerprint = await FingerprintAsync(FilePath, cancellationToken).ConfigureAwait(false);
                var loadedBytes = await ReadOptionalAsync(FilePath, cancellationToken).ConfigureAwait(false);
                if (loadedBytes is not null)
                {
                    var configuration = Decode(loadedBytes);
                    primaryValid = true;
                    return new(configuration, ConfigurationLoadKind.Loaded, null);
                }
                if (!File.Exists(BackupPath)) return new(AudioConfiguration.Empty, ConfigurationLoadKind.Missing, null);
            }
            catch (UnsupportedConfigurationVersionException exception)
            {
                blocked = true;
                return new(AudioConfiguration.Empty, ConfigurationLoadKind.Blocked, exception.Message);
            }
            catch (Exception exception) when (IsDataOrIoFailure(exception))
            {
                // 主文件不可解析时尝试备份；原文件保持原样。
            }
            try
            {
                var backupBytes = await ReadOptionalAsync(BackupPath, cancellationToken).ConfigureAwait(false);
                if (backupBytes is not null)
                    return new(Decode(backupBytes), ConfigurationLoadKind.BackupRecovered, "主配置缺失或损坏，已读取备份；原文件未改动。下次保存将修复主配置并保留有效备份。");
            }
            catch (Exception exception) when (IsDataOrIoFailure(exception)) { }
            blocked = true;
            return new(AudioConfiguration.Empty, ConfigurationLoadKind.Blocked, "无法读取有效配置和备份；自动恢复已禁用，普通保存被阻止。请检查文件权限，或显式导入/恢复默认；原文件会保留副本。");
        }
        finally { gate.Release(); }
    }

    public Task SaveAsync(AudioConfiguration configuration, CancellationToken cancellationToken = default)
        => WriteAsync(configuration, false, cancellationToken);

    public Task ReplaceAsync(AudioConfiguration configuration, CancellationToken cancellationToken = default)
        => WriteAsync(configuration, true, cancellationToken);

    public async Task<AudioConfiguration> ImportAsync(string path, CancellationToken cancellationToken = default)
        => Decode(await ReadOptionalAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("导入文件不存在。", path));

    public async Task ExportAsync(string path, AudioConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(path);
        if (string.Equals(destination, FilePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(destination, BackupPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能将导出文件写到正在使用的配置或备份路径。");
        var bytes = Encode(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // 导出为新文件；避免无提示覆盖用户的其他文件。
        await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        catch
        {
            stream.Close();
            File.Delete(destination);
            throw;
        }
    }

    private async Task WriteAsync(AudioConfiguration configuration, bool replace, CancellationToken cancellationToken)
    {
        var bytes = Encode(configuration);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            if (!loaded) throw new InvalidOperationException("保存前必须先加载配置。");
            if (blocked && !replace) throw new IOException("配置加载受阻；请显式导入或恢复默认，不能直接覆盖。");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var fileLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var currentFingerprint = await FingerprintAsync(FilePath, cancellationToken).ConfigureAwait(false);
            if (!SameBytes(loadedFingerprint, currentFingerprint))
                throw new IOException("配置已被另一个进程修改，请重新启动后再操作，避免覆盖其他修改。");
            temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if ((replace || !primaryValid) && currentFingerprint is not null)
                File.Copy(FilePath, FilePath + ".preserved-" + Guid.NewGuid().ToString("N"), false);
            if (currentFingerprint is not null)
                File.Replace(temporary, FilePath, primaryValid ? BackupPath : null);
            else File.Move(temporary, FilePath);
            temporary = null;
            loadedFingerprint = SHA256.HashData(bytes);
            primaryValid = true;
            blocked = false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            gate.Release();
        }
    }

    private static byte[] Encode(AudioConfiguration configuration)
    {
        var bytes = Encoding.UTF8.GetBytes(ConfigurationJson.Serialize(configuration));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("配置超过 4 MiB 上限。");
        return bytes;
    }

    private static AudioConfiguration Decode(byte[] bytes)
        => ConfigurationJson.Deserialize(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));

    private static async Task<byte[]?> ReadOptionalAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("配置超过 4 MiB 上限。");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("配置超过 4 MiB 上限。");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool SameBytes(byte[]? first, byte[]? second)
        => first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);
    private static bool IsDataOrIoFailure(Exception exception)
        => exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or DecoderFallbackException;

    private static async Task<byte[]?> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

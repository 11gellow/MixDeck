using AudioManager.Core.Configuration;

namespace AudioManager.Core.Interfaces;

public interface IConfigurationStore
{
    string FilePath { get; }
    Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AudioConfiguration configuration, CancellationToken cancellationToken = default);
    /// <summary>用户明确导入或恢复默认时调用；替换前保留原文件副本。</summary>
    Task ReplaceAsync(AudioConfiguration configuration, CancellationToken cancellationToken = default);
    Task<AudioConfiguration> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, AudioConfiguration configuration, CancellationToken cancellationToken = default);
}

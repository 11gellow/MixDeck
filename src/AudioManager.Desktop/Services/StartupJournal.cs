using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AudioManager.Windows;

namespace AudioManager.Desktop.Services;

/// <summary>启动里程碑单独记录，不把 UI 出现时间当作音频就绪时间。</summary>
public sealed class StartupJournal(string filePath)
{
    private readonly object sync = new();
    private readonly string runId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private readonly DateTimeOffset? logon = WindowsSessionTiming.GetLogonTime();
    private readonly HashSet<string> recorded = [];
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    public void Mark(string stage, string? detail = null, bool once = true)
    {
        lock (sync)
        {
            if (once && !recorded.Add(stage)) return;
            try
            {
                var now = DateTimeOffset.UtcNow;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 1024 * 1024)
                    File.Move(filePath, filePath + ".previous", overwrite: true);
                File.AppendAllText(filePath, JsonSerializer.Serialize(new
                {
                    RunId = runId, ProcessId = Environment.ProcessId, SessionId = Process.GetCurrentProcess().SessionId,
                    Build = typeof(StartupJournal).Assembly.ManifestModule.ModuleVersionId, Stage = stage, At = now,
                    ProcessCreatedAt = processStart, LogonAt = logon, SinceProcessMs = (now - processStart).TotalMilliseconds,
                    SinceLogonMs = logon is { } at ? (double?)(now - at).TotalMilliseconds : null,
                    JournalElapsedMs = elapsed.Elapsed.TotalMilliseconds, Detail = detail
                }) + Environment.NewLine);
            }
            catch (Exception) { /* 诊断不可阻断启动与音频恢复。 */ }
        }
    }
}

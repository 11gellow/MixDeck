using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioManager.Core.Configuration;
using AudioManager.Core.Events;
using AudioManager.Core.Models;

namespace AudioManager.Desktop.Services;

/// <summary>有界音频诊断记录。只接收现有消费者的数据，不访问音频接口或配置文件。</summary>
public sealed class AudioEventJournal(string filePath)
{
    private const long MaximumBytes = 1024 * 1024;
    private readonly Guid runId = Guid.NewGuid();
    private readonly object sync = new();
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    public string FilePath => filePath;
    public Exception? LastError { get; private set; }

    public void Record(string stage, AudioRuntimeState state, AudioStateChanged? change, AudioConfiguration? configuration)
    {
        if (change is not null && change.Kind is not (AudioStateChangeKind.SessionCreated or AudioStateChangeKind.SessionRemoved
            or AudioStateChangeKind.SessionStateChanged or AudioStateChangeKind.SessionVolumeChanged
            or AudioStateChangeKind.MasterVolumeChanged or AudioStateChangeKind.DefaultDeviceChanged
            or AudioStateChangeKind.DeviceListeningChanged or AudioStateChangeKind.ReadFailure)) return;
        Write(new
        {
            Stage = stage, state.Revision, state.CapturedAt, Change = change is null ? null : new
            {
                change.Kind, change.Origin, change.EventContext, change.DeviceId, change.SessionKey, change.Message, change.Issue
            },
            Devices = state.Devices.Where(device => device.IsListening && (change?.DeviceId is null
                || string.Equals(change.DeviceId, device.Device.Id, StringComparison.OrdinalIgnoreCase))).Select(device => new
            {
                device.Device.Id, device.MasterVolume,
                Sessions = device.Sessions.Where(session => change?.SessionKey is null || session.Key == change.SessionKey),
                Targets = configuration?.Profiles.Where(profile => configuration.DeviceProfiles.Any(selection => selection.ProfileId == profile.Id
                    && string.Equals(selection.DeviceId, device.Device.Id, StringComparison.OrdinalIgnoreCase)))
                    .SelectMany(profile => profile.Rules.Where(rule => string.Equals(rule.Target.Target.DeviceId, device.Device.Id, StringComparison.OrdinalIgnoreCase)))
            })
        });
    }

    public void RecordRestore(string deviceId, AudioSessionKey? sessionKey, AudioVolume actual)
        => Write(new { Stage = "restore-readback", DeviceId = deviceId, SessionKey = sessionKey, Actual = actual });

    private void Write(object data)
    {
        lock (sync)
        {
            try
            {
                var line = JsonSerializer.Serialize(new { Diagnostic = "AudioState", At = DateTimeOffset.UtcNow, RunId = runId,
                    ProcessId = Environment.ProcessId, Build = typeof(AudioEventJournal).Assembly.ManifestModule.ModuleVersionId, Data = data }, Options);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
                if (File.Exists(filePath) && new FileInfo(filePath).Length >= MaximumBytes)
                    File.Move(filePath, filePath + ".previous", overwrite: true);
                File.AppendAllText(filePath, line + Environment.NewLine);
                LastError = null;
            }
            // 诊断失败不能阻断音频事件处理、保存或恢复。
            catch (Exception exception) { LastError = exception; }
        }
    }
}

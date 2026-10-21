using System.Diagnostics;
using AudioManager.Core.Models;

namespace AudioManager.Windows.AudioSessions;

internal interface ISessionInfo
{
    string InstanceId { get; }
    string Identifier { get; }
    uint ProcessId { get; }
    string DisplayName { get; }
    bool IsSystemSounds { get; }
    AudioSessionState State { get; }
    AudioVolume Volume { get; }
}

internal sealed record ProcessInfo(string Name, string? Path, Exception? PathError = null);

internal static class SessionSnapshotReader
{
    internal static AudioSession Read(string deviceId, ISessionInfo session, List<AudioReadIssue> issues,
        Func<uint, ProcessInfo>? processReader = null)
        => Read(deviceId, Capture(session, issues), issues, processReader);

    // 回调中的借用接口不能跨回调保存。先复制本机字段，进程查询留给工作队列。
    internal static SessionInfoSnapshot Capture(ISessionInfo session, List<AudioReadIssue> issues)
    {
        // 必需数据失败时由枚举器跳过该实例；可选元数据失败不能让可控制的会话消失。
        var instanceId = session.InstanceId;
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var state = session.State;
        var volume = session.Volume;
        var identifier = Optional(() => session.Identifier, (string?)null, instanceId, issues);
        var processId = Optional(() => (uint?)session.ProcessId, null, instanceId, issues);
        var systemSounds = Optional(() => session.IsSystemSounds, false, instanceId, issues);
        var displayName = Optional(() => session.DisplayName, "", instanceId, issues);
        return new(instanceId, identifier, processId, displayName ?? "", systemSounds, state, volume);
    }

    internal static AudioSession Read(string deviceId, SessionInfoSnapshot session, List<AudioReadIssue> issues,
        Func<uint, ProcessInfo>? processReader = null)
    {
        var key = new AudioSessionKey(deviceId, session.InstanceId);
        var processId = session.ProcessId;
        ProcessInfo? process = null;
        if (processId is > 0)
        {
            process = Optional(() => (processReader ?? ReadProcess)(processId.Value), null, key.InstanceId, issues);
            if (process?.PathError is { } error) AddIssue(key.InstanceId, error, issues);
        }
        var name = session.IsSystemSounds ? "系统声音"
            : !string.IsNullOrWhiteSpace(session.DisplayName) ? session.DisplayName
            : process?.Name ?? (processId is > 0 ? $"进程 {processId}" : "未知应用");
        return new AudioSession(key, session.Identifier, processId, name, process?.Path, session.IsSystemSounds, session.State, session.Volume);
    }

    private static ProcessInfo ReadProcess(uint processId)
    {
        using var process = Process.GetProcessById(checked((int)processId));
        var name = process.ProcessName;
        try { return new ProcessInfo(name, process.MainModule?.FileName); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { return new ProcessInfo(name, null, error); }
    }

    private static T? Optional<T>(Func<T> read, T? fallback, string itemId, List<AudioReadIssue> issues)
    {
        try { return read(); }
        catch (Exception exception) { AddIssue(itemId, exception, issues); return fallback; }
    }

    internal static void AddIssue(string itemId, Exception exception, List<AudioReadIssue> issues)
        => issues.Add(new AudioReadIssue(itemId, exception.Message, exception.HResult));
}

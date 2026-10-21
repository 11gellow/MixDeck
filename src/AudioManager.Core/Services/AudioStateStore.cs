using System.Collections.ObjectModel;
using AudioManager.Core.Models;

namespace AudioManager.Core.Services;

/// <summary>
/// 只维护可观察的实时状态，不保存配置或执行音量写入。由调用方保证单线程访问，
/// 对外快照与内部可变集合完全分离。
/// </summary>
public sealed class AudioStateStore
{
    private readonly Dictionary<string, DeviceEntry> devices = new(StringComparer.Ordinal);
    private Dictionary<AudioDeviceRole, string?> defaults = EmptyDefaults();
    private AudioReadIssue[] issues = [];
    private long revision;
    private DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
    private bool isRunning;

    public AudioRuntimeState Snapshot()
        => new(revision, capturedAt, isRunning,
            Array.AsReadOnly(devices.Values.OrderBy(entry => entry.Device.Id, StringComparer.Ordinal)
                .Select(entry => new AudioDeviceRuntimeState(entry.Device, entry.MasterVolume, entry.IsListening,
                    Array.AsReadOnly(entry.Sessions.Values.OrderBy(session => session.Key.InstanceId, StringComparer.Ordinal).ToArray()),
                    Array.AsReadOnly(entry.Issues.ToArray()))).ToArray()),
            new ReadOnlyDictionary<AudioDeviceRole, string?>(new Dictionary<AudioDeviceRole, string?>(defaults)),
            Array.AsReadOnly(issues.ToArray()));

    public bool SetRunning(bool running)
    {
        if (isRunning == running) return false;
        isRunning = running;
        return Changed();
    }

    public bool ReplaceDevices(AudioEnumeration<AudioOutputDevice> value,
        IReadOnlyDictionary<AudioDeviceRole, string?> defaultOutputDevices)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(defaultOutputDevices);
        var incoming = new Dictionary<string, AudioOutputDevice>(StringComparer.Ordinal);
        foreach (var device in value.Items)
        {
            if (string.IsNullOrWhiteSpace(device.Id)) continue;
            incoming.TryAdd(device.Id, device);
        }

        var changed = false;
        foreach (var removedId in devices.Keys.Where(id => !incoming.ContainsKey(id)).ToArray())
        {
            devices.Remove(removedId);
            changed = true;
        }
        foreach (var (id, device) in incoming)
        {
            if (!devices.TryGetValue(id, out var entry))
            {
                devices.Add(id, new DeviceEntry(device));
                changed = true;
                continue;
            }
            if (entry.Device != device) { entry.Device = device; changed = true; }
            if (device.State != AudioDeviceState.Active) changed |= ClearEntry(entry, []);
        }

        var nextDefaults = EmptyDefaults();
        foreach (var role in Enum.GetValues<AudioDeviceRole>())
        {
            if (defaultOutputDevices.TryGetValue(role, out var id) && id is not null
                && devices.TryGetValue(id, out var entry) && entry.Device.State == AudioDeviceState.Active)
                nextDefaults[role] = id;
        }
        if (defaults.Any(pair => pair.Value != nextDefaults[pair.Key]))
        {
            defaults = nextDefaults;
            changed = true;
        }
        if (!issues.SequenceEqual(value.Issues)) { issues = value.Issues.ToArray(); changed = true; }
        return changed && Changed();
    }

    public bool SetEndpoint(string deviceId, AudioVolume master, AudioEnumeration<AudioSession> sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(sessions);
        if (!TryGetAvailable(deviceId, out var entry)) return false;
        var incoming = new Dictionary<AudioSessionKey, AudioSession>();
        foreach (var session in sessions.Items)
        {
            if (session.Key.DeviceId != deviceId || session.State == AudioSessionState.Expired) continue;
            incoming.TryAdd(session.Key, session);
        }
        if (entry.IsListening && entry.MasterVolume == master && EqualSessions(entry.Sessions, incoming)
            && entry.Issues.SequenceEqual(sessions.Issues)) return false;
        entry.IsListening = true;
        entry.MasterVolume = master;
        entry.Sessions = incoming;
        entry.Issues = sessions.Issues.ToArray();
        return Changed();
    }

    public bool ClearEndpoint(string deviceId, AudioReadIssue? issue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!devices.TryGetValue(deviceId, out var entry)) return false;
        return ClearEntry(entry, issue is null ? [] : [issue]) && Changed();
    }

    public bool SetMasterVolume(string deviceId, AudioVolume volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!TryGetListening(deviceId, out var entry) || entry.MasterVolume == volume) return false;
        entry.MasterVolume = volume;
        return Changed();
    }

    public bool UpsertSession(AudioSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.State == AudioSessionState.Expired) return RemoveSession(session.Key);
        if (!TryGetListening(session.Key.DeviceId, out var entry)) return false;
        if (entry.Sessions.TryGetValue(session.Key, out var existing) && existing == session) return false;
        entry.Sessions[session.Key] = session;
        return Changed();
    }

    public bool SetSessionVolume(AudioSessionKey key, AudioVolume volume)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!TryGetListening(key.DeviceId, out var entry) || !entry.Sessions.TryGetValue(key, out var session)
            || session.Volume == volume) return false;
        entry.Sessions[key] = session with { Volume = volume };
        return Changed();
    }

    public bool SetSessionState(AudioSessionKey key, AudioSessionState state)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (state == AudioSessionState.Expired) return RemoveSession(key);
        if (!TryGetListening(key.DeviceId, out var entry) || !entry.Sessions.TryGetValue(key, out var session)
            || session.State == state) return false;
        entry.Sessions[key] = session with { State = state };
        return Changed();
    }

    public bool RemoveSession(AudioSessionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TryGetListening(key.DeviceId, out var entry) && entry.Sessions.Remove(key) && Changed();
    }

    private bool TryGetAvailable(string id, out DeviceEntry entry)
        => devices.TryGetValue(id, out entry!) && entry.Device.State == AudioDeviceState.Active;

    private bool TryGetListening(string id, out DeviceEntry entry)
        => TryGetAvailable(id, out entry) && entry.IsListening;

    private static bool ClearEntry(DeviceEntry entry, AudioReadIssue[] nextIssues)
    {
        if (!entry.IsListening && entry.MasterVolume is null && entry.Sessions.Count == 0
            && entry.Issues.SequenceEqual(nextIssues)) return false;
        entry.IsListening = false;
        entry.MasterVolume = null;
        entry.Sessions.Clear();
        entry.Issues = nextIssues;
        return true;
    }

    private bool Changed()
    {
        revision++;
        capturedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private static bool EqualSessions(Dictionary<AudioSessionKey, AudioSession> first,
        Dictionary<AudioSessionKey, AudioSession> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && pair.Value == value);

    private static Dictionary<AudioDeviceRole, string?> EmptyDefaults()
        => Enum.GetValues<AudioDeviceRole>().ToDictionary(role => role, _ => (string?)null);

    private sealed class DeviceEntry(AudioOutputDevice device)
    {
        internal AudioOutputDevice Device { get; set; } = device;
        internal AudioVolume? MasterVolume { get; set; }
        internal bool IsListening { get; set; }
        internal Dictionary<AudioSessionKey, AudioSession> Sessions { get; set; } = [];
        internal AudioReadIssue[] Issues { get; set; } = [];
    }
}

using AudioManager.Core.Configuration;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;

namespace AudioManager.Core.Services;

/// <summary>保存与规则服务。由唯一实时消费者转发变化，不另行消费 monitor，不轮询、不自动保存实时值。</summary>
public sealed class AudioConfigurationService(IConfigurationStore store, IAudioController controller) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> failedAutomaticTargets = new(StringComparer.Ordinal);
    private AudioRuntimeState? latestState;
    public AudioConfiguration Current { get; private set; } = AudioConfiguration.Empty;
    public ConfigurationLoadResult? LoadResult { get; private set; }
    public string FilePath => store.FilePath;
    public event Action? Changed;
    public event Action<RestoreResult>? Reported;

    public async Task InitializeAsync()
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            LoadResult = await store.LoadAsync(lifetime.Token);
            Current = LoadResult.Configuration;
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    public async Task SaveSnapshotAsync(AudioRuntimeState state, string name)
    {
        if (!state.IsRunning) throw new InvalidOperationException("实时监听未运行，不能保存快照。");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entries = new List<SavedVolumeEntry>();
        var knownDevices = Current.KnownDevices.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
        var knownApps = Current.KnownApplications.ToDictionary(ConfigurationJson.IdentityKey, StringComparer.OrdinalIgnoreCase);
        foreach (var device in state.Devices.Where(device => device.IsListening && device.Device.State.HasFlag(AudioDeviceState.Active)))
        {
            knownDevices[device.Device.Id] = new(device.Device.Id, device.Device.Name);
            if (device.MasterVolume is { } master)
                entries.Add(new(new(device.Device.Id, null), device.Device.Name + " 主音量", new(master.Level, master.IsMuted)));
            foreach (var group in device.Sessions.Where(session => session.State != AudioSessionState.Expired)
                .GroupBy(session => AudioIdentityMatcher.Identify(session) is { } id ? ConfigurationJson.IdentityKey(id) : "", StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key == "")
                {
                    foreach (var session in group) Reported?.Invoke(new(session.Name, false, "程序完整路径不可读，跳过持久化匹配；不使用 PID 猜测。"));
                    continue;
                }
                var first = group.First();
                if (group.Any(session => !AudioIdentityMatcher.EqualVolume(new(first.Volume.Level, first.Volume.IsMuted), session.Volume)))
                    throw new InvalidOperationException($"{first.Name} 在设备 {device.Device.Name} 有不同音量或静音的会话，请统一后再保存。未保存任何修改。");
                var identity = AudioIdentityMatcher.Identify(first)!;
                knownApps[group.Key] = identity;
                entries.Add(new(new(device.Device.Id, identity), first.IsSystemSounds ? "系统声音" : first.Name, new(first.Volume.Level, first.Volume.IsMuted)));
            }
        }
        if (entries.Count == 0) throw new InvalidOperationException("没有可保存的音量状态。");
        var profile = new AudioProfile(Guid.NewGuid(), name.Trim(), new(state.CapturedAt, entries.ToArray()),
            entries.Select(entry => new RestoreRule(Guid.NewGuid(), true, entry with { Volume = entry.Volume with { } })).ToArray());
        await ChangeAsync(current => current with
        {
            Profiles = current.Profiles.Append(profile).ToArray(), ActiveProfileId = profile.Id,
            KnownDevices = knownDevices.Values.ToArray(), KnownApplications = knownApps.Values.ToArray()
        });
    }

    public Task SelectProfileAsync(Guid id)
        => ChangeAsync(current => current.Profiles.Any(profile => profile.Id == id)
            ? current with { ActiveProfileId = id } : throw new InvalidOperationException("方案不存在。"));

    public Task DeleteProfileAsync(Guid id)
        => ChangeAsync(current => current with
        {
            Profiles = current.Profiles.Where(profile => profile.Id != id).ToArray(),
            ActiveProfileId = current.ActiveProfileId == id ? null : current.ActiveProfileId
        });

    public Task UpdateRulesAsync(Guid profileId, IReadOnlyList<RestoreRule> rules)
        => ChangeAsync(current => !current.Profiles.Any(profile => profile.Id == profileId)
            ? throw new InvalidOperationException("方案不存在。")
            : current with { Profiles = current.Profiles.Select(profile => profile.Id == profileId ? profile with { Rules = rules.ToArray() } : profile).ToArray() });

    public Task UpdateSettingsAsync(SoftwareSettings settings)
        => ChangeAsync(current => current with { Settings = settings });

    public async Task ImportAsync(string path)
    {
        var imported = await store.ImportAsync(path, lifetime.Token);
        // 导入不能隐式开启自动写音量或登录启动，用户须在本机明确打开。
        await ChangeAsync(_ => imported with { Settings = imported.Settings with { AutomaticRestoreEnabled = false, StartAtLogin = Current.Settings.StartAtLogin } }, true);
    }

    public Task ExportAsync(string path) => store.ExportAsync(path, Current, lifetime.Token);
    public Task ResetAsync() => ChangeAsync(_ => AudioConfiguration.Empty, true);

    private async Task ChangeAsync(Func<AudioConfiguration, AudioConfiguration> update, bool replace = false)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            var next = update(Current);
            ConfigurationJson.Validate(next);
            if (replace) await store.ReplaceAsync(next, lifetime.Token);
            else await store.SaveAsync(next, lifetime.Token);
            Current = next;
            failedAutomaticTargets.Clear();
            Changed?.Invoke();
            if (latestState is { } state) await ApplyAsync(state, automatic: true);
        }
        finally { gate.Release(); }
    }

    /// <summary>手动恢复使用快照；自动恢复使用规则目标。二者都不更改快照。</summary>
    public async Task RestoreSnapshotAsync(AudioRuntimeState state, Guid profileId)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (Current.Settings.AutomaticRestoreEnabled)
                throw new InvalidOperationException("请先停用自动恢复，再手动恢复原始快照，避免与当前规则目标冲突。");
            var profile = Current.Profiles.FirstOrDefault(profile => profile.Id == profileId) ?? throw new InvalidOperationException("方案不存在。");
            await ApplyEntriesAsync(state, profile.Snapshot.Entries, automatic: false);
        }
        finally { gate.Release(); }
    }

    public async Task HandleStateAsync(AudioRuntimeState state, AudioStateChanged? change = null, bool retry = false)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (latestState is { } previous && state.Revision < previous.Revision) return;
            latestState = state;
            if (retry) failedAutomaticTargets.Clear();
            if (change is { Kind: AudioStateChangeKind.SessionRemoved, SessionKey: { } removed })
            {
                failedAutomaticTargets.Remove(AudioWriteGuard.SessionKey(removed));
                (controller as AudioWriteGuard)?.ForgetSession(removed);
            }
            if (change is { Kind: AudioStateChangeKind.DeviceListeningChanged, DeviceId: { } id }
                && state.Devices.Any(device => device.Device.Id == id && !device.IsListening))
            {
                (controller as AudioWriteGuard)?.ForgetDevice(id);
                failedAutomaticTargets.RemoveWhere(key => key == AudioWriteGuard.MasterKey(id) || key.StartsWith("session:" + id + "\n", StringComparison.Ordinal));
            }
            // 回传快照可能过时；ApplyOne 始终回读当前值，相等时不写，故 Self 不会形成回写循环。
            if (change?.Kind is not (AudioStateChangeKind.ReadFailure or AudioStateChangeKind.SessionStateChanged or AudioStateChangeKind.SessionRemoved))
                await ApplyAsync(state, automatic: true, change);
        }
        finally { gate.Release(); }
    }

    public bool IsManaged(string deviceId, AudioSession? session = null)
        => Current.Settings.AutomaticRestoreEnabled && ActiveRules.Any(rule => rule.Enabled
            && string.Equals(rule.Target.Target.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            && (session is null ? rule.Target.Target.Application is null
                : rule.Target.Target.Application is { } app && AudioIdentityMatcher.Matches(app, session)));

    private IEnumerable<RestoreRule> ActiveRules => Current.Profiles.FirstOrDefault(profile => profile.Id == Current.ActiveProfileId)?.Rules ?? [];

    private Task ApplyAsync(AudioRuntimeState state, bool automatic, AudioStateChanged? change = null)
        => !Current.Settings.AutomaticRestoreEnabled || !state.IsRunning ? Task.CompletedTask
            : ApplyEntriesAsync(state, ActiveRules.Where(rule => rule.Enabled).Select(rule => rule.Target).Where(entry =>
                change?.Kind is not (AudioStateChangeKind.MasterVolumeChanged or AudioStateChangeKind.SessionVolumeChanged or AudioStateChangeKind.SessionCreated)
                || string.Equals(entry.Target.DeviceId, change.DeviceId, StringComparison.OrdinalIgnoreCase)
                && (change.Kind == AudioStateChangeKind.MasterVolumeChanged ? entry.Target.Application is null
                    : entry.Target.Application is { } identity && state.Devices.SelectMany(device => device.Sessions)
                        .Any(session => session.Key == change.SessionKey && AudioIdentityMatcher.Matches(identity, session)))), automatic);

    private async Task ApplyEntriesAsync(AudioRuntimeState state, IEnumerable<SavedVolumeEntry> entries, bool automatic)
    {
        foreach (var entry in entries)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var device = state.Devices.FirstOrDefault(device => string.Equals(device.Device.Id, entry.Target.DeviceId, StringComparison.OrdinalIgnoreCase)
                && device.IsListening && device.Device.State.HasFlag(AudioDeviceState.Active));
            if (device is null)
            {
                if (!automatic) Reported?.Invoke(new(entry.DisplayName, false, "原输出设备不可用；保留目标，等待设备恢复。"));
                continue;
            }
            var sessions = entry.Target.Application is { } identity ? device.Sessions.Where(session => session.State != AudioSessionState.Expired && AudioIdentityMatcher.Matches(identity, session)).ToArray() : null;
            if (sessions is { Length: 0 })
            {
                if (!automatic) Reported?.Invoke(new(entry.DisplayName, false, "应用尚未创建会话；已保留规则，启用自动恢复后等待新会话。"));
                continue;
            }
            if (sessions is null) await ApplyOneAsync(entry, null, automatic);
            else foreach (var session in sessions) await ApplyOneAsync(entry, session.Key, automatic);
        }
    }

    private async Task ApplyOneAsync(SavedVolumeEntry entry, AudioSessionKey? sessionKey, bool automatic)
    {
        var key = sessionKey is null ? AudioWriteGuard.MasterKey(entry.Target.DeviceId) : AudioWriteGuard.SessionKey(sessionKey);
        if (automatic && failedAutomaticTargets.Contains(key)) return;
        try
        {
            var actual = sessionKey is null ? await controller.GetMasterVolumeAsync(entry.Target.DeviceId, lifetime.Token)
                : await controller.GetSessionVolumeAsync(sessionKey, lifetime.Token);
            if (AudioIdentityMatcher.EqualVolume(entry.Volume, actual))
            {
                if (!automatic) Reported?.Invoke(new(entry.DisplayName, true, "系统当前值已与快照一致。"));
                return;
            }
            if (Math.Abs(actual.Level - entry.Volume.Level) > .005f)
                actual = sessionKey is null ? await controller.SetMasterVolumeAsync(entry.Target.DeviceId, entry.Volume.Level, lifetime.Token)
                    : await controller.SetSessionVolumeAsync(sessionKey, entry.Volume.Level, lifetime.Token);
            if ((controller as AudioWriteGuard)?.Restriction(key) is null && actual.IsMuted != entry.Volume.IsMuted)
                actual = sessionKey is null ? await controller.SetMasterMuteAsync(entry.Target.DeviceId, entry.Volume.IsMuted, lifetime.Token)
                    : await controller.SetSessionMuteAsync(sessionKey, entry.Volume.IsMuted, lifetime.Token);
            var succeeded = AudioIdentityMatcher.EqualVolume(entry.Volume, actual);
            if (!succeeded) failedAutomaticTargets.Add(key);
            Reported?.Invoke(new(entry.DisplayName, succeeded, succeeded
                ? $"已恢复为 {actual.Level * 100:0}% / {(actual.IsMuted ? "静音" : "未静音")}"
                : $"系统未接受目标；实际为 {actual.Level * 100:0}% / {(actual.IsMuted ? "静音" : "未静音")}，停止自动重试。"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failedAutomaticTargets.Add(key);
            string actualText;
            try
            {
                var actual = sessionKey is null ? await controller.GetMasterVolumeAsync(entry.Target.DeviceId, lifetime.Token)
                    : await controller.GetSessionVolumeAsync(sessionKey, lifetime.Token);
                actualText = $"系统实际 {actual.Level * 100:0}% / {(actual.IsMuted ? "静音" : "未静音")}";
            }
            catch (Exception readException) when (readException is not OperationCanceledException) { actualText = "系统实际状态暂不可读"; }
            Reported?.Invoke(new(entry.DisplayName, false, $"无法修改：{exception.Message}；{actualText}，停止自动重试。"));
        }
    }

    public void Dispose() => lifetime.Cancel();
}

using AudioManager.Core.Configuration;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;

namespace AudioManager.Core.Services;

/// <summary>按设备记忆方案。生命周期恢复目标，用户音量事件保存目标；只由唯一实时消费者调用。</summary>
public sealed class AudioConfigurationService(IConfigurationStore store, IAudioController controller) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> failedAutomaticTargets = new(StringComparer.Ordinal);
    // 保存已确认实际值，区别恢复回调与后续用户调节；不使用事件中的过时音量。
    private readonly Dictionary<string, SavedVolume> observedVolumes = new(StringComparer.Ordinal);
    private readonly FirstPlaybackVolumeGuard firstPlaybackGuard = new();
    private AudioRuntimeState? latestState;
    private readonly Dictionary<Guid, VolumeEdit> volumeEdits = [];
    private sealed record VolumeEdit(string DeviceId, AudioSessionKey? SessionKey, Guid ProfileId, string TargetKey);
    public AudioConfiguration Current { get; private set; } = AudioConfiguration.Empty;
    public ConfigurationLoadResult? LoadResult { get; private set; }
    public string FilePath => store.FilePath;
    public event Action? Changed;
    public event Action<RestoreResult>? Reported;
    public event Action<string, AudioSessionKey?, AudioVolume>? RestoreReadback;

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

    public AudioProfile? ProfileForDevice(string? deviceId)
    {
        var id = Current.DeviceProfiles.FirstOrDefault(selection => SameDevice(selection.DeviceId, deviceId))?.ProfileId;
        return Current.Profiles.FirstOrDefault(profile => profile.Id == id);
    }

    private static bool SameDevice(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool Available(AudioDeviceRuntimeState device) => device.IsListening && device.Device.State.HasFlag(AudioDeviceState.Active);
    private static string? DefaultDevice(AudioRuntimeState state) => state.DefaultOutputDevices.TryGetValue(AudioDeviceRole.Multimedia, out var id)
        ? id : state.Devices.FirstOrDefault(Available)?.Device.Id;
    private static bool ContainsDevice(AudioProfile profile, string id) => profile.Rules.Any(rule => SameDevice(rule.Target.Target.DeviceId, id))
        || profile.Snapshot.Entries.Any(entry => SameDevice(entry.Target.DeviceId, id));
    private IEnumerable<SavedVolumeEntry> DeviceTargets(string id) => ProfileForDevice(id)?.Rules
        .Where(rule => rule.Enabled && SameDevice(rule.Target.Target.DeviceId, id)).Select(rule => rule.Target) ?? [];

    public async Task SaveSnapshotAsync(AudioRuntimeState state, string name, string? deviceId = null)
    {
        if (!state.IsRunning) throw new InvalidOperationException("实时监听未运行，不能保存快照。");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = deviceId ?? DefaultDevice(state) ?? throw new InvalidOperationException("没有可用的输出设备。");
        var device = state.Devices.FirstOrDefault(device => SameDevice(device.Device.Id, id) && Available(device))
            ?? throw new InvalidOperationException("所选输出设备不可用。");
        var entries = CaptureEntries(device, rejectConflicts: true);
        if (entries.Length == 0) throw new InvalidOperationException("没有可保存的音量状态。");
        var profile = new AudioProfile(Guid.NewGuid(), name.Trim(), new(state.CapturedAt, entries),
            entries.Select(entry => new RestoreRule(Guid.NewGuid(), true, entry with { Volume = entry.Volume with { } })).ToArray());
        await ChangeAsync(current => WithKnownEntries(Bind(current with { Profiles = current.Profiles.Append(profile).ToArray() }, id, profile.Id, state), device, entries));
    }

    private SavedVolumeEntry[] CaptureEntries(AudioDeviceRuntimeState device, bool rejectConflicts)
    {
        var entries = new List<SavedVolumeEntry>();
        if (device.MasterVolume is { } master)
            entries.Add(new(new(device.Device.Id, null), device.Device.Name + " 主音量", new(master.Level, master.IsMuted)));
        foreach (var group in device.Sessions.Where(session => session.State != AudioSessionState.Expired)
            .GroupBy(session => AudioIdentityMatcher.Identify(session) is { } id ? ConfigurationJson.IdentityKey(id) : "", StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (group.Key == "")
            {
                Reported?.Invoke(new(first.Name, false, "程序完整路径不可读，跳过持久化匹配；不使用 PID 猜测。"));
                continue;
            }
            if (group.Any(session => !AudioIdentityMatcher.EqualVolume(new(first.Volume.Level, first.Volume.IsMuted), session.Volume)))
            {
                var message = $"{first.Name} 在设备 {device.Device.Name} 有不同音量或静音的会话，请统一后再保存。";
                if (rejectConflicts) throw new InvalidOperationException(message + "未保存任何修改。");
                Reported?.Invoke(new(first.Name, false, message + "暂不为此应用选取目标；调节任一会话后将统一目标。"));
                continue;
            }
            entries.Add(new(new(device.Device.Id, AudioIdentityMatcher.Identify(first)!), first.IsSystemSounds ? "系统声音" : first.Name,
                new(first.Volume.Level, first.Volume.IsMuted)));
        }
        return entries.ToArray();
    }

    private static AudioConfiguration WithKnownEntries(AudioConfiguration current, AudioDeviceRuntimeState device, IEnumerable<SavedVolumeEntry> entries)
    {
        var devices = current.KnownDevices.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        devices[device.Device.Id] = new(device.Device.Id, device.Device.Name);
        var apps = current.KnownApplications.ToDictionary(ConfigurationJson.IdentityKey, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            if (entry.Target.Application is { } app) apps[ConfigurationJson.IdentityKey(app)] = app;
        return current with { KnownDevices = devices.Values.ToArray(), KnownApplications = apps.Values.ToArray() };
    }

    private static AudioConfiguration Bind(AudioConfiguration current, string deviceId, Guid profileId, AudioRuntimeState? state)
        => current with
        {
            DeviceProfiles = current.DeviceProfiles.Where(item => !SameDevice(item.DeviceId, deviceId)).Append(new(deviceId, profileId)).ToArray(),
            ActiveProfileId = state is null || SameDevice(DefaultDevice(state), deviceId) ? profileId : current.ActiveProfileId
        };

    public Task SelectProfileAsync(Guid id, string? deviceId = null)
    {
        var targetDevice = deviceId ?? (latestState is { } state ? DefaultDevice(state) : null)
            ?? Current.Profiles.FirstOrDefault(profile => profile.Id == id)?.Rules.FirstOrDefault()?.Target.Target.DeviceId;
        return ChangeAsync(current => current.Profiles.Any(profile => profile.Id == id && targetDevice is not null && ContainsDevice(profile, targetDevice))
            ? Bind(current, targetDevice!, id, latestState) : throw new InvalidOperationException("方案不存在或不属于当前输出设备。"), applyDevice: targetDevice);
    }

    public Task DeleteProfileAsync(Guid id) => ChangeAsync(current => current with
    {
        Profiles = current.Profiles.Where(profile => profile.Id != id).ToArray(),
        DeviceProfiles = current.DeviceProfiles.Where(item => item.ProfileId != id).ToArray(),
        ActiveProfileId = current.ActiveProfileId == id ? null : current.ActiveProfileId
    });

    /// <summary>仅合并用户实际修改的字段，不能携带界面缓存的整份规则覆盖新音量。</summary>
    public Task UpdateRuleAsync(Guid profileId, string deviceId, Guid ruleId, float? level = null, bool? muted = null, bool? enabled = null)
        => ChangeAsync(current =>
        {
            var profile = current.Profiles.FirstOrDefault(item => item.Id == profileId) ?? throw new InvalidOperationException("方案已不存在。");
            var rule = profile.Rules.FirstOrDefault(item => item.Id == ruleId && SameDevice(item.Target.Target.DeviceId, deviceId))
                ?? throw new InvalidOperationException("规则已更新或移除，已重新读取当前方案。");
            var updated = rule with { Enabled = enabled ?? rule.Enabled,
                Target = rule.Target with { Volume = new(level ?? rule.Target.Volume.Level, muted ?? rule.Target.Volume.IsMuted) } };
            return current with { Profiles = current.Profiles.Select(item => item.Id == profileId
                ? item with { Rules = item.Rules.Select(item => item.Id == ruleId ? updated : item).ToArray() } : item).ToArray() };
        }, applyDevice: deviceId, changedProfile: profileId, changedRule: ruleId);

    public Task UpdateSettingAsync(bool? startAtLogin = null, bool? showUnavailableDevices = null)
        => ChangeAsync(current => current with { Settings = current.Settings with {
            StartAtLogin = startAtLogin ?? current.Settings.StartAtLogin,
            ShowUnavailableDevices = showUnavailableDevices ?? current.Settings.ShowUnavailableDevices } });

    public async Task<Guid> BeginVolumeEditAsync(string deviceId, AudioSessionKey? sessionKey = null)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            var profile = ProfileForDevice(deviceId) ?? throw new InvalidOperationException("此设备尚未建立方案。");
            var session = sessionKey is null ? null : latestState?.Devices.FirstOrDefault(item => SameDevice(item.Device.Id, deviceId))?.Sessions.FirstOrDefault(item => item.Key == sessionKey);
            var identity = session is null ? null : AudioIdentityMatcher.Identify(session);
            if (sessionKey is not null && (session is null || identity is null)) throw new InvalidOperationException("会话身份不可用，不能开始音量编辑。");
            var token = Guid.NewGuid();
            volumeEdits.Add(token, new(deviceId, sessionKey, profile.Id, ConfigurationJson.TargetKey(new(deviceId, identity))));
            return token;
        }
        finally { gate.Release(); }
    }

    /// <summary>调用方须先等待最后一次系统写入；即使回读失败也释放延迟保存范围。</summary>
    public async Task EndVolumeEditAsync(Guid token)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (!volumeEdits.Remove(token, out var edit)) return;
            if (ProfileForDevice(edit.DeviceId)?.Id != edit.ProfileId || latestState is not { } state) return;
            var device = state.Devices.FirstOrDefault(item => SameDevice(item.Device.Id, edit.DeviceId) && Available(item));
            if (device is null || edit.SessionKey is not null && !device.Sessions.Any(session => session.Key == edit.SessionKey && session.State != AudioSessionState.Expired))
            {
                Reported?.Invoke(new("音量编辑", false, "设备或会话已失效，已结束编辑并保留原保存目标。"));
                return;
            }
            var change = new AudioStateChanged(edit.SessionKey is null ? AudioStateChangeKind.MasterVolumeChanged : AudioStateChangeKind.SessionVolumeChanged,
                AudioChangeOrigin.Self, state, edit.DeviceId, edit.SessionKey);
            await SaveUserChangeAsync(state, change, force: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { Reported?.Invoke(new("音量编辑", false, "结束编辑时无法保存实际音量：" + exception.Message)); }
        finally { gate.Release(); }
    }
    public async Task ImportAsync(string path)
    {
        var imported = await store.ImportAsync(path, lifetime.Token);
        await ChangeAsync(current => imported with { Settings = imported.Settings with { StartAtLogin = current.Settings.StartAtLogin } }, replace: true, applyAll: true);
    }
    public Task ExportAsync(string path) => store.ExportAsync(path, Current, lifetime.Token);
    public Task ResetAsync() => ChangeAsync(_ => AudioConfiguration.Empty, replace: true);

    private async Task PersistAsync(AudioConfiguration next, bool replace = false)
    {
        ConfigurationJson.Validate(next);
        if (replace) await store.ReplaceAsync(next, lifetime.Token);
        else await store.SaveAsync(next, lifetime.Token);
        Current = next;
        if (LoadResult?.Kind == ConfigurationLoadKind.Blocked)
            LoadResult = new(next, ConfigurationLoadKind.Loaded, null);
        Changed?.Invoke();
    }

    private async Task ChangeAsync(Func<AudioConfiguration, AudioConfiguration> update, bool replace = false,
        string? applyDevice = null, Guid? applyProfile = null, bool applyAll = false, Guid? changedProfile = null, Guid? changedRule = null)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            await PersistAsync(update(Current), replace);
            // 单字段编辑不能解除其他对象的失败保护，或重新应用其他目标。
            if (changedRule is null) failedAutomaticTargets.Clear();
            if (latestState is { IsRunning: true } state)
                foreach (var selection in Current.DeviceProfiles.Where(item => (changedProfile is null || item.ProfileId == changedProfile)
                    && (applyAll || SameDevice(item.DeviceId, applyDevice) || item.ProfileId == applyProfile)))
                {
                    var targets = changedRule is { } ruleId
                        ? ProfileForDevice(selection.DeviceId)!.Rules.Where(rule => rule.Id == ruleId && rule.Enabled
                            && SameDevice(rule.Target.Target.DeviceId, selection.DeviceId)).Select(rule => rule.Target)
                        : DeviceTargets(selection.DeviceId);
                    await ApplyEntriesAsync(state, targets, automatic: true);
                }
        }
        finally { gate.Release(); }
    }

    /// <summary>显式回退原始快照，同时将该设备的当前恢复目标改为快照，避免下次恢复反向覆盖。</summary>
    public async Task RestoreSnapshotAsync(AudioRuntimeState state, Guid profileId, string? deviceId = null)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            var profile = Current.Profiles.FirstOrDefault(profile => profile.Id == profileId) ?? throw new InvalidOperationException("方案不存在。");
            var id = deviceId ?? DefaultDevice(state) ?? profile.Snapshot.Entries.FirstOrDefault()?.Target.DeviceId;
            var entries = profile.Snapshot.Entries.Where(entry => SameDevice(entry.Target.DeviceId, id)).ToArray();
            if (entries.Length == 0) throw new InvalidOperationException("方案快照不包含所选输出设备。");
            var rules = profile.Rules.Where(rule => !entries.Any(entry => ConfigurationJson.TargetKey(entry.Target).Equals(ConfigurationJson.TargetKey(rule.Target.Target), StringComparison.OrdinalIgnoreCase)))
                .Concat(entries.Select(entry => new RestoreRule(Guid.NewGuid(), true, entry))).ToArray();
            await PersistAsync(Bind(Current with { Profiles = Current.Profiles.Select(item => item.Id == profileId ? item with { Rules = rules } : item).ToArray() }, id!, profileId, state));
            failedAutomaticTargets.Clear();
            await ApplyEntriesAsync(state, entries, automatic: false);
        }
        finally { gate.Release(); }
    }

    public async Task HandleStateAsync(AudioRuntimeState state, AudioStateChanged? change = null, bool retry = false)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (latestState is { } current && state.Revision < current.Revision) return;
            var previous = latestState;
            latestState = state;
            firstPlaybackGuard.Observe(state);
            if (retry) failedAutomaticTargets.Clear();
            if (change is { Kind: AudioStateChangeKind.SessionRemoved, SessionKey: { } removed })
            {
                failedAutomaticTargets.Remove(AudioWriteGuard.SessionKey(removed));
                observedVolumes.Remove(AudioWriteGuard.SessionKey(removed));
                (controller as AudioWriteGuard)?.ForgetSession(removed);
            }
            foreach (var device in previous?.Devices.Where(device => Available(device)
                && !state.Devices.Any(current => SameDevice(current.Device.Id, device.Device.Id) && Available(current))) ?? [])
            {
                (controller as AudioWriteGuard)?.ForgetDevice(device.Device.Id);
                foreach (var key in observedVolumes.Keys.Where(key => key == AudioWriteGuard.MasterKey(device.Device.Id) || key.StartsWith("session:" + device.Device.Id + "\n", StringComparison.Ordinal)).ToArray())
                { observedVolumes.Remove(key); failedAutomaticTargets.Remove(key); }
            }
            if (!state.IsRunning || LoadResult?.Kind == ConfigurationLoadKind.Blocked) return;
            await EnsureDeviceProfilesAsync(state);
            var activated = state.Devices.Where(device => Available(device) && (retry || previous is not { IsRunning: true }
                || !previous.Devices.Any(old => SameDevice(old.Device.Id, device.Device.Id) && Available(old))
                || !SameDevice(DefaultDevice(previous), DefaultDevice(state)) && SameDevice(device.Device.Id, DefaultDevice(state)))).ToArray();
            foreach (var device in activated)
            {
                await ApplyEntriesAsync(state, DeviceTargets(device.Device.Id), automatic: true);
                await RememberNewTargetsAsync(device);
            }
            if (change is { Kind: AudioStateChangeKind.SessionCreated, SessionKey: { } created })
            {
                var device = state.Devices.FirstOrDefault(device => SameDevice(device.Device.Id, created.DeviceId) && Available(device));
                var session = device?.Sessions.FirstOrDefault(session => session.Key == created);
                if (device is not null && session is not null)
                {
                    await ApplyEntriesAsync(state, DeviceTargets(device.Device.Id).Where(entry => entry.Target.Application is { } app && AudioIdentityMatcher.Matches(app, session)), automatic: true);
                    await RememberNewTargetsAsync(device);
                }
            }
            if (change is { Kind: AudioStateChangeKind.MasterVolumeChanged or AudioStateChangeKind.SessionVolumeChanged,
                Origin: AudioChangeOrigin.Self or AudioChangeOrigin.External } && !activated.Any(device => SameDevice(device.Device.Id, change.DeviceId)))
                await SaveUserChangeAsync(state, change);
        }
        finally { gate.Release(); }
    }

    private async Task EnsureDeviceProfilesAsync(AudioRuntimeState state)
    {
        var next = Current;
        foreach (var device in state.Devices.Where(Available))
        {
            var id = device.Device.Id;
            if (next.DeviceProfiles.Any(item => SameDevice(item.DeviceId, id))) continue;
            var profile = next.Profiles.FirstOrDefault(profile => profile.Id == next.ActiveProfileId && ContainsDevice(profile, id))
                ?? next.Profiles.FirstOrDefault(profile => ContainsDevice(profile, id));
            if (profile is null)
            {
                var entries = CaptureEntries(device, rejectConflicts: false);
                if (entries.Length == 0) continue;
                profile = new(Guid.NewGuid(), device.Device.Name + " 默认方案", new(state.CapturedAt, entries), entries.Select(entry => new RestoreRule(Guid.NewGuid(), true, entry)).ToArray());
                next = WithKnownEntries(next with { Profiles = next.Profiles.Append(profile).ToArray() }, device, entries);
            }
            next = Bind(next, id, profile.Id, state);
        }
        var activeId = next.DeviceProfiles.FirstOrDefault(item => SameDevice(item.DeviceId, DefaultDevice(state)))?.ProfileId;
        if (next.ActiveProfileId != activeId) next = next with { ActiveProfileId = activeId };
        if (!Equals(next, Current)) await PersistAsync(next);
    }

    private async Task RememberNewTargetsAsync(AudioDeviceRuntimeState device)
    {
        var profile = ProfileForDevice(device.Device.Id);
        if (profile is null) return;
        var unseen = device with
        {
            MasterVolume = profile.Rules.Any(rule => SameDevice(rule.Target.Target.DeviceId, device.Device.Id) && rule.Target.Target.Application is null) ? null : device.MasterVolume,
            Sessions = device.Sessions.Where(session => !profile.Rules.Any(rule => SameDevice(rule.Target.Target.DeviceId, device.Device.Id)
                && rule.Target.Target.Application is { } app && AudioIdentityMatcher.Matches(app, session))).ToArray()
        };
        var entries = CaptureEntries(unseen, rejectConflicts: false);
        if (entries.Length == 0) return;
        await PersistAsync(WithKnownEntries(Current with { Profiles = Current.Profiles.Select(item => item.Id == profile.Id
            ? item with { Rules = item.Rules.Concat(entries.Select(entry => new RestoreRule(Guid.NewGuid(), true, entry))).ToArray() } : item).ToArray() }, device, entries));
    }

    private async Task SaveUserChangeAsync(AudioRuntimeState state, AudioStateChanged change, bool force = false)
    {
        var device = state.Devices.FirstOrDefault(device => SameDevice(device.Device.Id, change.DeviceId) && Available(device));
        if (device is null || ProfileForDevice(device.Device.Id) is not { } profile) return;
        var session = change.Kind == AudioStateChangeKind.SessionVolumeChanged ? device.Sessions.FirstOrDefault(session => session.Key == change.SessionKey) : null;
        if (change.Kind == AudioStateChangeKind.SessionVolumeChanged && (session is null || session.State == AudioSessionState.Expired)) return;
        var identity = session is null ? null : AudioIdentityMatcher.Identify(session);
        if (session is not null && identity is null)
        {
            Reported?.Invoke(new(session.Name, false, "程序完整路径不可读，当前音量已保留但不能自动保存身份。"));
            return;
        }
        var editingKey = ConfigurationJson.TargetKey(new(device.Device.Id, identity));
        if (volumeEdits.Values.Any(edit => edit.TargetKey.Equals(editingKey, StringComparison.OrdinalIgnoreCase))) return;
        var key = session is null ? AudioWriteGuard.MasterKey(device.Device.Id) : AudioWriteGuard.SessionKey(session.Key);
        if (failedAutomaticTargets.Contains(key) || (controller as AudioWriteGuard)?.Restriction(key) is not null) return;
        var actual = session is null ? await controller.GetMasterVolumeAsync(device.Device.Id, lifetime.Token)
            : await controller.GetSessionVolumeAsync(session.Key, lifetime.Token);
        if (!force && observedVolumes.TryGetValue(key, out var observed) && SameActualVolume(observed, actual)) return;
        if (!force && session is not null)
        {
            var saved = profile.Rules.FirstOrDefault(rule => rule.Enabled && SameDevice(rule.Target.Target.DeviceId, device.Device.Id)
                && rule.Target.Target.Application is { } app && AudioIdentityMatcher.Matches(app, session));
            if (saved is not null && firstPlaybackGuard.TryProtect(session, change, saved.Target.Volume, observedVolumes.GetValueOrDefault(key), actual))
            {
                await ApplyOneAsync(saved.Target, session.Key, automatic: true);
                return;
            }
        }
        var entry = new SavedVolumeEntry(new(device.Device.Id, identity), session is null ? device.Device.Name + " 主音量" : session.IsSystemSounds ? "系统声音" : session.Name,
            new(actual.Level, actual.IsMuted));
        var targetKey = ConfigurationJson.TargetKey(entry.Target);
        var rule = profile.Rules.FirstOrDefault(rule => string.Equals(ConfigurationJson.TargetKey(rule.Target.Target), targetKey, StringComparison.OrdinalIgnoreCase));
        if (rule is null || !SameActualVolume(rule.Target.Volume, actual))
        {
            var rules = rule is null ? profile.Rules.Append(new RestoreRule(Guid.NewGuid(), true, entry)).ToArray()
                : profile.Rules.Select(item => item.Id == rule.Id ? item with { Target = entry } : item).ToArray();
            try
            {
                await PersistAsync(WithKnownEntries(Current with { Profiles = Current.Profiles.Select(item => item.Id == profile.Id ? item with { Rules = rules } : item).ToArray() }, device, [entry]));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Reported?.Invoke(new(entry.DisplayName, false, "当前音量已生效，但自动保存失败：" + exception.Message));
                return;
            }
            Reported?.Invoke(new(entry.DisplayName, true, $"已自动保存到“{profile.Name}”：{actual.Level * 100:0}% / {(actual.IsMuted ? "静音" : "未静音")}"));
        }
        observedVolumes[key] = entry.Volume;
        // 同设备同路径的其他会话同步最新用户目标，回传通过实际值基线去重。
        if (session is not null)
            foreach (var peer in device.Sessions.Where(peer => peer.Key != session.Key && peer.State != AudioSessionState.Expired && AudioIdentityMatcher.Matches(identity!, peer)))
                await ApplyOneAsync(entry, peer.Key, automatic: true);
    }

    private static bool SameActualVolume(SavedVolume saved, AudioVolume actual)
        => saved.IsMuted == actual.IsMuted && Math.Abs(saved.Level - actual.Level) < .00001f;

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
                if (!automatic) Reported?.Invoke(new(entry.DisplayName, false, "应用尚未创建会话；已保留规则，等待新会话自动恢复。"));
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
            observedVolumes[key] = new(actual.Level, actual.IsMuted);
            if (AudioIdentityMatcher.EqualVolume(entry.Volume, actual))
            {
                RestoreReadback?.Invoke(entry.Target.DeviceId, sessionKey, actual);
                if (!automatic) Reported?.Invoke(new(entry.DisplayName, true, "系统当前值已与快照一致。"));
                return;
            }
            if (Math.Abs(actual.Level - entry.Volume.Level) > .005f)
                actual = sessionKey is null ? await controller.SetMasterVolumeAsync(entry.Target.DeviceId, entry.Volume.Level, lifetime.Token)
                    : await controller.SetSessionVolumeAsync(sessionKey, entry.Volume.Level, lifetime.Token);
            if ((controller as AudioWriteGuard)?.Restriction(key) is null && actual.IsMuted != entry.Volume.IsMuted)
                actual = sessionKey is null ? await controller.SetMasterMuteAsync(entry.Target.DeviceId, entry.Volume.IsMuted, lifetime.Token)
                    : await controller.SetSessionMuteAsync(sessionKey, entry.Volume.IsMuted, lifetime.Token);
            observedVolumes[key] = new(actual.Level, actual.IsMuted);
            RestoreReadback?.Invoke(entry.Target.DeviceId, sessionKey, actual);
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
                observedVolumes[key] = new(actual.Level, actual.IsMuted);
                RestoreReadback?.Invoke(entry.Target.DeviceId, sessionKey, actual);
                actualText = $"系统实际 {actual.Level * 100:0}% / {(actual.IsMuted ? "静音" : "未静音")}";
            }
            catch (Exception readException) when (readException is not OperationCanceledException) { actualText = "系统实际状态暂不可读"; }
            Reported?.Invoke(new(entry.DisplayName, false, $"无法修改：{exception.Message}；{actualText}，停止自动重试。"));
        }
    }

    public void Dispose() => lifetime.Cancel();
}

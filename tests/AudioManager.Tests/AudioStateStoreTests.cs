using AudioManager.Core.Models;
using AudioManager.Core.Services;

namespace AudioManager.Tests;

public sealed class AudioStateStoreTests
{
    [Fact]
    public void StartupKeepsAllDeviceStatesAndOnlyAvailableDefaultRoles()
    {
        var store = new AudioStateStore();
        var issue = new AudioReadIssue("bad-device", "设备信息不完整", 5);
        Assert.True(store.ReplaceDevices(new([Device("active"), Device("off", AudioDeviceState.Disabled)], [issue]),
            new Dictionary<AudioDeviceRole, string?>
            {
                [AudioDeviceRole.Console] = "active",
                [AudioDeviceRole.Multimedia] = "off",
                [AudioDeviceRole.Communications] = "missing"
            }));
        Assert.True(store.SetRunning(true));
        var state = store.Snapshot();
        Assert.True(state.IsRunning);
        Assert.Equal(2, state.Devices.Count);
        Assert.Equal("active", state.DefaultOutputDevices[AudioDeviceRole.Console]);
        Assert.Null(state.DefaultOutputDevices[AudioDeviceRole.Multimedia]);
        Assert.Null(state.DefaultOutputDevices[AudioDeviceRole.Communications]);
        Assert.Equal(issue, Assert.Single(state.Issues));
        Assert.All(state.Devices, entry =>
        {
            Assert.Null(entry.MasterVolume);
            Assert.Empty(entry.Sessions);
            Assert.False(entry.IsListening);
        });
    }

    [Fact]
    public void EndpointSnapshotDeduplicatesRuntimeKeysWithoutMergingProcessesOrDevices()
    {
        var store = Create("first", "second");
        var sameProcessFirst = Session("first", "one");
        var sameProcessSecond = Session("first", "two");
        var crossDevice = Session("second", "one");
        Assert.True(store.SetEndpoint("first", new(0.7f, false),
            new([sameProcessFirst, sameProcessFirst, sameProcessSecond, crossDevice,
                Session("first", "expired") with { State = AudioSessionState.Expired }], [])));
        Assert.True(store.SetEndpoint("second", new(0.4f, true), new([crossDevice], [])));
        var state = store.Snapshot();
        Assert.Equal(2, state.Devices[0].Sessions.Count);
        Assert.Single(state.Devices[1].Sessions);
        Assert.Equal(3, state.Devices.SelectMany(device => device.Sessions).Select(session => session.Key).Distinct().Count());
        Assert.All(state.Devices.SelectMany(device => device.Sessions), session => Assert.Equal((uint)42, session.ProcessId));
    }

    [Fact]
    public void DuplicateUpdatesDoNotCreateNewRevisionsOrChangeCaptureTime()
    {
        var store = Create("device");
        var session = Session("device", "one");
        store.SetEndpoint("device", new(0.7f, false), new([session], []));
        var before = store.Snapshot();
        Assert.False(store.SetRunning(true));
        Assert.False(store.ReplaceDevices(new([Device("device")], []), new Dictionary<AudioDeviceRole, string?>()));
        Assert.False(store.SetEndpoint("device", new(0.7f, false), new([session, session], [])));
        Assert.False(store.UpsertSession(session));
        Assert.False(store.SetMasterVolume("device", new(0.7f, false)));
        Assert.False(store.SetSessionVolume(session.Key, session.Volume));
        Assert.False(store.SetSessionState(session.Key, session.State));
        Assert.False(store.RemoveSession(new("device", "missing")));
        var after = store.Snapshot();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.CapturedAt, after.CapturedAt);
    }

    [Fact]
    public void ReturnedSnapshotsAndCallerOwnedCollectionsRemainSeparate()
    {
        var store = new AudioStateStore();
        var inputDevices = new List<AudioOutputDevice> { Device("device") };
        var inputIssues = new List<AudioReadIssue> { new("device", "读取部分失败", 5) };
        var inputDefaults = new Dictionary<AudioDeviceRole, string?> { [AudioDeviceRole.Multimedia] = "device" };
        store.ReplaceDevices(new(inputDevices, inputIssues), inputDefaults);
        var inputSessions = new List<AudioSession> { Session("device", "one") };
        var endpointIssues = new List<AudioReadIssue> { new("one", "路径不可读", 6) };
        store.SetEndpoint("device", new(0.7f, false), new(inputSessions, endpointIssues));
        var previous = store.Snapshot();
        inputDevices.Clear();
        inputSessions.Clear();
        inputIssues.Clear();
        endpointIssues.Clear();
        inputDefaults.Clear();
        store.SetSessionVolume(new("device", "one"), new(0.2f, true));
        store.SetMasterVolume("device", new(0.5f, true));
        var current = store.Snapshot();
        Assert.Equal(new AudioVolume(0.3f, false), Assert.Single(previous.Devices).Sessions[0].Volume);
        Assert.Equal(new AudioVolume(0.7f, false), previous.Devices[0].MasterVolume);
        Assert.Equal(new AudioVolume(0.2f, true), current.Devices[0].Sessions[0].Volume);
        Assert.Equal(new AudioVolume(0.5f, true), current.Devices[0].MasterVolume);
        Assert.Equal("device", current.DefaultOutputDevices[AudioDeviceRole.Multimedia]);
        Assert.Single(current.Issues);
        Assert.Single(current.Devices[0].Issues);
        Assert.Throws<NotSupportedException>(() => ((IList<AudioDeviceRuntimeState>)current.Devices).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AudioSession>)current.Devices[0].Sessions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<AudioDeviceRole, string?>)current.DefaultOutputDevices).Clear());
    }

    [Fact]
    public void SessionVolumeMuteAndStateChangesAffectOnlyTheExactRuntimeKey()
    {
        var store = Create("first", "second");
        var selected = Session("first", "one");
        var sibling = Session("first", "two");
        var otherDevice = Session("second", "one");
        store.SetEndpoint("first", new(0.7f, false), new([selected, sibling], []));
        store.SetEndpoint("second", new(0.4f, false), new([otherDevice], []));
        Assert.True(store.SetSessionVolume(selected.Key, new(0.1f, true)));
        Assert.True(store.SetSessionState(selected.Key, AudioSessionState.Inactive));
        var state = store.Snapshot();
        var sessions = state.Devices.SelectMany(device => device.Sessions).ToDictionary(session => session.Key);
        Assert.Equal(new AudioVolume(0.1f, true), sessions[selected.Key].Volume);
        Assert.Equal(AudioSessionState.Inactive, sessions[selected.Key].State);
        Assert.Equal(sibling, sessions[sibling.Key]);
        Assert.Equal(otherDevice, sessions[otherDevice.Key]);
    }

    [Fact]
    public void ExpiredSessionsAreRemovedAndLaterVolumeUpdatesCannotRecreateThem()
    {
        var store = Create("device");
        var session = Session("device", "one");
        store.SetEndpoint("device", new(0.7f, false), new([session], []));
        Assert.True(store.SetSessionState(session.Key, AudioSessionState.Expired));
        Assert.False(store.SetSessionVolume(session.Key, new(0.4f, false)));
        Assert.False(store.UpsertSession(session with { State = AudioSessionState.Expired }));
        Assert.Empty(Assert.Single(store.Snapshot().Devices).Sessions);
    }

    [Fact]
    public void DisconnectedOrRemovedDeviceDropsItsRuntimeObjectsAndDefaults()
    {
        var store = Create("active", "removed");
        store.ReplaceDevices(new([Device("active"), Device("removed")], []),
            new Dictionary<AudioDeviceRole, string?> { [AudioDeviceRole.Multimedia] = "removed" });
        store.SetEndpoint("active", new(0.7f, false), new([Session("active", "one")], []));
        store.SetEndpoint("removed", new(0.6f, false), new([Session("removed", "one")], []));
        Assert.True(store.ReplaceDevices(new([Device("active", AudioDeviceState.Unplugged)], []),
            new Dictionary<AudioDeviceRole, string?> { [AudioDeviceRole.Multimedia] = "removed" }));
        var state = store.Snapshot();
        var endpoint = Assert.Single(state.Devices);
        Assert.False(endpoint.IsListening);
        Assert.Null(endpoint.MasterVolume);
        Assert.Empty(endpoint.Sessions);
        Assert.Null(state.DefaultOutputDevices[AudioDeviceRole.Multimedia]);
        Assert.False(store.UpsertSession(Session("active", "late")));
        Assert.False(store.SetMasterVolume("active", new(0.2f, false)));
        Assert.False(store.SetEndpoint("removed", new(0.5f, false), new([], [])));
    }

    [Fact]
    public void AvailableDeviceMetadataRefreshRetainsLiveSessionState()
    {
        var store = Create("device");
        store.SetEndpoint("device", new(0.7f, true), new([Session("device", "one")], []));
        Assert.True(store.ReplaceDevices(new([Device("device") with { Name = "新名称" }], []),
            new Dictionary<AudioDeviceRole, string?> { [AudioDeviceRole.Communications] = "device" }));
        var state = store.Snapshot();
        var endpoint = Assert.Single(state.Devices);
        Assert.Equal("新名称", endpoint.Device.Name);
        Assert.True(endpoint.IsListening);
        Assert.Equal(new AudioVolume(0.7f, true), endpoint.MasterVolume);
        Assert.Single(endpoint.Sessions);
        Assert.Equal("device", state.DefaultOutputDevices[AudioDeviceRole.Communications]);
    }

    [Fact]
    public void FailedEndpointCanBeClearedAndRebuiltWithoutAcceptingLateIncrements()
    {
        var store = Create("device");
        var session = Session("device", "one");
        store.SetEndpoint("device", new(0.7f, false), new([session], []));
        var problem = new AudioReadIssue("device", "接口失效", unchecked((int)0x88890004));
        Assert.True(store.ClearEndpoint("device", problem));
        Assert.False(store.ClearEndpoint("device", problem));
        Assert.False(store.UpsertSession(Session("device", "late")));
        Assert.False(store.SetSessionVolume(session.Key, new(0.4f, false)));
        Assert.False(store.SetMasterVolume("device", new(0.2f, false)));
        var failed = Assert.Single(store.Snapshot().Devices);
        Assert.Equal(problem, Assert.Single(failed.Issues));
        Assert.Empty(failed.Sessions);
        Assert.True(store.SetEndpoint("device", new(0.5f, false), new([Session("device", "rebuilt")], [])));
        var recovered = Assert.Single(store.Snapshot().Devices);
        Assert.True(recovered.IsListening);
        Assert.Empty(recovered.Issues);
        Assert.Equal("rebuilt", Assert.Single(recovered.Sessions).Key.InstanceId);
    }

    [Fact]
    public void MissingObjectsCannotFabricateRuntimeState()
    {
        var store = Create("device");
        var revision = store.Snapshot().Revision;
        Assert.False(store.SetEndpoint("missing", new(0.3f, false), new([], [])));
        Assert.False(store.ClearEndpoint("missing"));
        Assert.False(store.SetMasterVolume("missing", new(0.4f, false)));
        Assert.False(store.UpsertSession(Session("missing", "one")));
        Assert.False(store.SetSessionVolume(new("device", "missing"), new(0.3f, false)));
        Assert.False(store.SetSessionState(new("device", "missing"), AudioSessionState.Active));
        Assert.False(store.RemoveSession(new("device", "missing")));
        Assert.Equal(revision, store.Snapshot().Revision);
        Assert.Empty(Assert.Single(store.Snapshot().Devices).Sessions);
    }

    private static AudioStateStore Create(params string[] ids)
    {
        var store = new AudioStateStore();
        store.ReplaceDevices(new(ids.Select(id => Device(id)).ToArray(), []), new Dictionary<AudioDeviceRole, string?>());
        store.SetRunning(true);
        return store;
    }

    private static AudioOutputDevice Device(string id, AudioDeviceState state = AudioDeviceState.Active)
        => new(id, $"输出 {id}", state);

    private static AudioSession Session(string device, string instance)
        => new(new(device, instance), "identifier", 42, "player", "player.exe", false,
            AudioSessionState.Active, new AudioVolume(0.3f, false));
}

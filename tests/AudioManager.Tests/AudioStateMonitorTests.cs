using System.Collections.Concurrent;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Windows;
using AudioManager.Windows.AudioSessions;
using AudioManager.Windows.Interop;
using AudioManager.Windows.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioManager.Tests;

public sealed class AudioStateMonitorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartReusesRegistrationsAndOnlyActiveEndpointsRunOnOneMtaThread()
    {
        var world = new World(Initial(Device("active"), Device("unplugged", AudioDeviceState.Unplugged)));
        using var monitor = world.CreateMonitor();
        var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => monitor.StartAsync()));
        Assert.All(starts, state => Assert.True(state.IsRunning));
        var active = starts[^1].Devices.Single(item => item.Device.Id == "active");
        Assert.True(active.IsListening);
        Assert.NotNull(active.MasterVolume);
        Assert.False(starts[^1].Devices.Single(item => item.Device.Id == "unplugged").IsListening);
        var deviceSource = Assert.Single(world.DeviceSources);
        var endpoint = Assert.Single(world.Endpoints);
        Assert.Equal("active", endpoint.DeviceId);
        Assert.Equal(1, deviceSource.InitializeCalls);
        Assert.Equal(ApartmentState.MTA, endpoint.Source.InitializeApartment);
        Assert.Equal(deviceSource.InitializeThread, endpoint.Source.InitializeThread);
        Assert.Equal(deviceSource.InitializeThread, world.FactoryThread);
        await monitor.StopAsync();
        Assert.Equal(1, endpoint.Source.DisposeCalls);
        Assert.Equal(endpoint.Source.InitializeThread, endpoint.Source.DisposeThread);
        Assert.Equal(deviceSource.InitializeThread, deviceSource.DisposeThread);
    }

    [Fact]
    public async Task VolumeChangesTrackExactContextAndDuplicatesDoNotChangeRevision()
    {
        var world = new World(Initial(Device("device")))
        {
            NewEndpoint = _ => new FakeEndpoint([Session("device", "session")])
        };
        using var monitor = world.CreateMonitor();
        await monitor.StartAsync();
        await using var events = monitor.ReadChangesAsync().GetAsyncEnumerator();
        var endpoint = Assert.Single(world.Endpoints).Source;
        var externalContext = Guid.NewGuid();
        endpoint.Emit(new EndpointSessionVolumeChanged("session", new(0.2f, true), externalContext));
        var external = await NextAsync(events, change => change.Kind == AudioStateChangeKind.SessionVolumeChanged);
        Assert.Equal(AudioChangeOrigin.External, external.Origin);
        Assert.Equal(externalContext, external.EventContext);
        Assert.Equal(new AudioSessionKey("device", "session"), external.SessionKey);
        Assert.Equal(new AudioVolume(0.2f, true), Assert.Single(Assert.Single(external.State.Devices).Sessions).Volume);

        endpoint.Emit(new EndpointSessionVolumeChanged("session", new(0.4f, false), AudioOperationContext.Id));
        var self = await NextAsync(events, change => change.Kind == AudioStateChangeKind.SessionVolumeChanged);
        Assert.Equal(AudioChangeOrigin.Self, self.Origin);
        Assert.Equal(AudioOperationContext.Id, self.EventContext);
        endpoint.Emit(new EndpointSessionVolumeChanged("session", new(0.4f, false), Guid.NewGuid()));
        Assert.Equal(self.State.Revision, (await monitor.GetStateAsync()).Revision);

        endpoint.Emit(new EndpointMasterVolumeChanged(new(0.3f, true), AudioOperationContext.Id));
        var master = await NextAsync(events, change => change.Kind == AudioStateChangeKind.MasterVolumeChanged);
        Assert.Equal(AudioChangeOrigin.Self, master.Origin);
        Assert.Equal(new AudioVolume(0.3f, true), Assert.Single(master.State.Devices).MasterVolume);
        endpoint.Emit(new EndpointMasterVolumeChanged(new(0.3f, true), externalContext));
        Assert.Equal(master.State.Revision, (await monitor.GetStateAsync()).Revision);
    }

    [Fact]
    public async Task SessionCreationStateAndExitPreserveOtherInstancesAndDevices()
    {
        var world = new World(Initial(Device("first"), Device("second")))
        {
            NewEndpoint = id => new FakeEndpoint([Session(id, "shared-instance-name")])
        };
        using var monitor = world.CreateMonitor();
        await monitor.StartAsync();
        var first = world.Endpoints.Single(item => item.DeviceId == "first").Source;
        first.Emit(new EndpointSessionCreated(Data("created")));
        first.Emit(new EndpointSessionCreated(Data("created")));
        var state = await monitor.GetStateAsync();
        Assert.Equal(2, state.Devices.Single(item => item.Device.Id == "first").Sessions.Count);
        Assert.Single(state.Devices.Single(item => item.Device.Id == "second").Sessions);
        first.Emit(new EndpointSessionStateChanged("created", AudioSessionState.Inactive));
        state = await monitor.GetStateAsync();
        Assert.Equal(AudioSessionState.Inactive,
            state.Devices.Single(item => item.Device.Id == "first").Sessions.Single(item => item.Key.InstanceId == "created").State);
        first.Emit(new EndpointSessionStateChanged("created", AudioSessionState.Expired));
        first.Emit(new EndpointSessionRemoved("created"));
        state = await monitor.GetStateAsync();
        Assert.All(state.Devices, item => Assert.Equal("shared-instance-name", Assert.Single(item.Sessions).Key.InstanceId));
        var revision = state.Revision;
        first.Emit(new EndpointSessionVolumeChanged("created", new(0.1f, false), Guid.Empty));
        Assert.Equal(revision, (await monitor.GetStateAsync()).Revision);
    }

    [Fact]
    public async Task DefaultChangesUseReportedRoleAndNullWithoutRebuildingHealthyEndpoints()
    {
        var world = new World(Initial(Device("first"), Device("second")));
        using var monitor = world.CreateMonitor();
        var initial = await monitor.StartAsync();
        var source = Assert.Single(world.DeviceSources);
        await using var events = monitor.ReadChangesAsync().GetAsyncEnumerator();
        source.Emit(new(DeviceNotificationKind.DefaultChanged, "second", Role: AudioDeviceRole.Communications));
        var changed = await NextAsync(events, change => change.Kind == AudioStateChangeKind.DefaultDeviceChanged);
        Assert.Equal("second", changed.State.DefaultOutputDevices[AudioDeviceRole.Communications]);
        Assert.Equal(initial.DefaultOutputDevices[AudioDeviceRole.Multimedia], changed.State.DefaultOutputDevices[AudioDeviceRole.Multimedia]);
        source.Emit(new(DeviceNotificationKind.DefaultChanged, null, Role: AudioDeviceRole.Communications));
        changed = await NextAsync(events, change => change.Kind == AudioStateChangeKind.DefaultDeviceChanged);
        Assert.Null(changed.State.DefaultOutputDevices[AudioDeviceRole.Communications]);
        Assert.Equal(2, world.Endpoints.Count);
        Assert.All(world.Endpoints, item => Assert.Equal(0, item.Source.DisposeCalls));
    }

    [Fact]
    public async Task DisconnectAndReconnectClearOldStateAndIgnoreOldEndpointCallbacks()
    {
        var world = new World(Initial(Device("device")))
        {
            NewEndpoint = _ => new FakeEndpoint([Session("device", "seed")])
        };
        using var monitor = world.CreateMonitor();
        await monitor.StartAsync();
        var old = Assert.Single(world.Endpoints).Source;
        var source = Assert.Single(world.DeviceSources);
        world.Current = Initial(Device("device", AudioDeviceState.Unplugged));
        source.Emit(new(DeviceNotificationKind.StateChanged, "device", AudioDeviceState.Unplugged));
        var disconnected = Assert.Single((await monitor.GetStateAsync()).Devices);
        Assert.False(disconnected.IsListening);
        Assert.Null(disconnected.MasterVolume);
        Assert.Empty(disconnected.Sessions);
        Assert.Equal(1, old.DisposeCalls);
        world.Current = Initial(Device("device"));
        source.Emit(new(DeviceNotificationKind.StateChanged, "device", AudioDeviceState.Active));
        Assert.True(Assert.Single((await monitor.GetStateAsync()).Devices).IsListening);
        Assert.Equal(2, world.Endpoints.Count);
        old.Emit(new EndpointMasterVolumeChanged(new(0.01f, true), Guid.Empty));
        Assert.Equal(new AudioVolume(0.5f, false), Assert.Single((await monitor.GetStateAsync()).Devices).MasterVolume);
    }

    [Fact]
    public async Task RemovedAndAddedDeviceEventsUpdateInventoryAndBuildOnlyNewEndpoints()
    {
        var world = new World(Initial(Device("first"), Device("second")));
        using var monitor = world.CreateMonitor();
        await monitor.StartAsync();
        var first = world.Endpoints.Single(item => item.DeviceId == "first").Source;
        var second = world.Endpoints.Single(item => item.DeviceId == "second").Source;
        var source = Assert.Single(world.DeviceSources);
        world.Current = Initial(Device("second"));
        source.Emit(new(DeviceNotificationKind.Removed, "first"));
        Assert.Equal("second", Assert.Single((await monitor.GetStateAsync()).Devices).Device.Id);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(0, second.DisposeCalls);
        world.Current = Initial(Device("second"), Device("third"));
        source.Emit(new(DeviceNotificationKind.Added, "third"));
        var state = await monitor.GetStateAsync();
        Assert.Equal(new[] { "second", "third" }, state.Devices.Select(item => item.Device.Id).Order().ToArray());
        Assert.All(state.Devices, item => Assert.True(item.IsListening));
        Assert.Equal(3, world.Endpoints.Count);
        Assert.Equal(0, second.DisposeCalls);
    }

    [Fact]
    public async Task InvalidatedEndpointReleasesOldObjectsAndRecoversAfterTransientFailures()
    {
        var attempts = 0;
        var world = new World(Initial(Device("device")))
        {
            NewEndpoint = _ => ++attempts is 2 or 3
                ? new FakeEndpoint { Failure = new UnauthorizedAccessException("暂时无法打开设备") }
                : new FakeEndpoint([Session("device", $"seed-{attempts}")])
        };
        using var monitor = world.CreateMonitor([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(20)]);
        await monitor.StartAsync();
        var old = Assert.Single(world.Endpoints).Source;
        old.Emit(new EndpointInvalidated("设备会话接口失效", unchecked((int)0x88890004)));
        var recovered = await EventuallyAsync(monitor, state => state.Devices.Single().IsListening && world.Endpoints.Count == 4);
        Assert.Equal("seed-4", Assert.Single(Assert.Single(recovered.Devices).Sessions).Key.InstanceId);
        Assert.Equal(1, old.DisposeCalls);
        Assert.All(world.Endpoints.Take(3), item => Assert.Equal(1, item.Source.DisposeCalls));
        old.Emit(new EndpointSessionCreated(Data("stale")));
        Assert.DoesNotContain(Assert.Single((await monitor.GetStateAsync()).Devices).Sessions,
            session => session.Key.InstanceId == "stale");
    }

    [Fact]
    public async Task RecoveryHasFiniteAttemptsAndRefreshCanExplicitlyRetryAfterExhaustion()
    {
        var attempts = 0;
        var world = new World(Initial(Device("device")))
        {
            NewEndpoint = _ => ++attempts == 1 ? new FakeEndpoint()
                : new FakeEndpoint { Failure = new InvalidOperationException("设备仍然不可用") }
        };
        using var monitor = world.CreateMonitor([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(15)]);
        await monitor.StartAsync();
        world.Endpoints.Single().Source.Emit(new EndpointInvalidated("已失效"));
        var failed = await EventuallyAsync(monitor, state => !state.Devices.Single().IsListening && world.Endpoints.Count >= 3);
        Assert.NotEmpty(Assert.Single(failed.Devices).Issues);
        await Task.Delay(80);
        Assert.Equal(3, world.Endpoints.Count);
        world.NewEndpoint = _ => new FakeEndpoint();
        var refreshed = await monitor.RefreshAsync();
        Assert.True(Assert.Single(refreshed.Devices).IsListening);
        Assert.Equal(4, world.Endpoints.Count);
    }

    [Fact]
    public async Task RepeatedSuccessfulInitializationFollowedByImmediateInvalidationStillExhaustsRecoveryBudget()
    {
        var world = new World(Initial(Device("device")))
        {
            NewEndpoint = _ => new FakeEndpoint
            {
                DuringInitialize = callback => callback(new EndpointInvalidated("刚完成初始化就再次失效"))
            }
        };
        using var monitor = world.CreateMonitor([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(15)]);
        await monitor.StartAsync();
        await EventuallyAsync(monitor, state => world.Endpoints.Count >= 3 && !state.Devices.Single().IsListening);
        await Task.Delay(80);
        Assert.Equal(3, world.Endpoints.Count);
        Assert.All(world.Endpoints, endpoint => Assert.Equal(1, endpoint.Source.DisposeCalls));
        world.NewEndpoint = _ => new FakeEndpoint();
        Assert.True(Assert.Single((await monitor.RefreshAsync()).Devices).IsListening);
        Assert.Equal(4, world.Endpoints.Count);
    }

    [Fact]
    public async Task OtherDeviceEventsCannotBypassAnExhaustedEndpointRecoveryBudget()
    {
        var brokenAttempts = 0;
        var world = new World(Initial(Device("broken"), Device("healthy")))
        {
            NewEndpoint = id => id == "broken" && ++brokenAttempts > 1
                ? new FakeEndpoint { Failure = new InvalidOperationException("该设备持续失败") }
                : new FakeEndpoint()
        };
        using var monitor = world.CreateMonitor([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(15)]);
        await monitor.StartAsync();
        world.Endpoints.Single(item => item.DeviceId == "broken").Source.Emit(new EndpointInvalidated("接口失效"));
        await EventuallyAsync(monitor, state => !state.Devices.Single(item => item.Device.Id == "broken").IsListening
            && world.Endpoints.Count(item => item.DeviceId == "broken") == 3);
        var deviceSource = world.DeviceSources.Single();
        deviceSource.Emit(new(DeviceNotificationKind.DefaultChanged, "healthy", Role: AudioDeviceRole.Multimedia));
        var changed = await monitor.GetStateAsync();
        Assert.Equal("healthy", changed.DefaultOutputDevices[AudioDeviceRole.Multimedia]);
        deviceSource.Emit(new(DeviceNotificationKind.StateChanged, "healthy", AudioDeviceState.Active));
        await monitor.GetStateAsync();
        Assert.Equal(3, world.Endpoints.Count(item => item.DeviceId == "broken"));
        Assert.True(changed.Devices.Single(item => item.Device.Id == "healthy").IsListening);
        Assert.False(changed.Devices.Single(item => item.Device.Id == "broken").IsListening);
    }

    [Fact]
    public async Task StopCancelsPendingRecoveryAndNoNativeSourcesAreCreatedAfterItReturns()
    {
        var world = new World(Initial(Device("device")));
        using var monitor = world.CreateMonitor([TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60)]);
        await monitor.StartAsync();
        world.Endpoints.Single().Source.Emit(new EndpointInvalidated("等待重建"));
        await monitor.GetStateAsync();
        await monitor.StopAsync();
        var attemptsAtStop = world.Endpoints.Count;
        await Task.Delay(160);
        Assert.Equal(attemptsAtStop, world.Endpoints.Count);
        Assert.False((await monitor.GetStateAsync()).IsRunning);
        Assert.All(world.Endpoints, endpoint => Assert.Equal(1, endpoint.Source.DisposeCalls));
    }

    [Fact]
    public async Task DefiniteDisconnectStillClearsStaleStateWhenInventoryReadFails()
    {
        var world = new World(Initial(Device("device")))
        {
            NewEndpoint = _ => new FakeEndpoint([Session("device", "seed")])
        };
        using var monitor = world.CreateMonitor([]);
        await monitor.StartAsync();
        var endpoint = world.Endpoints.Single().Source;
        world.ReadFailure = () => new InvalidOperationException("枚举暂时失败");
        world.DeviceSources.Single().Emit(new(DeviceNotificationKind.StateChanged, "device", AudioDeviceState.Unplugged));
        var device = Assert.Single((await monitor.GetStateAsync()).Devices);
        Assert.Equal(AudioDeviceState.Unplugged, device.Device.State);
        Assert.False(device.IsListening);
        Assert.Null(device.MasterVolume);
        Assert.Empty(device.Sessions);
        Assert.Equal(1, endpoint.DisposeCalls);
        endpoint.Emit(new EndpointMasterVolumeChanged(new(0.99f, true), Guid.Empty));
        Assert.Null(Assert.Single((await monitor.GetStateAsync()).Devices).MasterVolume);
    }

    [Fact]
    public async Task OneSourceDisposalFailureDoesNotPreventOtherSourcesAndEventStreamFromClosing()
    {
        var failure = new InvalidOperationException("源释放异常");
        var logger = new CapturingLogger();
        var world = new World(Initial(Device("first"), Device("second")))
        {
            NewEndpoint = id => new FakeEndpoint { DisposeFailure = id == "first" ? failure : null }
        };
        var monitor = world.CreateMonitor(logger: logger);
        try
        {
            await monitor.StartAsync();
            monitor.Dispose();
            Assert.All(world.Endpoints, endpoint => Assert.Equal(1, endpoint.Source.DisposeCalls));
            Assert.Equal(1, world.DeviceSources.Single().DisposeCalls);
            Assert.Contains(logger.Errors, error => ReferenceEquals(error, failure));
            await using var events = monitor.ReadChangesAsync().GetAsyncEnumerator();
            while (await events.MoveNextAsync().AsTask().WaitAsync(Timeout)) { }
        }
        finally { monitor.Dispose(); }
    }

    [Fact]
    public async Task ComInitializationFailurePreservesCauseAndDisposalCompletesTheStream()
    {
        var failure = new InvalidOperationException("COM初始化异常");
        var logger = new CapturingLogger();
        var world = new World(Initial(Device("device")));
        var monitor = world.CreateMonitor(logger: logger, initialize: () => throw failure);
        try
        {
            var error = await Assert.ThrowsAsync<AudioControlException>(() => monitor.StartAsync());
            Assert.Same(failure, error.InnerException);
            Assert.Equal(AudioErrorCode.ApiFailure, error.Code);
            Assert.Empty(world.DeviceSources);
            monitor.Dispose();
            Assert.Contains(logger.Errors, captured => ReferenceEquals(captured, failure));
            await using var events = monitor.ReadChangesAsync().GetAsyncEnumerator();
            Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(Timeout));
        }
        finally { monitor.Dispose(); }
    }

    [Fact]
    public async Task NonFatalReadFailureIsVisibleWithoutRebuildingOrDisablingTheEndpoint()
    {
        var world = new World(Initial(Device("device")))
        {
            NewEndpoint = _ => new FakeEndpoint([Session("device", "seed")])
        };
        using var monitor = world.CreateMonitor();
        var initial = await monitor.StartAsync();
        await using var events = monitor.ReadChangesAsync().GetAsyncEnumerator();
        var source = world.Endpoints.Single().Source;
        source.Emit(new EndpointReadFailure("新会话必需信息不可读", unchecked((int)0x80070005), "unreadable-instance"));
        var failure = await NextAsync(events, change => change.Kind == AudioStateChangeKind.ReadFailure);
        Assert.Equal("device", failure.DeviceId);
        Assert.Equal("新会话必需信息不可读", failure.Message);
        Assert.True(Assert.Single(failure.State.Devices).IsListening);
        Assert.Equal(initial.Revision, failure.State.Revision);
        Assert.Single(world.Endpoints);
        Assert.Equal(0, source.DisposeCalls);
        source.Emit(new EndpointMasterVolumeChanged(new(0.2f, false), Guid.Empty));
        Assert.Equal(new AudioVolume(0.2f, false), Assert.Single((await monitor.GetStateAsync()).Devices).MasterVolume);
    }

    [Fact]
    public async Task PropertyNotificationsDoNotEnumerateOrRebuildOnEveryVolumePropertyChange()
    {
        var world = new World(Initial(Device("device")));
        using var monitor = world.CreateMonitor();
        await monitor.StartAsync();
        var before = world.ReadCalls;
        var source = Assert.Single(world.DeviceSources);
        await Task.Run(() =>
        {
            for (var index = 0; index < 100; index++) source.Emit(new(DeviceNotificationKind.PropertyChanged, "device"));
        });
        await monitor.GetStateAsync();
        Assert.Equal(before, world.ReadCalls);
        Assert.Single(world.Endpoints);
    }

    [Fact]
    public async Task StopRestartIgnoresOldDeviceAndEndpointGenerations()
    {
        var world = new World(Initial(Device("device")));
        using var monitor = world.CreateMonitor();
        await monitor.StartAsync();
        var oldDevice = world.DeviceSources.Single();
        var oldEndpoint = world.Endpoints.Single().Source;
        await monitor.StopAsync();
        Assert.False((await monitor.GetStateAsync()).IsRunning);
        await monitor.StartAsync();
        var revision = (await monitor.GetStateAsync()).Revision;
        oldDevice.Emit(new(DeviceNotificationKind.DefaultChanged, null, Role: AudioDeviceRole.Multimedia));
        oldEndpoint.Emit(new EndpointMasterVolumeChanged(new(0.02f, true), Guid.Empty));
        Assert.Equal(revision, (await monitor.GetStateAsync()).Revision);
        Assert.Equal(2, world.DeviceSources.Count);
        Assert.Equal(2, world.Endpoints.Count);
    }

    [Fact]
    public async Task CancelledReaderCanBeReplacedAndConcurrentReadersCannotStealItsEvents()
    {
        var world = new World(Initial(Device("device")));
        using var monitor = world.CreateMonitor();
        // 在未启动时没有初始事件，取消挂起的读取后可更换消费者。
        using (var cancellation = new CancellationTokenSource())
        {
            await using var first = monitor.ReadChangesAsync(cancellation.Token).GetAsyncEnumerator();
            var pending = first.MoveNextAsync().AsTask();
            await using var second = monitor.ReadChangesAsync().GetAsyncEnumerator();
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        }
        await using var replacement = monitor.ReadChangesAsync().GetAsyncEnumerator();
        var started = NextAsync(replacement, change => change.Kind == AudioStateChangeKind.Started);
        await monitor.StartAsync();
        Assert.True((await started).State.IsRunning);
    }

    [Fact]
    public async Task ConsumerCanStopAndDisposeWithoutExecutingOnAudioThread()
    {
        var world = new World(Initial(Device("device")));
        var monitor = world.CreateMonitor();
        try
        {
            var consumer = ConsumeAndCloseAsync();
            await monitor.StartAsync();
            await consumer.WaitAsync(Timeout);

            async Task ConsumeAndCloseAsync()
            {
                await using var events = monitor.ReadChangesAsync().GetAsyncEnumerator();
                await NextAsync(events, change => change.Kind == AudioStateChangeKind.Started);
                Assert.NotEqual(world.FactoryThread, Environment.CurrentManagedThreadId);
                await monitor.StopAsync();
                monitor.Dispose();
            }
        }
        finally { monitor.Dispose(); }
        Assert.All(world.DeviceSources, source => Assert.Equal(1, source.DisposeCalls));
        Assert.All(world.Endpoints, item => Assert.Equal(1, item.Source.DisposeCalls));
    }

    [Fact]
    public async Task ConcurrentNotificationsAndDisposeAreIsolatedAndAllAcceptedNativeObjectsAreReleased()
    {
        var world = new World(Initial(Device("device")))
        {
            NewEndpoint = _ => new FakeEndpoint([Session("device", "seed")])
        };
        var monitor = world.CreateMonitor();
        await monitor.StartAsync();
        var endpoint = world.Endpoints.Single().Source;
        var callbacks = Task.Run(() =>
        {
            for (var index = 0; index < 300; index++)
                endpoint.Emit(new EndpointSessionVolumeChanged("seed", new((index % 100) / 100f, false), Guid.Empty));
        });
        await Task.WhenAll(callbacks, Task.Run(() => monitor.Dispose())).WaitAsync(Timeout);
        monitor.Dispose();
        Assert.All(world.DeviceSources, source => Assert.Equal(1, source.DisposeCalls));
        Assert.All(world.Endpoints, item => Assert.Equal(1, item.Source.DisposeCalls));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.GetStateAsync());
        await using var events = monitor.ReadChangesAsync().GetAsyncEnumerator();
        while (await events.MoveNextAsync().AsTask().WaitAsync(Timeout)) { }
    }

    [Fact]
    public async Task FailedDeviceInitializationIsCleanedUpAndCanBeRetried()
    {
        var attempts = 0;
        var world = new World(Initial(Device("device")))
        {
            DeviceInitializationFailure = () => ++attempts == 1 ? new UnauthorizedAccessException("注册失败") : null
        };
        using var monitor = world.CreateMonitor();
        await Assert.ThrowsAnyAsync<Exception>(() => monitor.StartAsync());
        Assert.Equal(1, world.DeviceSources.Single().DisposeCalls);
        Assert.False((await monitor.GetStateAsync()).IsRunning);
        Assert.True((await monitor.StartAsync()).IsRunning);
        Assert.Equal(2, world.DeviceSources.Count);
    }

    [Fact]
    public async Task PreCancelledStartDoesNotCreateNativeSourcesAndContainerOwnsOneMonitor()
    {
        var world = new World(Initial(Device("device")));
        using var monitor = world.CreateMonitor();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.StartAsync(cancellation.Token));
        Assert.Empty(world.DeviceSources);
        var provider = new ServiceCollection().AddLogging().AddWindowsAudio().AddWindowsAudio()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var service = provider.GetRequiredService<IAudioStateMonitor>();
        Assert.Same(service, provider.GetRequiredService<IAudioStateMonitor>());
        provider.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync());
    }

    private static async Task<AudioStateChanged> NextAsync(IAsyncEnumerator<AudioStateChanged> events,
        Func<AudioStateChanged, bool> predicate)
    {
        while (await events.MoveNextAsync().AsTask().WaitAsync(Timeout))
            if (predicate(events.Current)) return events.Current;
        throw new InvalidOperationException("状态通知流提前结束。");
    }

    private static async Task<AudioRuntimeState> EventuallyAsync(WindowsAudioStateMonitor monitor,
        Func<AudioRuntimeState, bool> predicate)
    {
        var until = DateTime.UtcNow + Timeout;
        do
        {
            var state = await monitor.GetStateAsync();
            if (predicate(state)) return state;
            await Task.Delay(10);
        } while (DateTime.UtcNow < until);
        throw new TimeoutException("监听状态未在有界时间内达到预期。");
    }

    private static AudioOutputDevice Device(string id, AudioDeviceState state = AudioDeviceState.Active)
        => new(id, $"设备 {id}", state);

    private static DeviceInitialState Initial(params AudioOutputDevice[] devices)
        => new(new(devices, Array.Empty<AudioReadIssue>()),
            Enum.GetValues<AudioDeviceRole>().ToDictionary(role => role,
                _ => devices.FirstOrDefault(device => device.State == AudioDeviceState.Active)?.Id));

    private static AudioSession Session(string deviceId, string instanceId)
        => new(new(deviceId, instanceId), "identifier", (uint)Environment.ProcessId, "测试应用", null,
            false, AudioSessionState.Active, new(0.5f, false));

    private static SessionCreatedData Data(string instanceId)
        => new(new(instanceId, "identifier", (uint)Environment.ProcessId, "测试应用", false,
            AudioSessionState.Active, new(0.5f, false)), Array.Empty<AudioReadIssue>());

    private sealed class World(DeviceInitialState initial)
    {
        private DeviceInitialState current = initial;
        private int readCalls;
        public DeviceInitialState Current { get => Volatile.Read(ref current); set => Volatile.Write(ref current, value); }
        public ConcurrentQueue<FakeDeviceSource> DeviceSources { get; } = new();
        public ConcurrentQueue<(string DeviceId, FakeEndpoint Source)> Endpoints { get; } = new();
        public Func<string, FakeEndpoint> NewEndpoint { get; set; } = _ => new FakeEndpoint();
        public Func<Exception?> DeviceInitializationFailure { get; init; } = () => null;
        public Func<Exception?> ReadFailure { get; set; } = () => null;
        public int FactoryThread { get; private set; }
        public int ReadCalls => Volatile.Read(ref readCalls);

        public WindowsAudioStateMonitor CreateMonitor(TimeSpan[]? delays = null,
            ILogger<WindowsAudioStateMonitor>? logger = null, Func<IDisposable>? initialize = null)
            => new(() =>
            {
                FactoryThread = Environment.CurrentManagedThreadId;
                var source = new FakeDeviceSource(() => Current) { Failure = DeviceInitializationFailure() };
                DeviceSources.Enqueue(source);
                return source;
            }, (id, _) =>
            {
                var endpoint = NewEndpoint(id);
                Endpoints.Enqueue((id, endpoint));
                return endpoint;
            }, () =>
            {
                Interlocked.Increment(ref readCalls);
                if (ReadFailure() is { } error) throw error;
                return Current;
            }, logger ?? NullLogger<WindowsAudioStateMonitor>.Instance, new AudioThread(initialize), delays);
    }

    private sealed class FakeDeviceSource(Func<DeviceInitialState> read) : IAudioDeviceSource
    {
        private Action<DeviceNotification>? callback;
        public Exception? Failure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public int InitializeCalls { get; private set; }
        public int InitializeThread { get; private set; }
        public int DisposeCalls { get; private set; }
        public int DisposeThread { get; private set; }
        public DeviceInitialState Initialize(Action<DeviceNotification> onChanged)
        {
            InitializeCalls++;
            InitializeThread = Environment.CurrentManagedThreadId;
            callback = onChanged;
            if (Failure is { } error) throw error;
            return read();
        }
        public void Emit(DeviceNotification notification) => callback?.Invoke(notification);
        public void Dispose()
        {
            DisposeCalls++;
            DisposeThread = Environment.CurrentManagedThreadId;
        }
    }

    private sealed class FakeEndpoint(IReadOnlyList<AudioSession>? sessions = null) : IAudioEndpointSource
    {
        private Action<EndpointNotification>? callback;
        public Exception? Failure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public Action<Action<EndpointNotification>>? DuringInitialize { get; init; }
        public int InitializeThread { get; private set; }
        public ApartmentState InitializeApartment { get; private set; }
        public int DisposeCalls { get; private set; }
        public int DisposeThread { get; private set; }
        public EndpointInitialState Initialize(Action<EndpointNotification> onNotification)
        {
            callback = onNotification;
            InitializeThread = Environment.CurrentManagedThreadId;
            InitializeApartment = Thread.CurrentThread.GetApartmentState();
            DuringInitialize?.Invoke(onNotification);
            if (Failure is { } error) throw error;
            return new(new(0.5f, false), new(sessions ?? [], Array.Empty<AudioReadIssue>()));
        }
        // 故意保留已释放注册的回调，模拟本机通知已在途。
        public void Emit(EndpointNotification notification) => callback?.Invoke(notification);
        public void Dispose()
        {
            DisposeCalls++;
            DisposeThread = Environment.CurrentManagedThreadId;
            if (DisposeFailure is { } error) throw error;
        }
    }

    private sealed class CapturingLogger : ILogger<WindowsAudioStateMonitor>
    {
        public ConcurrentQueue<Exception?> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errors.Enqueue(exception);
        }
    }
}

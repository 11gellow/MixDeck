using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Windows;
using AudioManager.Windows.AudioSessions;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AudioManager.Tests;

public sealed class SessionMonitorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RepeatedStartsReuseOneSourceAndAllNativeLifecycleWorkStaysOnMtaWorker()
    {
        var source = new FakeSource([Session("device", "existing")]);
        var factoryCalls = 0;
        var factoryThread = 0;
        var monitor = Create(_ =>
        {
            factoryCalls++;
            factoryThread = Environment.CurrentManagedThreadId;
            return source;
        });
        try
        {
            var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => monitor.StartAsync("device")));
            Assert.All(starts, result => Assert.Equal("existing", Assert.Single(result.Items).Key.InstanceId));
            Assert.Equal(1, factoryCalls);
            Assert.Equal(1, source.InitializeCalls);
            Assert.Equal(factoryThread, source.InitializeThread);
            Assert.Equal(ApartmentState.MTA, source.InitializeApartment);
            Assert.NotEqual(Environment.CurrentManagedThreadId, source.InitializeThread);
        }
        finally { monitor.Dispose(); }
        Assert.Equal(1, source.DisposeCalls);
        Assert.Equal(factoryThread, source.DisposeThread);
    }

    [Fact]
    public async Task DuplicateInitialSessionsAndNotificationsProduceOnlyOneNewEvent()
    {
        var existing = Session("device", "existing");
        var source = new FakeSource([existing, existing]);
        using var monitor = Create(_ => source);
        Assert.Single((await monitor.StartAsync("device")).Items);
        using var cancellation = new CancellationTokenSource();
        await using var events = monitor.ReadCreatedSessionsAsync(cancellation.Token).GetAsyncEnumerator();
        var first = events.MoveNextAsync().AsTask();
        source.Emit(Data("existing"));
        source.Emit(Data("new"));
        source.Emit(Data("new"));
        Assert.True(await first.WaitAsync(Timeout));
        Assert.Equal(new AudioSessionKey("device", "new"), events.Current.Session.Key);
        Assert.Equal(2, (await monitor.GetKnownSessionsAsync("device")).Items.Count);
        var next = events.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(Timeout));
    }

    [Fact]
    public async Task SameProcessSessionsAndSameInstanceNamesOnDifferentDevicesRemainDistinct()
    {
        var firstSource = new FakeSource();
        var secondSource = new FakeSource();
        using var monitor = Create(device => device == "first" ? firstSource : secondSource);
        await monitor.StartAsync("first");
        await monitor.StartAsync("second");
        await using var events = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
        var firstRead = events.MoveNextAsync().AsTask();
        var pid = (uint)Environment.ProcessId;
        firstSource.Emit(Data("same", pid));
        firstSource.Emit(Data("other", pid));
        secondSource.Emit(Data("same", pid));
        var received = new List<AudioSession>();
        Assert.True(await firstRead.WaitAsync(Timeout));
        received.Add(events.Current.Session);
        for (var index = 0; index < 2; index++)
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout));
            received.Add(events.Current.Session);
        }
        Assert.Equal(3, received.Select(session => session.Key).Distinct().Count());
        Assert.All(received, session => Assert.Equal(pid, session.ProcessId));
        Assert.Equal(2, (await monitor.GetKnownSessionsAsync("first")).Items.Count);
        Assert.Single((await monitor.GetKnownSessionsAsync("second")).Items);
    }

    [Fact]
    public async Task NotificationsDuringInitializationMergeWithEnumerationWithoutLosingOrDuplicatingSessions()
    {
        var source = new FakeSource([Session("device", "existing")])
        {
            DuringInitialize = callback =>
            {
                callback(Data("existing"));
                callback(Data("created-during-start"));
            }
        };
        using var monitor = Create(_ => source);
        await using var events = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
        var read = events.MoveNextAsync().AsTask();
        await monitor.StartAsync("device");
        Assert.True(await read.WaitAsync(Timeout));
        Assert.Equal("created-during-start", events.Current.Session.Key.InstanceId);
        var known = await monitor.GetKnownSessionsAsync("device");
        Assert.Equal(2, known.Items.Count);
        Assert.Equal(2, known.Items.Select(session => session.Key).Distinct().Count());
    }

    [Fact]
    public async Task InitializationFailureDisposesSourceAndLateCallbacksCannotContaminateRetry()
    {
        var failed = new FakeSource
        {
            Failure = new InvalidOperationException("初始化失败"),
            DuringInitialize = callback => callback(Data("failed-initialization"))
        };
        var retry = new FakeSource([Session("device", "retry-seed")]);
        var calls = 0;
        using var monitor = Create(_ => ++calls == 1 ? failed : retry);
        await Assert.ThrowsAnyAsync<Exception>(() => monitor.StartAsync("device"));
        Assert.Equal(1, failed.DisposeCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.GetKnownSessionsAsync("device"));
        await monitor.StartAsync("device");
        failed.Emit(Data("late-failed-source"));
        retry.Emit(Data("valid"));
        var known = await monitor.GetKnownSessionsAsync("device");
        Assert.Equal(new[] { "retry-seed", "valid" }, known.Items.Select(session => session.Key.InstanceId).Order().ToArray());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StoppedAndRestartedDeviceIgnoresCallbacksFromOldRegistration()
    {
        var oldSource = new FakeSource([Session("device", "old-seed")]);
        var newSource = new FakeSource([Session("device", "new-seed")]);
        var calls = 0;
        using var monitor = Create(_ => ++calls == 1 ? oldSource : newSource);
        await monitor.StartAsync("device");
        await monitor.StopAsync("device");
        await monitor.StopAsync("device");
        Assert.Equal(1, oldSource.DisposeCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.GetKnownSessionsAsync("device"));
        await monitor.StartAsync("device");
        oldSource.Emit(Data("late-old-registration"));
        newSource.Emit(Data("new-notification"));
        var known = await monitor.GetKnownSessionsAsync("device");
        Assert.Equal(new[] { "new-notification", "new-seed" }, known.Items.Select(session => session.Key.InstanceId).Order().ToArray());
        Assert.Equal(1, newSource.InitializeCalls);
    }

    [Fact]
    public async Task CancellingAReaderDoesNotStopMonitoringAndAnotherReaderCanConsumeLaterEvents()
    {
        var source = new FakeSource();
        using var monitor = Create(_ => source);
        await monitor.StartAsync("device");
        using (var cancellation = new CancellationTokenSource())
        {
            await using var cancelledReader = monitor.ReadCreatedSessionsAsync(cancellation.Token).GetAsyncEnumerator();
            var pending = cancelledReader.MoveNextAsync().AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        }
        await using var nextReader = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
        var next = nextReader.MoveNextAsync().AsTask();
        source.Emit(Data("after-cancel"));
        Assert.True(await next.WaitAsync(Timeout));
        Assert.Equal("after-cancel", nextReader.Current.Session.Key.InstanceId);
        Assert.Single((await monitor.GetKnownSessionsAsync("device")).Items);
        Assert.Equal(0, source.DisposeCalls);
    }

    [Fact]
    public async Task ConcurrentReadersAreRejectedWithoutTakingEventsAwayFromTheFirstReader()
    {
        var source = new FakeSource();
        using var monitor = Create(_ => source);
        await monitor.StartAsync("device");
        using var cancellation = new CancellationTokenSource();
        await using var first = monitor.ReadCreatedSessionsAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = first.MoveNextAsync().AsTask();
        await using var second = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
        source.Emit(Data("first-reader"));
        Assert.True(await pending.WaitAsync(Timeout));
        Assert.Equal("first-reader", first.Current.Session.Key.InstanceId);
    }

    [Fact]
    public async Task SourceIssuesArePreservedInTheNewSessionEvent()
    {
        var source = new FakeSource();
        using var monitor = Create(_ => source);
        await monitor.StartAsync("device");
        await using var events = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
        var read = events.MoveNextAsync().AsTask();
        var issue = new AudioReadIssue("new", "可选元数据不可读", unchecked((int)0x80070005));
        source.Emit(Data("new") with { Issues = new[] { issue } });
        Assert.True(await read.WaitAsync(Timeout));
        Assert.Equal(issue, Assert.Single(events.Current.Issues));
        Assert.Equal("应用 new", events.Current.Session.Name);
    }

    [Fact]
    public async Task ExpiredAndMalformedNotificationsAreIgnoredWithoutBreakingSubsequentEvents()
    {
        var expired = Session("device", "expired-seed") with { State = AudioSessionState.Expired };
        var source = new FakeSource([expired]);
        using var monitor = Create(_ => source);
        Assert.Empty((await monitor.StartAsync("device")).Items);
        await using var events = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
        var read = events.MoveNextAsync().AsTask();
        var expiredData = Data("expired");
        source.Emit(expiredData with { Info = expiredData.Info with { State = AudioSessionState.Expired } });
        source.Emit(Data(" "));
        source.Emit(Data("valid"));
        Assert.True(await read.WaitAsync(Timeout));
        Assert.Equal("valid", events.Current.Session.Key.InstanceId);
        Assert.Equal("valid", Assert.Single((await monitor.GetKnownSessionsAsync("device")).Items).Key.InstanceId);
    }

    [Fact]
    public async Task BackgroundCallbacksAddSessionsWithoutRunningFactoryOrDisposalOnCallbackThread()
    {
        var source = new FakeSource();
        using var monitor = Create(_ => source);
        await monitor.StartAsync("device");
        await Task.Run(() => source.Emit(Data("background")));
        Assert.Equal("background", Assert.Single((await monitor.GetKnownSessionsAsync("device")).Items).Key.InstanceId);
        await monitor.StopAsync("device");
        Assert.Equal(source.InitializeThread, source.DisposeThread);
    }

    [Fact]
    public async Task PreCancelledRequestsAndInvalidDeviceIdsDoNotCreateSources()
    {
        var calls = 0;
        using var monitor = Create(_ => { calls++; return new FakeSource(); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.StartAsync("device", cancellation.Token));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => monitor.StartAsync(" "));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => monitor.StopAsync(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => monitor.GetKnownSessionsAsync(" "));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DisposeReleasesAllSourcesCompletesStreamAndRejectsNewOperations()
    {
        var firstSource = new FakeSource();
        var secondSource = new FakeSource();
        var monitor = Create(device => device == "first" ? firstSource : secondSource);
        try
        {
            await monitor.StartAsync("first");
            await monitor.StartAsync("second");
            await using var events = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
            var read = events.MoveNextAsync().AsTask();
            monitor.Dispose();
            monitor.Dispose();
            Assert.False(await read.WaitAsync(Timeout));
            Assert.Equal(1, firstSource.DisposeCalls);
            Assert.Equal(1, secondSource.DisposeCalls);
            Assert.Equal(firstSource.InitializeThread, firstSource.DisposeThread);
            Assert.Equal(secondSource.InitializeThread, secondSource.DisposeThread);
            firstSource.Emit(Data("too-late"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync("first"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.GetKnownSessionsAsync("first"));
        }
        finally { monitor.Dispose(); }
    }

    [Fact]
    public async Task ComInitializationFailureIsReportedAndDisposeStillCompletesTheEventStream()
    {
        var initializationError = new COMException("COM 初始化失败", unchecked((int)0x80004005));
        var logger = new CapturingLogger();
        var factoryCalls = 0;
        var monitor = new WindowsAudioSessionMonitor(_ => { factoryCalls++; return new FakeSource(); }, logger,
            new AudioThread(() => throw initializationError));
        try
        {
            var error = await Assert.ThrowsAsync<AudioControlException>(() => monitor.StartAsync("device"));
            Assert.Equal(AudioErrorCode.ApiFailure, error.Code);
            Assert.Same(initializationError, error.InnerException);
            Assert.Equal(initializationError.HResult, error.NativeErrorCode);
            Assert.Equal(0, factoryCalls);
            await using var events = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
            var read = events.MoveNextAsync().AsTask();
            monitor.Dispose();
            Assert.False(await read.WaitAsync(Timeout));
            Assert.Contains(logger.Errors, entry => ReferenceEquals(entry.Error, initializationError));
        }
        finally { monitor.Dispose(); }
    }

    [Fact]
    public async Task EventConsumerCanStopAndDisposeTheMonitorWithoutBlockingItsAudioWorker()
    {
        var source = new FakeSource();
        var monitor = Create(_ => source);
        try
        {
            await monitor.StartAsync("device");
            var consumer = ConsumeOneEventAndCloseAsync();
            source.Emit(Data("close-from-consumer"));
            await consumer.WaitAsync(Timeout);
            Assert.Equal(1, source.DisposeCalls);

            async Task ConsumeOneEventAndCloseAsync()
            {
                await using var events = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
                Assert.True(await events.MoveNextAsync());
                Assert.NotEqual(source.InitializeThread, Environment.CurrentManagedThreadId);
                await monitor.StopAsync("device");
                monitor.Dispose();
            }
        }
        finally { monitor.Dispose(); }
    }

    [Fact]
    public async Task SourceDisposalFailureIsLoggedAndDoesNotPreventOtherSourcesOrStreamFromClosing()
    {
        var disposalError = new InvalidOperationException("测试订阅释放失败");
        var firstSource = new FakeSource { DisposeFailure = disposalError };
        var secondSource = new FakeSource();
        var logger = new CapturingLogger();
        var monitor = new WindowsAudioSessionMonitor(device => device == "first" ? firstSource : secondSource,
            logger, new AudioThread());
        try
        {
            await monitor.StartAsync("first");
            await monitor.StartAsync("second");
            await using var events = monitor.ReadCreatedSessionsAsync().GetAsyncEnumerator();
            var read = events.MoveNextAsync().AsTask();
            monitor.Dispose();
            Assert.Equal(1, firstSource.DisposeCalls);
            Assert.Equal(1, secondSource.DisposeCalls);
            Assert.False(await read.WaitAsync(Timeout));
            var entry = Assert.Single(logger.Errors);
            Assert.Same(disposalError, entry.Error);
            Assert.Contains("释放", entry.Message);
        }
        finally { monitor.Dispose(); }
    }

    [Fact]
    public async Task DependencyInjectionProvidesOneMonitorAndDisposesItWithTheContainer()
    {
        var services = new ServiceCollection().AddLogging().AddWindowsAudio().AddWindowsAudio();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var monitor = provider.GetRequiredService<IAudioSessionMonitor>();
        Assert.Same(monitor, provider.GetRequiredService<IAudioSessionMonitor>());
        provider.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync("device"));
    }

    private static WindowsAudioSessionMonitor Create(Func<string, IAudioSessionSource> factory)
        => new(factory, NullLogger<WindowsAudioSessionMonitor>.Instance, new AudioThread());

    private static AudioSession Session(string device, string instance)
        => new(new(device, instance), "session-identifier", null, $"应用 {instance}", null, false,
            AudioSessionState.Active, new AudioVolume(0.5f, false));

    private static SessionCreatedData Data(string instance, uint? processId = null)
        => new(new SessionInfoSnapshot(instance, "session-identifier", processId, $"应用 {instance}", false,
            AudioSessionState.Active, new AudioVolume(0.5f, false)), Array.Empty<AudioReadIssue>());

    private sealed class FakeSource(IReadOnlyList<AudioSession>? seed = null) : IAudioSessionSource
    {
        private Action<SessionCreatedData>? callback;
        public int InitializeCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int InitializeThread { get; private set; }
        public int DisposeThread { get; private set; }
        public ApartmentState InitializeApartment { get; private set; }
        public Action<Action<SessionCreatedData>>? DuringInitialize { get; init; }
        public Exception? Failure { get; init; }
        public Exception? DisposeFailure { get; init; }

        public AudioEnumeration<AudioSession> Initialize(Action<SessionCreatedData> onCreated)
        {
            InitializeCalls++;
            InitializeThread = Environment.CurrentManagedThreadId;
            InitializeApartment = Thread.CurrentThread.GetApartmentState();
            callback = onCreated;
            DuringInitialize?.Invoke(onCreated);
            if (Failure is { } error) throw error;
            return new(seed ?? Array.Empty<AudioSession>(), Array.Empty<AudioReadIssue>());
        }

        // 故意保留回调，模拟撤销订阅前已在途、关闭后才到达的系统通知。
        public void Emit(SessionCreatedData data) => callback!(data);

        public void Dispose()
        {
            DisposeCalls++;
            DisposeThread = Environment.CurrentManagedThreadId;
            if (DisposeFailure is { } error) throw error;
        }
    }

    private sealed class CapturingLogger : ILogger<WindowsAudioSessionMonitor>
    {
        public ConcurrentQueue<(Exception? Error, string Message)> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Error) Errors.Enqueue((exception, formatter(state, exception)));
        }
    }
}

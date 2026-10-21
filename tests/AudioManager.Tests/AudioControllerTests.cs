using System.Runtime.InteropServices;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Windows;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioManager.Tests;

public sealed class AudioControllerTests
{
    [Theory]
    [MemberData(nameof(AudioModelTests.InvalidLevels), MemberType = typeof(AudioModelTests))]
    public async Task InvalidVolumesNeverReachBackend(float level)
    {
        var backend = new FakeBackend();
        using var controller = Create(backend);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetMasterVolumeAsync("device", level));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetSessionVolumeAsync(new("device", "a"), level));
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task EmptyDevicesAndMissingDefaultHaveExplicitEmptyResults()
    {
        using var controller = Create(new FakeBackend());
        var result = await controller.EnumerateOutputDevicesAsync();
        Assert.Empty(result.Items);
        Assert.Empty(result.Issues);
        Assert.Null(await controller.GetDefaultOutputDeviceAsync());
    }

    [Fact]
    public async Task ApiFailureIsLoggedWithHresultAndDoesNotKillWorker()
    {
        var failure = new COMException("拒绝访问", unchecked((int)0x80070005));
        var backend = new FakeBackend { Failure = failure };
        var logger = new CapturingLogger();
        using var controller = Create(backend, logger);
        var error = await Assert.ThrowsAsync<AudioControlException>(() => controller.GetMasterVolumeAsync("device"));
        Assert.Equal(AudioErrorCode.AccessDenied, error.Code);
        Assert.Equal(failure.HResult, error.NativeErrorCode);
        Assert.Same(failure, error.InnerException);
        Assert.Single(logger.Errors);
        backend.Failure = null;
        Assert.Equal(0.5f, (await controller.GetMasterVolumeAsync("device")).Level);
    }

    [Theory]
    [InlineData(AudioErrorCode.DeviceUnavailable)]
    [InlineData(AudioErrorCode.SessionUnavailable)]
    public async Task KnownUnavailableErrorsKeepTheirClassification(AudioErrorCode code)
    {
        var failure = new AudioControlException(code, "测试操作", "对象失效");
        using var controller = Create(new FakeBackend { Failure = failure });
        Assert.Same(failure, await Assert.ThrowsAsync<AudioControlException>(() => controller.GetMasterVolumeAsync("device")));
    }

    [Fact]
    public async Task InitializationFailureIsReportedAndCanBeDisposed()
    {
        var backend = new FakeBackend();
        using var controller = new WindowsAudioController(backend, NullLogger<WindowsAudioController>.Instance,
            new AudioThread(() => throw new COMException("初始化失败")));
        var error = await Assert.ThrowsAsync<AudioControlException>(() => controller.EnumerateOutputDevicesAsync());
        Assert.Equal(AudioErrorCode.ApiFailure, error.Code);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task CancellationAndDisposalNeverInvokeBackend()
    {
        var backend = new FakeBackend();
        var controller = Create(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.EnumerateOutputDevicesAsync(cancellation.Token));
        controller.Dispose();
        controller.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.GetMasterVolumeAsync("device"));
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task InvalidIdentifiersAndRolesNeverInvokeBackend()
    {
        var backend = new FakeBackend();
        using var controller = Create(backend);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.GetMasterVolumeAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.EnumerateSessionsAsync(""));
        await Assert.ThrowsAsync<ArgumentNullException>(() => controller.GetSessionVolumeAsync(null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.GetDefaultOutputDeviceAsync((AudioDeviceRole)99));
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task DependencyInjectionProvidesOneDisposableController()
    {
        var services = new ServiceCollection().AddLogging().AddWindowsAudio().AddWindowsAudio();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var controller = provider.GetRequiredService<IAudioController>();
        Assert.Same(controller, provider.GetRequiredService<IAudioController>());
        provider.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.EnumerateOutputDevicesAsync());
    }

    private static WindowsAudioController Create(FakeBackend backend, CapturingLogger? logger = null)
        => new(backend, (ILogger<WindowsAudioController>?)logger ?? NullLogger<WindowsAudioController>.Instance, new AudioThread());

    private sealed class CapturingLogger : ILogger<WindowsAudioController>
    {
        public List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Error) Errors.Add(exception); }
    }

    private sealed class FakeBackend : IAudioBackend
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        private T Return<T>(T value) { Calls++; if (Failure is { } error) throw error; return value; }
        public AudioEnumeration<AudioOutputDevice> EnumerateOutputDevices() => Return(new AudioEnumeration<AudioOutputDevice>([], []));
        public AudioOutputDevice? GetDefaultOutputDevice(AudioDeviceRole role) => Return<AudioOutputDevice?>(null);
        public AudioVolume GetMasterVolume(string deviceId) => Return(new AudioVolume(0.5f, false));
        public AudioVolume SetMasterVolume(string deviceId, float level) => Return(new AudioVolume(level, false));
        public AudioVolume SetMasterMute(string deviceId, bool isMuted) => Return(new AudioVolume(0.5f, isMuted));
        public AudioEnumeration<AudioSession> EnumerateSessions(string deviceId) => Return(new AudioEnumeration<AudioSession>([], []));
        public AudioVolume GetSessionVolume(AudioSessionKey key) => Return(new AudioVolume(0.5f, false));
        public AudioVolume SetSessionVolume(AudioSessionKey key, float level) => Return(new AudioVolume(level, false));
        public AudioVolume SetSessionMute(AudioSessionKey key, bool isMuted) => Return(new AudioVolume(0.5f, isMuted));
    }
}

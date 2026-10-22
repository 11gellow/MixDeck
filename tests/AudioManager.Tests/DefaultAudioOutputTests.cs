using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Windows;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioManager.Tests;

public sealed class DefaultAudioOutputTests
{
    [Fact]
    public async Task SwitchesBothPlaybackRolesAndVerifiesOnOneMtaThreadWithoutChangingCommunications()
    {
        var backend = new Backend();
        using var controller = Create(backend);
        await controller.SetDefaultOutputDeviceAsync("b");
        Assert.Equal("b", backend.Defaults[AudioDeviceRole.Console]);
        Assert.Equal("b", backend.Defaults[AudioDeviceRole.Multimedia]);
        Assert.Equal("communications", backend.Defaults[AudioDeviceRole.Communications]);
        Assert.Equal(2, backend.Writes.Count);
        Assert.Single(backend.Threads);
        Assert.All(backend.Apartments, apartment => Assert.Equal(ApartmentState.MTA, apartment));
        await controller.SetDefaultOutputDeviceAsync("b");
        Assert.Equal(2, backend.Writes.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unplugged")]
    public async Task UnavailableTargetNeverChangesDefaults(string target)
    {
        var backend = new Backend();
        using var controller = Create(backend);
        var failure = await Assert.ThrowsAsync<AudioControlException>(() => controller.SetDefaultOutputDeviceAsync(target));
        Assert.Equal(AudioErrorCode.DeviceUnavailable, failure.Code);
        Assert.Empty(backend.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialFailureOrIneffectiveWriteRollsBackOnlyChangedPlaybackRoles(bool ineffective)
    {
        var backend = new Backend();
        backend.BeforeSet = (id, role) =>
        {
            if (id == "b" && role == AudioDeviceRole.Multimedia && !ineffective) throw new UnauthorizedAccessException("拒绝");
        };
        backend.IgnoreMultimedia = ineffective;
        using var controller = Create(backend);
        var failure = await Assert.ThrowsAsync<AudioControlException>(() => controller.SetDefaultOutputDeviceAsync("b"));
        Assert.Equal(ineffective ? AudioErrorCode.ApiFailure : AudioErrorCode.AccessDenied, failure.Code);
        Assert.Equal("a", backend.Defaults[AudioDeviceRole.Console]);
        Assert.Equal("a", backend.Defaults[AudioDeviceRole.Multimedia]);
        Assert.Equal("communications", backend.Defaults[AudioDeviceRole.Communications]);
    }

    [Fact]
    public async Task ConcurrentExternalSelectionIsNotOverwrittenByRollback()
    {
        var backend = new Backend();
        backend.BeforeSet = (id, role) =>
        {
            if (id != "b" || role != AudioDeviceRole.Multimedia) return;
            backend.Defaults[AudioDeviceRole.Console] = "third-party";
            throw new UnauthorizedAccessException("拒绝");
        };
        using var controller = Create(backend);
        await Assert.ThrowsAsync<AudioControlException>(() => controller.SetDefaultOutputDeviceAsync("b"));
        Assert.Equal("third-party", backend.Defaults[AudioDeviceRole.Console]);
        Assert.DoesNotContain(("a", AudioDeviceRole.Console), backend.Writes);
    }

    [Fact]
    public async Task RollbackFailureIsReportedWithoutClaimingRestoredState()
    {
        var backend = new Backend();
        backend.BeforeSet = (id, role) =>
        {
            if (id == "b" && role == AudioDeviceRole.Multimedia || id == "a") throw new UnauthorizedAccessException("设备失效");
        };
        using var controller = Create(backend);
        var failure = await Assert.ThrowsAsync<AudioControlException>(() => controller.SetDefaultOutputDeviceAsync("b"));
        Assert.Contains("部分回退失败", failure.Message);
        Assert.Equal("b", backend.Defaults[AudioDeviceRole.Console]);
        Assert.Equal("a", backend.Defaults[AudioDeviceRole.Multimedia]);
    }

    [Fact]
    public async Task InvalidCancelledAndDisposedCallsNeverReachBackend()
    {
        var backend = new Backend();
        var controller = Create(backend);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => controller.SetDefaultOutputDeviceAsync(" "));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.SetDefaultOutputDeviceAsync("b", cancel.Token));
        controller.Dispose();
        controller.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.SetDefaultOutputDeviceAsync("b"));
        Assert.Empty(backend.Threads);
    }

    [Fact]
    public void DependencyInjectionRegistersOwnedDefaultOutputController()
    {
        using var provider = new ServiceCollection().AddLogging().AddWindowsAudio().BuildServiceProvider();
        var controller = provider.GetRequiredService<IDefaultAudioOutputController>();
        Assert.IsType<WindowsDefaultAudioOutputController>(controller);
        Assert.Same(controller, provider.GetRequiredService<IDefaultAudioOutputController>());
    }

    private static WindowsDefaultAudioOutputController Create(Backend backend)
        => new(backend, NullLogger<WindowsDefaultAudioOutputController>.Instance, new AudioThread());

    private sealed class Backend : IDefaultOutputBackend
    {
        public Dictionary<AudioDeviceRole, string?> Defaults { get; } = new()
        {
            [AudioDeviceRole.Console] = "a", [AudioDeviceRole.Multimedia] = "a", [AudioDeviceRole.Communications] = "communications"
        };
        public List<(string, AudioDeviceRole)> Writes { get; } = [];
        public HashSet<int> Threads { get; } = [];
        public List<ApartmentState> Apartments { get; } = [];
        public Action<string, AudioDeviceRole>? BeforeSet { get; set; }
        public bool IgnoreMultimedia { get; set; }
        private void Track() { Threads.Add(Environment.CurrentManagedThreadId); Apartments.Add(Thread.CurrentThread.GetApartmentState()); }
        public AudioOutputDevice? GetDevice(string id)
        {
            Track();
            return id == "missing" ? null : new(id, id, id == "unplugged" ? AudioDeviceState.Unplugged : AudioDeviceState.Active);
        }
        public string? GetDefault(AudioDeviceRole role) { Track(); return Defaults[role]; }
        public void SetDefault(string id, AudioDeviceRole role)
        {
            Track();
            Writes.Add((id, role));
            BeforeSet?.Invoke(id, role);
            if (!IgnoreMultimedia || role != AudioDeviceRole.Multimedia) Defaults[role] = id;
        }
    }
}

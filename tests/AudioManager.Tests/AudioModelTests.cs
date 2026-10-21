using AudioManager.Core.Models;

namespace AudioManager.Tests;

public sealed class AudioModelTests
{
    public static TheoryData<float> InvalidLevels => new() { -0.01f, 1.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };

    [Theory]
    [MemberData(nameof(InvalidLevels))]
    public void InvalidVolumeIsRejected(float level)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new AudioVolume(level, false));

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void VolumeAndMuteAreIndependent(float level)
    {
        Assert.Equal(level, new AudioVolume(level, true).Level);
        Assert.True(new AudioVolume(level, true).IsMuted);
        Assert.False(new AudioVolume(level, false).IsMuted);
    }

    [Fact]
    public void SessionKeysDistinguishDeviceAndRuntimeInstance()
    {
        Assert.NotEqual(new AudioSessionKey("device-a", "instance-a"), new AudioSessionKey("device-a", "instance-b"));
        Assert.NotEqual(new AudioSessionKey("device-a", "instance-a"), new AudioSessionKey("device-b", "instance-a"));
        Assert.Equal(new AudioSessionKey("device-a", "instance-a"), new AudioSessionKey("device-a", "instance-a"));
    }

    [Theory]
    [InlineData("", "instance")]
    [InlineData("device", " ")]
    public void EmptySessionKeyIsRejected(string device, string instance)
        => Assert.Throws<ArgumentException>(() => new AudioSessionKey(device, instance));
}

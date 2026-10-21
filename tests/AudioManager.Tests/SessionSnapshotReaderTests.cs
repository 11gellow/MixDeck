using AudioManager.Core.Models;
using AudioManager.Windows.AudioSessions;

namespace AudioManager.Tests;

public sealed class SessionSnapshotReaderTests
{
    [Fact]
    public void CapturedNativeFieldsRemainUsableAfterBorrowedInterfaceExpires()
    {
        var borrowed = new BorrowedSessionInfo();
        var issues = new List<AudioReadIssue>();
        var captured = SessionSnapshotReader.Capture(borrowed, issues);
        borrowed.Expired = true;
        var processRead = false;
        var snapshot = SessionSnapshotReader.Read("device", captured, issues, pid =>
        {
            processRead = true;
            Assert.Equal((uint)42, pid);
            return new ProcessInfo("player", "player.exe");
        });
        Assert.True(processRead);
        Assert.Equal("player", snapshot.Name);
        Assert.Equal("player.exe", snapshot.ProcessPath);
        Assert.Equal(new AudioSessionKey("device", "borrowed"), snapshot.Key);
        Assert.Equal(new AudioVolume(0.3f, false), snapshot.Volume);
        Assert.Empty(issues);
    }

    [Fact]
    public void CapturePreservesOptionalFieldFailuresWithoutInventingRequiredData()
    {
        var issues = new List<AudioReadIssue>();
        var captured = SessionSnapshotReader.Capture(new BorrowedSessionInfo { OptionalFieldsUnreadable = true }, issues);
        Assert.Null(captured.Identifier);
        Assert.Null(captured.ProcessId);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.Equal(unchecked((int)0x80070005), issue.NativeErrorCode));
        var snapshot = SessionSnapshotReader.Read("device", captured, issues);
        Assert.Equal("未知应用", snapshot.Name);
        Assert.Equal(new AudioVolume(0.3f, false), snapshot.Volume);
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void UnreadableProcessKeepsControllableSessionWithFeedback()
    {
        var issues = new List<AudioReadIssue>();
        var result = SessionSnapshotReader.Read("device", new SessionInfo(), issues, _ => throw new UnauthorizedAccessException("进程信息不可读"));
        Assert.Equal("进程 42", result.Name);
        Assert.Null(result.ProcessPath);
        Assert.Equal(0.3f, result.Volume.Level);
        Assert.Equal(new AudioSessionKey("device", "instance"), result.Key);
        Assert.Single(issues);
    }

    [Fact]
    public void ExitedProcessIsHandledLikeUnavailableMetadata()
    {
        var issues = new List<AudioReadIssue>();
        var result = SessionSnapshotReader.Read("device", new SessionInfo(), issues, _ => throw new ArgumentException("进程已经退出"));
        Assert.Equal((uint)42, result.ProcessId);
        Assert.Single(issues);
    }

    [Fact]
    public void UnreadablePathKeepsApplicationName()
    {
        var issues = new List<AudioReadIssue>();
        var result = SessionSnapshotReader.Read("device", new SessionInfo(), issues,
            _ => new ProcessInfo("player", null, new UnauthorizedAccessException()));
        Assert.Equal("player", result.Name);
        Assert.Null(result.ProcessPath);
        Assert.Single(issues);
    }

    [Fact]
    public void SameProcessCanHaveMultipleDistinctRuntimeSessions()
    {
        var issues = new List<AudioReadIssue>();
        var first = SessionSnapshotReader.Read("device", new SessionInfo(), issues, _ => new ProcessInfo("player", "player.exe"));
        var second = SessionSnapshotReader.Read("device", new SessionInfo { InstanceId = "second" }, issues, _ => new ProcessInfo("player", "player.exe"));
        Assert.Equal(first.ProcessId, second.ProcessId);
        Assert.NotEqual(first.Key, second.Key);
        Assert.Empty(issues);
    }

    [Fact]
    public void InvalidRequiredStateIsReportedInsteadOfFabricatingVolume()
    {
        var issues = new List<AudioReadIssue>();
        Assert.Throws<InvalidOperationException>(() => SessionSnapshotReader.Read("device", new SessionInfo { InvalidVolume = true }, issues));
    }

    private sealed class SessionInfo : ISessionInfo
    {
        public string InstanceId { get; init; } = "instance";
        public bool InvalidVolume { get; init; }
        public string Identifier => "stable";
        public uint ProcessId => 42;
        public string DisplayName => "";
        public bool IsSystemSounds => false;
        public AudioSessionState State => AudioSessionState.Active;
        public AudioVolume Volume => InvalidVolume ? throw new InvalidOperationException("会话失效") : new(0.3f, false);
    }

    private sealed class BorrowedSessionInfo : ISessionInfo
    {
        public bool Expired { get; set; }
        public bool OptionalFieldsUnreadable { get; init; }
        private T Get<T>(T value)
        {
            if (Expired) throw new ObjectDisposedException("回调借用接口");
            return value;
        }
        public string InstanceId => Get("borrowed");
        public string Identifier => OptionalFieldsUnreadable ? throw new UnauthorizedAccessException() : Get("identifier");
        public uint ProcessId => OptionalFieldsUnreadable ? throw new UnauthorizedAccessException() : Get(42u);
        public string DisplayName => Get("");
        public bool IsSystemSounds => Get(false);
        public AudioSessionState State => Get(AudioSessionState.Active);
        public AudioVolume Volume => Get(new AudioVolume(0.3f, false));
    }
}

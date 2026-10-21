using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AudioManager.Core.Events;
using AudioManager.Core.Interfaces;
using AudioManager.Core.Models;
using AudioManager.Windows.AudioSessions;
using AudioManager.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace AudioManager.Windows;

/// <summary>增量维护已知会话；所有本机注册在专用 MTA 线程创建和释放。</summary>
public sealed class WindowsAudioSessionMonitor : IAudioSessionMonitor
{
    private readonly Func<string, IAudioSessionSource> sourceFactory;
    private readonly ILogger<WindowsAudioSessionMonitor> logger;
    private readonly AudioThread audioThread;
    private readonly object gate = new();
    private readonly Dictionary<string, Registration> registrations = new(StringComparer.Ordinal);
    private readonly Channel<AudioSessionCreated> notifications = Channel.CreateUnbounded<AudioSessionCreated>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private bool disposed;
    private int reading;

    public WindowsAudioSessionMonitor(ILogger<WindowsAudioSessionMonitor> logger)
        : this(id => new NAudioSessionSource(id, logger), logger, new AudioThread()) { }

    internal WindowsAudioSessionMonitor(Func<string, IAudioSessionSource> sourceFactory,
        ILogger<WindowsAudioSessionMonitor> logger, AudioThread audioThread)
    {
        this.sourceFactory = sourceFactory;
        this.logger = logger;
        this.audioThread = audioThread;
    }

    public Task<AudioEnumeration<AudioSession>> StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return ExecuteAsync(() =>
        {
            if (registrations.TryGetValue(deviceId, out var existing)) return Snapshot(existing);
            var registration = new Registration(sourceFactory(deviceId));
            try
            {
                var initial = registration.Source.Initialize(data => QueueCreated(deviceId, registration, data));
                foreach (var session in initial.Items)
                {
                    if (session.Key.DeviceId != deviceId)
                        throw new InvalidOperationException("会话所属设备与监听设备不一致。");
                    if (session.State != AudioSessionState.Expired) registration.Sessions.TryAdd(session.Key, session);
                }
                registration.InitialIssues = initial.Issues.ToArray();
                LogIssues(initial.Issues);
                registrations.Add(deviceId, registration);
                return Snapshot(registration);
            }
            catch
            {
                Release(registration);
                throw;
            }
        }, cancellationToken);
    }

    public async Task StopAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await Schedule(() =>
        {
            if (registrations.Remove(deviceId, out var registration)) Release(registration);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<AudioEnumeration<AudioSession>> GetKnownSessionsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return Schedule(() => registrations.TryGetValue(deviceId, out var registration)
            ? Snapshot(registration) : throw new InvalidOperationException("该设备尚未开始监听。"), cancellationToken);
    }

    public async IAsyncEnumerable<AudioSessionCreated> ReadCreatedSessionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
            throw new InvalidOperationException("会话创建通知只支持一个同时运行的消费者。");
        try
        {
            await foreach (var item in notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally { Volatile.Write(ref reading, 0); }
    }

    private void QueueCreated(string deviceId, Registration registration, SessionCreatedData data)
    {
        Task<bool> work;
        lock (gate)
        {
            if (disposed) return;
            work = audioThread.InvokeAsync(() =>
            {
                if (!registrations.TryGetValue(deviceId, out var current) || !ReferenceEquals(current, registration)) return false;
                try
                {
                    if (data.Info.State == AudioSessionState.Expired) return false;
                    var key = new AudioSessionKey(deviceId, data.Info.InstanceId);
                    if (registration.Sessions.ContainsKey(key)) return false;
                    var issues = data.Issues.ToList();
                    var session = SessionSnapshotReader.Read(deviceId, data.Info, issues);
                    registration.Sessions.Add(key, session);
                    LogIssues(issues);
                    notifications.Writer.TryWrite(new(session, issues.AsReadOnly()));
                    return true;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "设备 {DeviceId} 的新会话无法加入集合", deviceId);
                    return false;
                }
            }, default);
        }
        // 不在系统回调中等待，也不运行任何调用方代码。
        _ = ObserveCallbackAsync(work);
    }

    private async Task ObserveCallbackAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) { logger.LogError(exception, "处理会话创建通知失败"); }
    }

    private Task<T> Schedule<T>(Func<T> action, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return audioThread.InvokeAsync(action, cancellationToken);
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        try { return await Schedule(action, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException and not ObjectDisposedException)
        {
            logger.LogError(exception, "监听会话创建失败");
            if (exception is AudioControlException) throw;
            var code = exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005)
                ? AudioErrorCode.AccessDenied : AudioErrorCode.ApiFailure;
            throw new AudioControlException(code, "监听会话创建", "注册或读取会话失败，请检查设备后重试。", exception);
        }
    }

    private static AudioEnumeration<AudioSession> Snapshot(Registration registration)
        => new(Array.AsReadOnly(registration.Sessions.Values.ToArray()), Array.AsReadOnly(registration.InitialIssues));

    private void LogIssues(IEnumerable<AudioReadIssue> issues)
    {
        foreach (var issue in issues)
            logger.LogWarning("会话信息部分读取失败：{ItemId}，{Message}，HRESULT={HResult}", issue.ItemId, issue.Message, issue.NativeErrorCode);
    }

    private void Release(Registration registration)
    {
        try { registration.Source.Dispose(); }
        catch (Exception exception) { logger.LogError(exception, "释放会话创建监听失败"); }
    }

    public void Dispose()
    {
        Task<bool> cleanup;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cleanup = audioThread.InvokeAsync(() =>
            {
                foreach (var registration in registrations.Values) Release(registration);
                registrations.Clear();
                notifications.Writer.TryComplete();
                return true;
            }, default);
        }
        try { cleanup.GetAwaiter().GetResult(); }
        catch (Exception exception) { logger.LogError(exception, "关闭会话监听工作线程失败"); }
        finally
        {
            notifications.Writer.TryComplete();
            audioThread.Dispose();
        }
    }

    private sealed class Registration(IAudioSessionSource source)
    {
        internal IAudioSessionSource Source { get; } = source;
        internal Dictionary<AudioSessionKey, AudioSession> Sessions { get; } = [];
        internal AudioReadIssue[] InitialIssues { get; set; } = [];
    }
}

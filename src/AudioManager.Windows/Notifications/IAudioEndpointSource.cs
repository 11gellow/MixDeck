using AudioManager.Core.Models;
using AudioManager.Windows.AudioSessions;

namespace AudioManager.Windows.Notifications;

/// <summary>初始化、读取及释放均由所属音频工作线程执行，回调只输出托管通知。</summary>
internal interface IAudioEndpointSource : IDisposable
{
    EndpointInitialState Initialize(Action<EndpointNotification> onNotification);
}

internal sealed record EndpointInitialState(AudioVolume MasterVolume, AudioEnumeration<AudioSession> Sessions);
internal abstract record EndpointNotification;
internal sealed record EndpointSessionCreated(SessionCreatedData Data) : EndpointNotification;
internal sealed record EndpointSessionVolumeChanged(string InstanceId, AudioVolume Volume, Guid EventContext) : EndpointNotification;
internal sealed record EndpointSessionStateChanged(string InstanceId, AudioSessionState State) : EndpointNotification;
internal sealed record EndpointSessionRemoved(string InstanceId) : EndpointNotification;
internal sealed record EndpointMasterVolumeChanged(AudioVolume Volume, Guid EventContext) : EndpointNotification;
internal sealed record EndpointInvalidated(string Reason, int? NativeErrorCode = null) : EndpointNotification;
internal sealed record EndpointReadFailure(string Reason, int? NativeErrorCode = null, string? InstanceId = null) : EndpointNotification;

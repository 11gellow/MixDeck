namespace AudioManager.Core.Models;

public enum AudioErrorCode { DeviceUnavailable, SessionUnavailable, AccessDenied, ApiFailure }

public sealed class AudioControlException : Exception
{
    public AudioErrorCode Code { get; }
    public string Operation { get; }
    public int? NativeErrorCode => InnerException?.HResult;

    public AudioControlException(AudioErrorCode code, string operation, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Operation = operation;
    }
}

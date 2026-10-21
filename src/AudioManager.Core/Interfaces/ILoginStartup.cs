namespace AudioManager.Core.Interfaces;

public interface ILoginStartup
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

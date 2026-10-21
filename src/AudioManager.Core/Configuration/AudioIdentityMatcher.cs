using AudioManager.Core.Models;

namespace AudioManager.Core.Configuration;

public static class AudioIdentityMatcher
{
    public static ApplicationIdentity? Identify(AudioSession session)
        => session.IsSystemSounds ? new(ApplicationIdentityKind.SystemSounds, "system-sounds")
            : ConfigurationJson.IsAbsoluteExecutablePath(session.ProcessPath)
                ? new(ApplicationIdentityKind.ExecutablePath, session.ProcessPath!) : null;

    public static bool Matches(ApplicationIdentity identity, AudioSession session)
        => Identify(session) is { } current && current.Kind == identity.Kind
           && string.Equals(current.Value, identity.Value, StringComparison.OrdinalIgnoreCase);

    public static bool EqualVolume(SavedVolume desired, AudioVolume actual)
        => Math.Abs(desired.Level - actual.Level) <= .005f && desired.IsMuted == actual.IsMuted;
}

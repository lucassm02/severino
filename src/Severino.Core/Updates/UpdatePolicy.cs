namespace Severino.Core.Updates;

/// <summary>When to ask GitHub, and which release is worth a notice.</summary>
public static class UpdatePolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>A day since the last answer, or a clock that went backwards.</summary>
    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || now - last >= Interval || last > now;

    /// <summary>Newer than the app and than the version the person chose to skip.</summary>
    public static bool ShouldOffer(ReleaseInfo release, Version current, string skippedVersion) =>
        release.Version > current
        && (UpdateChecker.ParseVersion(skippedVersion) is not { } skipped || release.Version > skipped);
}

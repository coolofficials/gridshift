namespace GridShift;

public sealed record OwnedProcess(ProcessIdentity Identity, string Executable)
{
    public int ProcessId => Identity.ProcessId;
    public DateTime StartTimeUtc => Identity.StartTimeUtc;
}

public enum CleanupStep { Cancel, RequestGracefulClose, Wait, ForceTerminate, Complete }

public sealed class CompanionCleanupProgress(DateTime closeRequestedUtc)
{
    public DateTime CloseRequestedUtc { get; } = closeRequestedUtc;
    public bool CloseRequested { get; set; } = true;
}

public static class ProcessSafety
{
    public static CleanupStep NextCleanupStep(CompanionCleanupProgress progress, DateTime nowUtc, TimeSpan grace,
        bool identityAndOwnershipValid, bool protectedByGameOrSharedUse, bool gameRestarted, bool processExited, bool forceOptIn)
    {
        if (gameRestarted || !identityAndOwnershipValid || protectedByGameOrSharedUse) return CleanupStep.Cancel;
        if (processExited) return CleanupStep.Complete;
        if (nowUtc - progress.CloseRequestedUtc < grace) return CleanupStep.Wait;
        return forceOptIn ? CleanupStep.ForceTerminate : CleanupStep.Cancel;
    }

    public static bool ShouldLaunchCompanion(int matchingOrUncertainProcessCount) => matchingOrUncertainProcessCount == 0;

    public static bool IsSharedWithActiveProfile(string profileId, string executable, IEnumerable<(string ProfileId, string Executable)> activeUses)
        => activeUses.Any(use => use.ProfileId != profileId && PathsEqual(use.Executable, executable));

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool MayMoveCompanionWindow(OwnedProcess? owned, ProcessIdentity identity, string executable)
        => owned is not null
            && owned.Identity == identity
            && string.Equals(owned.Executable, executable, StringComparison.OrdinalIgnoreCase);

    public static bool IsStableGame(DateTime firstSeenUtc, DateTime nowUtc, TimeSpan grace) => nowUtc - firstSeenUtc >= grace;

    public static bool HasGameEnded(int activeProcessCount, DateTime lastSeenUtc, DateTime nowUtc, TimeSpan grace)
        => activeProcessCount == 0 && nowUtc - lastSeenUtc >= grace;

    public static bool MayTerminate(
        OwnedProcess owned,
        bool explicitlyOptedIn,
        bool gameStillActive,
        bool anotherActiveProfileUsesProcess,
        bool processIdentityStillMatches,
        int matchingOrUncertainProcessCount)
    {
        return explicitlyOptedIn
            && !gameStillActive
            && !anotherActiveProfileUsesProcess
            && processIdentityStillMatches
            && matchingOrUncertainProcessCount == 1;
    }
}

namespace GridShift;

public sealed class ProfileRuntimeState(DateTime firstSeenUtc)
{
    public DateTime FirstSeenUtc { get; } = firstSeenUtc;
    public DateTime LastSeenUtc { get; set; } = firstSeenUtc;
    public bool CompanionsChecked { get; set; }
    public bool SwitchAttempted { get; set; }
    public Guid? DesktopId { get; set; }
    public HashSet<ProcessIdentity> ConfirmedGameRoots { get; } = [];
    public HashSet<ProcessIdentity> KnownGameProcesses { get; } = [];
    public int ActiveFamilyProcessCount { get; set; }
}

public sealed record ProfileObservation(
    IReadOnlySet<ProcessIdentity> ActiveFamily,
    bool IsActive,
    bool StartCompanions,
    bool EndSession);

public sealed record ProfilePollInput(
    string ProfileId,
    ProfileRuntimeState State,
    IReadOnlyCollection<ProcessIdentity> ConfirmedRoots,
    bool RootActive,
    TimeSpan ExitDebounce = default);

public static class ProfileOrchestration
{
    public static IReadOnlyDictionary<string, ProfileObservation> ObserveAll(
        IEnumerable<ProfilePollInput> profiles,
        IReadOnlyList<ProcessTreeEntry> snapshot,
        bool snapshotAvailable,
        DateTime nowUtc,
        TimeSpan startStability,
        TimeSpan exitGrace)
        => profiles.ToDictionary(profile => profile.ProfileId, profile => Observe(
            profile.State, snapshot, profile.ConfirmedRoots, profile.RootActive,
            snapshotAvailable, nowUtc, startStability,
            profile.ExitDebounce > TimeSpan.Zero ? profile.ExitDebounce : exitGrace));

    public static ProfileObservation Observe(
        ProfileRuntimeState state,
        IReadOnlyList<ProcessTreeEntry> snapshot,
        IEnumerable<ProcessIdentity> confirmedRoots,
        bool rootActive,
        bool snapshotAvailable,
        DateTime nowUtc,
        TimeSpan startStability,
        TimeSpan exitGrace)
    {
        foreach (var root in confirmedRoots)
        {
            state.ConfirmedGameRoots.Add(root);
            state.KnownGameProcesses.Add(root);
        }
        var family = ProcessTreePolicy.Expand(snapshot, state.KnownGameProcesses);
        state.KnownGameProcesses.UnionWith(family);
        state.ActiveFamilyProcessCount = ProcessTreePolicy.CountActive(snapshot, family);
        var uncertain = ProcessTreePolicy.HasUncertainChild(snapshot, family);
        var active = rootActive || state.ActiveFamilyProcessCount > 0 || uncertain;
        if (!snapshotAvailable) active = true;
        if (active)
        {
            state.LastSeenUtc = nowUtc;
            var hasConfirmedFamily = state.KnownGameProcesses.Count > 0;
            var start = snapshotAvailable && !state.CompanionsChecked && hasConfirmedFamily
                && ProcessSafety.IsStableGame(state.FirstSeenUtc, nowUtc, startStability);
            if (start) state.CompanionsChecked = true;
            return new(family, true, start, false);
        }
        var ended = snapshotAvailable && ProcessSafety.HasGameEnded(state.ActiveFamilyProcessCount, state.LastSeenUtc, nowUtc, exitGrace);
        return new(family, false, false, ended);
    }

    public static IReadOnlySet<ProcessIdentity> PlacementFamily(ProfileRuntimeState state, IReadOnlyList<ProcessTreeEntry> snapshot)
    {
        var family = ProcessTreePolicy.Expand(snapshot, state.KnownGameProcesses);
        state.KnownGameProcesses.UnionWith(family);
        return family;
    }

    public static bool ShouldSwitchDesktop(bool requested, bool alreadyAttempted, bool hasWindows, bool placementSucceeded)
        => requested && !alreadyAttempted && hasWindows && placementSucceeded;

    public static bool MayPlaceOwnedCompanionWindow(
        OwnedProcess owned,
        ProcessIdentity currentIdentity,
        string executable,
        IReadOnlySet<ProcessIdentity> activeGameFamily,
        IReadOnlySet<string> activeGameExecutables,
        bool sharedWithProfile)
        => !sharedWithProfile
            && !activeGameFamily.Contains(currentIdentity)
            && !activeGameExecutables.Contains(Path.GetFullPath(executable))
            && ProcessSafety.MayMoveCompanionWindow(owned, currentIdentity, executable);

    public static bool TryCleanupOwnedCompanion(
        OwnedProcess owned,
        IReadOnlyList<ProcessTreeEntry> snapshot,
        IReadOnlySet<ProcessIdentity> observedCompanionFamily,
        IReadOnlySet<ProcessIdentity> activeGameFamily,
        IReadOnlySet<string> protectedGameExecutables,
        bool sharedWithProfile,
        bool identityStillMatches,
        int matchingOrUncertainProcessCount,
        Func<bool> terminate)
        => MayCleanupOwnedCompanion(owned, snapshot, observedCompanionFamily, activeGameFamily,
            protectedGameExecutables, sharedWithProfile, identityStillMatches, matchingOrUncertainProcessCount)
            && terminate();

    public static bool MayCleanupOwnedCompanion(
        OwnedProcess owned,
        IReadOnlyList<ProcessTreeEntry> snapshot,
        IReadOnlySet<ProcessIdentity> observedCompanionFamily,
        IReadOnlySet<ProcessIdentity> activeGameFamily,
        IReadOnlySet<string> protectedGameExecutables,
        bool sharedWithProfile,
        bool identityStillMatches,
        int matchingOrUncertainProcessCount,
        bool snapshotAvailable = true,
        bool configurationMatches = true)
    {
        if (!snapshotAvailable || !configurationMatches) return false;
        var companionFamily = ProcessTreePolicy.Expand(snapshot, observedCompanionFamily.Append(owned.Identity));
        var activeCompanionMembers = ProcessTreePolicy.CountActive(snapshot, companionFamily);
        var uncertainDescendant = ProcessTreePolicy.HasUncertainChild(snapshot, companionFamily);
        var gameIdentity = activeGameFamily.Contains(owned.Identity)
            || companionFamily.Any(activeGameFamily.Contains);
        var executableProtected = protectedGameExecutables.Contains(Path.GetFullPath(owned.Executable));
        return identityStillMatches && !sharedWithProfile && !gameIdentity && !executableProtected
            && companionFamily.SetEquals([owned.Identity]) && activeCompanionMembers <= 1 && !uncertainDescendant
            && ProcessSafety.MayTerminate(owned, true, false, false, true, matchingOrUncertainProcessCount);
    }
}

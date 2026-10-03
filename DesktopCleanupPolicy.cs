namespace GridShift;

public sealed record DesktopRemovalDecision(bool Allowed, Guid? FallbackDesktop, bool MustSwitchBeforeRemoval, string Reason);
public sealed record PendingDesktopCleanup(string ProfileId, Guid DesktopId, IReadOnlyList<ProcessIdentity> OwnedCompanionFamily);
public enum PendingDesktopCleanupReadiness { Cancel, Wait, Attempt }

public sealed class PendingDesktopCleanupQueue
{
    private readonly Dictionary<string, PendingDesktopCleanup> pending = new(StringComparer.Ordinal);

    public IReadOnlyCollection<PendingDesktopCleanup> Items => pending.Values.ToArray();
    public void Enqueue(string profileId, Guid desktopId, IEnumerable<ProcessIdentity>? ownedCompanionFamily = null)
        => pending[profileId] = new(profileId, desktopId, ownedCompanionFamily?.Distinct().ToArray() ?? []);
    public void Remove(string profileId) => pending.Remove(profileId);
}

public static class PendingDesktopCleanupPolicy
{
    public static PendingDesktopCleanupReadiness Evaluate(bool profileExists, bool cleanupConsented, bool sameDesktop,
        bool ownershipConfirmed, bool profileActive, bool processSnapshotAvailable, bool companionCleanupPending,
        bool ownedCompanionFamilyComplete, bool sharedWithActiveProfile)
    {
        if (!profileExists || !cleanupConsented || !sameDesktop || !ownershipConfirmed || profileActive)
            return PendingDesktopCleanupReadiness.Cancel;
        if (!processSnapshotAvailable || companionCleanupPending || !ownedCompanionFamilyComplete || sharedWithActiveProfile)
            return PendingDesktopCleanupReadiness.Wait;
        return PendingDesktopCleanupReadiness.Attempt;
    }

    public static bool IsOwnedCompanionFamilyComplete(IReadOnlyCollection<ProcessIdentity> ownedFamily,
        IReadOnlyList<ProcessTreeEntry> snapshot, out bool uncertain)
    {
        uncertain = false;
        foreach (var identity in ownedFamily)
        {
            var process = snapshot.FirstOrDefault(entry => entry.ProcessId == identity.ProcessId);
            if (process is null) continue;
            if (process.Identity is null) { uncertain = true; continue; }
            if (process.Identity.Value == identity) return false;
        }
        return !uncertain;
    }
}

public static class DesktopCleanupPolicy
{
    public static DesktopRemovalDecision Evaluate(
        Guid targetDesktop,
        bool positivelyLauncherCreated,
        bool sharedWithActiveProfile,
        IReadOnlyList<Guid> desktops,
        bool allDesktopWindowsEnumerated,
        IReadOnlyCollection<Guid> windowDesktopIds,
        Guid currentDesktop)
    {
        if (!positivelyLauncherCreated) return Block("이 런처가 만든 것으로 확인되지 않은 데스크톱은 유지합니다.");
        if (sharedWithActiveProfile) return Block("다른 실행 중인 게임도 이 데스크톱을 사용 중이라 유지합니다.");
        if (!allDesktopWindowsEnumerated) return Block("열린 창을 모두 확인하지 못해 데스크톱을 유지합니다.");
        if (!desktops.Contains(targetDesktop)) return Block("정리할 데스크톱이 이미 없어졌습니다.");
        if (!desktops.Contains(currentDesktop)) return Block("현재 데스크톱을 확인하지 못해 안전한 복귀 경로를 정할 수 없습니다.");
        if (windowDesktopIds.Any(id => !desktops.Contains(id))) return Block("일부 창의 데스크톱 위치를 확인하지 못해 유지합니다.");
        if (windowDesktopIds.Contains(targetDesktop)) return Block("데스크톱에 창이 남아 있어 유지합니다.");

        var fallback = currentDesktop != targetDesktop && desktops.Contains(currentDesktop)
            ? currentDesktop
            : desktops.FirstOrDefault(id => id != targetDesktop);
        if (fallback == Guid.Empty || fallback == targetDesktop)
            return Block("돌아갈 다른 데스크톱을 확인하지 못해 정리를 취소합니다.");
        return new(true, fallback, currentDesktop == targetDesktop, "");
    }

    private static DesktopRemovalDecision Block(string reason) => new(false, null, false, reason);
}

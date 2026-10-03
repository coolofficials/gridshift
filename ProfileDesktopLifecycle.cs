namespace GridShift;

public sealed class ProfileDesktopLifecycle(VirtualDesktopCoordinator coordinator, DesktopOwnershipStore ownership)
{
    public DesktopOperation Ensure(GameProfile profile)
    {
        var operation = coordinator.Ensure(profile);
        if (!operation.Created || operation.DesktopId is not Guid desktopId) return operation;
        try
        {
            ownership.MarkCreated(profile.Id, desktopId);
            return operation;
        }
        catch (Exception ex)
        {
            var warning = $"새 데스크톱 생성 기록을 저장하지 못했습니다. 자동 정리는 꺼집니다 ({ex.GetType().Name}).";
            return operation with { Warning = operation.Warning is null ? warning : $"{operation.Warning} {warning}" };
        }
    }

    public bool IsCreatedByLauncher(string profileId, Guid desktopId)
        => ownership.IsCreatedByLauncher(profileId, desktopId);

    public void Forget(string profileId, Guid desktopId)
        => ownership.Forget(profileId, desktopId);
}

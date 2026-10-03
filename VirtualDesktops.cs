namespace GridShift;

public enum VirtualDesktopApiKind { Unsupported, Windows10, Windows11_24H2, Windows11_25H2 }

public sealed record DesktopOperation(Guid? DesktopId, string? Warning, bool Created = false);

public interface IVirtualDesktopApi
{
    IReadOnlyList<Guid> GetDesktops();
    Guid CreateDesktop();
    Guid GetCurrentDesktop();
    void SwitchTo(Guid desktopId);
    void MoveWindowToDesktop(IntPtr window, Guid desktopId);
    Guid GetWindowDesktop(IntPtr window);
    bool IsWindowPinned(IntPtr window);
    void RemoveDesktop(Guid desktopId, Guid fallbackDesktopId);
}

public static class VirtualDesktopCompatibility
{
    public static VirtualDesktopApiKind Select(int major, int minor, int build, int updateRevision)
    {
        if (major != 10 || minor != 0) return VirtualDesktopApiKind.Unsupported;
        if (build is >= 19041 and <= 19045) return VirtualDesktopApiKind.Windows10;
        if (build == 26100 && updateRevision >= 2605) return VirtualDesktopApiKind.Windows11_24H2;
        if (build == 26200 && updateRevision >= 8117) return VirtualDesktopApiKind.Windows11_25H2;
        return VirtualDesktopApiKind.Unsupported;
    }
}

public sealed class VirtualDesktopCoordinator
{
    private readonly Func<IVirtualDesktopApi?> createApi;

    public VirtualDesktopCoordinator() : this(WindowsVirtualDesktopApi.Create) { }
    public VirtualDesktopCoordinator(Func<IVirtualDesktopApi?> createApi) => this.createApi = createApi;

    public DesktopOperation Ensure(GameProfile profile)
    {
        try
        {
            var api = createApi();
            if (api is null) return new(null, "이 Windows 빌드의 가상 데스크톱 API는 검증된 버전이 아닙니다. 안전을 위해 일반 데스크톱에서 계속합니다.");
            var desktops = api.GetDesktops();
            if (Guid.TryParse(profile.DesktopId, out var savedId) && desktops.Contains(savedId)) return new(savedId, null);
            var createdId = api.CreateDesktop();
            profile.DesktopId = createdId.ToString("D");
            return new(createdId, null, true);
        }
        catch (Exception ex)
        {
            return new(null, $"가상 데스크톱 생성/조회에 실패했습니다 ({ex.GetType().Name}: {ex.Message}). 현재 데스크톱에서 계속합니다.");
        }
    }

    public string? PlaceWindows(Guid desktopId, IEnumerable<IntPtr> windowHandles, Func<IntPtr, bool>? mayMoveWindow = null)
    {
        try
        {
            var api = createApi();
            if (api is null) return "검증되지 않은 Windows 빌드이므로 가상 데스크톱 창 배치를 건너뛰었습니다.";
            var failures = new List<string>();
            foreach (var hwnd in windowHandles.Where(x => x != IntPtr.Zero).Distinct())
            {
                if (mayMoveWindow is not null && !mayMoveWindow(hwnd)) continue;
                try
                {
                    if (api.GetWindowDesktop(hwnd) == desktopId || api.IsWindowPinned(hwnd)) continue;
                    api.MoveWindowToDesktop(hwnd, desktopId);
                    if (api.GetWindowDesktop(hwnd) != desktopId) failures.Add("창 이동 확인 실패");
                }
                catch (Exception ex) { failures.Add($"{ex.GetType().Name}: {ex.Message}"); }
            }
            return failures.Count == 0 ? null : $"{failures.Count}개 창 배치가 실패했습니다 ({failures[0]}). 지원되지 않는 창은 현재 데스크톱에 남아 있습니다.";
        }
        catch (Exception ex) { return $"창 배치 API 실패 ({ex.GetType().Name}: {ex.Message}). 일반 데스크톱 동작을 유지합니다."; }
    }

    public string? MoveWindowToCurrentDesktop(IntPtr window)
    {
        try
        {
            var api = createApi();
            if (api is null) return "GridShift 창을 다른 데스크톱에서 가져오는 API가 지원되지 않습니다. 트레이에서 다시 열기를 시도하거나 데스크톱을 전환해 사용하세요.";
            var currentDesktop = api.GetCurrentDesktop();
            if (api.IsWindowPinned(window)) return null;
            if (api.GetWindowDesktop(window) != currentDesktop) api.MoveWindowToDesktop(window, currentDesktop);
            if (api.GetWindowDesktop(window) != currentDesktop) return "GridShift 창을 현재 데스크톱으로 옮겼는지 확인하지 못했습니다.";
            return null;
        }
        catch (Exception ex) { return $"GridShift 창을 현재 데스크톱으로 가져오지 못했습니다 ({ex.GetType().Name}: {ex.Message})."; }
    }

    public string? RemoveCreatedDesktop(Guid targetDesktopId, bool positivelyLauncherCreated, bool sharedWithActiveProfile,
        Func<IReadOnlyList<IntPtr>> enumerateAllWindows, IntPtr launcherWindow)
    {
        try
        {
            var api = createApi();
            if (api is null) return "이 Windows 빌드에서는 데스크톱 정리를 지원하지 않아 그대로 유지합니다.";
            var desktops = api.GetDesktops();
            var current = api.GetCurrentDesktop();
            var windowDesktopIds = ReadWindowDesktopIds(api, enumerateAllWindows());
            var launcherDesktop = Guid.Empty;
            var launcherKnown = launcherWindow != IntPtr.Zero && windowDesktopIds.TryGetValue(launcherWindow, out launcherDesktop);
            if (launcherKnown && !desktops.Contains(launcherDesktop)) return "GridShift 창의 데스크톱 위치를 확인하지 못해 정리를 취소합니다.";
            var launcherOnTarget = launcherKnown && launcherDesktop == targetDesktopId;
            var otherWindowDesktopIds = windowDesktopIds.Where(pair => pair.Key != launcherWindow).Select(pair => pair.Value).ToArray();
            var decision = DesktopCleanupPolicy.Evaluate(targetDesktopId, positivelyLauncherCreated, sharedWithActiveProfile,
                desktops, true, otherWindowDesktopIds, current);
            if (!decision.Allowed || decision.FallbackDesktop is not Guid fallback)
                return decision.Reason;
            if (decision.MustSwitchBeforeRemoval)
            {
                api.SwitchTo(fallback);
                if (api.GetCurrentDesktop() != fallback) return "다른 데스크톱으로 안전하게 돌아왔는지 확인하지 못해 정리를 취소합니다.";
            }
            if (launcherOnTarget)
            {
                if (api.IsWindowPinned(launcherWindow)) return "GridShift 창이 모든 데스크톱에 표시되도록 고정되어 있어 정리하지 않았습니다.";
                api.MoveWindowToDesktop(launcherWindow, fallback);
                if (api.GetWindowDesktop(launcherWindow) != fallback) return "GridShift 창을 안전한 데스크톱으로 옮겼는지 확인하지 못해 정리를 취소합니다.";
            }
            var finalWindowDesktopIds = ReadWindowDesktopIds(api, enumerateAllWindows());
            var finalDesktops = api.GetDesktops();
            if (finalWindowDesktopIds.Values.Any(id => !finalDesktops.Contains(id))) return "일부 창의 위치를 다시 확인하지 못해 데스크톱을 그대로 유지합니다.";
            if (finalWindowDesktopIds.Values.Contains(targetDesktopId)) return "창이 남아 있거나 새로 열려 데스크톱을 그대로 유지합니다.";
            if (!finalDesktops.Contains(targetDesktopId) || !finalDesktops.Contains(fallback)) return "정리할 데스크톱이나 돌아갈 데스크톱이 바뀌어 작업을 취소합니다.";
            if (api.GetCurrentDesktop() != fallback) return "현재 데스크톱이 안전한 복귀 대상과 달라져 정리를 취소합니다.";
            api.RemoveDesktop(targetDesktopId, fallback);
            if (api.GetDesktops().Contains(targetDesktopId) || api.GetCurrentDesktop() == targetDesktopId)
                return "데스크톱 삭제를 확인하지 못했습니다. 안전한 다른 데스크톱에 그대로 머뭅니다.";
            return null;
        }
        catch (Exception ex) { return $"데스크톱 정리 API를 확인하지 못해 그대로 유지합니다 ({ex.GetType().Name}: {ex.Message})."; }
    }

    private static Dictionary<IntPtr, Guid> ReadWindowDesktopIds(IVirtualDesktopApi api, IReadOnlyList<IntPtr> windows)
    {
        var result = new Dictionary<IntPtr, Guid>();
        foreach (var window in windows.Where(window => window != IntPtr.Zero).Distinct())
            result.Add(window, api.GetWindowDesktop(window));
        return result;
    }

    public string? SwitchTo(Guid desktopId)
    {
        try
        {
            var api = createApi();
            if (api is null) return "검증되지 않은 Windows 빌드이므로 가상 데스크톱 전환을 건너뛰었습니다.";
            if (!api.GetDesktops().Contains(desktopId)) return "가상 데스크톱을 찾지 못해 자동 전환하지 않았습니다.";
            api.SwitchTo(desktopId);
            return null;
        }
        catch (Exception ex) { return $"가상 데스크톱 전환 실패 ({ex.GetType().Name}: {ex.Message}). 현재 작업을 유지합니다."; }
    }
}

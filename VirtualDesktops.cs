namespace GridShift;

public enum VirtualDesktopApiKind { Unsupported, Windows10, Windows11_24H2, Windows11_25H2 }

public sealed record DesktopOperation(Guid? DesktopId, string? Warning);

public interface IVirtualDesktopApi
{
    IReadOnlyList<Guid> GetDesktops();
    Guid CreateDesktop();
    Guid GetCurrentDesktop();
    void SwitchTo(Guid desktopId);
    void MoveWindowToDesktop(IntPtr window, Guid desktopId);
    Guid GetWindowDesktop(IntPtr window);
    bool IsWindowPinned(IntPtr window);
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
            return new(createdId, null);
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

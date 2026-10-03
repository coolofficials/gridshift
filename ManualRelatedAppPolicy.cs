namespace GridShift;

public sealed record ManualWindowCandidate(int ProcessId, ProcessIdentity Identity, string Executable, IntPtr WindowHandle);

public sealed record ManualWindowObservation(IntPtr WindowHandle, int ProcessId, ProcessIdentity? Identity, string? Executable);

public static class ManualRelatedAppPolicy
{
    public static bool TryCreateCandidate(
        int processId,
        string? executable,
        ProcessIdentity? identity,
        IntPtr windowHandle,
        string configuredExecutable,
        out ManualWindowCandidate? candidate,
        out string reason)
    {
        candidate = null;
        if (processId <= 0 || identity is not ProcessIdentity confirmed || confirmed.ProcessId != processId)
        {
            reason = "실행 중인 앱 정보를 확인할 수 없어 창을 열지 않았습니다.";
            return false;
        }
        if (windowHandle == IntPtr.Zero)
        {
            reason = "이 앱의 열려 있는 창을 찾지 못했습니다.";
            return false;
        }
        if (!PathsEqual(executable, configuredExecutable))
        {
            reason = "선택한 파일이 등록된 앱과 달라 창을 열지 않았습니다.";
            return false;
        }
        candidate = new ManualWindowCandidate(processId, confirmed, Path.GetFullPath(executable!), windowHandle);
        reason = "";
        return true;
    }

    public static bool TryInvokeAction(
        ManualWindowCandidate candidate,
        string configuredExecutable,
        Func<IntPtr, ManualWindowObservation?> reobserveWindowOwner,
        Action<IntPtr> action,
        out string reason)
    {
        if (candidate.WindowHandle == IntPtr.Zero
            || candidate.Identity.ProcessId != candidate.ProcessId
            || !PathsEqual(candidate.Executable, configuredExecutable))
        {
            reason = "등록된 앱과 창이 달라 안전하게 열 수 없습니다.";
            return false;
        }
        var current = reobserveWindowOwner(candidate.WindowHandle);
        if (current is null
            || current.WindowHandle != candidate.WindowHandle
            || current.ProcessId != candidate.ProcessId
            || current.Identity != candidate.Identity
            || !PathsEqual(current.Executable, configuredExecutable))
        {
            reason = "창이 다른 앱으로 바뀌었거나 실행 위치를 확인할 수 없어 아무 동작도 하지 않았습니다.";
            return false;
        }
        action(candidate.WindowHandle);
        reason = "";
        return true;
    }

    private static bool PathsEqual(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)) return false;
        try { return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

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
            reason = "프로세스 identity를 확인할 수 없습니다.";
            return false;
        }
        if (windowHandle == IntPtr.Zero)
        {
            reason = "프로세스의 창 handle을 확인할 수 없습니다.";
            return false;
        }
        if (!PathsEqual(executable, configuredExecutable))
        {
            reason = "실행 경로가 설정된 관련 앱과 일치하지 않거나 확인되지 않았습니다.";
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
            reason = "선택 앱의 실행 경로 또는 identity가 설정과 일치하지 않습니다.";
            return false;
        }
        var current = reobserveWindowOwner(candidate.WindowHandle);
        if (current is null
            || current.WindowHandle != candidate.WindowHandle
            || current.ProcessId != candidate.ProcessId
            || current.Identity != candidate.Identity
            || !PathsEqual(current.Executable, configuredExecutable))
        {
            reason = "창 소유 PID, process creation identity 또는 실행 경로가 바뀌었거나 확인되지 않아 안전 동작을 건너뛰었습니다.";
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

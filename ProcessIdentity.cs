using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace GridShift;

public readonly record struct ProcessIdentity(int ProcessId, long CreationFileTime)
{
    public DateTime StartTimeUtc => DateTime.FromFileTimeUtc(CreationFileTime);
}

public static class ProcessIdentityProbe
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSynchronize = 0x00100000;
    private const uint WaitTimeout = 0x00000102;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime creation, out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    public static SafeProcessHandle? OpenCleanupHandle(OwnedProcess expected)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | ProcessSynchronize, false, checked((uint)expected.ProcessId));
        if (handle.IsInvalid || !Matches(handle, expected)) { handle.Dispose(); return null; }
        return handle;
    }

    public static bool Matches(SafeProcessHandle handle, OwnedProcess expected)
    {
        if (handle.IsClosed || handle.IsInvalid || WaitForSingleObject(handle, 0) != WaitTimeout) return false;
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
        var identity = new ProcessIdentity(expected.ProcessId, ((long)created.High << 32) | created.Low);
        if (identity != expected.Identity) return false;
        var buffer = new StringBuilder(32768);
        uint size = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return false;
        try { return string.Equals(Path.GetFullPath(buffer.ToString()), expected.Executable, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool TryTerminate(SafeProcessHandle handle, OwnedProcess expected)
        => Matches(handle, expected) && TerminateProcess(handle, 1);

    public static bool TryGetIdentity(int processId, out ProcessIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsWindows() || processId <= 0) return false;
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, checked((uint)processId));
        if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
        identity = new ProcessIdentity(processId, ((long)created.High << 32) | created.Low);
        return true;
    }

    public static bool TryRead(int processId, out ProcessIdentity identity, out string executable)
    {
        identity = default;
        executable = "";
        if (!OperatingSystem.IsWindows() || processId <= 0) return false;
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, checked((uint)processId));
        if (handle.IsInvalid) return false;
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
        var buffer = new StringBuilder(32768);
        uint size = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return false;
        identity = new ProcessIdentity(processId, ((long)created.High << 32) | created.Low);
        executable = buffer.ToString();
        return true;
    }

}

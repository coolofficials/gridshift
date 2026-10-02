using System.Runtime.InteropServices;

namespace GridShift;

public sealed record ProcessTreeEntry(int ProcessId, int ParentProcessId, string Name, ProcessIdentity? Identity = null);

public static class ProcessTreePolicy
{
    public static HashSet<ProcessIdentity> Expand(IReadOnlyCollection<ProcessTreeEntry> snapshot, IEnumerable<ProcessIdentity> knownProcesses)
    {
        var family = new HashSet<ProcessIdentity>(knownProcesses);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var child in snapshot)
            {
                if (child.Identity is not ProcessIdentity childIdentity || family.Contains(childIdentity)) continue;
                var parent = snapshot.FirstOrDefault(x => x.ProcessId == child.ParentProcessId);
                if (parent?.Identity is not ProcessIdentity parentIdentity || !family.Contains(parentIdentity)) continue;
                if (childIdentity.CreationFileTime < parentIdentity.CreationFileTime) continue;
                if (family.Add(childIdentity)) changed = true;
            }
        }
        return family;
    }

    public static int CountActive(IReadOnlyCollection<ProcessTreeEntry> snapshot, IReadOnlySet<ProcessIdentity> family)
        => snapshot.Count(process => process.Identity is ProcessIdentity identity && family.Contains(identity));

    public static bool HasUncertainChild(IReadOnlyCollection<ProcessTreeEntry> snapshot, IReadOnlySet<ProcessIdentity> family)
    {
        var familyIds = family.Select(x => x.ProcessId).ToHashSet();
        return snapshot.Any(process => process.Identity is null && familyIds.Contains(process.ParentProcessId));
    }
}

public static class ProcessTreeProbe
{
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableName;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);

    public static IReadOnlyList<ProcessTreeEntry> ReadSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var handle = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (handle == InvalidHandle) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entries = new List<ProcessTreeEntry>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(handle, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            do
            {
                var processId = checked((int)entry.ProcessId);
                ProcessIdentity? identity = ProcessIdentityProbe.TryGetIdentity(processId, out var found) ? found : null;
                entries.Add(new ProcessTreeEntry(processId, checked((int)entry.ParentProcessId), entry.ExecutableName, identity));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(handle, ref entry));
            return entries;
        }
        finally { CloseHandle(handle); }
    }
}

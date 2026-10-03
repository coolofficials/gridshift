using System.Runtime.InteropServices;

namespace GridShift;

public static class AppPathSelectionPolicy
{
    public static bool TryValidateExecutable(string? path, out string fullPath, out string reason)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) { reason = "실행 파일을 선택하세요."; return false; }
        try { fullPath = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { reason = "파일 경로를 읽을 수 없습니다."; return false; }
        if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        { reason = "실제로 존재하는 .exe 파일을 선택하세요."; return false; }
        reason = "";
        return true;
    }

    public static bool TryResolveShortcut(string shortcutPath, out string executable, out string reason)
    {
        executable = "";
        reason = "바로가기를 읽지 못했습니다.";
        if (!OperatingSystem.IsWindows() || !shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(shortcutPath))
        { reason = "Windows .lnk 바로가기를 선택하세요."; return false; }
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;
            dynamic automation = shell;
            shortcut = automation.CreateShortcut(Path.GetFullPath(shortcutPath));
            dynamic link = shortcut;
            string target = link.TargetPath;
            return TryValidateExecutable(target, out executable, out reason);
        }
        catch (Exception ex) { reason = $"바로가기 대상을 확인하지 못했습니다 ({ex.GetType().Name})."; return false; }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}

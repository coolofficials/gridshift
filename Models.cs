using System.Text.Json;
using Microsoft.Win32;

namespace GridShift;

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "새 게임";
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string SteamUri { get; set; } = "";
    public bool UseVirtualDesktop { get; set; }
    public bool SwitchToVirtualDesktop { get; set; }
    public string DesktopId { get; set; } = "";
    public List<Companion> Companions { get; set; } = [];
    public List<RelatedApp> RelatedApps { get; set; } = [];
}

public sealed class Companion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool StopWhenGameEnds { get; set; }
    public bool ForceTerminateAfterGrace { get; set; }
}

public sealed record CompanionCleanupConfiguration(string Id, string Executable, string Arguments, bool StopWhenGameEnds, bool ForceTerminateAfterGrace)
{
    public static CompanionCleanupConfiguration Capture(Companion companion)
        => new(companion.Id, companion.Executable, companion.Arguments, companion.StopWhenGameEnds, companion.ForceTerminateAfterGrace);

    public bool Matches(Companion? current)
        => current is not null
            && string.Equals(current.Id, Id, StringComparison.Ordinal)
            && PathsEqual(current.Executable, Executable)
            && string.Equals(current.Arguments, Arguments, StringComparison.Ordinal)
            && current.StopWhenGameEnds == StopWhenGameEnds
            && current.ForceTerminateAfterGrace == ForceTerminateAfterGrace;

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

public sealed class CompanionEditorDraft
{
    private readonly List<Companion> items;

    public CompanionEditorDraft(IEnumerable<Companion> committed)
        => items = committed.Select(Clone).ToList();

    public IReadOnlyList<Companion> Items => items;

    public Companion Add(Companion companion)
    {
        var detached = Clone(companion);
        items.Add(detached);
        return detached;
    }

    public Companion? SetStopWhenGameEnds(string id, bool enabled)
    {
        var item = items.FirstOrDefault(companion => companion.Id == id);
        if (item is null) return null;
        item.StopWhenGameEnds = enabled;
        if (!enabled) item.ForceTerminateAfterGrace = false;
        return item;
    }

    public Companion? ToggleForceConsent(string id)
    {
        var item = items.FirstOrDefault(companion => companion.Id == id);
        if (item is null || !item.StopWhenGameEnds) return null;
        item.ForceTerminateAfterGrace = !item.ForceTerminateAfterGrace;
        return item;
    }

    public List<Companion> Commit(IEnumerable<(Companion Companion, bool StopWhenGameEnds)> rows)
        => rows.Select(row =>
        {
            var draft = items.FirstOrDefault(item => item.Id == row.Companion.Id)
                ?? throw new InvalidOperationException("Companion draft row is no longer available.");
            var committed = Clone(draft);
            committed.StopWhenGameEnds = row.StopWhenGameEnds;
            if (!committed.StopWhenGameEnds) committed.ForceTerminateAfterGrace = false;
            return committed;
        }).ToList();

    private static Companion Clone(Companion companion)
        => new()
        {
            Id = companion.Id,
            Executable = companion.Executable,
            Arguments = companion.Arguments,
            StopWhenGameEnds = companion.StopWhenGameEnds,
            ForceTerminateAfterGrace = companion.ForceTerminateAfterGrace
        };
}

public sealed class RelatedApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public static class CatalogSelectionPolicy
{
    public static RelatedApp AddToRelatedApps(GameProfile profile, string name, string executable)
    {
        var app = new RelatedApp { Name = name.Trim(), Executable = Path.GetFullPath(executable) };
        profile.RelatedApps.Add(app);
        return app;
    }
}

public sealed class ProfileStore
{
    private readonly string path;
    public ProfileStore(string? path = null) => this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GridShift", "profiles.json");
    public List<GameProfile> Load()
    {
        try { return JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(path)) ?? []; }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException) { return []; }
    }
    public void Save(List<GameProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}

public static class AppCatalog
{
    public static IEnumerable<(string Name, string Path)> Discover()
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var apps = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (apps == null) continue;
                foreach (var subName in apps.GetSubKeyNames())
                {
                    using var app = apps.OpenSubKey(subName);
                    var name = app?.GetValue("DisplayName") as string;
                    var rawPath = app?.GetValue("DisplayIcon") as string ?? app?.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rawPath)) continue;
                    var path = GetExecutablePath(rawPath);
                    if (path is not null) results.TryAdd(path, name.Trim());
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
        return results.Select(x => (x.Value, x.Key)).OrderBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase);
    }

    private static string? GetExecutablePath(string value)
    {
        var text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1) text = text[1..end];
        }
        else if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(text))
        {
            return Path.GetFullPath(text);
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 0 && int.TryParse(text[(comma + 1)..], out _)) text = text[..comma];
        }
        return File.Exists(text) && text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(text) : null;
    }
}

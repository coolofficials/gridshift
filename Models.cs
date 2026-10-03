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
    public bool CleanupCreatedDesktop { get; set; }
    public int ExitDebounceSeconds { get; set; } = 3;
    public string DesktopId { get; set; } = "";
    public List<Companion> Companions { get; set; } = [];
    public List<RelatedApp> RelatedApps { get; set; } = [];
}

public sealed class Companion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool Autostart { get; set; } = true;
    public bool StopWhenGameEnds { get; set; } = true;
    public bool ForceTerminateAfterGrace { get; set; }
}

public sealed record CompanionCleanupConfiguration(string Id, string Executable, string Arguments, bool Autostart, bool StopWhenGameEnds, bool ForceTerminateAfterGrace)
{
    public static CompanionCleanupConfiguration Capture(Companion companion)
        => new(companion.Id, companion.Executable, companion.Arguments, companion.Autostart, companion.StopWhenGameEnds, companion.ForceTerminateAfterGrace);

    public bool Matches(Companion? current)
        => current is not null
            && string.Equals(current.Id, Id, StringComparison.Ordinal)
            && PathsEqual(current.Executable, Executable)
            && string.Equals(current.Arguments, Arguments, StringComparison.Ordinal)
            && current.Autostart == Autostart
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

    public Companion? SetAutostart(string id, bool enabled)
    {
        var item = items.FirstOrDefault(companion => companion.Id == id);
        if (item is null) return null;
        item.Autostart = enabled;
        return item;
    }

    public Companion? SetStopWhenGameEnds(string id, bool enabled)
    {
        var item = items.FirstOrDefault(companion => companion.Id == id);
        if (item is null) return null;
        item.StopWhenGameEnds = enabled;
        if (!enabled) item.ForceTerminateAfterGrace = false;
        return item;
    }

    public IReadOnlyList<Companion> EnableLegacyNormalStopWithConsent(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var changed = items.Where(item => selected.Contains(item.Id) && !item.StopWhenGameEnds).ToList();
        foreach (var item in changed) item.StopWhenGameEnds = true;
        return changed;
    }

    public Companion? ToggleForceConsent(string id)
    {
        var item = items.FirstOrDefault(companion => companion.Id == id);
        if (item is null || !item.StopWhenGameEnds) return null;
        item.ForceTerminateAfterGrace = !item.ForceTerminateAfterGrace;
        return item;
    }

    public List<Companion> Commit(IEnumerable<(Companion Companion, bool StopWhenGameEnds)> rows)
        => CommitFull(rows.Select(row => (row.Companion, row.Companion.Autostart, row.StopWhenGameEnds)));

    public List<Companion> CommitFull(IEnumerable<(Companion Companion, bool Autostart, bool StopWhenGameEnds)> rows)
        => rows.Select(row =>
        {
            var draft = items.FirstOrDefault(item => item.Id == row.Companion.Id)
                ?? throw new InvalidOperationException("Companion draft row is no longer available.");
            var committed = Clone(draft);
            committed.Autostart = row.Autostart;
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
            Autostart = companion.Autostart,
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
        try
        {
            var json = File.ReadAllText(path);
            var profiles = JsonSerializer.Deserialize<List<GameProfile>>(json) ?? [];
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var profileElements = document.RootElement.EnumerateArray().ToArray();
                for (var index = 0; index < Math.Min(profiles.Count, profileElements.Length); index++)
                {
                    if (!profileElements[index].TryGetProperty(nameof(GameProfile.Companions), out var companions)
                        || companions.ValueKind != JsonValueKind.Array) continue;
                    var companionElements = companions.EnumerateArray().ToArray();
                    for (var companionIndex = 0; companionIndex < Math.Min(profiles[index].Companions.Count, companionElements.Length); companionIndex++)
                    {
                        if (!companionElements[companionIndex].TryGetProperty(nameof(Companion.StopWhenGameEnds), out _))
                            profiles[index].Companions[companionIndex].StopWhenGameEnds = false;
                    }
                }
            }
            return profiles;
        }
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

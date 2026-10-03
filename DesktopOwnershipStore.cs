using System.Text.Json;

namespace GridShift;

public sealed class DesktopOwnershipStore
{
    private readonly string path;
    private readonly Dictionary<string, HashSet<Guid>> ownership;

    public DesktopOwnershipStore(string? path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GridShift", "desktop-ownership.json");
        ownership = Load(this.path);
    }

    public bool IsCreatedByLauncher(string profileId, Guid desktopId)
        => ownership.TryGetValue(profileId, out var ids) && ids.Contains(desktopId);

    public void MarkCreated(string profileId, Guid desktopId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || desktopId == Guid.Empty) throw new ArgumentException("A profile and desktop identity are required.");
        if (!ownership.TryGetValue(profileId, out var ids)) ownership[profileId] = ids = [];
        if (!ids.Add(desktopId)) return;
        try { Save(); }
        catch
        {
            ids.Remove(desktopId);
            if (ids.Count == 0) ownership.Remove(profileId);
            throw;
        }
    }

    public void Forget(string profileId, Guid desktopId)
    {
        if (!ownership.TryGetValue(profileId, out var ids) || !ids.Remove(desktopId)) return;
        if (ids.Count == 0) ownership.Remove(profileId);
        Save();
    }

    private static Dictionary<string, HashSet<Guid>> Load(string path)
    {
        try
        {
            var records = JsonSerializer.Deserialize<Dictionary<string, HashSet<Guid>>>(File.ReadAllText(path));
            return records is null ? new(StringComparer.Ordinal) : new(records, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(StringComparer.Ordinal); }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(ownership, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

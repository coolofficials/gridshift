namespace GridShift;

public sealed record TreeNodeSnapshot(string Key, bool Expanded);

public sealed record StableTreeSnapshot(IReadOnlySet<string> ExpandedKeys, string? SelectedKey)
{
    public static StableTreeSnapshot Capture(IEnumerable<TreeNodeSnapshot> nodes, string? selectedKey)
        => new(nodes.Where(node => node.Expanded).Select(node => node.Key).ToHashSet(StringComparer.Ordinal), selectedKey);

    public bool IsExpanded(string key) => ExpandedKeys.Contains(key);

    public string? ResolveSelection(IEnumerable<string> availableKeys, string? fallbackKey = null)
    {
        var available = availableKeys.ToHashSet(StringComparer.Ordinal);
        if (SelectedKey is not null && available.Contains(SelectedKey)) return SelectedKey;
        return fallbackKey is not null && available.Contains(fallbackKey) ? fallbackKey : null;
    }
}

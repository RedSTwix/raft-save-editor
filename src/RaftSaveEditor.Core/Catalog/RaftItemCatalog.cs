namespace RaftSaveEditor.Core.Catalog;

public sealed record RaftItemDefinition(
    short Id,
    string Name,
    int StackSize,
    int MaxUses,
    string Type,
    string? IconFile,
    bool CanAddToInventory)
{
    public bool HasKnownStackLimit => StackSize > 0;
    public bool IsStackable => StackSize > 1;
    public bool HasKnownUsesLimit => MaxUses > 0;
    public string SearchLabel => $"{Name} · ID {Id}";
    public string? IconPath => IconFile is null ? null : $"/Assets/Items/{IconFile}";
    public override string ToString() => SearchLabel;
}

public sealed class RaftItemCatalog
{
    private readonly IReadOnlyDictionary<short, RaftItemDefinition> _items;

    public RaftItemCatalog(IReadOnlyDictionary<short, string>? nameOverrides = null)
    {
        Dictionary<short, RaftItemDefinition> merged = new(BuiltIn);
        if (nameOverrides is not null)
        {
            foreach ((short id, string name) in nameOverrides)
            {
                merged[id] = merged.TryGetValue(id, out RaftItemDefinition? current)
                    ? current with { Name = name }
                    : Unknown(id) with { Name = name, Type = "Personalizado" };
            }
        }
        _items = merged;
        Items = merged.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id)
            .ToArray();
        AddableItems = Items.Where(item => item.CanAddToInventory).ToArray();
    }

    public IReadOnlyList<RaftItemDefinition> Items { get; }
    public IReadOnlyList<RaftItemDefinition> AddableItems { get; }

    public RaftItemDefinition Get(short itemId) => _items.TryGetValue(itemId, out RaftItemDefinition? item)
        ? item
        : Unknown(itemId);

    public string Resolve(short itemId) => Get(itemId).Name;

    public bool IsKnown(short itemId) => _items.ContainsKey(itemId);

    public static IReadOnlyDictionary<short, string> LoadVersionedFile(string path)
    {
        Dictionary<short, string> result = [];
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int separator = line.IndexOf('|');
            if (separator > 0 && short.TryParse(line.AsSpan(0, separator), out short id))
                result[id] = line[(separator + 1)..].Trim();
        }
        return result;
    }

    public static IReadOnlySet<short> BuildingMaterials { get; } = new HashSet<short>
    {
        20, 21, 22, 23, 24, 25, 50, 53, 65, 66, 67, 70, 125, 155, 177, 239, 243, 246
    };

    private static RaftItemDefinition Unknown(short id) =>
        new(id, $"Unknown Item (ID {id})", 0, 0, "Desconhecido", null, false);

    private static IReadOnlyDictionary<short, RaftItemDefinition> LoadBuiltIn()
    {
        string resource = typeof(RaftItemCatalog).Assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("items.metadata.txt", StringComparison.Ordinal));
        using Stream stream = typeof(RaftItemCatalog).Assembly.GetManifestResourceStream(resource)!;
        Dictionary<short, RaftItemDefinition> result = [];
        using StreamReader reader = new(stream);
        while (reader.ReadLine() is { } line)
            ParseMetadataLine(line, result);
        return result;
    }

    private static void ParseMetadataLine(string line, Dictionary<short, RaftItemDefinition> target)
    {
        if (line.Length == 0 || line[0] == '#') return;
        string[] fields = line.Split('|');
        if (fields.Length < 7
            || !short.TryParse(fields[0], out short id)
            || !int.TryParse(fields[2], out int stackSize)
            || !int.TryParse(fields[3], out int maxUses)
            || !bool.TryParse(fields[6], out bool canAddToInventory))
            return;

        target[id] = new RaftItemDefinition(
            id,
            fields[1].Trim(),
            stackSize,
            maxUses,
            fields[4].Trim(),
            string.IsNullOrWhiteSpace(fields[5]) ? null : fields[5].Trim(),
            canAddToInventory);
    }

    private static IReadOnlyDictionary<short, RaftItemDefinition> BuiltIn { get; } = LoadBuiltIn();
}

using System.IO;
using System.Text.Json;
using RaftSaveEditor.Core.Catalog;

namespace RaftSaveEditor.App.Services;

internal sealed record ItemAddRequest(short ItemId, int Amount);

internal sealed class ItemPreferencesService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RaftSaveEditor",
        "item-preferences.json");
    private PreferencesData _data;

    internal ItemPreferencesService() => _data = Load();

    internal IReadOnlySet<short> Favorites => _data.Favorites.ToHashSet();
    internal IReadOnlyList<short> Recent => _data.Recent;

    internal bool IsFavorite(short id) => _data.Favorites.Contains(id);

    internal void ToggleFavorite(short id)
    {
        if (!_data.Favorites.Remove(id)) _data.Favorites.Add(id);
        Save();
    }

    internal void RecordRecent(short id)
    {
        _data.Recent.Remove(id);
        _data.Recent.Insert(0, id);
        if (_data.Recent.Count > 20)
            _data.Recent.RemoveRange(20, _data.Recent.Count - 20);
        Save();
    }

    private PreferencesData Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<PreferencesData>(File.ReadAllText(_path)) ?? new PreferencesData()
                : new PreferencesData();
        }
        catch
        {
            return new PreferencesData();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }

    private sealed class PreferencesData
    {
        public List<short> Favorites { get; set; } = [];
        public List<short> Recent { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

internal sealed record ItemPreset(string Name, IReadOnlyList<short> ItemIds, bool IsBuiltIn)
{
    public string Summary => $"{ItemIds.Count} item(ns)";
    public override string ToString() => Name;
}

internal sealed class ItemPresetService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RaftSaveEditor",
        "presets.json");

    internal IReadOnlyList<ItemPreset> Load(RaftItemCatalog catalog)
    {
        List<ItemPreset> presets =
        [
            Build("Construção", catalog, item => item.Type is "Materials" or "Resources"),
            Build("Comida e água", catalog, item => item.Type is "Sustenance" or "Food & Water"),
            Build("Ferramentas", catalog, item => item.Type == "Tools"),
            Build("Combate", catalog, item => item.Type is "Weapons" or "Equipment")
        ];
        presets.AddRange(LoadCustom().Where(preset => preset.ItemIds.Count > 0));
        return presets;
    }

    internal ItemPreset SaveCustom(string name, IEnumerable<short> itemIds)
    {
        short[] ids = itemIds.Where(id => id > 0).Distinct().ToArray();
        ItemPreset preset = new(name.Trim(), ids, false);
        List<ItemPreset> custom = LoadCustom()
            .Where(item => !item.Name.Equals(preset.Name, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        custom.Add(preset);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(custom, JsonOptions));
        return preset;
    }

    private static ItemPreset Build(string name, RaftItemCatalog catalog, Func<RaftItemDefinition, bool> predicate) =>
        new(name, catalog.AddableItems.Where(predicate).Select(item => item.Id).ToArray(), true);

    private List<ItemPreset> LoadCustom()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<ItemPreset>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

using System.Collections.ObjectModel;
using RaftSaveEditor.App.Infrastructure;
using RaftSaveEditor.App.Services;
using RaftSaveEditor.Core.Catalog;

namespace RaftSaveEditor.App.ViewModels;

internal sealed class LiveContainerViewModel : ObservableObject
{
    private readonly ObservableCollection<LiveItemViewModel> _items = [];
    internal LiveContainerViewModel(LiveContainerSnapshot snapshot, RaftItemCatalog catalog)
    {
        Key = snapshot.Key;
        Name = snapshot.Name;
        IsPlayer = snapshot.IsPlayer;
        Capacity = snapshot.Capacity;
        ReplaceItems(snapshot.Items, catalog);
    }

    public string Key { get; }
    public string Name { get; }
    public bool IsPlayer { get; }
    public int Capacity { get; private set; }
    public ReadOnlyObservableCollection<LiveItemViewModel> Items => new(_items);
    public int OccupiedSlots => _items.Count;
    public int EmptySlots => Math.Max(0, Capacity - OccupiedSlots);
    public int TotalUnits => _items.Sum(item => item.Amount);
    public string OccupancyText => $"{OccupiedSlots} de {Capacity} slots";
    public int HotbarCapacity => IsPlayer ? Math.Min(10, Capacity) : 0;
    public int InternalCapacity => IsPlayer ? Math.Max(0, Math.Min(15, Capacity - 10)) : 0;
    public int BackpackCapacity => IsPlayer ? Math.Max(0, Capacity - 25) : 0;
    public int HotbarOccupied => IsPlayer ? _items.Count(item => item.SlotIndex < 10) : 0;
    public int InternalOccupied => IsPlayer ? _items.Count(item => item.SlotIndex is >= 10 and < 25) : 0;
    public int BackpackOccupied => IsPlayer ? _items.Count(item => item.SlotIndex >= 25) : 0;
    public string PlayerAreaSummary => !IsPlayer ? string.Empty :
        BackpackCapacity > 0
            ? $"Hotbar {HotbarOccupied}/{HotbarCapacity} · Interno {InternalOccupied}/{InternalCapacity} · Mochila {BackpackOccupied}/{BackpackCapacity}"
            : $"Hotbar {HotbarOccupied}/{HotbarCapacity} · Interno {InternalOccupied}/{InternalCapacity}";

    internal void Replace(LiveContainerSnapshot snapshot, RaftItemCatalog catalog)
    {
        Capacity = snapshot.Capacity;
        ReplaceItems(snapshot.Items, catalog);
        OnPropertyChanged(nameof(Capacity));
    }

    private void ReplaceItems(IReadOnlyList<LiveItemSnapshot> snapshots, RaftItemCatalog catalog)
    {
        _items.Clear();
        foreach (LiveItemSnapshot item in snapshots.OrderBy(item => item.SlotIndex))
            _items.Add(new LiveItemViewModel(this, item, catalog));
        OnPropertyChanged(nameof(OccupiedSlots));
        OnPropertyChanged(nameof(EmptySlots));
        OnPropertyChanged(nameof(TotalUnits));
        OnPropertyChanged(nameof(OccupancyText));
        OnPropertyChanged(nameof(HotbarOccupied));
        OnPropertyChanged(nameof(InternalOccupied));
        OnPropertyChanged(nameof(BackpackOccupied));
        OnPropertyChanged(nameof(PlayerAreaSummary));
    }
}

internal sealed class LiveItemViewModel
{
    internal LiveItemViewModel(LiveContainerViewModel container, LiveItemSnapshot snapshot, RaftItemCatalog catalog)
    {
        Container = container;
        Snapshot = snapshot;
        Definition = catalog.Get(checked((short)snapshot.ItemId));
    }

    internal LiveItemSnapshot Snapshot { get; }
    internal RaftItemDefinition Definition { get; }
    public LiveContainerViewModel Container { get; }
    public string Name => Definition.Name;
    public string Type => Definition.Type;
    public int ItemId => Snapshot.ItemId;
    public int Amount => Snapshot.Amount;
    public int Uses => Snapshot.Uses;
    public int MaxStack => Snapshot.MaxStack;
    public int MaxUses => Snapshot.MaxUses;
    public int SlotIndex => Snapshot.SlotIndex;
    public string? IconPath => Definition.IconFile is null ? null : $"/Assets/Items/{Definition.IconFile}";
    public string Area => !Container.IsPlayer ? "Storage" : SlotIndex switch
    {
        < 10 => "Hotbar",
        < 25 => "Inventário interno",
        _ => "Mochila"
    };
    public string DisplaySlot => !Container.IsPlayer ? (SlotIndex + 1).ToString() : SlotIndex switch
    {
        < 9 => $"Key {SlotIndex + 1}",
        9 => "Key 0",
        < 25 => (SlotIndex - 9).ToString(),
        _ => (SlotIndex - 24).ToString()
    };
}

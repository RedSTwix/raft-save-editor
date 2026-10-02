using System.Collections.ObjectModel;
using RaftSaveEditor.App.Infrastructure;
using RaftSaveEditor.Core.Catalog;
using RaftSaveEditor.Core.Models;

namespace RaftSaveEditor.App.ViewModels;

internal sealed class ItemRowViewModel(ItemSlot slot, RaftItemCatalog catalog) : ObservableObject
{
    private bool _isSelected;
    internal ItemSlot Slot { get; } = slot;
    public RaftItemDefinition Definition => catalog.Get(Slot.ItemId);
    public string Name => Definition.Name;
    public string Type => Definition.Type;
    public int MaxStackSize => Definition.StackSize;
    public int MaxUses => Definition.MaxUses;
    public string StackLimitText => Definition.HasKnownStackLimit ? Definition.StackSize.ToString() : "Não verificado";
    public string UsesLimitText => Definition.HasKnownUsesLimit ? Definition.MaxUses.ToString() : "Não verificado";
    public string? IconPath => Definition.IconFile is null ? null : $"/Assets/Items/{Definition.IconFile}";
    public short ItemId => Slot.ItemId;
    public short Amount => Slot.Amount;
    public short Uses => Slot.Uses;
    public string Location => Slot.Location;
    public string TechnicalOwner => Slot.TechnicalOwner;
    public short SlotIndex => Slot.SlotIndex;
    public bool IsPlayerInventory => TechnicalOwner.StartsWith("RGD_PlayerInventory", StringComparison.Ordinal);
    public string PlayerInventorySection => SlotIndex switch
    {
        < 10 when IsPlayerInventory => "Hotbar · teclas 1 a 0",
        < 25 when IsPlayerInventory => "Inventário interno",
        _ when IsPlayerInventory => "Mochila",
        _ => Location
    };
    public string DisplaySlot => SlotIndex switch
    {
        >= 0 and < 9 when IsPlayerInventory => $"Key {SlotIndex + 1}",
        9 when IsPlayerInventory => "Key 0",
        >= 10 and < 25 when IsPlayerInventory => (SlotIndex - 9).ToString(),
        >= 25 when IsPlayerInventory => (SlotIndex - 24).ToString(),
        _ => SlotIndex.ToString()
    };
    public bool IsStorage => Location.StartsWith("Storage", StringComparison.OrdinalIgnoreCase)
        || IsPlayerInventory;
    public bool IsKnown => catalog.IsKnown(Slot.ItemId);
    public bool IsModified => Slot.IsModified;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    internal RaftItemDefinition GetDefinition(short itemId) => catalog.Get(itemId);
    internal IReadOnlyList<RaftItemDefinition> KnownItems => catalog.AddableItems;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Amount));
        OnPropertyChanged(nameof(ItemId));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(MaxStackSize));
        OnPropertyChanged(nameof(MaxUses));
        OnPropertyChanged(nameof(StackLimitText));
        OnPropertyChanged(nameof(UsesLimitText));
        OnPropertyChanged(nameof(IconPath));
        OnPropertyChanged(nameof(IsKnown));
        OnPropertyChanged(nameof(Uses));
        OnPropertyChanged(nameof(SlotIndex));
        OnPropertyChanged(nameof(PlayerInventorySection));
        OnPropertyChanged(nameof(DisplaySlot));
        OnPropertyChanged(nameof(IsModified));
    }
}

internal sealed class TotalRowViewModel(short itemId, string name, int total, IReadOnlyList<ItemRowViewModel> occurrences)
{
    public short ItemId { get; } = itemId;
    public string Name { get; } = name;
    public int Total { get; } = total;
    public int OccurrenceCount { get; } = occurrences.Count;
    public string Locations { get; } = string.Join(", ", occurrences.GroupBy(item => item.Location)
        .Select(group => $"{group.Key}: {group.Sum(item => item.Amount)}"));
    internal IReadOnlyList<ItemRowViewModel> Occurrences { get; } = occurrences;
}

internal sealed class StorageViewModel : ObservableObject
{
    private readonly ObservableCollection<ItemRowViewModel> _items;
    private bool _isSelected;

    internal StorageViewModel(
        SaveDocument document,
        SaveStorage source,
        string name,
        int slotCount,
        string technicalOwner,
        IReadOnlyList<ItemRowViewModel> items)
    {
        Document = document;
        Source = source;
        Name = name;
        SlotCount = slotCount;
        TechnicalOwner = technicalOwner;
        _items = new ObservableCollection<ItemRowViewModel>(items);
        Items = new ReadOnlyObservableCollection<ItemRowViewModel>(_items);
    }

    internal SaveDocument Document { get; }
    internal SaveStorage Source { get; }
    public string Name { get; }
    public int SlotCount { get; }
    public int OccupiedSlots => Items.Count;
    public int EmptySlots => SlotCount - OccupiedSlots;
    public int TotalUnits => Items.Sum(item => item.Amount);
    public string OccupancyText => $"{OccupiedSlots} de {SlotCount} slots ocupados";
    public string PlayerAreaSummary
    {
        get
        {
            int hotbar = Items.Count(item => item.SlotIndex < 10);
            int inventory = Items.Count(item => item.SlotIndex is >= 10 and < 25);
            int backpack = Items.Count(item => item.SlotIndex >= 25);
            return backpack > 0
                ? $"Hotbar {hotbar} · Interno {inventory} · Mochila {backpack}"
                : $"Hotbar {hotbar} · Interno {inventory}";
        }
    }
    public string TechnicalOwner { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public ReadOnlyObservableCollection<ItemRowViewModel> Items { get; }

    internal void Add(ItemRowViewModel item)
    {
        if (!_items.Contains(item))
            _items.Add(item);
        Refresh();
    }

    internal void Remove(ItemRowViewModel item)
    {
        _items.Remove(item);
        Refresh();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(OccupiedSlots));
        OnPropertyChanged(nameof(EmptySlots));
        OnPropertyChanged(nameof(TotalUnits));
        OnPropertyChanged(nameof(OccupancyText));
        OnPropertyChanged(nameof(PlayerAreaSummary));
    }
}

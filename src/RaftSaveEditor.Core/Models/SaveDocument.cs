using System.Security.Cryptography;

namespace RaftSaveEditor.Core.Models;

public sealed class SaveDocument
{
    internal SaveDocument(
        string path,
        byte[] bytes,
        string rootType,
        IReadOnlyList<ItemSlot> allSlots,
        IReadOnlyList<SaveStorage> storages,
        IReadOnlyList<SaveStorage> playerStorages,
        int slotMetadataId,
        int slotLibraryId,
        int maxObjectId)
    {
        Path = path;
        OriginalBytes = bytes;
        OriginalHash = SHA256.HashData(bytes);
        RootType = rootType;
        _allSlots = [.. allSlots];
        Storages = storages;
        PlayerStorages = playerStorages;
        SlotMetadataId = slotMetadataId;
        SlotLibraryId = slotLibraryId;
        MaxObjectId = maxObjectId;
        RefreshFileInfo();
    }

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public long FileSize { get; private set; }
    public DateTime LastWriteTime { get; private set; }
    public string RootType { get; }
    private readonly List<ItemSlot> _allSlots;
    public IReadOnlyList<ItemSlot> AllSlots => _allSlots;
    public IReadOnlyList<SaveStorage> Storages { get; }
    public IReadOnlyList<SaveStorage> PlayerStorages { get; }
    public IReadOnlyList<ItemSlot> Items => _allSlots.Where(slot => slot.ItemId > 0).ToArray();
    public int PendingChanges => AllSlots.Count(item => item.IsModified);
    internal int SlotMetadataId { get; }
    internal int SlotLibraryId { get; }
    internal int MaxObjectId { get; }

    internal byte[] OriginalBytes { get; private set; }
    internal byte[] OriginalHash { get; private set; }

    public void SetAmount(ItemSlot slot, int amount)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!AllSlots.Contains(slot))
            throw new ArgumentException("O slot não pertence a este save.", nameof(slot));
        if (amount is < 0 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(amount), "ItemAmount é Int16 e deve estar entre 0 e 32767.");

        slot.Amount = checked((short)amount);
    }

    public void SetSlotValues(ItemSlot slot, int itemId, int amount, int uses, int slotIndex)
    {
        SetAmount(slot, amount);
        if (itemId is < 0 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(itemId), "ItemIndex deve estar entre 0 e 32767.");
        if (uses is < 0 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(uses), "ItemUses deve estar entre 0 e 32767.");
        if (slotIndex is < 0 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(slotIndex), "SlotIndex deve estar entre 0 e 32767.");

        slot.ItemId = checked((short)itemId);
        slot.Uses = checked((short)uses);
        slot.SlotIndex = checked((short)slotIndex);
    }

    public ItemSlot AddSlot(SaveStorage storage, int itemId, int amount, int uses, int slotIndex)
    {
        if (!Storages.Contains(storage) && !PlayerStorages.Contains(storage))
            throw new ArgumentException("O storage não pertence a este save.", nameof(storage));
        if (itemId is <= 0 or > short.MaxValue || amount is <= 0 or > short.MaxValue || uses is < 0 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(itemId), "Os valores do novo item estão fora dos limites Int16.");

        ItemSlot slot = new(
            recordId: -(_allSlots.Count + 1),
            slotIndex: checked((short)slotIndex),
            itemId: 0,
            amount: 0,
            uses: 0,
            slotIndexOffset: -1,
            itemIdOffset: -1,
            amountOffset: -1,
            usesOffset: -1,
            location: storage.Name,
            technicalOwner: storage.TechnicalOwner,
            isSynthetic: true);
        _allSlots.Add(slot);
        SetSlotValues(slot, itemId, amount, uses, slotIndex);
        return slot;
    }

    public void DiscardChanges()
    {
        foreach (ItemSlot slot in AllSlots)
        {
            slot.Amount = slot.OriginalAmount;
            slot.ItemId = slot.OriginalItemId;
            slot.Uses = slot.OriginalUses;
            slot.SlotIndex = slot.OriginalSlotIndex;
        }
    }

    internal void AcceptSavedBytes(byte[] bytes)
    {
        OriginalBytes = bytes;
        OriginalHash = SHA256.HashData(bytes);
        foreach (ItemSlot slot in AllSlots)
        {
            slot.OriginalAmount = slot.Amount;
            slot.OriginalItemId = slot.ItemId;
            slot.OriginalUses = slot.Uses;
            slot.OriginalSlotIndex = slot.SlotIndex;
        }
        RefreshFileInfo();
    }

    private void RefreshFileInfo()
    {
        FileInfo info = new(Path);
        FileSize = info.Length;
        LastWriteTime = info.LastWriteTime;
    }
}

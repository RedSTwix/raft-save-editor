namespace RaftSaveEditor.Core.Models;

public sealed class ItemSlot
{
    internal ItemSlot(
        int recordId,
        short slotIndex,
        short itemId,
        short amount,
        short uses,
        long slotIndexOffset,
        long itemIdOffset,
        long amountOffset,
        long usesOffset,
        string location,
        string technicalOwner,
        bool isSynthetic = false)
    {
        RecordId = recordId;
        OriginalSlotIndex = slotIndex;
        SlotIndex = slotIndex;
        OriginalItemId = itemId;
        ItemId = itemId;
        OriginalAmount = amount;
        Amount = amount;
        OriginalUses = uses;
        Uses = uses;
        SlotIndexOffset = slotIndexOffset;
        ItemIdOffset = itemIdOffset;
        AmountOffset = amountOffset;
        UsesOffset = usesOffset;
        Location = location;
        TechnicalOwner = technicalOwner;
        IsSynthetic = isSynthetic;
    }

    public int RecordId { get; }
    public short OriginalSlotIndex { get; internal set; }
    public short SlotIndex { get; internal set; }
    public short OriginalItemId { get; internal set; }
    public short ItemId { get; internal set; }
    public short OriginalAmount { get; internal set; }
    public short Amount { get; internal set; }
    public short OriginalUses { get; internal set; }
    public short Uses { get; internal set; }
    public string Location { get; }
    public string TechnicalOwner { get; }
    public bool IsModified => ItemId != OriginalItemId || Amount != OriginalAmount || Uses != OriginalUses || SlotIndex != OriginalSlotIndex;
    public bool IsSynthetic { get; }
    internal long SlotIndexOffset { get; }
    internal long ItemIdOffset { get; }
    internal long AmountOffset { get; }
    internal long UsesOffset { get; }
}

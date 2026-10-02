namespace RaftSaveEditor.Core.Models;

public sealed record SaveStorage(
    string Name,
    string TechnicalOwner,
    int SlotCapacity,
    int StorageItemId,
    int SerializedSlotCount,
    int SlotMetadataId,
    long SlotArrayLengthOffset,
    long SlotArrayEndOffset);

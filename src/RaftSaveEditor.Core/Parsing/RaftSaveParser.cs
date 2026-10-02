using System.Formats.Nrbf;
using System.Text;
using RaftSaveEditor.Core.Models;

namespace RaftSaveEditor.Core.Parsing;

public sealed class RaftSaveParser
{
    public const long MaximumFileSize = 512L * 1024 * 1024;

    public SaveDocument Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        FileInfo info = new(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("Save não encontrado.", fullPath);
        if (info.Length <= 0 || info.Length > MaximumFileSize)
            throw new InvalidDataException("O arquivo está vazio ou excede o limite seguro de 512 MB.");

        byte[] bytes = File.ReadAllBytes(fullPath);
        ValidationResult validation = Validate(bytes);
        IReadOnlyList<ItemSlot> slots = validation.Trace.Slots.Select(raw => new ItemSlot(
            raw.RecordId,
            raw.SlotIndex,
            raw.ItemId,
            raw.Amount,
            raw.Uses,
            raw.SlotIndexOffset,
            raw.ItemIdOffset,
            raw.AmountOffset,
            raw.UsesOffset,
            raw.Location,
            raw.TechnicalOwner)).ToArray();

        IReadOnlyList<SaveStorage> storages = validation.Trace.Owners
            .Where(owner => owner.Kind == "RGD_Storage")
            .Select(owner => new SaveStorage(
                owner.Label,
                owner.Technical,
                owner.SlotCapacity,
                owner.StorageItemId,
                owner.SerializedSlotCount,
                owner.SlotMetadataId,
                owner.SlotArrayLengthOffset,
                owner.SlotArrayEndOffset))
            .ToArray();
        IReadOnlyList<SaveStorage> playerStorages = validation.Trace.Owners
            .Where(owner => owner.Kind == "RGD_PlayerInventory")
            .Select(owner => new SaveStorage(
                owner.Label,
                owner.Technical,
                owner.SlotCapacity,
                owner.StorageItemId,
                owner.SerializedSlotCount,
                owner.SlotMetadataId,
                owner.SlotArrayLengthOffset,
                owner.SlotArrayEndOffset))
            .ToArray();

        return new SaveDocument(
            fullPath,
            bytes,
            validation.RootType,
            slots,
            storages,
            playerStorages,
            validation.Trace.SlotMetadataId,
            validation.Trace.SlotLibraryId,
            validation.Trace.MaxObjectId);
    }

    internal static ValidationResult Validate(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        SerializationRecord root;
        IReadOnlyDictionary<SerializationRecordId, SerializationRecord> map;
        try
        {
            root = NrbfDecoder.Decode(stream, out map);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Runtime.Serialization.SerializationException
            or NotSupportedException or DecoderFallbackException or EndOfStreamException)
        {
            throw new InvalidDataException("O arquivo não é um payload NRBF compatível.", ex);
        }

        NrbfTrace trace = NrbfOffsetParser.Parse(bytes);
        List<(short Slot, short Item, short Amount, short Uses)> official = map.Values
            .OfType<ClassRecord>()
            .Where(record => record.TypeName.FullName == "RGD_Slot")
            .Select(record => (
                record.GetInt16("SlotIndex"),
                record.GetInt16("ItemIndex"),
                record.GetInt16("ItemAmount"),
                record.GetInt16("ItemUses")))
            .OrderBy(value => value)
            .ToList();

        List<(short Slot, short Item, short Amount, short Uses)> traced = trace.Slots
            .Select(slot => (slot.SlotIndex, slot.ItemId, slot.Amount, slot.Uses))
            .OrderBy(value => value)
            .ToList();

        if (!official.SequenceEqual(traced))
            throw new InvalidDataException("A validação cruzada dos RGD_Slot falhou; nenhuma edição será permitida.");

        return new ValidationResult(root.TypeName.FullName, trace);
    }
}

internal sealed record ValidationResult(string RootType, NrbfTrace Trace);

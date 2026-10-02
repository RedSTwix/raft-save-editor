using System.Buffers.Binary;
using System.Security.Cryptography;
using RaftSaveEditor.Core.Models;
using RaftSaveEditor.Core.Parsing;

namespace RaftSaveEditor.Core.Writing;

public sealed class RaftSaveWriter
{
    public void Save(SaveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ItemSlot[] changed = document.AllSlots.Where(slot => slot.IsModified && !slot.IsSynthetic).ToArray();
        ItemSlot[] additions = document.AllSlots.Where(slot => slot.IsSynthetic && slot.ItemId > 0).ToArray();
        if (changed.Length == 0 && additions.Length == 0)
            throw new InvalidOperationException("Não há alterações pendentes.");

        byte[] current = File.ReadAllBytes(document.Path);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), document.OriginalHash))
            throw new IOException("O save foi alterado por outro programa depois de ser aberto. Recarregue antes de salvar.");

        byte[] patched = (byte[])document.OriginalBytes.Clone();
        HashSet<int> allowedDiffs = [];
        foreach (ItemSlot slot in changed)
        {
            PatchInt16(slot.SlotIndexOffset, slot.OriginalSlotIndex, slot.SlotIndex, "SlotIndex", slot.RecordId);
            PatchInt16(slot.ItemIdOffset, slot.OriginalItemId, slot.ItemId, "ItemIndex", slot.RecordId);
            PatchInt16(slot.AmountOffset, slot.OriginalAmount, slot.Amount, "ItemAmount", slot.RecordId);
            PatchInt16(slot.UsesOffset, slot.OriginalUses, slot.Uses, "ItemUses", slot.RecordId);
        }

        void PatchInt16(long rawOffset, short original, short value, string field, int recordId)
        {
            if (original == value) return;
            int offset = checked((int)rawOffset);
            short existing = BinaryPrimitives.ReadInt16LittleEndian(patched.AsSpan(offset, sizeof(short)));
            if (existing != original)
                throw new InvalidDataException($"O valor original de {field} no slot #{recordId} não corresponde ao offset validado.");

            BinaryPrimitives.WriteInt16LittleEndian(patched.AsSpan(offset, sizeof(short)), value);
            allowedDiffs.Add(offset);
            allowedDiffs.Add(offset + 1);
        }

        for (int i = 0; i < patched.Length; i++)
        {
            if (patched[i] != document.OriginalBytes[i] && !allowedDiffs.Contains(i))
                throw new InvalidDataException("A verificação byte a byte detectou uma alteração fora de ItemAmount.");
        }

        int nextObjectId = document.MaxObjectId;
        var additionGroups = additions
            .GroupBy(slot => slot.TechnicalOwner)
            .Select(group => new
            {
                Storage = document.Storages.Concat(document.PlayerStorages)
                    .Single(storage => storage.TechnicalOwner == group.Key),
                Slots = group.OrderBy(slot => slot.SlotIndex).ToArray()
            })
            .OrderByDescending(group => group.Storage.SlotArrayEndOffset)
            .ToArray();

        foreach (var group in additionGroups)
        {
            SaveStorage storage = group.Storage;
            if (storage.SlotArrayLengthOffset < 0 || storage.SlotArrayEndOffset < 0)
                throw new InvalidDataException($"O array de slots de {storage.Name} não foi localizado.");

            int lengthOffset = checked((int)storage.SlotArrayLengthOffset);
            int insertOffset = checked((int)storage.SlotArrayEndOffset);
            int currentLength = BinaryPrimitives.ReadInt32LittleEndian(patched.AsSpan(lengthOffset, sizeof(int)));
            if (currentLength != storage.SerializedSlotCount)
                throw new InvalidDataException($"A quantidade serializada de slots de {storage.Name} foi alterada.");
            BinaryPrimitives.WriteInt32LittleEndian(
                patched.AsSpan(lengthOffset, sizeof(int)),
                checked(currentLength + group.Slots.Length));

            using MemoryStream records = new();
            using (BinaryWriter writer = new(records, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                foreach (ItemSlot slot in group.Slots)
                {
                    writer.Write((byte)5); // ClassWithMembersAndTypes
                    writer.Write(++nextObjectId);
                    writer.Write("RGD_Slot");
                    writer.Write(5);
                    writer.Write("SlotIndex");
                    writer.Write("ItemIndex");
                    writer.Write("ItemAmount");
                    writer.Write("ItemUses");
                    writer.Write("ExclusiveString");
                    writer.Write((byte)0); // Primitive
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((byte)1); // String
                    writer.Write((byte)7); // Int16
                    writer.Write((byte)7);
                    writer.Write((byte)7);
                    writer.Write((byte)7);
                    writer.Write(document.SlotLibraryId);
                    writer.Write(slot.SlotIndex);
                    writer.Write(slot.ItemId);
                    writer.Write(slot.Amount);
                    writer.Write(slot.Uses);
                    writer.Write((byte)10); // ObjectNull for ExclusiveString
                }
            }

            byte[] inserted = records.ToArray();
            byte[] expanded = new byte[patched.Length + inserted.Length];
            Buffer.BlockCopy(patched, 0, expanded, 0, insertOffset);
            Buffer.BlockCopy(inserted, 0, expanded, insertOffset, inserted.Length);
            Buffer.BlockCopy(patched, insertOffset, expanded, insertOffset + inserted.Length, patched.Length - insertOffset);
            patched = expanded;
        }

        ValidationResult validation = RaftSaveParser.Validate(patched);
        Dictionary<int, RawSlot> validated = validation.Trace.Slots.ToDictionary(slot => slot.RecordId);
        foreach (ItemSlot slot in changed)
        {
            if (!validated.TryGetValue(slot.RecordId, out RawSlot? parsed)
                || parsed.Amount != slot.Amount
                || parsed.ItemId != slot.ItemId
                || parsed.Uses != slot.Uses
                || parsed.SlotIndex != slot.SlotIndex)
                throw new InvalidDataException($"A releitura do slot #{slot.RecordId} não confirmou os novos valores.");
        }
        foreach (ItemSlot slot in additions)
        {
            if (!validation.Trace.Slots.Any(parsed =>
                    parsed.TechnicalOwner == slot.TechnicalOwner
                    && parsed.SlotIndex == slot.SlotIndex
                    && parsed.ItemId == slot.ItemId
                    && parsed.Amount == slot.Amount
                    && parsed.Uses == slot.Uses))
                throw new InvalidDataException($"A releitura não confirmou o item adicionado ao slot {slot.SlotIndex}.");
        }

        string directory = System.IO.Path.GetDirectoryName(document.Path)!;
        string temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(document.Path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(temporary, patched);
            using (FileStream verify = new(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                verify.Flush(flushToDisk: true);
            File.Replace(temporary, document.Path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        document.AcceptSavedBytes(patched);
    }
}

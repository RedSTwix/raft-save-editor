using System.Collections.ObjectModel;
using System.Text;

namespace RaftSaveEditor.Core.Parsing;

/// <summary>
/// Walks NRBF record framing and records the exact byte position of primitive members.
/// It never creates payload-named CLR types and never executes constructors or callbacks.
/// </summary>
internal sealed class NrbfOffsetParser
{
    private readonly BinaryReader _reader;
    private readonly Dictionary<int, ClassMetadata> _metadata = [];
    private readonly Dictionary<int, OwnerContext> _objectContexts = [];
    private readonly Dictionary<int, OwnerContext> _pendingOwnerContexts = [];
    private readonly List<OwnerContext> _owners = [];
    private readonly List<RawSlot> _slots = [];
    private readonly Stack<ValueFrame> _frames = [];
    private readonly Dictionary<string, int> _ownerSequence = new(StringComparer.Ordinal);
    private int _maxObjectId;
    private bool _messageEnded;

    private NrbfOffsetParser(byte[] bytes)
    {
        _reader = new BinaryReader(new MemoryStream(bytes, writable: false), new UTF8Encoding(false, true));
    }

    internal static NrbfTrace Parse(byte[] bytes)
    {
        NrbfOffsetParser parser = new(bytes);
        parser.ParseCore();
        ClassMetadata slotMetadata = parser._metadata.Values.First(metadata => metadata.TypeName == "RGD_Slot");
        return new NrbfTrace(
            parser._slots.AsReadOnly(),
            parser._owners.AsReadOnly(),
            slotMetadata.MetadataId,
            slotMetadata.LibraryId,
            parser._maxObjectId);
    }

    private void ParseCore()
    {
        if ((RecordType)_reader.ReadByte() != RecordType.SerializedStreamHeader)
            throw Invalid("Cabeçalho NRBF ausente.");

        _reader.ReadInt32(); // root id
        _reader.ReadInt32(); // header id
        if (_reader.ReadInt32() != 1 || _reader.ReadInt32() != 0)
            throw Invalid("Versão NRBF não suportada.");

        while (!_messageEnded)
        {
            DrainCompletedFrames();

            if (_frames.Count == 0)
            {
                ParsedRecord record = ReadRecord(null);
                if (record.Frame is not null)
                    _frames.Push(record.Frame);
                continue;
            }

            ValueFrame parent = _frames.Peek();
            ExpectedValue expected = parent.Current;
            if (expected.BinaryType == BinaryType.Primitive)
            {
                long offset = Position;
                object value = ReadPrimitive(expected.PrimitiveType);
                parent.AcceptPrimitive(expected, value, offset);
                continue;
            }

            ParsedRecord child = ReadRecord(new ParentInfo(parent, expected));
            if (child.IsLibrary)
                continue;

            parent.AcceptRecordCount(child.ValuesConsumed);
            if (child.Frame is not null)
                _frames.Push(child.Frame);
        }

        DrainCompletedFrames();
        if (_frames.Count != 0 || Position != _reader.BaseStream.Length)
            throw Invalid("Payload NRBF terminou com records incompletos ou bytes extras.");
    }

    private void DrainCompletedFrames()
    {
        while (_frames.TryPeek(out ValueFrame? frame) && frame.IsComplete)
        {
            _frames.Pop();
            if (frame is ClassFrame classFrame)
            {
                classFrame.CompleteOwner();
                RawSlot? slot = classFrame.CompleteSlot();
                if (slot is not null)
                    _slots.Add(slot);
            }
            else if (frame is ArrayFrame arrayFrame)
                arrayFrame.Complete(Position);
        }
    }

    private ParsedRecord ReadRecord(ParentInfo? parent)
    {
        RecordType type = (RecordType)_reader.ReadByte();
        return type switch
        {
            RecordType.SerializedStreamHeader => throw Invalid("Cabeçalho NRBF duplicado."),
            RecordType.ClassWithId => ReadClassWithId(parent),
            RecordType.SystemClassWithMembersAndTypes => ReadClassWithMembersAndTypes(parent, hasLibrary: false),
            RecordType.ClassWithMembersAndTypes => ReadClassWithMembersAndTypes(parent, hasLibrary: true),
            RecordType.BinaryObjectString => ReadObjectString(),
            RecordType.BinaryArray => ReadBinaryArray(parent),
            RecordType.MemberPrimitiveTyped => ReadMemberPrimitive(),
            RecordType.MemberReference => ReadReference(parent),
            RecordType.ObjectNull => ParsedRecord.Value,
            RecordType.MessageEnd => EndMessage(),
            RecordType.BinaryLibrary => ReadLibrary(),
            RecordType.ObjectNullMultiple256 => ParsedRecord.Multiple(_reader.ReadByte()),
            RecordType.ObjectNullMultiple => ParsedRecord.Multiple(ReadPositiveCount()),
            RecordType.ArraySinglePrimitive => ReadPrimitiveArray(parent),
            RecordType.ArraySingleObject => ReadSimpleArray(parent, BinaryType.Object),
            RecordType.ArraySingleString => ReadSimpleArray(parent, BinaryType.String),
            RecordType.SystemClassWithMembers or RecordType.ClassWithMembers =>
                throw new NotSupportedException("Records NRBF sem informações de tipo não são suportados."),
            _ => throw Invalid($"Record NRBF desconhecido: {(byte)type}.")
        };
    }

    private ParsedRecord ReadClassWithId(ParentInfo? parent)
    {
        int objectId = _reader.ReadInt32();
        ObserveObjectId(objectId);
        int metadataId = _reader.ReadInt32();
        if (!_metadata.TryGetValue(metadataId, out ClassMetadata? metadata))
            throw Invalid($"Metadata NRBF #{metadataId} não encontrada.");

        return ParsedRecord.Container(CreateClassFrame(objectId, metadata, parent));
    }

    private ParsedRecord ReadClassWithMembersAndTypes(ParentInfo? parent, bool hasLibrary)
    {
        int objectId = _reader.ReadInt32();
        ObserveObjectId(objectId);
        string typeName = ReadString();
        int memberCount = ReadBoundedCount("membros");
        string[] names = new string[memberCount];
        for (int i = 0; i < names.Length; i++)
            names[i] = ReadString();

        BinaryType[] binaryTypes = new BinaryType[memberCount];
        for (int i = 0; i < binaryTypes.Length; i++)
        {
            binaryTypes[i] = (BinaryType)_reader.ReadByte();
            if (binaryTypes[i] is < BinaryType.Primitive or > BinaryType.PrimitiveArray)
                throw Invalid("BinaryType NRBF inválido.");
        }

        ExpectedValue[] members = new ExpectedValue[memberCount];
        for (int i = 0; i < members.Length; i++)
            members[i] = ReadExpected(names[i], binaryTypes[i]);

        int libraryId = hasLibrary ? _reader.ReadInt32() : 0;

        ClassMetadata metadata = new(objectId, typeName, members, libraryId);
        _metadata.Add(objectId, metadata);
        return ParsedRecord.Container(CreateClassFrame(objectId, metadata, parent));
    }

    private ClassFrame CreateClassFrame(int objectId, ClassMetadata metadata, ParentInfo? parent)
    {
        OwnerContext inherited = parent?.Parent.Context ?? OwnerContext.CreateUnknown();
        OwnerContext context = metadata.TypeName switch
        {
            "RGD_Storage" => NewOwner("Storage", metadata.TypeName, objectId),
            "RGD_PlayerInventory" => NewOwner("Player Storage", metadata.TypeName, objectId),
            _ => inherited
        };

        RegisterObjectContext(objectId, context);
        return new ClassFrame(objectId, metadata, context);
    }

    private OwnerContext NewOwner(string label, string typeName, int objectId)
    {
        int sequence = _ownerSequence.TryGetValue(typeName, out int value) ? value + 1 : 1;
        _ownerSequence[typeName] = sequence;
        OwnerContext owner = new($"{label} #{sequence}", $"{typeName} (NRBF object #{objectId})", typeName);
        // RGD_PlayerInventory only serializes occupied slots, not its UI capacity.
        // Raft's base inventory is 10 hotbar + 15 inventory slots. Existing higher
        // slot indexes are preserved separately when the view model calculates capacity.
        if (typeName == "RGD_PlayerInventory")
            owner.SetSlotCapacity(25);
        _owners.Add(owner);
        return owner;
    }

    private ParsedRecord ReadBinaryArray(ParentInfo? parent)
    {
        int objectId = _reader.ReadInt32();
        ObserveObjectId(objectId);
        byte arrayType = _reader.ReadByte();
        if (arrayType > 2)
            throw new NotSupportedException("Arrays NRBF com offset diferente de zero não são suportados.");

        int rank = ReadBoundedCount("rank", max: 32);
        long length = 1;
        long lengthOffset = rank == 1 ? Position : -1;
        for (int i = 0; i < rank; i++)
            length = checked(length * ReadBoundedCount("comprimento do array", max: 20_000_000));

        BinaryType binaryType = (BinaryType)_reader.ReadByte();
        ExpectedValue expected = ReadExpected("[]", binaryType);
        OwnerContext context = ResolveArrayContext(parent, objectId);
        bool isSlotArray = lengthOffset >= 0 && IsSlotArray(parent, context);
        RegisterObjectContext(objectId, context);
        return ParsedRecord.Container(new ArrayFrame(checked((int)length), expected, context, lengthOffset, isSlotArray));
    }

    private ParsedRecord ReadSimpleArray(ParentInfo? parent, BinaryType elementType)
    {
        int objectId = _reader.ReadInt32();
        ObserveObjectId(objectId);
        long lengthOffset = Position;
        int length = ReadBoundedCount("comprimento do array", max: 20_000_000);
        OwnerContext context = ResolveArrayContext(parent, objectId);
        bool isSlotArray = IsSlotArray(parent, context);
        RegisterObjectContext(objectId, context);
        return ParsedRecord.Container(new ArrayFrame(length, new ExpectedValue("[]", elementType), context, lengthOffset, isSlotArray));
    }

    private ParsedRecord ReadPrimitiveArray(ParentInfo? parent)
    {
        ObserveObjectId(_reader.ReadInt32());
        int length = ReadBoundedCount("comprimento do array", max: 20_000_000);
        PrimitiveType primitive = ReadPrimitiveType();
        for (int i = 0; i < length; i++)
            ReadPrimitive(primitive);
        return ParsedRecord.Value;
    }

    private OwnerContext ResolveArrayContext(ParentInfo? parent, int objectId)
    {
        if (parent?.Parent is ClassFrame owner && IsSlotsMember(parent.Expected.Name))
            return owner.Context;
        // Keep every array's structural offsets isolated. If it is referenced later
        // by RGD_Storage.Slots, AssignFrom links this context (and its slot records)
        // to the real owner without mixing it with sibling arrays.
        return new OwnerContext("Unknown Container", $"Array NRBF object #{objectId}", "Array");
    }

    private static bool IsSlotArray(ParentInfo? parent, OwnerContext context) =>
        IsSlotsMember(parent?.Expected.Name)
        || (context.IsResolved && parent?.Expected.Name == "_items");

    private static bool IsSlotsMember(string? name) =>
        name?.Equals("slots", StringComparison.OrdinalIgnoreCase) == true;

    private ParsedRecord ReadObjectString()
    {
        ObserveObjectId(_reader.ReadInt32());
        ReadString();
        return ParsedRecord.Value;
    }

    private ParsedRecord ReadMemberPrimitive()
    {
        ReadPrimitive(ReadPrimitiveType());
        return ParsedRecord.Value;
    }

    private ParsedRecord ReadReference(ParentInfo? parent)
    {
        int referencedId = _reader.ReadInt32();
        OwnerContext? ownerContext = parent?.Parent is ClassFrame owner && IsSlotsMember(parent.Expected.Name)
            ? owner.Context
            : parent?.Parent is ArrayFrame && parent.Parent.Context.IsResolved
                ? parent.Parent.Context
                : null;

        if (ownerContext is not null)
        {
            if (_objectContexts.TryGetValue(referencedId, out OwnerContext? referencedContext))
            {
                ownerContext.SetSlotCapacity(referencedContext.SlotCapacity);
                if (referencedContext.SlotArrayLengthOffset >= 0)
                    ownerContext.SetSlotArray(
                        referencedContext.SerializedSlotCount,
                        referencedContext.SlotArrayLengthOffset,
                        referencedContext.SlotArrayEndOffset);
                if (referencedContext.SlotMetadataId > 0)
                    ownerContext.SetSlotMetadataId(referencedContext.SlotMetadataId);
                referencedContext.AssignFrom(ownerContext);
            }
            else
                _pendingOwnerContexts[referencedId] = ownerContext;
        }

        return ParsedRecord.Value;
    }

    private void RegisterObjectContext(int objectId, OwnerContext context)
    {
        _objectContexts[objectId] = context;
        if (_pendingOwnerContexts.Remove(objectId, out OwnerContext? owner))
        {
            owner.SetSlotCapacity(context.SlotCapacity);
            if (context.SlotArrayLengthOffset >= 0)
                owner.SetSlotArray(context.SerializedSlotCount, context.SlotArrayLengthOffset, context.SlotArrayEndOffset);
            if (context.SlotMetadataId > 0)
                owner.SetSlotMetadataId(context.SlotMetadataId);
            context.AssignFrom(owner);
        }
    }

    private ParsedRecord ReadLibrary()
    {
        _reader.ReadInt32();
        ReadString();
        return ParsedRecord.Library;
    }

    private ParsedRecord EndMessage()
    {
        if (_frames.Count != 0)
            throw Invalid("MessageEnd encontrado dentro de um objeto NRBF.");
        _messageEnded = true;
        return ParsedRecord.MessageEnd;
    }

    private ExpectedValue ReadExpected(string name, BinaryType binaryType)
    {
        return binaryType switch
        {
            BinaryType.Primitive or BinaryType.PrimitiveArray => new ExpectedValue(name, binaryType, ReadPrimitiveType()),
            BinaryType.SystemClass => new ExpectedValue(name, binaryType, TypeName: ReadString()),
            BinaryType.Class => ReadClassExpected(name, binaryType),
            BinaryType.String or BinaryType.Object or BinaryType.ObjectArray or BinaryType.StringArray => new ExpectedValue(name, binaryType),
            _ => throw Invalid("BinaryType NRBF inválido.")
        };
    }

    private ExpectedValue ReadClassExpected(string name, BinaryType type)
    {
        string className = ReadString();
        _reader.ReadInt32(); // library id
        return new ExpectedValue(name, type, TypeName: className);
    }

    private PrimitiveType ReadPrimitiveType()
    {
        PrimitiveType value = (PrimitiveType)_reader.ReadByte();
        if (value is PrimitiveType.Unused or < PrimitiveType.Boolean or > PrimitiveType.UInt64)
            throw Invalid("PrimitiveType NRBF inválido.");
        return value;
    }

    private object ReadPrimitive(PrimitiveType type) => type switch
    {
        PrimitiveType.Boolean => _reader.ReadBoolean(),
        PrimitiveType.Byte => _reader.ReadByte(),
        PrimitiveType.Char => _reader.ReadChar(),
        PrimitiveType.Decimal => decimal.Parse(ReadString(), System.Globalization.CultureInfo.InvariantCulture),
        PrimitiveType.Double => _reader.ReadDouble(),
        PrimitiveType.Int16 => _reader.ReadInt16(),
        PrimitiveType.Int32 => _reader.ReadInt32(),
        PrimitiveType.Int64 => _reader.ReadInt64(),
        PrimitiveType.SByte => _reader.ReadSByte(),
        PrimitiveType.Single => _reader.ReadSingle(),
        PrimitiveType.TimeSpan => _reader.ReadInt64(),
        PrimitiveType.DateTime => _reader.ReadUInt64(),
        PrimitiveType.UInt16 => _reader.ReadUInt16(),
        PrimitiveType.UInt32 => _reader.ReadUInt32(),
        PrimitiveType.UInt64 => _reader.ReadUInt64(),
        _ => throw Invalid("PrimitiveType NRBF não suportado.")
    };

    private string ReadString() => _reader.ReadString();

    private int ReadPositiveCount() => ReadBoundedCount("contagem");

    private int ReadBoundedCount(string label, int max = 1_000_000)
    {
        int value = _reader.ReadInt32();
        if (value < 0 || value > max)
            throw Invalid($"{label} fora do limite seguro: {value}.");
        return value;
    }

    private long Position => _reader.BaseStream.Position;
    private void ObserveObjectId(int objectId) => _maxObjectId = Math.Max(_maxObjectId, objectId);
    private static InvalidDataException Invalid(string message) => new(message);

    private sealed record ClassMetadata(int MetadataId, string TypeName, ExpectedValue[] Members, int LibraryId);
    private sealed record ExpectedValue(string Name, BinaryType BinaryType, PrimitiveType PrimitiveType = default, string? TypeName = null);
    private sealed record ParentInfo(ValueFrame Parent, ExpectedValue Expected);
    private abstract class ValueFrame
    {
        protected int Index;
        internal abstract int Count { get; }
        internal abstract ExpectedValue Current { get; }
        internal OwnerContext Context { get; private protected set; } = OwnerContext.CreateUnknown();
        internal bool IsComplete => Index >= Count;
        internal virtual void AcceptPrimitive(ExpectedValue expected, object value, long offset) => Index++;
        internal void AcceptRecordCount(int count)
        {
            if (count <= 0 || Index + count > Count)
                throw Invalid("Contagem de valores NRBF excede o contêiner pai.");
            Index += count;
        }
    }

    private sealed class ArrayFrame : ValueFrame
    {
        private readonly ExpectedValue _element;
        private readonly long _lengthOffset;
        private readonly bool _isSlotArray;

        internal ArrayFrame(int count, ExpectedValue element, OwnerContext context, long lengthOffset, bool isSlotArray)
        {
            Count = count;
            _element = element;
            Context = context;
            _lengthOffset = lengthOffset;
            _isSlotArray = isSlotArray;
        }

        internal override int Count { get; }
        internal override ExpectedValue Current => _element;
        internal void Complete(long endOffset)
        {
            // Arrays can be serialized before the owning RGD_Storage and linked later
            // through MemberReference. Preserve their location until that link exists.
            Context.SetSlotArray(Count, _lengthOffset, endOffset);
        }
    }

    private sealed class ClassFrame : ValueFrame
    {
        private readonly int _objectId;
        private readonly ClassMetadata _metadata;
        private readonly Dictionary<string, (object Value, long Offset, PrimitiveType Type)> _primitiveValues = new(StringComparer.Ordinal);

        internal ClassFrame(int objectId, ClassMetadata metadata, OwnerContext context)
        {
            _objectId = objectId;
            _metadata = metadata;
            Context = context;
        }

        internal override int Count => _metadata.Members.Length;
        internal override ExpectedValue Current => _metadata.Members[Index];

        internal override void AcceptPrimitive(ExpectedValue expected, object value, long offset)
        {
            _primitiveValues[expected.Name] = (value, offset, expected.PrimitiveType);
            base.AcceptPrimitive(expected, value, offset);
        }

        internal RawSlot? CompleteSlot()
        {
            if (_metadata.TypeName != "RGD_Slot")
                return null;

            Context.SetSlotMetadataId(_metadata.MetadataId);

            (short slotIndex, long slotIndexOffset) = ReadInt16("SlotIndex");
            (short itemId, long itemIdOffset) = ReadInt16("ItemIndex");
            (short amount, long amountOffset) = ReadInt16("ItemAmount");
            (short uses, long usesOffset) = ReadInt16("ItemUses");
            return new RawSlot(_objectId, slotIndex, itemId, amount, uses, slotIndexOffset, itemIdOffset, amountOffset, usesOffset, Context);
        }

        internal void CompleteOwner()
        {
            if (_metadata.TypeName == "RGD_Storage"
                && _primitiveValues.TryGetValue("BlockIndex", out var entry)
                && entry.Type == PrimitiveType.Int32
                && entry.Value is int itemId)
                Context.SetStorageItemId(itemId);
        }

        private (short Value, long Offset) ReadInt16(string name)
        {
            if (!_primitiveValues.TryGetValue(name, out var entry) || entry.Type != PrimitiveType.Int16 || entry.Value is not short value)
                throw Invalid($"RGD_Slot.{name} não é Int16 no payload.");
            return (value, entry.Offset);
        }
    }

    private readonly record struct ParsedRecord(ValueFrame? Frame, int ValuesConsumed, bool IsLibrary, bool IsMessageEnd)
    {
        internal static ParsedRecord Value => new(null, 1, false, false);
        internal static ParsedRecord Library => new(null, 0, true, false);
        internal static ParsedRecord MessageEnd => new(null, 0, false, true);
        internal static ParsedRecord Container(ValueFrame frame) => new(frame, 1, false, false);
        internal static ParsedRecord Multiple(int count)
        {
            if (count <= 0) throw Invalid("ObjectNullMultiple com contagem inválida.");
            return new ParsedRecord(null, count, false, false);
        }
    }

    private enum RecordType : byte
    {
        SerializedStreamHeader = 0,
        ClassWithId = 1,
        SystemClassWithMembers = 2,
        ClassWithMembers = 3,
        SystemClassWithMembersAndTypes = 4,
        ClassWithMembersAndTypes = 5,
        BinaryObjectString = 6,
        BinaryArray = 7,
        MemberPrimitiveTyped = 8,
        MemberReference = 9,
        ObjectNull = 10,
        MessageEnd = 11,
        BinaryLibrary = 12,
        ObjectNullMultiple256 = 13,
        ObjectNullMultiple = 14,
        ArraySinglePrimitive = 15,
        ArraySingleObject = 16,
        ArraySingleString = 17,
        MethodCall = 21,
        MethodReturn = 22
    }

    private enum BinaryType : byte
    {
        Primitive = 0,
        String = 1,
        Object = 2,
        SystemClass = 3,
        Class = 4,
        ObjectArray = 5,
        StringArray = 6,
        PrimitiveArray = 7
    }

    private enum PrimitiveType : byte
    {
        Boolean = 1,
        Byte = 2,
        Char = 3,
        Unused = 4,
        Decimal = 5,
        Double = 6,
        Int16 = 7,
        Int32 = 8,
        Int64 = 9,
        SByte = 10,
        Single = 11,
        TimeSpan = 12,
        DateTime = 13,
        UInt16 = 14,
        UInt32 = 15,
        UInt64 = 16
    }
}

internal sealed record NrbfTrace(
    ReadOnlyCollection<RawSlot> Slots,
    ReadOnlyCollection<OwnerContext> Owners,
    int SlotMetadataId,
    int SlotLibraryId,
    int MaxObjectId);

internal sealed class OwnerContext(string label, string technical, string kind)
{
    private OwnerContext? _canonical;
    private int _slotCapacity;
    private int _storageItemId;
    private int _serializedSlotCount;
    private int _slotMetadataId;
    private long _slotArrayLengthOffset = -1;
    private long _slotArrayEndOffset = -1;
    internal string Label => _canonical?.Label ?? label;
    internal string Technical => _canonical?.Technical ?? technical;
    internal string Kind => _canonical?.Kind ?? kind;
    internal int SlotCapacity => _canonical?.SlotCapacity ?? _slotCapacity;
    internal int StorageItemId => _canonical?.StorageItemId ?? _storageItemId;
    internal int SerializedSlotCount => _canonical?.SerializedSlotCount ?? _serializedSlotCount;
    internal int SlotMetadataId => _canonical?.SlotMetadataId ?? _slotMetadataId;
    internal long SlotArrayLengthOffset => _canonical?.SlotArrayLengthOffset ?? _slotArrayLengthOffset;
    internal long SlotArrayEndOffset => _canonical?.SlotArrayEndOffset ?? _slotArrayEndOffset;
    internal bool IsResolved => Kind is "RGD_Storage" or "RGD_PlayerInventory";

    internal static OwnerContext CreateUnknown() => new("Unknown Container", "Owner not resolved", "Unknown");

    internal void AssignFrom(OwnerContext owner)
    {
        _canonical = owner;
    }

    internal void SetSlotCapacity(int capacity)
    {
        if (_canonical is not null) _canonical.SetSlotCapacity(capacity);
        else if (capacity > 0) _slotCapacity = capacity;
    }
    internal void SetStorageItemId(int itemId)
    {
        if (_canonical is not null)
        {
            _canonical.SetStorageItemId(itemId);
            return;
        }
        _storageItemId = itemId;
        _slotCapacity = itemId switch
        {
            140 => 8,
            42 => 20,
            296 => 40,
            640 => 25,
            _ => _slotCapacity
        };
    }

    internal void SetSlotArray(int count, long lengthOffset, long endOffset)
    {
        if (_canonical is not null)
        {
            _canonical.SetSlotArray(count, lengthOffset, endOffset);
            return;
        }
        _serializedSlotCount = count;
        _slotArrayLengthOffset = lengthOffset;
        _slotArrayEndOffset = endOffset;
    }

    internal void SetSlotMetadataId(int metadataId)
    {
        if (_canonical is not null) _canonical.SetSlotMetadataId(metadataId);
        else if (_slotMetadataId == 0) _slotMetadataId = metadataId;
    }
}

internal sealed class RawSlot(
    int recordId,
    short slotIndex,
    short itemId,
    short amount,
    short uses,
    long slotIndexOffset,
    long itemIdOffset,
    long amountOffset,
    long usesOffset,
    OwnerContext owner)
{
    internal int RecordId { get; } = recordId;
    internal short SlotIndex { get; } = slotIndex;
    internal short ItemId { get; } = itemId;
    internal short Amount { get; } = amount;
    internal short Uses { get; } = uses;
    internal long SlotIndexOffset { get; } = slotIndexOffset;
    internal long ItemIdOffset { get; } = itemIdOffset;
    internal long AmountOffset { get; } = amountOffset;
    internal long UsesOffset { get; } = usesOffset;
    internal string Location => owner.Kind == "RGD_PlayerInventory"
        ? SlotIndex < 10 ? "Hotbar" : "Player Inventory"
        : owner.Label;
    internal string TechnicalOwner => owner.Technical;
}

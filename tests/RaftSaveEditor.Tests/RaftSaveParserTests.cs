using System.Runtime.Serialization.Formatters.Binary;
using RaftSaveEditor.Core.Parsing;
using RaftSaveEditor.Core.Writing;
using RaftSaveEditor.Core.Catalog;

namespace RaftSaveEditor.Tests;

public sealed class RaftSaveParserTests
{
    [Fact]
    public void Catalog_resolves_known_and_preserves_unknown_ids()
    {
        RaftItemCatalog catalog = new();
        Assert.Equal("Tábua", catalog.Resolve(21));
        Assert.Equal("Plástico", catalog.Resolve(53));
        Assert.Equal("Unknown Item (ID 32000)", catalog.Resolve(32_000));
        Assert.Equal(20, catalog.Get(21).StackSize);
        Assert.Equal("Materials", catalog.Get(21).Type);
        Assert.Equal("Game_21.png", catalog.Get(21).IconFile);
        Assert.Equal(40, catalog.Get(35).MaxUses);
        Assert.False(catalog.Get(35).IsStackable);
        Assert.Equal("Receita: sushi", catalog.Resolve(227));
        Assert.Equal("Rede de captura", catalog.Resolve(291));
        Assert.Equal(35, catalog.Get(291).MaxUses);
        Assert.Equal("Machado de titânio", catalog.Resolve(559));
        Assert.Equal(240, catalog.Get(559).MaxUses);
        Assert.Equal("Game_559.png", catalog.Get(559).IconFile);
        Assert.Equal("Moeda de troca", catalog.Resolve(564));
        Assert.True(catalog.IsKnown(645));
        Assert.True(catalog.Get(7).CanAddToInventory);
        Assert.Equal(5, catalog.Get(7).StackSize);
        Assert.Equal("Game_7.png", catalog.Get(7).IconFile);
        Assert.True(catalog.Get(589).CanAddToInventory);
        Assert.Equal("Game_589.png", catalog.Get(589).IconFile);
        Assert.False(catalog.Get(1).CanAddToInventory);
        Assert.DoesNotContain(catalog.AddableItems, item => item.Name.Equals("An item", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(catalog.AddableItems, item => item.Id == 1);
        Assert.Contains(catalog.AddableItems, item => item.Id == 7);
        Assert.Equal(0, catalog.Get(32_000).StackSize);
        RaftItemCatalog overridden = new(new Dictionary<short, string> { [21] = "Madeira", [559] = "New Item" });
        Assert.Equal("Madeira", overridden.Resolve(21));
        Assert.Equal("New Item", overridden.Resolve(559));
        Assert.Equal("Plástico", overridden.Resolve(53));
    }

    [Fact]
    public void Opens_and_safely_patches_a_synthetic_nrbf_save()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "test.rgd");
        TestRoot root = new()
        {
            Slots =
            [
                new RGD_Slot { SlotIndex = 3, ItemIndex = 21, ItemAmount = 20, ItemUses = 1, ExclusiveString = "" },
                new RGD_Slot { SlotIndex = 4, ItemIndex = 53, ItemAmount = 7, ItemUses = 1, ExclusiveString = "" }
            ]
        };

#pragma warning disable SYSLIB0011 // Trusted test fixture only; production never uses BinaryFormatter.
        using (FileStream output = File.Create(path))
            new BinaryFormatter().Serialize(output, root);
#pragma warning restore SYSLIB0011

        byte[] before = File.ReadAllBytes(path);
        RaftSaveParser parser = new();
        var document = parser.Open(path);

        Assert.Equal(2, document.Items.Count);
        Assert.Equal((short)21, document.Items[0].ItemId);
        Assert.Equal((short)20, document.Items[0].Amount);

        document.SetSlotValues(document.Items[0], itemId: 22, amount: 30_000, uses: 9, slotIndex: 12);
        new RaftSaveWriter().Save(document);
        byte[] after = File.ReadAllBytes(path);
        var reopened = parser.Open(path);

        Assert.Empty(Directory.GetFiles(temporary.Path, "*.backup-*"));
        var edited = reopened.Items.Single(item => item.ItemId == 22);
        Assert.Equal(22, edited.ItemId);
        Assert.Equal(30_000, edited.Amount);
        Assert.Equal(9, edited.Uses);
        Assert.Equal(12, edited.SlotIndex);
        Assert.InRange(before.Zip(after).Count(pair => pair.First != pair.Second), 4, 8);
    }

    [Fact]
    public void Clearing_an_item_slot_removes_it_from_the_reopened_save()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "delete-item.rgd");
        TestRoot root = new()
        {
            Slots =
            [
                new RGD_Slot { SlotIndex = 7, ItemIndex = 21, ItemAmount = 20, ItemUses = 3, ExclusiveString = "" }
            ]
        };

#pragma warning disable SYSLIB0011 // Trusted test fixture only; production never uses BinaryFormatter.
        using (FileStream output = File.Create(path))
            new BinaryFormatter().Serialize(output, root);
#pragma warning restore SYSLIB0011

        RaftSaveParser parser = new();
        var document = parser.Open(path);
        var item = Assert.Single(document.Items);

        document.SetSlotValues(item, itemId: 0, amount: 0, uses: 0, slotIndex: item.SlotIndex);
        new RaftSaveWriter().Save(document);

        var reopened = parser.Open(path);
        Assert.Empty(reopened.Items);
        var clearedSlot = Assert.Single(reopened.AllSlots);
        Assert.Equal(0, clearedSlot.ItemId);
        Assert.Equal(0, clearedSlot.Amount);
        Assert.Equal(0, clearedSlot.Uses);
        Assert.Equal(7, clearedSlot.SlotIndex);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.backup-*"));
    }

    [Fact]
    public void Filling_an_empty_slot_adds_an_item_and_counts_as_a_pending_change()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "add-item.rgd");
        TestRoot root = new()
        {
            Slots =
            [
                new RGD_Slot { SlotIndex = 4, ItemIndex = 0, ItemAmount = 0, ItemUses = 0, ExclusiveString = "" }
            ]
        };

#pragma warning disable SYSLIB0011 // Trusted test fixture only; production never uses BinaryFormatter.
        using (FileStream output = File.Create(path))
            new BinaryFormatter().Serialize(output, root);
#pragma warning restore SYSLIB0011

        RaftSaveParser parser = new();
        var document = parser.Open(path);
        Assert.Empty(document.Items);
        var emptySlot = Assert.Single(document.AllSlots);

        document.SetSlotValues(emptySlot, itemId: 21, amount: 20, uses: 1, slotIndex: emptySlot.SlotIndex);
        Assert.Equal(1, document.PendingChanges);
        new RaftSaveWriter().Save(document);

        var reopened = parser.Open(path);
        var added = Assert.Single(reopened.Items);
        Assert.Equal(21, added.ItemId);
        Assert.Equal(20, added.Amount);
        Assert.Equal(1, added.Uses);
        Assert.Equal(4, added.SlotIndex);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.backup-*"));
    }

    [Fact]
    public void Opens_real_save_when_RAFT_TEST_SAVE_is_set()
    {
        string? source = Environment.GetEnvironmentVariable("RAFT_TEST_SAVE");
        if (string.IsNullOrWhiteSpace(source))
            return;

        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "real-copy.rgd");
        File.Copy(source, path);
        byte[] before = File.ReadAllBytes(path);
        RaftSaveParser parser = new();
        var document = parser.Open(path);
        Assert.NotEmpty(document.Items);
        Assert.All(document.Items, item => Assert.InRange(item.Amount, (short)0, short.MaxValue));
        if (document.RootType == "RGD_Game")
        {
            Assert.NotEmpty(document.Storages);
            Assert.NotEmpty(document.AllSlots.Where(slot => slot.Location.StartsWith("Storage", StringComparison.OrdinalIgnoreCase)));
        }
        if (Path.GetFileName(source).Equals("RGD_Users.rgd", StringComparison.OrdinalIgnoreCase))
            Assert.NotEmpty(document.PlayerStorages);

        var target = document.Items[0];
        short changedAmount = target.Amount == 30_000 ? (short)29_999 : (short)30_000;
        document.SetAmount(target, changedAmount);
        new RaftSaveWriter().Save(document);
        byte[] after = File.ReadAllBytes(path);
        var reopened = parser.Open(path);

        Assert.Equal(changedAmount, reopened.AllSlots.Single(slot => slot.RecordId == target.RecordId).Amount);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.backup-*"));
        Assert.InRange(before.Zip(after).Count(pair => pair.First != pair.Second), 1, 2);
    }

    [Fact]
    public void Adds_item_to_real_player_storage_when_RAFT_TEST_PLAYER_SAVE_is_set()
    {
        string? source = Environment.GetEnvironmentVariable("RAFT_TEST_PLAYER_SAVE");
        if (string.IsNullOrWhiteSpace(source))
            return;

        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "RGD_Users.rgd");
        File.Copy(source, path);
        RaftSaveParser parser = new();
        var document = parser.Open(path);
        var playerStorage = document.PlayerStorages.FirstOrDefault(storage =>
        {
            int occupied = document.AllSlots.Count(slot =>
                slot.TechnicalOwner == storage.TechnicalOwner && slot.ItemId > 0);
            return storage.SlotCapacity > occupied
                && storage.SlotArrayLengthOffset >= 0
                && storage.SlotArrayEndOffset >= 0;
        });
        Assert.True(playerStorage is not null, string.Join(" | ", document.PlayerStorages.Select(storage =>
        {
            var slots = document.AllSlots.Where(slot => slot.TechnicalOwner == storage.TechnicalOwner).ToArray();
            return $"{storage.Name}: capacity={storage.SlotCapacity}, serialized={storage.SerializedSlotCount}, occupied={slots.Count(slot => slot.ItemId > 0)}, indexes=[{string.Join(',', slots.Select(slot => $"{slot.SlotIndex}:{slot.ItemId}"))}], offsets={storage.SlotArrayLengthOffset}/{storage.SlotArrayEndOffset}";
        })));
        var selectedStorage = playerStorage!;
        HashSet<short> usedSlots = document.AllSlots
            .Where(slot => slot.TechnicalOwner == selectedStorage.TechnicalOwner && slot.ItemId > 0)
            .Select(slot => slot.SlotIndex)
            .ToHashSet();
        int emptyIndex = Enumerable.Range(0, selectedStorage.SlotCapacity)
            .First(index => !usedSlots.Contains((short)index));

        document.AddSlot(selectedStorage, itemId: 21, amount: 20, uses: 1, slotIndex: emptyIndex);
        new RaftSaveWriter().Save(document);

        var reopened = parser.Open(path);
        Assert.Contains(reopened.Items, item =>
            item.TechnicalOwner == selectedStorage.TechnicalOwner
            && item.SlotIndex == emptyIndex
            && item.ItemId == 21
            && item.Amount == 20
            && item.Uses == 1);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.backup-*"));
    }

    [Serializable]
    private sealed class TestRoot
    {
        public RGD_Slot[] Slots = [];
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RaftSaveEditor.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

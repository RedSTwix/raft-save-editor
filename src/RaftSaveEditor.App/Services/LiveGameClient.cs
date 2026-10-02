using System.IO;
using System.IO.Pipes;

namespace RaftSaveEditor.App.Services;

internal sealed record LiveItemSnapshot(
    int SlotIndex,
    int ItemId,
    int Amount,
    int Uses,
    int MaxStack,
    int MaxUses,
    string InternalName);

internal sealed record LiveContainerSnapshot(
    string Key,
    string Name,
    bool IsPlayer,
    int Capacity,
    IReadOnlyList<LiveItemSnapshot> Items);

internal sealed record LiveSnapshot(IReadOnlyList<LiveContainerSnapshot> Containers, string Message);

internal sealed class LiveConnectorException(string message) : Exception(message);

internal sealed class LiveGameClient
{
    private const string PipeName = "RaftSaveEditor.Live.v5";
    private const int Magic = 0x52534545;
    private const int ProtocolVersion = 5;

    private enum Command : byte
    {
        Ping = 1,
        Snapshot,
        SetItem,
        RemoveItem,
        AddItem,
        MaxContainer,
        Save,
        AddItems,
        Status
    }

    internal async Task<string> PingAsync() => (await SendAsync(Command.Ping)).Message;
    internal async Task<string> StatusAsync() => (await SendAsync(Command.Status)).Message;
    internal async Task<LiveSnapshot> SnapshotAsync()
    {
        LiveResponse response = await SendAsync(Command.Snapshot);
        return new LiveSnapshot(response.Containers, response.Message);
    }

    internal async Task<string> SetItemAsync(string containerKey, int slotIndex, int targetSlotIndex, int itemId, int amount, int uses, int expectedItemId) =>
        (await SendAsync(Command.SetItem, containerKey, slotIndex, targetSlotIndex, itemId, amount, uses, expectedItemId)).Message;

    internal async Task<string> RemoveItemAsync(string containerKey, int slotIndex, int expectedItemId) =>
        (await SendAsync(Command.RemoveItem, containerKey, slotIndex, expectedItemId: expectedItemId)).Message;

    internal async Task<string> AddItemAsync(string containerKey, int itemId) =>
        (await SendAsync(Command.AddItem, containerKey, itemId: itemId)).Message;

    internal async Task<string> AddItemsAsync(string containerKey, IReadOnlyList<ItemAddRequest> items)
    {
        if (items.Count == 0) throw new LiveConnectorException("A lista de itens está vazia.");
        return (await SendAsync(Command.AddItems, containerKey, itemRequests: items)).Message;
    }

    internal async Task<string> MaxContainerAsync(string containerKey) =>
        (await SendAsync(Command.MaxContainer, containerKey)).Message;

    private static async Task<LiveResponse> SendAsync(
        Command command,
        string containerKey = "",
        int slotIndex = 0,
        int targetSlotIndex = 0,
        int itemId = 0,
        int amount = 0,
        int uses = 0,
        int expectedItemId = 0,
        IReadOnlyList<ItemAddRequest>? itemRequests = null)
    {
        using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        try
        {
            await pipe.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new LiveConnectorException("O jogo foi encontrado, mas o conector não respondeu. Reinicie o Raft após instalar o conector.");
        }

        BinaryWriter writer = new(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(ProtocolVersion);
        writer.Write((byte)command);
        writer.Write(containerKey ?? string.Empty);
        writer.Write(slotIndex);
        writer.Write(targetSlotIndex);
        writer.Write(itemId);
        writer.Write(amount);
        writer.Write(uses);
        writer.Write(expectedItemId);
        if (command == Command.AddItems)
        {
            writer.Write(itemRequests?.Count ?? 0);
            if (itemRequests is not null)
                foreach (ItemAddRequest item in itemRequests)
                {
                    writer.Write((int)item.ItemId);
                    writer.Write(item.Amount);
                }
        }
        writer.Flush();

        BinaryReader reader = new(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != ProtocolVersion)
            throw new LiveConnectorException("O conector instalado é incompatível com esta versão do editor.");
        bool success = reader.ReadBoolean();
        string message = reader.ReadString();
        if (!success) throw new LiveConnectorException(message);

        List<LiveContainerSnapshot> containers = [];
        if (command == Command.Snapshot)
        {
            int containerCount = reader.ReadInt32();
            for (int c = 0; c < containerCount; c++)
            {
                string key = reader.ReadString();
                string name = reader.ReadString();
                bool isPlayer = reader.ReadBoolean();
                int capacity = reader.ReadInt32();
                int itemCount = reader.ReadInt32();
                List<LiveItemSnapshot> items = new(itemCount);
                for (int i = 0; i < itemCount; i++)
                {
                    items.Add(new LiveItemSnapshot(
                        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                        reader.ReadInt32(), reader.ReadInt32(), reader.ReadString()));
                }
                containers.Add(new LiveContainerSnapshot(key, name, isPlayer, capacity, items));
            }
        }
        return new LiveResponse(message, containers);
    }

    private sealed record LiveResponse(string Message, IReadOnlyList<LiveContainerSnapshot> Containers);
}

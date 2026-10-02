using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace RaftSaveEditor.Connector;

public static class EntryPoint
{
    private static GameObject _connectorObject;

    public static void Initialize()
    {
        if (_connectorObject != null) return;
        _connectorObject = new GameObject("Raft Save Editor Live Connector");
        UnityEngine.Object.DontDestroyOnLoad(_connectorObject);
        _connectorObject.AddComponent<LiveConnectorPlugin>();
    }
}

[BepInPlugin(LiveConnectorPlugin.PluginId, LiveConnectorPlugin.PluginName, LiveConnectorPlugin.PluginVersion)]
public sealed class PersistentConnectorPlugin : BaseUnityPlugin
{
    private void Awake() => EntryPoint.Initialize();
}

public sealed class LiveConnectorPlugin : MonoBehaviour
{
    public const string PluginId = "com.rafael.raftsaveeditor.connector";
    public const string PluginName = "Raft Save Editor Connector";
    public const string PluginVersion = "5.0.0";
    private const string PipeName = "RaftSaveEditor.Live.v5";
    private const int Magic = 0x52534545;
    private const int ProtocolVersion = 5;

    private readonly ConcurrentQueue<PendingRequest> _requests = new();
    private Thread _serverThread;
    private volatile bool _stopping;
    private NamedPipeServerStream _activeServer;

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

    private sealed class Request
    {
        public Command Command;
        public string ContainerKey;
        public int SlotIndex;
        public int TargetSlotIndex;
        public int ItemId;
        public int Amount;
        public int Uses;
        public int ExpectedItemId;
        public List<RequestedItem> Items;
    }

    private sealed class RequestedItem
    {
        public int ItemId;
        public int Amount;
    }

    private sealed class PendingRequest
    {
        public readonly Request Request;
        public readonly ManualResetEvent Completed = new(false);
        public Response Response;
        public PendingRequest(Request request) => Request = request;
    }

    private sealed class Response
    {
        public bool Success;
        public string Message;
        public List<ContainerData> Containers;
        public static Response Ok(string message) => new() { Success = true, Message = message };
        public static Response Fail(string message) => new() { Success = false, Message = message };
        public static Response Snapshot(List<ContainerData> containers) =>
            new() { Success = true, Message = "Jogo conectado.", Containers = containers };
    }

    private sealed class ContainerData
    {
        public readonly string Key;
        public readonly string Name;
        public readonly bool IsPlayer;
        public readonly int Capacity;
        public readonly List<ItemData> Items = new();
        public ContainerData(string key, string name, bool isPlayer, int capacity)
        {
            Key = key;
            Name = name;
            IsPlayer = isPlayer;
            Capacity = capacity;
        }
    }

    private sealed class ItemData
    {
        public readonly int SlotIndex;
        public readonly int ItemId;
        public readonly int Amount;
        public readonly int Uses;
        public readonly int MaxStack;
        public readonly int MaxUses;
        public readonly string InternalName;
        public ItemData(int slotIndex, int itemId, int amount, int uses, int maxStack, int maxUses, string internalName)
        {
            SlotIndex = slotIndex;
            ItemId = itemId;
            Amount = amount;
            Uses = uses;
            MaxStack = maxStack;
            MaxUses = maxUses;
            InternalName = internalName;
        }
    }

    private sealed class ContainerTarget
    {
        public readonly object Inventory;
        public readonly object Storage;
        public readonly IList Slots;
        public ContainerTarget(object inventory, object storage, IList slots)
        {
            Inventory = inventory;
            Storage = storage;
            Slots = slots;
        }
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        _serverThread = new Thread(ServerLoop) { IsBackground = true, Name = "RaftSaveEditor IPC" };
        _serverThread.Start();
        Debug.Log($"[Raft Save Editor] Conector v{ProtocolVersion} iniciado.");
    }

    private void Update()
    {
        while (_requests.TryDequeue(out PendingRequest pending))
        {
            try { pending.Response = ExecuteOnMainThread(pending.Request); }
            catch (Exception ex) { pending.Response = Response.Fail(ex.GetBaseException().Message); }
            finally { pending.Completed.Set(); }
        }
    }

    private void OnDestroy()
    {
        _stopping = true;
        try { _activeServer?.Dispose(); } catch { }
    }

    private void ServerLoop()
    {
        while (!_stopping)
        {
            try
            {
                using NamedPipeServerStream server = new(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                _activeServer = server;
                server.WaitForConnection();
                if (_stopping) break;

                Request request = ReadRequest(server);
                Response response;
                if (request.Command == Command.Ping)
                {
                    response = Response.Ok($"{PluginName} {PluginVersion}");
                }
                else
                {
                    PendingRequest pending = new(request);
                    _requests.Enqueue(pending);
                    response = pending.Completed.WaitOne(TimeSpan.FromSeconds(15))
                        ? pending.Response
                        : Response.Fail("O jogo não respondeu dentro do tempo limite.");
                }
                WriteResponse(server, request.Command, response ?? Response.Fail("Resposta vazia do conector."));
            }
            catch (ObjectDisposedException) when (_stopping) { break; }
            catch (IOException) when (_stopping) { break; }
            catch (Exception ex)
            {
                if (!_stopping) Debug.LogWarning($"[Raft Save Editor] Falha no canal local: {ex.Message}");
            }
            finally { _activeServer = null; }
        }
    }

    private Response ExecuteOnMainThread(Request request) => request.Command switch
    {
        Command.Status => CheckWorldReady(),
        Command.Snapshot => CreateSnapshot(),
        Command.SetItem => SetItem(request),
        Command.RemoveItem => RemoveItem(request),
        Command.AddItem => AddItem(request),
        Command.AddItems => AddItems(request),
        Command.MaxContainer => MaxContainer(request),
        Command.Save => SaveGame("Jogo salvo."),
        _ => Response.Fail("Comando desconhecido.")
    };

    private Response CreateSnapshot()
    {
        Response readiness = CheckWorldReady();
        if (!readiness.Success) return readiness;
        List<ContainerData> containers = new();
        object playerInventory = FindPlayerInventory();
        if (playerInventory != null)
            containers.Add(ReadContainer("player", "Inventário do jogador", true, playerInventory));

        IList storages = GetStaticValue(GameType("StorageManager"), "allStorages") as IList;
        if (storages != null)
        {
            foreach (object storage in storages)
            {
                if (storage == null) continue;
                object inventory = Invoke(storage, "GetInventoryReference");
                if (inventory == null) continue;
                uint id = Convert.ToUInt32(GetValue(storage, "ObjectIndex") ?? 0);
                containers.Add(ReadContainer($"storage:{id}", $"Storage #{id}", false, inventory));
            }
        }

        return containers.Count == 0
            ? Response.Fail("Entre em um mundo do Raft antes de detectar o jogo.")
            : Response.Snapshot(containers);
    }

    private static ContainerData ReadContainer(string key, string name, bool isPlayer, object inventory)
    {
        IList slots = GetValue(inventory, "allSlots") as IList;
        ContainerData result = new(key, name, isPlayer, slots?.Count ?? 0);
        if (slots == null) return result;
        for (int i = 0; i < slots.Count; i++)
        {
            ItemData item = ReadItem(slots[i], i);
            if (item != null) result.Items.Add(item);
        }
        return result;
    }

    private static ItemData ReadItem(object slot, int slotIndex)
    {
        if (slot == null) return null;
        object instance = GetValue(slot, "itemInstance");
        object baseItem = GetValue(instance, "baseItem");
        if (instance == null || baseItem == null) return null;

        int id = Convert.ToInt32(GetValue(baseItem, "UniqueIndex") ?? 0);
        int amount = Convert.ToInt32(GetValue(instance, "Amount") ?? 0);
        int uses = Convert.ToInt32(GetValue(instance, "Uses") ?? 0);
        int maxUses = Math.Max(1, Convert.ToInt32(GetValue(baseItem, "MaxUses") ?? 1));
        object inventorySettings = GetValue(baseItem, "settings_Inventory");
        int maxStack = Math.Max(1, Convert.ToInt32(GetValue(inventorySettings, "StackSize") ?? 1));
        string internalName = Convert.ToString(GetValue(baseItem, "UniqueName") ?? $"Item {id}");
        return new ItemData(slotIndex, id, amount, uses, maxStack, maxUses, internalName);
    }

    private Response SetItem(Request request)
    {
        Response readiness = CheckWorldReady();
        if (!readiness.Success) return readiness;
        ContainerTarget target = FindContainer(request.ContainerKey);
        if (target == null) return Response.Fail("Inventário não encontrado. Atualize a lista.");
        if (!ValidSlot(target.Slots, request.SlotIndex) || !ValidSlot(target.Slots, request.TargetSlotIndex))
            return Response.Fail("Slot fora do limite deste inventário.");

        object baseItem = GetItemByIndex(request.ItemId);
        if (baseItem == null) return Response.Fail($"O Raft não reconheceu o Item ID {request.ItemId}.");
        int maxStack = GetMaxStack(baseItem);
        int maxUses = GetMaxUses(baseItem);
        if (request.Amount < 1 || request.Amount > maxStack)
            return Response.Fail($"Quantidade permitida: 1 a {maxStack}.");
        if (request.Uses < 0 || request.Uses > maxUses)
            return Response.Fail($"Uses permitido: 0 a {maxUses}.");

        object source = target.Slots[request.SlotIndex];
        object destination = target.Slots[request.TargetSlotIndex];
        ItemData current = ReadItem(source, request.SlotIndex);
        if (current == null || current.ItemId != request.ExpectedItemId)
            return Response.Fail("O item mudou de posição ou não existe mais. Atualize a lista antes de editar.");
        if (request.TargetSlotIndex != request.SlotIndex && ReadItem(destination, request.TargetSlotIndex) != null)
            return Response.Fail("O slot de destino já está ocupado.");

        SetSlot(destination, baseItem, request.Amount, request.Uses);
        if (request.TargetSlotIndex != request.SlotIndex) Invoke(source, "Reset");
        Refresh(target, source, destination);
        return SaveGame("Item alterado ao vivo e jogo salvo.");
    }

    private Response RemoveItem(Request request)
    {
        Response readiness = CheckWorldReady();
        if (!readiness.Success) return readiness;
        ContainerTarget target = FindContainer(request.ContainerKey);
        if (target == null || !ValidSlot(target.Slots, request.SlotIndex))
            return Response.Fail("Item não encontrado. Atualize a lista.");
        object slot = target.Slots[request.SlotIndex];
        ItemData current = ReadItem(slot, request.SlotIndex);
        if (current == null || current.ItemId != request.ExpectedItemId)
            return Response.Fail("O item mudou de posição ou não existe mais. Atualize a lista antes de remover.");
        Invoke(slot, "Reset");
        Refresh(target, slot);
        return SaveGame("Item removido ao vivo e jogo salvo.");
    }

    private Response AddItem(Request request)
    {
        Response readiness = CheckWorldReady();
        if (!readiness.Success) return readiness;
        ContainerTarget target = FindContainer(request.ContainerKey);
        if (target == null) return Response.Fail("Inventário não encontrado.");
        object slot = target.Slots.Cast<object>().FirstOrDefault(candidate => ReadItem(candidate, 0) == null);
        if (slot == null) return Response.Fail("Este inventário não possui slot vazio.");
        object baseItem = GetItemByIndex(request.ItemId);
        if (baseItem == null) return Response.Fail($"O Raft não reconheceu o Item ID {request.ItemId}.");
        SetSlot(slot, baseItem, GetMaxStack(baseItem), GetMaxUses(baseItem));
        Refresh(target, slot);
        return SaveGame("Item adicionado ao vivo no máximo e jogo salvo.");
    }

    private Response AddItems(Request request)
    {
        Response readiness = CheckWorldReady();
        if (!readiness.Success) return readiness;
        ContainerTarget target = FindContainer(request.ContainerKey);
        if (target == null) return Response.Fail("Inventário não encontrado.");
        if (request.Items == null || request.Items.Count == 0) return Response.Fail("A lista de itens está vazia.");

        List<object> emptySlots = target.Slots.Cast<object>()
            .Where(candidate => ReadItem(candidate, 0) == null)
            .ToList();
        if (request.Items.Count > emptySlots.Count)
            return Response.Fail($"A lista possui {request.Items.Count} itens, mas restam apenas {emptySlots.Count} slots livres.");

        List<object> baseItems = new(request.Items.Count);
        foreach (RequestedItem requested in request.Items)
        {
            object baseItem = GetItemByIndex(requested.ItemId);
            if (baseItem == null) return Response.Fail($"O Raft não reconheceu o Item ID {requested.ItemId}.");
            int maxStack = GetMaxStack(baseItem);
            if (requested.Amount < 1 || requested.Amount > maxStack)
                return Response.Fail($"O Item ID {requested.ItemId} permite quantidade de 1 a {maxStack}.");
            baseItems.Add(baseItem);
        }

        for (int i = 0; i < baseItems.Count; i++)
            SetSlot(emptySlots[i], baseItems[i], request.Items[i].Amount, GetMaxUses(baseItems[i]));
        Refresh(target, emptySlots.Take(baseItems.Count).ToArray());
        return SaveGame($"{baseItems.Count} item(ns) adicionado(s) ao vivo no máximo e jogo salvo.");
    }

    private Response MaxContainer(Request request)
    {
        Response readiness = CheckWorldReady();
        if (!readiness.Success) return readiness;
        ContainerTarget target = FindContainer(request.ContainerKey);
        if (target == null) return Response.Fail("Inventário não encontrado.");
        int count = 0;
        foreach (object slot in target.Slots)
        {
            ItemData item = ReadItem(slot, 0);
            if (item == null) continue;
            object baseItem = GetValue(GetValue(slot, "itemInstance"), "baseItem");
            SetSlot(slot, baseItem, item.MaxStack, item.MaxUses);
            count++;
        }
        Refresh(target, target.Slots.Cast<object>().ToArray());
        return SaveGame($"{count} itens colocados no máximo e jogo salvo.");
    }

    private Response SaveGame(string successMessage)
    {
        object saveAndLoad = FindObject("SaveAndLoad");
        if (saveAndLoad == null) return Response.Fail("O sistema de save do Raft não está disponível.");
        MethodInfo save = FindMethod(saveAndLoad.GetType(), "SaveGame", 1);
        if (save == null) return Response.Fail("O método de salvar desta versão do Raft não foi encontrado.");
        save.Invoke(saveAndLoad, new object[] { false });
        return Response.Ok(successMessage);
    }

    private static Response CheckWorldReady()
    {
        Type saveType = GameType("SaveAndLoad");
        Type sceneType = GameType("LoadSceneManager");
        if (ReadStaticBool(saveType, "IsGameLoading") ||
            ReadStaticBool(sceneType, "IsLoadingScene") ||
            ReadStaticBool(sceneType, "IsLoadingLobbyScene"))
            return Response.Fail("O Raft está carregando uma cena ou um mundo. Aguarde terminar antes de editar.");
        if (ReadStaticBool(saveType, "IsPerformingASave"))
            return Response.Fail("O Raft está salvando neste momento. Aguarde alguns segundos e atualize.");
        if (!ReadStaticBool(sceneType, "IsGameSceneLoaded"))
            return Response.Fail("Entre em um mundo do Raft antes de usar a edição ao vivo.");
        if (FindObject("SaveAndLoad") == null || FindPlayerInventory() == null)
            return Response.Fail("O mundo ainda não disponibilizou o jogador e o sistema de save.");
        return Response.Ok("Pronto para edição ao vivo · mundo carregado · save livre");
    }

    private static bool ReadStaticBool(Type type, string name)
    {
        object value = GetStaticValue(type, name);
        return value is bool flag && flag;
    }

    private static void SetSlot(object slot, object baseItem, int amount, int uses)
    {
        MethodInfo setItem = FindMethod(slot.GetType(), "SetItem", 2,
            method => method.GetParameters()[0].ParameterType.Name == "Item_Base");
        if (setItem == null) throw new MissingMethodException("Slot.SetItem(Item_Base, int)");
        setItem.Invoke(slot, new[] { baseItem, (object)amount });
        object instance = GetValue(slot, "itemInstance");
        if (!SetValue(instance, "Uses", uses)) throw new MissingMemberException("ItemInstance.Uses");
        Invoke(slot, "RefreshComponents");
    }

    private static void Refresh(ContainerTarget target, params object[] slots)
    {
        foreach (object slot in slots)
            if (slot != null) Invoke(slot, "RefreshComponents");
        if (target.Storage != null) Invoke(target.Storage, "UpdateStorageFillRenderers");
    }

    private static ContainerTarget FindContainer(string key)
    {
        if (string.Equals(key, "player", StringComparison.Ordinal))
        {
            object inventory = FindPlayerInventory();
            IList slots = GetValue(inventory, "allSlots") as IList;
            return slots == null ? null : new ContainerTarget(inventory, null, slots);
        }
        if (key == null || !key.StartsWith("storage:", StringComparison.Ordinal) ||
            !uint.TryParse(key.Substring(8), out uint id)) return null;

        IList storages = GetStaticValue(GameType("StorageManager"), "allStorages") as IList;
        if (storages == null) return null;
        foreach (object storage in storages)
        {
            if (storage == null || Convert.ToUInt32(GetValue(storage, "ObjectIndex") ?? 0) != id) continue;
            object inventory = Invoke(storage, "GetInventoryReference");
            IList slots = GetValue(inventory, "allSlots") as IList;
            return slots == null ? null : new ContainerTarget(inventory, storage, slots);
        }
        return null;
    }

    private static bool ValidSlot(IList slots, int index) => slots != null && index >= 0 && index < slots.Count;
    private static int GetMaxStack(object baseItem) => Math.Max(1,
        Convert.ToInt32(GetValue(GetValue(baseItem, "settings_Inventory"), "StackSize") ?? 1));
    private static int GetMaxUses(object baseItem) => Math.Max(1, Convert.ToInt32(GetValue(baseItem, "MaxUses") ?? 1));

    private static object GetItemByIndex(int id)
    {
        Type type = GameType("ItemManager");
        MethodInfo method = type == null ? null : FindMethod(type, "GetItemByIndex", 1, null, true);
        return method?.Invoke(null, new object[] { id });
    }

    private static object FindObject(string typeName)
    {
        Type type = GameType(typeName);
        return type == null ? null : UnityEngine.Object.FindObjectOfType(type);
    }

    private static object FindPlayerInventory()
    {
        object network = FindObject("Raft_Network");
        object localPlayer = Invoke(network, "GetLocalPlayer");
        return GetValue(localPlayer, "Inventory");
    }

    private static Type GameType(string name) => Type.GetType($"{name}, Assembly-CSharp", false);

    private static object GetValue(object instance, string name)
    {
        if (instance == null) return null;
        for (Type type = instance.GetType(); type != null; type = type.BaseType)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return property.GetValue(instance, null);
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(instance);
        }
        return null;
    }

    private static object GetStaticValue(Type type, string name)
    {
        if (type == null) return null;
        PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null) return property.GetValue(null, null);
        return type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
    }

    private static bool SetValue(object instance, string name, object value)
    {
        if (instance == null) return false;
        for (Type type = instance.GetType(); type != null; type = type.BaseType)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.CanWrite == true) { property.SetValue(instance, value, null); return true; }
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) { field.SetValue(instance, value); return true; }
        }
        return false;
    }

    private static object Invoke(object instance, string name, params object[] arguments)
    {
        if (instance == null) return null;
        MethodInfo method = FindMethod(instance.GetType(), name, arguments.Length);
        return method?.Invoke(instance, arguments);
    }

    private static MethodInfo FindMethod(Type type, string name, int count,
        Func<MethodInfo, bool> predicate = null, bool isStatic = false)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                             (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        for (; type != null; type = type.BaseType)
        {
            MethodInfo method = type.GetMethods(flags).FirstOrDefault(candidate =>
                candidate.Name == name && candidate.GetParameters().Length == count &&
                (predicate == null || predicate(candidate)));
            if (method != null) return method;
        }
        return null;
    }

    private static Request ReadRequest(Stream stream)
    {
        BinaryReader reader = new(stream);
        if (reader.ReadInt32() != Magic) throw new InvalidDataException("Assinatura inválida.");
        if (reader.ReadInt32() != ProtocolVersion) throw new InvalidDataException("Versão incompatível.");
        Request request = new Request
        {
            Command = (Command)reader.ReadByte(),
            ContainerKey = reader.ReadString(),
            SlotIndex = reader.ReadInt32(),
            TargetSlotIndex = reader.ReadInt32(),
            ItemId = reader.ReadInt32(),
            Amount = reader.ReadInt32(),
            Uses = reader.ReadInt32(),
            ExpectedItemId = reader.ReadInt32()
        };
        if (request.Command == Command.AddItems)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 1000) throw new InvalidDataException("Quantidade inválida de itens.");
            request.Items = new List<RequestedItem>(count);
            for (int i = 0; i < count; i++)
                request.Items.Add(new RequestedItem { ItemId = reader.ReadInt32(), Amount = reader.ReadInt32() });
        }
        return request;
    }

    private static void WriteResponse(Stream stream, Command command, Response response)
    {
        BinaryWriter writer = new(stream);
        writer.Write(Magic);
        writer.Write(ProtocolVersion);
        writer.Write(response.Success);
        writer.Write(response.Message ?? string.Empty);
        if (command == Command.Snapshot && response.Success)
        {
            writer.Write(response.Containers.Count);
            foreach (ContainerData container in response.Containers)
            {
                writer.Write(container.Key);
                writer.Write(container.Name);
                writer.Write(container.IsPlayer);
                writer.Write(container.Capacity);
                writer.Write(container.Items.Count);
                foreach (ItemData item in container.Items)
                {
                    writer.Write(item.SlotIndex);
                    writer.Write(item.ItemId);
                    writer.Write(item.Amount);
                    writer.Write(item.Uses);
                    writer.Write(item.MaxStack);
                    writer.Write(item.MaxUses);
                    writer.Write(item.InternalName ?? string.Empty);
                }
            }
        }
        writer.Flush();
    }
}

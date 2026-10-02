using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Data;
using RaftSaveEditor.App.Infrastructure;
using RaftSaveEditor.App.Services;
using RaftSaveEditor.Core.Catalog;
using RaftSaveEditor.Core.Models;
using RaftSaveEditor.Core.Parsing;
using RaftSaveEditor.Core.Writing;

namespace RaftSaveEditor.App.ViewModels;

internal sealed class MainViewModel : ObservableObject
{
    private readonly RaftSaveParser _parser = new();
    private readonly RaftSaveWriter _writer = new();
    private readonly RaftItemCatalog _catalog = CreateCatalog();
    private readonly IDesktopService _desktop;
    private readonly AppLogger _logger;
    private readonly ItemPresetService _presetService = new();
    private readonly ObservableCollection<ItemRowViewModel> _items = [];
    private readonly ObservableCollection<TotalRowViewModel> _totals = [];
    private readonly ObservableCollection<ItemRowViewModel> _totalOccurrences = [];
    private readonly ObservableCollection<StorageViewModel> _storages = [];
    private readonly ObservableCollection<ItemRowViewModel> _storageItems = [];
    private readonly ObservableCollection<StorageViewModel> _playerStorages = [];
    private readonly ObservableCollection<ItemRowViewModel> _playerStorageItems = [];
    private readonly ObservableCollection<string> _typeFilters = ["Todos os tipos"];
    private readonly ObservableCollection<ItemPreset> _presets = [];
    private readonly List<SaveDocument> _documents = [];
    private readonly Dictionary<ItemSlot, SaveDocument> _documentBySlot = [];
    private SaveDocument? _document;
    private string _searchText = "";
    private string _selectedLocationFilter = "Todos os locais";
    private string _selectedTypeFilter = "Todos os tipos";
    private string _selectedVisibilityFilter = "Todos os itens";
    private string _selectedSort = "Nome";
    private bool _isBusy;
    private string _status = "Selecione um save para começar";
    private string _activePage = "Itens";
    private TotalRowViewModel? _selectedTotal;
    private StorageViewModel? _selectedStorage;
    private StorageViewModel? _selectedPlayerStorage;
    private ItemPreset? _selectedPreset;
    private string _connectorStatus = "Não consultado";
    private string _gameStatus = "Raft não detectado";
    private string _gameVersion = "—";
    private string _lastOperation = "Nenhuma operação nesta sessão";
    private string _lastError = "Nenhum erro nesta sessão";

    internal MainViewModel(IDesktopService desktop, AppLogger logger)
    {
        _desktop = desktop;
        _logger = logger;
        ItemsView = CollectionViewSource.GetDefaultView(_items);
        ItemsView.Filter = FilterItem;
        ApplySort();
        PlayerStorageItemsView = CollectionViewSource.GetDefaultView(_playerStorageItems);
        PlayerStorageItemsView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ItemRowViewModel.PlayerInventorySection)));

        OpenCommand = new AsyncRelayCommand(_ => OpenDetectedSaveAsync(), _ => !IsBusy);
        ReloadCommand = new AsyncRelayCommand(_ => ReloadAsync(), _ => IsLoaded && !IsBusy);
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => PendingChanges > 0 && !IsBusy);
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelectedItems(), _ => IsLoaded && !IsBusy);
        MaximizeStorageItemsCommand = new RelayCommand(_ => MaximizeStacks(_storageItems), _ => SelectedStorage is not null && !IsBusy);
        MaximizePlayerStorageItemsCommand = new RelayCommand(_ => MaximizeStacks(_playerStorageItems), _ => SelectedPlayerStorage is not null && !IsBusy);
        SelectMaterialsCommand = new RelayCommand(_ => SelectBuildingMaterials(), _ => IsLoaded);
        ClearSelectionCommand = new RelayCommand(_ => SetAllSelected(false), _ => IsLoaded);
        ExportSaveCopyCommand = new RelayCommand(_ => ExportSaveCopy(), _ => IsLoaded && !IsBusy);
        MaximizeSelectedStoragesCommand = new RelayCommand(_ => MaximizeSelectedStorages(), _ => IsLoaded && !IsBusy);
        OrganizeSelectedStoragesCommand = new RelayCommand(_ => OrganizeSelectedStorages(), _ => IsLoaded && !IsBusy);
        RemoveInvalidItemsCommand = new RelayCommand(_ => RemoveInvalidItems(), _ => IsLoaded && !IsBusy);
        ApplyPresetCommand = new RelayCommand(_ => ApplyPreset(), _ => IsLoaded && SelectedPreset is not null && !IsBusy);
        SaveCustomPresetCommand = new RelayCommand(_ => SaveCustomPreset(), _ => IsLoaded && !IsBusy);
        NavigateCommand = new RelayCommand(page => ActivePage = page as string ?? "Itens");
        ReloadPresets();
    }

    public ICollectionView ItemsView { get; }
    public ReadOnlyObservableCollection<TotalRowViewModel> Totals => new(_totals);
    public ReadOnlyObservableCollection<ItemRowViewModel> TotalOccurrences => new(_totalOccurrences);
    public ReadOnlyObservableCollection<StorageViewModel> Storages => new(_storages);
    public ReadOnlyObservableCollection<ItemRowViewModel> StorageItems => new(_storageItems);
    public ReadOnlyObservableCollection<StorageViewModel> PlayerStorages => new(_playerStorages);
    public ReadOnlyObservableCollection<ItemRowViewModel> PlayerStorageItems => new(_playerStorageItems);
    public ICollectionView PlayerStorageItemsView { get; }
    public ReadOnlyObservableCollection<string> TypeFilters => new(_typeFilters);
    public ReadOnlyObservableCollection<ItemPreset> Presets => new(_presets);
    public IReadOnlyList<string> LocationFilters { get; } = ["Todos os locais", "Inventário", "Hotbar", "Storages", "Player Storage"];
    public IReadOnlyList<string> VisibilityFilters { get; } = ["Todos os itens", "Somente alterados", "IDs desconhecidos"];
    public IReadOnlyList<string> SortOptions { get; } = ["Nome", "ID", "Quantidade", "Localização"];
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand MaximizeStorageItemsCommand { get; }
    public RelayCommand MaximizePlayerStorageItemsCommand { get; }
    public RelayCommand SelectMaterialsCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ExportSaveCopyCommand { get; }
    public RelayCommand MaximizeSelectedStoragesCommand { get; }
    public RelayCommand OrganizeSelectedStoragesCommand { get; }
    public RelayCommand RemoveInvalidItemsCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }
    public RelayCommand SaveCustomPresetCommand { get; }
    public RelayCommand NavigateCommand { get; }

    public bool IsLoaded => _document is not null;
    public bool IsWelcome => !IsLoaded;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) UpdateCommands(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string FileName => _document?.FileName ?? "";
    public string FilePath => _document?.Path ?? "";
    public string FileSize => _document is null ? "" : FormatSize(_document.FileSize);
    public string LastModified => _document?.LastWriteTime.ToString("dd/MM/yyyy HH:mm:ss") ?? "";
    public string RootType => _document?.RootType ?? "";
    public int ItemCount => _items.Count(item => item.ItemId > 0);
    public int StorageCount => _storages.Count;
    public string StorageSummary => StorageCount == 1 ? "1 storage detectado" : $"{StorageCount} storages detectados";
    public int PlayerStorageCount => _playerStorages.Count;
    public string PlayerStorageSummary => PlayerStorageCount == 1 ? "1 player storage detectado" : $"{PlayerStorageCount} player storages detectados";
    public int PendingChanges => _documents.Sum(document => document.PendingChanges);
    public string PendingText => PendingChanges == 1 ? "1 alteração pendente" : $"{PendingChanges} alterações pendentes";
    public string LogPath => _logger.Path;
    public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    public int CatalogItemCount => _catalog.Items.Count;
    public string EditorMode => IsLoaded ? "OFFLINE · edição do save" : "Nenhum save offline aberto";
    public string ConnectorStatus { get => _connectorStatus; private set => SetProperty(ref _connectorStatus, value); }
    public string GameStatus { get => _gameStatus; private set => SetProperty(ref _gameStatus, value); }
    public string GameVersion { get => _gameVersion; private set => SetProperty(ref _gameVersion, value); }
    public string LastOperation { get => _lastOperation; private set => SetProperty(ref _lastOperation, value); }
    public string LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ItemsView.Refresh(); } }
    public string SelectedLocationFilter { get => _selectedLocationFilter; set { if (SetProperty(ref _selectedLocationFilter, value)) ItemsView.Refresh(); } }
    public string SelectedTypeFilter { get => _selectedTypeFilter; set { if (SetProperty(ref _selectedTypeFilter, value)) ItemsView.Refresh(); } }
    public string SelectedVisibilityFilter { get => _selectedVisibilityFilter; set { if (SetProperty(ref _selectedVisibilityFilter, value)) ItemsView.Refresh(); } }
    public string SelectedSort { get => _selectedSort; set { if (SetProperty(ref _selectedSort, value)) ApplySort(); } }
    public string ActivePage { get => _activePage; set => SetProperty(ref _activePage, value); }
    public ItemPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value)) ApplyPresetCommand.RaiseCanExecuteChanged();
        }
    }

    public TotalRowViewModel? SelectedTotal
    {
        get => _selectedTotal;
        set
        {
            if (!SetProperty(ref _selectedTotal, value)) return;
            _totalOccurrences.Clear();
            if (value is not null)
                foreach (ItemRowViewModel item in value.Occurrences)
                    _totalOccurrences.Add(item);
        }
    }

    public StorageViewModel? SelectedStorage
    {
        get => _selectedStorage;
        set
        {
            if (!SetProperty(ref _selectedStorage, value)) return;
            _storageItems.Clear();
            if (value is not null)
                foreach (ItemRowViewModel item in value.Items.OrderBy(item => item.SlotIndex))
                    _storageItems.Add(item);
            MaximizeStorageItemsCommand.RaiseCanExecuteChanged();
        }
    }

    public StorageViewModel? SelectedPlayerStorage
    {
        get => _selectedPlayerStorage;
        set
        {
            if (!SetProperty(ref _selectedPlayerStorage, value)) return;
            _playerStorageItems.Clear();
            if (value is not null)
                foreach (ItemRowViewModel item in value.Items.OrderBy(item => item.SlotIndex))
                    _playerStorageItems.Add(item);
            MaximizePlayerStorageItemsCommand.RaiseCanExecuteChanged();
        }
    }

    internal async Task OpenDetectedSaveAsync()
    {
        IsBusy = true;
        Status = "Procurando saves do Raft...";
        IReadOnlyList<DetectedSave> saves;
        try
        {
            saves = await Task.Run(_desktop.DetectRaftSaves);
            _logger.Write($"Automatic save discovery found {saves.Count} file(s)");
        }
        catch (Exception ex)
        {
            _logger.Write($"Automatic save discovery failed: {ex}");
            saves = [];
        }
        finally { IsBusy = false; }

        string? path;
        if (saves.Count > 0)
        {
            path = _desktop.PickDetectedSave(saves);
        }
        else
        {
            _desktop.ShowInfo("Nenhum save detectado", "Nenhum save do Raft foi encontrado na pasta padrão. Você pode escolher um arquivo manualmente.");
            path = _desktop.PickSave();
        }
        if (path is not null) await LoadAsync(path);
    }

    internal async Task LoadAsync(string path)
    {
        IsBusy = true;
        Status = "Analisando records NRBF...";
        _logger.Write($"File selected: {path}");
        _logger.Write("NRBF parsing started");
        try
        {
            SaveDocument loaded = await Task.Run(() => _parser.Open(path));
            List<SaveDocument> loadedDocuments = [loaded];
            string? companionPlayerPath = loaded.RootType == "RGD_Game" ? FindCompanionPlayerSave(path) : null;
            if (companionPlayerPath is not null
                && !System.IO.Path.GetFullPath(companionPlayerPath).Equals(loaded.Path, StringComparison.OrdinalIgnoreCase))
            {
                Status = "Carregando Player Storage associado...";
                loadedDocuments.Add(await Task.Run(() => _parser.Open(companionPlayerPath)));
                _logger.Write($"Companion player save loaded: {companionPlayerPath}");
            }

            _document = loaded;
            _documents.Clear();
            _documents.AddRange(loadedDocuments);
            _documentBySlot.Clear();
            _items.Clear();
            foreach (SaveDocument document in loadedDocuments)
            {
                foreach (ItemSlot slot in document.AllSlots)
                    _documentBySlot[slot] = document;
                foreach (ItemSlot slot in document.Items)
                    _items.Add(new ItemRowViewModel(slot, _catalog));
            }
            RebuildTypeFilters();
            RebuildTotals();
            ItemsView.Refresh();
            Status = $"Saves carregados · {_items.Count} itens · {StorageCount} storages · {PlayerStorageCount} player storages";
            RecordOperation($"Save offline carregado às {DateTime.Now:HH:mm:ss}");
            _logger.Write($"Root object found: {loaded.RootType}");
            _logger.Write($"{loadedDocuments.Sum(document => document.AllSlots.Count)} item slots found ({_items.Count} occupied)");
            _logger.Write($"{StorageCount} storages resolved from owner records");
            _logger.Write($"{PlayerStorageCount} player storages resolved from owner records");
            NotifyDocumentChanged();
        }
        catch (Exception ex)
        {
            _logger.Write($"Open failed: {ex}");
            _desktop.ShowError("Não foi possível abrir o save", ex.Message);
            Status = "Falha ao abrir o arquivo";
            RecordError(ex.Message);
        }
        finally { IsBusy = false; }
    }

    internal bool EditItem(ItemRowViewModel row, int itemId, int amount, int uses, int slotIndex)
    {
        if (!_documentBySlot.TryGetValue(row.Slot, out SaveDocument? ownerDocument)) return false;
        (short ItemId, short Amount, short Uses, short SlotIndex) before = (row.Slot.ItemId, row.Slot.Amount, row.Slot.Uses, row.Slot.SlotIndex);
        try
        {
            ownerDocument.SetSlotValues(row.Slot, itemId, amount, uses, slotIndex);
            row.Refresh();
            FindContainer(row)?.Refresh();
            _logger.Write($"Item record {row.Slot.RecordId} changed in memory: ID {before.ItemId}->{itemId}, amount {before.Amount}->{amount}, uses {before.Uses}->{uses}, slot {before.SlotIndex}->{slotIndex}");
            RebuildTypeFilters();
            RebuildTotals();
            ItemsView.Refresh();
            PlayerStorageItemsView.Refresh();
            NotifyPendingChanged();
            return true;
        }
        catch (Exception ex)
        {
            _desktop.ShowError("Valores inválidos", ex.Message);
            return false;
        }
    }

    internal StorageViewModel? FindContainer(ItemRowViewModel row) =>
        _storages.Concat(_playerStorages)
            .FirstOrDefault(storage => storage.TechnicalOwner == row.TechnicalOwner);

    internal RaftItemDefinition? FindKnownItem(int itemId)
    {
        if (itemId is <= 0 or > short.MaxValue) return null;
        short id = checked((short)itemId);
        return _catalog.IsKnown(id) ? _catalog.Get(id) : null;
    }

    internal IReadOnlyList<RaftItemDefinition> KnownItems => _catalog.AddableItems;
    internal IReadOnlyList<RaftItemDefinition> CatalogItems => _catalog.Items;
    internal RaftItemCatalog Catalog => _catalog;
    internal IReadOnlyList<StorageViewModel> BatchStorages => GetBatchStorages();

    internal int AddItemsToBatchStorages(IReadOnlyList<ItemAddRequest> items)
    {
        Queue<ItemAddRequest> pending = new(items);
        StorageViewModel[] targets = GetBatchStorages();
        int added = 0;
        foreach (StorageViewModel storage in targets)
        {
            while (pending.Count > 0 && storage.EmptySlots > 0)
            {
                ItemAddRequest item = pending.Dequeue();
                if (AddItemToStorage(storage, item.ItemId, item.Amount)) added++;
            }
            if (pending.Count == 0) break;
        }
        Status = $"{added} item(ns) distribuído(s) em {targets.Length} storage(s) (ainda não salvo)";
        RecordOperation(Status);
        return added;
    }

    internal bool AddItemToStorage(StorageViewModel storage, short itemId, int? requestedAmount = null)
    {
        RaftItemDefinition? definition = FindKnownItem(itemId);
        if (definition is null)
        {
            _desktop.ShowError("Item desconhecido", $"O ID {itemId} não existe no catálogo de itens.");
            return false;
        }
        if (!definition.CanAddToInventory)
        {
            _desktop.ShowError("Item interno do jogo", $"{definition.Name} não é um item de inventário e não pode ser adicionado ao save.");
            return false;
        }
        if (!definition.HasKnownStackLimit || !definition.HasKnownUsesLimit)
        {
            _desktop.ShowError("Limites não confirmados", $"{definition.Name} não possui StackSize e Uses máximos confirmados.");
            return false;
        }
        int amount = requestedAmount ?? definition.StackSize;
        if (amount < 1 || amount > definition.StackSize)
        {
            _desktop.ShowError("Quantidade inválida", $"{definition.Name} permite quantidade de 1 a {definition.StackSize}.");
            return false;
        }

        ItemSlot[] ownedSlots = storage.Document.AllSlots
            .Where(slot => slot.TechnicalOwner == storage.TechnicalOwner)
            .ToArray();
        int occupiedSlots = ownedSlots.Count(slot => slot.ItemId > 0);
        if (occupiedSlots >= storage.SlotCount)
        {
            _desktop.ShowError("Storage cheio", $"{storage.Name} já ocupa todos os {storage.SlotCount} slots.");
            return false;
        }

        if (storage.Source.SlotArrayLengthOffset < 0 || storage.Source.SlotArrayEndOffset < 0)
        {
            _desktop.ShowError("Storage incompatível", "Não foi possível localizar com segurança o array de slots deste storage.");
            return false;
        }

        HashSet<short> usedIndexes = ownedSlots.Where(slot => slot.ItemId > 0).Select(slot => slot.SlotIndex).ToHashSet();
        int emptyIndex = Enumerable.Range(0, storage.SlotCount).First(index => !usedIndexes.Contains((short)index));
        ItemSlot? reusableSlot = ownedSlots.FirstOrDefault(slot => slot.ItemId <= 0 && slot.SlotIndex == emptyIndex);
        ItemSlot targetSlot;
        if (reusableSlot is not null)
        {
            storage.Document.SetSlotValues(
                reusableSlot,
                definition.Id,
                amount,
                definition.MaxUses,
                emptyIndex);
            targetSlot = reusableSlot;
        }
        else
        {
            targetSlot = storage.Document.AddSlot(
                storage.Source,
                definition.Id,
                amount,
                definition.MaxUses,
                emptyIndex);
        }

        ItemRowViewModel? row = _items.FirstOrDefault(item => ReferenceEquals(item.Slot, targetSlot));
        if (row is null)
        {
            row = new ItemRowViewModel(targetSlot, _catalog);
            _items.Add(row);
        }
        else
            row.Refresh();

        storage.Add(row);
        if (ReferenceEquals(storage, SelectedStorage) && !_storageItems.Contains(row))
            _storageItems.Add(row);
        if (ReferenceEquals(storage, SelectedPlayerStorage) && !_playerStorageItems.Contains(row))
            _playerStorageItems.Add(row);
        RebuildTypeFilters();
        RebuildTotals();
        ItemsView.Refresh();
        OnPropertyChanged(nameof(ItemCount));
        NotifyPendingChanged();
        Status = $"{definition.Name} adicionado a {storage.Name} · quantidade {amount} · Uses {definition.MaxUses} (ainda não salvo)";
        RecordOperation(Status);
        _logger.Write($"Item {definition.Id} added to {storage.Name}, slot {emptyIndex}, amount {amount}, uses {definition.MaxUses}");
        return true;
    }

    internal bool RemoveItem(ItemRowViewModel row)
    {
        if (!_documentBySlot.TryGetValue(row.Slot, out SaveDocument? ownerDocument) || row.ItemId <= 0)
            return false;

        string itemName = row.Name;
        StorageViewModel? container = FindContainer(row);
        ownerDocument.SetSlotValues(row.Slot, itemId: 0, amount: 0, uses: 0, slotIndex: row.SlotIndex);
        row.IsSelected = false;
        row.Refresh();
        container?.Remove(row);
        _storageItems.Remove(row);
        _playerStorageItems.Remove(row);
        RebuildTypeFilters();
        RebuildTotals();
        ItemsView.Refresh();
        OnPropertyChanged(nameof(ItemCount));
        NotifyPendingChanged();
        Status = $"{itemName} removido de {container?.Name ?? row.Location} (ainda não salvo)";
        RecordOperation(Status);
        _logger.Write($"Item record {row.Slot.RecordId} marked for removal from {row.TechnicalOwner}");
        return true;
    }

    private async Task ReloadAsync()
    {
        if (_document is null) return;
        if (PendingChanges > 0 && !_desktop.ConfirmSave("As alterações pendentes serão descartadas. Continuar?")) return;
        await LoadAsync(_document.Path);
    }

    private async Task SaveAsync()
    {
        if (_document is null || PendingChanges == 0) return;
        string details = BuildSaveSummary();
        if (!_desktop.ConfirmSave(details)) return;

        IsBusy = true;
        Status = "Validando e salvando...";
        try
        {
            SaveDocument[] changedDocuments = _documents.Where(document => document.PendingChanges > 0).ToArray();
            bool hasStructuralAdditions = changedDocuments.Any(document =>
                document.AllSlots.Any(slot => slot.IsSynthetic && slot.ItemId > 0));
            await Task.Run(() =>
            {
                foreach (SaveDocument document in changedDocuments)
                    _writer.Save(document);
            });
            if (hasStructuralAdditions)
            {
                string reloadPath = _document.Path;
                await LoadAsync(reloadPath);
                Status = "Itens adicionados, save atualizado e relido com sucesso";
                _logger.Write($"{changedDocuments.Length} file(s) saved and reloaded after structural slot insertion");
                _desktop.ShowInfo("Save atualizado", "Os itens foram adicionados aos slots vazios e o save foi relido com sucesso.");
                return;
            }
            foreach (ItemRowViewModel item in _items) item.Refresh();
            for (int index = _items.Count - 1; index >= 0; index--)
            {
                if (_items[index].ItemId <= 0)
                    _items.RemoveAt(index);
            }
            RebuildTypeFilters();
            RebuildTotals();
            ItemsView.Refresh();
            OnPropertyChanged(nameof(ItemCount));
            NotifyPendingChanged();
            Status = "Alterações salvas e verificadas";
            RecordOperation($"Save offline gravado às {DateTime.Now:HH:mm:ss}");
            _logger.Write($"{changedDocuments.Length} file(s) saved successfully");
            _desktop.ShowInfo("Save atualizado", changedDocuments.Length == 1
                ? "Alterações salvas com sucesso."
                : $"Alterações salvas com sucesso em {changedDocuments.Length} arquivos.");
        }
        catch (Exception ex)
        {
            Status = "Falha ao salvar";
            RecordError(ex.Message);
            _logger.Write($"Save failed: {ex}");
            _desktop.ShowError("Não foi possível salvar", ex.Message);
        }
        finally { IsBusy = false; }
    }

    private void DeleteSelectedItems()
    {
        if (_documents.Count == 0) return;
        ItemRowViewModel[] selected = _items
            .Where(item => item.IsSelected && item.ItemId > 0)
            .ToArray();
        if (selected.Length == 0)
        {
            _desktop.ShowError("Nenhum item selecionado", "Marque ao menos uma ocorrência na primeira coluna.");
            return;
        }

        foreach (ItemRowViewModel row in selected)
        {
            if (!_documentBySlot.TryGetValue(row.Slot, out SaveDocument? ownerDocument)) continue;
            StorageViewModel? container = FindContainer(row);
            ownerDocument.SetSlotValues(row.Slot, itemId: 0, amount: 0, uses: 0, slotIndex: row.SlotIndex);
            row.IsSelected = false;
            row.Refresh();
            container?.Remove(row);
            _storageItems.Remove(row);
            _playerStorageItems.Remove(row);
            _logger.Write($"Item record {row.Slot.RecordId} marked for removal from save");
        }
        RebuildTypeFilters();
        RebuildTotals();
        ItemsView.Refresh();
        OnPropertyChanged(nameof(ItemCount));
        NotifyPendingChanged();
        Status = selected.Length == 1
            ? "1 item removido (ainda não salvo)"
            : $"{selected.Length} itens removidos (ainda não salvo)";
    }

    private void MaximizeStacks(IEnumerable<ItemRowViewModel> source)
    {
        if (_documents.Count == 0) return;
        ItemRowViewModel[] storageItems = source
            .Where(item => item.ItemId > 0)
            .ToArray();
        if (storageItems.Length == 0)
        {
            _desktop.ShowInfo("Storage vazio", "Este storage não possui itens para maximizar.");
            return;
        }

        ItemRowViewModel[] itemsWithKnownLimit = storageItems
            .Where(item => item.MaxStackSize > 0)
            .ToArray();
        if (itemsWithKnownLimit.Length == 0)
        {
            _desktop.ShowInfo(
                "Limite não confirmado",
                "Nenhum item deste storage possui StackSize confirmado; nada foi alterado.");
            return;
        }

        int changed = 0;
        foreach (ItemRowViewModel row in itemsWithKnownLimit)
        {
            int maximum = row.MaxStackSize;
            short before = row.Slot.Amount;
            if (before == maximum) continue;

            if (_documentBySlot.TryGetValue(row.Slot, out SaveDocument? ownerDocument))
            {
                ownerDocument.SetAmount(row.Slot, maximum);
                changed++;
            }
            row.Refresh();
            _logger.Write($"Item {row.ItemId} quantity maximized in memory: {before} -> {maximum}");
        }

        RebuildTotals();
        ItemsView.Refresh();
        NotifyPendingChanged();

        int skippedUnknown = storageItems.Count(item => item.MaxStackSize <= 0);
        string skipped = BuildMaxStackStatusSuffix(skippedUnknown);
        Status = changed == 0
            ? $"Todos os {itemsWithKnownLimit.Length} itens deste storage já estavam no próprio StackSize máximo{skipped}"
            : $"{changed} itens deste storage colocados no máximo{skipped} (ainda não salvo)";
        RecordOperation(Status);
    }

    private StorageViewModel[] GetBatchStorages()
    {
        StorageViewModel[] marked = _storages.Where(storage => storage.IsSelected).ToArray();
        return marked.Length > 0 ? marked : SelectedStorage is null ? [] : [SelectedStorage];
    }

    private void MaximizeSelectedStorages()
    {
        StorageViewModel[] targets = GetBatchStorages();
        if (targets.Length == 0) return;
        MaximizeStacks(targets.SelectMany(storage => storage.Items).Distinct());
        RecordOperation($"Itens maximizados em {targets.Length} storage(s)");
    }

    private void OrganizeSelectedStorages()
    {
        StorageViewModel[] targets = GetBatchStorages();
        int changed = 0;
        foreach (StorageViewModel storage in targets)
        {
            ItemRowViewModel[] ordered = storage.Items
                .Where(item => item.ItemId > 0)
                .OrderBy(item => item.Type)
                .ThenBy(item => item.Name)
                .ThenBy(item => item.ItemId)
                .ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                ItemRowViewModel row = ordered[index];
                if (row.SlotIndex == index) continue;
                storage.Document.SetSlotValues(row.Slot, row.ItemId, row.Amount, row.Uses, index);
                row.Refresh();
                changed++;
            }
            storage.Refresh();
        }
        ItemsView.Refresh();
        PlayerStorageItemsView.Refresh();
        NotifyPendingChanged();
        Status = $"{changed} slot(s) reorganizados por categoria em {targets.Length} storage(s) (ainda não salvo)";
        RecordOperation(Status);
    }

    private void RemoveInvalidItems()
    {
        StorageViewModel[] targets = GetBatchStorages();
        ItemRowViewModel[] invalid = targets.SelectMany(storage => storage.Items)
            .Where(item => item.ItemId > 0 && (!item.IsKnown || !item.Definition.CanAddToInventory))
            .Distinct()
            .ToArray();
        if (invalid.Length == 0)
        {
            _desktop.ShowInfo("Nenhum item inválido", "Não foram encontrados itens desconhecidos ou não armazenáveis nos storages escolhidos.");
            return;
        }
        if (!_desktop.ConfirmSave($"Remover {invalid.Length} item(ns) inválido(s) dos storages escolhidos?\n\nA remoção ficará pendente até você salvar.")) return;
        foreach (ItemRowViewModel item in invalid) RemoveItem(item);
        Status = $"{invalid.Length} item(ns) inválido(s) removido(s) (ainda não salvo)";
        RecordOperation(Status);
    }

    private void ApplyPreset()
    {
        if (SelectedPreset is null) return;
        StorageViewModel[] targets = GetBatchStorages();
        int added = 0;
        foreach (StorageViewModel storage in targets)
        {
            int free = storage.EmptySlots;
            foreach (short id in SelectedPreset.ItemIds.Take(free))
                if (AddItemToStorage(storage, id)) added++;
        }
        Status = $"Preset {SelectedPreset.Name} aplicado: {added} item(ns) em {targets.Length} storage(s) (ainda não salvo)";
        RecordOperation(Status);
    }

    private void SaveCustomPreset()
    {
        short[] ids = _items.Where(item => item.IsSelected && item.ItemId > 0).Select(item => item.ItemId).Distinct().ToArray();
        if (ids.Length == 0)
        {
            _desktop.ShowInfo("Seleção vazia", "Marque os itens que deverão fazer parte do preset personalizado.");
            return;
        }
        string? name = _desktop.PromptText("Novo preset", "Nome do preset personalizado:", $"Meu preset {DateTime.Now:dd-MM}");
        if (name is null) return;
        ItemPreset saved = _presetService.SaveCustom(name, ids);
        ReloadPresets(saved.Name);
        Status = $"Preset {saved.Name} salvo com {saved.ItemIds.Count} item(ns)";
        RecordOperation(Status);
    }

    private void ReloadPresets(string? selectName = null)
    {
        string? previous = selectName ?? SelectedPreset?.Name;
        _presets.Clear();
        foreach (ItemPreset preset in _presetService.Load(_catalog)) _presets.Add(preset);
        SelectedPreset = _presets.FirstOrDefault(item => item.Name == previous) ?? _presets.FirstOrDefault();
    }

    private void ExportSaveCopy()
    {
        if (_document is null) return;
        try
        {
            string? exported = _desktop.ExportSaveCopy(_documents.Select(document => document.Path).ToArray(), Path.GetFileNameWithoutExtension(FileName));
            if (exported is null) return;
            Status = $"Cópia manual exportada: {exported}";
            RecordOperation(Status);
            _desktop.ShowInfo("Cópia exportada", $"O save foi copiado para:\n{exported}\n\nNenhum backup automático foi criado.");
        }
        catch (Exception ex)
        {
            RecordError(ex.Message);
            _desktop.ShowError("Não foi possível exportar", ex.Message);
        }
    }

    internal void UpdateLiveDiagnostics(string connectorStatus, string gameStatus, string gameVersion)
    {
        ConnectorStatus = connectorStatus;
        GameStatus = gameStatus;
        GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "—" : gameVersion;
    }

    internal void RecordOperation(string message)
    {
        LastOperation = message;
        _logger.Write(message);
    }

    internal void RecordError(string message)
    {
        LastError = $"{DateTime.Now:HH:mm:ss} · {message}";
        _logger.Write($"Error: {message}");
    }

    private static string BuildMaxStackStatusSuffix(int unknownLimitCount)
    {
        if (unknownLimitCount == 0) return "";
        return unknownLimitCount == 1
            ? " · 1 item sem limite confirmado ignorado"
            : $" · {unknownLimitCount} itens sem limite confirmado ignorados";
    }

    private void SelectBuildingMaterials()
    {
        int count = 0;
        foreach (ItemRowViewModel row in _items)
        {
            row.IsSelected = RaftItemCatalog.BuildingMaterials.Contains(row.ItemId);
            if (row.IsSelected) count++;
        }
        Status = $"{count} ocorrências de materiais de construção selecionadas";
    }

    private void SetAllSelected(bool selected)
    {
        foreach (ItemRowViewModel row in _items.Where(item => item.ItemId > 0))
            row.IsSelected = selected;
    }

    private bool FilterItem(object value)
    {
        if (value is not ItemRowViewModel item) return false;
        if (item.ItemId <= 0) return false;
        if (SelectedLocationFilter == "Inventário" && item.Location != "Player Inventory") return false;
        if (SelectedLocationFilter == "Hotbar" && item.Location != "Hotbar") return false;
        if (SelectedLocationFilter == "Storages" && !item.Location.StartsWith("Storage", StringComparison.OrdinalIgnoreCase)) return false;
        if (SelectedLocationFilter == "Player Storage"
            && !item.TechnicalOwner.StartsWith("RGD_PlayerInventory", StringComparison.Ordinal)) return false;
        if (SelectedTypeFilter != "Todos os tipos"
            && !item.Type.Equals(SelectedTypeFilter, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (SelectedVisibilityFilter == "Somente alterados" && !item.IsModified) return false;
        if (SelectedVisibilityFilter == "IDs desconhecidos" && item.IsKnown) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        string query = SearchText.Trim();
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || item.ItemId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Location.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ApplySort()
    {
        using IDisposable defer = ItemsView.DeferRefresh();
        ItemsView.SortDescriptions.Clear();
        string property = SelectedSort switch
        {
            "ID" => nameof(ItemRowViewModel.ItemId),
            "Quantidade" => nameof(ItemRowViewModel.Amount),
            "Localização" => nameof(ItemRowViewModel.Location),
            _ => nameof(ItemRowViewModel.Name)
        };
        ItemsView.SortDescriptions.Add(new SortDescription(property, ListSortDirection.Ascending));
    }

    private void RebuildTypeFilters()
    {
        string previous = SelectedTypeFilter;
        _typeFilters.Clear();
        _typeFilters.Add("Todos os tipos");
        foreach (string type in _items
                     .Where(item => item.ItemId > 0)
                     .Select(item => item.Type)
                     .Where(type => !string.IsNullOrWhiteSpace(type))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(type => type, StringComparer.CurrentCultureIgnoreCase))
        {
            _typeFilters.Add(type);
        }

        SelectedTypeFilter = _typeFilters.Contains(previous) ? previous : "Todos os tipos";
    }

    private void RebuildTotals()
    {
        short? selectedId = SelectedTotal?.ItemId;
        _totals.Clear();
        foreach (IGrouping<short, ItemRowViewModel> group in _items
                     .Where(item => item.ItemId > 0)
                     .GroupBy(item => item.ItemId)
                     .OrderBy(group => group.First().Name))
        {
            ItemRowViewModel[] occurrences = group.ToArray();
            _totals.Add(new TotalRowViewModel(group.Key, occurrences[0].Name, occurrences.Sum(item => item.Amount), occurrences));
        }
        SelectedTotal = selectedId is null ? _totals.FirstOrDefault() : _totals.FirstOrDefault(total => total.ItemId == selectedId);
        RebuildStorages();
    }

    private void RebuildStorages()
    {
        if (_document is null) return;
        string? selectedName = SelectedStorage?.Name;
        string? selectedPlayerName = SelectedPlayerStorage?.Name;
        HashSet<string> markedStorages = _storages.Where(storage => storage.IsSelected)
            .Select(storage => storage.TechnicalOwner).ToHashSet(StringComparer.Ordinal);
        Dictionary<ItemSlot, ItemRowViewModel> occupiedBySlot = _items
            .Where(item => item.ItemId > 0)
            .ToDictionary(item => item.Slot);
        _storages.Clear();
        _playerStorages.Clear();

        foreach (SaveDocument document in _documents)
        {
            BuildStorageCollection(document, document.Storages, _storages, occupiedBySlot);
            BuildStorageCollection(document, document.PlayerStorages, _playerStorages, occupiedBySlot);
        }

        foreach (StorageViewModel storage in _storages)
            storage.IsSelected = markedStorages.Contains(storage.TechnicalOwner);

        SelectedStorage = selectedName is null
            ? _storages.FirstOrDefault()
            : _storages.FirstOrDefault(storage => storage.Name == selectedName) ?? _storages.FirstOrDefault();
        SelectedPlayerStorage = selectedPlayerName is null
            ? _playerStorages.FirstOrDefault()
            : _playerStorages.FirstOrDefault(storage => storage.Name == selectedPlayerName) ?? _playerStorages.FirstOrDefault();
        OnPropertyChanged(nameof(StorageCount));
        OnPropertyChanged(nameof(StorageSummary));
        OnPropertyChanged(nameof(PlayerStorageCount));
        OnPropertyChanged(nameof(PlayerStorageSummary));
    }

    private void BuildStorageCollection(
        SaveDocument document,
        IEnumerable<SaveStorage> source,
        ObservableCollection<StorageViewModel> destination,
        IReadOnlyDictionary<ItemSlot, ItemRowViewModel> occupiedBySlot)
    {
        foreach (SaveStorage storage in source.OrderBy(storage => StorageNumber(storage.Name)))
        {
            ItemSlot[] ownedSlots = document.AllSlots
                .Where(slot => slot.TechnicalOwner == storage.TechnicalOwner)
                .ToArray();
            ItemRowViewModel[] contents = ownedSlots
                .Where(slot => slot.ItemId > 0 && occupiedBySlot.ContainsKey(slot))
                .Select(slot => occupiedBySlot[slot])
                .OrderBy(item => item.SlotIndex)
                .ToArray();
            int highestSerializedSlot = ownedSlots.Length == 0 ? 0 : ownedSlots.Max(slot => (int)slot.SlotIndex) + 1;
            destination.Add(new StorageViewModel(
                document,
                storage,
                storage.Name,
                Math.Max(storage.SlotCapacity, highestSerializedSlot),
                storage.TechnicalOwner,
                contents));
        }

    }

    private static int StorageNumber(string location)
    {
        int separator = location.LastIndexOf('#');
        return separator >= 0 && int.TryParse(location.AsSpan(separator + 1), out int number) ? number : int.MaxValue;
    }

    private static string? FindCompanionPlayerSave(string worldSavePath)
    {
        System.IO.DirectoryInfo? directory = new(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(worldSavePath))!);
        while (directory is not null)
        {
            string candidate = System.IO.Path.Combine(directory.FullName, "Player", "RGD_Users.rgd");
            if (System.IO.File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private string BuildSaveSummary()
    {
        ItemRowViewModel[] changes = _items.Where(item => item.IsModified).ToArray();
        StringBuilder text = new();
        text.AppendLine($"Salvar {changes.Length} alterações em {FileName}?").AppendLine();
        foreach (ItemRowViewModel item in changes.Take(14))
        {
            if (item.ItemId <= 0 && item.Slot.OriginalItemId > 0)
            {
                string originalName = _catalog.Get(item.Slot.OriginalItemId).Name;
                text.AppendLine($"Remover {originalName} · {item.Location} · ID {item.Slot.OriginalItemId} · slot {item.Slot.OriginalSlotIndex}");
            }
            else if (item.Slot.OriginalItemId <= 0 && item.ItemId > 0)
            {
                text.AppendLine($"Adicionar {item.Name} · {item.Location} · ID {item.ItemId} · quantidade {item.Amount} · uses {item.Uses} · slot {item.SlotIndex}");
            }
            else
            {
                text.AppendLine($"{item.Name} · {item.Location} · ID {item.Slot.OriginalItemId}→{item.ItemId}, quantidade {item.Slot.OriginalAmount}→{item.Amount}, uses {item.Slot.OriginalUses}→{item.Uses}, slot {item.Slot.OriginalSlotIndex}→{item.SlotIndex}");
            }
        }
        if (changes.Length > 14) text.AppendLine($"... e mais {changes.Length - 14} alterações");
        return text.ToString();
    }

    private void NotifyDocumentChanged()
    {
        OnPropertyChanged(nameof(IsLoaded)); OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(FilePath)); OnPropertyChanged(nameof(FileSize));
        OnPropertyChanged(nameof(LastModified)); OnPropertyChanged(nameof(RootType)); OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(StorageCount)); OnPropertyChanged(nameof(StorageSummary));
        OnPropertyChanged(nameof(PlayerStorageCount)); OnPropertyChanged(nameof(PlayerStorageSummary));
        OnPropertyChanged(nameof(EditorMode));
        NotifyPendingChanged(); UpdateCommands();
    }

    private void NotifyPendingChanged()
    {
        OnPropertyChanged(nameof(PendingChanges)); OnPropertyChanged(nameof(PendingText));
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void UpdateCommands()
    {
        OpenCommand.RaiseCanExecuteChanged(); ReloadCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged(); MaximizeStorageItemsCommand.RaiseCanExecuteChanged(); MaximizePlayerStorageItemsCommand.RaiseCanExecuteChanged();
        SelectMaterialsCommand.RaiseCanExecuteChanged(); ClearSelectionCommand.RaiseCanExecuteChanged();
        ExportSaveCopyCommand.RaiseCanExecuteChanged(); MaximizeSelectedStoragesCommand.RaiseCanExecuteChanged();
        OrganizeSelectedStoragesCommand.RaiseCanExecuteChanged(); RemoveInvalidItemsCommand.RaiseCanExecuteChanged();
        ApplyPresetCommand.RaiseCanExecuteChanged(); SaveCustomPresetCommand.RaiseCanExecuteChanged();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:N2} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes} bytes"
    };

    private static RaftItemCatalog CreateCatalog()
    {
        string overridePath = System.IO.Path.Combine(AppContext.BaseDirectory, "items.override.txt");
        return System.IO.File.Exists(overridePath)
            ? new RaftItemCatalog(RaftItemCatalog.LoadVersionedFile(overridePath))
            : new RaftItemCatalog();
    }
}

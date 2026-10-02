using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using RaftSaveEditor.App.ViewModels;
using RaftSaveEditor.App.Services;
using RaftSaveEditor.Core.Catalog;

namespace RaftSaveEditor.App;

public partial class AddItemWindow : Window
{
    private RaftItemDefinition? _definition;
    private readonly RaftItemDefinition[] _allItems;
    private readonly ObservableCollection<QueuedItemEntry> _queuedItems = [];
    private readonly int _emptySlots;
    private readonly bool _liveBatchMode;
    private readonly ItemPreferencesService _preferences = new();
    private double _expandedHeight = 980;

    internal AddItemWindow(MainViewModel viewModel, StorageViewModel storage)
        : this(viewModel.CatalogItems, storage.Name, storage.EmptySlots, storage.SlotCount)
    {
    }

    internal AddItemWindow(
        IReadOnlyList<RaftItemDefinition> knownItems,
        string containerName,
        int emptySlots,
        int slotCount,
        bool liveBatchMode = false)
    {
        InitializeComponent();
        _emptySlots = emptySlots;
        _liveBatchMode = liveBatchMode;
        _allItems = knownItems.ToArray();
        CategoryComboBox.ItemsSource = new[] { "Todas as categorias" }.Concat(
            _allItems.Select(item => item.Type).Where(type => !string.IsNullOrWhiteSpace(type)).Distinct().OrderBy(type => type)).ToArray();
        CategoryComboBox.SelectedIndex = 0;
        ScopeComboBox.ItemsSource = new[] { "Todos os itens", "Favoritos", "Usados recentemente" };
        ScopeComboBox.SelectedIndex = 0;
        ItemComboBox.ItemsSource = _allItems;
        StorageText.Text = $"{containerName} · {emptySlots} de {slotCount} slots livres";
        EmptySlotsText.Text = emptySlots.ToString();
        SearchResultText.Text = $"{_allItems.Length} itens disponíveis no catálogo local";
        QueuedItemsList.ItemsSource = _queuedItems;
        LiveBatchPanel.Visibility = liveBatchMode ? Visibility.Visible : Visibility.Collapsed;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        Height = Math.Min(_expandedHeight, MaxHeight);
        UpdateLiveControls();
        Loaded += (_, _) => ItemSearchTextBox.Focus();
    }

    internal short? NewItemId { get; private set; }
    internal int? NewItemAmount { get; private set; }
    internal IReadOnlyList<ItemAddRequest> NewItems { get; private set; } = [];
    internal IReadOnlyList<short> NewItemIds => NewItems.Select(item => item.ItemId).ToArray();

    private void ItemSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyCatalogFilters();
    }

    private void CatalogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) ApplyCatalogFilters();
    }

    private void ApplyCatalogFilters()
    {
        string query = ItemSearchTextBox.Text.Trim();
        string idQuery = query.StartsWith("id ", StringComparison.OrdinalIgnoreCase)
            ? query[3..].Trim()
            : query.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                ? query[3..].Trim()
                : query;

        string category = CategoryComboBox.SelectedItem as string ?? "Todas as categorias";
        string scope = ScopeComboBox.SelectedItem as string ?? "Todos os itens";
        IReadOnlyList<short> recent = _preferences.Recent;
        IEnumerable<RaftItemDefinition> filtered = _allItems
            .Where(item => category == "Todas as categorias" || item.Type.Equals(category, StringComparison.CurrentCultureIgnoreCase))
            .Where(item => scope switch
            {
                "Favoritos" => _preferences.IsFavorite(item.Id),
                "Usados recentemente" => recent.Contains(item.Id),
                _ => true
            });
        if (!string.IsNullOrWhiteSpace(query))
            filtered = filtered.Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                              item.Id.ToString(CultureInfo.InvariantCulture).Contains(idQuery, StringComparison.Ordinal));

        RaftItemDefinition[] matches = filtered
                .OrderByDescending(item => item.Id.ToString(CultureInfo.InvariantCulture) == idQuery)
                .ThenBy(item => scope == "Usados recentemente" ? Array.IndexOf(recent.ToArray(), item.Id) : int.MaxValue)
                .ThenBy(item => item.Name)
                .ToArray();

        ItemComboBox.ItemsSource = matches;
        _definition = null;
        FavoriteButton.IsEnabled = false;
        FavoriteButton.Content = "☆ Favoritar";
        UpdateLiveControls();
        SearchResultText.Text = matches.Length == 0
            ? "Nenhum item encontrado no catálogo local"
            : $"{matches.Length} item(ns) encontrado(s) no catálogo local";
        ShowInvalid(matches.Length == 0
            ? "Nenhum item encontrado com esse nome ou ID."
            : "Escolha um item na lista de resultados.");
    }

    private void ItemComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _definition = ItemComboBox.SelectedItem as RaftItemDefinition;

        if (_definition is null)
        {
            ShowInvalid("Selecione um item da lista.");
            return;
        }

        ItemNameText.Text = _definition.Name;
        ItemTypeText.Text = $"ID {_definition.Id} · {_definition.Type}";
        AmountTextBox.IsEnabled = IsValid(_definition);
        AmountTextBox.Text = _definition.HasKnownStackLimit ? _definition.StackSize.ToString() : "1";
        AmountLimitText.Text = _definition.HasKnownStackLimit ? $"/ {_definition.StackSize}" : "/ —";
        UsesText.Text = _definition.HasKnownUsesLimit ? _definition.MaxUses.ToString() : "—";
        ItemIcon.Source = _definition.IconFile is null
            ? null
            : new BitmapImage(new Uri($"/Assets/Items/{_definition.IconFile}", UriKind.Relative));

        bool valid = IsValid(_definition);
        FavoriteButton.IsEnabled = true;
        FavoriteButton.Content = _preferences.IsFavorite(_definition.Id) ? "★ Favorito" : "☆ Favoritar";
        UpdateLiveControls();
        ValidationText.Text = valid
            ? BuildItemNotice(_definition)
            : BuildInvalidNotice(_definition);
        ValidationText.Foreground = valid
            ? (System.Windows.Media.Brush)FindResource("MutedBrush")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 123, 114));
    }

    private void ShowInvalid(string message)
    {
        ItemNameText.Text = "Item não selecionado";
        ItemTypeText.Text = "";
        AmountTextBox.IsEnabled = false;
        AmountTextBox.Text = "1";
        AmountLimitText.Text = "/ —";
        UsesText.Text = "—";
        ItemIcon.Source = null;
        FavoriteButton.IsEnabled = false;
        UpdateLiveControls();
        ValidationText.Text = message;
        ValidationText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 123, 114));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_liveBatchMode)
        {
            if (_queuedItems.Count > 0)
            {
                foreach (QueuedItemEntry item in _queuedItems) item.ClampAmount();
                NewItems = _queuedItems.Select(item => new ItemAddRequest(item.Id, item.Amount)).ToArray();
                foreach (ItemAddRequest item in NewItems) _preferences.RecordRecent(item.ItemId);
            }
            else
            {
                if (!IsValid(_definition) || !TryGetSelectedAmount(out int amount)) return;
                int count = FillStorageCheckBox.IsChecked == true ? _emptySlots : 1;
                NewItems = Enumerable.Repeat(new ItemAddRequest(_definition!.Id, amount), count).ToArray();
                _preferences.RecordRecent(_definition!.Id);
            }
            DialogResult = true;
            return;
        }

        if (!IsValid(_definition) || !TryGetSelectedAmount(out int selectedAmount)) return;
        NewItemId = _definition!.Id;
        NewItemAmount = selectedAmount;
        _preferences.RecordRecent(_definition!.Id);
        DialogResult = true;
    }

    private void QueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (!IsValid(_definition) || !TryGetSelectedAmount(out int amount) || _queuedItems.Count >= _emptySlots || FillStorageCheckBox.IsChecked == true) return;
        _queuedItems.Add(new QueuedItemEntry(_definition!, amount));
        _preferences.RecordRecent(_definition!.Id);
        UpdateLiveControls();
    }

    private void RemoveQueuedItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: QueuedItemEntry item })
            _queuedItems.Remove(item);
        UpdateLiveControls();
    }

    private void FillStorageCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (FillStorageCheckBox.IsChecked == true && ActualHeight > 0)
            _expandedHeight = ActualHeight;

        UpdateLiveControls();
        if (FillStorageCheckBox.IsChecked == true)
        {
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            Height = Math.Min(_expandedHeight, MaxHeight);
        }
    }

    private void AmountTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateLiveControls();
    }

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void QueuedAmount_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: QueuedItemEntry item }) item.ClampAmount();
        QueuedItemsList.Items.Refresh();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        ItemSearchTextBox.Clear();
        CategoryComboBox.SelectedIndex = 0;
        ScopeComboBox.SelectedIndex = 0;
        ApplyCatalogFilters();
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_definition is null) return;
        _preferences.ToggleFavorite(_definition.Id);
        FavoriteButton.Content = _preferences.IsFavorite(_definition.Id) ? "★ Favorito" : "☆ Favoritar";
        if ((ScopeComboBox.SelectedItem as string) == "Favoritos") ApplyCatalogFilters();
    }

    private void UpdateLiveControls()
    {
        bool validSelection = IsValid(_definition) && TryGetSelectedAmount(out _);
        if (!_liveBatchMode)
        {
            AddButton.IsEnabled = validSelection;
            ActionSummaryText.Text = string.Empty;
            return;
        }

        bool hasQueue = _queuedItems.Count > 0;
        bool fill = FillStorageCheckBox.IsChecked == true;
        int selectedAmount = TryGetSelectedAmount(out int parsedAmount) ? parsedAmount : 0;
        FillStorageCheckBox.IsEnabled = !hasQueue;
        QueueBuilderPanel.Visibility = fill ? Visibility.Collapsed : Visibility.Visible;
        QueueItemButton.IsEnabled = validSelection && !fill && _queuedItems.Count < _emptySlots;
        QueuedItemsList.IsEnabled = !fill;
        AddButton.IsEnabled = hasQueue || validSelection;
        AddButton.Content = "Aplicar";
        ActionSummaryText.Text = hasQueue
            ? $"Adicionar {_queuedItems.Count} item(ns) da lista"
            : fill
                ? $"Encher {_emptySlots} slots com {selectedAmount} unidade(s) em cada"
                : $"Adicionar {selectedAmount} unidade(s) em 1 slot livre";
        QueueStatusText.Text = $"{_queuedItems.Count} de {_emptySlots} slots livres reservados pela lista" +
                               (_queuedItems.Count >= _emptySlots ? " · lista completa" : string.Empty);
    }

    private static bool IsValid(RaftItemDefinition? definition) =>
        definition is { CanAddToInventory: true, HasKnownStackLimit: true, HasKnownUsesLimit: true };

    private bool TryGetSelectedAmount(out int amount)
    {
        amount = 0;
        return _definition is { StackSize: > 0 }
               && int.TryParse(AmountTextBox.Text, out amount)
               && amount >= 1
               && amount <= _definition.StackSize;
    }

    private static string BuildItemNotice(RaftItemDefinition definition)
    {
        string prefix = definition.Type is "Blueprint" or "Building"
            ? "Atenção: este item pertence a conteúdo de construção/projeto. "
            : definition.Type.Contains("Quest", StringComparison.OrdinalIgnoreCase)
                ? "Atenção: item relacionado a missão. "
                : string.Empty;
        return prefix + "A quantidade pode ser escolhida entre 1 e o StackSize; Uses continuará no máximo do item.";
    }

    private static string BuildInvalidNotice(RaftItemDefinition definition) => !definition.CanAddToInventory
        ? "Este é um objeto interno/de construção e não pode ser colocado em inventários ou storages."
        : "Este item não possui StackSize e Uses máximos confirmados e não pode ser adicionado com segurança.";

    private sealed class QueuedItemEntry(RaftItemDefinition definition, int amount)
    {
        public short Id => definition.Id;
        public string Name => definition.Name;
        public string? IconPath => definition.IconPath;
        public int MaxAmount => definition.StackSize;
        public int Amount { get; set; } = amount;

        public void ClampAmount() => Amount = Math.Clamp(Amount, 1, MaxAmount);
    }

}

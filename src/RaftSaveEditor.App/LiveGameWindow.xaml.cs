using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RaftSaveEditor.App.Services;
using RaftSaveEditor.App.ViewModels;

namespace RaftSaveEditor.App;

public partial class LiveGameWindow : Window
{
    private readonly LiveGameClient _client;
    private readonly MainViewModel _mainViewModel;
    private readonly ObservableCollection<LiveContainerViewModel> _containers = [];
    private bool _busy;

    internal LiveGameWindow(LiveGameClient client, MainViewModel mainViewModel, LiveSnapshot initialSnapshot)
    {
        InitializeComponent();
        _client = client;
        _mainViewModel = mainViewModel;
        ContainersGrid.ItemsSource = _containers;
        ApplySnapshot(initialSnapshot, null);
    }

    private LiveContainerViewModel? SelectedContainer => ContainersGrid.SelectedItem as LiveContainerViewModel;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(string? preferredKey = null)
    {
        if (_busy) return;
        if (Process.GetProcessesByName("Raft").Length == 0)
        {
            ShowFailure("O Raft foi fechado. Abra o jogo novamente para continuar.");
            return;
        }
        SetBusy(true, "Atualizando diretamente do jogo...");
        try
        {
            preferredKey ??= SelectedContainer?.Key;
            LiveSnapshot snapshot = await _client.SnapshotAsync();
            ApplySnapshot(snapshot, preferredKey);
            StatusText.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex) { ShowFailure(ex.Message); }
        finally { SetBusy(false); }
    }

    private void ApplySnapshot(LiveSnapshot snapshot, string? preferredKey)
    {
        _containers.Clear();
        foreach (LiveContainerSnapshot container in snapshot.Containers.OrderByDescending(item => item.IsPlayer).ThenBy(item => item.Name))
            _containers.Add(new LiveContainerViewModel(container, _mainViewModel.Catalog));
        ContainersGrid.SelectedItem = preferredKey is null
            ? _containers.FirstOrDefault()
            : _containers.FirstOrDefault(container => container.Key == preferredKey) ?? _containers.FirstOrDefault();
        StatusText.Text = $"{snapshot.Containers.Count} inventários detectados no jogo.";
    }

    private void ContainersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LiveContainerViewModel? container = SelectedContainer;
        ContainerNameText.Text = container?.Name ?? "Selecione um inventário";
        OccupancyText.Text = container?.OccupancyText ?? string.Empty;
        TotalText.Text = container is null ? string.Empty : $"{container.TotalUnits} unidades no total";
        PlayerAreasText.Text = container?.PlayerAreaSummary ?? string.Empty;
        PlayerAreasText.Visibility = container?.IsPlayer == true ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = container is not null && container.EmptySlots > 0;
        MaxButton.IsEnabled = container is not null && container.OccupiedSlots > 0;
        RemoveButton.IsEnabled = false;
        if (container is null) { ItemsGrid.ItemsSource = null; return; }
        ICollectionView view = CollectionViewSource.GetDefaultView(container.Items);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LiveItemViewModel.Area)));
        ItemsGrid.ItemsSource = view;
    }

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = !_busy && ItemsGrid.SelectedItem is LiveItemViewModel;

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        LiveContainerViewModel? container = SelectedContainer;
        if (container is null || _busy) return;
        AddItemWindow dialog = new(_mainViewModel.CatalogItems, container.Name, container.EmptySlots, container.Capacity, liveBatchMode: true) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.NewItems.Count == 0) return;
        await MutateAsync(() => _client.AddItemsAsync(container.Key, dialog.NewItems), container.Key);
    }

    private async void Max_Click(object sender, RoutedEventArgs e)
    {
        LiveContainerViewModel? container = SelectedContainer;
        if (container is null || _busy) return;
        if (MessageBox.Show(this, $"Colocar todos os itens de {container.Name} no máximo agora?", "Alteração ao vivo",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await MutateAsync(() => _client.MaxContainerAsync(container.Key), container.Key);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ItemsGrid.SelectedItem is not LiveItemViewModel row) return;
        if (MessageBox.Show(this,
                $"Remover {row.Name} de {row.Container.Name}?",
                "Remover item ao vivo",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await MutateAsync(() => _client.RemoveItemAsync(row.Container.Key, row.SlotIndex, row.ItemId), row.Container.Key);
    }

    private void ItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LiveItemViewModel row }) _ = EditItemAsync(row);
    }

    private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsGrid.SelectedItem is LiveItemViewModel row) _ = EditItemAsync(row);
    }

    private async Task EditItemAsync(LiveItemViewModel row)
    {
        if (_busy) return;
        LiveEditItemWindow dialog = new(row, _mainViewModel.KnownItems) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.DeleteRequested)
        {
            await MutateAsync(() => _client.RemoveItemAsync(row.Container.Key, row.SlotIndex, row.ItemId), row.Container.Key);
            return;
        }
        if (dialog.NewItemId is int itemId && dialog.NewAmount is int amount && dialog.NewUses is int uses && dialog.NewSlotIndex is int targetSlot)
            await MutateAsync(() => _client.SetItemAsync(row.Container.Key, row.SlotIndex, targetSlot, itemId, amount, uses, row.ItemId), row.Container.Key);
    }

    private async Task MutateAsync(Func<Task<string>> action, string containerKey)
    {
        SetBusy(true, "Aplicando alteração no jogo...");
        try
        {
            string result = await action();
            LiveSnapshot snapshot = await _client.SnapshotAsync();
            ApplySnapshot(snapshot, containerKey);
            StatusText.Text = result;
            _mainViewModel.RecordOperation($"AO VIVO · {result}");
        }
        catch (Exception ex) { _mainViewModel.RecordError(ex.Message); ShowFailure(ex.Message); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        ContainersGrid.IsEnabled = !busy;
        AddButton.IsEnabled = !busy && SelectedContainer is { EmptySlots: > 0 };
        MaxButton.IsEnabled = !busy && SelectedContainer is { OccupiedSlots: > 0 };
        RemoveButton.IsEnabled = !busy && ItemsGrid.SelectedItem is LiveItemViewModel;
        if (status is not null) StatusText.Text = status;
    }

    private void ShowFailure(string message)
    {
        StatusText.Text = message;
        MessageBox.Show(this, message, "Conexão com o Raft", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

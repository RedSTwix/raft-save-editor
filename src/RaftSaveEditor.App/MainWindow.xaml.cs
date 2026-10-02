using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RaftSaveEditor.App.ViewModels;
using RaftSaveEditor.App.Services;

namespace RaftSaveEditor.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void DetectGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (Process.GetProcessesByName("Raft").Length == 0)
        {
            MessageBox.Show(this, "Abra o Raft e entre em um mundo antes de usar a edição ao vivo.",
                "Jogo não detectado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            LiveGameClient client = new();
            Process process = Process.GetProcessesByName("Raft").OrderByDescending(item => item.StartTime).First();
            LiveGameInjector injector = new(client);
            await injector.EnsureConnectedAsync(process);
            string connector = await client.PingAsync();
            string liveStatus = await client.StatusAsync();
            LiveSnapshot snapshot = await client.SnapshotAsync();
            viewModel.UpdateLiveDiagnostics(connector, $"{liveStatus} · {snapshot.Containers.Count} inventários", GetGameVersion(process));
            viewModel.RecordOperation($"Modo AO VIVO conectado às {DateTime.Now:HH:mm:ss}");
            LiveGameWindow window = new(client, viewModel, snapshot) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            viewModel.RecordError(ex.Message);
            MessageBox.Show(this, ex.Message, "Não foi possível conectar ao Raft",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        Process? process = Process.GetProcessesByName("Raft").OrderByDescending(item => item.StartTime).FirstOrDefault();
        if (process is null)
        {
            viewModel.UpdateLiveDiagnostics("Conector indisponível", "Raft não detectado", "—");
            return;
        }

        string connectorStatus;
        string gameStatus = "Raft aberto · mundo não confirmado";
        try
        {
            LiveGameClient client = new();
            connectorStatus = await client.PingAsync();
            string liveStatus = await client.StatusAsync();
            LiveSnapshot snapshot = await client.SnapshotAsync();
            gameStatus = $"{liveStatus} · {snapshot.Containers.Count} inventários";
        }
        catch (Exception ex)
        {
            connectorStatus = "Não conectado · use Detectar jogo aberto";
            viewModel.RecordError(ex.Message);
        }
        viewModel.UpdateLiveDiagnostics(connectorStatus, gameStatus, GetGameVersion(process));
    }

    private static string GetGameVersion(Process process)
    {
        try
        {
            string? file = process.MainModule?.FileName;
            return file is null ? "—" : FileVersionInfo.GetVersionInfo(file).FileVersion ?? "—";
        }
        catch
        {
            return "—";
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 1 && string.Equals(System.IO.Path.GetExtension(files[0]), ".rgd", StringComparison.OrdinalIgnoreCase))
            await viewModel.LoadAsync(files[0]);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length == 1 && string.Equals(System.IO.Path.GetExtension(files[0]), ".rgd", StringComparison.OrdinalIgnoreCase))
                e.Effects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private void ItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ItemRowViewModel row })
            EditItem(row);
    }

    private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: ItemRowViewModel row })
            EditItem(row);
    }

    private void EditItem(ItemRowViewModel row)
    {
        if (DataContext is not MainViewModel viewModel) return;
        EditItemWindow dialog = new(row) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.DeleteRequested)
        {
            viewModel.RemoveItem(row);
            return;
        }
        if (dialog.NewItemId is int itemId
            && dialog.NewAmount is int amount
            && dialog.NewUses is int uses
            && dialog.NewSlotIndex is int slotIndex)
            viewModel.EditItem(row, itemId, amount, uses, slotIndex);
    }

    private void LocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ItemRowViewModel row }
            || DataContext is not MainViewModel viewModel)
            return;

        StorageViewModel? storage = viewModel.FindContainer(row);
        if (storage is not null)
            ShowStorage(viewModel, storage);
    }

    private void StorageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StorageViewModel storage }
            && DataContext is MainViewModel viewModel)
            ShowStorage(viewModel, storage);
    }

    private void StoragesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: StorageViewModel storage }
            && DataContext is MainViewModel viewModel)
            ShowStorage(viewModel, storage);
    }

    private void ShowStorage(MainViewModel viewModel, StorageViewModel storage)
    {
        StorageWindow dialog = new(viewModel, storage) { Owner = this };
        dialog.ShowDialog();
    }

    private void AddItemToStorageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: StorageViewModel storage }
            || DataContext is not MainViewModel viewModel)
            return;

        AddItemWindow dialog = new(viewModel.CatalogItems, storage.Name, storage.EmptySlots, storage.SlotCount) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.NewItemId is short itemId && dialog.NewItemAmount is int amount)
            viewModel.AddItemToStorage(storage, itemId, amount);
    }

    private void AddItemsToSelectedStorages_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        IReadOnlyList<StorageViewModel> storages = viewModel.BatchStorages;
        int emptySlots = storages.Sum(storage => Math.Max(0, storage.EmptySlots));
        if (storages.Count == 0 || emptySlots == 0)
        {
            MessageBox.Show(this, "Marque ao menos um storage com slot livre.", "Storages sem espaço",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddItemWindow dialog = new(
            viewModel.CatalogItems,
            storages.Count == 1 ? storages[0].Name : $"{storages.Count} storages marcados",
            emptySlots,
            storages.Sum(storage => storage.SlotCount),
            liveBatchMode: true) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.NewItems.Count > 0)
            viewModel.AddItemsToBatchStorages(dialog.NewItems);
    }
}

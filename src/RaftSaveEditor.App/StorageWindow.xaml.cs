using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RaftSaveEditor.App.ViewModels;

namespace RaftSaveEditor.App;

public partial class StorageWindow : Window
{
    private readonly MainViewModel _mainViewModel;
    private readonly StorageViewModel _storage;

    internal StorageWindow(MainViewModel mainViewModel, StorageViewModel storage)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;
        _storage = storage;
        DataContext = storage;
        Title = $"{storage.Name} — Raft Save Editor";
        if (storage.TechnicalOwner.StartsWith("RGD_PlayerInventory", StringComparison.Ordinal))
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(storage.Items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemRowViewModel.PlayerInventorySection)));
            ItemsGrid.ItemsSource = view;
        }
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
        EditItemWindow dialog = new(row) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.DeleteRequested)
        {
            _mainViewModel.RemoveItem(row);
            _storage.Refresh();
            ItemsGrid.Items.Refresh();
            return;
        }
        if (dialog.NewItemId is not int itemId
            || dialog.NewAmount is not int amount
            || dialog.NewUses is not int uses
            || dialog.NewSlotIndex is not int slotIndex)
            return;

        if (_mainViewModel.EditItem(row, itemId, amount, uses, slotIndex))
        {
            _storage.Refresh();
            ItemsGrid.Items.Refresh();
        }
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        AddItemWindow dialog = new(_mainViewModel, _storage) { Owner = this };
        if (dialog.ShowDialog() == true
            && dialog.NewItemId is short itemId
            && dialog.NewItemAmount is int amount
            && _mainViewModel.AddItemToStorage(_storage, itemId, amount))
        {
            _storage.Refresh();
            ItemsGrid.Items.Refresh();
        }
    }
}

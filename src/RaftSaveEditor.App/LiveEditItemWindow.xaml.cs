using System.Windows;
using System.Windows.Media.Imaging;
using RaftSaveEditor.App.ViewModels;
using RaftSaveEditor.Core.Catalog;

namespace RaftSaveEditor.App;

public partial class LiveEditItemWindow : Window
{
    private readonly LiveItemViewModel _row;
    private bool _initializing;

    internal LiveEditItemWindow(LiveItemViewModel row, IReadOnlyList<RaftItemDefinition> knownItems)
    {
        _row = row;
        _initializing = true;
        InitializeComponent();
        ItemComboBox.ItemsSource = knownItems;
        ItemComboBox.SelectedItem = knownItems.FirstOrDefault(item => item.Id == row.ItemId);
        AmountTextBox.Text = row.Amount.ToString();
        UsesTextBox.Text = row.Uses.ToString();
        SlotTextBox.Text = row.SlotIndex.ToString();
        LocationText.Text = $"{row.Container.Name} · {row.Area} · {row.DisplaySlot}";
        ShowDefinition(row.Definition);
        _initializing = false;
    }

    internal int? NewAmount { get; private set; }
    internal int? NewItemId { get; private set; }
    internal int? NewUses { get; private set; }
    internal int? NewSlotIndex { get; private set; }
    internal bool DeleteRequested { get; private set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ItemComboBox.SelectedItem is not RaftItemDefinition definition ||
            !Read(AmountTextBox.Text, out int amount) || !Read(UsesTextBox.Text, out int uses) ||
            !Read(SlotTextBox.Text, out int slot))
        {
            ValidationText.Text = "Selecione um item e informe somente números válidos.";
            return;
        }
        if (amount is < 1 || amount > definition.StackSize)
        {
            ValidationText.Text = $"{definition.Name} permite quantidade de 1 a {definition.StackSize}.";
            return;
        }
        if (uses < 0 || uses > definition.MaxUses)
        {
            ValidationText.Text = $"{definition.Name} permite Uses de 0 a {definition.MaxUses}.";
            return;
        }
        NewItemId = definition.Id;
        NewAmount = amount;
        NewUses = uses;
        NewSlotIndex = slot;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Remover {_row.Name} de {_row.Container.Name}?", "Excluir item ao vivo",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        DeleteRequested = true;
        DialogResult = true;
    }

    private void ItemComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ItemComboBox.SelectedItem is not RaftItemDefinition definition) return;
        ShowDefinition(definition);
        if (_initializing) return;
        AmountTextBox.Text = definition.StackSize.ToString();
        UsesTextBox.Text = definition.MaxUses.ToString();
    }

    private void ShowDefinition(RaftItemDefinition definition)
    {
        ItemNameText.Text = definition.Name;
        ItemIdText.Text = $"ID {definition.Id}";
        TypeText.Text = definition.Type;
        LimitsText.Text = $"Stack {definition.StackSize} · Uses {definition.MaxUses}";
        ItemIcon.Source = definition.IconFile is null ? null :
            new BitmapImage(new Uri($"/Assets/Items/{definition.IconFile}", UriKind.Relative));
    }

    private static bool Read(string text, out int value) => int.TryParse(text, out value) && value is >= 0 and <= short.MaxValue;
}

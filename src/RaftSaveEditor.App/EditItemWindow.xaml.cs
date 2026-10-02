using System.Windows;
using System.Windows.Input;
using RaftSaveEditor.App.ViewModels;
using RaftSaveEditor.Core.Catalog;

namespace RaftSaveEditor.App;

public partial class EditItemWindow : Window
{
    private readonly ItemRowViewModel _row;
    private bool _initializing;

    internal EditItemWindow(ItemRowViewModel row)
    {
        _row = row;
        _initializing = true;
        InitializeComponent();
        ItemNameText.Text = row.Name;
        ItemIdText.Text = $"Registro NRBF #{row.Slot.RecordId}";
        ItemComboBox.ItemsSource = row.KnownItems;
        ItemComboBox.SelectedItem = row.KnownItems.FirstOrDefault(item => item.Id == row.ItemId);
        AmountTextBox.Text = row.Amount.ToString();
        UsesTextBox.Text = row.Uses.ToString();
        SlotTextBox.Text = row.SlotIndex.ToString();
        LocationText.Text = row.Location;
        ShowDefinition(row.Definition);
        _initializing = false;
        Loaded += (_, _) => { AmountTextBox.Focus(); AmountTextBox.SelectAll(); };
    }

    internal int? NewAmount { get; private set; }
    internal int? NewItemId { get; private set; }
    internal int? NewUses { get; private set; }
    internal int? NewSlotIndex { get; private set; }
    internal bool DeleteRequested { get; private set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ItemComboBox.SelectedItem is not RaftItemDefinition definition
            || !TryReadValue(AmountTextBox.Text, out int amount)
            || !TryReadValue(UsesTextBox.Text, out int uses)
            || !TryReadValue(SlotTextBox.Text, out int slotIndex))
        {
            ValidationText.Text = "Selecione um item da lista. Quantidade, Uses e Slot devem ser números inteiros entre 0 e 32767.";
            return;
        }
        if (definition.HasKnownStackLimit && amount > definition.StackSize)
        {
            ValidationText.Text = $"{definition.Name} permite no máximo {definition.StackSize} por stack.";
            return;
        }
        if (definition.HasKnownUsesLimit && uses > definition.MaxUses)
        {
            ValidationText.Text = $"{definition.Name} permite no máximo {definition.MaxUses} Uses.";
            return;
        }
        NewItemId = definition.Id;
        NewAmount = amount;
        NewUses = uses;
        NewSlotIndex = slotIndex;
        DialogResult = true;
    }

    private static bool TryReadValue(string text, out int value) =>
        int.TryParse(text, out value) && value is >= 0 and <= short.MaxValue;

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Remover {_row.Name} de {_row.Location}?", "Excluir item",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DeleteRequested = true;
        DialogResult = true;
    }

    private void ItemComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ItemComboBox.SelectedItem is not RaftItemDefinition definition) return;
        ShowDefinition(definition);
        if (_initializing) return;
        if (definition.HasKnownStackLimit) AmountTextBox.Text = definition.StackSize.ToString();
        if (definition.HasKnownUsesLimit) UsesTextBox.Text = definition.MaxUses.ToString();
        ValidationText.Text = "Quantidade e Uses foram ajustados aos máximos do item selecionado; você ainda pode reduzi-los.";
    }

    private void ShowDefinition(RaftItemDefinition definition)
    {
        ItemNameText.Text = definition.Name;
        TypeText.Text = $"Tipo: {definition.Type}";
        string stack = definition.HasKnownStackLimit ? definition.StackSize.ToString() : "não verificado";
        string uses = definition.HasKnownUsesLimit ? definition.MaxUses.ToString() : "não verificado";
        LimitsText.Text = $"Stack: {stack}  ·  Uses: {uses}";
        ItemIcon.Source = definition.IconFile is null
            ? null
            : new System.Windows.Media.Imaging.BitmapImage(new Uri($"/Assets/Items/{definition.IconFile}", UriKind.Relative));
    }

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
}

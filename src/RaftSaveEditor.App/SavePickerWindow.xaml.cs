using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using RaftSaveEditor.App.Services;

namespace RaftSaveEditor.App;

public partial class SavePickerWindow : Window
{
    internal SavePickerWindow(IReadOnlyList<DetectedSave> saves)
    {
        InitializeComponent();
        SavesGrid.ItemsSource = saves;
        SavesGrid.SelectedIndex = saves.Count > 0 ? 0 : -1;
    }

    internal string? SelectedPath { get; private set; }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void SavesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (SavesGrid.SelectedItem is not DetectedSave save) return;
        SelectedPath = save.Path;
        DialogResult = true;
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Selecionar save do Raft",
            Filter = "Raft Save (*.rgd)|*.rgd|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        SelectedPath = dialog.FileName;
        DialogResult = true;
    }
}

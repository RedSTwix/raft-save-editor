using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace RaftSaveEditor.App.Services;

internal interface IDesktopService
{
    IReadOnlyList<DetectedSave> DetectRaftSaves();
    string? PickDetectedSave(IReadOnlyList<DetectedSave> saves);
    string? PickSave();
    string? PromptText(string title, string message, string initialValue);
    string? ExportSaveCopy(IReadOnlyList<string> sourceFiles, string suggestedName);
    bool ConfirmSave(string details);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
}

internal sealed class DesktopService : IDesktopService
{
    public IReadOnlyList<DetectedSave> DetectRaftSaves()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            local, "..", "LocalLow", "Redbeet Interactive", "Raft", "User"));
        if (!System.IO.Directory.Exists(root)) return [];

        List<DetectedSave> saves = [];
        foreach (string path in System.IO.Directory.EnumerateFiles(root, "*.rgd", System.IO.SearchOption.AllDirectories))
        {
            string fileName = System.IO.Path.GetFileName(path);
            if (fileName.Equals("Settings.rgd", StringComparison.OrdinalIgnoreCase)) continue;
            if (fileName.Contains(".backup-", StringComparison.OrdinalIgnoreCase)) continue;

            System.IO.FileInfo info = new(path);
            string relative = System.IO.Path.GetRelativePath(root, path);
            string[] parts = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string type;
            string description;
            if (parts.Contains("World", StringComparer.OrdinalIgnoreCase))
            {
                int worldIndex = Array.FindIndex(parts, part => part.Equals("World", StringComparison.OrdinalIgnoreCase));
                string mode = worldIndex >= 0 && worldIndex + 1 < parts.Length ? parts[worldIndex + 1] : "desconhecido";
                string folder = worldIndex >= 0 && worldIndex + 2 < parts.Length ? parts[worldIndex + 2] : "";
                type = "Mundo";
                description = string.IsNullOrEmpty(folder) ? $"Modo: {mode}" : $"Modo: {mode} · {folder}";
            }
            else if (fileName.Equals("RGD_Users.rgd", StringComparison.OrdinalIgnoreCase))
            {
                type = "Jogadores";
                description = "Inventários e dados dos jogadores";
            }
            else
            {
                type = "Outro save";
                description = relative;
            }

            saves.Add(new DetectedSave(
                path,
                System.IO.Path.GetFileNameWithoutExtension(path),
                type,
                description,
                info.Length,
                info.LastWriteTime));
        }

        return saves.OrderByDescending(save => save.LastModified).ThenBy(save => save.Name).ToArray();
    }

    public string? PickDetectedSave(IReadOnlyList<DetectedSave> saves)
    {
        SavePickerWindow dialog = new(saves) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.SelectedPath : null;
    }

    public string? PickSave()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Selecionar save do Raft",
            Filter = "Raft Save (*.rgd)|*.rgd|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PromptText(string title, string message, string initialValue)
    {
        TextBox input = new() { Text = initialValue, MinWidth = 330, Margin = new Thickness(0, 8, 0, 14) };
        Button ok = new() { Content = "Salvar", IsDefault = true, MinWidth = 95, Margin = new Thickness(8, 0, 0, 0) };
        Button cancel = new() { Content = "Cancelar", IsCancel = true, MinWidth = 95 };
        Window dialog = new()
        {
            Title = title,
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                    input,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } }
                }
            }
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        input.SelectAll();
        input.Focus();
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }

    public string? ExportSaveCopy(IReadOnlyList<string> sourceFiles, string suggestedName)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Exportar cópia manual do save",
            Filter = "Arquivo ZIP (*.zip)|*.zip",
            FileName = $"{suggestedName}-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog() != true) return null;

        using FileStream output = new(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sourceFiles.Where(File.Exists))
        {
            string entryName = Path.GetFileName(source);
            if (!names.Add(entryName))
                entryName = $"{Path.GetFileName(Path.GetDirectoryName(source))}-{entryName}";
            archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
        }
        return dialog.FileName;
    }

    public bool ConfirmSave(string details) => MessageBox.Show(
        details,
        "Confirmar alterações",
        MessageBoxButton.OKCancel,
        MessageBoxImage.Warning,
        MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}

internal sealed record DetectedSave(
    string Path,
    string Name,
    string Type,
    string Description,
    long Size,
    DateTime LastModified)
{
    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:N2} MB"
        : $"{Size / 1024d:N1} KB";
    public string LastModifiedText => LastModified.ToString("dd/MM/yyyy HH:mm");
}

internal sealed class AppLogger
{
    private readonly string _path;
    private readonly object _gate = new();

    internal AppLogger()
    {
        string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaftSaveEditor");
        System.IO.Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "raft-save-editor.log");
    }

    internal string Path => _path;

    internal void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        lock (_gate)
            System.IO.File.AppendAllText(_path, line, new UTF8Encoding(false));
    }
}

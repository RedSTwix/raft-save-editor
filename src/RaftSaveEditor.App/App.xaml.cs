using System.Configuration;
using System.Windows;
using RaftSaveEditor.App.Services;
using RaftSaveEditor.App.ViewModels;

namespace RaftSaveEditor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private AppLogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _logger = new AppLogger();
        DispatcherUnhandledException += (_, args) =>
        {
            _logger.Write($"Unhandled UI error: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        MainViewModel viewModel = new(new DesktopService(), _logger);
        MainWindow window = new() { DataContext = viewModel };
        MainWindow = window;
        window.Show();
        if (e.Args.Length == 1)
            _ = viewModel.LoadAsync(e.Args[0]);
    }
}

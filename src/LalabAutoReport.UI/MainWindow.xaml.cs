using System.ComponentModel;
using System.Windows;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.UI.ViewModels;

namespace LalabAutoReport.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISystemTrayManager? _trayManager;
    private readonly ISettingsRepository? _settingsRepository;
    private bool _isExplicitExit = false;

    public MainWindow(
        MainViewModel viewModel,
        ISystemTrayManager? trayManager = null,
        ISettingsRepository? settingsRepository = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _trayManager = trayManager;
        _settingsRepository = settingsRepository;
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    public void RequestExplicitExit()
    {
        _isExplicitExit = true;
        _trayManager?.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    public void RestoreAndActivate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExplicitExit)
        {
            _trayManager?.Dispose();
            return;
        }

        bool minimizeToTray = true;
        try
        {
            var settingsRepo = _settingsRepository;
            // Run the read on the thread pool: blocking the UI thread on an awaited call can deadlock on close.
            var settings = settingsRepo == null ? null : Task.Run(() => settingsRepo.GetSettingsAsync()).GetAwaiter().GetResult();
            if (settings != null)
            {
                minimizeToTray = settings.MinimizeToTrayOnClose;
            }
        }
        catch
        {
            minimizeToTray = true;
        }

        if (minimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayManager?.ShowFirstTimeMinimizedNotification();
        }
        else
        {
            _trayManager?.Dispose();
        }
    }
}
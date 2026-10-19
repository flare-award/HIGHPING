using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using LatencyStudio.App.Infrastructure;
using LatencyStudio.App.ViewModels;
using LatencyStudio.Core.Runtime;

namespace LatencyStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly bool _startInTray;
    private readonly DispatcherTimer _refreshTimer;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayToggleItem;
    private DrawingIcon? _trayImage;
    private bool _trayImageIsShared;
    private bool _trayDisposed;

    internal MainWindow(MainViewModel viewModel, bool startInTray)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _startInTray = startInTray;
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ElevationRequested += OnElevationRequested;
        SetupTrayIcon();
        _viewModel.RefreshSnapshot();
        _refreshTimer.Start();
        if (_startInTray)
        {
            Hide();
        }
    }

    private void SetupTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "latency-studio.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                using var stream = File.OpenRead(iconPath);
                Icon = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                _trayImage = new DrawingIcon(iconPath);
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Не удалось загрузить значок приложения; используется системный значок.", exception);
                _trayImage = DrawingSystemIcons.Application;
                _trayImageIsShared = true;
            }
        }
        else
        {
            _trayImage = DrawingSystemIcons.Application;
            _trayImageIsShared = true;
        }

        _trayMenu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Открыть Latency Studio", null, (_, _) => ShowWindow());
        _trayToggleItem = new Forms.ToolStripMenuItem("Включить задержку", null, (_, _) => _viewModel.ToggleDelayCommand.Execute(null));
        var exitItem = new Forms.ToolStripMenuItem("Завершить программу", null, (_, _) => _ = ((App)System.Windows.Application.Current).RequestExitAsync());
        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(_trayToggleItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);
        _trayMenu.Opening += (_, _) => UpdateTrayMenu();

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayImage,
            Text = "Latency Studio — задержка выключена",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_notifyIcon is null || _trayToggleItem is null)
        {
            return;
        }

        var state = _viewModel.EngineSnapshot.State;
        _trayToggleItem.Text = state switch
        {
            TrafficEngineState.Running => "Выключить задержку",
            TrafficEngineState.Starting => "Обработчик запускается…",
            TrafficEngineState.Stopping => "Обработчик останавливается…",
            TrafficEngineState.Error when _viewModel.HasRuntime => "Остановить обработчик",
            _ => "Включить задержку"
        };
        _trayToggleItem.Enabled = state is not TrafficEngineState.Starting and not TrafficEngineState.Stopping;

        var tooltip = _viewModel.IsDelayActive
            ? $"Latency Studio — задержка включена ({_viewModel.ModeText})"
            : state == TrafficEngineState.Error
                ? "Latency Studio — ошибка обработчика"
                : "Latency Studio — задержка выключена";
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        _viewModel.RefreshSnapshot();
        UpdateTrayMenu();
    }

    private void ShowWindow()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async void OnElevationRequested(object? sender, EventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            this,
            "Чтобы задерживать сетевые пакеты всех приложений, Windows должна загрузить подписанный драйвер WinDivert. Это требует прав администратора.\n\nСейчас откроется штатный запрос UAC. Latency Studio не отключает защиту Windows и не меняет настройки сетевых адаптеров. Продолжить?",
            "Требуются права администратора",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _viewModel.SaveNowAsync();
            _ = WindowsStartupManager.StartElevatedEnable();
            await ((App)System.Windows.Application.Current).RequestExitAsync();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            System.Windows.MessageBox.Show(this, "Запрос UAC отменён. Задержка не включена.", "Latency Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Не удалось перезапустить приложение с повышением прав.", exception);
            System.Windows.MessageBox.Show(this, "Не удалось открыть повышенный процесс.\n\n" + exception.Message, "Latency Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App app && !app.IsExiting)
        {
            if (_viewModel.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            e.Cancel = true;
            _ = app.RequestExitAsync();
            return;
        }

        _refreshTimer.Stop();
        _viewModel.ElevationRequested -= OnElevationRequested;
        DisposeTrayIcon();
    }

    private void DisposeTrayIcon()
    {
        if (_trayDisposed)
        {
            return;
        }

        _trayDisposed = true;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        if (!_trayImageIsShared)
        {
            _trayImage?.Dispose();
        }

        _trayImage = null;
    }
}

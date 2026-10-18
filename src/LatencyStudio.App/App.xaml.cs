using System.Windows;
using System.Windows.Threading;
using LatencyStudio.App.Infrastructure;
using LatencyStudio.App.ViewModels;
using LatencyStudio.Core.Configuration;
using LatencyStudio.Core.Runtime;

namespace LatencyStudio.App;

public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private SettingsStore? _settingsStore;
    private TrafficEngineController? _engineController;
    private MainViewModel? _viewModel;
    private int _exitRequested;

    internal bool IsExiting { get; private set; }
    internal MainViewModel? ViewModel => _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var elevatedEnable = e.Args.Contains("--elevated-enable", StringComparer.OrdinalIgnoreCase);
            _instanceGuard = new SingleInstanceGuard();
            if (!_instanceGuard.TryAcquire(waitForPreviousInstance: elevatedEnable))
            {
                MessageBox.Show(
                    "Latency Studio уже запущена. Откройте существующее окно из системного трея.",
                    "Latency Studio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            _settingsStore = new SettingsStore();
            _engineController = new TrafficEngineController(static () => new WinDivertRuntime());
            _viewModel = new MainViewModel(_settingsStore, _engineController);
            MainWindow = new MainWindow(_viewModel, startInTray: e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
            MainWindow.Show();

            if (e.Args.Contains("--elevated-enable", StringComparer.OrdinalIgnoreCase))
            {
                MainWindow.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => _viewModel.ToggleDelayCommand.Execute(null)));
            }

            AppLogger.Info($"Latency Studio {_viewModel.AppVersionText} started; administrator={WindowsStartupManager.IsAdministrator()}.");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Ошибка запуска приложения.", exception);
            MessageBox.Show(
                $"Не удалось запустить Latency Studio.\n\n{exception.Message}",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            IsExiting = true;
            Shutdown();
        }
    }

    internal async Task RequestExitAsync()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
        {
            return;
        }

        IsExiting = true;
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.SaveNowAsync();
            }

            if (_engineController is not null)
            {
                await _engineController.DisableAsync();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Ошибка при штатном завершении приложения.", exception);
            MessageBox.Show(
                "При остановке сетевого обработчика возникла ошибка. Windows закроет дескриптор WinDivert при завершении процесса.\n\n" + exception.Message,
                "Latency Studio — предупреждение",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        try
        {
            _engineController?.DisableAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Последняя попытка остановить обработчик при выходе завершилась ошибкой.", exception);
        }

        try
        {
            _viewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Не удалось завершить очистку пользовательских настроек.", exception);
        }

        try
        {
            _engineController?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Не удалось освободить сетевой обработчик.", exception);
        }

        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Необработанная ошибка интерфейса.", e.Exception);
        MessageBox.Show(
            "Произошла ошибка интерфейса. Подробности записаны в журнал диагностики.\n\n" + e.Exception.Message,
            "Latency Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLogger.Error("Необработанная ошибка процесса.", exception);
        }
        else
        {
            AppLogger.Error("Процесс завершился из-за неизвестного исключения.");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Необработанная ошибка фоновой задачи.", e.Exception);
        e.SetObserved();
    }
}

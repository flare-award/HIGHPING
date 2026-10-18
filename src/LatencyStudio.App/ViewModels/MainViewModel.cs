using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using LatencyStudio.App.Infrastructure;
using LatencyStudio.Core.Configuration;
using LatencyStudio.Core.Latency;
using LatencyStudio.Core.Runtime;

namespace LatencyStudio.App.ViewModels;

internal sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("ru-RU");
    private readonly SettingsStore _settingsStore;
    private readonly TrafficEngineController _engineController;
    private readonly ProbeService _probeService = new();
    private readonly System.Threading.Timer _saveTimer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private AppSettings _settings;
    private TrafficEngineSnapshot _engineSnapshot = new(
        TrafficEngineState.Disabled, 0, 0, 0, 0, 0, 0, 0, null);
    private string _currentPage = "Home";
    private string _statusMessage = "Выберите режим и включите задержку.";
    private string _testNodeInput;
    private string _excludedPortsInput;
    private string _testNodeValidationMessage = string.Empty;
    private string _excludedPortsValidationMessage = string.Empty;
    private string _probeErrorMessage = string.Empty;
    private string _recentLogsText = "Журнал пока пуст.";
    private string _probeTimestampText = "Ещё не измерялось";
    private double? _sourceBaselineRttMs;
    private double? _measuredTcpConnectMs;
    private bool _isProbing;
    private int _faultCleanupScheduled;

    public MainViewModel(SettingsStore settingsStore, TrafficEngineController engineController)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _engineController = engineController ?? throw new ArgumentNullException(nameof(engineController));
        _settings = AppSettingsValidator.Repair(_settingsStore.Load());
        _testNodeInput = _settings.TestNode;
        _excludedPortsInput = _settings.ExcludedPorts;

        if (!string.IsNullOrWhiteSpace(_settingsStore.LastLoadError))
        {
            AppLogger.Warning(_settingsStore.LastLoadError);
            _statusMessage = _settingsStore.LastLoadError;
        }

        try
        {
            WindowsStartupManager.SetStartWithWindows(_settings.StartWithWindows);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Не удалось синхронизировать настройку автозапуска.", exception);
            _statusMessage = "Не удалось синхронизировать автозапуск: " + exception.Message;
        }

        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string));
        ToggleDelayCommand = new AsyncRelayCommand(_ => ToggleDelayAsync(), onError: HandleCommandError);
        MeasureProbeCommand = new AsyncRelayCommand(_ => MeasureProbeAsync(), _ => CanMeasureProbe, HandleCommandError);
        RestoreDefaultsCommand = new RelayCommand(_ => RestoreDefaults());
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics());
        OpenLogsFolderCommand = new RelayCommand(_ => OpenLogsFolder());
        CopyVersionCommand = new RelayCommand(_ => CopyVersion());
        OpenWinDivertDocsCommand = new RelayCommand(_ => OpenWinDivertDocs());

        _saveTimer = new System.Threading.Timer(SaveTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _engineController.UpdateSettings(_settings);
        RefreshSnapshot();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ElevationRequested;

    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand ToggleDelayCommand { get; }
    public AsyncRelayCommand MeasureProbeCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand CopyVersionCommand { get; }
    public RelayCommand OpenWinDivertDocsCommand { get; }

    public AppSettings Settings => _settings;

    public string CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsHomePage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsDiagnosticsPage));
            OnPropertyChanged(nameof(IsAboutPage));
            OnPropertyChanged(nameof(PageTitle));
            if (value == "Diagnostics")
            {
                RefreshRecentLogs();
            }
        }
    }

    public bool IsHomePage => CurrentPage == "Home";
    public bool IsSettingsPage => CurrentPage == "Settings";
    public bool IsDiagnosticsPage => CurrentPage == "Diagnostics";
    public bool IsAboutPage => CurrentPage == "About";

    public string PageTitle => CurrentPage switch
    {
        "Home" => "Панель управления",
        "Settings" => "Настройки",
        "Diagnostics" => "Диагностика",
        "About" => "О программе",
        _ => "Latency Studio"
    };

    public bool IsTargetMode
    {
        get => _settings.Mode == LatencyMode.TargetRtt;
        set
        {
            if (value && _settings.Mode != LatencyMode.TargetRtt)
            {
                UpdateSettings(_settings with { Mode = LatencyMode.TargetRtt });
            }
        }
    }

    public bool IsFixedMode
    {
        get => _settings.Mode == LatencyMode.Fixed;
        set
        {
            if (value && _settings.Mode != LatencyMode.Fixed)
            {
                UpdateSettings(_settings with { Mode = LatencyMode.Fixed });
            }
        }
    }

    public int TargetRttMs
    {
        get => _settings.TargetRttMs;
        set => UpdateSettings(_settings with
        {
            TargetRttMs = Math.Clamp(value, AppSettingsValidator.MinimumTargetRttMs, AppSettingsValidator.MaximumTargetRttMs)
        });
    }

    public int OutgoingDelayMs
    {
        get => _settings.OutgoingDelayMs;
        set => UpdateSettings(_settings with
        {
            OutgoingDelayMs = Math.Clamp(value, AppSettingsValidator.MinimumDirectionalDelayMs, AppSettingsValidator.MaximumDirectionalDelayMs)
        });
    }

    public int IncomingDelayMs
    {
        get => _settings.IncomingDelayMs;
        set => UpdateSettings(_settings with
        {
            IncomingDelayMs = Math.Clamp(value, AppSettingsValidator.MinimumDirectionalDelayMs, AppSettingsValidator.MaximumDirectionalDelayMs)
        });
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (value == _settings.StartWithWindows)
            {
                return;
            }

            try
            {
                WindowsStartupManager.SetStartWithWindows(value);
                UpdateSettings(_settings with { StartWithWindows = value });
                StatusMessage = value
                    ? "Latency Studio будет запускаться вместе с Windows (задержка останется выключенной)."
                    : "Автозапуск Latency Studio выключен.";
            }
            catch (Exception exception)
            {
                AppLogger.Error("Не удалось изменить автозапуск.", exception);
                StatusMessage = "Не удалось изменить автозапуск: " + exception.Message;
                OnPropertyChanged();
            }
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (value != _settings.MinimizeToTray)
            {
                UpdateSettings(_settings with { MinimizeToTray = value });
            }
        }
    }

    public string TestNodeInput
    {
        get => _testNodeInput;
        set
        {
            if (!SetProperty(ref _testNodeInput, value ?? string.Empty))
            {
                return;
            }

            SourceBaselineRttMs = null;
            MeasuredTcpConnectMs = null;
            ProbeTimestampText = "Ещё не измерялось";
            ProbeErrorMessage = string.Empty;

            if (ProbeEndpoint.TryParse(_testNodeInput, out var endpoint, out var error))
            {
                TestNodeValidationMessage = string.Empty;
                var canonical = endpoint.Port == 443 && !_testNodeInput.Contains(':')
                    ? endpoint.Host
                    : endpoint.Host.Contains(':')
                        ? $"[{endpoint.Host}]:{endpoint.Port}"
                        : $"{endpoint.Host}:{endpoint.Port}";
                if (!string.Equals(canonical, _settings.TestNode, StringComparison.Ordinal))
                {
                    SourceBaselineRttMs = null;
                    MeasuredTcpConnectMs = null;
                    ProbeTimestampText = "Ещё не измерялось";
                }

                UpdateSettings(_settings with { TestNode = canonical });
            }
            else
            {
                TestNodeValidationMessage = error;
            }

            OnPropertyChanged(nameof(CanMeasureProbe));
            MeasureProbeCommand.RaiseCanExecuteChanged();
        }
    }

    public string ExcludedPortsInput
    {
        get => _excludedPortsInput;
        set
        {
            if (!SetProperty(ref _excludedPortsInput, value ?? string.Empty))
            {
                return;
            }

            if (AppSettingsValidator.TryParseExcludedPorts(_excludedPortsInput, out var ports, out var error))
            {
                ExcludedPortsValidationMessage = string.Empty;
                UpdateSettings(_settings with { ExcludedPorts = string.Join(", ", ports) });
            }
            else
            {
                ExcludedPortsValidationMessage = error;
            }

            OnPropertyChanged(nameof(SettingsValidationMessage));
        }
    }

    public string TestNodeValidationMessage
    {
        get => _testNodeValidationMessage;
        private set
        {
            if (SetProperty(ref _testNodeValidationMessage, value))
            {
                OnPropertyChanged(nameof(SettingsValidationMessage));
            }
        }
    }

    public string ExcludedPortsValidationMessage
    {
        get => _excludedPortsValidationMessage;
        private set
        {
            if (SetProperty(ref _excludedPortsValidationMessage, value))
            {
                OnPropertyChanged(nameof(SettingsValidationMessage));
            }
        }
    }

    public string SettingsValidationMessage => string.Join(" ", new[]
    {
        TestNodeValidationMessage,
        ExcludedPortsValidationMessage
    }.Where(static text => !string.IsNullOrWhiteSpace(text)));

    public bool CanMeasureProbe => !_isProbing && string.IsNullOrWhiteSpace(TestNodeValidationMessage);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsProbing
    {
        get => _isProbing;
        private set
        {
            if (SetProperty(ref _isProbing, value))
            {
                OnPropertyChanged(nameof(CanMeasureProbe));
                MeasureProbeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ProbeButtonText));
            }
        }
    }

    public string ProbeButtonText => IsProbing ? "Измеряем…" : "Измерить RTT";

    public double? SourceBaselineRttMs
    {
        get => _sourceBaselineRttMs;
        private set
        {
            if (SetProperty(ref _sourceBaselineRttMs, value))
            {
                NotifyLatencyMetricsChanged();
            }
        }
    }

    public double? MeasuredTcpConnectMs
    {
        get => _measuredTcpConnectMs;
        private set
        {
            if (SetProperty(ref _measuredTcpConnectMs, value))
            {
                NotifyLatencyMetricsChanged();
            }
        }
    }

    public string ProbeErrorMessage
    {
        get => _probeErrorMessage;
        private set => SetProperty(ref _probeErrorMessage, value);
    }

    public string ProbeTimestampText
    {
        get => _probeTimestampText;
        private set => SetProperty(ref _probeTimestampText, value);
    }

    public TrafficEngineSnapshot EngineSnapshot => _engineSnapshot;

    public bool HasRuntime => _engineController.HasRuntime;

    public bool IsDelayActive => _engineSnapshot.State == TrafficEngineState.Running;

    public string EngineStateText => _engineSnapshot.State switch
    {
        TrafficEngineState.Disabled => "ОТКЛЮЧЕНО",
        TrafficEngineState.Starting => "ЗАПУСК",
        TrafficEngineState.Running => "АКТИВНО",
        TrafficEngineState.Stopping => "ОСТАНОВКА",
        TrafficEngineState.Error => "ОШИБКА",
        _ => "НЕИЗВЕСТНО"
    };

    public string ToggleButtonText => _engineSnapshot.State switch
    {
        TrafficEngineState.Running => "Выключить задержку",
        TrafficEngineState.Starting => "Запуск обработчика…",
        TrafficEngineState.Stopping => "Отключение…",
        TrafficEngineState.Error when _engineController.HasRuntime => "Остановить обработчик",
        _ => "Включить задержку"
    };

    public string MainStatusText => _engineSnapshot.State switch
    {
        TrafficEngineState.Running when _settings.Mode == LatencyMode.Fixed => "Искусственная задержка применяется к внешнему IP-трафику всего компьютера.",
        TrafficEngineState.Running => "Целевой режим работает для TCP-потоков с измеренным handshake; UDP/QUIC/ICMP без RTT-оценки пропускаются.",
        TrafficEngineState.Starting => "Подготовка системного фильтра WinDivert…",
        TrafficEngineState.Stopping => "Очередь пакетов освобождается; задержка больше не добавляется.",
        TrafficEngineState.Error => _engineSnapshot.LastError ?? "Сетевой обработчик завершился с ошибкой.",
        _ => "Задержка выключена. Пакеты не удерживаются приложением."
    };

    public string ModeText => _settings.Mode == LatencyMode.Fixed ? "Фиксированная задержка" : "Целевой RTT";

    public string SourceBaselineText => SourceBaselineRttMs.HasValue
        ? $"{SourceBaselineRttMs.Value.ToString("0.0", DisplayCulture)} мс"
        : "—";

    public string MeasuredTcpConnectText => MeasuredTcpConnectMs.HasValue
        ? $"{MeasuredTcpConnectMs.Value.ToString("0.0", DisplayCulture)} мс"
        : "—";

    public string AddedRttText
    {
        get
        {
            var plan = LatencyPolicy.Calculate(_settings, SourceBaselineRttMs);
            if (!plan.AddedRttMs.HasValue)
            {
                return "ожидает TCP-замера";
            }

            return $"≈ {plan.AddedRttMs.Value.ToString("0.#", DisplayCulture)} мс";
        }
    }

    public string DirectionalDelayText
    {
        get
        {
            var plan = LatencyPolicy.Calculate(_settings, SourceBaselineRttMs);
            if (_settings.Mode == LatencyMode.TargetRtt && !plan.IsCalibrated)
            {
                return "— / — мс (нет базового RTT)";
            }

            return $"{plan.Delay.OutgoingMs.ToString("0.#", DisplayCulture)} / {plan.Delay.IncomingMs.ToString("0.#", DisplayCulture)} мс";
        }
    }

    public string GaugeValueText => _settings.Mode == LatencyMode.TargetRtt
        ? _settings.TargetRttMs.ToString(DisplayCulture)
        : Math.Round(_settings.OutgoingDelayMs + _settings.IncomingDelayMs).ToString("0", DisplayCulture);

    public string GaugeCaption => _settings.Mode == LatencyMode.TargetRtt ? "ЦЕЛЬ RTT" : "ДОБАВКА RTT";

    public string ConfiguredDelayText => _settings.Mode == LatencyMode.Fixed
        ? $"Исходящий {_settings.OutgoingDelayMs} мс · входящий {_settings.IncomingDelayMs} мс"
        : $"Целевой RTT {_settings.TargetRttMs} мс";

    public string TargetModeExplanation =>
        "Калибровка выполняется отдельно по TCP SYN → SYN-ACK для новых соединений. Пока исходный RTT не измерен, поток не задерживается. UDP/QUIC/ICMP и уже открытые до включения потоки в целевом режиме не калибруются — для них используйте фиксированный режим.";

    public string ProbeExplanation =>
        "Это реальное время установки TCP-соединения с выбранным узлом (без TLS/HTTP). Оно не является универсальным игровым пингом; измерение включает активную искусственную задержку, если она включена.";

    public string ProcessedPacketsText => _engineSnapshot.ProcessedPackets.ToString("N0", DisplayCulture);
    public string DelayedPacketsText => _engineSnapshot.DelayedPackets.ToString("N0", DisplayCulture);
    public string BypassedPacketsText => _engineSnapshot.BypassedPackets.ToString("N0", DisplayCulture);
    public string ErrorCountText => _engineSnapshot.ErrorCount.ToString("N0", DisplayCulture);
    public string QueueText => $"{_engineSnapshot.QueuedPackets:N0} пакетов · {FormatBytes(_engineSnapshot.QueuedBytes)}";
    public string CalibratedFlowsText => _engineSnapshot.CalibratedTcpFlows.ToString("N0", DisplayCulture);
    public string LastEngineErrorText => string.IsNullOrWhiteSpace(_engineSnapshot.LastError)
        ? "Ошибок обработчика не зарегистрировано."
        : _engineSnapshot.LastError!;

    public string DriverStatusText
    {
        get
        {
            var dllPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert.dll"));
            var driverPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert64.sys"));
            return dllPresent && driverPresent
                ? "WinDivert 2.2.2: библиотека и x64-драйвер найдены"
                : "Компоненты WinDivert не найдены рядом с приложением";
        }
    }

    public string AdministratorStatusText => WindowsStartupManager.IsAdministrator()
        ? "Приложение запущено с правами администратора"
        : "Обычный запуск; UAC будет запрошен при включении перехвата";

    public string OsVersionText => Environment.OSVersion.VersionString;
    public string AppVersionText => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    public string LogFilePath => AppLogger.FilePath;

    public string RecentLogsText
    {
        get => _recentLogsText;
        private set => SetProperty(ref _recentLogsText, value);
    }

    public void RefreshSnapshot()
    {
        var snapshot = _engineController.Snapshot;
        var changed = snapshot != _engineSnapshot;
        if (!changed)
        {
            return;
        }

        _engineSnapshot = snapshot;
        OnPropertyChanged(nameof(EngineSnapshot));
        OnPropertyChanged(nameof(IsDelayActive));
        OnPropertyChanged(nameof(EngineStateText));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(MainStatusText));
        OnPropertyChanged(nameof(ProcessedPacketsText));
        OnPropertyChanged(nameof(DelayedPacketsText));
        OnPropertyChanged(nameof(BypassedPacketsText));
        OnPropertyChanged(nameof(ErrorCountText));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(CalibratedFlowsText));
        OnPropertyChanged(nameof(LastEngineErrorText));
        NotifyLatencyMetricsChanged();

        if (snapshot.State == TrafficEngineState.Error && _engineController.HasRuntime
            && Interlocked.Exchange(ref _faultCleanupScheduled, 1) == 0)
        {
            _ = CleanUpFaultedEngineAsync();
        }
        else if (snapshot.State != TrafficEngineState.Error)
        {
            Interlocked.Exchange(ref _faultCleanupScheduled, 0);
        }
    }

    public async Task SaveNowAsync()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var settings = Volatile.Read(ref _settings);
            await _settingsStore.SaveAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await SaveNowAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Не удалось сохранить настройки при завершении.", exception);
        }
        finally
        {
            _saveTimer.Dispose();
            _saveLock.Dispose();
        }
    }

    private async Task ToggleDelayAsync()
    {
        var snapshot = _engineController.Snapshot;
        if (snapshot.State is TrafficEngineState.Running or TrafficEngineState.Starting or TrafficEngineState.Stopping
            || (snapshot.State == TrafficEngineState.Error && _engineController.HasRuntime))
        {
            try
            {
                await _engineController.DisableAsync();
                StatusMessage = "Задержка отключена. Очередь пакетов передана без ожидания.";
                AppLogger.Info("System-wide packet delay stopped by the user.");
            }
            catch (Exception exception)
            {
                AppLogger.Error("Ошибка при остановке сетевого обработчика.", exception);
                StatusMessage = "Ошибка при остановке: " + exception.Message;
            }

            RefreshSnapshot();
            return;
        }

        if (!string.IsNullOrWhiteSpace(SettingsValidationMessage))
        {
            StatusMessage = "Исправьте ошибки в настройках перед включением.";
            return;
        }

        if (!WindowsStartupManager.IsAdministrator())
        {
            StatusMessage = "Для системного перехвата пакетов потребуется подтверждение UAC Windows.";
            ElevationRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            await _engineController.EnableAsync(_settings);
            StatusMessage = "Обработчик WinDivert запущен.";
            AppLogger.Info($"Packet delay enabled in {_settings.Mode} mode.");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Не удалось включить сетевой обработчик.", exception);
            StatusMessage = exception.Message;
        }

        RefreshSnapshot();
    }

    private async Task MeasureProbeAsync()
    {
        if (!CanMeasureProbe)
        {
            return;
        }

        IsProbing = true;
        ProbeErrorMessage = string.Empty;
        StatusMessage = $"Измеряется TCP-соединение с {_settings.TestNode}…";
        try
        {
            var result = await _probeService.MeasureAsync(_settings.TestNode, _engineController);
            MeasuredTcpConnectMs = result.MeasuredTcpConnectMs;
            SourceBaselineRttMs = result.SourceBaselineAvailable ? result.SourceBaselineRttMs : null;
            ProbeTimestampText = "Последнее измерение: " + DateTime.Now.ToString("HH:mm:ss", DisplayCulture);
            StatusMessage = result.SourceBaselineAvailable
                ? "TCP handshake измерен; исходная оценка получена для этого тестового узла."
                : "TCP-соединение измерено, но исходный RTT отдельно определить не удалось.";
            AppLogger.Info($"TCP probe completed: {result.Host}:{result.Port}, measured {result.MeasuredTcpConnectMs:0.0} ms, baseline {(result.SourceBaselineRttMs.HasValue ? result.SourceBaselineRttMs.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")} ms.");
        }
        catch (Exception exception)
        {
            MeasuredTcpConnectMs = null;
            SourceBaselineRttMs = null;
            ProbeErrorMessage = exception.Message;
            StatusMessage = "Тестовое TCP-соединение не удалось измерить.";
            AppLogger.Warning("Ошибка измерения тестового TCP-соединения.", exception);
        }
        finally
        {
            IsProbing = false;
            NotifyLatencyMetricsChanged();
        }
    }

    private async Task CleanUpFaultedEngineAsync()
    {
        try
        {
            await _engineController.DisableAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Не удалось штатно очистить обработчик после ошибки.", exception);
        }
        finally
        {
            RefreshSnapshot();
            StatusMessage = "Обработчик остановлен после ошибки; проверьте раздел «Диагностика».";
            Interlocked.Exchange(ref _faultCleanupScheduled, 0);
        }
    }

    private void UpdateSettings(AppSettings newSettings)
    {
        if (newSettings == _settings)
        {
            return;
        }

        var validation = AppSettingsValidator.Validate(newSettings);
        if (!validation.IsValid)
        {
            StatusMessage = validation.ErrorSummary;
            return;
        }

        Volatile.Write(ref _settings, newSettings);
        try
        {
            _engineController.UpdateSettings(newSettings);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Настройки сохранены, но не удалось передать их активному обработчику.", exception);
            StatusMessage = "Настройки не применились к сетевому обработчику: " + exception.Message;
        }

        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(IsTargetMode));
        OnPropertyChanged(nameof(IsFixedMode));
        OnPropertyChanged(nameof(TargetRttMs));
        OnPropertyChanged(nameof(OutgoingDelayMs));
        OnPropertyChanged(nameof(IncomingDelayMs));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(TargetModeExplanation));
        NotifyLatencyMetricsChanged();
        ScheduleSave();
    }

    private void RestoreDefaults()
    {
        try
        {
            WindowsStartupManager.SetStartWithWindows(false);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Не удалось отключить автозапуск при восстановлении настроек.", exception);
        }

        _testNodeInput = AppSettings.Defaults.TestNode;
        _excludedPortsInput = AppSettings.Defaults.ExcludedPorts;
        SourceBaselineRttMs = null;
        MeasuredTcpConnectMs = null;
        ProbeTimestampText = "Ещё не измерялось";
        ProbeErrorMessage = string.Empty;
        OnPropertyChanged(nameof(TestNodeInput));
        OnPropertyChanged(nameof(ExcludedPortsInput));
        TestNodeValidationMessage = string.Empty;
        ExcludedPortsValidationMessage = string.Empty;
        UpdateSettings(AppSettings.Defaults);
        StatusMessage = "Настройки восстановлены. Задержка осталась выключенной.";
    }

    private void Navigate(string? page)
    {
        if (page is "Home" or "Settings" or "Diagnostics" or "About")
        {
            CurrentPage = page;
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildDiagnosticsReport());
            StatusMessage = "Диагностический отчёт скопирован в буфер обмена.";
        }
        catch (Exception exception)
        {
            StatusMessage = "Не удалось скопировать отчёт: " + exception.Message;
            AppLogger.Warning("Не удалось скопировать диагностический отчёт.", exception);
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppLogger.FilePath)!);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{AppLogger.FilePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusMessage = "Не удалось открыть папку журнала: " + exception.Message;
            AppLogger.Warning("Не удалось открыть папку журнала.", exception);
        }
    }

    private void CopyVersion()
    {
        try
        {
            System.Windows.Clipboard.SetText($"Latency Studio {AppVersionText}");
            StatusMessage = "Версия скопирована.";
        }
        catch (Exception exception)
        {
            StatusMessage = "Не удалось скопировать версию: " + exception.Message;
        }
    }

    private void OpenWinDivertDocs()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/basil00/WinDivert")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusMessage = "Не удалось открыть страницу WinDivert: " + exception.Message;
        }
    }

    private string BuildDiagnosticsReport()
    {
        var snapshot = _engineController.Snapshot;
        var delayDescription = _settings.Mode == LatencyMode.Fixed
            ? $"out={_settings.OutgoingDelayMs} ms, in={_settings.IncomingDelayMs} ms"
            : $"target={_settings.TargetRttMs} ms";
        return string.Join(Environment.NewLine,
        new[]
        {
            $"Latency Studio {AppVersionText}",
            $"ОС: {OsVersionText}",
            $"Состояние: {snapshot.State}",
            $"Режим: {_settings.Mode}",
            $"Задержка: {delayDescription}",
            $"Обработано пакетов: {snapshot.ProcessedPackets}",
            $"Отложено пакетов: {snapshot.DelayedPackets}",
            $"Передано без задержки: {snapshot.BypassedPackets}",
            $"Ошибки: {snapshot.ErrorCount}",
            $"Очередь: {snapshot.QueuedPackets} пакетов / {snapshot.QueuedBytes} байт",
            $"Калибровано TCP-потоков: {snapshot.CalibratedTcpFlows}",
            $"Права администратора: {WindowsStartupManager.IsAdministrator()}",
            $"WinDivert.dll: {File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert.dll"))}",
            $"WinDivert64.sys: {File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert64.sys"))}",
            $"Последняя ошибка: {snapshot.LastError ?? "нет"}",
            $"Журнал: {AppLogger.FilePath}"
        });
    }

    private void RefreshRecentLogs()
    {
        var lines = AppLogger.ReadRecentLines(50);
        RecentLogsText = lines.Count == 0 ? "Журнал пока пуст." : string.Join(Environment.NewLine, lines);
    }

    private void ScheduleSave() => _saveTimer.Change(450, Timeout.Infinite);

    private async void SaveTimerElapsed(object? state)
    {
        try
        {
            await SaveNowAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Не удалось сохранить пользовательские настройки.", exception);
            var message = "Не удалось сохранить настройки: " + exception.Message;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                await dispatcher.InvokeAsync(() => StatusMessage = message);
            }
        }
    }

    private void HandleCommandError(Exception exception)
    {
        AppLogger.Error("Ошибка команды пользовательского интерфейса.", exception);
        StatusMessage = exception.Message;
    }

    private void NotifyLatencyMetricsChanged()
    {
        OnPropertyChanged(nameof(SourceBaselineText));
        OnPropertyChanged(nameof(MeasuredTcpConnectText));
        OnPropertyChanged(nameof(AddedRttText));
        OnPropertyChanged(nameof(DirectionalDelayText));
        OnPropertyChanged(nameof(GaugeValueText));
        OnPropertyChanged(nameof(GaugeCaption));
        OnPropertyChanged(nameof(ConfiguredDelayText));
        OnPropertyChanged(nameof(TargetModeExplanation));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} Б";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.0} КБ";
        }

        return $"{bytes / (1024d * 1024d):0.0} МБ";
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace LatencyStudio.App.Infrastructure;

internal static class WindowsStartupManager
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LatencyStudio";

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void SetStartWithWindows(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Автозапуск доступен только в Windows.");
        }

        using var runKey = Registry.CurrentUser.CreateSubKey(RunSubKey, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска текущего пользователя.");
        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу Latency Studio.");
        }

        runKey.SetValue(ValueName, $"\"{executablePath}\" --tray", RegistryValueKind.String);
    }

    public static Process StartElevatedEnable()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Повышение прав через UAC доступно только в Windows.");
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу Latency Studio.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--elevated-enable",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows не запустила повышенный процесс Latency Studio.");
    }
}

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    public SingleInstanceGuard()
    {
        var userName = Environment.UserName.Replace('\\', '_').Replace('/', '_');
        _mutex = new Mutex(initiallyOwned: false, $"Local\\LatencyStudio-{userName}");
    }

    public bool TryAcquire(bool waitForPreviousInstance)
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(waitForPreviousInstance ? TimeSpan.FromSeconds(25) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The OS releases an abandoned mutex when the process exits.
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}

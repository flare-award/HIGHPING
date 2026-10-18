using System.Text.Json;
using System.Text.Json.Serialization;

namespace LatencyStudio.Core.Configuration;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<LatencyMode>() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LatencyStudio",
            "settings.json");
    }

    public string FilePath => _path;
    public string? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        if (!File.Exists(_path))
        {
            return AppSettings.Defaults;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            var validation = AppSettingsValidator.Validate(settings);
            if (!validation.IsValid)
            {
                LastLoadError = "Часть настроек повреждена и заменена значениями по умолчанию: " + validation.ErrorSummary;
            }

            return AppSettingsValidator.Repair(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LastLoadError = $"Не удалось прочитать настройки: {exception.Message}";
            return AppSettings.Defaults;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = AppSettingsValidator.Validate(settings);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorSummary, nameof(settings));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Путь к настройкам не содержит каталога.");
            Directory.CreateDirectory(directory);
            temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed cleanup must not hide the original save error.
                }
                catch (UnauthorizedAccessException)
                {
                    // A failed cleanup must not hide the original save error.
                }
            }

            _writeLock.Release();
        }
    }
}

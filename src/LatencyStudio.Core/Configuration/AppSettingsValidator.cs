using System.Globalization;

namespace LatencyStudio.Core.Configuration;

public sealed record SettingsValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public string ErrorSummary => string.Join(" ", Errors);
}

public static class AppSettingsValidator
{
    public const int MinimumTargetRttMs = 0;
    public const int MaximumTargetRttMs = 2_000;
    public const int MinimumDirectionalDelayMs = 0;
    public const int MaximumDirectionalDelayMs = 1_000;
    public const int MaximumExcludedPorts = 64;

    public static SettingsValidationResult Validate(AppSettings? settings)
    {
        if (settings is null)
        {
            return new SettingsValidationResult(false, ["Настройки отсутствуют."]);
        }

        var errors = new List<string>();
        if (!Enum.IsDefined(settings.Mode))
        {
            errors.Add("Выбран неизвестный режим задержки.");
        }

        if (settings.TargetRttMs is < MinimumTargetRttMs or > MaximumTargetRttMs)
        {
            errors.Add($"Целевой RTT должен быть от {MinimumTargetRttMs} до {MaximumTargetRttMs} мс.");
        }

        if (settings.OutgoingDelayMs is < MinimumDirectionalDelayMs or > MaximumDirectionalDelayMs)
        {
            errors.Add($"Исходящая задержка должна быть от {MinimumDirectionalDelayMs} до {MaximumDirectionalDelayMs} мс.");
        }

        if (settings.IncomingDelayMs is < MinimumDirectionalDelayMs or > MaximumDirectionalDelayMs)
        {
            errors.Add($"Входящая задержка должна быть от {MinimumDirectionalDelayMs} до {MaximumDirectionalDelayMs} мс.");
        }

        if (!ProbeEndpoint.TryParse(settings.TestNode, out _, out var endpointError))
        {
            errors.Add($"Тестовый узел: {endpointError}");
        }

        if (!TryParseExcludedPorts(settings.ExcludedPorts, out _, out var portsError))
        {
            errors.Add($"Исключения портов: {portsError}");
        }

        return new SettingsValidationResult(errors.Count == 0, errors);
    }

    public static bool TryParseExcludedPorts(string? text, out IReadOnlyList<ushort> ports, out string error)
    {
        ports = Array.Empty<ushort>();
        error = string.Empty;
        var input = text?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            return true;
        }

        var result = new SortedSet<ushort>();
        foreach (var part in input.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0)
            {
                error = $"«{part}» не является номером порта от 1 до 65535.";
                return false;
            }

            result.Add(port);
            if (result.Count > MaximumExcludedPorts)
            {
                error = $"Можно указать не более {MaximumExcludedPorts} портов.";
                return false;
            }
        }

        ports = result.ToArray();
        return true;
    }

    public static string NormalizeExcludedPorts(string? text)
    {
        return TryParseExcludedPorts(text, out var ports, out _)
            ? string.Join(", ", ports)
            : string.Empty;
    }

    /// <summary>
    /// Repairs values read from a user-editable settings file. Invalid individual values
    /// are replaced with defaults so a damaged file cannot prevent the application starting.
    /// </summary>
    public static AppSettings Repair(AppSettings? settings)
    {
        var source = settings ?? AppSettings.Defaults;
        var defaults = AppSettings.Defaults;
        var repaired = source with
        {
            Mode = Enum.IsDefined(source.Mode) ? source.Mode : defaults.Mode,
            TargetRttMs = InRange(source.TargetRttMs, MinimumTargetRttMs, MaximumTargetRttMs)
                ? source.TargetRttMs
                : defaults.TargetRttMs,
            OutgoingDelayMs = InRange(source.OutgoingDelayMs, MinimumDirectionalDelayMs, MaximumDirectionalDelayMs)
                ? source.OutgoingDelayMs
                : defaults.OutgoingDelayMs,
            IncomingDelayMs = InRange(source.IncomingDelayMs, MinimumDirectionalDelayMs, MaximumDirectionalDelayMs)
                ? source.IncomingDelayMs
                : defaults.IncomingDelayMs,
            TestNode = ProbeEndpoint.TryParse(source.TestNode, out _, out _) ? source.TestNode.Trim() : defaults.TestNode,
            ExcludedPorts = TryParseExcludedPorts(source.ExcludedPorts, out var ports, out _)
                ? string.Join(", ", ports)
                : defaults.ExcludedPorts
        };

        return repaired;
    }

    private static bool InRange(int value, int minimum, int maximum) => value >= minimum && value <= maximum;
}

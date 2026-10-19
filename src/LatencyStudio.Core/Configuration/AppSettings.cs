using System.Text.Json.Serialization;

namespace LatencyStudio.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<LatencyMode>))]
public enum LatencyMode
{
    TargetRtt = 0,
    Fixed = 1
}

/// <summary>
/// User preferences. These values are deliberately kept separate from runtime state.
/// </summary>
public sealed record AppSettings
{
    public LatencyMode Mode { get; init; } = LatencyMode.TargetRtt;
    public int TargetRttMs { get; init; } = 95;
    public int OutgoingDelayMs { get; init; } = 40;
    public int IncomingDelayMs { get; init; } = 40;
    public bool StartWithWindows { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public string TestNode { get; init; } = "1.1.1.1:443";
    public string ExcludedPorts { get; init; } = string.Empty;

    public static AppSettings Defaults => new();
}

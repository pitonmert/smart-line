namespace SmartLine.API.Infrastructure.Configuration;

public sealed class SortingOptions
{
    public const string SectionName = "Sorting";

    /// <summary>
    /// Reserved for a future actuator transport. The current guard still does not publish motion commands.
    /// </summary>
    public bool HardwareActuationEnabled { get; set; }
}

public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";
    public bool Enabled { get; set; }
}

public sealed class VisionEndpointOptions
{
    public const string SectionName = "Vision";
    public string HealthUrl { get; set; } = "";
    public int CaptureTimeoutSeconds { get; set; } = 35;
}

public sealed class DatasetOptions
{
    public const string SectionName = "Dataset";
    public string Root { get; set; } = "datasets";
}

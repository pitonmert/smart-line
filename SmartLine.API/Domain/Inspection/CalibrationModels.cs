namespace SmartLine.API.Domain.Inspection;

public sealed class CalibrationDefinition
{
    public PixelRect? CameraRoi { get; set; }
    public PixelRect? InspectionZone { get; set; }
    public List<CalibrationPoint>? PerspectiveReference { get; set; }
    public double? PixelsPerMillimeter { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public int OrientationDegrees { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public ColorReference? ColorReference { get; set; }
    public double? BeltSpeedMmPerSecond { get; set; }
    public double? InspectionToSorterDistanceMm { get; set; }
    public List<GateMapping> GateMappings { get; set; } = [];
    public TofPlaceholder TofBaseline { get; set; } = new();
    public bool HardwareActuationEnabled { get; set; }
    public bool HardwareActuationConfirmed { get; set; }
    public string? Notes { get; set; }
}

public sealed class PixelRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class CalibrationPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class ColorReference
{
    public int Red { get; set; }
    public int Green { get; set; }
    public int Blue { get; set; }
    public string? IlluminantNote { get; set; }
}

public sealed class GateMapping
{
    public string BinCode { get; set; } = "";
    public int ServoDegrees { get; set; }
    public bool Enabled { get; set; }
}

public sealed class TofPlaceholder
{
    public string Source { get; set; } = InspectionVocabulary.TofNotIntegrated;
    public bool? ProductPresent { get; set; }
    public double? DistanceMm { get; set; }
    public DateTime? TriggerTimestamp { get; set; }
    public bool? SensorHealthy { get; set; }
}

public static class CalibrationValidator
{
    public static IReadOnlyList<string> Validate(CalibrationDefinition calibration)
    {
        var errors = new List<string>();
        if (calibration is null)
            return ["Calibration is required."];
        // Eksik JSON bölümleri ve sonlu olmayan sayılar sunucu hatasına dönüşmez.
        if (calibration.GateMappings is null || calibration.TofBaseline is null)
            return ["Gate mappings and time-of-flight baseline are required."];
        if (calibration.Notes?.Length > 1000)
            errors.Add("Notes exceed 1000 characters.");
        if (
            calibration.PixelsPerMillimeter is not null
            && (
                calibration.SourceWidth is null or <= 0 or > 10000
                || calibration.SourceHeight is null or <= 0 or > 10000
            )
        )
            errors.Add("Scale calibration requires source image dimensions.");
        if (calibration.OrientationDegrees is not (0 or 90 or 180 or 270))
            errors.Add("Camera orientation must be 0, 90, 180, or 270 degrees.");
        if (
            calibration.PixelsPerMillimeter is { } ppm
            && (!double.IsFinite(ppm) || ppm <= 0 || ppm > 100)
        )
            errors.Add("Pixels per millimetre must be between 0 and 100 when it is set.");
        if (
            calibration.BeltSpeedMmPerSecond is { } speed
            && (
                !double.IsFinite(speed)
                || speed <= 0
                || speed > SortingTiming.MaxBeltSpeedMmPerSecond
            )
        )
            errors.Add("Belt speed must be between 0 and 5000 mm/s when it is set.");
        if (
            calibration.InspectionToSorterDistanceMm is { } distance
            && (
                !double.IsFinite(distance) || distance < 0 || distance > SortingTiming.MaxDistanceMm
            )
        )
            errors.Add(
                "Inspection-to-sorter distance must be between 0 and 20000 mm when it is set."
            );

        ValidateRect(errors, "camera ROI", calibration.CameraRoi);
        ValidateRect(errors, "inspection zone", calibration.InspectionZone);

        if (calibration.PerspectiveReference is { Count: not 0 and not 4 })
            errors.Add("Perspective reference must contain exactly four points when it is set.");

        if (
            calibration.PerspectiveReference?.Any(point =>
                point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            ) == true
        )
            errors.Add("Perspective points must be finite.");

        if (calibration.ColorReference is { } color)
        {
            if (
                color.Red is < 0 or > 255
                || color.Green is < 0 or > 255
                || color.Blue is < 0 or > 255
            )
                errors.Add("Color reference channels must stay between 0 and 255.");
        }

        var bins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in calibration.GateMappings)
        {
            if (gate is null)
            {
                errors.Add("Gate mapping cannot be null.");
                continue;
            }
            if (gate.BinCode?.Length > 41)
                errors.Add("Gate bin code is too long.");
            if (string.IsNullOrWhiteSpace(gate.BinCode) || !bins.Add(gate.BinCode))
                errors.Add("Gate mappings need unique destination bins.");
            if (gate.ServoDegrees is < 0 or > 180)
                errors.Add($"Servo angle for {gate.BinCode} must stay between 0 and 180 degrees.");
            if (gate.Enabled && !calibration.HardwareActuationEnabled)
                errors.Add(
                    $"Gate {gate.BinCode} cannot be enabled while hardware actuation is off."
                );
        }

        // Fiziksel aktüasyon bu sürümde desteklenmez; onay bayrağı bunu değiştirmez.
        if (
            calibration.HardwareActuationEnabled
            || calibration.GateMappings.Any(g => g?.Enabled == true)
        )
            errors.Add("Hardware actuation is not integrated.");
        if (calibration.HardwareActuationEnabled && !calibration.HardwareActuationConfirmed)
            errors.Add("Hardware actuation requires an explicit confirmation flag.");

        var tof = calibration.TofBaseline ?? new TofPlaceholder();
        var hasReading =
            tof.ProductPresent is not null
            || tof.DistanceMm is not null
            || tof.TriggerTimestamp is not null
            || tof.SensorHealthy is not null;
        if (tof.Source == InspectionVocabulary.TofNotIntegrated && hasReading)
            errors.Add("VL53L1X is not integrated. The baseline cannot contain sensor readings.");
        else if (
            tof.Source == InspectionVocabulary.TofSimulation
            && tof.DistanceMm is { } simulated
            && (!double.IsFinite(simulated) || simulated < 0)
        )
            errors.Add("A simulated distance cannot be negative.");
        else if (
            tof.Source != InspectionVocabulary.TofNotIntegrated
            && tof.Source != InspectionVocabulary.TofSimulation
        )
            errors.Add(
                "Time-of-flight source must be NOT_INTEGRATED or SIMULATION. Hardware readings are deferred."
            );

        return errors;
    }

    public static bool UsesSimulation(CalibrationDefinition calibration) =>
        calibration.TofBaseline?.Source == InspectionVocabulary.TofSimulation;

    private static void ValidateRect(List<string> errors, string label, PixelRect? rect)
    {
        if (rect is null)
            return;
        if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0)
            errors.Add($"{label} must sit inside a positive pixel rectangle.");
        if (rect.X > 10_000 || rect.Y > 10_000 || rect.Width > 10_000 || rect.Height > 10_000)
            errors.Add($"{label} exceeds the 10000 pixel safety limit.");
    }
}

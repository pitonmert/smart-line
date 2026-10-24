namespace SmartLine.API.Domain.Inspection;

public sealed class ProductRecipeDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<ProductClassDefinition> ProductClasses { get; set; } = [];
    public List<ColorClassDefinition> AllowedColors { get; set; } = [];
    public List<SizeClassDefinition> SizeClasses { get; set; } = [];
    public List<DefectClassDefinition> DefectClasses { get; set; } = [];
    public AcceptanceCriteria Acceptance { get; set; } = new();
    public List<SortingRuleDefinition> SortingRules { get; set; } = [];
    public List<DestinationBinDefinition> DestinationBins { get; set; } = [];
    public ModelConfiguration Model { get; set; } = new();
    public CameraProfileDefinition CameraProfile { get; set; } = new();
    public NormalizedRoi Roi { get; set; } = new();
    public Guid? CalibrationProfileId { get; set; }
    public PhysicalLimits PhysicalLimits { get; set; } = new();
    public InspectionSettings Inspection { get; set; } = new();
}

public sealed class ProductClassDefinition
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ExpectedColor { get; set; }
}

public sealed class ColorClassDefinition
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double HueMin { get; set; }
    public double HueMax { get; set; }
    public double SaturationMin { get; set; }
    public double ValueMin { get; set; }
}

public sealed class SizeClassDefinition
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double MinWidthMm { get; set; }
    public double MaxWidthMm { get; set; }
}

public sealed class DefectClassDefinition
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Rejects { get; set; } = true;
}

public sealed class AcceptanceCriteria
{
    public double MinimumConfidence { get; set; } = 0.6;
    public bool RejectUnknownClass { get; set; } = true;
    public bool RejectUnknownDefects { get; set; } = true;
    public bool DefectsOverrideLowConfidence { get; set; } = true;
    public bool RequireKnownColor { get; set; }
    public string DefaultDestinationBin { get; set; } = InspectionVocabulary.ManualReview;
}

public sealed class SortingRuleDefinition
{
    public string Id { get; set; } = "";
    public int Priority { get; set; }
    public SortingRuleWhen When { get; set; } = new();
    public string DestinationBin { get; set; } = "";
}

public sealed class SortingRuleWhen
{
    public string? Quality { get; set; }
    public string? ProductClass { get; set; }
    public string? ColorClass { get; set; }
    public string? SizeClass { get; set; }
    public string? DefectClass { get; set; }
}

public sealed class DestinationBinDefinition
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = InspectionVocabulary.Hold;
}

public sealed class ModelConfiguration
{
    public string Task { get; set; } = InspectionVocabulary.TaskDetect;
    public string Family { get; set; } = "YOLO26";
    public string Name { get; set; } = "yolo26n";
    public string? Version { get; set; }
    public string? ArtifactFileName { get; set; }
    public int InputSize { get; set; } = 640;
}

public sealed class CameraProfileDefinition
{
    public string? StreamUrl { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public string? Sensor { get; set; }
    public string? PixelFormat { get; set; }
    public string? Transport { get; set; }
    public string? Notes { get; set; }
}

public sealed class NormalizedRoi
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
}

public sealed class PhysicalLimits
{
    public double? MaxWidthMm { get; set; }
    public double? MaxHeightMm { get; set; }
    public double? MinConfidenceToSort { get; set; }
}

public sealed class InspectionSettings
{
    public List<string> TriggerModes { get; set; } = [];
    public string SelectionStrategy { get; set; } = "latest_frame_on_trigger";
    public bool ContinuousInference { get; set; }
}

public sealed class InspectionObservation
{
    // Kaynak bilgisi manuel girdiyi gerçek model çıktısından ayırır.
    public string ObservationSource { get; set; } = "manual";
    public DateTime? CapturedAt { get; set; }
    public string? ProductClass { get; set; }
    public string? ColorClass { get; set; }
    public string? SizeClass { get; set; }
    public List<string> DefectClasses { get; set; } = [];
    public double Confidence { get; set; }
    public double? VisualWidth { get; set; }
    public double? VisualHeight { get; set; }
    public double? EstimatedWidthMm { get; set; }
    public double? EstimatedHeightMm { get; set; }
    public bool Simulation { get; set; }
    public List<BoundingBox> BoundingBoxes { get; set; } = [];
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public string? ModelName { get; set; }
    public string? ModelVersion { get; set; }
    public int? ProcessingLatencyMs { get; set; }
    public string? CameraSource { get; set; }
    public string? ImageReference { get; set; }
    public string? TriggerSource { get; set; }
    public string? FrameRole { get; set; }
    public string? CaptureSource { get; set; }
    public bool PreviewFallback { get; set; }
}

public sealed class BoundingBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? Label { get; set; }
    public double? Confidence { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
}

public sealed class InspectionGeometry
{
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public NormalizedRoi? Roi { get; set; }
}

public sealed class EstimatedPhysicalDimensions
{
    public double? WidthMm { get; set; }
    public double? HeightMm { get; set; }
    public string Method { get; set; } = "visual_estimate";
    public bool DepthAvailable { get; set; }
    public double? DistanceMm { get; set; }
}

public sealed class DecisionResult
{
    public string ProductClass { get; set; } = InspectionVocabulary.Unknown;
    public string ColorClass { get; set; } = InspectionVocabulary.Unknown;
    public string SizeClass { get; set; } = InspectionVocabulary.Unknown;
    public List<string> DefectClasses { get; set; } = [];
    public string QualityStatus { get; set; } = InspectionVocabulary.ManualReview;
    public string Decision { get; set; } = InspectionVocabulary.ManualReview;
    public string DestinationBin { get; set; } = InspectionVocabulary.ManualReview;
    public string? RuleId { get; set; }
    public List<string> Reasons { get; set; } = [];
    public bool Simulation { get; set; }
}

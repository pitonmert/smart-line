namespace SmartLine.API.Domain.Entities;

public class ProductRecipe
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Version { get; set; } = 1;
    public string DefinitionJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CalibrationProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Version { get; set; } = 1;
    public string DefinitionJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class InspectionRecord
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid RecipeId { get; set; }
    public DateTime CapturedAt { get; set; }
    public DateTime ProcessedAt { get; set; }
    public string ProductClass { get; set; } = string.Empty;
    public string ColorClass { get; set; } = string.Empty;
    public string SizeClass { get; set; } = string.Empty;
    public string DefectClassesJson { get; set; } = "[]";
    public string QualityStatus { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string DestinationBin { get; set; } = string.Empty;
    public string? ModelName { get; set; }
    public string? ModelVersion { get; set; }
    public int? ProcessingLatencyMs { get; set; }
    public string? ImageReference { get; set; }
    public bool IsSimulation { get; set; }
    public string ResultJson { get; set; } = "{}";
}

public class ProductTrack
{
    public Guid Id { get; set; }
    public string PublicCode { get; set; } = string.Empty;
    public Guid? RecipeId { get; set; }
    public Guid? InspectionId { get; set; }
    public string Stage { get; set; } = "DETECTED";
    public bool IsSimulation { get; set; }
    public string? DestinationBin { get; set; }
    public string? Decision { get; set; }
    public string? FailureReason { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime? CapturedAt { get; set; }
    public DateTime? InspectingAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? SortedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? ManualReviewAt { get; set; }
    public DateTime? SortDueAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class DatasetSession
{
    public Guid Id { get; set; }
    public Guid? RecipeId { get; set; }
    public string RecipeName { get; set; } = string.Empty;
    public string Status { get; set; } = "ACTIVE";
    public string ProductClass { get; set; } = string.Empty;
    public string QualityLabel { get; set; } = string.Empty;
    public string ColorLabel { get; set; } = string.Empty;
    public string SizeLabel { get; set; } = string.Empty;
    public string DefectLabelsJson { get; set; } = "[]";
    public string? Notes { get; set; }
    public string CaptureMode { get; set; } = "MANUAL";
    public int? IntervalMs { get; set; }
    public int ImageCount { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public bool IsSimulation { get; set; }
}

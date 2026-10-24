namespace SmartLine.API.Domain.Inspection;

public sealed record InspectionResult(
    Guid InspectionId,
    Guid ProductId,
    Guid RecipeId,
    DateTime CapturedAt,
    DateTime ProcessedAt,
    string ProductClass,
    string ColorClass,
    string SizeClass,
    IReadOnlyList<string> DefectClasses,
    string QualityStatus,
    double Confidence,
    IReadOnlyList<BoundingBox> BoundingBoxes,
    InspectionGeometry? Geometry,
    double? VisualWidth,
    double? VisualHeight,
    EstimatedPhysicalDimensions? EstimatedPhysicalDimensions,
    string? ModelName,
    string? ModelVersion,
    string Decision,
    string DestinationBin,
    int? ProcessingLatencyMs,
    string CameraSource,
    string? ImageReference,
    bool Simulation,
    string? RuleId,
    IReadOnlyList<string> Reasons,
    int RecipeVersion = 0,
    string? FrameRole = null,
    string? CaptureSource = null,
    bool PreviewFallback = false,
    string ObservationSource = "manual",
    Guid? CalibrationProfileId = null,
    int? CalibrationVersion = null
);

public static class InspectionResultFactory
{
    public static InspectionResult Create(
        Guid inspectionId,
        Guid productId,
        Guid recipeId,
        ProductRecipeDefinition recipe,
        InspectionObservation observation,
        DecisionResult decision,
        DateTime capturedAt,
        DateTime processedAt,
        int? processingLatencyMs,
        int recipeVersion = 0
    )
    {
        EstimatedPhysicalDimensions? physical = null;
        if (observation.EstimatedWidthMm is not null || observation.EstimatedHeightMm is not null)
        {
            physical = new EstimatedPhysicalDimensions
            {
                WidthMm = observation.EstimatedWidthMm,
                HeightMm = observation.EstimatedHeightMm,
                Method = "visual_estimate",
                DepthAvailable = false,
                DistanceMm = null,
            };
        }

        return new InspectionResult(
            inspectionId,
            productId,
            recipeId,
            DateTime.SpecifyKind(capturedAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(processedAt, DateTimeKind.Utc),
            decision.ProductClass,
            decision.ColorClass,
            decision.SizeClass,
            decision.DefectClasses,
            decision.QualityStatus,
            observation.Confidence,
            observation.BoundingBoxes ?? [],
            new InspectionGeometry
            {
                SourceWidth = observation.SourceWidth ?? recipe.CameraProfile.SourceWidth,
                SourceHeight = observation.SourceHeight ?? recipe.CameraProfile.SourceHeight,
                Roi = recipe.Roi,
            },
            observation.VisualWidth,
            observation.VisualHeight,
            physical,
            observation.ObservationSource == "model" ? observation.ModelName : null,
            observation.ObservationSource == "model" ? observation.ModelVersion : null,
            decision.Decision,
            decision.DestinationBin,
            observation.ObservationSource == "model" ? processingLatencyMs ?? observation.ProcessingLatencyMs : null,
            observation.CameraSource ?? recipe.CameraProfile.StreamUrl ?? "",
            observation.ImageReference,
            decision.Simulation,
            decision.RuleId,
            decision.Reasons,
            recipeVersion,
            observation.FrameRole,
            observation.CaptureSource,
            observation.PreviewFallback,
            decision.Simulation ? "simulation" : observation.ObservationSource
        );
    }
}

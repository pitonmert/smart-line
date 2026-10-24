namespace SmartLine.API.Domain.Inspection;

/// <summary>
/// Ürün değerlendirmesinde API, Vision ve arayüzün kullandığı ortak kodlar.
/// </summary>
public static class InspectionVocabulary
{
    public const string Unknown = "UNKNOWN";
    public const string Good = "GOOD";
    public const string Defective = "DEFECTIVE";
    public const string ManualReview = "MANUAL_REVIEW";
    public const string Pass = "PASS";
    public const string Reject = "REJECT";
    public const string Hold = "HOLD";

    public const string StageDetected = "DETECTED";
    public const string StageCaptured = "CAPTURED";
    public const string StageInspecting = "INSPECTING";
    public const string StageDecided = "DECIDED";
    public const string StageQueued = "QUEUED_FOR_SORT";
    public const string StageSorted = "SORTED";
    public const string StageFailed = "FAILED";
    public const string StageManualReview = "MANUAL_REVIEW";

    public const string TriggerManual = "manual";
    public const string TriggerApi = "api";
    public const string TriggerSimulator = "simulator";
    public const string TriggerHardware = "hardware";

    public const string TofNotIntegrated = "NOT_INTEGRATED";
    public const string TofSimulation = "SIMULATION";

    public const string TaskDetect = "detect";
    public const string TaskClassify = "classify";
    public const string TaskSegment = "segment";

    public static string DecisionForBinRole(string role) =>
        role switch
        {
            Pass => Pass,
            Reject => Reject,
            Hold => ManualReview,
            _ => ManualReview,
        };
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartLine.API.Domain.Inspection;

public static class InspectionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}.");

    public static string ReadEmbedded(string logicalName)
    {
        var assembly = typeof(InspectionJson).Assembly;
        using var stream =
            assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded contract '{logicalName}' is missing."
            );
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> EmbeddedNames() =>
        typeof(InspectionJson).Assembly.GetManifestResourceNames();
}

public static class ContractCatalog
{
    public const string LegacyDemoBoxResource = "SmartLine.API.Contracts.recipes.demobox-legacy-320x240.json";
    public const string UncalibratedResource = "SmartLine.API.Contracts.calibration.uncalibrated.json";

    public static ProductRecipeDefinition LegacyDemoBox() =>
        InspectionJson.Deserialize<ProductRecipeDefinition>(InspectionJson.ReadEmbedded(LegacyDemoBoxResource));

    public static CalibrationDefinition Uncalibrated() =>
        InspectionJson.Deserialize<CalibrationDefinition>(InspectionJson.ReadEmbedded(UncalibratedResource));
    public const string DemoBoxResource = "SmartLine.API.Contracts.recipes.demobox.json";
    public const string DecisionCasesResource =
        "SmartLine.API.Contracts.fixtures.decision-cases.json";

    public static ProductRecipeDefinition DemoBox() =>
        InspectionJson.Deserialize<ProductRecipeDefinition>(
            InspectionJson.ReadEmbedded(DemoBoxResource)
        );

    public static DecisionFixtureDocument DecisionCases() =>
        InspectionJson.Deserialize<DecisionFixtureDocument>(
            InspectionJson.ReadEmbedded(DecisionCasesResource)
        );
}

public sealed class DecisionFixtureDocument
{
    public string Recipe { get; set; } = "";
    public List<DecisionFixtureCase> Cases { get; set; } = [];
}

public sealed class DecisionFixtureCase
{
    public string Name { get; set; } = "";
    public List<string> ExpectedReasons { get; set; } = [];

    // Test senaryosu kendi sınırlarını verirse ortak reçetenin sınırları yerine bunlar kullanılır.
    public PhysicalLimits? PhysicalLimits { get; set; }
    public InspectionObservation Observation { get; set; } = new();
    public DecisionExpectation Expected { get; set; } = new();
}

public sealed class DecisionExpectation
{
    public string? ProductClass { get; set; }
    public string? ColorClass { get; set; }
    public string? SizeClass { get; set; }
    public List<string>? DefectClasses { get; set; }
    public string? QualityStatus { get; set; }
    public string? Decision { get; set; }
    public string? DestinationBin { get; set; }
    public string? RuleId { get; set; }
    public bool? Simulation { get; set; }
}

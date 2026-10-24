using SmartLine.API.Domain.Inspection;

namespace SmartLine.API.Tests.Inspection;

public class DecisionEngineTests
{
    // Kararları kamera, model veya veritabanı çalıştırmadan doğrularız.
    private static readonly ProductRecipeDefinition Recipe = ContractCatalog.DemoBox();

    public static IEnumerable<object[]> Cases() =>
        ContractCatalog.DecisionCases().Cases.Select(item => new object[] { item });

    [Fact]
    public void DemoBoxRecipeIsValid()
    {
        var errors = RecipeValidator.Validate(Recipe);
        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedFixturesMatchTheEngine(DecisionFixtureCase testCase)
    {
        // Her senaryo ayrı reçete kullanır; bir testin sınırları diğer testlere taşınmaz.
        var recipe = ContractCatalog.DemoBox();
        if (testCase.PhysicalLimits is not null)
            recipe.PhysicalLimits = testCase.PhysicalLimits;
        var result = DecisionEngine.Decide(recipe, testCase.Observation);
        foreach (var reason in testCase.ExpectedReasons) Assert.Contains(reason, result.Reasons);
        var expected = testCase.Expected;
        if (expected.ProductClass is not null)
            Assert.Equal(expected.ProductClass, result.ProductClass);
        if (expected.ColorClass is not null)
            Assert.Equal(expected.ColorClass, result.ColorClass);
        if (expected.SizeClass is not null)
            Assert.Equal(expected.SizeClass, result.SizeClass);
        if (expected.DefectClasses is not null)
            Assert.Equal(expected.DefectClasses, result.DefectClasses);
        if (expected.QualityStatus is not null)
            Assert.Equal(expected.QualityStatus, result.QualityStatus);
        if (expected.Decision is not null)
            Assert.Equal(expected.Decision, result.Decision);
        if (expected.DestinationBin is not null)
            Assert.Equal(expected.DestinationBin, result.DestinationBin);
        if (expected.RuleId is not null || testCase.Expected.DestinationBin == "MANUAL_REVIEW")
            Assert.Equal(expected.RuleId, result.RuleId);
        if (expected.Simulation is not null)
            Assert.Equal(expected.Simulation, result.Simulation);
    }

    [Fact]
    public void RejectsConfidenceOutsideTheUnitInterval()
    {
        var observation = new InspectionObservation { Confidence = 1.4 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DecisionEngine.Decide(Recipe, observation)
        );
    }

    [Fact]
    public void OverlappingSizeClassesAreRejected()
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.SizeClasses[1].MinWidthMm = 20;
        var errors = RecipeValidator.Validate(recipe);
        Assert.Contains(errors, error => error.Contains("overlaps", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownDestinationIsRejected()
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.SortingRules[0].DestinationBin = "MISSING";
        var errors = RecipeValidator.Validate(recipe);
        Assert.Contains(errors, error => error.Contains("unknown bin", StringComparison.Ordinal));
    }

    // Hatalı ölçüm geldiğinde ürünün yanlışlıkla kabul edilmesini engeller.
    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidMeasuredDimensionsAreRejected(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DecisionEngine.Decide(
                Recipe,
                new InspectionObservation { Confidence = 0.9, EstimatedWidthMm = value }
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DecisionEngine.Decide(
                Recipe,
                new InspectionObservation { Confidence = 0.9, EstimatedHeightMm = value }
            )
        );
    }

    // Eksik reçete bölümleri exception yerine doğrulama hatası üretmeli.
    [Theory]
    [InlineData("ProductClasses")]
    [InlineData("AllowedColors")]
    [InlineData("SizeClasses")]
    [InlineData("DefectClasses")]
    [InlineData("DestinationBins")]
    [InlineData("SortingRules")]
    [InlineData("Acceptance")]
    [InlineData("PhysicalLimits")]
    [InlineData("Model")]
    [InlineData("CameraProfile")]
    [InlineData("Roi")]
    [InlineData("Inspection")]
    public void ExplicitNullSectionsAreValidationErrors(string property)
    {
        var recipe = ContractCatalog.DemoBox();
        typeof(ProductRecipeDefinition).GetProperty(property)!.SetValue(recipe, null);
        Assert.NotEmpty(RecipeValidator.Validate(recipe));
    }

    [Fact]
    public void NullEntriesAndRuleConditionsAreValidationErrors()
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.ProductClasses.Add(null!);
        Assert.NotEmpty(RecipeValidator.Validate(recipe));

        recipe = ContractCatalog.DemoBox();
        recipe.SortingRules[0].When = null!;
        Assert.NotEmpty(RecipeValidator.Validate(recipe));

        recipe = ContractCatalog.DemoBox();
        recipe.Inspection.TriggerModes = null!;
        Assert.NotEmpty(RecipeValidator.Validate(recipe));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteRecipeValuesAreValidationErrors(double value)
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.PhysicalLimits.MaxWidthMm = value;
        Assert.NotEmpty(RecipeValidator.Validate(recipe));
        recipe = ContractCatalog.DemoBox();
        recipe.PhysicalLimits.MinConfidenceToSort = value;
        Assert.NotEmpty(RecipeValidator.Validate(recipe));
        recipe = ContractCatalog.DemoBox();
        recipe.Roi.X = value;
        Assert.NotEmpty(RecipeValidator.Validate(recipe));
    }

    [Fact]
    public void RecipeAndDecisionRoundTripWithoutRuntimeServices()
    {
        // Yalnızca bu modülün reçete ve kararını JSON'a çevirip geri okuruz.
        var recipe = InspectionJson.Deserialize<ProductRecipeDefinition>(
            InspectionJson.Serialize(Recipe)
        );
        Assert.Empty(RecipeValidator.Validate(recipe));
        var decision = DecisionEngine.Decide(
            recipe,
            new InspectionObservation
            {
                ProductClass = "BOX_GREEN",
                ColorClass = "GREEN",
                SizeClass = "SMALL",
                Confidence = 0.9,
            }
        );
        var restored = InspectionJson.Deserialize<DecisionResult>(
            InspectionJson.Serialize(decision)
        );
        Assert.Equal("PASS", restored.Decision);
        Assert.Equal("BIN_C", restored.DestinationBin);
        Assert.Equal(decision.Reasons, restored.Reasons);
    }

    [Fact]
    public void PhysicalConfidenceFloorDoesNotWeakenAcceptance()
    {
        // Fiziksel güven eşiğini düşürmek, reçetenin daha yüksek eşiğini devre dışı bırakmamalı.
        var recipe = ContractCatalog.DemoBox();
        recipe.PhysicalLimits.MinConfidenceToSort = 0.2;
        var result = DecisionEngine.Decide(
            recipe,
            new InspectionObservation
            {
                ProductClass = "BOX_GREEN",
                ColorClass = "GREEN",
                SizeClass = "SMALL",
                Confidence = 0.3,
            }
        );
        Assert.Equal("MANUAL_REVIEW", result.Decision);
    }

    // En öncelikli kural bile kusurlu veya kontrol bekleyen ürünü sağlam ürün bölmesine gönderememeli.
    [Theory]
    [InlineData("low-confidence", "MANUAL_REVIEW", "MANUAL_REVIEW", "MANUAL_REVIEW", null)]
    [InlineData("width", "DEFECTIVE", "REJECT", "REJECT_BIN", "any-defect")]
    [InlineData("height", "DEFECTIVE", "REJECT", "REJECT_BIN", "any-defect")]
    [InlineData("unknown-product", "DEFECTIVE", "REJECT", "REJECT_BIN", "any-defect")]
    [InlineData("defect", "DEFECTIVE", "REJECT", "REJECT_BIN", "any-defect")]
    public void CatchAllPassRuleCannotOverrideAcceptance(
        string scenario,
        string quality,
        string decision,
        string destination,
        string? ruleId
    )
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.SortingRules.Insert(
            0,
            new SortingRuleDefinition
            {
                Id = "catch-all",
                Priority = 0,
                DestinationBin = "BIN_A",
            }
        );
        var observation = GreenObservation();
        switch (scenario)
        {
            case "low-confidence":
                observation.Confidence = 0.1;
                break;
            case "width":
                observation.EstimatedWidthMm = 251;
                break;
            case "height":
                observation.EstimatedHeightMm = 251;
                break;
            case "unknown-product":
                observation.ProductClass = "MYSTERY";
                break;
            case "defect":
                observation.DefectClasses = ["DAMAGED"];
                break;
        }

        Assert.Empty(RecipeValidator.Validate(recipe));
        var result = DecisionEngine.Decide(recipe, observation);
        Assert.Equal(quality, result.QualityStatus);
        Assert.Equal(decision, result.Decision);
        Assert.Equal(destination, result.DestinationBin);
        Assert.Equal(ruleId, result.RuleId);
        Assert.DoesNotContain("RULE:catch-all", result.Reasons);
    }

    // Aynı koruma yalnızca kurallarda değil, varsayılan bölmede de uygulanmalı.
    [Theory]
    [InlineData(false, "BIN_A")]
    [InlineData(true, "BIN_A")]
    [InlineData(true, "REJECT_BIN")]
    public void UnsafeDefaultFallsBackToManualReview(bool lowConfidence, string defaultBin)
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.SortingRules.Clear();
        recipe.Acceptance.DefaultDestinationBin = defaultBin;
        var observation = GreenObservation();
        if (lowConfidence)
            observation.Confidence = 0.1;
        else
            observation.DefectClasses = ["DAMAGED"];

        Assert.Empty(RecipeValidator.Validate(recipe));
        var result = DecisionEngine.Decide(recipe, observation);
        Assert.Equal(lowConfidence ? "MANUAL_REVIEW" : "DEFECTIVE", result.QualityStatus);
        Assert.Equal("MANUAL_REVIEW", result.Decision);
        Assert.Equal("MANUAL_REVIEW", result.DestinationBin);
        Assert.Null(result.RuleId);
        Assert.Contains("DESTINATION_GUARD", result.Reasons);
    }

    [Fact]
    public void LowConfidenceCannotBeSentToRejectByACatchAllRule()
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.SortingRules.Insert(
            0,
            new SortingRuleDefinition
            {
                Id = "catch-all-reject",
                Priority = 0,
                DestinationBin = "REJECT_BIN",
            }
        );
        var observation = GreenObservation();
        observation.Confidence = 0.1;
        var result = DecisionEngine.Decide(recipe, observation);
        Assert.Equal("MANUAL_REVIEW", result.Decision);
        Assert.Equal("MANUAL_REVIEW", result.DestinationBin);
        Assert.Null(result.RuleId);
    }

    [Theory]
    [InlineData("PASS", "BIN_C", "green-good")]
    [InlineData("REJECT", "REJECT_BIN", "any-defect")]
    [InlineData("HOLD", "MANUAL_REVIEW", "review")]
    public void CompatibleRulesRetainTheirDestination(
        string role,
        string destination,
        string ruleId
    )
    {
        var recipe = ContractCatalog.DemoBox();
        var observation = GreenObservation();
        if (role == "REJECT")
            observation.DefectClasses = ["DAMAGED"];
        if (role == "HOLD")
        {
            observation.Confidence = 0.1;
            recipe.SortingRules.Add(
                new SortingRuleDefinition
                {
                    Id = "review",
                    Priority = 50,
                    When = new() { Quality = "MANUAL_REVIEW" },
                    DestinationBin = "MANUAL_REVIEW",
                }
            );
        }
        var result = DecisionEngine.Decide(recipe, observation);
        Assert.Equal(destination, result.DestinationBin);
        Assert.Equal(ruleId, result.RuleId);
        Assert.DoesNotContain("DESTINATION_GUARD", result.Reasons);
    }

    [Fact]
    public void MissingSafeDestinationFailsInsteadOfPassing()
    {
        // Reçete doğrulaması atlanmış olsa bile uygun bölme yokken PASS üretilmemeli.
        var recipe = ContractCatalog.DemoBox();
        recipe.SortingRules.Clear();
        recipe.Acceptance.DefaultDestinationBin = "BIN_A";
        recipe.DestinationBins.RemoveAll(item => item.Role == "HOLD");
        var observation = GreenObservation();
        observation.Confidence = 0.1;
        Assert.NotEmpty(RecipeValidator.Validate(recipe));
        Assert.Throws<InvalidOperationException>(() => DecisionEngine.Decide(recipe, observation));
    }

    private static InspectionObservation GreenObservation() =>
        new()
        {
            ProductClass = "BOX_GREEN",
            ColorClass = "GREEN",
            SizeClass = "SMALL",
            Confidence = 0.9,
        };
}

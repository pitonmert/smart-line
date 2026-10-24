using SmartLine.API.Domain.Inspection;

namespace SmartLine.API.Tests.Inspection;

public class RecipeValidatorTests
{
    // Yazımı geçerli olsa da reçetede tanımlı olmayan kodları kabul etmeyiz.
    [Theory]
    [InlineData("Quality", "EXCELLENT")]
    [InlineData("ProductClass", "MISSING_BOX")]
    [InlineData("ColorClass", "PURPLE")]
    [InlineData("SizeClass", "EXTRA_LARGE")]
    [InlineData("DefectClass", "SCRATCHED")]
    public void UndefinedRuleReferencesAreRejected(string property, string value)
    {
        var recipe = ContractCatalog.DemoBox();
        typeof(SortingRuleWhen).GetProperty(property)!.SetValue(recipe.SortingRules[1].When, value);
        Assert.Contains(
            RecipeValidator.Validate(recipe),
            error => error.Contains("not defined", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void UndefinedExpectedColorIsRejected()
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.ProductClasses[0].ExpectedColor = "PURPLE";
        Assert.Contains(
            RecipeValidator.Validate(recipe),
            error => error.Contains("Expected color", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ANY")]
    public void OptionalAndWildcardRuleConditionsRemainValid(string? value)
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.SortingRules[1].When = new()
        {
            Quality = value,
            ProductClass = value,
            ColorClass = value,
            SizeClass = value,
            DefectClass = value,
        };
        Assert.Empty(RecipeValidator.Validate(recipe));
    }

    [Fact]
    public void UnknownObservationCodesCanBeTargetedByRules()
    {
        // UNKNOWN, tanınamayan gözlemler için geçerli bir kural koşuludur.
        var recipe = ContractCatalog.DemoBox();
        recipe.SortingRules[1].When = new()
        {
            Quality = "MANUAL_REVIEW",
            ProductClass = "UNKNOWN",
            ColorClass = "UNKNOWN",
            SizeClass = "UNKNOWN",
        };
        Assert.Empty(RecipeValidator.Validate(recipe));
    }

    // Görünmeyen satır sonunu hem tanımlarda hem bu tanımlara verilen referanslarda kontrol ederiz.
    [Theory]
    [InlineData("product")]
    [InlineData("color")]
    [InlineData("size")]
    [InlineData("defect")]
    [InlineData("destination")]
    [InlineData("expected-color")]
    [InlineData("rule")]
    public void TrailingNewlineIsNotAValidCode(string field)
    {
        var recipe = ContractCatalog.DemoBox();
        switch (field)
        {
            case "product":
                recipe.ProductClasses[0].Code += "\n";
                break;
            case "color":
                recipe.AllowedColors[0].Code += "\n";
                break;
            case "size":
                recipe.SizeClasses[0].Code += "\n";
                break;
            case "defect":
                recipe.DefectClasses[0].Code += "\n";
                break;
            case "destination":
                recipe.DestinationBins[0].Code += "\n";
                break;
            case "expected-color":
                recipe.ProductClasses[0].ExpectedColor += "\n";
                break;
            case "rule":
                recipe.SortingRules[1].When.ColorClass = "RED\n";
                break;
        }
        Assert.Contains(
            RecipeValidator.Validate(recipe),
            error =>
                error.Contains("valid code", StringComparison.Ordinal)
                || error.Contains("uppercase", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void RecipeNeedsAManualReviewDestination()
    {
        var recipe = ContractCatalog.DemoBox();
        recipe.Acceptance.DefaultDestinationBin = "BIN_A";
        recipe.DestinationBins.RemoveAll(item => item.Role == "HOLD");
        Assert.Contains(
            RecipeValidator.Validate(recipe),
            error => error.Contains("HOLD destination", StringComparison.Ordinal)
        );
    }
}

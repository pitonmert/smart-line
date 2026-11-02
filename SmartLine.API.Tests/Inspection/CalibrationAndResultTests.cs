using SmartLine.API.Domain.Inspection;

namespace SmartLine.API.Tests.Inspection;

public class CalibrationAndResultTests
{
    [Fact]
    public void UncalibratedProfileIsSafe() =>
        Assert.Empty(CalibrationValidator.Validate(ContractCatalog.Uncalibrated()));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteCalibrationIsRejected(double value)
    {
        var calibration = ContractCatalog.Uncalibrated();
        calibration.PixelsPerMillimeter = value;
        calibration.BeltSpeedMmPerSecond = value;
        calibration.InspectionToSorterDistanceMm = value;
        Assert.True(CalibrationValidator.Validate(calibration).Count >= 3);
    }

    [Fact]
    public void ExplicitNullAndNullItemsAreRejected()
    {
        var calibration = ContractCatalog.Uncalibrated();
        calibration.GateMappings = null!;
        Assert.NotEmpty(CalibrationValidator.Validate(calibration));
        calibration.GateMappings = [null!];
        Assert.NotEmpty(CalibrationValidator.Validate(calibration));
        calibration.TofBaseline = null!;
        Assert.NotEmpty(CalibrationValidator.Validate(calibration));
    }

    [Fact]
    public void HardwareCannotBeEnabledEvenWithConfirmation()
    {
        var calibration = ContractCatalog.Uncalibrated();
        calibration.HardwareActuationEnabled = true;
        calibration.HardwareActuationConfirmed = true;
        Assert.Contains(
            CalibrationValidator.Validate(calibration),
            e => e.Contains("not integrated")
        );
    }

    [Theory]
    [InlineData("manual", false)]
    [InlineData("model", true)]
    public void ResultDoesNotInventModelMetadata(string source, bool hasModel)
    {
        var recipe = ContractCatalog.DemoBox();
        var observation = new InspectionObservation
        {
            ObservationSource = source,
            Confidence = .9,
            ProductClass = "BOX_RED",
            ModelName = "actual",
            ModelVersion = "1",
        };
        var decision = DecisionEngine.Decide(recipe, observation);
        var now = DateTime.UtcNow;
        var result = InspectionResultFactory.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            recipe,
            observation,
            decision,
            now,
            now,
            12
        );
        Assert.Equal(hasModel ? "actual" : null, result.ModelName);
        Assert.Equal(hasModel ? 12 : (int?)null, result.ProcessingLatencyMs);
        Assert.Equal(source, result.ObservationSource);
        Assert.Equal(
            result.ObservationSource,
            InspectionJson
                .Deserialize<InspectionResult>(InspectionJson.Serialize(result))
                .ObservationSource
        );
    }
}

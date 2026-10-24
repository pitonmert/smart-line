namespace SmartLine.API.Domain.Inspection;

public static class ProductionMath
{
    public static double? YieldPercent(int passCount, int rejectCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(passCount);
        ArgumentOutOfRangeException.ThrowIfNegative(rejectCount);

        if (passCount == 0 && rejectCount == 0)
            return null;

        return Math.Round(
            100d * passCount / ((double)passCount + rejectCount),
            1,
            MidpointRounding.AwayFromZero
        );
    }
}

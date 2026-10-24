namespace SmartLine.API.Domain.Inspection;

public static class SortingTiming
{
    public const double MaxBeltSpeedMmPerSecond = 5_000;
    public const double MaxDistanceMm = 20_000;

    /// <summary>
    /// Returns the travel time from the inspection point to the gate.
    /// A missing or non-positive belt speed cannot be timed. This does not command an actuator.
    /// </summary>
    public static double? DelayMilliseconds(double? distanceMm, double? beltSpeedMmPerSecond)
    {
        if (distanceMm is null || beltSpeedMmPerSecond is null)
            return null;
        // NaN ve sonsuz uzaklıklarla bir ayırma zamanı hesaplanamaz.
        if (!double.IsFinite(distanceMm.Value) || distanceMm < 0 || distanceMm > MaxDistanceMm)
            throw new ArgumentOutOfRangeException(
                nameof(distanceMm),
                "Sorter distance is outside 0..20000 mm."
            );
        // Geçerli bir hız yoksa zamanlama bilinmiyor kabul edilir.
        if (
            !double.IsFinite(beltSpeedMmPerSecond.Value)
            || beltSpeedMmPerSecond <= 0
            || beltSpeedMmPerSecond > MaxBeltSpeedMmPerSecond
        )
            return null;
        var delay = distanceMm.Value / beltSpeedMmPerSecond.Value * 1000d;
        if (!double.IsFinite(delay))
            throw new ArgumentOutOfRangeException(
                nameof(beltSpeedMmPerSecond),
                "The calculated delay must be finite."
            );
        return delay;
    }

    public static DateTime? SortDueAt(DateTime decidedAtUtc, double? delayMilliseconds)
    {
        if (delayMilliseconds is null)
            return null;
        // Negatif süre ürünü karar verilmeden önce kuyruğa hazır gösterirdi.
        if (!double.IsFinite(delayMilliseconds.Value) || delayMilliseconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(delayMilliseconds),
                "Delay must be finite and non-negative."
            );
        return DateTime
            .SpecifyKind(decidedAtUtc, DateTimeKind.Utc)
            .AddMilliseconds(delayMilliseconds.Value);
    }
}

public sealed record QueuedSortItem(
    Guid ProductId,
    DateTime QueuedAt,
    DateTime? SortDueAt,
    string DestinationBin,
    bool Simulation
);

public static class SortingQueue
{
    public static IReadOnlyList<QueuedSortItem> OrderPending(IEnumerable<QueuedSortItem> items) =>
        items
            .OrderBy(item => item.SortDueAt ?? DateTime.MaxValue)
            .ThenBy(item => item.QueuedAt)
            .ThenBy(item => item.ProductId)
            .ToList();

    public static IReadOnlyList<QueuedSortItem> Due(
        IEnumerable<QueuedSortItem> items,
        DateTime utcNow
    ) =>
        OrderPending(items)
            .Where(item => item.SortDueAt is not null && item.SortDueAt <= utcNow)
            .ToList();
}

public sealed record SortingActuationRequest(
    Guid ProductId,
    string DestinationBin,
    bool Simulation,
    bool HardwareRequested
);

public sealed record SortingActuationResult(
    bool HardwareCommandIssued,
    string Actuation,
    string Message
);

/// <summary>
/// Logical sorting stays separate from hardware. This implementation never publishes an actuator command.
/// </summary>
public static class SortingActuatorGuard
{
    public static SortingActuationResult Release(bool hardwareActuationEnabled)
    {
        if (!hardwareActuationEnabled)
        {
            return new SortingActuationResult(
                false,
                "disabled",
                "Hardware actuation is disabled. No servo, motor, fan, or gate command was sent."
            );
        }

        return new SortingActuationResult(
            false,
            "not_wired",
            "Hardware actuation is enabled in configuration, but no actuator transport is implemented. No command was sent."
        );
    }
}

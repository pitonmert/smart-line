using SmartLine.API.Domain.Entities;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Monitoring;

public sealed class StatisticsTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Barcode_ingestion_updates_daily_and_hourly_statistics_once()
    {
        using var device = Fixture.CreateClient();
        using var viewer = Fixture.CreateClient();

        var red = await device.PostAsJsonAsync(
            "/api/sensors/barcode",
            new { barcode = "STATS-RED-001", color = "RED" }
        );
        var defective = await device.PostAsJsonAsync(
            "/api/sensors/barcode",
            new { barcode = "", color = (string?)null }
        );
        await AssertSuccessAsync(red);
        await AssertSuccessAsync(defective);

        var dailyResponse = await viewer.GetAsync("/api/statistics/daily");
        var hourlyResponse = await viewer.GetAsync("/api/statistics/hourly");
        var daily = await AssertSuccessAsync(dailyResponse);
        var hourly = await AssertSuccessAsync(hourlyResponse);

        var dailyTotal = daily
            .EnumerateArray()
            .Single(item => item.GetProperty("hour").ValueKind == JsonValueKind.Null);
        Assert.Equal(2, dailyTotal.GetProperty("totalProducts").GetInt32());
        Assert.Equal(1, dailyTotal.GetProperty("defectiveProducts").GetInt32());
        Assert.Equal(1, dailyTotal.GetProperty("redProducts").GetInt32());

        var hourlyTotal = hourly.EnumerateArray().Single();
        Assert.Equal(2, hourlyTotal.GetProperty("totalProducts").GetInt32());
        Assert.Equal(1, hourlyTotal.GetProperty("defectiveProducts").GetInt32());
        Assert.Equal(1, hourlyTotal.GetProperty("redProducts").GetInt32());
    }

    [Fact]
    public async Task Daily_statistics_reject_a_range_longer_than_366_days()
    {
        using var viewer = Fixture.CreateClient();

        var response = await viewer.GetAsync(
            "/api/statistics/daily?startDate=2024-01-01&endDate=2025-01-02"
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task Daily_statistics_exclude_hourly_rows()
    {
        var date = new DateOnly(2026, 8, 9);
        await Fixture.WithDbContextAsync(async db =>
        {
            db.Statistics.AddRange(
                new Statistic
                {
                    Date = date,
                    Hour = null,
                    TotalProducts = 7,
                },
                new Statistic
                {
                    Date = date,
                    Hour = 10,
                    TotalProducts = 3,
                }
            );
            await db.SaveChangesAsync();
        });
        using var viewer = Fixture.CreateClient();

        var response = await viewer.GetAsync(
            $"/api/statistics/daily?startDate={date:yyyy-MM-dd}&endDate={date:yyyy-MM-dd}"
        );
        var rows = await AssertSuccessAsync(response);

        var row = Assert.Single(rows.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("hour").ValueKind);
        Assert.Equal(7, row.GetProperty("totalProducts").GetInt32());
    }

    [Fact]
    public async Task Concurrent_ingestion_keeps_one_daily_and_one_hourly_row()
    {
        using var device = Fixture.CreateClient();
        var requests = Enumerable
            .Range(1, 12)
            .Select(index =>
                device.PostAsJsonAsync(
                    "/api/sensors/barcode",
                    new { barcode = $"CONCURRENT-{index:D2}", color = "BLUE" }
                )
            )
            .ToArray();

        var responses = await Task.WhenAll(requests);
        try
        {
            foreach (var response in responses)
                await AssertSuccessAsync(response);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }

        var rows = await Fixture.WithDbContextAsync(db =>
            db.Statistics.AsNoTracking().OrderBy(row => row.Hour).ToListAsync()
        );
        var daily = Assert.Single(rows, row => row.Hour is null);
        var hourly = Assert.Single(rows, row => row.Hour is not null);
        Assert.Equal(2, rows.Count);
        Assert.Equal(12, daily.TotalProducts);
        Assert.Equal(12, daily.BlueProducts);
        Assert.Equal(12, hourly.TotalProducts);
        Assert.Equal(12, hourly.BlueProducts);
    }
}

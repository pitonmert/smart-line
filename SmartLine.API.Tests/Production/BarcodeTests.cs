using SmartLine.API.Tests.Infrastructure;
using SmartLine.API.Domain.Enums;

namespace SmartLine.API.Tests.Production;

public sealed class BarcodeTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Duplicate_barcode_returns_conflict_and_does_not_increment_statistics_twice()
    {
        using var client = Fixture.CreateDeviceClient();
        var payload = new { barcode = "DUPLICATE-001", color = "RED" };

        var first = await client.PostAsJsonAsync("/api/sensors/barcode", payload);
        var duplicate = await client.PostAsJsonAsync("/api/sensors/barcode", payload);

        await AssertSuccessAsync(first);
        await AssertErrorAsync(
            duplicate,
            HttpStatusCode.Conflict,
            "PRODUCT_BARCODE_DUPLICATE"
        );

        var state = await Fixture.WithDbContextAsync(async db => new
        {
            ProductCount = await db.Products.CountAsync(product =>
                product.Barcode == "DUPLICATE-001"
            ),
            DailyTotal = await db.Statistics
                .Where(statistic => statistic.Hour == null)
                .SumAsync(statistic => statistic.TotalProducts),
            HourlyTotal = await db.Statistics
                .Where(statistic => statistic.Hour != null)
                .SumAsync(statistic => statistic.TotalProducts),
        });
        Assert.Equal(1, state.ProductCount);
        Assert.Equal(1, state.DailyTotal);
        Assert.Equal(1, state.HourlyTotal);
    }

    [Fact]
    public async Task Concurrent_duplicate_barcode_allows_exactly_one_product()
    {
        using var client = Fixture.CreateDeviceClient();
        const string barcode = "CONCURRENT-001";

        var requests = Enumerable
            .Range(0, 8)
            .Select(_ =>
                client.PostAsJsonAsync(
                    "/api/sensors/barcode",
                    new { barcode, color = "BLUE" }
                )
            );
        var responses = await Task.WhenAll(requests);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(
            7,
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict)
        );
        foreach (var conflict in responses.Where(response =>
                     response.StatusCode == HttpStatusCode.Conflict
                 ))
        {
            var json = await ReadJsonAsync(conflict);
            Assert.Equal("PRODUCT_BARCODE_DUPLICATE", StringProperty(json, "code"));
        }

        var productCount = await Fixture.WithDbContextAsync(db =>
            db.Products.CountAsync(product => product.Barcode == barcode)
        );
        var dailyTotal = await Fixture.WithDbContextAsync(db =>
            db.Statistics
                .Where(statistic => statistic.Hour == null)
                .SumAsync(statistic => statistic.TotalProducts)
        );
        Assert.Equal(1, productCount);
        Assert.Equal(1, dailyTotal);
    }

    [Fact]
    public async Task Comma_separated_color_value_is_rejected_instead_of_matching_a_flag_combination()
    {
        using var client = Fixture.CreateDeviceClient();

        var response = await client.PostAsJsonAsync(
            "/api/sensors/barcode",
            new { barcode = "COMMA-COLOR-001", color = "RED,GREEN" }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PRODUCT_COLOR_INVALID");
    }

    [Fact]
    public async Task Empty_barcode_creates_a_no_barcode_product_and_alert_atomically()
    {
        using var client = Fixture.CreateDeviceClient();

        var response = await client.PostAsJsonAsync(
            "/api/sensors/barcode",
            new { barcode = "", color = (string?)null }
        );

        await AssertSuccessAsync(response);
        var state = await Fixture.WithDbContextAsync(async db => new
        {
            Products = await db.Products.CountAsync(product =>
                product.Status == ProductStatus.NO_BARCODE
            ),
            Alerts = await db.Alerts.CountAsync(alert =>
                alert.AlertType == AlertType.NO_BARCODE_DETECTED && alert.IsActive
            ),
            DailyTotal = await db.Statistics
                .Where(statistic => statistic.Hour == null)
                .SumAsync(statistic => statistic.TotalProducts),
        });
        Assert.Equal(1, state.Products);
        Assert.Equal(1, state.Alerts);
        Assert.Equal(1, state.DailyTotal);
    }
}

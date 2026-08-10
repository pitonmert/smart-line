using SmartLine.API.Domain.Enums;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Contracts;

public sealed class PaginationContractTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Product_pagination_normalizes_page_and_caps_page_size_at_one_hundred()
    {
        await SeedProductsAsync(105);
        using var client = Fixture.CreateUserClient("Viewer");

        var response = await client.GetAsync("/api/products?pageNumber=0&pageSize=500");
        var page = await AssertSuccessAsync(response);

        Assert.Equal(100, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("hasNextPage").GetBoolean());
        Assert.False(page.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(1, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal(100, page.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Product_pagination_returns_a_deterministic_second_page()
    {
        await SeedProductsAsync(15);
        using var client = Fixture.CreateUserClient("Viewer");

        var response = await client.GetAsync("/api/products?pageNumber=2&pageSize=10");
        var page = await AssertSuccessAsync(response);

        Assert.Equal(5, page.GetProperty("items").GetArrayLength());
        Assert.False(page.GetProperty("hasNextPage").GetBoolean());
        Assert.True(page.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(2, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal(10, page.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("/api/products/defective?pageNumber=1&pageSize=10")]
    [InlineData("/api/products/status/NO_BARCODE?pageNumber=1&pageSize=10")]
    [InlineData("/api/products/color/RED?pageNumber=1&pageSize=10")]
    public async Task Filtered_product_collections_use_the_same_page_contract(string path)
    {
        await SeedProductsAsync(12, ProductStatus.NO_BARCODE);
        using var client = Fixture.CreateUserClient("Viewer");

        var response = await client.GetAsync(path);
        var page = await AssertSuccessAsync(response);

        Assert.Equal(10, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(1, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal(10, page.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("/api/products/defective", 10)]
    [InlineData("/api/products/status/NO_BARCODE", 10)]
    [InlineData("/api/products/color/RED", 10)]
    [InlineData("/api/alerts/active", 20)]
    [InlineData("/api/alerts/type/FIRE", 20)]
    [InlineData("/api/logs/severity/Info", 20)]
    [InlineData("/api/logs/event/PRODUCT_PASSED", 20)]
    public async Task Every_filtered_collection_has_a_bounded_default_page(
        string path,
        int defaultPageSize
    )
    {
        await SeedFilteredCollectionsAsync(25);
        using var client = Fixture.CreateUserClient("Viewer");

        var page = await AssertSuccessAsync(await client.GetAsync(path));

        Assert.Equal(defaultPageSize, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(1, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal(defaultPageSize, page.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("/api/products/defective?pageNumber=2&pageSize=10")]
    [InlineData("/api/products/status/NO_BARCODE?pageNumber=2&pageSize=10")]
    [InlineData("/api/products/color/RED?pageNumber=2&pageSize=10")]
    [InlineData("/api/alerts/active?pageNumber=2&pageSize=10")]
    [InlineData("/api/alerts/type/FIRE?pageNumber=2&pageSize=10")]
    [InlineData("/api/logs/severity/Info?pageNumber=2&pageSize=10")]
    [InlineData("/api/logs/event/PRODUCT_PASSED?pageNumber=2&pageSize=10")]
    public async Task Every_filtered_collection_returns_a_correct_second_page(string path)
    {
        await SeedFilteredCollectionsAsync(15);
        using var client = Fixture.CreateUserClient("Viewer");

        var page = await AssertSuccessAsync(await client.GetAsync(path));

        Assert.Equal(5, page.GetProperty("items").GetArrayLength());
        Assert.False(page.GetProperty("hasNextPage").GetBoolean());
        Assert.True(page.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(2, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal(10, page.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("/api/products/defective")]
    [InlineData("/api/products/status/NO_BARCODE")]
    [InlineData("/api/products/color/RED")]
    [InlineData("/api/alerts/active")]
    [InlineData("/api/alerts/type/FIRE")]
    [InlineData("/api/logs/severity/Info")]
    [InlineData("/api/logs/event/PRODUCT_PASSED")]
    public async Task Every_filtered_collection_normalizes_page_and_caps_page_size(string path)
    {
        await SeedFilteredCollectionsAsync(105);
        using var client = Fixture.CreateUserClient("Viewer");

        var page = await AssertSuccessAsync(
            await client.GetAsync($"{path}?pageNumber=0&pageSize=500")
        );

        Assert.Equal(100, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("hasNextPage").GetBoolean());
        Assert.False(page.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(1, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal(100, page.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Collection_order_is_stable_when_timestamps_are_equal()
    {
        await SeedFilteredCollectionsAsync(3);
        using var client = Fixture.CreateUserClient("Viewer");

        var products = await AssertSuccessAsync(
            await client.GetAsync("/api/products/status/NO_BARCODE?pageSize=3")
        );
        var alerts = await AssertSuccessAsync(
            await client.GetAsync("/api/alerts/type/FIRE?pageSize=3")
        );
        var logs = await AssertSuccessAsync(
            await client.GetAsync("/api/logs/event/PRODUCT_PASSED?pageSize=3")
        );

        AssertDescendingIds(products);
        AssertDescendingIds(alerts);
        AssertDescendingIds(logs);
    }

    private Task SeedProductsAsync(int count, ProductStatus status = ProductStatus.OK) =>
        Fixture.WithDbContextAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var products = Enumerable.Range(1, count).Select(index => new Product
            {
                Barcode = $"PAGE-{index:D3}",
                Color = ProductColor.RED,
                Status = status,
                PassedAt = now.AddSeconds(-index),
            });
            await db.Products.AddRangeAsync(products);
            await db.SaveChangesAsync();
        });

    private Task SeedFilteredCollectionsAsync(int count) =>
        Fixture.WithDbContextAsync(async db =>
        {
            var timestamp = DateTime.UtcNow;
            await db.Products.AddRangeAsync(
                Enumerable.Range(1, count).Select(index => new Product
                {
                    Barcode = $"FILTER-{index:D3}",
                    Color = ProductColor.RED,
                    Status = ProductStatus.NO_BARCODE,
                    PassedAt = timestamp,
                })
            );
            await db.Alerts.AddRangeAsync(
                Enumerable.Range(1, count).Select(_ => new Alert
                {
                    AlertType = AlertType.FIRE,
                    IsActive = true,
                    TriggeredAt = timestamp,
                })
            );
            await db.SystemLogs.AddRangeAsync(
                Enumerable.Range(1, count).Select(_ => new SystemLog
                {
                    EventType = EventType.PRODUCT_PASSED,
                    Severity = "Info",
                    CreatedAt = timestamp,
                })
            );
            await db.SaveChangesAsync();
        });

    private static void AssertDescendingIds(JsonElement page)
    {
        var ids = page
            .GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32())
            .ToArray();
        Assert.Equal(ids.OrderByDescending(id => id), ids);
    }
}

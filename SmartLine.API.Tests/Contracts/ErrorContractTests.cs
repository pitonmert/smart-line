using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SmartLine.API.Shared;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Contracts;

public sealed class ErrorContractTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Missing_product_returns_stable_not_found_code()
    {
        using var client = Fixture.CreateClient();

        var response = await client.GetAsync("/api/products/2147483647");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Invalid_product_color_returns_stable_code()
    {
        using var client = Fixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new
            {
                barcode = "INVALID-COLOR-1",
                color = "PURPLE",
                status = "OK",
            }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PRODUCT_COLOR_INVALID");
    }

    [Fact]
    public async Task Invalid_product_status_returns_stable_code()
    {
        using var client = Fixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new
            {
                barcode = "INVALID-STATUS-1",
                color = "RED",
                status = "BROKEN",
            }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PRODUCT_STATUS_INVALID");
    }

    [Fact]
    public async Task Whitespace_padded_barcode_is_rejected_as_duplicate_before_insert()
    {
        using var client = Fixture.CreateClient();
        await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/products",
                new
                {
                    barcode = "PADDED-BARCODE-1",
                    color = "RED",
                    status = "OK",
                }
            ),
            HttpStatusCode.Created
        );

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new
            {
                barcode = "  PADDED-BARCODE-1  ",
                color = "RED",
                status = "OK",
            }
        );

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "PRODUCT_BARCODE_DUPLICATE");
    }

    [Fact]
    public async Task Structurally_invalid_request_returns_request_invalid_code()
    {
        using var client = Fixture.CreateClient();
        using var content = new StringContent("{", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/products", content);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Non_numeric_speed_returns_request_invalid_code()
    {
        using var client = Fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/conveyor/speed", new { speed = "fast" });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task Speed_change_while_running_returns_domain_code()
    {
        using var client = Fixture.CreateClient();
        await AssertSuccessAsync(await client.PostAsync("/api/conveyor/start", null));

        var response = await client.PostAsJsonAsync("/api/conveyor/speed", new { speed = 60 });

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "CONVEYOR_MUST_BE_STOPPED");
    }

    [Fact]
    public async Task Speed_change_without_speed_field_is_rejected_instead_of_defaulting_to_zero()
    {
        using var client = Fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/conveyor/speed", new { });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Theory]
    [InlineData("1", null, "PRODUCT_STATUS_INVALID")]
    [InlineData("OK", "1", "PRODUCT_COLOR_INVALID")]
    public async Task Numeric_enum_values_are_rejected_in_product_bodies(
        string status,
        string? color,
        string expectedCode
    )
    {
        using var client = Fixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new
            {
                barcode = $"NUMERIC-{status}-{color}",
                color,
                status,
            }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, expectedCode);
    }

    [Theory]
    [InlineData("/api/products/status/1", "PRODUCT_STATUS_INVALID")]
    [InlineData("/api/products/color/1", "PRODUCT_COLOR_INVALID")]
    [InlineData("/api/alerts/type/1", "REQUEST_INVALID")]
    [InlineData("/api/logs/event/1", "REQUEST_INVALID")]
    public async Task Numeric_enum_values_are_rejected_in_paths(string path, string expectedCode)
    {
        using var client = Fixture.CreateClient();

        var response = await client.GetAsync(path);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, expectedCode);
    }

    [Fact]
    public async Task Numeric_color_is_rejected_in_device_barcode_payload()
    {
        using var client = Fixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/sensors/barcode",
            new { barcode = "NUMERIC-DEVICE-1", color = "1" }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PRODUCT_COLOR_INVALID");
    }

    [Fact]
    public async Task Unexpected_exception_is_hidden_behind_the_500_error_contract()
    {
        const string sensitiveDetail = "sensitive synthetic detail";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseApiPipeline();
        app.MapGet("/failure", IResult () => throw new InvalidOperationException(sensitiveDetail));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/failure");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.False(json.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("UNEXPECTED_ERROR", StringProperty(json, "code"));
        Assert.DoesNotContain(
            sensitiveDetail,
            json.GetProperty("message").GetString(),
            StringComparison.Ordinal
        );
    }
}

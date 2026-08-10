using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SmartLine.API.Shared;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Contracts;

public sealed class ErrorContractTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Missing_product_returns_stable_not_found_code()
    {
        using var client = Fixture.CreateUserClient("Viewer");

        var response = await client.GetAsync("/api/products/2147483647");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Invalid_product_color_returns_stable_code()
    {
        using var client = Fixture.CreateUserClient("Admin");

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new { barcode = "INVALID-COLOR-1", color = "PURPLE", status = "OK" }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PRODUCT_COLOR_INVALID");
    }

    [Fact]
    public async Task Invalid_product_status_returns_stable_code()
    {
        using var client = Fixture.CreateUserClient("Admin");

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new { barcode = "INVALID-STATUS-1", color = "RED", status = "BROKEN" }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PRODUCT_STATUS_INVALID");
    }

    [Fact]
    public async Task Whitespace_padded_barcode_is_rejected_as_duplicate_before_insert()
    {
        using var client = Fixture.CreateUserClient("Admin");
        await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/products",
                new { barcode = "PADDED-BARCODE-1", color = "RED", status = "OK" }
            ),
            HttpStatusCode.Created
        );

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new { barcode = "  PADDED-BARCODE-1  ", color = "RED", status = "OK" }
        );

        await AssertErrorAsync(
            response,
            HttpStatusCode.Conflict,
            "PRODUCT_BARCODE_DUPLICATE"
        );
    }

    [Fact]
    public async Task Structurally_invalid_request_returns_request_invalid_code()
    {
        using var client = Fixture.CreateUserClient("Admin");
        using var content = new StringContent("{", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/products", content);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task Speed_change_while_running_returns_domain_code()
    {
        using var client = Fixture.CreateUserClient("Operator");
        await AssertSuccessAsync(await client.PostAsync("/api/conveyor/start", null));

        var response = await client.PostAsJsonAsync(
            "/api/conveyor/speed",
            new { speed = 60 }
        );

        await AssertErrorAsync(
            response,
            HttpStatusCode.Conflict,
            "CONVEYOR_MUST_BE_STOPPED"
        );
    }

    [Fact]
    public async Task Speed_change_without_speed_field_is_rejected_instead_of_defaulting_to_zero()
    {
        using var client = Fixture.CreateUserClient("Operator");

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
        using var client = Fixture.CreateUserClient("Operator");

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new { barcode = $"NUMERIC-{status}-{color}", color, status }
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
        using var client = Fixture.CreateUserClient("Viewer");

        var response = await client.GetAsync(path);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, expectedCode);
    }

    [Fact]
    public async Task Numeric_color_is_rejected_in_device_barcode_payload()
    {
        using var client = Fixture.CreateDeviceClient();

        var response = await client.PostAsJsonAsync(
            "/api/sensors/barcode",
            new { barcode = "NUMERIC-DEVICE-1", color = "1" }
        );

        await AssertErrorAsync(
            response,
            HttpStatusCode.BadRequest,
            "PRODUCT_COLOR_INVALID"
        );
    }

    [Fact]
    public async Task Rate_limit_rejection_uses_the_error_envelope()
    {
        await using var isolated = Fixture.CreateIsolatedAnonymousClient();
        HttpResponseMessage? rejected = null;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var response = await isolated.Client.PostAsJsonAsync(
                "/api/auth/login",
                new { email = "nobody@smartline.test", password = "invalid" }
            );
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
                break;
            }
            response.Dispose();
        }

        Assert.NotNull(rejected);
        using (rejected)
            await AssertErrorAsync(
                rejected,
                HttpStatusCode.TooManyRequests,
                "RATE_LIMIT_EXCEEDED"
            );
    }

    [Fact]
    public async Task Unexpected_exception_is_hidden_behind_the_500_error_contract()
    {
        const string sensitiveDetail = "sensitive synthetic detail";
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException(sensitiveDetail),
            NullLogger<ExceptionHandlingMiddleware>.Instance
        );
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var json = await JsonSerializer.DeserializeAsync<JsonElement>(
            context.Response.Body,
            JsonOptions
        );
        Assert.False(json.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("UNEXPECTED_ERROR", StringProperty(json, "code"));
        Assert.DoesNotContain(
            sensitiveDetail,
            json.GetProperty("message").GetString(),
            StringComparison.Ordinal
        );
    }
}

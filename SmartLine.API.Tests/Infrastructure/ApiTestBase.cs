namespace SmartLine.API.Tests.Infrastructure;

[Collection(ApiCollection.Name)]
public abstract class ApiTestBase(ApiFixture fixture) : IAsyncLifetime
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected ApiFixture Fixture { get; } = fixture;

    public Task InitializeAsync() => Fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<JsonElement>(stream, JsonOptions);
    }

    protected static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    protected static async Task<JsonElement> AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code
    )
    {
        Assert.Equal(status, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.False(json.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(code, StringProperty(json, "code"));
        Assert.False(string.IsNullOrWhiteSpace(StringProperty(json, "message")));
        return json;
    }

    protected static async Task<JsonElement> AssertSuccessAsync(
        HttpResponseMessage response,
        HttpStatusCode status = HttpStatusCode.OK
    )
    {
        Assert.Equal(status, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.True(json.GetProperty("isSuccess").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(StringProperty(json, "message")));
        return json.GetProperty("data");
    }
}

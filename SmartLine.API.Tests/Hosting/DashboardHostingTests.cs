using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Hosting;

public sealed class DashboardHostingTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task DashboardAssetsAndApiAreAvailableWithoutAuthentication()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "smartline-dashboard",
            Guid.NewGuid().ToString()
        );
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "index.html"),
                "<html>Dashboard shell</html>"
            );
            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "console.log('dashboard');");
            await using var app = Fixture.CreateDashboardClient(root);

            foreach (var path in new[] { "/", "/recipes", "/dataset/session" })
            {
                using var response = await app.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
                Assert.Contains("Dashboard shell", await response.Content.ReadAsStringAsync());
                Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            }

            using var asset = await app.Client.GetAsync("/app.js");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Contains("console.log", await asset.Content.ReadAsStringAsync());

            foreach (
                var path in new[]
                {
                    "/api/not-found",
                    "/hubs/not-found",
                    "/health/not-found",
                    "/missing.js",
                }
            )
            {
                using var response = await app.Client.GetAsync(path);
                await AssertErrorAsync(response, HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND");
            }

            using var recipes = await app.Client.GetAsync("/api/recipes");
            Assert.Equal(HttpStatusCode.OK, recipes.StatusCode);

            using var post = await app.Client.PostAsync("/recipes", null);
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

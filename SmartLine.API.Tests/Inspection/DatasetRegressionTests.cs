using SmartLine.API.Features.Inspection;
using SmartLine.API.Shared;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Inspection;

public sealed class DatasetRegressionTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static object Start(
        string product = "BOX_RED",
        string mode = "manual",
        int? interval = null
    ) =>
        new
        {
            productClass = product,
            qualityLabel = "GOOD",
            colorLabel = "RED",
            sizeLabel = "LARGE",
            captureMode = mode,
            intervalMs = interval,
        };

    [Fact]
    public async Task UnknownLabelsAndMissingIntervalAreRejected()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateClient();
        foreach (var body in new[] { Start("PURPLE"), Start(mode: "interval") })
            await AssertErrorAsync(
                await client.PostAsJsonAsync("/api/dataset/sessions", body),
                HttpStatusCode.BadRequest,
                ErrorCodes.DatasetInvalid
            );
        Assert.Equal(0, await Fixture.WithDbContextAsync(db => db.DatasetSessions.CountAsync()));
    }

    [Fact]
    public async Task ConcurrentStartCreatesOnlyOneSessionAndDiskUploadIsNotFound()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateClient();
        var responses = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ => client.PostAsJsonAsync("/api/dataset/sessions", Start()))
        );
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var data = await AssertSuccessAsync(
            responses.Single(r => r.StatusCode == HttpStatusCode.Created),
            HttpStatusCode.Created
        );
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent([0xff, 0xd8, 1, 2, 0xff, 0xd9]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(image, "image", "fake.jpg");
        await AssertErrorAsync(
            await client.PostAsync(
                $"/api/dataset/sessions/{data.GetProperty("sessionId").GetGuid()}/captures",
                form
            ),
            HttpStatusCode.NotFound,
            ErrorCodes.ResourceNotFound
        );
        Assert.Equal(
            0,
            await Fixture.WithDbContextAsync(db =>
                db.DatasetSessions.SumAsync(session => session.ImageCount)
            )
        );
    }

    [Fact]
    public async Task ExistingPngImageRemainsReadable()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateClient();
        var data = await AssertSuccessAsync(
            await client.PostAsJsonAsync("/api/dataset/sessions", Start()),
            HttpStatusCode.Created
        );
        var id = data.GetProperty("sessionId").GetGuid();
        var path = await Fixture.WithDbContextAsync(db =>
            db.DatasetSessions.Where(s => s.Id == id).Select(s => s.StoragePath).SingleAsync()
        );
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aQ1sAAAAASUVORK5CYII="
        );
        await File.WriteAllBytesAsync(Path.Combine(path, "images", "legacy.png"), png);

        var response = await client.GetAsync($"/api/dataset/sessions/{id}/images/legacy.png");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MissingImageRequiresRecoveryWithoutBlockingOtherApiFunctions()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateClient();
        var data = await AssertSuccessAsync(
            await client.PostAsJsonAsync("/api/dataset/sessions", Start()),
            HttpStatusCode.Created
        );
        var id = data.GetProperty("sessionId").GetGuid();
        Fixture.GetRequiredService<ScriptedVisionFrames>().Jpeg = TestImages.Jpeg();
        await AssertSuccessAsync(
            await client.PostAsJsonAsync($"/api/dataset/sessions/{id}/captures/camera", Start())
        );
        var path = await Fixture.WithDbContextAsync(db =>
            db.DatasetSessions.Where(s => s.Id == id).Select(s => s.StoragePath).SingleAsync()
        );
        File.Delete(Directory.GetFiles(Path.Combine(path, "images")).Single());
        await Fixture.WithDbContextAsync(db =>
            new DatasetStore(
                db,
                Microsoft.Extensions.Options.Options.Create(
                    new SmartLine.API.Infrastructure.Configuration.DatasetOptions
                    {
                        Root = Path.GetDirectoryName(path)!,
                    }
                )
            ).RecoverAsync(CancellationToken.None)
        );
        Assert.Equal(
            "RECOVERY_REQUIRED",
            await Fixture.WithDbContextAsync(db =>
                db.DatasetSessions.Where(s => s.Id == id).Select(s => s.Status).SingleAsync()
            )
        );
        await AssertSuccessAsync(await client.GetAsync("/api/recipes"));
    }

    [Fact]
    public async Task DatabaseFailureRemovesNewImageAndManifest()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateClient();
        var data = await AssertSuccessAsync(
            await client.PostAsJsonAsync("/api/dataset/sessions", Start()),
            HttpStatusCode.Created
        );
        var id = data.GetProperty("sessionId").GetGuid();
        var path = await Fixture.WithDbContextAsync(db =>
            db.DatasetSessions.Where(s => s.Id == id).Select(s => s.StoragePath).SingleAsync()
        );
        await Fixture.WithDbContextAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE FUNCTION fail_capture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$"
            );
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_capture BEFORE UPDATE ON \"DatasetSessions\" FOR EACH ROW EXECUTE FUNCTION fail_capture()"
            );
        });
        try
        {
            Fixture.GetRequiredService<ScriptedVisionFrames>().Jpeg = TestImages.Jpeg();
            Assert.Equal(
                HttpStatusCode.Conflict,
                (
                    await client.PostAsJsonAsync(
                        $"/api/dataset/sessions/{id}/captures/camera",
                        Start()
                    )
                ).StatusCode
            );
            Assert.Empty(Directory.GetFiles(Path.Combine(path, "images")));
            Assert.False(File.Exists(Path.Combine(path, "manifest.jsonl")));
        }
        finally
        {
            await Fixture.WithDbContextAsync(async db =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DROP TRIGGER reject_capture ON \"DatasetSessions\""
                );
                await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_capture()");
            });
        }
    }
}

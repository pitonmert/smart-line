using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Inspection;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Infrastructure.Messaging.Mqtt;
using SmartLine.API.Shared;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Inspection;

public sealed class InspectionPlatformTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task RecipesAreAvailableWithoutAuthentication()
    {
        var response = await Fixture.CreateClient().GetAsync("/api/recipes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DemoBoxIsSeededAndInvalidRecipesAreRejected()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var viewer = Fixture.CreateClient();
        var recipes = await AssertSuccessAsync(await viewer.GetAsync("/api/recipes"));
        Assert.Equal("DemoBox", recipes.EnumerateArray().Single().GetProperty("name").GetString());

        var invalid = await Fixture
            .CreateClient()
            .PostAsJsonAsync(
                "/api/recipes",
                new
                {
                    name = "Broken",
                    definition = new
                    {
                        name = "Broken",
                        productClasses = Array.Empty<object>(),
                        destinationBins = Array.Empty<object>(),
                    },
                }
            );
        await AssertErrorAsync(invalid, HttpStatusCode.BadRequest, ErrorCodes.RecipeInvalid);
    }

    [Fact]
    public async Task SimulationArrivalIsMarkedAndDoesNotCommandActuators()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateClient();
        var created = await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/simulation/arrivals",
                new
                {
                    productClass = "BOX_RED",
                    colorClass = "RED",
                    sizeClass = "LARGE",
                    confidence = 0.93,
                    distanceMm = 1000,
                    beltSpeedMmPerSecond = 200,
                }
            ),
            HttpStatusCode.Created
        );

        var inspection = created.GetProperty("inspection");
        Assert.True(inspection.GetProperty("simulation").GetBoolean());
        Assert.Equal("PASS", inspection.GetProperty("decision").GetString());
        Assert.Equal("BIN_A", inspection.GetProperty("destinationBin").GetString());
        Assert.Equal(
            "QUEUED_FOR_SORT",
            created.GetProperty("track").GetProperty("stage").GetString()
        );
        Assert.False(
            created.GetProperty("actuation").GetProperty("hardwareCommandIssued").GetBoolean()
        );
        Assert.DoesNotContain(
            Fixture.Mqtt.Calls,
            call =>
                call.Operation
                    is nameof(IMqttService.PublishAsync)
                        or nameof(IMqttService.PublishSpeedChangeAsync)
                        or nameof(IMqttService.PublishFanAsync)
                        or nameof(IMqttService.PublishConveyorStateAsync)
        );

        var tick = await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/simulation/tick",
                new { utcNow = DateTime.UtcNow.AddMinutes(1) }
            )
        );
        Assert.Equal(1, tick.GetProperty("sortedCount").GetInt32());
        Assert.False(
            tick.GetProperty("actuation").GetProperty("hardwareCommandIssued").GetBoolean()
        );

        var history = await AssertSuccessAsync(
            await client.GetAsync("/api/inspections?simulation=true")
        );
        Assert.Equal(
            "BOX_RED",
            history
                .GetProperty("items")
                .EnumerateArray()
                .Single()
                .GetProperty("productClass")
                .GetString()
        );

        var room = await AssertSuccessAsync(await client.GetAsync("/api/control-room"));
        Assert.True(room.GetProperty("simulationActive").GetBoolean());
        Assert.Equal(
            "BIN_A",
            room.GetProperty("lastInspection").GetProperty("destinationBin").GetString()
        );
        Assert.Equal(0, room.GetProperty("totalCount").GetInt32());
        Assert.False(
            room.TryGetProperty("yieldPercent", out var yield)
                && yield.ValueKind == JsonValueKind.Number
        );
    }

    [Fact]
    public async Task DeviceCanSubmitARealInspectionWithoutAFakeSensorReading()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var created = await AssertSuccessAsync(
            await Fixture
                .CreateClient()
                .PostAsJsonAsync(
                    "/api/inspections",
                    new
                    {
                        productClass = "BOX_GREEN",
                        colorClass = "GREEN",
                        sizeClass = "SMALL",
                        confidence = 0.84,
                        triggerSource = "api",
                        simulation = false,
                        sourceWidth = 640,
                        sourceHeight = 480,
                        frameRole = "inspection",
                        captureSource = "esp32_capture",
                        previewFallback = false,
                    }
                ),
            HttpStatusCode.Created
        );
        var inspection = created.GetProperty("inspection");
        Assert.False(inspection.GetProperty("simulation").GetBoolean());
        Assert.Equal("BIN_C", inspection.GetProperty("destinationBin").GetString());
        Assert.False(created.GetProperty("track").GetProperty("simulation").GetBoolean());
        Assert.True(inspection.GetProperty("recipeVersion").GetInt32() >= 1);
        Assert.Equal("inspection", inspection.GetProperty("frameRole").GetString());
        Assert.Equal("esp32_capture", inspection.GetProperty("captureSource").GetString());
        Assert.False(inspection.GetProperty("previewFallback").GetBoolean());
        Assert.Equal(640, inspection.GetProperty("geometry").GetProperty("sourceWidth").GetInt32());
        Assert.Equal(
            480,
            inspection.GetProperty("geometry").GetProperty("sourceHeight").GetInt32()
        );
    }

    [Fact]
    public async Task ActiveRecipeSnapshotIsAvailableWithoutDeviceKey()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var device = await AssertSuccessAsync(
            await Fixture.CreateClient().GetAsync("/api/recipes/active/snapshot")
        );
        Assert.Equal("DemoBox", device.GetProperty("name").GetString());
        Assert.True(device.GetProperty("version").GetInt32() >= 1);
        Assert.True(
            device.GetProperty("definition").GetProperty("productClasses").GetArrayLength() > 0
        );
    }

    [Fact]
    public async Task CalibrationRejectsHardwareTimeOfFlightAndEnabledGates()
    {
        var response = await Fixture
            .CreateClient()
            .PostAsJsonAsync(
                "/api/calibration-profiles",
                new
                {
                    name = "Unsafe",
                    definition = new
                    {
                        orientationDegrees = 45,
                        hardwareActuationEnabled = true,
                        gateMappings = new[]
                        {
                            new
                            {
                                binCode = "BIN_A",
                                servoDegrees = 90,
                                enabled = true,
                            },
                        },
                        tofBaseline = new { source = "HARDWARE", distanceMm = 42 },
                    },
                }
            );
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.CalibrationInvalid);
    }

    [Fact]
    public async Task DatasetCaptureStoresAnUnannotatedImage()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateClient();
        var started = await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/dataset/sessions",
                new
                {
                    productClass = "BOX_BLUE",
                    qualityLabel = "GOOD",
                    colorLabel = "BLUE",
                    sizeLabel = "MEDIUM",
                    captureMode = "manual",
                    simulation = false,
                }
            ),
            HttpStatusCode.Created
        );
        var sessionId = started.GetProperty("sessionId").GetGuid();
        var frames = Fixture.GetRequiredService<ScriptedVisionFrames>();
        frames.Jpeg = TestImages.Jpeg();
        var captured = await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                $"/api/dataset/sessions/{sessionId}/captures/camera",
                new
                {
                    productClass = "BOX_BLUE",
                    qualityLabel = "GOOD",
                    colorLabel = "BLUE",
                    sizeLabel = "MEDIUM",
                }
            )
        );
        var session = captured.GetProperty("session");
        Assert.Equal(1, session.GetProperty("imageCount").GetInt32());
        Assert.Equal(1, session.GetProperty("classCounts").GetProperty("BOX_BLUE").GetInt32());
        Assert.Contains(
            "not inferred",
            session.GetProperty("labelStatus").GetString(),
            StringComparison.Ordinal
        );
        var path = await Fixture.WithDbContextAsync(db =>
            db.DatasetSessions.Where(item => item.Id == sessionId)
                .Select(item => item.StoragePath)
                .SingleAsync()
        );
        var entry = InspectionJson.Deserialize<DatasetManifestEntry>(
            File.ReadAllLines(Path.Combine(path, "manifest.jsonl")).Single()
        );
        Assert.False(entry.Annotated);
        Assert.Empty(entry.BoundingBoxes);

        frames.Jpeg = [1, 2, 3, 4];
        var rejected = await client.PostAsJsonAsync(
            $"/api/dataset/sessions/{sessionId}/captures/camera",
            new
            {
                productClass = "BOX_BLUE",
                qualityLabel = "GOOD",
                colorLabel = "BLUE",
                sizeLabel = "MEDIUM",
            }
        );
        await AssertErrorAsync(rejected, HttpStatusCode.BadRequest, ErrorCodes.DatasetInvalid);
        Assert.Equal(
            1,
            await Fixture.WithDbContextAsync(db =>
                db.DatasetSessions.Where(item => item.Id == sessionId)
                    .Select(item => item.ImageCount)
                    .SingleAsync()
            )
        );
    }

    [Fact]
    public async Task CameraCaptureStoresOneFrameAndCanDiscardIt()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var frames = Fixture.GetRequiredService<ScriptedVisionFrames>();
        frames.FailStill = false;
        frames.Jpeg = TestImages.Jpeg();
        var client = Fixture.CreateClient();
        var started = await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/dataset/sessions",
                new
                {
                    productClass = "BOX_RED",
                    qualityLabel = "GOOD",
                    colorLabel = "RED",
                    sizeLabel = "LARGE",
                    captureMode = "manual",
                    simulation = false,
                }
            ),
            HttpStatusCode.Created
        );
        var sessionId = started.GetProperty("sessionId").GetGuid();
        var captured = await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                $"/api/dataset/sessions/{sessionId}/captures/camera",
                new
                {
                    productClass = "BOX_BLUE",
                    qualityLabel = "DEFECTIVE",
                    colorLabel = "BLUE",
                    sizeLabel = "MEDIUM",
                    defectLabels = new[] { "DAMAGED" },
                    notes = "bench sample",
                }
            )
        );
        Assert.Equal("dataset", frames.LastRole);
        Assert.Equal(1, frames.Reads);
        Assert.Equal(1, captured.GetProperty("session").GetProperty("imageCount").GetInt32());
        Assert.Equal(
            1,
            captured
                .GetProperty("session")
                .GetProperty("classCounts")
                .GetProperty("BOX_BLUE")
                .GetInt32()
        );
        Assert.Equal(
            1,
            captured
                .GetProperty("session")
                .GetProperty("qualityCounts")
                .GetProperty("DEFECTIVE")
                .GetInt32()
        );
        Assert.Equal(
            1,
            captured
                .GetProperty("session")
                .GetProperty("defectCounts")
                .GetProperty("DAMAGED")
                .GetInt32()
        );
        var sample = captured.GetProperty("sample");
        Assert.Equal("camera", sample.GetProperty("trigger").GetString());
        Assert.Equal("dataset", sample.GetProperty("frameRole").GetString());
        Assert.Equal(640, sample.GetProperty("sourceWidth").GetInt32());
        Assert.Equal(480, sample.GetProperty("sourceHeight").GetInt32());
        Assert.Equal("esp32_capture", sample.GetProperty("captureSource").GetString());
        Assert.False(sample.GetProperty("previewFallback").GetBoolean());
        Assert.Equal("BOX_BLUE", sample.GetProperty("productClass").GetString());
        var fileName = sample.GetProperty("fileName").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fileName));
        var image = await client.GetAsync($"/api/dataset/sessions/{sessionId}/images/{fileName}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);

        var unknown = await client.PostAsJsonAsync(
            $"/api/dataset/sessions/{sessionId}/captures/camera",
            new
            {
                productClass = "BOX_PURPLE",
                qualityLabel = "GOOD",
                colorLabel = "RED",
                sizeLabel = "LARGE",
            }
        );
        await AssertErrorAsync(unknown, HttpStatusCode.BadRequest, ErrorCodes.DatasetInvalid);
        Assert.Equal(2, frames.Reads);

        var discarded = await AssertSuccessAsync(
            await client.DeleteAsync(
                $"/api/dataset/sessions/{sessionId}/samples/{sample.GetProperty("sampleId").GetGuid()}"
            )
        );
        Assert.Equal(0, discarded.GetProperty("imageCount").GetInt32());
        var missing = await client.GetAsync($"/api/dataset/sessions/{sessionId}/images/{fileName}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CameraCaptureWithoutVisionIsRejected()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateClient();
        var started = await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/dataset/sessions",
                new
                {
                    productClass = "BOX_RED",
                    qualityLabel = "GOOD",
                    colorLabel = "RED",
                    sizeLabel = "SMALL",
                    captureMode = "interval",
                    intervalMs = 1000,
                    simulation = false,
                }
            ),
            HttpStatusCode.Created
        );
        var response = await client.PostAsJsonAsync(
            $"/api/dataset/sessions/{started.GetProperty("sessionId").GetGuid()}/captures/camera",
            new
            {
                productClass = "BOX_RED",
                qualityLabel = "GOOD",
                colorLabel = "RED",
                sizeLabel = "SMALL",
            }
        );
        await AssertErrorAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            ErrorCodes.DatasetInvalid
        );
    }

    [Fact]
    public async Task StillFailureDoesNotStoreADatasetImage()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var frames = Fixture.GetRequiredService<ScriptedVisionFrames>();
        frames.FailStill = true;
        frames.Jpeg = TestImages.Jpeg();
        try
        {
            var client = Fixture.CreateClient();
            var started = await AssertSuccessAsync(
                await client.PostAsJsonAsync(
                    "/api/dataset/sessions",
                    new
                    {
                        productClass = "BOX_RED",
                        qualityLabel = "GOOD",
                        colorLabel = "RED",
                        sizeLabel = "LARGE",
                        captureMode = "manual",
                        simulation = false,
                    }
                ),
                HttpStatusCode.Created
            );
            var sessionId = started.GetProperty("sessionId").GetGuid();
            var response = await client.PostAsJsonAsync(
                $"/api/dataset/sessions/{sessionId}/captures/camera",
                new
                {
                    productClass = "BOX_RED",
                    qualityLabel = "GOOD",
                    colorLabel = "RED",
                    sizeLabel = "LARGE",
                }
            );
            var error = await AssertErrorAsync(
                response,
                HttpStatusCode.Conflict,
                ErrorCodes.DatasetInvalid
            );
            Assert.Contains(
                "High-quality still capture failed",
                error.GetProperty("message").GetString(),
                StringComparison.Ordinal
            );
            Assert.Equal(1, frames.Reads);
            var session = await AssertSuccessAsync(
                await client.GetAsync($"/api/dataset/sessions/{sessionId}")
            );
            Assert.Equal(0, session.GetProperty("imageCount").GetInt32());
        }
        finally
        {
            frames.FailStill = false;
        }
    }

    [Fact]
    public async Task UntouchedLegacyDemoBoxCameraProfileIsCorrected()
    {
        await Fixture.WithDbContextAsync(async db =>
        {
            var legacy = ContractCatalog.LegacyDemoBox();
            db.ProductRecipes.Add(
                new ProductRecipe
                {
                    Id = Guid.NewGuid(),
                    Name = ContractSeeder.DemoBoxName,
                    Description = legacy.Description,
                    IsActive = true,
                    Version = 1,
                    DefinitionJson = InspectionJson.Serialize(legacy),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            );
            await db.SaveChangesAsync();
            await ContractSeeder.EnsureAsync(db);
        });

        var recipes = await AssertSuccessAsync(
            await Fixture.CreateClient().GetAsync("/api/recipes")
        );
        var demo = recipes
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "DemoBox");
        Assert.Equal(
            240,
            demo.GetProperty("definition")
                .GetProperty("cameraProfile")
                .GetProperty("sourceWidth")
                .GetInt32()
        );
        Assert.Equal(
            176,
            demo.GetProperty("definition")
                .GetProperty("cameraProfile")
                .GetProperty("sourceHeight")
                .GetInt32()
        );
        Assert.Equal(2, demo.GetProperty("version").GetInt32());
        Assert.Equal(
            "BOX_RED",
            demo.GetProperty("definition")
                .GetProperty("productClasses")[0]
                .GetProperty("code")
                .GetString()
        );
    }

    [Fact]
    public async Task CustomizedDemoBoxKeepsItsCameraProfile()
    {
        await Fixture.WithDbContextAsync(async db =>
        {
            var legacy = ContractCatalog.LegacyDemoBox();
            legacy.ProductClasses[0].DisplayName = "Operator renamed red box";
            db.ProductRecipes.Add(
                new ProductRecipe
                {
                    Id = Guid.NewGuid(),
                    Name = ContractSeeder.DemoBoxName,
                    Description = legacy.Description,
                    IsActive = true,
                    Version = 4,
                    DefinitionJson = InspectionJson.Serialize(legacy),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            );
            await db.SaveChangesAsync();
            await ContractSeeder.EnsureAsync(db);
        });

        var recipes = await AssertSuccessAsync(
            await Fixture.CreateClient().GetAsync("/api/recipes")
        );
        var demo = recipes
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "DemoBox");
        Assert.Equal(
            320,
            demo.GetProperty("definition")
                .GetProperty("cameraProfile")
                .GetProperty("sourceWidth")
                .GetInt32()
        );
        Assert.Equal(4, demo.GetProperty("version").GetInt32());
        Assert.Equal(
            "Operator renamed red box",
            demo.GetProperty("definition")
                .GetProperty("productClasses")[0]
                .GetProperty("displayName")
                .GetString()
        );
    }

    [Fact]
    public async Task DiagnosticsDoesNotExposeSecrets()
    {
        var data = await AssertSuccessAsync(
            await Fixture.CreateClient().GetAsync("/api/diagnostics")
        );
        var json = data.GetRawText();
        Assert.Equal("not_loaded", data.GetProperty("modelStatus").GetString());
        Assert.Equal("NOT_CONFIGURED", data.GetProperty("vision").GetString());
        Assert.Equal("UNKNOWN", data.GetProperty("camera").GetString());
        Assert.False(data.GetProperty("model").GetProperty("loaded").GetBoolean());
        Assert.Equal("NOT_INTEGRATED", data.GetProperty("timeOfFlightSource").GetString());
        Assert.False(data.GetProperty("hardwareActuationEnabled").GetBoolean());
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signing", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", json, StringComparison.Ordinal);
    }
}

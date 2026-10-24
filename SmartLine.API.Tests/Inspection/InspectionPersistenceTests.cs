using SmartLine.API.Domain.Inspection;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Shared;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Inspection;

public sealed class InspectionPersistenceTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task ConcurrentActivationLeavesExactlyOneActiveRecipe()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateUserClient("Operator");
        var first = await AssertSuccessAsync(await client.PostAsJsonAsync("/api/recipes", new { name = "First", definition = ContractCatalog.DemoBox() }), HttpStatusCode.Created);
        var second = await AssertSuccessAsync(await client.PostAsJsonAsync("/api/recipes", new { name = "Second", definition = ContractCatalog.DemoBox() }), HttpStatusCode.Created);
        var ids = new[] { first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid() };
        var responses = await Task.WhenAll(ids.Select(id => client.PostAsync($"/api/recipes/{id}/activate", null)));
        foreach (var response in responses) await AssertSuccessAsync(response);
        Assert.Equal(1, await Fixture.WithDbContextAsync(db => db.ProductRecipes.CountAsync(r => r.IsActive)));
    }

    [Fact]
    public async Task FailedActivationRollsBackPreviousActiveRecipe()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateUserClient("Operator");
        var created = await AssertSuccessAsync(await client.PostAsJsonAsync("/api/recipes", new { name = "Fail activation", definition = ContractCatalog.DemoBox() }), HttpStatusCode.Created);
        await Fixture.WithDbContextAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_activation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"IsActive\" AND NEW.\"Name\" = 'Fail activation' THEN RAISE EXCEPTION 'injected failure'; END IF; RETURN NEW; END $$");
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_activation BEFORE UPDATE ON \"ProductRecipes\" FOR EACH ROW EXECUTE FUNCTION fail_activation()");
        });
        try
        {
            var response = await client.PostAsync($"/api/recipes/{created.GetProperty("id").GetGuid()}/activate", null);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("DemoBox", await Fixture.WithDbContextAsync(db => db.ProductRecipes.Where(r => r.IsActive).Select(r => r.Name).SingleAsync()));
        }
        finally
        {
            await Fixture.WithDbContextAsync(async db => {
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_activation ON \"ProductRecipes\"");
                await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_activation()");
            });
        }
    }

    [Fact]
    public async Task InspectionRetainsTheExplicitRecipeCalibrationIdentity()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var client = Fixture.CreateUserClient("Operator");
        var calibration = await AssertSuccessAsync(await client.PostAsJsonAsync("/api/calibration-profiles", new { name = "Explicit", definition = ContractCatalog.Uncalibrated() }), HttpStatusCode.Created);
        var calibrationId = calibration.GetProperty("id").GetGuid();
        var recipeId = await Fixture.WithDbContextAsync(db => db.ProductRecipes.Where(r => r.IsActive).Select(r => r.Id).SingleAsync());
        var definition = ContractCatalog.DemoBox();
        definition.CalibrationProfileId = calibrationId;
        await AssertSuccessAsync(await client.PutAsJsonAsync($"/api/recipes/{recipeId}", new { name = "DemoBox", definition }));
        var inspection = await AssertSuccessAsync(await client.PostAsJsonAsync("/api/inspections", new { confidence = .9, productClass = "BOX_GREEN", colorClass = "GREEN", sizeClass = "SMALL" }), HttpStatusCode.Created);
        Assert.Equal(calibrationId, inspection.GetProperty("inspection").GetProperty("calibrationProfileId").GetGuid());
        Assert.Equal(1, inspection.GetProperty("inspection").GetProperty("calibrationVersion").GetInt32());
    }

    [Fact]
    public async Task StaleRecipeVersionDoesNotCreateRecords()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var response = await Fixture.CreateUserClient("Operator").PostAsJsonAsync("/api/inspections", new { recipeVersion = 999, confidence = .9 });
        await AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.RecipeInvalid);
        Assert.Equal(0, await Fixture.WithDbContextAsync(db => db.InspectionRecords.CountAsync()));
    }
}

using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Inspection;
using SmartLine.API.Infrastructure.Persistence;

namespace SmartLine.API.Features.Inspection;

public static class ContractSeeder
{
    public const string DemoBoxName = "DemoBox";
    public const string UncalibratedName = "Uncalibrated baseline";

    public static async Task EnsureAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (!await db.ProductRecipes.AnyAsync(recipe => recipe.Name == DemoBoxName, ct))
        {
            var definition = ContractCatalog.DemoBox();
            var errors = RecipeValidator.Validate(definition);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(' ', errors));
            var hasActive = await db.ProductRecipes.AnyAsync(recipe => recipe.IsActive, ct);
            db.ProductRecipes.Add(
                new ProductRecipe
                {
                    Id = Guid.NewGuid(),
                    Name = DemoBoxName,
                    Description = definition.Description,
                    IsActive = !hasActive,
                    Version = 1,
                    DefinitionJson = InspectionJson.Serialize(definition),
                    CreatedAt = now,
                    UpdatedAt = now,
                }
            );
        }

        if (!await db.CalibrationProfiles.AnyAsync(profile => profile.Name == UncalibratedName, ct))
        {
            var definition = ContractCatalog.Uncalibrated();
            var errors = CalibrationValidator.Validate(definition);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(' ', errors));
            db.CalibrationProfiles.Add(
                new CalibrationProfile
                {
                    Id = Guid.NewGuid(),
                    Name = UncalibratedName,
                    Description = definition.Notes ?? "No physical calibration has been measured.",
                    IsActive = false,
                    Version = 1,
                    DefinitionJson = InspectionJson.Serialize(definition),
                    CreatedAt = now,
                    UpdatedAt = now,
                }
            );
        }

        var demos = await db
            .ProductRecipes.Where(recipe => recipe.Name == DemoBoxName)
            .ToListAsync(ct);
        foreach (var recipe in demos)
            LegacyRecipeCorrection.TryCorrect(recipe);

        await db.SaveChangesAsync(ct);
    }
}

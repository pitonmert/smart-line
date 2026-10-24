using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Inspection;

namespace SmartLine.API.Features.Inspection;

/// <summary>
/// Moves an untouched DemoBox seed from the old 320x240 profile to the HQVGA profile.
/// Any edited recipe, including one that still says 320x240, is left alone.
/// </summary>
public static class LegacyRecipeCorrection
{
    public static bool TryCorrect(ProductRecipe recipe)
    {
        if (!string.Equals(recipe.Name, ContractSeeder.DemoBoxName, StringComparison.Ordinal))
            return false;
        ProductRecipeDefinition stored;
        try
        {
            stored = InspectionJson.Deserialize<ProductRecipeDefinition>(recipe.DefinitionJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        var legacy = ContractCatalog.LegacyDemoBox();
        if (InspectionJson.Serialize(stored) != InspectionJson.Serialize(legacy))
            return false;

        var current = ContractCatalog.DemoBox();
        stored.CameraProfile.SourceWidth = current.CameraProfile.SourceWidth;
        stored.CameraProfile.SourceHeight = current.CameraProfile.SourceHeight;
        stored.CameraProfile.Notes = current.CameraProfile.Notes;
        recipe.DefinitionJson = InspectionJson.Serialize(stored);
        recipe.Version += 1;
        recipe.UpdatedAt = DateTime.UtcNow;
        return true;
    }
}

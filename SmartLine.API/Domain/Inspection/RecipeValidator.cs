using System.Text.RegularExpressions;

namespace SmartLine.API.Domain.Inspection;

public static class RecipeValidator
{
    // Kodun tamamını kontrol eder; sondaki görünmeyen Enter karakterini de reddeder.
    private static readonly Regex CodePattern = new(
        @"\A[A-Z][A-Z0-9_]{0,40}\z",
        RegexOptions.CultureInvariant
    );
    private static readonly HashSet<string> Qualities =
    [
        InspectionVocabulary.Good,
        InspectionVocabulary.Defective,
        InspectionVocabulary.ManualReview,
    ];
    private static readonly HashSet<string> Tasks =
    [
        InspectionVocabulary.TaskDetect,
    ];
    private static readonly HashSet<string> Roles =
    [
        InspectionVocabulary.Pass,
        InspectionVocabulary.Reject,
        InspectionVocabulary.Hold,
    ];
    private static readonly HashSet<string> Triggers =
    [
        InspectionVocabulary.TriggerManual,
        InspectionVocabulary.TriggerApi,
        InspectionVocabulary.TriggerSimulator,
        InspectionVocabulary.TriggerHardware,
    ];

    public static IReadOnlyList<string> Validate(ProductRecipeDefinition recipe)
    {
        var errors = new List<string>();
        // JSON içinde açıkça null gelen bölümler uygulamayı düşürmeden hata olarak bildirilsin.
        if (
            recipe.ProductClasses is null
            || recipe.AllowedColors is null
            || recipe.SizeClasses is null
            || recipe.DefectClasses is null
            || recipe.DestinationBins is null
            || recipe.SortingRules is null
            || recipe.Acceptance is null
            || recipe.PhysicalLimits is null
            || recipe.Model is null
            || recipe.Roi is null
            || recipe.CameraProfile is null
            || recipe.Inspection is null
            || recipe.Inspection.TriggerModes is null
        )
            return ["Recipe sections and collections must not be null."];
        // Listenin kendisi dolu olsa da içindeki bir kayıt veya kural koşulu null olabilir.
        if (
            recipe.ProductClasses.Any(item => item is null)
            || recipe.AllowedColors.Any(item => item is null)
            || recipe.SizeClasses.Any(item => item is null)
            || recipe.DefectClasses.Any(item => item is null)
            || recipe.DestinationBins.Any(item => item is null)
            || recipe.SortingRules.Any(item => item is null || item.When is null)
        )
            return ["Recipe collections must not contain null entries or rule conditions."];
        if (string.IsNullOrWhiteSpace(recipe.Name) || recipe.Name.Trim().Length > 80)
            errors.Add("Recipe name is required and must be at most 80 characters.");

        if (recipe.Description is null) errors.Add("Recipe description must not be null.");
        if (recipe.Model.ArtifactFileName is not null && string.IsNullOrWhiteSpace(recipe.Model.Version)) errors.Add("A model artifact requires its version.");
        if (recipe.Description?.Length > 1000 || recipe.Model.Name?.Length > 80 || recipe.Model.Version?.Length > 80)
            errors.Add("Recipe description or model metadata is too long.");

        RequireCodes(errors, "product class", recipe.ProductClasses.Select(item => item.Code));
        RequireCodes(errors, "color", recipe.AllowedColors.Select(item => item.Code));
        RequireCodes(errors, "size class", recipe.SizeClasses.Select(item => item.Code));
        RequireCodes(errors, "defect class", recipe.DefectClasses.Select(item => item.Code));
        RequireCodes(errors, "destination bin", recipe.DestinationBins.Select(item => item.Code));

        if (recipe.ProductClasses.Count == 0)
            errors.Add("A recipe needs at least one product class.");
        if (recipe.DestinationBins.Count == 0)
            errors.Add("A recipe needs at least one destination bin.");

        // Sayısal ayarlar sonlu olmalı; NaN gibi değerler yalnızca aralık kontrolüyle yakalanamaz.
        foreach (var color in recipe.AllowedColors)
        {
            if (
                !double.IsFinite(color.HueMin)
                || !double.IsFinite(color.HueMax)
                || color.HueMin is < 0 or > 360
                || color.HueMax is < 0 or > 360
            )
                errors.Add($"Color {color.Code} hue must stay between 0 and 360.");
            if (
                !double.IsFinite(color.SaturationMin)
                || !double.IsFinite(color.ValueMin)
                || color.SaturationMin is < 0 or > 1
                || color.ValueMin is < 0 or > 1
            )
                errors.Add(
                    $"Color {color.Code} saturation and value minimums must stay between 0 and 1."
                );
        }

        var sizes = recipe.SizeClasses.OrderBy(item => item.MinWidthMm).ToList();
        for (var index = 0; index < sizes.Count; index++)
        {
            var size = sizes[index];
            if (
                !double.IsFinite(size.MinWidthMm)
                || !double.IsFinite(size.MaxWidthMm)
                || size.MinWidthMm < 0
                || size.MaxWidthMm <= size.MinWidthMm
            )
                errors.Add($"Size class {size.Code} needs a positive width range.");
            if (index > 0 && size.MinWidthMm < sizes[index - 1].MaxWidthMm)
                errors.Add($"Size class {size.Code} overlaps {sizes[index - 1].Code}.");
        }

        var bins = recipe
            .DestinationBins.Select(item => item.Code)
            .ToHashSet(StringComparer.Ordinal);
        // Kuraldaki bir kodun biçimi kadar reçetede gerçekten tanımlı olması da gerekir.
        var products = recipe
            .ProductClasses.Select(item => item.Code)
            .ToHashSet(StringComparer.Ordinal);
        var colors = recipe
            .AllowedColors.Select(item => item.Code)
            .ToHashSet(StringComparer.Ordinal);
        var sizesByCode = recipe
            .SizeClasses.Select(item => item.Code)
            .ToHashSet(StringComparer.Ordinal);
        var defects = recipe
            .DefectClasses.Select(item => item.Code)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var bin in recipe.DestinationBins)
        {
            if (!Roles.Contains(bin.Role))
                errors.Add($"Destination {bin.Code} has an unknown role '{bin.Role}'.");
        }

        if (!bins.Contains(recipe.Acceptance.DefaultDestinationBin))
            errors.Add("The default destination bin is not defined.");
        // Emin olunamayan ürünleri bekletebilmek için her reçetede bir HOLD bölmesi bulunmalı.
        if (!recipe.DestinationBins.Any(item => item.Role == InspectionVocabulary.Hold))
            errors.Add("A recipe needs a HOLD destination for products requiring manual review.");
        if (
            !double.IsFinite(recipe.Acceptance.MinimumConfidence)
            || recipe.Acceptance.MinimumConfidence is < 0 or > 1
        )
            errors.Add("Minimum confidence must be between 0 and 1.");
        if (
            recipe.PhysicalLimits.MinConfidenceToSort is { } minimum
            && (!double.IsFinite(minimum) || minimum is < 0 or > 1)
        )
            errors.Add("Physical minimum confidence must be between 0 and 1.");

        var priorities = new HashSet<int>();
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in recipe.SortingRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || !ruleIds.Add(rule.Id))
                errors.Add("Every sorting rule needs a unique id.");
            if (!priorities.Add(rule.Priority))
                errors.Add($"Sorting rule priority {rule.Priority} is duplicated.");
            if (!bins.Contains(rule.DestinationBin))
                errors.Add($"Sorting rule {rule.Id} points at an unknown bin.");
            ValidateOptionalCode(errors, rule.When.Quality, "quality", Qualities);
            ValidateOptionalCode(
                errors,
                rule.When.ProductClass,
                "product class",
                products,
                allowUnknown: true
            );
            ValidateOptionalCode(errors, rule.When.ColorClass, "color", colors, allowUnknown: true);
            ValidateOptionalCode(
                errors,
                rule.When.SizeClass,
                "size",
                sizesByCode,
                allowUnknown: true
            );
            ValidateOptionalCode(errors, rule.When.DefectClass, "defect", defects);
        }

        if (!Tasks.Contains(recipe.Model.Task))
            errors.Add("Only detection models are supported in v0.2.0.");
        if (recipe.Model.InputSize is < 32 or > 4096)
            errors.Add("Model input size is outside 32..4096.");
        if (string.IsNullOrWhiteSpace(recipe.Model.Name))
            errors.Add("Model name is required even when weights are not loaded.");

        var roi = recipe.Roi;
        if (
            !double.IsFinite(roi.X)
            || !double.IsFinite(roi.Y)
            || !double.IsFinite(roi.Width)
            || !double.IsFinite(roi.Height)
            || roi.X < 0
            || roi.Y < 0
            || roi.Width <= 0
            || roi.Height <= 0
            || roi.X + roi.Width > 1
            || roi.Y + roi.Height > 1
        )
            errors.Add("ROI must be a normalized rectangle inside the frame.");

        if (recipe.CameraProfile.SourceWidth is <= 0 || recipe.CameraProfile.SourceHeight is <= 0)
            errors.Add("Camera source dimensions must be positive when they are set.");
        if (
            recipe.PhysicalLimits.MaxWidthMm is { } maxWidth
                && (!double.IsFinite(maxWidth) || maxWidth <= 0)
            || recipe.PhysicalLimits.MaxHeightMm is { } maxHeight
                && (!double.IsFinite(maxHeight) || maxHeight <= 0)
        )
            errors.Add("Physical limits must be positive when they are set.");

        foreach (var mode in recipe.Inspection.TriggerModes)
        {
            if (!Triggers.Contains(mode))
                errors.Add($"Trigger mode '{mode}' is not supported.");
        }

        if (recipe.Inspection.ContinuousInference)
            errors.Add(
                "Continuous inference is not part of the event-triggered inspection pipeline."
            );

        // Üründen beklenen renk, reçetenin tanıdığı renklerden biri olmalı.
        foreach (var product in recipe.ProductClasses)
        {
            if (product.ExpectedColor is not null && !CodePattern.IsMatch(product.ExpectedColor))
                errors.Add($"Expected color for {product.Code} is not a valid code.");
            else if (product.ExpectedColor is not null && !colors.Contains(product.ExpectedColor))
                errors.Add($"Expected color for {product.Code} is not defined in the recipe.");
        }

        return errors;
    }

    private static void RequireCodes(List<string> errors, string label, IEnumerable<string> codes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            if (string.IsNullOrWhiteSpace(code) || !CodePattern.IsMatch(code))
                errors.Add(
                    $"{label} code '{code}' must be uppercase letters, digits, or underscores."
                );
            else if (!seen.Add(code))
                errors.Add($"Duplicate {label} code '{code}'.");
        }
    }

    private static void ValidateOptionalCode(
        List<string> errors,
        string? code,
        string label,
        HashSet<string> definedCodes,
        bool allowUnknown = false
    )
    {
        // Boş koşul ve ANY özel anlam taşır; tanımlı bir kod gibi aranmaz.
        // UNKNOWN ise ürün, renk veya boyutun tanınamadığı durumu hedeflemek için kullanılabilir.
        if (string.IsNullOrWhiteSpace(code) || code == "ANY")
            return;
        if (!CodePattern.IsMatch(code))
            errors.Add($"Sorting rule {label} '{code}' is not a valid code.");
        else if (
            !definedCodes.Contains(code) && !(allowUnknown && code == InspectionVocabulary.Unknown)
        )
            errors.Add($"Sorting rule {label} '{code}' is not defined in the recipe.");
    }
}

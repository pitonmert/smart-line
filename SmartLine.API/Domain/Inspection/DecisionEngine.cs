namespace SmartLine.API.Domain.Inspection;

/// <summary>
/// Deterministic recipe rules. The vision model does not choose a bin.
/// A measured visual width replaces a claimed size class.
/// </summary>
public static class DecisionEngine
{
    public static DecisionResult Decide(
        ProductRecipeDefinition recipe,
        InspectionObservation observation
    )
    {
        if (observation.Confidence is < 0 or > 1 || double.IsNaN(observation.Confidence))
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                "Confidence must be between 0 and 1."
            );

        // Negatif, sonsuz veya sayı olmayan ölçümlerle ürün kararı üretmeyelim.
        if (
            observation.EstimatedWidthMm is { } widthMm
                && (!double.IsFinite(widthMm) || widthMm < 0)
            || observation.EstimatedHeightMm is { } heightMm
                && (!double.IsFinite(heightMm) || heightMm < 0)
        )
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                "Physical dimensions must be finite and non-negative."
            );

        var reasons = new List<string>();
        var product = ResolveProductClass(recipe, observation.ProductClass, reasons);
        var color = ResolveColor(recipe, observation.ColorClass, reasons);
        var size = ResolveSize(recipe, observation, reasons);
        var defects = ResolveDefects(recipe, observation.DefectClasses, reasons);

        if (
            product.Known
            && color.Known
            && product.ExpectedColor is not null
            && product.ExpectedColor != color.Code
        )
        {
            reasons.Add("COLOR_MISMATCH");
            AddKnownDefect(recipe, defects, "WRONG_COLOR");
        }

        if (observation.EstimatedWidthMm is not null && size.Code == InspectionVocabulary.Unknown)
        {
            reasons.Add("SIZE_OUT_OF_RANGE");
            AddKnownDefect(recipe, defects, "WRONG_SIZE");
        }

        // Kusur listesi gönderilmemişse boş liste kabul edilir.
        var unknownDefects = (observation.DefectClasses ?? [])
            .Select(Normalize)
            .Where(code => code.Length > 0 && recipe.DefectClasses.All(item => item.Code != code))
            .Distinct()
            .ToList();
        foreach (var unknown in unknownDefects)
            reasons.Add($"UNKNOWN_DEFECT:{unknown}");

        var rejectingDefect = defects.Any(code =>
            recipe.DefectClasses.First(item => item.Code == code).Rejects
        );
        var rejectUnknownDefect =
            unknownDefects.Count > 0 && recipe.Acceptance.RejectUnknownDefects;
        var rejectUnknownClass = !product.Known && recipe.Acceptance.RejectUnknownClass;
        if (rejectUnknownClass)
            reasons.Add("UNKNOWN_CLASS");

        // Boyut sınıfına uyması yeterli değildir; ürün fiziksel üst sınırları da aşmamalı.
        var widthExceeded =
            observation.EstimatedWidthMm is { } width
            && recipe.PhysicalLimits.MaxWidthMm is { } maxWidth
            && width > maxWidth;
        var heightExceeded =
            observation.EstimatedHeightMm is { } height
            && recipe.PhysicalLimits.MaxHeightMm is { } maxHeight
            && height > maxHeight;
        if (widthExceeded)
            reasons.Add("PHYSICAL_WIDTH_EXCEEDED");
        if (heightExceeded)
            reasons.Add("PHYSICAL_HEIGHT_EXCEEDED");

        // İki güven eşiğinden daha yüksek olanı kullanarak mevcut kabul şartını koruruz.
        var minimumConfidence = Math.Max(
            recipe.Acceptance.MinimumConfidence,
            recipe.PhysicalLimits.MinConfidenceToSort ?? 0
        );
        var lowConfidence = observation.Confidence < minimumConfidence;
        var rejecting =
            rejectingDefect
            || rejectUnknownDefect
            || rejectUnknownClass
            || widthExceeded
            || heightExceeded;
        string quality;
        if (rejecting && (recipe.Acceptance.DefectsOverrideLowConfidence || !lowConfidence))
            quality = InspectionVocabulary.Defective;
        else if (lowConfidence)
        {
            quality = InspectionVocabulary.ManualReview;
            reasons.Add("LOW_CONFIDENCE");
        }
        else if (!product.Known)
            quality = InspectionVocabulary.ManualReview;
        else if (recipe.Acceptance.RequireKnownColor && !color.Known)
        {
            quality = InspectionVocabulary.ManualReview;
            reasons.Add("COLOR_UNKNOWN");
        }
        else
            quality = InspectionVocabulary.Good;

        if (rejecting && lowConfidence && !recipe.Acceptance.DefectsOverrideLowConfidence)
        {
            quality = InspectionVocabulary.ManualReview;
            reasons.Add("LOW_CONFIDENCE");
        }

        var orderedDefects = recipe
            .DefectClasses.Select(item => item.Code)
            .Where(defects.Contains)
            .ToList();

        // Kuralın koşulları tutsa bile hedef bölme ürünün değerlendirmesine uygun olmalı.
        var rule = recipe
            .SortingRules.OrderBy(item => item.Priority)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault(item =>
                Matches(item.When, quality, product.Code, color.Code, size.Code, orderedDefects)
                && recipe.DestinationBins.Any(bin =>
                    bin.Code == item.DestinationBin && AllowsDestination(quality, bin.Role)
                )
            );

        var binCode = rule?.DestinationBin ?? recipe.Acceptance.DefaultDestinationBin;
        var bin = recipe.DestinationBins.First(item => item.Code == binCode);
        if (!AllowsDestination(quality, bin.Role))
        {
            // Varsayılan bölme uygun değilse ürünü insan kontrolü için bekletiriz.
            // Birden fazla bekleme bölmesi varsa kod sırasına göre sabit bir seçim yaparız.
            bin =
                recipe
                    .DestinationBins.Where(item => item.Role == InspectionVocabulary.Hold)
                    .OrderBy(item => item.Code, StringComparer.Ordinal)
                    .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No HOLD destination is defined for manual review."
                );
            reasons.Add("DESTINATION_GUARD");
        }
        if (rule is null)
            reasons.Add("DEFAULT_DESTINATION");
        else
            reasons.Add($"RULE:{rule.Id}");

        return new DecisionResult
        {
            ProductClass = product.Code,
            ColorClass = color.Code,
            SizeClass = size.Code,
            DefectClasses = orderedDefects,
            QualityStatus = quality,
            Decision = InspectionVocabulary.DecisionForBinRole(bin.Role),
            DestinationBin = bin.Code,
            RuleId = rule?.Id,
            Reasons = reasons,
            Simulation = observation.Simulation || IsSimulatorTrigger(observation.TriggerSource),
        };
    }

    public static string? MatchSizeClass(IReadOnlyList<SizeClassDefinition> classes, double widthMm)
    {
        var ordered = classes.OrderBy(item => item.MinWidthMm).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var size = ordered[index];
            var last = index == ordered.Count - 1;
            var upperMatch = last ? widthMm <= size.MaxWidthMm : widthMm < size.MaxWidthMm;
            if (widthMm >= size.MinWidthMm && upperMatch)
                return size.Code;
        }

        return null;
    }

    private static bool IsSimulatorTrigger(string? trigger) =>
        string.Equals(
            trigger,
            InspectionVocabulary.TriggerSimulator,
            StringComparison.OrdinalIgnoreCase
        );

    // Sağlam ürün bölmesine yalnızca GOOD ürünler gidebilir.
    // İnsan kontrolü gereken ürünler ise yalnızca HOLD bölmesine gönderilebilir.
    private static bool AllowsDestination(string quality, string role) =>
        role == InspectionVocabulary.Hold
        || role == InspectionVocabulary.Reject && quality != InspectionVocabulary.ManualReview
        || role == InspectionVocabulary.Pass && quality == InspectionVocabulary.Good;

    private static ResolvedCode ResolveProductClass(
        ProductRecipeDefinition recipe,
        string? raw,
        List<string> reasons
    )
    {
        var code = Normalize(raw);
        var match = recipe.ProductClasses.FirstOrDefault(item => item.Code == code);
        if (match is null)
            return new ResolvedCode(InspectionVocabulary.Unknown, false, null);
        reasons.Add($"CLASS:{match.Code}");
        var expected = Normalize(match.ExpectedColor);
        return new ResolvedCode(match.Code, true, expected.Length == 0 ? null : expected);
    }

    private static ResolvedCode ResolveColor(
        ProductRecipeDefinition recipe,
        string? raw,
        List<string> reasons
    )
    {
        var code = Normalize(raw);
        if (recipe.AllowedColors.Any(item => item.Code == code))
            return new ResolvedCode(code, true, null);
        if (code.Length > 0)
            reasons.Add("COLOR_NOT_IN_RECIPE");
        return new ResolvedCode(InspectionVocabulary.Unknown, false, null);
    }

    private static ResolvedCode ResolveSize(
        ProductRecipeDefinition recipe,
        InspectionObservation observation,
        List<string> reasons
    )
    {
        if (observation.EstimatedWidthMm is { } width)
        {
            var measured = MatchSizeClass(recipe.SizeClasses, width);
            reasons.Add("SIZE_FROM_VISUAL_WIDTH");
            return measured is null
                ? new ResolvedCode(InspectionVocabulary.Unknown, false, null)
                : new ResolvedCode(measured, true, null);
        }

        var claimed = Normalize(observation.SizeClass);
        if (recipe.SizeClasses.Any(item => item.Code == claimed))
            return new ResolvedCode(claimed, true, null);
        return new ResolvedCode(InspectionVocabulary.Unknown, false, null);
    }

    private static HashSet<string> ResolveDefects(
        ProductRecipeDefinition recipe,
        IEnumerable<string>? raw,
        List<string> reasons
    )
    {
        var known = recipe
            .DefectClasses.Select(item => item.Code)
            .ToHashSet(StringComparer.Ordinal);
        var defects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in (raw ?? []).Select(Normalize).Where(code => code.Length > 0))
        {
            if (known.Contains(code))
                defects.Add(code);
        }

        if (defects.Count > 0)
            reasons.Add("DEFECTS_PRESENT");
        return defects;
    }

    private static void AddKnownDefect(
        ProductRecipeDefinition recipe,
        HashSet<string> defects,
        string code
    )
    {
        if (recipe.DefectClasses.Any(item => item.Code == code))
            defects.Add(code);
    }

    private static bool Matches(
        SortingRuleWhen when,
        string quality,
        string product,
        string color,
        string size,
        IReadOnlyList<string> defects
    )
    {
        if (!Field(when.Quality, quality))
            return false;
        if (!Field(when.ProductClass, product))
            return false;
        if (!Field(when.ColorClass, color))
            return false;
        if (!Field(when.SizeClass, size))
            return false;
        if (string.IsNullOrWhiteSpace(when.DefectClass))
            return true;
        if (when.DefectClass == "ANY")
            return defects.Count > 0;
        return defects.Contains(when.DefectClass);
    }

    private static bool Field(string? expected, string actual) =>
        string.IsNullOrWhiteSpace(expected)
        || expected == "ANY"
        || string.Equals(expected, actual, StringComparison.Ordinal);

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

    private sealed record ResolvedCode(string Code, bool Known, string? ExpectedColor);
}

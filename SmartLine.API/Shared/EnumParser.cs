namespace SmartLine.API.Shared;

public static class EnumParser
{
    public static bool TryParseDefined<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value)
            && !value.Contains(',')
            && !long.TryParse(value, out _)
            && Enum.TryParse(value, true, out parsed)
            && Enum.IsDefined(parsed);
    }
}

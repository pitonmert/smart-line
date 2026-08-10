namespace SmartLine.API.Shared;

public static class Pagination
{
    public static (int PageNumber, int PageSize) Normalize(
        int pageNumber,
        int pageSize,
        int maxSize = 100
    ) => (Math.Clamp(pageNumber, 1, 100_000), Math.Clamp(pageSize, 1, maxSize));
}

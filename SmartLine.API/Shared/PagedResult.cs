namespace SmartLine.API.Shared;

public sealed record PagedResult<T>(List<T> Items, bool HasNextPage, int PageNumber, int PageSize)
{
    public bool HasPreviousPage => PageNumber > 1;
}

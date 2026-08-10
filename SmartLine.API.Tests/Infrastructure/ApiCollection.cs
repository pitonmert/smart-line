namespace SmartLine.API.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "SmartLine API";
}

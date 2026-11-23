using Testcontainers.MongoDb;

namespace SettlementRecon.Tests.Integration;

/// <summary>
/// One MongoDB container for the whole integration namespace, started on demand by Testcontainers.
/// Needs Docker, so everything that uses it carries <c>[Category("Integration")]</c>; CI runs it, a laptop
/// without Docker runs <c>dotnet test --filter "Category!=Integration"</c>.
/// </summary>
[SetUpFixture]
public sealed class MongoFixture
{
    private static MongoDbContainer? _container;

    public static string ConnectionString =>
        _container?.GetConnectionString() ?? throw new InvalidOperationException("MongoDB container is not running.");

    /// <summary>Starts the container the first time an integration test asks for it.</summary>
    public static async Task EnsureStartedAsync()
    {
        if (_container is not null)
        {
            return;
        }

        var container = new MongoDbBuilder("mongo:8.0").Build();
        await container.StartAsync();
        _container = container;
    }

    [OneTimeTearDown]
    public async Task StopContainer()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }
}

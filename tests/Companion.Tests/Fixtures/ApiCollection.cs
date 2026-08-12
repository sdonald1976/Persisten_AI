using Xunit;

namespace Companion.Tests.Fixtures;

/// <summary>
/// One shared app instance for every API-level test class. The factory configures the app via
/// PROCESS-WIDE environment variables, so two factories booting in parallel stomp each other's
/// settings — a shared collection fixture serializes the classes and boots the app once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CompanionApiFactory>
{
    public const string Name = "api";
}

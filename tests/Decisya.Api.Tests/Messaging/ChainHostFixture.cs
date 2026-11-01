namespace Decisya.Api.Tests.Messaging;

/// <summary>
/// One Production-environment host with the probe handlers compiled into real chains (TypeLoadMode
/// Dynamic is its only Wolverine override), shared by the classes of the "ChainHost" collection. Compiling
/// handlers takes seconds, so the classes share it and tell their messages apart by label.
/// </summary>
public sealed class ChainHostFixture : IAsyncLifetime
{
    internal MessagingHost? Host { get; private set; }

    public async ValueTask InitializeAsync() => Host = await MessagingHost.StartAsync("Production");

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ChainHostDefinition : ICollectionFixture<ChainHostFixture>
{
    public const string Name = "ChainHost";
}

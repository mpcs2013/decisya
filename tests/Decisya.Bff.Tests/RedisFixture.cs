using Decisya.AppHost;
using Testcontainers.Redis;

namespace Decisya.Bff.Tests;

/// <summary>
/// One throwaway Redis container per test run (testcontainers skill: lazy start, no data
/// volume, random container name). The image is pinned via <see cref="ContainerImages"/>
/// (linked as source, the same way <c>Decisya.Identity.Tests</c> links it), so this fixture
/// and the AppHost resolve to the exact same image at compile time.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime, IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private RedisContainer? _container;
    private bool _started;

    public string ConnectionString => (_container ?? throw NotStarted()).GetConnectionString();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        _startLock.Dispose();
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            var image = ContainerImages.Reference(
                ContainerImages.RedisRegistry, ContainerImages.RedisImage, ContainerImages.RedisTag, ContainerImages.RedisSha256);
            _container = new RedisBuilder(image).Build();
            await _container.StartAsync(cancellationToken);
            _started = true;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Story 3's outage scenario: stops the container without disposing the fixture.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_container is not null)
        {
            await _container.StopAsync(cancellationToken);
        }
    }

    private static InvalidOperationException NotStarted() =>
        new("RedisFixture has not started yet. Call EnsureStartedAsync first.");
}

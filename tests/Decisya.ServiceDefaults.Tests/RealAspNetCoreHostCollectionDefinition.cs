namespace Decisya.ServiceDefaults.Tests;

/// <summary>
/// Groups every test class that binds a real Kestrel socket (<c>WebApplication</c> or a
/// generic host built with <c>AddServiceDefaults</c>) so they never run concurrently with
/// each other. xUnit parallelizes across collections by default; two of these hosts
/// starting at the same instant reportedly raced once under the CPU and ephemeral-port
/// pressure of a solution-wide run. See <see cref="MapDefaultEndpointsTests"/> for the
/// incident this guards against.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class RealAspNetCoreHostCollectionDefinition
{
    public const string Name = "Real ASP.NET Core host";
}

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>A minimal <see cref="IHostEnvironment"/> whose name a test controls directly.</summary>
internal sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "Decisya.ServiceDefaults.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

using System.Text;
using Decisya.AppHost;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, G3 G4-121-03 (a) and (b): the real <c>deploy/keycloak/entrypoint-stack.sh</c>, run in
/// the pinned Keycloak image with <c>kc.sh</c> replaced by a stub that only reports what it was
/// started with. Every refusal is exercised: each forbidden character in the host, the wrong port,
/// pre-set derived values, a bad secret file, and the arguments the wrapper never accepts. No test
/// asserts that a value was echoed; each refusal asserts that the value is NOT in the output.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductionWrapperTests : IClassFixture<ProductionWrapperTests.SharedContainer>
{
    private const string SecretPath = "/run/secrets/Bff__Oidc__ClientSecret";
    private const string DbPasswordPath = "/tmp/wrapper-test-db-password";

    private static readonly string[] BaseEnvironment =
    [
        "PATH=/usr/bin:/bin",
        "KC_DB_PASSWORD_FILE=" + DbPasswordPath,
        "DECISYA_APP_HOST=app.decisya.example",
        "DECISYA_HTTPS_PORT=8443",
    ];

    private static readonly string GoodSecret = new('a', 32);

    private readonly SharedContainer _shared;

    public ProductionWrapperTests(SharedContainer shared) => _shared = shared;

    /// <summary>One idle Keycloak-image container for the whole class (xunit builds a new test-class instance per
    /// case, and starting the image per case would take far longer than the cases themselves). Each case only
    /// execs the wrapper under <c>env -i</c>, so cases share no state besides the secret file, which every run rewrites.</summary>
    public sealed class SharedContainer : IAsyncLifetime
    {
        internal SemaphoreSlim Lock { get; } = new(1, 1);

        internal IContainer? Container { get; set; }

        public ValueTask InitializeAsync() => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (Container is not null)
            {
                await Container.DisposeAsync();
            }

            Lock.Dispose();
        }
    }

    [Fact]
    public async Task Valid_inputs_derive_the_origin_pass_the_secret_to_the_process_and_start_with_import_realm()
    {
        var run = await RunAsync(GoodSecret + "\n", [], ["--optimized"]);

        run.ExitCode.Should().Be(0, run.Output);
        run.Output.Should().Contain("STUB args=start --import-realm --optimized");
        run.Output.Should().Contain("STUB origin=https://app.decisya.example:8443");
        run.Output.Should().Contain("STUB secret_length=32");
        run.Output.Should().NotContain(GoodSecret, "the wrapper and the stub print the length only");
    }

    [Theory]
    [InlineData("DECISYA_BFF_CLIENT_SECRET=preset-value-from-compose")]
    [InlineData("DECISYA_BFF_CLIENT_SECRET=")]
    [InlineData("DECISYA_REALM_APP_ORIGIN=https://evil.example")]
    [InlineData("DECISYA_REALM_APP_ORIGIN=")]
    public async Task A_preset_derived_value_is_refused_by_name_only(string preset)
    {
        var run = await RunAsync(GoodSecret, [preset], []);

        run.ExitCode.Should().NotBe(0);
        run.Output.Should().Contain(preset.Split('=')[0]);
        var value = preset.Split('=', 2)[1];
        if (value.Length > 0)
        {
            run.Output.Should().NotContain(value);
        }
    }

    [Theory]
    [InlineData("--import-realm")]
    [InlineData("start-dev")]
    [InlineData("import")]
    [InlineData("export")]
    [InlineData("build")]
    public async Task A_caller_argument_for_the_import_or_the_development_server_is_refused(string argument)
    {
        var run = await RunAsync(GoodSecret, [], [argument]);

        run.ExitCode.Should().NotBe(0);
        run.Output.Should().NotContain("STUB args");
    }

    [Fact]
    public async Task A_missing_host_or_port_is_refused_by_name()
    {
        var withoutHost = await RunAsync(GoodSecret, ["DECISYA_APP_HOST="], [], dropDefault: "DECISYA_APP_HOST");
        withoutHost.ExitCode.Should().NotBe(0);
        withoutHost.Output.Should().Contain("DECISYA_APP_HOST");

        var withoutPort = await RunAsync(GoodSecret, [], [], dropDefault: "DECISYA_HTTPS_PORT");
        withoutPort.ExitCode.Should().NotBe(0);
        withoutPort.Output.Should().Contain("DECISYA_HTTPS_PORT");
    }

    [Theory]
    [InlineData("8444")]
    [InlineData("08443")]
    [InlineData("8443 ")]
    [InlineData(" 8443")]
    [InlineData("443")]
    [InlineData("8443,")]
    [InlineData("*")]
    public async Task Only_port_8443_is_accepted(string port)
    {
        var run = await RunAsync(GoodSecret, ["DECISYA_HTTPS_PORT=" + port], [], dropDefault: "DECISYA_HTTPS_PORT");

        run.ExitCode.Should().NotBe(0, run.Output);
        run.Output.Should().Contain("DECISYA_HTTPS_PORT");
        run.Output.Should().NotContain("STUB args");
    }

    /// <summary>
    /// The host check is a security control: the value is substituted into the realm JSON text, so
    /// each of these would let a value break out of its string or add a redirect URI.
    /// </summary>
    [Theory]
    [InlineData("\"")]
    [InlineData("\\")]
    [InlineData(",")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData(":")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData(";")]
    [InlineData("$")]
    [InlineData("`")]
    [InlineData("'")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("@")]
    [InlineData("#")]
    [InlineData("%")]
    [InlineData("&")]
    [InlineData("=")]
    [InlineData("+")]
    [InlineData("!")]
    [InlineData("?")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("|")]
    [InlineData("^")]
    [InlineData("~")]
    [InlineData("_")]
    [InlineData("A")]
    [InlineData("é")]
    [InlineData("\u0000x")]
    public async Task A_host_with_a_forbidden_character_is_refused_and_never_echoed(string forbidden)
    {
        var host = "app" + forbidden + "x.decisya.example";
        if (forbidden.Contains('\u0000', StringComparison.Ordinal))
        {
            // A NUL can not travel in an environment value; the neighbouring control character stands in.
            host = "app\u0001x.decisya.example";
        }

        var run = await RunAsync(GoodSecret, ["DECISYA_APP_HOST=" + host], [], dropDefault: "DECISYA_APP_HOST");

        run.ExitCode.Should().NotBe(0, $"a host containing U+{(int)forbidden[0]:X4} must be refused");
        run.Output.Should().Contain("DECISYA_APP_HOST");
        run.Output.Should().NotContain("STUB args");
        run.Output.Should().NotContain("decisya.example", "the wrapper names the variable and never the value");
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("decisya")]
    [InlineData(".decisya.example")]
    [InlineData("decisya.example.")]
    [InlineData("app..decisya.example")]
    [InlineData("-app.decisya.example")]
    [InlineData("app-.decisya.example")]
    [InlineData("app.-decisya.example")]
    [InlineData("app.decisya-.example")]
    public async Task A_malformed_host_shape_is_refused(string host)
    {
        var run = await RunAsync(GoodSecret, ["DECISYA_APP_HOST=" + host], [], dropDefault: "DECISYA_APP_HOST");

        run.ExitCode.Should().NotBe(0, run.Output);
        run.Output.Should().NotContain("STUB args");
    }

    [Fact]
    public async Task A_label_over_63_characters_and_a_name_over_253_are_refused_but_the_limits_pass()
    {
        var label63 = new string('a', 63);
        var label64 = new string('a', 64);

        (await RunAsync(GoodSecret, ["DECISYA_APP_HOST=" + label64 + ".example"], [], dropDefault: "DECISYA_APP_HOST")).ExitCode.Should().NotBe(0);
        (await RunAsync(GoodSecret, ["DECISYA_APP_HOST=" + label63 + ".example"], [], dropDefault: "DECISYA_APP_HOST")).ExitCode.Should().Be(0);

        var longHost = string.Join('.', Enumerable.Repeat(label63, 4)) + ".ab"; // 63*4 + 4 dots... = 259 > 253
        longHost.Length.Should().BeGreaterThan(253);
        (await RunAsync(GoodSecret, ["DECISYA_APP_HOST=" + longHost], [], dropDefault: "DECISYA_APP_HOST")).ExitCode.Should().NotBe(0);

        // 253 characters exactly: 3 labels of 63, one of 61, three dots.
        var maxHost = string.Join('.', label63, label63, label63, new string('b', 61));
        maxHost.Length.Should().Be(253);
        (await RunAsync(GoodSecret, ["DECISYA_APP_HOST=" + maxHost], [], dropDefault: "DECISYA_APP_HOST")).ExitCode.Should().Be(0);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 31
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa,b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa$b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa*")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n\n")]
    public async Task A_bad_secret_file_is_refused_and_the_secret_is_never_echoed(string secretFile)
    {
        var run = await RunAsync(secretFile, [], []);

        run.ExitCode.Should().NotBe(0, run.Output);
        run.Output.Should().Contain("Bff__Oidc__ClientSecret");
        run.Output.Should().NotContain("STUB args");
        if (secretFile.Trim().Length > 4)
        {
            run.Output.Should().NotContain(secretFile.Trim());
        }
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 32, no newline
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData("AbCdEf0123456789AbCdEf0123456789AbCdEf0123456789\n")]
    public async Task A_good_secret_file_passes(string secretFile)
    {
        var run = await RunAsync(secretFile, [], []);

        run.ExitCode.Should().Be(0, run.Output);
        run.Output.Should().Contain("STUB args=start --import-realm");
    }

    [Fact]
    public async Task A_missing_secret_file_is_refused()
    {
        var run = await RunAsync(secretFile: null, [], []);

        run.ExitCode.Should().NotBe(0);
        run.Output.Should().Contain("Bff__Oidc__ClientSecret");
    }

    // ------------------------------------------------------------------ harness

    private sealed record Run(long ExitCode, string Output);

    private async Task<Run> RunAsync(string? secretFile, string[] extraEnvironment, string[] arguments, string? dropDefault = null)
    {
        var container = await EnsureContainerAsync();
        var ct = TestContext.Current.CancellationToken;

        await _shared.Lock.WaitAsync(ct);
        try
        {
            if (secretFile is null)
            {
                await container.ExecAsync(["rm", "-f", SecretPath], ct);
            }
            else
            {
                await container.CopyAsync(Encoding.UTF8.GetBytes(secretFile), SecretPath, ct: ct);
            }

            var command = new List<string> { "env", "-i" };
            command.AddRange(BaseEnvironment.Where(entry => dropDefault is null || !entry.StartsWith(dropDefault + "=", StringComparison.Ordinal)));
            command.AddRange(extraEnvironment);
            command.AddRange(["/bin/sh", "/opt/decisya/entrypoint-stack.sh"]);
            command.AddRange(arguments);

            var result = await container.ExecAsync(command, ct);
            return new Run(result.ExitCode ?? -1, result.Stdout + result.Stderr);
        }
        finally
        {
            _shared.Lock.Release();
        }
    }

    private async Task<IContainer> EnsureContainerAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _shared.Lock.WaitAsync(ct);
        try
        {
            if (_shared.Container is not null)
            {
                return _shared.Container;
            }

            var image = ContainerImages.Reference(
                ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage,
                ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256);

            // The stub reports only lengths and the derived origin, never the secret itself.
            const string stub = "#!/bin/sh\n"
                + "echo \"STUB args=$*\"\n"
                + "echo \"STUB origin=$DECISYA_REALM_APP_ORIGIN\"\n"
                + "echo \"STUB secret_length=${#DECISYA_BFF_CLIENT_SECRET}\"\n";

            var container = new ContainerBuilder(image)
                .WithEntrypoint("/bin/sh", "-c", "sleep 3600")
                .WithCreateParameterModifier(parameters => parameters.User = "0")
                .WithResourceMapping(ProductionKeycloak.ReadRepoFile("deploy/keycloak/entrypoint-stack.sh"), "/opt/decisya/entrypoint-stack.sh")
                .WithResourceMapping(Encoding.ASCII.GetBytes(stub), "/opt/keycloak/bin/kc.sh")
                .WithResourceMapping(Encoding.ASCII.GetBytes("a-database-password"), DbPasswordPath)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("true"))
                .Build();
            _shared.Container = container;
            await container.StartAsync(ct);

            // The tar copy does not carry the execute bit through reliably; set it explicitly.
            var chmod = await container.ExecAsync(["chmod", "755", "/opt/keycloak/bin/kc.sh"], ct);
            chmod.ExitCode.Should().Be(0, chmod.Stderr);
            return container;
        }
        finally
        {
            _shared.Lock.Release();
        }
    }
}

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Decisya.AppHost;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Decisya.Identity.Tests;

/// <summary>
/// The production identity stack in miniature (issue #121, G2 D1/D5/D6): a throwaway Postgres and
/// the pinned Keycloak started the way the deployable stack starts it, through the real
/// <c>deploy/keycloak/entrypoint-stack.sh</c> wrapper (<c>kc.sh start --import-realm</c>), with the
/// production realm file, the password list and the secret files mounted at the stack's targets.
/// Nothing here reads a developer secret store: every secret is generated per run.
/// </summary>
/// <remarks>
/// This file is linked as source into <c>tests/Decisya.Api.Tests</c> (as the other Identity.Tests
/// helpers are), so it depends only on <c>ContainerImages</c>, <c>RepoPaths</c>,
/// <c>OidcTestHelpers</c>, <c>SecureCookieRelayHandler</c> and <c>JwtHelper</c>. Start-up is lazy
/// (<see cref="EnsureStartedAsync"/>), like the other Integration fixtures, so the unit lane never
/// touches Docker. Users are created at test time; none is committed.
/// </remarks>
public sealed class ProductionKeycloak : IAsyncLifetime
{
    public const string Realm = "decisya";
    public const string ClientId = "decisya-bff";
    public const string AppHost = "app.decisya.example";
    public const string IdHost = "id.decisya.example";
    public const string HttpsPort = "8443";

    /// <summary>The tenant a tenant-user gets unless a test passes its own.</summary>
    public const string DefaultTenantId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    public const string AdminRole = "platform-admin";
    public const string TenantRole = "tenant-user";

    private const string BootstrapAdmin = "admin";

    private static readonly object EvidenceLock = new();

    private readonly SemaphoreSlim _startLock = new(1, 1);

    private INetwork? _network;
    private IContainer? _postgres;
    private IContainer? _keycloak;
    private string _bootstrapPassword = string.Empty;
    private string _dbPassword = string.Empty;
    private bool _started;

    public string ClientSecret { get; private set; } = string.Empty;

    /// <summary>The password every test user gets (random per run, never listed, never a username part).</summary>
    public string UserPassword { get; private set; } = string.Empty;

    public static string AppOrigin => $"https://{AppHost}:{HttpsPort}";

    public static string RedirectUri => AppOrigin + "/signin-oidc";

    /// <summary>The issuer Keycloak puts in every token: the wrapper-independent <c>KC_HOSTNAME</c>.</summary>
    public static string Issuer => $"https://{IdHost}:{HttpsPort}/realms/{Realm}";

    /// <summary>The Keycloak container's HTTP address as the test host reaches it.</summary>
    public string BaseAddress => $"http://{Keycloak.Hostname}:{Keycloak.GetMappedPublicPort(8080)}";

    /// <summary>The users the realm held right after the import, before any test created one.</summary>
    public int UsersRightAfterImport { get; private set; } = -1;

    /// <summary><see cref="RunIdentityCheckAsync"/> as it was right after the import.</summary>
    public IReadOnlyDictionary<string, string> IdentityCheckRightAfterImport { get; private set; } =
        new Dictionary<string, string>();

    /// <summary>Why the first identity check failed, or <see langword="null"/>.</summary>
    public string? IdentityCheckError { get; private set; }

    private IContainer Keycloak => _keycloak ?? throw NotStarted();

    private IContainer Postgres => _postgres ?? throw NotStarted();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_keycloak is not null)
        {
            await _keycloak.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
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

            ClientSecret = Hex(32);
            UserPassword = Hex(24);
            _dbPassword = Hex(24);
            _bootstrapPassword = Hex(24);

            _network = new NetworkBuilder().Build();
            await _network.CreateAsync(cancellationToken);

            _postgres = new ContainerBuilder(PostgresImage())
                .WithNetwork(_network)
                .WithNetworkAliases("pg")
                .WithEnvironment("POSTGRES_USER", "keycloak")
                .WithEnvironment("POSTGRES_PASSWORD", _dbPassword)
                .WithEnvironment("POSTGRES_DB", "keycloak")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                    "pg_isready", "-h", "127.0.0.1", "-p", "5432", "-U", "keycloak", "-d", "keycloak"))
                .Build();
            await _postgres.StartAsync(cancellationToken);

            _keycloak = BuildKeycloak(
                _network, ClientSecret, _dbPassword, _bootstrapPassword, withPasswordList: true);
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startTimeout.CancelAfter(TimeSpan.FromMinutes(6));
            await _keycloak.StartAsync(startTimeout.Token);

            using var admin = await AdminClientAsync(cancellationToken);
            UsersRightAfterImport = await CountUsersAsync(admin, cancellationToken);
            try
            {
                IdentityCheckRightAfterImport = await RunIdentityCheckAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Kept, not thrown: one broken query must not hide every other production test. The
                // identity-check tests assert on this and fail with the message.
                IdentityCheckError = exception.Message;
            }

            _started = true;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Builds a Keycloak container configured like the stack's (the pinned image, the wrapper as
    /// entrypoint, the same mount targets). <paramref name="network"/> null uses Keycloak's embedded
    /// development database, which is enough for tests that never read the database.
    /// </summary>
    public static IContainer BuildKeycloak(
        INetwork? network,
        string clientSecret,
        string dbPassword,
        string bootstrapPassword,
        bool withPasswordList,
        string? realmFileOverride = null)
    {
        var image = ContainerImages.Reference(
            ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage,
            ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256);

        var builder = new ContainerBuilder(image)
            .WithPortBinding(8080, true)
            .WithPortBinding(9000, true)
            .WithEntrypoint("/bin/sh", "/opt/decisya/entrypoint-stack.sh")
            .WithEnvironment("KC_DB_PASSWORD_FILE", "/run/secrets/keycloak_db_password")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", BootstrapAdmin)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD_FILE", "/run/secrets/keycloak_bootstrap_admin_password")
            .WithEnvironment("KC_HOSTNAME", $"https://{IdHost}:{HttpsPort}")
            .WithEnvironment("KC_HTTP_ENABLED", "true")
            .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
            .WithEnvironment("KC_HEALTH_ENABLED", "true")
            .WithEnvironment("DECISYA_APP_HOST", AppHost)
            .WithEnvironment("DECISYA_HTTPS_PORT", HttpsPort)
            .WithResourceMapping(ReadRepoFile("deploy/keycloak/entrypoint-stack.sh"), "/opt/decisya/entrypoint-stack.sh")
            .WithResourceMapping(
                realmFileOverride is null ? ReadRepoFile("deploy/keycloak/production/realm-decisya.json") : Encoding.UTF8.GetBytes(realmFileOverride),
                "/opt/keycloak/data/import/realm-decisya.json")
            .WithResourceMapping(Encoding.ASCII.GetBytes(dbPassword), "/run/secrets/keycloak_db_password")
            .WithResourceMapping(Encoding.ASCII.GetBytes(bootstrapPassword), "/run/secrets/keycloak_bootstrap_admin_password")
            .WithResourceMapping(Encoding.ASCII.GetBytes(clientSecret), "/run/secrets/Bff__Oidc__ClientSecret")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(9000).ForPath("/health/ready")));

        if (withPasswordList)
        {
            builder = builder.WithResourceMapping(
                ReadRepoFile("deploy/keycloak/production/common-passwords.txt"),
                "/opt/keycloak/data/password-blacklists/common-passwords.txt");
        }

        if (network is not null)
        {
            builder = builder
                .WithNetwork(network)
                .WithEnvironment("KC_DB", "postgres")
                .WithEnvironment("KC_DB_URL", "jdbc:postgresql://pg:5432/keycloak")
                .WithEnvironment("KC_DB_USERNAME", "keycloak");
        }

        return builder.Build();
    }

    public static string Hex(int length) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(length))[..length];

    public static byte[] ReadRepoFile(string relativePath) =>
        File.ReadAllBytes(Path.Combine(RepoRoot.Value, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Own repo-root lookup (walk up to <c>decisya.slnx</c>): this file is linked into two test projects whose <c>RepoPaths</c> differ.</summary>
    private static readonly Lazy<string> RepoRoot = new(() =>
    {
        // Directory.Build.props stamps every project with Decisya.RepoRoot: the build output lives outside the repository.
        var stamped = typeof(ProductionKeycloak).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "Decisya.RepoRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(stamped) && File.Exists(Path.Combine(stamped, "decisya.slnx")))
        {
            return stamped;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "decisya.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (decisya.slnx).");
    });

    private static string PostgresImage() => ContainerImages.Reference(
        ContainerImages.PostgresRegistry, ContainerImages.PostgresImage, ContainerImages.PostgresTag, ContainerImages.PostgresSha256);

    // ------------------------------------------------------------------ admin REST

    public async Task<HttpClient> AdminClientAsync(CancellationToken cancellationToken)
    {
        var token = await GetAdminTokenAsync(cancellationToken);
        var client = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> GetAdminTokenAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        using var response = await client.PostAsync(
            "/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = BootstrapAdmin,
                ["password"] = _bootstrapPassword,
            }),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    public static async Task<JsonElement> GetJsonAsync(HttpClient admin, string path, CancellationToken cancellationToken)
    {
        using var response = await admin.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    public static async Task<int> CountUsersAsync(HttpClient admin, CancellationToken cancellationToken)
    {
        var count = await GetJsonAsync(admin, $"/admin/realms/{Realm}/users/count", cancellationToken);
        return count.GetInt32();
    }

    /// <summary>
    /// Creates a synthetic user (the realm's <c>synthetic</c> attribute set to <c>true</c>) by partial
    /// import, so an OTP credential with a known secret can be created at test time. Returns the
    /// credentials the test needs and nothing else; no value is ever written to evidence.
    /// </summary>
    public async Task<TestUser> CreateUserAsync(
        string role, bool withTotp, CancellationToken cancellationToken, bool synthetic = true, string? tenantId = null)
    {
        var username = "t-" + Hex(12);
        var totpSecret = withTotp ? Hex(20) : null;
        var attributes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (synthetic)
        {
            attributes["synthetic"] = ["true"];
        }

        if (role == TenantRole)
        {
            attributes["tenant_id"] = [tenantId ?? DefaultTenantId];
        }

        var credentials = new List<object>
        {
            new { type = "password", value = UserPassword, temporary = false },
        };
        if (totpSecret is not null)
        {
            credentials.Add(new
            {
                type = "otp",
                secretData = JsonSerializer.Serialize(new { value = totpSecret }),
                credentialData = JsonSerializer.Serialize(new { subType = "totp", digits = 6, counter = 0, period = 30, algorithm = "HmacSHA1" }),
            });
        }

        using var admin = await AdminClientAsync(cancellationToken);
        using var response = await admin.PostAsJsonAsync(
            $"/admin/realms/{Realm}/partialImport",
            new
            {
                ifResourceExists = "FAIL",
                users = new[]
                {
                    new
                    {
                        username,
                        email = username + "@decisya.invalid",
                        enabled = true,
                        emailVerified = true,
                        firstName = "Synthetic",
                        lastName = "User",
                        requiredActions = Array.Empty<string>(),
                        realmRoles = new[] { role },
                        attributes,
                        credentials,
                    },
                },
            },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue($"the partial import answered {(int)response.StatusCode}: {Truncate(body, 300)}");

        return new TestUser(username, UserPassword, totpSecret);
    }

    public async Task DeleteUserAsync(string username, CancellationToken cancellationToken)
    {
        using var admin = await AdminClientAsync(cancellationToken);
        var users = await GetJsonAsync(admin, $"/admin/realms/{Realm}/users?username={username}&exact=true", cancellationToken);
        foreach (var user in users.EnumerateArray())
        {
            using var response = await admin.DeleteAsync($"/admin/realms/{Realm}/users/{user.GetProperty("id").GetString()}", cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    // ------------------------------------------------------------------ identity check (psql)

    public async Task<IReadOnlyDictionary<string, string>> RunIdentityCheckAsync(CancellationToken cancellationToken)
    {
        var sql = ReadRepoFile("deploy/keycloak/production/identity-check.sql");
        await Postgres.CopyAsync(sql, "/tmp/identity-check.sql", ct: cancellationToken);

        // Same access path as stackctl: the SQL on stdin, -At, ON_ERROR_STOP.
        var result = await Postgres.ExecAsync(
            ["sh", "-c", "psql -U keycloak -d keycloak -At -v ON_ERROR_STOP=1 < /tmp/identity-check.sql"], cancellationToken);
        result.ExitCode.Should().Be(0, $"psql answered: {Truncate(result.Stderr, 600)}");

        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // BEGIN and ROLLBACK echo their command tags; only key=value lines count.
            if (raw is "BEGIN" or "ROLLBACK")
            {
                continue;
            }

            var match = Regex.Match(raw, "^([a-z0-9_]+)=(true|false|[0-9]+)$");
            match.Success.Should().BeTrue("the identity check prints key=<int|true|false> lines only");
            lines[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return lines;
    }

    /// <summary>Executes an arbitrary read-only diagnostic query in the Keycloak database (schema discovery).</summary>
    public async Task<string> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        var result = await Postgres.ExecAsync(
            ["psql", "-U", "keycloak", "-d", "keycloak", "-At", "-v", "ON_ERROR_STOP=1", "-c", sql], cancellationToken);
        return result.ExitCode == 0 ? result.Stdout : "ERROR: " + result.Stderr;
    }

    public async Task<string> KeycloakLogsAsync(CancellationToken cancellationToken)
    {
        var (stdout, stderr) = await Keycloak.GetLogsAsync(ct: cancellationToken);
        return stdout + stderr;
    }

    // ------------------------------------------------------------------ login

    public sealed record TestUser(string Username, string Password, string? TotpSecret);

    public sealed record LoginResult(
        HttpClient Client,
        string Code,
        string Verifier,
        bool OtpPromptShown,
        bool ConfigureTotpShown);

    public sealed record TokenSet(string AccessToken, string RefreshToken, string IdToken);

    public readonly record struct AcrInfo(int Count, JsonValueKind Kind, string Value);

    public static string BuildAuthorizeUrl(string challenge, string? acrValues) =>
        $"/realms/{Realm}/protocol/openid-connect/auth"
        + $"?client_id={ClientId}&response_type=code&scope=openid"
        + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
        + $"&state={Guid.NewGuid():N}&nonce={Guid.NewGuid():N}"
        + $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256"
        + (acrValues is null ? string.Empty : $"&acr_values={Uri.EscapeDataString(acrValues)}");

    /// <summary>Creates the browser-shaped client (cookie relay, <c>X-Forwarded-Proto: https</c> as Caddy sends it).</summary>
    public HttpClient CreateBrowserClient()
    {
        var client = new HttpClient(new SecureCookieRelayHandler()) { BaseAddress = new Uri(BaseAddress) };
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return client;
    }

    /// <summary>
    /// Drives the authorization-code flow to the redirect carrying the code: the login form, then
    /// the OTP form or the <c>CONFIGURE_TOTP</c> enrolment page when Keycloak shows one. When
    /// <paramref name="stopAtConfigureTotp"/> is set, the flow stops at the enrolment page.
    /// </summary>
    public async Task<LoginResult> LoginAsync(
        string username,
        string password,
        string? acrValues,
        string? totpSecret,
        CancellationToken cancellationToken,
        bool stopAtConfigureTotp = false,
        bool expectFailure = false)
    {
        var client = CreateBrowserClient();
        var (verifier, challenge) = OidcTestHelpers.GeneratePkce();
        var response = await client.GetAsync(BuildAuthorizeUrl(challenge, acrValues), cancellationToken);

        var otpShown = false;
        var configureShown = false;
        var passwordSent = false;

        for (var step = 0; step < 8; step++)
        {
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
            {
                var location = response.Headers.Location!;
                var absolute = location.IsAbsoluteUri ? location.ToString() : new Uri(client.BaseAddress!, location).ToString();
                if (absolute.StartsWith(RedirectUri, StringComparison.Ordinal))
                {
                    var query = OidcTestHelpers.ParseQuery(new Uri(absolute).Query);
                    query.Should().ContainKey("code", "the redirect should carry a code");
                    return new LoginResult(client, query["code"], verifier, otpShown, configureShown);
                }

                response = await client.GetAsync(ToLocalPath(location), cancellationToken);
                continue;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK && expectFailure)
            {
                return new LoginResult(client, string.Empty, verifier, otpShown, configureShown);
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"step {step} should be a page; it starts: {Truncate(StripTags(html), 160)}");

            if (html.Contains("name=\"otp\"", StringComparison.Ordinal))
            {
                otpShown = true;
                totpSecret.Should().NotBeNull("an OTP prompt appeared but the test holds no secret");
                var fields = HiddenInputs(html);
                fields["otp"] = ComputeTotp(totpSecret!);
                response = await client.PostAsync(FormAction(html), new FormUrlEncodedContent(fields), cancellationToken);
            }
            else if (html.Contains("name=\"totpSecret\"", StringComparison.Ordinal))
            {
                configureShown = true;
                if (stopAtConfigureTotp)
                {
                    return new LoginResult(client, string.Empty, verifier, otpShown, configureShown);
                }

                var fields = HiddenInputs(html);
                fields["totp"] = ComputeTotp(fields["totpSecret"]);
                fields["userLabel"] = "test";
                response = await client.PostAsync(FormAction(html), new FormUrlEncodedContent(fields), cancellationToken);
            }
            else if (html.Contains("kc-form-login", StringComparison.Ordinal) && !passwordSent)
            {
                passwordSent = true;
                var action = OidcTestHelpers.ExtractLoginFormAction(html);
                response = await client.PostAsync(
                    ToLocalPath(new Uri(action, UriKind.RelativeOrAbsolute)),
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = password }),
                    cancellationToken);
            }
            else if (expectFailure)
            {
                return new LoginResult(client, string.Empty, verifier, otpShown, configureShown);
            }
            else
            {
                throw new InvalidOperationException($"Unrecognised page at step {step}: {Truncate(StripTags(html), 200)}");
            }
        }

        if (expectFailure)
        {
            return new LoginResult(client, string.Empty, verifier, otpShown, configureShown);
        }

        throw new InvalidOperationException("The login did not finish within 8 steps.");
    }

    public async Task<TokenSet> ExchangeAsync(LoginResult login, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/realms/{Realm}/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = login.Code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = login.Verifier,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}")));
        using var response = await login.Client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"the token endpoint answered: {Truncate(body, 200)}");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new TokenSet(
            root.GetProperty("access_token").GetString()!,
            root.GetProperty("refresh_token").GetString()!,
            root.GetProperty("id_token").GetString()!);
    }

    /// <summary>The <c>acr</c> claim of a token: how many times it occurs in the raw payload, and its JSON kind and value.</summary>
    public static AcrInfo ReadAcr(string jwt)
    {
        var raw = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(jwt.Split('.')[1]));
        var count = Regex.Count(raw, "\"acr\"\\s*:");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.TryGetProperty("acr", out var element)
            ? new AcrInfo(count, element.ValueKind, element.ToString())
            : new AcrInfo(count, JsonValueKind.Undefined, "(absent)");
    }

    // ------------------------------------------------------------------ evidence

    /// <summary>Appends an observation line to <c>production-evidence.txt</c> next to the test binaries. Never a token, secret or password.</summary>
    public static void Record(string line)
    {
        lock (EvidenceLock)
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "production-evidence.txt"), line + Environment.NewLine);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string ToLocalPath(Uri uri) => uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;

    private static string FormAction(string html)
    {
        var tag = Regex.Match(html, "<form\\b[^>]*>", RegexOptions.IgnoreCase).Value;
        var action = Regex.Match(tag, "action=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value;
        return ToLocalPath(new Uri(WebUtility.HtmlDecode(action), UriKind.RelativeOrAbsolute));
    }

    private static Dictionary<string, string> HiddenInputs(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = input.Value;
            if (!Regex.IsMatch(tag, "type=\"hidden\"", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var name = Regex.Match(tag, "name=\"([^\"]*)\"").Groups[1].Value;
            var value = Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value;
            if (name.Length > 0)
            {
                result[name] = WebUtility.HtmlDecode(value);
            }
        }

        return result;
    }

    private static string StripTags(string html) =>
        Regex.Replace(Regex.Replace(html, "<script.*?</script>|<style.*?</style>", " ", RegexOptions.Singleline), "<[^>]+>", " ")
            .Replace('\n', ' ');

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    // RFC 6238 over the raw secret's UTF-8 bytes, as Keycloak validates it. SHA-1 is the algorithm
    // of the realm's OTP policy (HmacSHA1), so the weak-hash rule does not apply.
#pragma warning disable CA5350
    public static string ComputeTotp(string secret)
    {
        var counter = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() / 30;
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
#pragma warning restore CA5350

    private static InvalidOperationException NotStarted() =>
        new("ProductionKeycloak has not started yet. Call EnsureStartedAsync first.");
}

using System.Net;
using System.Text.Json;
using Decisya.Bff.KeyRing;
using Decisya.Bff.Session;
using Decisya.Bff.Tests.RateLimiting;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using static Decisya.Bff.Tests.KeyRing.KeyRingHosts;
using static Decisya.Bff.Tests.RateLimiting.RateLimitRequests;

namespace Decisya.Bff.Tests.KeyRing;

/// <summary>
/// #122 G5 (Stories 8, 9 and 11, NFR-54, NFR-56) with a real session cookie: the cookie is protected by the
/// BFF's own cookie ticket format over a certificate-wrapped on-disk key ring, the ticket lives in the real
/// Redis store, and a host restart is a second <see cref="RateLimitFactory"/> over the same ring directory.
/// The loopback ApiDouble stands in for the Api; no listener is opened on any other address.
/// </summary>
[Trait("Category", "Integration")]
public class SessionKeyRingTests(RedisFixture redisFixture)
{
    private static Dictionary<string, string?> Ring(string directory, TestCertificate current, TestCertificate? previous = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Bff:DataProtection:KeyRingPath"] = directory,
            ["Bff:DataProtection:Certificate"] = current.Pfx,
            ["Bff:DataProtection:CertificatePassword"] = current.Password,
        };
        if (previous is not null)
        {
            values["Bff:DataProtection:PreviousCertificate"] = previous.Pfx;
            values["Bff:DataProtection:PreviousCertificatePassword"] = previous.Password;
        }

        return values;
    }

    private RateLimitFactory Host(string directory, TestCertificate current, TestCertificate? previous = null, string? apiAddress = null) =>
        RateLimitFactory.Create(
            Ring(directory, current, previous), redisConnectionString: redisFixture.ConnectionString, apiAddress: apiAddress);

    private static async Task<bool> IsAuthenticatedAsync(RateLimitFactory factory, string cookie)
    {
        using var client = factory.CreateBffClient();
        using var request = Get("/bff/me", cookie: cookie);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("isAuthenticated").GetBoolean();
    }

    [Fact]
    public async Task A_session_cookie_still_authenticates_after_a_host_restart_with_the_same_wrapped_ring()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        var directory = NewKeyDirectory(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        SeededSession session;
        using (var first = Host(directory, TestCertificates.A))
        {
            session = await SessionSeeding.SeedAsync(first);
            (await IsAuthenticatedAsync(first, session.Cookie)).Should().BeTrue("the cookie authenticates on the host that issued it");
        }

        var filesBefore = KeyFiles(directory);
        filesBefore.Should().ContainSingle();

        using var second = Host(directory, TestCertificates.A);
        (await IsAuthenticatedAsync(second, session.Cookie)).Should().BeTrue("the ring survived on disk and the certificate unwraps it");

        KeyFiles(directory).Should().BeEquivalentTo(filesBefore, "a restart creates no second key");
        KeyRingFileRules.Inspect(KeyFiles(directory)[0]).Verdict.Should().Be(KeyFileVerdict.Ok);
    }

    [Fact]
    public async Task A_session_cookie_survives_certificate_rotation_and_new_keys_are_wrapped_by_the_new_certificate()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        var directory = NewKeyDirectory(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        SeededSession session;
        using (var first = Host(directory, TestCertificates.A))
        {
            session = await SessionSeeding.SeedAsync(first);
        }

        var oldFiles = KeyFiles(directory);

        using var rotated = Host(directory, TestCertificates.B, previous: TestCertificates.A);
        (await IsAuthenticatedAsync(rotated, session.Cookie)).Should().BeTrue("certificate A stays as unprotect-only");

        var now = SystemClock.Instance.GetCurrentInstant().ToDateTimeOffset();
        rotated.Services.GetRequiredService<IKeyManager>().CreateNewKey(now, now.AddDays(90));
        var newFile = KeyFiles(directory).Except(oldFiles).Should().ContainSingle().Subject;
        var info = KeyRingFileRules.Inspect(newFile);
        info.Verdict.Should().Be(KeyFileVerdict.Ok);
        info.WrappingFingerprints.Should().Contain(KeyRingCertificates.Fingerprint(TestCertificates.B.Certificate));
        info.WrappingFingerprints.Should().NotContain(KeyRingCertificates.Fingerprint(TestCertificates.A.Certificate));
    }

    [Fact]
    public async Task After_a_key_ring_reset_a_pre_reset_cookie_gets_401_never_500_and_a_new_sign_in_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        await using var api = await ApiDouble.StartAsync(ct);
        var directory = NewKeyDirectory(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        SeededSession oldSession;
        using (var first = Host(directory, TestCertificates.A, apiAddress: api.Address))
        {
            oldSession = await SessionSeeding.SeedAsync(first);
            using var firstClient = first.CreateBffClient();
            (await StatusAsync(firstClient, Get("/api/tenancy/me", "203.0.113.5", cookie: oldSession.Cookie)))
                .Should().Be(HttpStatusCode.OK, "before the reset the cookie works");
        }

        // The documented reset: move the old ring aside and leave the volume empty.
        var aside = NewKeyDirectory();
        foreach (var file in KeyFiles(directory))
        {
            File.Move(file, Path.Combine(aside, Path.GetFileName(file)));
        }

        KeyFiles(directory).Should().BeEmpty();
        var requestsBefore = api.Requests.Count;

        using var reset = Host(directory, TestCertificates.A, apiAddress: api.Address);
        using var client = reset.CreateBffClient();

        (await StatusAsync(client, Get("/api/tenancy/me", "203.0.113.5", cookie: oldSession.Cookie)))
            .Should().Be(HttpStatusCode.Unauthorized, "an old cookie is rejected as unauthenticated, never with a 500");
        api.Requests.Count.Should().Be(requestsBefore, "the rejected request is not forwarded");
        (await IsAuthenticatedAsync(reset, oldSession.Cookie)).Should().BeFalse();

        var fresh = await SessionSeeding.SeedAsync(reset);
        (await IsAuthenticatedAsync(reset, fresh.Cookie)).Should().BeTrue("a new sign-in works on the new ring");
        (await StatusAsync(client, Get("/api/tenancy/me", "203.0.113.5", cookie: fresh.Cookie))).Should().Be(HttpStatusCode.OK);

        var created = KeyFiles(directory);
        created.Should().ContainSingle("the reset volume holds exactly the new ring");
        KeyRingFileRules.Inspect(created[0]).Verdict.Should().Be(KeyFileVerdict.Ok, "the new key file is certificate-wrapped");
        // The ticket body is itself Data Protection-protected, so after the reset the store cannot read it
        // (RetrieveAsync degrades to null, never a 500); the entry is still in Redis, untouched.
        (await reset.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase()
            .KeyExistsAsync("decisya:bff:ticket:" + oldSession.TicketKey))
            .Should().BeTrue("the reset touches nothing in Redis");
        (await reset.Services.GetRequiredService<RedisTicketStore>().RetrieveAsync(oldSession.TicketKey))
            .Should().BeNull("the old ticket body cannot be unprotected on the new ring, and that degrades to null");
    }
}

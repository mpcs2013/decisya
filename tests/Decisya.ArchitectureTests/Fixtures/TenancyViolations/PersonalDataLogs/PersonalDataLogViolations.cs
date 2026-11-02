#pragma warning disable CA1848, CA2254, CA1873, CA1822, CA2201, CA1810, CA1823, IDE0060, IDE0051, CA1859, CA2007
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.PersonalDataLogs;

// Issue #230 red fixtures (G1 Stories 1 and 5; G2 R1 to R4a; G3 G4-230-01 to G4-230-04). Each method passes personal
// data to a sink under an innocent placeholder, and each must be reported. None of them runs; the IL is what the rule
// reads. CA1848 and CA2254 are suppressed on purpose: plain calls and interpolated templates are the shapes the rule
// has to catch. Synthetic values only.

// ---- R1: one per category, F1 (the caller), F2 to F8, F9 ([LoggerMessage]), F10 (interpolated), F11 (#15) ----

/// <summary>F1: <c>ICurrentCaller.UserId</c> under <c>{Who}</c>.</summary>
public static class UserIdOverCaller
{
    public static void Run(ILogger logger, ICurrentCaller caller) => logger.LogInformation("Seen {Who}", caller.UserId);
}

/// <summary>F2 to F8: one per category, each under the placeholder <c>{Value}</c>.</summary>
public static class EachCategory
{
    public static void LogUserId(ILogger logger, Person person) => logger.LogInformation("Value {Value}", person.UserId);

    public static void LogEmail(ILogger logger, Person person) => logger.LogInformation("Value {Value}", person.Email);

    public static void LogDisplayName(ILogger logger, Person person) => logger.LogInformation("Value {Value}", person.DisplayName);

    public static void LogIban(ILogger logger, Person person) => logger.LogInformation("Value {Value}", person.Iban);

    public static void LogDescription(ILogger logger, Tx tx) => logger.LogInformation("Value {Value}", tx.Description);

    public static void LogPrompt(ILogger logger, Person person) => logger.LogInformation("Value {Value}", person.Prompt);

    public static void LogClientSecret(ILogger logger, Person person) => logger.LogInformation("Value {Value}", person.ClientSecret);
}

/// <summary>F9: a <c>[LoggerMessage]</c> parameter fed from a description.</summary>
public static partial class LoggerMessageFed
{
    public static void Run(ILogger logger, Tx tx) => Paid(logger, tx.Description);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Paid {Payee}")]
    private static partial void Paid(ILogger logger, string payee);
}

/// <summary>F10: an interpolated template.</summary>
public static class InterpolatedTemplate
{
    public static void Run(ILogger logger, Person member) => logger.LogWarning($"Failed for {member.UserId}");
}

/// <summary>F11: the #15 case, <c>LogInformation("Paid {Payee}", tx.Description)</c>.</summary>
public static class PaidPayee
{
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("Paid {Payee}", tx.Description);
}

// ---- R2: one per lowering ----

/// <summary>A local variable hop.</summary>
public static class LocalHop
{
    public static void Run(ILogger logger, Tx tx)
    {
        var first = tx.Description;
        var second = first;
        logger.LogInformation("Hop {Value}", second);
    }
}

/// <summary>An <c>await</c> between the read and the log (a hoisted local).</summary>
public static class AwaitBetween
{
    public static async Task Run(ILogger logger, Tx tx)
    {
        var description = tx.Description;
        await Task.Delay(1).ConfigureAwait(false);
        logger.LogInformation("Later {Value}", description);
    }
}

/// <summary>A lambda that captures the value.</summary>
public static class ClosureCapture
{
    public static void Run(ILogger logger, Tx tx)
    {
        var description = tx.Description;
        Action log = () => logger.LogInformation("Captured {Value}", description);
        log();
    }
}

/// <summary><c>?:</c> and <c>??</c>.</summary>
public static class Conditionals
{
    public static void Ternary(ILogger logger, Tx tx, bool flag)
    {
        var value = flag ? tx.Description : "none";
        logger.LogInformation("Choice {Value}", value);
    }

    public static void Coalesce(ILogger logger, Tx tx, string? other)
    {
        var value = other ?? tx.Description;
        logger.LogInformation("Choice {Value}", value);
    }
}

/// <summary><c>string.Concat</c> and <c>StringBuilder</c>.</summary>
public static class ConcatAndBuilder
{
    public static void Concat(ILogger logger, Tx tx) => logger.LogInformation(string.Concat("paid ", tx.Description));

    public static void Builder(ILogger logger, Tx tx)
    {
        var builder = new StringBuilder();
        builder.Append("paid ").Append(tx.Description);
        logger.LogInformation("Built {Value}", builder.ToString());
    }
}

/// <summary><c>BeginScope</c> with a dictionary.</summary>
public static class ScopeWithMap
{
    public static void Run(ILogger logger, Tx tx)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["payee"] = tx.Description });
    }
}

/// <summary>S4: an exception message built from a catalogue value.</summary>
public static class ExceptionMessage
{
    public static void Run(Tx tx) => throw new InvalidOperationException($"Failed for {tx.Description}");
}

/// <summary>S5: a helper that forwards its parameter to a log call. The call site in <c>Run</c> is the finding.</summary>
public static class HelperForwarding
{
    public static void Run(ILogger logger, Tx tx) => Forward(logger, tx.Description);

    private static void Forward(ILogger logger, string text) => logger.LogInformation("Item {Text}", text);
}

/// <summary>S5: a method that returns a catalogue value under an innocent name.</summary>
public static class ReturnsValue
{
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("Label {Label}", Label(tx));

    private static string Label(Tx tx) => tx.Description.Trim();
}

/// <summary>S5: a field set from a source in one method and logged in another.</summary>
public sealed class FieldSetElsewhere
{
    private string? _held;

    public void Capture(Tx tx) => _held = tx.Description;

    public void Emit(ILogger logger) => logger.LogInformation("Held {Value}", _held);
}

/// <summary>S3: a <c>LoggerMessage.Define</c> delegate invoked.</summary>
public static class DefineInvoke
{
    private static readonly Action<ILogger, string, Exception?> Paid =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(9002), "Paid {Payee}");

    public static void Run(ILogger logger, Tx tx) => Paid(logger, tx.Description, null);
}

/// <summary><c>principal.FindFirst("sub")?.Value</c>.</summary>
public static class ClaimSub
{
    public static void Run(ILogger logger, ClaimsPrincipal principal) =>
        logger.LogInformation("Seen {Subject}", principal.FindFirst("sub")?.Value);
}

/// <summary><c>principal.Claims</c> enumerated into a log.</summary>
public static class ClaimsEnumerated
{
    public static void Run(ILogger logger, ClaimsPrincipal principal)
    {
        foreach (var claim in principal.Claims)
        {
            logger.LogInformation("Claim {Value}", claim.Value);
        }
    }
}

// ---- R3: [Sensitive] semantics ----

/// <summary>A scalar read from a <c>[Sensitive]</c> member is not masked (it is a string).</summary>
public static class ScalarFromSensitive
{
    public static void NoCategory(ILogger logger, SensitiveHolder holder) => logger.LogInformation("Value {Value}", holder.Opaque);

    public static void InCategory(ILogger logger, Vault vault) => logger.LogInformation("Value {Value}", vault.UserId);
}

/// <summary>An object passed whole whose personal member lacks <c>[Sensitive]</c>.</summary>
public static class WholeWithUnmarkedMember
{
    public static void Run(ILogger logger, OpenProfile profile) => logger.LogInformation("Profile {Profile}", profile);
}

/// <summary>A record with a <c>[Sensitive]</c> member and a synthesized <c>ToString</c>, interpolated.</summary>
public static class RecordInterpolated
{
    public static void Run(ILogger logger, MarkedRecord record) => logger.LogInformation($"seen {record}");
}

// ---- R4a: the G3 MUSTs ----

/// <summary>G4-230-01: taint survives a Decisya object.</summary>
public static class WrappedInDecisyaObject
{
    public static void LoggedWhole(ILogger logger, Tx tx)
    {
        var note = new PaymentNote(tx.Description);
        logger.LogInformation("Note {Note}", note);
    }

    public static void Interpolated(ILogger logger, Tx tx)
    {
        var note = new PaymentNote(tx.Description);
        logger.LogInformation($"note {note}");
    }

    public static void Initializer(ILogger logger, Tx tx)
    {
        var view = new View { Text = tx.Description };
        logger.LogInformation("View {View}", view);
    }

    public static void ValueObject(ILogger logger, Account account) => logger.LogInformation("Account {Account}", account.Iban);

    public static void GuidMember(ILogger logger, GuidIdentified identified) => logger.LogInformation("Who {Who}", identified.UserId);
}

/// <summary>G4-230-02: the framework readers of tokens, cookies and JWT payloads.</summary>
public static class FrameworkReaders
{
    public static void JwtPayload(ILogger logger, JsonWebToken token)
    {
        if (token.TryGetPayloadValue<string>("sub", out var subject))
        {
            logger.LogInformation("Subject {Subject}", subject);
        }
    }

    public static void RequestCookie(ILogger logger, HttpContext context, string name)
    {
        if (context.Request.Cookies.TryGetValue(name, out var cookie))
        {
            logger.LogInformation("Cookie {Cookie}", cookie);
        }
    }

    public static void StoredToken(ILogger logger, AuthenticationProperties properties) =>
        logger.LogInformation("Token {Token}", properties.GetTokenValue("refresh_token"));

    public static async Task HttpContextToken(ILogger logger, HttpContext context)
    {
        var token = await context.GetTokenAsync("access_token").ConfigureAwait(false);
        logger.LogInformation("Token {Token}", token);
    }
}

/// <summary>G4-230-03: <c>Console</c>, <c>Debug</c> and <c>Trace</c> writes are sinks.</summary>
public static class UnstructuredWrites
{
    public static void ConsoleWrite(Tx tx) => Console.WriteLine(tx.Description);

    public static void ConsoleErrorWrite(Tx tx) => Console.Error.WriteLine(tx.Description);

    public static void DebugWrite(Tx tx) => Debug.WriteLine(tx.Description);

    public static void TraceWrite(Tx tx) => Trace.WriteLine(tx.Description);
}

/// <summary>G4-230-04: a hand-written method carrying <c>[GeneratedCode]</c> is examined as usual.</summary>
public static class HandWrittenGenerated
{
    [GeneratedCode("fixture", "1.0")]
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("Value {Value}", tx.Description);
}

/// <summary>G6-230-01: a hand-written type carrying <c>[GeneratedCode]</c> is checked for unmarked personal members.</summary>
[GeneratedCode("fixture", "1.0")]
public sealed class GeneratedCodeContact
{
    public string Email { get; init; } = string.Empty;
}

/// <summary>G6-230-01: a hand-written type carrying <c>[CompilerGenerated]</c> is checked for unmarked personal members.</summary>
[System.Runtime.CompilerServices.CompilerGenerated]
public sealed class CompilerGeneratedContact
{
    public string Email { get; init; } = string.Empty;
}

/// <summary>G6-230-01: each marked type is logged whole.</summary>
public static class HandWrittenGeneratedTypes
{
    public static void GeneratedCodeType(ILogger logger, GeneratedCodeContact contact) => logger.LogInformation("Value {Value}", contact);

    public static void CompilerGeneratedType(ILogger logger, CompilerGeneratedContact contact) => logger.LogInformation("Value {Value}", contact);
}

// ---- R5 (extension): a member under a name the real catalogue lacks, red only once a category lists it ----

/// <summary>Clean with the real catalogue; the extension test adds a category whose memberNames holds <c>Payee</c>.</summary>
public static class PayeeRead
{
    public static void Run(ILogger logger, PaymentNote note) => logger.LogInformation("Paid {Payee}", note.Payee);
}

// ---- R8 (G3 S-02): a template that holds a canary literal, which must never reach the output ----

/// <summary>The template literal must not appear in any finding or statistic.</summary>
public static class CanaryLiteral
{
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("canary-template-literal-9c1e {Value}", tx.Description);
}

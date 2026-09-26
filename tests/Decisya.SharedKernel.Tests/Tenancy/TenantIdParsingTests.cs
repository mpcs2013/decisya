using System.Text.Json;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>
/// G3 change 1 (G4-32-01): <see cref="Guid.TryParseExact(ReadOnlySpan{char}, ReadOnlySpan{char}, out Guid)"/>
/// with the "D" format keeps .NET's legacy compatibility parsing, so an explicit
/// character-shape check must run first. G3 change 2 (G4-32-02): a length cap runs before
/// any whitespace trim.
/// </summary>
/// <remarks>
/// What the runtime actually accepts, verified by running <c>Guid.TryParseExact(value, "D",
/// out _)</c> directly, with no shape check in front of it, on .NET 10 (2026-09-26):
/// <list type="bullet">
/// <item><description><c>"0x5b1407-351d-4694-9392-03acc5870eb1"</c> and
/// <c>"0X5b1407-351d-4694-9392-03acc5870eb1"</c> both parse, to
/// <c>005b1407-351d-4694-9392-03acc5870eb1</c> — the same GUID a plain
/// <c>"005b1407-351d-4694-9392-03acc5870eb1"</c> parses to. This is the exact collision
/// T-02 describes: two different strings naming one tenant.</description></item>
/// <item><description><c>"+85b1407-351d-4694-9392-03acc5870eb1"</c> parses, to
/// <c>085b1407-351d-4694-9392-03acc5870eb1</c>.</description></item>
/// <item><description><c>"d85b1407-0x1d-4694-9392-03acc5870eb1"</c> (the <c>0x</c> prefix
/// inside a later, non-first component) parses, to
/// <c>d85b1407-001d-4694-9392-03acc5870eb1</c>.</description></item>
/// <item><description>The braced (<c>{…}</c>), parenthesised (<c>(…)</c>) and bare-hex
/// (<c>N</c>, 32 characters, no hyphens) forms of a valid GUID, and inputs one character
/// shorter or longer than 36, are all rejected by <c>TryParseExact("D")</c> itself, purely
/// because their length differs from 36 — the explicit shape check below is redundant for
/// these, but still runs first for every input, not only the ones that need it.</description></item>
/// <item><description>A fullwidth digit (U+FF10) in place of the leading <c>0</c> is
/// <em>also</em> rejected by <c>TryParseExact("D")</c> itself, even though the span stays
/// 36 characters long. The explicit <see cref="char.IsAsciiHexDigit(char)"/> check below is
/// defence in depth, not a fix for an accepted case, for this particular input.</description></item>
/// </list>
/// The only inputs the shape check must actively intercept — because
/// <c>TryParseExact("D")</c> alone would otherwise accept them — are the <c>0x</c>/<c>0X</c>/
/// <c>+</c> compatibility-prefixed ones above.
/// </remarks>
public class TenantIdParsingTests
{
    public const string CanonicalText = "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70";

    public static TheoryData<string> RejectedShapes() =>
    [
        // Legacy Guid compatibility forms TryParseExact("D") alone would still accept.
        "0x5b1407-351d-4694-9392-03acc5870eb1",
        "0X5b1407-351d-4694-9392-03acc5870eb1",
        "+85b1407-351d-4694-9392-03acc5870eb1",
        "d85b1407-0x1d-4694-9392-03acc5870eb1",

        // The N, B, P and X forms of a valid GUID.
        "018f2c9e6b7a7c3e8b1a2f3c4d5e6f70",
        "{018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70}",
        "(018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70)",
        "{0x018f2c9e,0x6b7a,0x7c3e,{0x8b,0x1a,0x2f,0x3c,0x4d,0x5e,0x6f,0x70}}",

        // A hyphen moved by one position.
        "018f2c9e6-b7a-7c3e-8b1a-2f3c4d5e6f70",

        // A fullwidth digit (U+FF10) in place of the leading '0'.
        "０18f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70",
    ];

    [Theory]
    [MemberData(nameof(RejectedShapes))]
    public void TryParse_span_overload_rejects_every_non_canonical_shape(string value)
    {
        TenantId.TryParse(value.AsSpan(), out var tenantId).Should().BeFalse();
        tenantId.Should().Be(default(TenantId));
    }

    [Theory]
    [MemberData(nameof(RejectedShapes))]
    public void Parse_throws_for_every_non_canonical_shape(string value)
    {
        var act = () => TenantId.Parse(value);

        act.Should().Throw<TenantIdFormatException>();
    }

    [Theory]
    [MemberData(nameof(RejectedShapes))]
    public void The_JSON_converter_rejects_every_non_canonical_shape(string value)
    {
        var json = JsonSerializer.Serialize(value);

        var act = () => JsonSerializer.Deserialize<TenantId>(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void The_canonical_form_itself_still_parses()
    {
        TenantId.TryParse(CanonicalText.AsSpan(), out var tenantId).Should().BeTrue();
        tenantId.ToString().Should().Be(CanonicalText);
    }

    [Fact]
    public void A_valid_GUID_padded_with_one_megabyte_of_spaces_is_rejected()
    {
        var padded = new string(' ', 1024 * 1024) + CanonicalText;

        TenantId.TryParse(padded.AsSpan(), out var tenantId).Should().BeFalse();
        tenantId.Should().Be(default(TenantId));
    }

    [Theory]
    [InlineData("   " + CanonicalText + "   ")]
    [InlineData("\t\r\n" + CanonicalText + "\t\r\n")]
    public void A_valid_GUID_padded_with_a_few_ASCII_whitespace_characters_is_accepted(string padded)
    {
        TenantId.TryParse(padded.AsSpan(), out var tenantId).Should().BeTrue();
        tenantId.ToString().Should().Be(CanonicalText);
    }

    [Fact]
    public void Input_longer_than_64_characters_fails_before_any_trim_would_run()
    {
        // 65 non-whitespace characters: even if every character were meaningful, the cap
        // runs first, so this can never reach the trim or the shape check.
        var tooLong = new string('a', 65);

        TenantId.TryParse(tooLong.AsSpan(), out var tenantId).Should().BeFalse();
        tenantId.Should().Be(default(TenantId));
    }

    /// <summary>
    /// N32-05(b) (G6 review): pins the exact 64/65-character cap boundary with a valid GUID,
    /// rather than relying only on the 65-character all-'a' input above (which would fail the
    /// shape check regardless of the length cap) or the 1 MiB case (which only proves the cap
    /// runs before the trim, not its exact value).
    /// </summary>
    [Fact]
    public void A_valid_GUID_padded_to_exactly_64_characters_is_accepted_and_65_is_rejected()
    {
        var paddedTo64 = new string(' ', 64 - CanonicalText.Length) + CanonicalText;
        var paddedTo65 = new string(' ', 65 - CanonicalText.Length) + CanonicalText;

        paddedTo64.Length.Should().Be(64);
        paddedTo65.Length.Should().Be(65);

        TenantId.TryParse(paddedTo64.AsSpan(), out var accepted).Should().BeTrue();
        accepted.ToString().Should().Be(CanonicalText);

        TenantId.TryParse(paddedTo65.AsSpan(), out var rejected).Should().BeFalse();
        rejected.Should().Be(default(TenantId));
    }
}

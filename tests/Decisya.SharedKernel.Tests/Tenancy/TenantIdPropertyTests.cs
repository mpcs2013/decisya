using System.Text.Json;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>
/// NFR-14 — TenantId round-trips (New()/From/Parse -&gt; ToString -&gt; Parse) losslessly,
/// with zero mismatches, across &gt;= 10,000 randomized property-based cases. Also covers
/// G3's robustness property (every string either fails to parse or round-trips to its
/// trimmed, lowercase form), extended with the '+' / 'x' / 'X' legacy-compatibility
/// characters per G3 change 3.
/// </summary>
public class TenantIdPropertyTests
{
    private static readonly char[] WhitespaceAlphabet = [' ', '\t', '\r', '\n'];

    private static readonly char[] RobustnessAlphabet =
    [
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        'a', 'b', 'c', 'd', 'e', 'f', 'A', 'B', 'C', 'D', 'E', 'F',
        '-', '{', '}', '+', 'x', 'X',
        ' ', '\t', '\r', '\n', '\f', '\v',
        '\u0000', '\u001b', // control characters
        'é', '漢', // non-ASCII letters
    ];

    [Fact]
    public void TenantId_round_trips_through_Parse_and_JSON_losslessly_across_ten_thousand_random_cases()
    {
        // Fixed seed: failures are reproducible, not flaky.
        var random = new Random(20260926);

        for (var i = 0; i < 10_000; i++)
        {
            var original = random.Next(2) == 0 ? TenantId.New() : RandomTenantId(random);
            var canonical = original.Value.ToString("D");
            var variant = Vary(canonical, random);

            var parsed = TenantId.Parse(variant);

            parsed.Should().Be(original, "case {0}: parsing {1} must round-trip to the original TenantId", i, variant);
            parsed.Value.Should().Be(original.Value, "case {0}", i);
            parsed.ToString().Should().Be(canonical, "case {0}: ToString must be the lowercase canonical form", i);

            var json = JsonSerializer.Serialize(original);
            JsonSerializer.Deserialize<TenantId>(json).Should()
                .Be(original, "case {0}: the JSON round trip must be lossless", i);
        }
    }

    [Fact]
    public void Every_random_string_either_fails_to_parse_or_round_trips_to_its_trimmed_lowercase_form_across_ten_thousand_cases()
    {
        // Fixed seed: failures are reproducible, not flaky. A different seed than the
        // round-trip test above, per the G2 test approach.
        var random = new Random(20260927);

        for (var i = 0; i < 10_000; i++)
        {
            var candidate = RandomCandidate(random);

            var succeeded = TenantId.TryParse(candidate, out var tenantId);

            Exception? thrown = null;
            try
            {
                TenantId.Parse(candidate);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            if (succeeded)
            {
                thrown.Should().BeNull("case {0}: TryParse succeeded for {1} but Parse threw {2}", i, candidate, thrown);
                tenantId.ToString().Should().Be(
                    TrimAsciiWhitespace(candidate).ToLowerInvariant(),
                    "case {0}: a successfully parsed candidate must round-trip to its trimmed, lowercase form", i);
            }
            else
            {
                thrown.Should().BeOfType<TenantIdFormatException>(
                    "case {0}: TryParse failed for {1}, so Parse must throw exactly TenantIdFormatException and nothing else", i, candidate);
            }
        }
    }

    private static TenantId RandomTenantId(Random random)
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid guid;
        do
        {
            random.NextBytes(bytes);
            guid = new Guid(bytes);
        } while (guid == Guid.Empty); // covers every version and variant, proving the leniency.

        return TenantId.From(guid);
    }

    private static string Vary(string canonical, Random random)
    {
        var chars = canonical.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsAsciiLetter(chars[i]) && random.Next(2) == 0)
            {
                chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
            }
        }

        var leading = RandomWhitespace(random, random.Next(4));
        var trailing = RandomWhitespace(random, random.Next(4));

        return leading + new string(chars) + trailing;
    }

    private static string RandomWhitespace(Random random, int count) =>
        new([.. Enumerable.Range(0, count).Select(_ => WhitespaceAlphabet[random.Next(WhitespaceAlphabet.Length)])]);

    private static string RandomCandidate(Random random)
    {
        var length = random.Next(0, 81);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = RobustnessAlphabet[random.Next(RobustnessAlphabet.Length)];
        }

        return new string(chars);
    }

    private static string TrimAsciiWhitespace(string value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsAsciiWhitespace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';
}

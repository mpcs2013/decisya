// Deliberately not "...Tests.Money": a namespace segment literally named "Money" would
// shadow the Decisya.SharedKernel.Money class for every unqualified reference in this
// file (CS0118, "'Money' is a namespace but is used like a type").
namespace Decisya.SharedKernel.Tests;

/// <summary>Story 1 — Currency models ISO 4217 codes and minor-unit exponents.</summary>
public class CurrencyTests
{
    [Fact]
    public void A_known_ISO4217_code_resolves_to_the_correct_exponent()
    {
        var usd = Currency.FromCode("USD");

        usd.Code.Should().Be("USD");
        usd.MinorUnitExponent.Should().Be(2);
    }

    [Fact]
    public void A_zero_exponent_currency_resolves_correctly()
    {
        var jpy = Currency.FromCode("JPY");

        jpy.MinorUnitExponent.Should().Be(0);
    }

    [Fact]
    public void A_three_decimal_currency_resolves_correctly()
    {
        var bhd = Currency.FromCode("BHD");

        bhd.MinorUnitExponent.Should().Be(3);
    }

    [Theory]
    [InlineData("ZZZ")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_malformed_code_is_rejected(string? code)
    {
        var act = () => Currency.FromCode(code!);

        act.Should().Throw<InvalidCurrencyCodeException>();
    }

    [Fact]
    public void TryFromCode_returns_false_for_an_unknown_code_without_throwing()
    {
        var succeeded = Currency.TryFromCode("ZZZ", out _);

        succeeded.Should().BeFalse();
    }

    [Fact]
    public void TryFromCode_returns_true_and_resolves_a_known_code()
    {
        var succeeded = Currency.TryFromCode(" usd ", out var currency);

        succeeded.Should().BeTrue();
        currency.Code.Should().Be("USD");
        currency.MinorUnitExponent.Should().Be(2);
    }

    [Fact]
    public void A_code_containing_a_unicode_look_alike_letter_is_rejected()
    {
        // "uſd": the middle character is LATIN SMALL LETTER LONG S (U+017F), whose
        // simple uppercase mapping is ordinary 'S' — a naive Trim().ToUpperInvariant()
        // would silently resolve this to "USD".
        var act = () => Currency.FromCode("uſd");

        act.Should().Throw<InvalidCurrencyCodeException>();
        Currency.TryFromCode("uſd", out _).Should().BeFalse();
    }

    [Fact]
    public void A_code_padded_with_unicode_em_space_is_rejected()
    {
        // U+2003 EM SPACE is Unicode whitespace but not ASCII whitespace; a naive
        // Trim() (which trims all Unicode whitespace) would silently accept this.
        var act = () => Currency.FromCode(" USD ");

        act.Should().Throw<InvalidCurrencyCodeException>();
        Currency.TryFromCode(" USD ", out _).Should().BeFalse();
    }

    [Fact]
    public void A_code_with_ordinary_ASCII_whitespace_padding_is_still_accepted()
    {
        // Regression guard for the fix above: trimming ASCII whitespace must still work.
        Currency.FromCode("\t USD \n").Code.Should().Be("USD");
    }

    [Fact]
    public void Currency_ToString_returns_its_code()
    {
        Currency.FromCode("GBP").ToString().Should().Be("GBP");
    }

    [Fact]
    public void A_four_decimal_currency_resolves_correctly()
    {
        Currency.FromCode("CLF").MinorUnitExponent.Should().Be(4);
    }

    [Fact]
    public void Currency_equality_is_code_based_value_equality()
    {
        var first = Currency.FromCode("EUR");
        var second = Currency.FromCode("EUR");

        first.Should().Be(second);
        first.Equals(second).Should().BeTrue();
        (first == second).Should().BeTrue();
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Fact]
    public void InvalidCurrencyCodeExceptions_message_strips_CRLF_without_relying_on_truncation()
    {
        // Security review 0.04, N6: this input is well under the 16-character preview
        // limit, so the CR/LF can only disappear because Sanitize actively strips them —
        // never because truncation happened to cut them off (which is what the previous,
        // longer "USD\r\n{...}" input let this assertion pass for the wrong reason).
        var maliciousCode = "US\r\nD";

        var act = () => Currency.FromCode(maliciousCode);

        var exception = act.Should().Throw<InvalidCurrencyCodeException>().Which;
        exception.Code.Should().Be(maliciousCode, "the raw value is still available via the Code property");
        exception.Message.Should().NotContain("\r");
        exception.Message.Should().NotContain("\n");
        exception.Message.Should().Contain(
            "USD", "the surrounding printable characters survive sanitisation - only the CR/LF are stripped");
    }

    [Fact]
    public void InvalidCurrencyCodeExceptions_message_truncates_a_very_long_code()
    {
        var veryLongCode = new string('A', 10_000);

        var act = () => Currency.FromCode(veryLongCode);

        var exception = act.Should().Throw<InvalidCurrencyCodeException>().Which;
        exception.Code.Should().Be(veryLongCode, "the raw value is still available via the Code property");
        exception.Message.Length.Should().BeLessThan(200, "the message must never grow with the input length");
        exception.Message.Should().NotContain(veryLongCode);
    }

    [Fact]
    public void IsInitialized_distinguishes_a_default_currency_from_one_resolved_via_FromCode()
    {
        // Security review 0.04, N5: IsInitialized must be public so module code (not just
        // Decisya.SharedKernel itself) can check default(Currency).Currency.IsInitialized
        // without triggering Code's exception, as the XML doc promises.
        Currency.FromCode("USD").IsInitialized.Should().BeTrue();
        default(Currency).IsInitialized.Should().BeFalse();
    }

    [Fact]
    public void Currency_is_immutable()
    {
        // No public member of Currency has a setter: the only way to obtain one is the
        // validating factory method, and its properties are get-only for the lifetime
        // of the value.
        var properties = typeof(Currency).GetProperties();

        properties.Should().NotBeEmpty();
        properties.Should().OnlyContain(p => p.SetMethod == null);
        typeof(Currency).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Should().BeEmpty();
    }
}

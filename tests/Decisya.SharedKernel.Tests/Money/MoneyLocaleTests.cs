using System.Globalization;

// See MoneyTests.cs for why this is "Decisya.SharedKernel.Tests", not "...Tests.Money".
namespace Decisya.SharedKernel.Tests;

/// <summary>
/// NFR-09 — Money/Currency arithmetic and formatting results are identical regardless
/// of CurrentCulture/CurrentUICulture (e.g. a culture that uses a comma as the decimal
/// separator, or a non-Latin native digit set, must never change a computed result).
/// </summary>
public class MoneyLocaleTests
{
    private static readonly CultureInfo[] Cultures =
    [
        CultureInfo.InvariantCulture,
        new("en-US"),
        new("de-DE"), // comma decimal separator, period thousands separator
        new("fr-FR"), // comma decimal separator, narrow no-break space thousands separator
        new("ar-SA"), // native (non-Latin) digit set
    ];

    [Fact]
    public void Money_formatting_is_identical_across_cultures()
    {
        RunAcrossCultures(() =>
        {
            var money = new Money(1234.56m, Currency.FromCode("USD"));
            return money.ToString();
        }, expected: "1234.56 USD");
    }

    [Fact]
    public void Money_arithmetic_is_identical_across_cultures()
    {
        RunAcrossCultures(() =>
        {
            var usd = Currency.FromCode("USD");
            var sum = new Money(10.10m, usd) + new Money(5.05m, usd);
            return sum.ToString();
        }, expected: "15.15 USD");
    }

    [Fact]
    public void Money_allocation_is_identical_across_cultures()
    {
        RunAcrossCultures(() =>
        {
            var parts = new Money(100.00m, Currency.FromCode("USD")).Allocate(3);
            return string.Join("|", parts.Select(p => p.ToString()));
        }, expected: "33.34 USD|33.33 USD|33.33 USD");
    }

    private static void RunAcrossCultures(Func<string> operation, string expected)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            foreach (var culture in Cultures)
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                operation().Should().Be(expected, "culture {0} must never change a Money result", culture.Name);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}

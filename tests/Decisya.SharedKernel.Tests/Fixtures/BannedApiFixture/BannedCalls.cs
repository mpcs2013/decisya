namespace Decisya.SharedKernel.Tests.Fixtures.BannedApiFixture;

/// <summary>
/// Intentionally non-compiling (see BannedApiFixture.csproj). Each method below calls
/// exactly one member banned by BannedSymbols.txt, on its own line, so a build of this
/// project produces one RS0030 diagnostic per member.
/// </summary>
public static class BannedCalls
{
    public static System.DateTime UseDateTimeNow() => System.DateTime.Now;

    public static System.DateTime UseDateTimeUtcNow() => System.DateTime.UtcNow;

    public static System.DateTimeOffset UseDateTimeOffsetNow() => System.DateTimeOffset.Now;

    public static System.DateTimeOffset UseDateTimeOffsetUtcNow() => System.DateTimeOffset.UtcNow;

    public static double UseConvertToDouble(decimal amount) => System.Convert.ToDouble(amount);

    public static double UseDecimalToDouble(decimal amount) => System.Decimal.ToDouble(amount);
}

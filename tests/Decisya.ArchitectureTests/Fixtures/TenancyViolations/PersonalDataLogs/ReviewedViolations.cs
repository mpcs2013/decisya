#pragma warning disable CA1848, CA2254, CA1873
using Decisya.SharedKernel.Observability;
using Microsoft.Extensions.Logging;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.PersonalDataLogs;

// Issue #230, R6 (G2 D4; G3 G4-230-04): uses of [PersonalDataLogReviewed]. Each logs personal data, so each is a
// finding unless a valid, pinned attribute for its category covers it. Justifications hold no personal data.

/// <summary>A valid attribute; the tests pin it in an in-memory catalogue copy.</summary>
public static class ReviewedFinding
{
    public const string Reason = "Fixture: reviewed, the value is a fixed bucket label";

    [PersonalDataLogReviewed("free-text", Reason)]
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("Value {Value}", tx.Description);
}

/// <summary>A valid attribute that no catalogue entry pins.</summary>
public static class UnpinnedReview
{
    [PersonalDataLogReviewed("free-text", "Fixture: reviewed but never pinned in the catalogue")]
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("Value {Value}", tx.Description);
}

/// <summary>A blank justification.</summary>
public static class BlankReview
{
    [PersonalDataLogReviewed("free-text", "   ")]
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("Value {Value}", tx.Description);
}

/// <summary>A justification under 20 characters.</summary>
public static class ShortReview
{
    [PersonalDataLogReviewed("free-text", "too short")]
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("Value {Value}", tx.Description);
}

/// <summary>An unknown category id.</summary>
public static class UnknownCategoryReview
{
    [PersonalDataLogReviewed("no-such-category", "Fixture: a long enough justification text")]
    public static void Run(ILogger logger, Tx tx) => logger.LogInformation("Value {Value}", tx.Description);
}

/// <summary>A suppression naming <c>secret-material</c>, which can never be suppressed.</summary>
public static class SecretReview
{
    [PersonalDataLogReviewed("secret-material", "Fixture: a long enough justification text")]
    public static void Run(ILogger logger, Person person) => logger.LogInformation("Value {Value}", person.ClientSecret);
}

/// <summary>A suppression for category A must not hide category B.</summary>
public static class CategoryOnlyReview
{
    [PersonalDataLogReviewed("free-text", "Fixture: the description is a fixed bucket label")]
    public static void Run(ILogger logger, Tx tx, Person person)
    {
        logger.LogInformation("Value {Value}", tx.Description);
        logger.LogInformation("Value {Value}", person.Email);
    }
}

/// <summary>Seven attributes on one method are seven pinned entries.</summary>
public static class SevenAttributes
{
    public const string Reason = "Fixture: a long enough justification text";

    [PersonalDataLogReviewed("user-identifier", Reason + " one")]
    [PersonalDataLogReviewed("contact", Reason + " two")]
    [PersonalDataLogReviewed("name", Reason + " three")]
    [PersonalDataLogReviewed("account-number", Reason + " four")]
    [PersonalDataLogReviewed("free-text", Reason + " five")]
    [PersonalDataLogReviewed("ai-text", Reason + " six")]
    [PersonalDataLogReviewed("free-text", Reason + " seven")]
    public static void Run(ILogger logger, Person person, Tx tx)
    {
        logger.LogInformation("Value {Value}", person.UserId);
        logger.LogInformation("Value {Value}", person.Email);
        logger.LogInformation("Value {Value}", person.DisplayName);
        logger.LogInformation("Value {Value}", person.Iban);
        logger.LogInformation("Value {Value}", tx.Description);
        logger.LogInformation("Value {Value}", person.Prompt);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Decisya.Modules.Ledger.Contracts;
using Decisya.Modules.Ledger.Endpoints;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// G6-341-06 (a), G3 M341-05 (a), ADR-0027 point 3: the request-body serializer options hold the values the
/// design binds. A copy of the admin options (no <see cref="JsonNumberHandling.Strict"/>) or a loosened value
/// fails here before it reaches the HTTP cases. No database, no host.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LedgerJsonOptionsTests
{
    private static JsonSerializerOptions Options => LedgerJson.Options;

    [Fact]
    public void Numbers_are_read_strictly_so_a_string_amount_is_no_amount()
    {
        Options.NumberHandling.Should().Be(JsonNumberHandling.Strict);
    }

    [Fact]
    public void An_unmapped_member_is_an_error()
    {
        Options.UnmappedMemberHandling.Should().Be(JsonUnmappedMemberHandling.Disallow);
    }

    [Fact]
    public void A_duplicate_property_is_refused()
    {
        Options.AllowDuplicateProperties.Should().BeFalse();
    }

    [Fact]
    public void The_nesting_depth_is_four()
    {
        Options.MaxDepth.Should().Be(4);
    }

    [Fact]
    public void A_required_constructor_parameter_and_a_non_nullable_annotation_are_respected()
    {
        Options.RespectRequiredConstructorParameters.Should().BeTrue();
        Options.RespectNullableAnnotations.Should().BeTrue();
    }

    [Fact]
    public void The_naming_and_case_rules_are_the_web_defaults()
    {
        Options.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
        Options.PropertyNameCaseInsensitive.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"name":"A","currency":"EUR","openingBalance":{"amountMinor":"125000","currency":"EUR"},"openingDate":"2026-12-01"}""")]
    [InlineData("""{"name":"A","currency":"EUR","openingBalance":{"amountMinor":1e3,"currency":"EUR"},"openingDate":"2026-12-01"}""")]
    [InlineData("""{"name":"A","currency":"EUR","openingBalance":{"amountMinor":125000.0,"currency":"EUR"},"openingDate":"2026-12-01"}""")]
    [InlineData("""{"name":"A","currency":"EUR","iban":"x"}""")]
    [InlineData("""{"name":"A","currency":"EUR","name":"B"}""")]
    [InlineData("""{"name":"A","currency":"EUR","openingBalance":{"amountMinor":1,"currency":"EUR","x":{"y":1}}}""")]
    public void The_options_refuse_each_of_these_bodies_when_they_deserialise_a_create_request(string json)
    {
        var act = () => JsonSerializer.Deserialize<CreateAccountRequest>(json, Options);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_well_formed_body_deserialises()
    {
        var request = JsonSerializer.Deserialize<CreateAccountRequest>(
            """{"name":"Everyday","currency":"EUR","openingBalance":{"amountMinor":125000,"currency":"EUR"},"openingDate":"2026-12-01"}""",
            Options);

        request.Should().NotBeNull();
        request!.OpeningBalance!.AmountMinor.Should().Be(125000);
    }
}

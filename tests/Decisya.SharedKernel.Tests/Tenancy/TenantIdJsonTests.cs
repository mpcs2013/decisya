using System.Text.Json;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.SharedKernel.Tests.Tenancy;

/// <summary>Story 2 — TenantId round-trips through JSON as a plain string.</summary>
public class TenantIdJsonTests
{
    private const string CanonicalText = "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70";

    private sealed class PlainDto
    {
        public TenantId Tenant { get; set; }
    }

    private sealed class RequiredDto
    {
        public required TenantId Tenant { get; set; }
    }

    private sealed class NullableDto
    {
        public TenantId? Tenant { get; set; }
    }

    [Fact]
    public void Serializing_a_TenantId_produces_a_plain_JSON_string()
    {
        var tenantId = TenantId.Parse(CanonicalText);

        var json = JsonSerializer.Serialize(tenantId);

        json.Should().Be($"\"{CanonicalText}\"");
        JsonDocument.Parse(json).RootElement.ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void Deserializing_a_valid_string_round_trips_to_the_same_TenantId()
    {
        var deserialized = JsonSerializer.Deserialize<TenantId>($"\"{CanonicalText}\"");

        deserialized.Should().Be(TenantId.Parse(CanonicalText));
    }

    [Fact]
    public void Deserializing_a_malformed_string_throws_and_produces_no_instance()
    {
        var act = () => JsonSerializer.Deserialize<TenantId>("\"not-a-guid\"");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Deserializing_JSON_null_for_a_non_nullable_member_throws()
    {
        var act = () => JsonSerializer.Deserialize<PlainDto>("""{"Tenant":null}""");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Deserializing_JSON_null_for_a_nullable_TenantId_member_yields_null()
    {
        var dto = JsonSerializer.Deserialize<NullableDto>("""{"Tenant":null}""");

        dto!.Tenant.Should().BeNull();
    }

    [Fact]
    public void The_round_trip_needs_no_caller_registered_converter()
    {
        var dto = new PlainDto { Tenant = TenantId.Parse(CanonicalText) };

        var json = JsonSerializer.Serialize(dto);
        var roundTripped = JsonSerializer.Deserialize<PlainDto>(json);

        roundTripped!.Tenant.Should().Be(dto.Tenant);
    }

    [Fact]
    public void A_non_string_token_throws()
    {
        var act = () => JsonSerializer.Deserialize<TenantId>("42");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void An_object_token_throws()
    {
        var act = () => JsonSerializer.Deserialize<TenantId>("""{"value":1}""");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_raw_token_over_128_bytes_is_rejected_and_the_message_carries_no_canary()
    {
        var canary = Canaries.Unique("oversized-token");
        var oversized = canary + new string('a', 1024 * 1024);
        var json = JsonSerializer.Serialize(oversized);

        var act = () => JsonSerializer.Deserialize<TenantId>(json);

        var exception = act.Should().Throw<JsonException>().Which;
        exception.Message.Should().NotContain(canary);
    }

    [Fact]
    public void A_malformed_value_never_appears_in_the_exception_message_or_ToString()
    {
        var canary = Canaries.Unique("malformed");
        var json = JsonSerializer.Serialize(canary);

        var act = () => JsonSerializer.Deserialize<TenantId>(json);

        var exception = act.Should().Throw<JsonException>().Which;
        exception.Message.Should().NotContain(canary);
        exception.ToString().Should().NotContain(canary);
    }

    [Fact]
    public void A_DTO_with_a_required_TenantId_member_throws_when_the_property_is_missing()
    {
        var act = () => JsonSerializer.Deserialize<RequiredDto>("{}");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_DTO_with_a_non_required_TenantId_member_defaults_when_the_property_is_missing()
    {
        var dto = JsonSerializer.Deserialize<PlainDto>("{}");

        dto!.Tenant.Should().Be(default(TenantId));
        dto.Tenant.IsInitialized.Should().BeFalse();
    }

    // --- G4-32-03 (fifth entry point): the JSON converter also rejects Guid.Empty ---

    [Fact]
    public void The_all_zero_guid_text_is_rejected_by_the_converter()
    {
        var act = () => JsonSerializer.Deserialize<TenantId>("\"00000000-0000-0000-0000-000000000000\"");

        act.Should().Throw<JsonException>();
    }

    // --- G4-32-04: serializing the uninitialized default fails, including as a DTO member ---

    [Fact]
    public void Serializing_the_default_TenantId_throws()
    {
        var act = () => JsonSerializer.Serialize(default(TenantId));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Serializing_a_DTO_with_an_uninitialized_TenantId_member_throws()
    {
        var dto = new PlainDto();

        var act = () => JsonSerializer.Serialize(dto);

        act.Should().Throw<JsonException>();
    }
}

using System.Text.Json;
using Decisya.Modules.Tenancy.Contracts;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.Modules.Tenancy.Tests.TestSupport;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Decisya.Modules.Tenancy.Tests;

/// <summary>
/// Issue #230, G1 Story 3 (G2 D5): the stored raw <c>sub</c> of a member is <c>[Sensitive]</c> and masked when the
/// owning object is logged whole through the real masking processor and the <c>decisya-json</c> formatter, and the
/// attribute changes neither the API JSON nor the EF model. The canary is synthetic. The same test names the
/// Gherkin scenarios of Story 3.
/// </summary>
[Trait("Category", "Unit")]
public class TenancyUserIdMaskingTests
{
    private const string Canary = "sub-canary-7f3a";

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static Membership NewMembership() =>
        new(TenantId.Parse(Guid.NewGuid().ToString()), Canary, TenantRole.Owner, Instant.FromUtc(2026, 1, 1, 0, 0));

    [Fact]
    public void The_attributes_are_present_on_Membership_UserId_and_MemberDto_UserId()
    {
        Attribute.IsDefined(typeof(Membership).GetProperty(nameof(Membership.UserId))!, typeof(SensitiveAttribute)).Should().BeTrue();
        Attribute.IsDefined(typeof(MemberDto).GetProperty(nameof(MemberDto.UserId))!, typeof(SensitiveAttribute)).Should().BeTrue();
    }

    [Fact]
    public void A_Membership_value_is_logged_as_structured_state_and_no_emitted_line_contains_the_canary()
    {
        var membership = NewMembership();

        var line = MaskedLogLine.Write("Member", membership);

        line.Should().NotContain(Canary);
        line.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void A_Membership_passed_whole_to_the_masking_processor_renders_the_user_id_member_as_the_mask()
    {
        var rendered = new SensitiveDataMaskingProcessor().ProcessValue("Member", NewMembership(), out var masked);

        masked.Should().BeTrue();
        var text = rendered as string ?? JsonSerializer.Serialize(rendered);
        text.Should().NotContain(Canary);
        text.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void A_MemberDto_value_is_logged_and_no_emitted_line_contains_the_canary()
    {
        var line = MaskedLogLine.Write("Member", new MemberDto(Canary, "Owner"));

        line.Should().NotContain(Canary);
        line.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void A_MemberDto_list_is_logged_and_no_emitted_line_contains_the_canary()
    {
        var members = new List<MemberDto> { new(Canary, "Owner"), new(Canary + "-2", "Member") };

        var line = MaskedLogLine.Write("Members", members);

        line.Should().NotContain(Canary);
        line.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void ToString_and_interpolation_of_a_MemberDto_and_a_Membership_do_not_leak_the_canary()
    {
        var dto = new MemberDto(Canary, "Owner");
        var membership = NewMembership();

        dto.ToString().Should().NotContain(Canary).And.Contain("Owner");
        $"Failed for {dto}".Should().NotContain(Canary);
        membership.ToString().Should().NotContain(Canary);
        $"Failed for {membership}".Should().NotContain(Canary);
    }

    [Fact]
    public void The_PrintMembers_override_leaves_the_MemberDto_value_and_the_JSON_unchanged()
    {
        var dto = new MemberDto(Canary, "Owner");

        // Story 3, "API output is unchanged": [Sensitive] and the ToString override do not touch System.Text.Json.
        var json = JsonSerializer.Serialize(dto, WebOptions);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("userId").GetString().Should().Be(Canary);
        document.RootElement.GetProperty("role").GetString().Should().Be("Owner");
        dto.UserId.Should().Be(Canary);
        dto.Should().Be(new MemberDto(Canary, "Owner"));
    }

    [Fact]
    public void The_Tenancy_model_has_no_pending_changes_so_no_migration_is_needed()
    {
        using var db = new TenancyDesignTimeDbContextFactory().CreateDbContext([]);

        db.Database.HasPendingModelChanges().Should().BeFalse("[Sensitive] must not change the EF model or the snapshot (issue #230, G2 D5)");
    }
}

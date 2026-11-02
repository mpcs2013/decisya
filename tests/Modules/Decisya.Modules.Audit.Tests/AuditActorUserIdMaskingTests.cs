using System.Text.Json;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Audit.Domain;
using Decisya.Modules.Audit.Tests.TestSupport;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Audit.Tests;

/// <summary>
/// Issue #230, G1 Story 3 (Marco's option A): <c>AuditRecord.ActorUserId</c> holds the raw <c>sub</c> of the admin
/// and is masked when the record is logged whole through the real masking processor and the <c>decisya-json</c>
/// formatter. The EF model is unchanged (the existing no-pending-changes test in
/// <c>AuditModelAndRegistrationTests</c> covers it). The canary is synthetic.
/// </summary>
[Trait("Category", "Unit")]
public class AuditActorUserIdMaskingTests
{
    private const string Canary = "sub-canary-7f3a";

    private static AuditRecord NewRecord() =>
        AuditRecord.Create(
            TenantId.Parse(Guid.NewGuid().ToString()),
            AuditAction.EntitlementsTrialStart,
            featureKey: null,
            Canary,
            Instant.FromUtc(2026, 1, 1, 0, 0),
            traceId: null);

    [Fact]
    public void ActorUserId_carries_Sensitive()
    {
        Attribute.IsDefined(typeof(AuditRecord).GetProperty(nameof(AuditRecord.ActorUserId))!, typeof(SensitiveAttribute)).Should().BeTrue();
    }

    [Fact]
    public void An_AuditRecord_is_logged_as_structured_state_and_no_emitted_line_contains_the_canary()
    {
        var line = MaskedLogLine.Write("Record", NewRecord());

        line.Should().NotContain(Canary);
        line.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }

    [Fact]
    public void An_AuditRecord_passed_whole_to_the_masking_processor_renders_the_actor_user_id_member_as_the_mask()
    {
        var rendered = new SensitiveDataMaskingProcessor().ProcessValue("Record", NewRecord(), out var masked);

        masked.Should().BeTrue();
        var text = rendered as string ?? JsonSerializer.Serialize(rendered);
        text.Should().NotContain(Canary);
        text.Should().Contain(SensitiveDataMaskingProcessor.Mask);
    }
}

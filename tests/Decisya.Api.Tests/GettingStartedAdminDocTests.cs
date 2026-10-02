using System.Text.RegularExpressions;

namespace Decisya.Api.Tests;

/// <summary>
/// G3 S-1 (T-15), issue #25: the GETTING-STARTED path for the hand-added <c>realm-roles-access-token</c> mapper
/// uses <c>kcadm.sh</c>'s interactive password prompt, never a password or token on a command line, names the
/// target, states what the volume reset deletes, and gives the dev-admin / dev-alice smoke check.
/// </summary>
[Trait("Category", "Unit")]
public class GettingStartedAdminDocTests
{
    private static readonly string Doc = File.ReadAllText(RepoPaths.Find(Path.Combine("docs", "GETTING-STARTED.md")));

    [Fact]
    public void No_kcadm_or_curl_command_in_the_guide_carries_a_password_or_a_token_on_the_command_line()
    {
        var commandSpans = Regex.Matches(Doc, "`([^`]*(?:kcadm|curl)[^`]*)`").Select(m => m.Groups[1].Value).ToList();

        commandSpans.Should().NotBeEmpty();
        foreach (var span in commandSpans)
        {
            span.Should().NotContain("--password").And.NotContain("--token").And.NotContain("-d ").And.NotContain("Authorization:", span);
        }

        Doc.Should().Contain("kcadm prompts for it");
    }

    [Fact]
    public void The_guide_names_the_mapper_its_target_the_reset_cost_and_the_smoke_check()
    {
        Doc.Should().Contain("realm-roles-access-token");
        Doc.Should().Contain("decisya-bff-dedicated");
        Doc.Should().Contain("This deletes all local data: Keycloak (realm, users, sessions), tenancy, entitlements and audit");
        Doc.Should().Contain("204 for `dev-admin`, 403 for `dev-alice`");
        Doc.Should().Contain("POST /api/admin/tenants/{tenantId}/trial");
    }

    [Fact]
    public void The_guide_gives_the_Visual_Studio_2026_path_and_the_CLI_path_side_by_side_for_the_mapper_steps()
    {
        Doc.Should().Contain("| Step | VS 2026 (admin console in Firefox) | CLI (`kcadm.sh` in the container) |");
        Doc.Should().Contain("| Step | VS 2026 | CLI |");
    }
}

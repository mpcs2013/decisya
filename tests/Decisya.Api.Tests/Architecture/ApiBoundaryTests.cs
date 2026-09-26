using NetArchTest.Rules;

namespace Decisya.Api.Tests.Architecture;

/// <summary>
/// Hosts get telemetry only through <c>AddServiceDefaults</c>; only
/// <c>Decisya.ServiceDefaults</c> ever references OpenTelemetry directly (#22 adds
/// <c>Decisya.Modules.*</c> to this rule).
/// </summary>
public class ApiBoundaryTests
{
    [Fact]
    public void Decisya_Api_does_not_depend_on_OpenTelemetry_directly()
    {
        var result = Types.InAssembly(typeof(Program).Assembly)
            .ShouldNot()
            .HaveDependencyOn("OpenTelemetry")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}

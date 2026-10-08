using System.Globalization;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.RateLimiting;

/// <summary>One route class's limit: permits per sliding window (G2 D5).</summary>
internal sealed class ClassLimit(int permitLimit, int windowSeconds, int anonymousPermitLimit = 0)
{
    internal int PermitLimit { get; set; } = permitLimit;

    internal int WindowSeconds { get; set; } = windowSeconds;

    /// <summary>The <c>api</c> class only: the limit for a caller counted by address (no session).</summary>
    internal int AnonymousPermitLimit { get; set; } = anonymousPermitLimit;
}

/// <summary>
/// <c>Bff:RateLimits:&lt;class&gt;:*</c> (#122, G2 D5). Bound by hand because the class key
/// <c>backchannel_logout</c> does not match a property name, and because an unknown child must
/// fail start-up instead of silently leaving the defaults in place. The validator
/// (<see cref="BffRateLimitOptionsValidator"/>) runs in every environment. Failure messages name the
/// key and never a value. None of these values is a secret.
/// </summary>
internal sealed class BffRateLimitOptions
{
    internal const string SectionName = "Bff:RateLimits";
    internal const int MaxPermitLimit = 100_000;
    internal const int MaxWindowSeconds = 3_600;

    internal ClassLimit Login { get; } = new(10, 60);

    internal ClassLimit BackchannelLogout { get; } = new(300, 60);

    internal ClassLimit Api { get; } = new(300, 60, anonymousPermitLimit: 60);

    internal ClassLimit Admin { get; } = new(30, 60);

    /// <summary>Binder problems (unknown key, not a whole number), each naming the configuration key.</summary>
    internal List<string> LoadProblems { get; } = [];

    internal ClassLimit For(RouteClass routeClass) => routeClass switch
    {
        RouteClass.Login => Login,
        RouteClass.BackchannelLogout => BackchannelLogout,
        RouteClass.Api => Api,
        RouteClass.Admin => Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(routeClass)),
    };

    /// <summary>The number of segments each class's sliding window is divided into (fixed in code, G2 D4).</summary>
    internal static int SegmentsFor(RouteClass routeClass) =>
        routeClass is RouteClass.Login or RouteClass.BackchannelLogout ? 4 : 6;

    internal void Load(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        foreach (var classSection in section.GetChildren())
        {
            var classPath = SectionName + ":" + classSection.Key;
            var limit = ClassOf(classSection.Key);
            if (limit is null)
            {
                LoadProblems.Add(classPath + " is not a known route class (login, backchannel_logout, api, admin).");
                continue;
            }

            var settings = classSection.GetChildren().ToList();
            if (settings.Count == 0)
            {
                LoadProblems.Add(classPath + " must contain PermitLimit and WindowSeconds settings.");
                continue;
            }

            foreach (var setting in settings)
            {
                var path = classPath + ":" + setting.Key;
                if (string.Equals(setting.Key, "PermitLimit", StringComparison.OrdinalIgnoreCase))
                {
                    Assign(setting, path, value => limit.PermitLimit = value);
                }
                else if (string.Equals(setting.Key, "WindowSeconds", StringComparison.OrdinalIgnoreCase))
                {
                    Assign(setting, path, value => limit.WindowSeconds = value);
                }
                else if (ReferenceEquals(limit, Api)
                    && string.Equals(setting.Key, "AnonymousPermitLimit", StringComparison.OrdinalIgnoreCase))
                {
                    Assign(setting, path, value => limit.AnonymousPermitLimit = value);
                }
                else
                {
                    LoadProblems.Add(path + " is not a known setting.");
                }
            }
        }
    }

    private ClassLimit? ClassOf(string key)
    {
        if (string.Equals(key, RateLimitNames.Login, StringComparison.OrdinalIgnoreCase))
        {
            return Login;
        }

        if (string.Equals(key, RateLimitNames.BackchannelLogout, StringComparison.OrdinalIgnoreCase))
        {
            return BackchannelLogout;
        }

        if (string.Equals(key, RateLimitNames.Api, StringComparison.OrdinalIgnoreCase))
        {
            return Api;
        }

        return string.Equals(key, RateLimitNames.Admin, StringComparison.OrdinalIgnoreCase) ? Admin : null;
    }

    private void Assign(IConfigurationSection setting, string path, Action<int> assign)
    {
        if (setting.GetChildren().Any()
            || !int.TryParse(setting.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            LoadProblems.Add(path + " must be a whole number.");
            return;
        }

        assign(value);
    }
}

/// <summary>Fails start-up, in every environment, on a bad limit (G1 Story 6): the limiter cannot be switched off by a typo.</summary>
internal sealed class BffRateLimitOptionsValidator : IValidateOptions<BffRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, BffRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>(options.LoadProblems);

        foreach (var routeClass in Enum.GetValues<RouteClass>())
        {
            var path = BffRateLimitOptions.SectionName + ":" + RateLimitNames.Of(routeClass);
            var limit = options.For(routeClass);
            CheckPermits(failures, path + ":PermitLimit", limit.PermitLimit);
            if (limit.WindowSeconds is < 1 or > BffRateLimitOptions.MaxWindowSeconds)
            {
                failures.Add(path + ":WindowSeconds must be a whole number from 1 to " + BffRateLimitOptions.MaxWindowSeconds.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        CheckPermits(failures, BffRateLimitOptions.SectionName + ":api:AnonymousPermitLimit", options.Api.AnonymousPermitLimit);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckPermits(List<string> failures, string path, int value)
    {
        if (value is < 1 or > BffRateLimitOptions.MaxPermitLimit)
        {
            failures.Add(path + " must be a whole number from 1 to " + BffRateLimitOptions.MaxPermitLimit.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}

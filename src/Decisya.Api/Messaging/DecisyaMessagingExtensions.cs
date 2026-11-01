using Decisya.Api.Authentication;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Decisya.Api.Messaging;

/// <summary>
/// The single entry point <c>Program</c> calls for messaging (issue #231, ADR-0024; closes the
/// serializer, discovery and outbox-entity parts of register item C-13). Wolverine runs here as an
/// in-process mediator and nothing else: no message store, no transport, no listener, no durable
/// queue. The issue that first needs a message to survive a crash enables durable storage under its
/// own review (backlog #243 item 231-1); until then a startup test refuses durable mode.
/// </summary>
/// <remarks>
/// <para>
/// Call it after <c>AddDecisyaApiAuthentication</c>: the receive middleware populates the scoped
/// <see cref="RequestCaller"/> that call registers. Call it once. <c>Program.cs</c> never calls
/// <c>RunJasperFxCommands</c> (its <c>resources teardown</c> is dangerous functionality, G3 T-19).
/// </para>
/// <para>
/// Code generation is Static: WolverineFx 6.39.1 ships no runtime compiler, so Roslyn never runs in
/// this process, and the host starts only because no handler exists yet. The first module that lists a
/// handler needs the code-generation step (pre-generated handler code, or the separate
/// <c>WolverineFx.RuntimeCompilation</c> package in development), decided in that issue with Marco.
/// </para>
/// <para>
/// The tenant crosses the bus on the envelope, never in a message member: see
/// <see cref="EnvelopeTenantStamp"/> (send), <see cref="EnvelopeTenantMiddleware"/> (receive) and
/// <see cref="EnvelopeFacts"/>. Message records live in <c>Modules.&lt;Name&gt;.Contracts</c>
/// (ADR-0005); a <see cref="TenantId"/> member of one is a constructor parameter or
/// <see langword="required"/>, and a handler scopes its work by <see cref="ICurrentTenant"/> only.
/// </para>
/// </remarks>
public static class DecisyaMessagingExtensions
{
    private const string WolverineLogCategory = "Wolverine";
    private const string JasperFxLogCategory = "JasperFx";

    /// <summary>
    /// Adds Wolverine as an in-process mediator with the envelope-tenant pipeline.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="moduleHandlerTypes">
    /// The handler lists each module names explicitly (ADR-0024 point 5). Conventional discovery is
    /// off, so a handler that is not listed here never receives a message. A type that carries
    /// <see cref="AllowCrossTenantAttribute"/> is refused: the HTTP admin role and MFA policy do not
    /// apply to a message, so no cross-tenant handler is on a message path in #231.
    /// </param>
    public static WebApplicationBuilder AddDecisyaMessaging(
        this WebApplicationBuilder builder,
        params IReadOnlyList<Type>[] moduleHandlerTypes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(moduleHandlerTypes);

        var handlerTypes = CollectHandlerTypes(moduleHandlerTypes);

        // ManualOnly: an IWolverineExtension that a referenced assembly carries ([WolverineModule])
        // is never applied on its own (G3 T-12). The composed runtime is still tested.
        builder.Services.AddWolverine(ExtensionDiscovery.ManualOnly, options => Configure(options, handlerTypes));

        MessageCallerBindings.DecorateScopedBus(builder.Services);
        FloorWolverineLogging(builder.Services);

        return builder;
    }

    private static List<Type> CollectHandlerTypes(IReadOnlyList<Type>[] moduleHandlerTypes)
    {
        var handlerTypes = new List<Type>();
        foreach (var list in moduleHandlerTypes)
        {
            ArgumentNullException.ThrowIfNull(list);

            foreach (var type in list)
            {
                ArgumentNullException.ThrowIfNull(type);

                if (type.IsDefined(typeof(AllowCrossTenantAttribute), inherit: false))
                {
                    throw new ArgumentException(
                        "A type that carries [AllowCrossTenant] is never a message handler in this host.",
                        nameof(moduleHandlerTypes));
                }

                if (!handlerTypes.Contains(type))
                {
                    handlerTypes.Add(type);
                }
            }
        }

        return handlerTypes;
    }

    private static void Configure(WolverineOptions options, List<Type> handlerTypes)
    {
        // In-process mediator only: no node persistence, inbox or outbox (ADR-0024 point 2).
        options.Durability.Mode = DurabilityMode.MediatorOnly;
        options.AutoBuildMessageStorageOnStartup = JasperFx.AutoCreate.None;
        options.DisableAllExternalListeners = true;
        options.EnableRelayOfUserName = false;

        // Wolverine 6 no longer ships its runtime compiler: with WolverineFx alone, Dynamic mode
        // cannot start. Static mode loads pre-generated handler code, so no Roslyn runs in the Api
        // process (G3 T-19). There is no handler yet; the first one comes with the code-generation
        // step (backlog #243: dynamic in development, static for release images).
        options.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Static;

        // Explicit handlers only (ADR-0024 point 5).
        options.Discovery.DisableConventionalDiscovery();
        options.Discovery.IncludeHandlerModules = false;
        foreach (var type in handlerTypes)
        {
            options.Discovery.IncludeType(type);
        }

        // System.Text.Json is the one serializer. A missing TenantId member fails to deserialize
        // (a `required` member or a constructor parameter) instead of becoming default(TenantId),
        // and TenantIdJsonConverter, applied to the type itself, rejects an all-zero or malformed one.
        options.UseSystemTextJsonForSerialization(json =>
        {
            json.RespectRequiredConstructorParameters = true;
            json.RespectNullableAnnotations = true;
        });

        // The scoped RequestCaller sits behind lambda registrations; the generated code may locate
        // exactly these two services and no others.
        options.CodeGeneration.AlwaysUseServiceLocationFor<ICurrentTenant>();
        options.CodeGeneration.AlwaysUseServiceLocationFor<ICurrentCaller>();

        // Send: the scope's IMessageBus is EnvelopeTenantStamp (see MessageCallerBindings). Receive:
        // one middleware, first in every chain.
        options.Policies.AddMiddleware<EnvelopeTenantMiddleware>();
    }

    /// <summary>
    /// Wolverine's Information and Debug logs describe envelopes. The floor is Warning for the
    /// <c>Wolverine</c> and <c>JasperFx</c> categories, for every provider, and it is applied after
    /// every other configuration, so a <c>Logging:LogLevel</c> setting cannot lower it (G3 S-05, T-14).
    /// </summary>
    private static void FloorWolverineLogging(IServiceCollection services) =>
        services.AddSingleton<IPostConfigureOptions<LoggerFilterOptions>, WolverineLogFloor>();

    private sealed class WolverineLogFloor : IPostConfigureOptions<LoggerFilterOptions>
    {
        private static readonly string?[] Providers = [null, "Console", "OpenTelemetry"];
        private static readonly string[] Categories = [WolverineLogCategory, JasperFxLogCategory];

        public void PostConfigure(string? name, LoggerFilterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            // Raise any rule that names a Wolverine or JasperFx category below Warning (a more
            // specific category such as Wolverine.Runtime would otherwise win over the floor).
            for (var i = 0; i < options.Rules.Count; i++)
            {
                var rule = options.Rules[i];
                if (rule.CategoryName is not null
                    && Categories.Any(category => rule.CategoryName.StartsWith(category, StringComparison.Ordinal))
                    && (rule.LogLevel is null || rule.LogLevel < LogLevel.Warning))
                {
                    options.Rules[i] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, LogLevel.Warning, null);
                }
            }

            foreach (var category in Categories)
            {
                foreach (var provider in Providers)
                {
                    options.Rules.Add(new LoggerFilterRule(provider, category, LogLevel.Warning, null));
                }
            }
        }
    }
}

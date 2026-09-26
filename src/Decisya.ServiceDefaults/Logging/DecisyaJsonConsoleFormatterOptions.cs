using Microsoft.Extensions.Logging.Console;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Options for <see cref="DecisyaJsonConsoleFormatter"/>.
/// </summary>
/// <remarks>
/// It deliberately adds no member of its own. There is no switch that turns masking off,
/// selects a different JSON encoder, or changes the field set: a configuration file must
/// not be able to weaken the stdout sink. The inherited
/// <see cref="ConsoleFormatterOptions"/> members are not read by the formatter either —
/// the timestamp comes from NodaTime's <c>IClock</c> and scopes are always included.
/// </remarks>
public sealed class DecisyaJsonConsoleFormatterOptions : ConsoleFormatterOptions;

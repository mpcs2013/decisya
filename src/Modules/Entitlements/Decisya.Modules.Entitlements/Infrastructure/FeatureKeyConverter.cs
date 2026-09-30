using Decisya.Modules.Entitlements.Contracts;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Decisya.Modules.Entitlements.Infrastructure;

/// <summary>Stores a <see cref="FeatureKey"/> as its text (<c>varchar(64)</c>); reads go back through <see cref="FeatureKey.Create"/>.</summary>
internal sealed class FeatureKeyConverter : ValueConverter<FeatureKey, string>
{
    public FeatureKeyConverter()
        : base(key => key.Value, text => FeatureKey.Create(text))
    {
    }
}

using Decisya.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Decisya.Modules.Ledger.Infrastructure;

/// <summary>Stores a <see cref="Currency"/> as its three-letter code (<c>char(3)</c>); reads go back through <see cref="Currency.FromCode"/>.</summary>
internal sealed class CurrencyConverter : ValueConverter<Currency, string>
{
    public CurrencyConverter()
        : base(currency => currency.Code, code => Currency.FromCode(code))
    {
    }
}

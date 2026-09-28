using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Decisya.Infrastructure.Persistence;

/// <summary>
/// Converts <see cref="TenantId"/> to and from the <c>uuid</c> column type. Registered for
/// every <see cref="TenantId"/>-typed property by <see cref="TenantDbContext.ConfigureConventions"/>.
/// A stored <see cref="Guid.Empty"/> throws on read (<see cref="TenantId.From(Guid)"/>'s own
/// fail-loud behavior): a derived context must never see an untenanted row silently become
/// <c>default(TenantId)</c>.
/// </summary>
internal sealed class TenantIdValueConverter() : ValueConverter<TenantId, Guid>(
    id => id.Value,
    value => TenantId.From(value));

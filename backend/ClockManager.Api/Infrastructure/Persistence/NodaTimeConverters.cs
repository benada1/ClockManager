using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace ClockManager.Api.Infrastructure.Persistence;

/// <summary>
/// NodaTime ↔ SQL Server mappings (plans/02-schema.md §4). Our own three converters instead of a package: the domain keeps
/// NodaTime types (D-007) and the database keeps plain SQL types that any tool can read.
/// </summary>
internal static class NodaTimeConverters
{
    /// <summary>
    /// <see cref="Instant"/> ↔ <c>datetimeoffset(7)</c> at offset +00:00. SQL Server keeps 100 ns ticks; NodaTime has
    /// nanoseconds, so anything below 100 ns is truncated (no provider sends that much precision).
    /// </summary>
    public sealed class InstantConverter() : ValueConverter<Instant, DateTimeOffset>(
        instant => instant.ToDateTimeOffset(),
        value => Instant.FromDateTimeOffset(value));

    /// <summary><see cref="Offset"/> ↔ <c>smallint</c> minutes (Zurich: 60 or 120). Seconds offsets don't occur in tzdb's modern rules.</summary>
    public sealed class OffsetMinutesConverter() : ValueConverter<Offset, short>(
        offset => (short)(offset.Seconds / 60),
        minutes => Offset.FromSeconds(minutes * 60));

    /// <summary><see cref="LocalDate"/> ↔ <c>date</c> (via <see cref="DateOnly"/>, supported natively by EF Core 8 on SQL Server).</summary>
    public sealed class LocalDateConverter() : ValueConverter<LocalDate, DateOnly>(
        date => date.ToDateOnly(),
        value => LocalDate.FromDateOnly(value));
}

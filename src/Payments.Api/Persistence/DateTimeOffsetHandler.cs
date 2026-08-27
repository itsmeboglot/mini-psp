using System.Data;
using Dapper;

namespace Payments.Api.Persistence;

/// <summary>
/// Bridges PostgreSQL's timestamptz and <see cref="DateTimeOffset"/>.
/// </summary>
/// <remarks>
/// Npgsql reads timestamptz as a UTC <see cref="DateTime"/>, because the type
/// stores an instant and carries no offset of its own. The domain speaks
/// <see cref="DateTimeOffset"/>, so without this handler Dapper cannot find a
/// constructor to materialise into. The conversion is lossless in both
/// directions: an instant read back is the same instant, expressed at UTC.
/// </remarks>
public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)),
        _ => throw new DataException($"Cannot read {value?.GetType().Name ?? "null"} as a DateTimeOffset.")
    };

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value.UtcDateTime;
}

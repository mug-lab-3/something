using System.Globalization;
using System.Numerics;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Something.Storage;

internal sealed class DuckModelInsert<T> : IDisposable
    where T : class
{
    private readonly DuckScanDbContext context;
    private readonly DuckDBAppender appender;
    private readonly IProperty?[] properties;
    private readonly (string Column, int Value)? version;
    private readonly string table;
    private readonly Func<T, object?>[] values;
    private static readonly BigInteger SignedMaximum = (BigInteger.One << 127) - 1;
    private readonly List<(T Row, IProperty Property, BigInteger Value)> large = [];
    private bool disposed;

    /// <param name="version">Writes a fact-model row into a shared state table: the integer
    /// version column replaces the entity's generation UUID (<c>ScanId</c>).</param>
    internal DuckModelInsert(
        DuckScanDbContext context,
        string? targetTable = null,
        (string Column, int Value)? version = null
    )
    {
        this.context = context;
        this.version = version;
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Appender requires a publication transaction.");
        var entity = context.Model.FindEntityType(typeof(T))!;
        table = targetTable ?? entity.GetTableName()!;
        using var columns = context.Database.GetDbConnection().CreateCommand();
        columns.Transaction = context.Database.CurrentTransaction.GetDbTransaction();
        columns.CommandText =
            "SELECT column_name,data_type FROM information_schema.columns WHERE table_schema='main' AND table_name=$table ORDER BY ordinal_position;";
        columns.Parameters.Add(new DuckDBParameter("table", table));
        using var reader = columns.ExecuteReader();
        var ordered = new List<IProperty?>();
        while (reader.Read())
        {
            if (version is { } replaced && reader.GetString(0) == replaced.Column)
            {
                if (!string.Equals(reader.GetString(1), "INTEGER", StringComparison.Ordinal))
                    throw new InvalidDataException("Appender version column must be INTEGER.");
                ordered.Add(null);
                continue;
            }
            var property =
                entity
                    .GetProperties()
                    .SingleOrDefault(p => p.GetColumnName() == reader.GetString(0))
                ?? throw new InvalidDataException("Appender column has no typed model property.");
            if (
                !string.Equals(
                    property.GetRelationalTypeMapping().StoreType,
                    reader.GetString(1),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidDataException("Appender column type and typed model disagree.");
            ordered.Add(property);
        }
        // Every entity column is written once; a version column stands in for ScanId only.
        if (
            ordered.Count != entity.GetProperties().Count()
            || ordered.Count(p => p is null) != (version is null ? 0 : 1)
            || (
                version is not null
                && (entity.FindProperty("ScanId") is null || ordered.Any(p => p?.Name == "ScanId"))
            )
        )
            throw new InvalidDataException("Appender schema and typed model disagree.");
        properties = [.. ordered];
        values = properties
            .Select(property =>
            {
                if (property is null)
                    return _ => version!.Value.Value;
                var getter = property.GetGetter();
                var converter = property.GetRelationalTypeMapping().Converter;
                return new Func<T, object?>(item =>
                    converter is null
                        ? getter.GetClrValue(item)
                        : converter.ConvertToProvider(getter.GetClrValue(item))
                );
            })
            .ToArray();
        appender = ((DuckDBConnection)context.Database.GetDbConnection()).CreateAppender(table);
    }

    internal void Add(T item, CancellationToken token)
    {
        try
        {
            AddCore(item, token);
        }
        catch (Exception error)
        {
            context.AppendFailure ??= error;
            throw;
        }
    }

    private void AddCore(T item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var row = appender.CreateRow();
        for (int index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            object? value = values[index](item);
            if (value is BigInteger integer && integer > SignedMaximum)
            {
                large.Add((item, property!, integer));
                value = BigInteger.Zero;
            }
            switch (value)
            {
                case null:
                    row.AppendNullValue();
                    break;
                case Guid guid:
                    row.AppendValue(guid);
                    break;
                case byte[] bytes:
                    row.AppendValue(bytes);
                    break;
                case string text:
                    row.AppendValue(text);
                    break;
                case bool flag:
                    row.AppendValue(flag);
                    break;
                case byte number:
                    row.AppendValue(number);
                    break;
                case ushort number:
                    row.AppendValue(number);
                    break;
                case uint number:
                    row.AppendValue(number);
                    break;
                case int number:
                    row.AppendValue(number);
                    break;
                case long number:
                    row.AppendValue(number);
                    break;
                case BigInteger number:
                    row.AppendValue(number);
                    break;
                case Enum number:
                    row.AppendValue(Convert.ToInt32(number, CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported appender mapping.");
            }
        }
        row.EndRow();
        if (item is ObservationEntity observation)
            context.DirtyGenerations.Add(observation.ScanId);
    }

    private static object? Read(IProperty property, T row)
    {
        var value = property.GetGetter().GetClrValue(row);
        var converter = property.GetRelationalTypeMapping().Converter;
        return converter is null ? value : converter.ConvertToProvider(value);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            FlushAndClose();
        }
        catch (Exception) when (context.AppendFailure is not null)
        {
            // Another append/flush already failed. Disposal must not replace that
            // exception (especially OOM) with the resulting aborted-transaction error.
        }
        catch (Exception error)
        {
            context.AppendFailure = error;
            throw;
        }
    }

    private void FlushAndClose()
    {
        appender.Dispose();
        // Rare values outside the signed appender range are fixed in the same
        // unpublished transaction. A failed correction prevents publication.
        if (large.Count == 0)
            return; // Keyless reference tables never hold full-range capacities.
        var keys = context.Model.FindEntityType(typeof(T))!.FindPrimaryKey()!.Properties;
        bool Replaced(IProperty key) => version is not null && key.Name == "ScanId";
        foreach (var item in large)
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText =
                $"UPDATE \"{table}\" SET \"{item.Property.GetColumnName()}\"=CAST($value AS UHUGEINT) WHERE "
                + string.Join(
                    " AND ",
                    keys.Select(
                        (p, i) =>
                            $"\"{(Replaced(p) ? version!.Value.Column : p.GetColumnName())}\"=$key{i}"
                    )
                )
                + $" RETURNING \"{item.Property.GetColumnName()}\";";
            command.Parameters.Add(
                new DuckDBParameter("value", item.Value.ToString(CultureInfo.InvariantCulture))
            );
            for (int i = 0; i < keys.Count; i++)
                command.Parameters.Add(
                    new DuckDBParameter(
                        $"key{i}",
                        Replaced(keys[i]) ? version!.Value.Value : Read(keys[i], item.Row)
                    )
                );
            if (command.ExecuteScalar() is not BigInteger actual || actual != item.Value)
                throw new InvalidDataException("Full-range capacity append failed.");
        }
    }
}

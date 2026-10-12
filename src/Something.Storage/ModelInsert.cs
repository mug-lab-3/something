using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Something.Storage;

/// <summary>Prepared append-only inserts derived from the EF model, including its value conversions.
/// Does not replace tracked updates, queries, migrations or transaction ownership.</summary>
internal sealed class ModelInsert<T> : IDisposable
    where T : class
{
    private readonly DbCommand command;
    private readonly Func<T, object?>[] values;

    internal ModelInsert(ScanDbContext context)
    {
        var entity =
            context.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException("Insert entity is absent from the EF model.");
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var properties = entity.GetProperties().ToArray();
        if (properties.Any(p => p.IsShadowProperty() || p.ValueGenerated != ValueGenerated.Never))
            throw new InvalidOperationException("Prepared inserts require explicit scalar values.");
        command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction =
            context.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Prepared inserts require a transaction.");
        var sql = context.GetService<ISqlGenerationHelper>();
        command.CommandText =
            $"INSERT INTO {sql.DelimitIdentifier(table.Name)} ({string.Join(",", properties.Select(p => sql.DelimitIdentifier(p.GetColumnName(table)!)))}) VALUES ({string.Join(",", properties.Select((_, i) => $"$p{i}"))});";
        values = new Func<T, object?>[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            var getter = properties[i].GetGetter();
            var mapping = properties[i].GetRelationalTypeMapping();
            var converter = mapping.Converter;
            values[i] = row =>
            {
                var value = getter.GetClrValue(row);
                return converter is null ? value : converter.ConvertToProvider(value);
            };
            command.Parameters.Add(
                mapping.CreateParameter(command, $"$p{i}", null, properties[i].IsNullable)
            );
        }
        command.Prepare();
    }

    internal void Add(T row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        for (int i = 0; i < values.Length; i++)
            command.Parameters[i].Value = values[i](row) ?? DBNull.Value;
        try
        {
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidDataException("Prepared insert did not write exactly one row.");
        }
        catch (SqliteException exception)
        {
            throw new DbUpdateException("Prepared model insert failed.", exception);
        }
    }

    public void Dispose() => command.Dispose();
}

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using SQLitePCL;

namespace Something.Storage;

/// <summary>Prepared append-only inserts derived from the EF model and its value conversions.
/// Uses the existing SQLite connection and transaction; queries, updates and migrations remain EF.</summary>
internal sealed class ModelInsert<T> : IDisposable
    where T : class
{
    private readonly sqlite3 connection = null!;
    private readonly sqlite3_stmt statement = null!;
    private readonly Func<T, object?>[] values = [];
    private readonly string?[] boundText = [];
    private readonly DuckModelInsert<T>? duck;

    internal ModelInsert(ScanDbContext context)
    {
        if (context is DuckScanDbContext native)
        {
            duck = new(native);
            return;
        }
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Prepared inserts require a transaction.");
        connection =
            ((SqliteConnection)context.Database.GetDbConnection()).Handle
            ?? throw new InvalidOperationException(
                "Prepared inserts require an open SQLite connection."
            );
        var entity =
            context.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException("Insert entity is absent from the EF model.");
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var properties = entity.GetProperties().ToArray();
        if (properties.Any(p => p.IsShadowProperty() || p.ValueGenerated != ValueGenerated.Never))
            throw new InvalidOperationException("Prepared inserts require explicit scalar values.");
        var sql = context.GetService<ISqlGenerationHelper>();
        string text =
            $"INSERT INTO {sql.DelimitIdentifier(table.Name)} ({string.Join(",", properties.Select(p => sql.DelimitIdentifier(p.GetColumnName(table)!)))}) VALUES ({string.Join(",", properties.Select((_, i) => $"?{i + 1}"))});";
        values = new Func<T, object?>[properties.Length];
        boundText = new string?[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            var getter = properties[i].GetGetter();
            var mapping = properties[i].GetRelationalTypeMapping();
            var converter = mapping.Converter;
            var providerType =
                Nullable.GetUnderlyingType(converter?.ProviderClrType ?? properties[i].ClrType)
                ?? converter?.ProviderClrType
                ?? properties[i].ClrType;
            if (
                !providerType.IsEnum
                && providerType != typeof(Guid)
                && providerType != typeof(string)
                && providerType != typeof(byte[])
                && providerType != typeof(bool)
                && providerType != typeof(byte)
                && providerType != typeof(ushort)
                && providerType != typeof(int)
                && providerType != typeof(uint)
                && providerType != typeof(long)
            )
                throw new InvalidOperationException(
                    "Unsupported prepared insert provider mapping."
                );
            if (providerType == typeof(Guid) && mapping.StoreType != "TEXT")
                throw new InvalidOperationException(
                    "Prepared GUID inserts require the provider's TEXT mapping."
                );
            Guid? previousGuid = null;
            string? previousGuidText = null;
            values[i] = row =>
            {
                var value = getter.GetClrValue(row);
                var provider = converter is null ? value : converter.ConvertToProvider(value);
                if (provider is not Guid guid)
                    return provider;
                // Microsoft.Data.Sqlite stores GUIDs as uppercase D-format text.
                if (previousGuid != guid)
                {
                    previousGuid = guid;
                    previousGuidText = guid.ToString("D").ToUpperInvariant();
                }
                return previousGuidText;
            };
        }
        int result = raw.sqlite3_prepare_v2(connection, text, out var prepared);
        if (result != raw.SQLITE_OK)
        {
            prepared?.Dispose();
            ThrowSqlite(result);
        }
        statement =
            prepared ?? throw new InvalidOperationException("SQLite did not prepare the insert.");
    }

    internal void Add(T row, CancellationToken token)
    {
        if (duck is not null)
        {
            duck.Add(row, token);
            return;
        }
        token.ThrowIfCancellationRequested();
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                int result = Bind(i + 1, values[i](row));
                if (result != raw.SQLITE_OK)
                    ThrowSqlite(result);
            }
            int step = raw.sqlite3_step(statement);
            if (step != raw.SQLITE_DONE)
                ThrowSqlite(step);
            if (raw.sqlite3_changes(connection) != 1)
                throw new InvalidDataException("Prepared insert did not write exactly one row.");
        }
        finally
        {
            // A failed statement reports its prior error from reset too. Preserve the original
            // failure; the enclosing transaction is rolled back by ScanStore.Write.
            // Reset preserves bindings. Only identical immutable TEXT values may reuse a binding;
            // all other values, including NULLs and blobs, are rebound on every row.
            raw.sqlite3_reset(statement);
        }
    }

    private int Bind(int index, object? value)
    {
        if (value is string text)
        {
            if (ReferenceEquals(boundText[index - 1], text))
                return raw.SQLITE_OK;
            int result = raw.sqlite3_bind_text(statement, index, text);
            if (result == raw.SQLITE_OK)
                boundText[index - 1] = text;
            return result;
        }
        boundText[index - 1] = null;
        return value switch
        {
            null or DBNull => raw.sqlite3_bind_null(statement, index),
            byte[] bytes => raw.sqlite3_bind_blob(statement, index, bytes),
            bool boolean => raw.sqlite3_bind_int64(statement, index, boolean ? 1 : 0),
            byte number => raw.sqlite3_bind_int64(statement, index, number),
            ushort number => raw.sqlite3_bind_int64(statement, index, number),
            int number => raw.sqlite3_bind_int64(statement, index, number),
            uint number => raw.sqlite3_bind_int64(statement, index, number),
            long number => raw.sqlite3_bind_int64(statement, index, number),
            Enum number => raw.sqlite3_bind_int64(
                statement,
                index,
                Convert.ToInt64(number, CultureInfo.InvariantCulture)
            ),
            _ => throw new InvalidOperationException("Unsupported prepared insert provider value."),
        };
    }

    private void ThrowSqlite(int code) =>
        throw new DbUpdateException(
            "Prepared model insert failed.",
            new SqliteException(
                raw.sqlite3_errmsg(connection).utf8_to_string(),
                code & 0xff,
                raw.sqlite3_extended_errcode(connection)
            )
        );

    public void Dispose()
    {
        if (duck is not null)
            duck.Dispose();
        else
            statement.Dispose();
    }
}

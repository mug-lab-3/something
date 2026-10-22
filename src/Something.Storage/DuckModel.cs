using System.Data.Common;
using System.Globalization;
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Something.Storage;

internal static class DuckModel
{
    internal static void Configure(ModelBuilder model)
    {
        foreach (var entity in model.Model.GetEntityTypes().ToArray())
        {
            foreach (var property in entity.GetProperties())
                if (
                    (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType)
                    == typeof(UInt128)
                )
                {
                    property.SetValueConverter(
                        new ValueConverter<UInt128, BigInteger>(
                            v => BigInteger.CreateChecked(v),
                            v => UInt128.CreateChecked(v)
                        )
                    );
                    property.SetColumnType("UHUGEINT");
                }
            foreach (var check in entity.GetCheckConstraints().ToArray())
            {
                string sql = check.Sql;
                foreach (var property in entity.GetProperties())
                {
                    string column = property.GetColumnName();
                    if (property.GetColumnType() == "UHUGEINT")
                        sql = sql.Replace($"length({column})=16", "TRUE", StringComparison.Ordinal);
                    else if (property.GetValueConverter()?.ProviderClrType == typeof(byte[]))
                        sql = sql.Replace(
                            $"length({column})",
                            $"octet_length({column})",
                            StringComparison.Ordinal
                        );
                }
                entity.RemoveCheckConstraint(check.Name!);
                entity.AddCheckConstraint(check.Name!, sql);
            }
            foreach (var index in entity.GetIndexes())
                if (index.GetFilter() is not null)
                {
                    index.SetFilter(null);
                    index.IsUnique = false;
                }
        }
    }
}

// The EF provider lacks HUGEINT scalar mappings. Keep this narrow, lossless adapter
// in storage; queries remain LINQ and native columns remain UHUGEINT.
internal sealed class DuckCapacityMappingPlugin : IRelationalTypeMappingSourcePlugin
{
    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo) =>
        mappingInfo.ClrType == typeof(BigInteger) ? new DuckCapacityMapping() : null;
}

internal sealed class DuckCapacityMapping : RelationalTypeMapping
{
    internal DuckCapacityMapping()
        : base(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(typeof(BigInteger)),
                "UHUGEINT",
                dbType: System.Data.DbType.String
            )
        ) { }

    private DuckCapacityMapping(RelationalTypeMappingParameters parameters)
        : base(parameters) { }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) =>
        new DuckCapacityMapping(parameters);

    protected override void ConfigureParameter(DbParameter parameter)
    {
        base.ConfigureParameter(parameter);
        parameter.ParameterName = parameter.ParameterName.TrimStart('$', '@', ':');
        if (parameter.Value is BigInteger value)
        {
            _ = UInt128.CreateChecked(value);
            parameter.Value = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    protected override string GenerateNonNullSqlLiteral(object value) =>
        $"CAST('{UInt128.CreateChecked((BigInteger)value).ToString(CultureInfo.InvariantCulture)}' AS UHUGEINT)";
}

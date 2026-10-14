using DuckDB.EFCoreProvider.Extensions;
using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Something.Storage;

internal sealed class DuckScanDbContext(DbContextOptions<DuckScanDbContext> options)
    : ScanDbContext(options)
{
    internal bool ReadOnly { get; init; }
    internal HashSet<Guid> DirtyGenerations { get; } = [];
}

internal sealed class DuckScanDbContextFactory : IDesignTimeDbContextFactory<DuckScanDbContext>
{
    private static readonly ServiceProvider Services = new ServiceCollection()
        .AddEntityFrameworkDuckDB()
        .AddSingleton<IRelationalTypeMappingSourcePlugin, DuckCapacityMappingPlugin>()
        .BuildServiceProvider();

    internal static DbContextOptions<DuckScanDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<DuckScanDbContext>()
            .UseDuckDB(connection)
            .UseInternalServiceProvider(Services)
            .Options;

    internal static DbContextOptions<DuckScanDbContext> Options(DuckDBConnection connection) =>
        new DbContextOptionsBuilder<DuckScanDbContext>()
            .UseDuckDB(connection, contextOwnsConnection: true)
            .UseInternalServiceProvider(Services)
            .Options;

    public DuckScanDbContext CreateDbContext(string[] args) => new(Options("Data Source=:memory:"));
}

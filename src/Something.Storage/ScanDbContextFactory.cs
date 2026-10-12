using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Something.Storage;

// Tooling builds the model only. No connection to a user's history is opened.
internal sealed class ScanDbContextFactory : IDesignTimeDbContextFactory<ScanDbContext>
{
    public ScanDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ScanDbContext>().UseSqlite("Data Source=:memory:").Options);
}

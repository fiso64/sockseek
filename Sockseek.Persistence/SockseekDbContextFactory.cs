using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence;

public sealed class SockseekDbContextFactory(DbContextOptions<SockseekDbContext> options)
    : IDbContextFactory<SockseekDbContext>
{
    public SockseekDbContext CreateDbContext() => new(options);

    public Task<SockseekDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

internal sealed class SockseekDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SockseekDbContext>
{
    public SockseekDbContext CreateDbContext(string[] args)
    {
        string path = Path.Combine(Path.GetTempPath(), "sockseek-design.db");
        return new SockseekDbContext(SockseekDbContextOptions.Create(new SockseekSqliteOptions(path)));
    }
}

using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Read;

public sealed class PersistenceMetadataReader(IDbContextFactory<SockseekDbContext> contextFactory)
{
    public async Task<long> GetMaximumDisplayIdAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Jobs.AsNoTracking()
            .Select(job => (long?)job.DisplayId)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
    }
}

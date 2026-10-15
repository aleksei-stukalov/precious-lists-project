using Microsoft.EntityFrameworkCore;
using PreciousLists.Infrastructure.Persistence;

namespace PreciousLists.Services;

public sealed class RefreshTokenCleanup(IServiceScopeFactory scopes, ILogger<RefreshTokenCleanup> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Expired tokens already fail validation, so their replay records can be removed.
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // A hosted service is a singleton; each pass needs its own scoped DbContext.
                await using var scope = scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
                await database.UsedRefreshTokens.Where(token => token.ExpiresAt <= now)
                    .ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Could not clean up expired refresh-token records.");
            }
        }
    }
}

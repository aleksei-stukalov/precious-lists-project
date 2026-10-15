using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PreciousLists.Features.Auth;

namespace PreciousLists.Infrastructure.Persistence;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<UsedRefreshToken> UsedRefreshTokens => Set<UsedRefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // Unique hashes enforce single-use refresh; expiry indexing supports hourly cleanup.
        builder.Entity<UsedRefreshToken>().HasKey(token => token.Hash);
        builder.Entity<UsedRefreshToken>().Property(token => token.Hash).HasMaxLength(64);
        builder.Entity<UsedRefreshToken>().HasIndex(token => token.ExpiresAt);
    }
}

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PreciousLists.Models;

namespace PreciousLists.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<UsedRefreshToken> UsedRefreshTokens => Set<UsedRefreshToken>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemImage> ItemImages => Set<ItemImage>();
    public DbSet<Collection> Collections => Set<Collection>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<UsedRefreshToken>(entity =>
        {
            entity.HasKey(token => token.Hash);
            entity.Property(token => token.Hash).HasMaxLength(64);
            entity.HasIndex(token => token.ExpiresAt);
        });

        // Configure foreign keys without requiring EF navigation properties in the models.
        builder.Entity<Item>(entity =>
        {
            entity.HasOne<Collection>()
                .WithMany()
                .HasForeignKey(item => item.CollectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Items_ThumbnailIndex_NonNegative",
                "\"ThumbnailIndex\" IS NULL OR \"ThumbnailIndex\" >= 0"));
        });

        builder.Entity<ItemImage>(entity =>
        {
            entity.HasOne<Item>()
                .WithMany()
                .HasForeignKey(image => image.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

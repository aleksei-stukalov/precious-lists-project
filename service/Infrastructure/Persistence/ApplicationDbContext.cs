using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PreciousLists.Models;

namespace PreciousLists.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<UsedRefreshToken> UsedRefreshTokens => Set<UsedRefreshToken>();
    public DbSet<Item> Items => Set<Item>();
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

        builder.Entity<Collection>(entity =>
        {
            entity
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(collection => collection.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasAlternateKey(collection => new { collection.Id, collection.UserId });
            entity.ToTable(table =>
                table.HasCheckConstraint("CK_Collections_Name_NotBlank", "btrim(\"Name\") <> ''")
            );
        });

        // Models use explicit foreign keys without navigation properties.
        builder.Entity<Item>(entity =>
        {
            entity
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasOne<Collection>()
                .WithMany()
                .HasForeignKey(item => item.CollectionId)
                .OnDelete(DeleteBehavior.SetNull);

            // Prevent assigning an item to another user's collection. The simple FK
            // above clears CollectionId on deletion; UserId must remain unchanged.
            entity
                .HasOne<Collection>()
                .WithMany()
                .HasForeignKey(item => new { item.CollectionId, item.UserId })
                .HasPrincipalKey(collection => new { collection.Id, collection.UserId })
                .OnDelete(DeleteBehavior.NoAction);

            entity.ToTable(table =>
                table.HasCheckConstraint("CK_Items_Name_NotBlank", "btrim(\"Name\") <> ''")
            );
        });

    }
}

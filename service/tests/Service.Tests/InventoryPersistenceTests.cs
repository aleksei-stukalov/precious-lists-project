using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PreciousLists.Features.Inventory;
using PreciousLists.Infrastructure.Persistence;
using Xunit;

namespace Service.Tests;

public sealed class InventoryPersistenceTests(AuthFixture fixture) : IClassFixture<AuthFixture>
{
    [Fact]
    public async Task ItemsCanBeAssignedMovedAndUnassigned()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var first = new Collection { Name = "Artwork" };
        var second = new Collection { Name = "Favorites" };
        var item = new Item { Name = "Painting" };
        database.Collections.AddRange(first, second);
        database.Items.Add(item);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var saved = await database.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Null(saved.CollectionId);
        foreach (Guid? collectionId in new Guid?[] { first.Id, second.Id, null })
        {
            saved.CollectionId = collectionId;
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            saved = await database.Items.Include(i => i.Collection).SingleAsync(i => i.Id == item.Id);
            Assert.Equal(collectionId, saved.CollectionId);
            Assert.Equal(collectionId, saved.Collection?.Id);
        }
    }

    [Fact]
    public async Task DeletingCollectionKeepsItsItemsAndImages()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var collection = new Collection { Name = "Artwork" };
        var item = new Item
        {
            Name = "Painting", Collection = collection,
            Images = [new ItemImage { Url = "https://images.example.test/painting.jpg", IsThumbnail = true }]
        };
        database.Items.Add(item);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        // ExecuteDelete tests PostgreSQL's FK behavior without EF fixing up tracked items.
        await database.Collections.Where(c => c.Id == collection.Id).ExecuteDeleteAsync();
        var saved = await database.Items.Include(i => i.Images).SingleAsync(i => i.Id == item.Id);
        Assert.Null(saved.CollectionId);
        Assert.Single(saved.Images);
    }

    [Fact]
    public async Task DatabaseRejectsAnUnknownCollection()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        database.Items.Add(new Item { Name = "Painting", CollectionId = Guid.NewGuid() });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task ItemsAndCollectionsPersistWithOptionalThumbnails()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = new Item
        {
            Name = "Painting",
            Description = "Oil on canvas",
            Images =
            [
                new ItemImage { Url = "https://images.example.test/front.jpg", IsThumbnail = true },
                new ItemImage { Url = "https://images.example.test/back.jpg" },
                new ItemImage { Url = "https://images.example.test/detail.jpg" }
            ]
        };
        var collection = new Collection { Name = "Artwork" };
        var illustratedCollection = new Collection
        {
            Name = "Collectibles", ThumbnailUrl = "https://images.example.test/collection.jpg"
        };
        database.Items.Add(item);
        database.Collections.AddRange(collection, illustratedCollection);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var saved = await database.Items.Include(i => i.Images).SingleAsync(i => i.Id == item.Id);
        Assert.Equal("Painting", saved.Name);
        Assert.Equal("Oil on canvas", saved.Description);
        Assert.Equal(3, saved.Images.Count);
        Assert.Equal("https://images.example.test/front.jpg", Assert.Single(saved.Images, i => i.IsThumbnail).Url);
        Assert.Null((await database.Collections.FindAsync(collection.Id))!.ThumbnailUrl);
        Assert.Equal(illustratedCollection.ThumbnailUrl,
            (await database.Collections.FindAsync(illustratedCollection.Id))!.ThumbnailUrl);

        // Database cascade must work even when image entities are not loaded.
        database.ChangeTracker.Clear();
        await database.Items.Where(i => i.Id == item.Id).ExecuteDeleteAsync();
        Assert.False(await database.ItemImages.AnyAsync(i => i.ItemId == item.Id));
        Assert.True(await database.Collections.AnyAsync(c => c.Id == collection.Id));
    }

    [Fact]
    public async Task DatabaseRejectsSecondThumbnailForTheSameItem()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = new Item { Name = "Vase" };
        database.Items.Add(item);
        await database.SaveChangesAsync();
        database.ItemImages.Add(new ItemImage
        {
            ItemId = item.Id, Url = "https://images.example.test/first.jpg", IsThumbnail = true
        });
        await database.SaveChangesAsync();
        database.ItemImages.Add(new ItemImage
        {
            ItemId = item.Id, Url = "https://images.example.test/second.jpg", IsThumbnail = true
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_ItemImages_OneThumbnailPerItem", postgres.ConstraintName);
    }
}

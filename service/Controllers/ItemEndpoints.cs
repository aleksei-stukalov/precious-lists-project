using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PreciousLists.Contracts;
using PreciousLists.Infrastructure.Persistence;
using PreciousLists.Models;

namespace PreciousLists.Controllers;

public static class ItemEndpoints
{
    public static void MapItemEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/items")
            .WithTags("Items")
            .RequireRateLimiting("auth");

        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
    }

    private static async Task<IResult> Create(
        ItemRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext database,
        CancellationToken cancellation
    )
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (!await OwnsCollection(request.CollectionId, userId, database, cancellation))
            return Results.NotFound();

        var item = new Item {
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description,
            CollectionId = request.CollectionId,
        };
        database.Items.Add(item);

        await database.SaveChangesAsync(cancellation);
        return Results.Created(
            $"/v1/items/{item.Id}",
            new {
                item.Id,
                item.CollectionId,
                item.Name,
                item.Description
            });
    }

    private static async Task<IResult> Update(
        Guid id,
        ItemRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext database,
        CancellationToken cancellation
    )
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
        
        if (!await OwnsCollection(request.CollectionId, userId, database, cancellation))
            return Results.NotFound();

        var item = await database.Items.SingleOrDefaultAsync(
            item => item.Id == id && item.UserId == userId,
            cancellation
        );
        if (item is null) return Results.NotFound();

        item.Name = request.Name.Trim();
        item.Description = request.Description;
        item.CollectionId = request.CollectionId;

        await database.SaveChangesAsync(cancellation);
        return Results.Ok(new {
            item.Id,
            item.CollectionId,
            item.Name,
            item.Description
        });
    }

    private static async Task<IResult> Delete(
        Guid id,
        ClaimsPrincipal user,
        ApplicationDbContext database,
        CancellationToken cancellation
    )
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var item = await database.Items.SingleOrDefaultAsync(
            item => item.Id == id && item.UserId == userId,
            cancellation
        );
        if (item is null) return Results.NotFound();

        database.Items.Remove(item);

        await database.SaveChangesAsync(cancellation);
        return Results.NoContent();
    }

    private static async Task<bool> OwnsCollection(
        Guid? collectionId,
        string userId,
        ApplicationDbContext database,
        CancellationToken cancellation)
    {
        return collectionId is null
               || await database.Collections.AnyAsync(
                   collection => collection.Id == collectionId && collection.UserId == userId,
                   cancellation);
    }
}

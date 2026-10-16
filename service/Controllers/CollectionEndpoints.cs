using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PreciousLists.Contracts;
using PreciousLists.Infrastructure.Persistence;
using PreciousLists.Models;

namespace PreciousLists.Controllers;

public static class CollectionEndpoints
{
    public static void MapCollectionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/v1/collections")
            .WithTags("Collections")
            .RequireRateLimiting("auth");

        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
    }

    private static async Task<IResult> Create(
        CollectionRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext database,
        CancellationToken cancellation
    )
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var collection = new Collection {
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description
        };
        database.Collections.Add(collection);

        await database.SaveChangesAsync(cancellation);
        return Results.Created(
            $"/v1/collections/{collection.Id}",
            new {
                collection.Id,
                collection.Name,
                collection.Description
            });
    }

    private static async Task<IResult> Update(
        Guid id,
        CollectionRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext database,
        CancellationToken cancellation
    )
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var collection = await database.Collections.SingleOrDefaultAsync(
            collection => collection.Id == id && collection.UserId == userId,
            cancellation
        );
        if (collection is null) return Results.NotFound();

        collection.Name = request.Name.Trim();
        collection.Description = request.Description;

        await database.SaveChangesAsync(cancellation);
        return Results.Ok(new {
            collection.Id,
            collection.Name,
            collection.Description
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

        var collection = await database.Collections.SingleOrDefaultAsync(
            collection => collection.Id == id && collection.UserId == userId,
            cancellation
        );
        if (collection is null) return Results.NotFound();

        database.Collections.Remove(collection);

        await database.SaveChangesAsync(cancellation);
        return Results.NoContent();
    }
}

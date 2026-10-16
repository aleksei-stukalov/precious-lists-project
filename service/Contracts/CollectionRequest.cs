using System.ComponentModel.DataAnnotations;

namespace PreciousLists.Contracts;

// POST creates a collection; PUT replaces its editable fields.
public sealed record CollectionRequest(
    [property: Required, StringLength(200)] string Name,
    [property: StringLength(10000)] string? Description = null
);

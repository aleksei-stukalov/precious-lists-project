using System.ComponentModel.DataAnnotations;

namespace PreciousLists.Contracts;

// Null CollectionId means unassigned. PUT replaces all editable fields.
public sealed record ItemRequest(
    [property: Required, StringLength(200)] string Name,
    [property: StringLength(10000)] string? Description = null,
    Guid? CollectionId = null
);

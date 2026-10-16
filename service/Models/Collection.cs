namespace PreciousLists.Models;

public sealed class Collection
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

namespace PreciousLists.Models;

public sealed class Item
{
    public Guid Id { get; set; }
    public Guid? CollectionId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public short? ThumbnailIndex { get; set; }
}

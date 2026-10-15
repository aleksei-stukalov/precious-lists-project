namespace PreciousLists.Models;

public sealed class ItemImage
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public string Url { get; set; } = "";
}

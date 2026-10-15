namespace PreciousLists.Models;

public sealed class UsedRefreshToken
{
    // SHA-256 fingerprint of a consumed token, never the reusable credential itself.
    public string Hash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

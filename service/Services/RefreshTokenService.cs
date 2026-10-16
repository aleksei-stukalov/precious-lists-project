using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PreciousLists.Infrastructure.Persistence;
using PreciousLists.Models;

namespace PreciousLists.Services;

// Identity issues tokens; this service adds single-use refresh and replay detection.
public sealed class RefreshTokenService(
    SignInManager<ApplicationUser> signIn,
    IOptionsMonitor<BearerTokenOptions> options,
    ApplicationDbContext database,
    TimeProvider clock)
{
    public async Task<bool> ConsumeAsync(string token, CancellationToken cancellation)
    {
        var settings = options.Get(IdentityConstants.BearerScheme);
        var ticket = settings.RefreshTokenProtector.Unprotect(token);
        if (ticket?.Properties.ExpiresUtc is not { } expires
            || expires <= clock.GetUtcNow()
            || await signIn.ValidateSecurityStampAsync(ticket.Principal) is not { } user
            || !user.EmailConfirmed
            || await signIn.UserManager.IsLockedOutAsync(user))
            return false;

        // Store only a fingerprint. The primary key lets just one concurrent request win.
        var used = new UsedRefreshToken {
            Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            ExpiresAt = expires
        };
        database.UsedRefreshTokens.Add(used);
        try {
            await database.SaveChangesAsync(cancellation);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException {
                      SqlState: PostgresErrorCodes.UniqueViolation
                  }) {
            // Detach the failed insert before Identity saves the security stamp.
            database.Entry(used).State = EntityState.Detached;
            var revoked = await signIn.UserManager.UpdateSecurityStampAsync(user);
            if (!revoked.Succeeded)
                throw new InvalidOperationException(
                    "Could not revoke sessions after refresh-token replay.");
            return false;
        }
    }
}

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace PreciousLists.Features.Auth;

public sealed record ReauthenticationRequest(string Password, string? TwoFactorCode = null,
    string? TwoFactorRecoveryCode = null);

public static class AccountEndpoints
{
    public static async Task<IResult> Me(ClaimsPrincipal principal, UserManager<ApplicationUser> users)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? Results.Unauthorized() : Results.Ok(new
        {
            user.Id, user.Email, user.EmailConfirmed, user.TwoFactorEnabled
        });
    }

    // No persistent per-device sessions yet: both logout routes revoke ALL tokens.
    public static async Task<IResult> Logout(ClaimsPrincipal principal, UserManager<ApplicationUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return Results.Unauthorized();
        var result = await users.UpdateSecurityStampAsync(user);
        return result.Succeeded ? Results.NoContent() : Results.Conflict();
    }

    public static async Task<IResult> Reauthenticate(ReauthenticationRequest request,
        ClaimsPrincipal principal, SignInManager<ApplicationUser> signIn,
        ReauthenticationTickets tickets)
    {
        var users = signIn.UserManager;
        var user = await users.GetUserAsync(principal);
        if (user is null || string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
            return Results.Unauthorized();
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) return Results.Unauthorized();
        // An existing bearer token is not enough to change account security settings.
        // Enabled MFA must also be proved again; a recovery code is consumed once.
        if (user.TwoFactorEnabled)
        {
            var valid = !string.IsNullOrEmpty(request.TwoFactorCode)
                && await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider,
                    request.TwoFactorCode.Replace(" ", "").Replace("-", ""));
            if (!valid && !string.IsNullOrEmpty(request.TwoFactorRecoveryCode))
                valid = (await users.RedeemTwoFactorRecoveryCodeAsync(user, request.TwoFactorRecoveryCode)).Succeeded;
            if (!valid)
            {
                await users.AccessFailedAsync(user);
                return Results.Unauthorized();
            }
        }
        return Results.Ok(new { reauthenticationToken = tickets.Create(user), expiresIn = 300 });
    }
}

public sealed class ReauthenticationTickets(IDataProtectionProvider provider)
{
    // Separate purpose and short expiry prevent using access/reset tokens as proof.
    private readonly ITimeLimitedDataProtector protector = provider
        .CreateProtector("PreciousLists.Reauthentication.v1").ToTimeLimitedDataProtector();

    public string Create(ApplicationUser user) => protector.Protect(
        JsonSerializer.Serialize(new Ticket(user.Id, user.SecurityStamp)), TimeSpan.FromMinutes(5));

    public bool IsValid(string value, ApplicationUser user)
    {
        try
        {
            var ticket = JsonSerializer.Deserialize<Ticket>(protector.Unprotect(value));
            // Bind proof to this account and invalidate it whenever sessions are revoked.
            return ticket?.UserId == user.Id && ticket.SecurityStamp == user.SecurityStamp;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record Ticket(string UserId, string? SecurityStamp);
}

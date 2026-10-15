using Microsoft.AspNetCore.Identity;
using PreciousLists.Models;

namespace PreciousLists.Infrastructure;

// Built-in access tokens normally stay valid until expiry. Validate the stamp on
// every authenticated request to make password resets and logout immediate.
public sealed class SecurityStampMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SignInManager<ApplicationUser> signIn)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var user = await signIn.ValidateSecurityStampAsync(context.User);
            if (user is null || !user.EmailConfirmed || await signIn.UserManager.IsLockedOutAsync(user))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        await next(context);
    }
}

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;

namespace PreciousLists.Features.Auth;

// Adds project-specific safeguards around the framework's Identity endpoints.
public sealed class AuthEndpointFilter(
    SignInManager<ApplicationUser> signIn,
    ReauthenticationTickets tickets,
    RefreshTokenService refreshTokens) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        var users = signIn.UserManager;

        if (http.Request.Query.ContainsKey("useCookies") || http.Request.Query.ContainsKey("useSessionCookies"))
            return Results.Problem(statusCode: 400, title: "This API only supports bearer-token login.");

        // Reject oversized inputs before Identity hashes passwords or parses tokens.
        if (context.Arguments.Any(argument => !IsValidInput(argument)))
            return Results.Problem(statusCode: 400, title: "Invalid authentication request.");

        // Account changes need recent password/MFA proof, not just an access token.
        ApplicationUser? managedUser = null;
        if (HttpMethods.IsPost(http.Request.Method)
            && http.Request.Path.StartsWithSegments("/api/v1/auth/manage"))
        {
            managedUser = await users.GetUserAsync(http.User);
            var proof = http.Request.Headers["X-Reauthentication-Token"].ToString();
            if (managedUser is null || proof.Length > 8192
                || !tickets.IsValid(proof, managedUser))
                return Results.Problem(statusCode: 403, title: "Fresh authentication is required.",
                    detail: "Call /api/v1/auth/reauthenticate and supply X-Reauthentication-Token.");
        }

        if (context.Arguments.OfType<RefreshRequest>().SingleOrDefault() is { } refresh
            && !await refreshTokens.ConsumeAsync(refresh.RefreshToken, http.RequestAborted))
            return Results.Unauthorized();

        var result = await next(context);
        var innerResult = result;
        // Identity returns nested typed results; unwrap them before checking the outcome.
        while (innerResult is INestedHttpResult nested) innerResult = nested.Result;
        // Keep Identity's confirmation/token validation, but expose an API JSON contract.
        if (HttpMethods.IsGet(http.Request.Method)
            && string.Equals(http.Request.Path.Value?.TrimEnd('/'), "/api/v1/auth/confirmEmail",
                StringComparison.OrdinalIgnoreCase))
        {
            if (innerResult is IStatusCodeHttpResult { StatusCode: 200 }
                or ContentHttpResult { StatusCode: null })
                return Results.Ok(new { status = "ok", message = "Email confirmed." });
            if (innerResult is IStatusCodeHttpResult { StatusCode: 400 or 401 })
                return Results.Problem(statusCode: 400, title: "Invalid email confirmation link.",
                    detail: "The confirmation link is invalid or expired. Request a new confirmation email.");
        }

        // Match the successful response for existing emails to avoid account enumeration.
        if (context.Arguments.OfType<RegisterRequest>().Any()
            && innerResult is ValidationProblem { ProblemDetails: var problem }
            && problem.Errors.Keys.All(key => key is "DuplicateUserName" or "DuplicateEmail"))
            return Results.Ok();

        // Enabling/disabling MFA or regenerating secrets invalidates all old tokens.
        if (managedUser is not null
            && context.Arguments.OfType<TwoFactorRequest>().SingleOrDefault() is { } mfa
            && (mfa.Enable.HasValue || mfa.ResetSharedKey || mfa.ResetRecoveryCodes)
            && innerResult is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
        {
            var changed = await users.UpdateSecurityStampAsync(managedUser);
            if (!changed.Succeeded) return Results.Conflict();
        }
        return result;
    }

    // Identity enforces password strength and email syntax; these are input size limits.
    private static bool IsValidInput(object? argument) => argument switch
    {
        RegisterRequest r => Email(r.Email) && Password(r.Password),
        LoginRequest r => Email(r.Email) && Password(r.Password)
            && Optional(r.TwoFactorCode, 32) && Optional(r.TwoFactorRecoveryCode, 128),
        RefreshRequest r => Required(r.RefreshToken, 8192),
        ForgotPasswordRequest r => Email(r.Email),
        ResendConfirmationEmailRequest r => Email(r.Email),
        ResetPasswordRequest r => Email(r.Email) && Password(r.NewPassword) && Required(r.ResetCode, 8192),
        InfoRequest r => (r.NewEmail is null || Email(r.NewEmail))
            && Optional(r.NewPassword, 128) && Optional(r.OldPassword, 128),
        TwoFactorRequest r => Optional(r.TwoFactorCode, 32),
        ReauthenticationRequest r => Password(r.Password)
            && Optional(r.TwoFactorCode, 32) && Optional(r.TwoFactorRecoveryCode, 128),
        _ => true
    };

    private static bool Required(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool Optional(string? value, int max) => value is null || value.Length <= max;
    private static bool Email(string? value) => Required(value, 254);
    private static bool Password(string? value) => Required(value, 128);
}

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PreciousLists.Controllers;
using PreciousLists.Infrastructure.Persistence;
using PreciousLists.Models;
using PreciousLists.Services;

namespace PreciousLists.Infrastructure;

public static class AuthenticationSetup
{
    public static void AddAccountAuthentication(this WebApplicationBuilder builder)
    {
        // Email delivery is intentionally deferred. Fail closed outside development.
        if (!builder.Environment.IsDevelopment())
            throw new InvalidOperationException(
                "Production authentication requires a real email sender. See docs/authentication.md."
            );

        // A configured origin prevents request Host headers from controlling email links.
        builder
            .Services.AddOptions<AccountOptions>()
            .BindConfiguration("Authentication")
            .Validate(
                o =>
                    Uri.TryCreate(o.PublicBaseUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == "https"
                    && string.IsNullOrEmpty(uri.UserInfo)
                    && uri.AbsolutePath == "/"
                    && string.IsNullOrEmpty(uri.Query)
                    && string.IsNullOrEmpty(uri.Fragment),
                "Authentication:PublicBaseUrl must be an HTTPS origin."
            )
            .Validate(o => o.RequestsPerMinute > 0, "RequestsPerMinute must be positive.")
            .ValidateOnStart();

        // Use Identity's password hashing, token providers, MFA and account storage.
        builder
            .Services.AddIdentityApiEndpoints<ApplicationUser>(options => {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<ApplicationDbContext>();

        // Only bearer authentication is accepted by this API. Browser cookie/BFF
        // authentication requires a separate CSRF-protected integration.
        builder.Services.Configure<AuthenticationOptions>(options => {
            options.DefaultAuthenticateScheme = IdentityConstants.BearerScheme;
            options.DefaultChallengeScheme = IdentityConstants.BearerScheme;
        });
        builder.Services.Configure<BearerTokenOptions>(
            IdentityConstants.BearerScheme,
            options => {
                options.BearerTokenExpiration = TimeSpan.FromMinutes(10);
                options.RefreshTokenExpiration = TimeSpan.FromDays(7);
            }
        );
        builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromHours(1)
        );

        // Persist encryption keys so container restarts do not invalidate issued tokens.
        var protection = builder.Services.AddDataProtection().SetApplicationName("PreciousLists");
        if (builder.Configuration["Authentication:DataProtectionKeysPath"] is { Length: > 0 } path)
            protection.PersistKeysToFileSystem(new DirectoryInfo(path));

        // FUTURE: replace DevelopmentEmailSender with a real SMTP/provider
        // IEmailSender<ApplicationUser>. Never silently discard verification mail.
        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, DevelopmentEmailSender>();
        builder.Services.AddSingleton<ReauthenticationTickets>();
        builder.Services.AddScoped<RefreshTokenService>();
        builder.Services.AddHostedService<RefreshTokenCleanup>();
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<AuthExceptionHandler>();

        // Per-process throttling. Multiple API replicas need a shared gateway limiter.
        builder.Services.AddRateLimiter(options => {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(
                "auth",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions {
                            PermitLimit = context
                                .RequestServices.GetRequiredService<IOptions<AccountOptions>>()
                                .Value.RequestsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        }
                    )
            );
            options.OnRejected = async (context, cancellation) => {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await Results
                    .Problem(statusCode: 429, title: "Too many requests.")
                    .ExecuteAsync(context.HttpContext);
            };
        });
    }
}

public sealed class AccountOptions
{
    public string PublicBaseUrl { get; set; } = "https://localhost:7183";
    public int RequestsPerMinute { get; set; } = 30;
}

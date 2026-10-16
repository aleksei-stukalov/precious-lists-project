using Microsoft.EntityFrameworkCore;
using PreciousLists.Infrastructure;
using PreciousLists.Controllers;
using PreciousLists.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Resolve configuration when the context is created so integration tests can
// replace the connection string before any database access happens.
builder.Services.AddDbContext<ApplicationDbContext>((services, options) =>
    options.UseNpgsql(
        services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' is missing."
        )
    )
);

builder.Services.AddOpenApi();
builder.AddAccountAuthentication();

var app = builder.Build();
app.UseExceptionHandler();

// Expose OpenAPI only in development for security reasons.
if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseHttpsRedirection(); // Enforce HTTPS for all requests.
app.UseAuthentication(); // Validate the bearer token and populate HttpContext.User.
app.UseRateLimiter(); // Apply rate limiting to all requests.
app.UseMiddleware<SecurityStampMiddleware>(); // Reject requests with a stale security stamp
app.UseAuthorization(); // Reject requests that fail the authorization policy.

// Map the endpoints.
app.MapAccountAuthenticationEndpoints();
app.MapCollectionEndpoints();
app.MapItemEndpoints();

// Liveness only: this does not check PostgreSQL or other dependencies.
app.MapGet("/", () => Results.Ok(new { status = "ok" }));

app.Run();

// Expose the entry point to WebApplicationFactory in the integration test
// project.
public partial class Program;

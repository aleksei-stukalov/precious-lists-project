using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PreciousLists.Features.Auth;
using PreciousLists.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace Service.Tests;

public sealed class CapturedEmail : IEmailSender<ApplicationUser>
{
    // Exercise the real confirmation/reset endpoints without sending email or reading files.
    public ConcurrentDictionary<string, string> Confirmations { get; } = new();
    public ConcurrentDictionary<string, string> Resets { get; } = new();
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string link)
    {
        Confirmations[email] = System.Net.WebUtility.HtmlDecode(link);
        return Task.CompletedTask;
    }
    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string code)
    {
        Resets[email] = code;
        return Task.CompletedTask;
    }
    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string link) =>
        throw new NotSupportedException();
}

public sealed class AuthFactory(string connection, CapturedEmail email, int limit = 10000)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connection,
                ["Authentication:PublicBaseUrl"] = "https://localhost",
                ["Authentication:RequestsPerMinute"] = limit.ToString()
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender<ApplicationUser>>();
            services.AddSingleton<IEmailSender<ApplicationUser>>(email);
        });
    }
}

public sealed class AuthFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public CapturedEmail Email { get; } = new();
    public AuthFactory Factory { get; private set; } = null!;
    public string Connection => database.GetConnectionString();

    public async Task InitializeAsync()
    {
        // Never migrate until the connection has been checked against the disposable container.
        await database.StartAsync();
        Factory = new AuthFactory(Connection, Email);
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var actual = new Npgsql.NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        var expected = new Npgsql.NpgsqlConnectionStringBuilder(Connection);
        Assert.Equal(expected.Host, actual.Host);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.Database, actual.Database);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        // Release the container even if stopping the in-memory API fails.
        try
        {
            if (Factory is not null) await Factory.DisposeAsync();
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false
    });
}

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PreciousLists.Features.Auth;
using Xunit;

namespace Service.Tests;

public sealed class DevelopmentEmailTests
{
    [Theory]
    [InlineData("https://inventory.example.test/#fragment")]
    [InlineData("https://inventory.example.test/?query=value")]
    [InlineData("https://inventory.example.test/account")]
    public void EmailOriginRejectsPathsQueriesAndFragments(string origin)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:PublicBaseUrl"] = origin
        });
        builder.AddAccountAuthentication();
        using var services = builder.Services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<AccountOptions>>().Value);
    }

    [Fact]
    public async Task DevelopmentOutboxUsesTrustedOriginAndPrivatePermissions()
    {
        var root = Path.Combine(Path.GetTempPath(), "preciouslists-mail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sender = new DevelopmentEmailSender(new TestEnvironment(root),
                Options.Create(new AccountOptions { PublicBaseUrl = "https://inventory.example.test" }));
            await sender.SendConfirmationLinkAsync(new ApplicationUser(), "person@example.test",
                "https://untrusted.example/api/v1/auth/confirmEmail?userId=test&amp;code=test-code");
            var file = Assert.Single(Directory.GetFiles(Path.Combine(root, ".local-mail")));
            using var message = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal("https://inventory.example.test/api/v1/auth/confirmEmail?userId=test&code=test-code",
                message.RootElement.GetProperty("content").GetString());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DevelopmentSenderRefusesProductionDelivery()
    {
        var root = Path.Combine(Path.GetTempPath(), "preciouslists-unused-" + Guid.NewGuid().ToString("N"));
        var sender = new DevelopmentEmailSender(new TestEnvironment(root) { EnvironmentName = "Production" },
            Options.Create(new AccountOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendPasswordResetCodeAsync(new ApplicationUser(), "person@example.test", "test-code"));
        Assert.False(Directory.Exists(root));
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Service.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

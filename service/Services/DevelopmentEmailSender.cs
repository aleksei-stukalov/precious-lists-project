using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PreciousLists.Models;

namespace PreciousLists.Infrastructure;

// Development-only delivery. No messages leave the machine and tokens are never logged.
public sealed class DevelopmentEmailSender(
    IWebHostEnvironment environment,
    IOptions<AccountOptions> options) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        var generated = new Uri(WebUtility.HtmlDecode(confirmationLink));
        // Preserve Identity's query parameters while replacing the untrusted request origin.
        var safeLink = new Uri(new Uri(options.Value.PublicBaseUrl), generated.PathAndQuery);
        return SaveAsync(email, "Confirm your email", safeLink.AbsoluteUri);
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        throw new NotSupportedException("Password recovery uses reset codes, not client-supplied links.");

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SaveAsync(email, "Reset your password", WebUtility.HtmlDecode(resetCode));

    private async Task SaveAsync(string recipient, string subject, string content)
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Email delivery has not been implemented for production.");

        var directory = Path.Combine(environment.ContentRootPath, ".local-mail");
        // Messages contain account recovery secrets; restrict the directory and each file.
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, settings);
        await JsonSerializer.SerializeAsync(stream, new { recipient, subject, content },
            new JsonSerializerOptions { WriteIndented = true });
    }
}

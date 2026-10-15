using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PreciousLists.Features.Auth;
using PreciousLists.Infrastructure.Persistence;
using Xunit;

namespace Service.Tests;

public sealed class AuthenticationTests(AuthFixture fixture) : IClassFixture<AuthFixture>
{
    private const string Prefix = "/api/v1/auth";
    private const string Password = "a long test passphrase 123";
    private const string NewPassword = "a different long passphrase 456";

    private async Task<string> Register(HttpClient client, bool confirm = true)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var response = await client.PostAsJsonAsync(Prefix + "/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(fixture.Email.Confirmations.ContainsKey(email));
        if (confirm)
        {
            response = await client.GetAsync(new Uri(fixture.Email.Confirmations[email]).PathAndQuery);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var confirmation = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ok", confirmation.GetProperty("status").GetString());
            Assert.Equal("Email confirmed.", confirmation.GetProperty("message").GetString());
        }
        return email;
    }

    private async Task<Tokens> Login(HttpClient client, string email, string password = Password,
        string? twoFactorCode = null, string? twoFactorRecoveryCode = null)
    {
        var response = await client.PostAsJsonAsync(Prefix + "/login",
            new { email, password, twoFactorCode, twoFactorRecoveryCode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var tokens = (await response.Content.ReadFromJsonAsync<Tokens>())!;
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(600, tokens.ExpiresIn);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return tokens;
    }

    private async Task Reauthenticate(HttpClient client, string? twoFactorCode = null,
        string? twoFactorRecoveryCode = null)
    {
        var response = await client.PostAsJsonAsync(Prefix + "/reauthenticate",
            new { password = Password, twoFactorCode, twoFactorRecoveryCode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove("X-Reauthentication-Token");
        client.DefaultRequestHeaders.Add("X-Reauthentication-Token", json.GetProperty("reauthenticationToken").GetString());
    }

    [Theory]
    [InlineData("/confirmEmail")]
    [InlineData("/confirmEmail?userId=example")]
    [InlineData("/confirmEmail?code=example")]
    public async Task IncompleteConfirmationLinkReturnsBadRequest(string path)
    {
        using var client = fixture.Client();
        var response = await client.GetAsync(Prefix + path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SessionFlowCanBeRepeatedWithFreshAccounts()
    {
        var emails = new HashSet<string>();
        for (var run = 0; run < 2; run++)
        {
            using var client = fixture.Client();
            var health = await client.GetFromJsonAsync<JsonElement>("/");
            Assert.Equal("ok", health.GetProperty("status").GetString());

            // Register confirms this fresh account through its captured email link
            // and checks the confirmation's success JSON.
            var email = await Register(client);
            Assert.True(emails.Add(email));
            var tokens = await Login(client, email);
            var me = await client.GetFromJsonAsync<JsonElement>(Prefix + "/me");
            Assert.Equal(email, me.GetProperty("email").GetString());
            Assert.True(me.GetProperty("emailConfirmed").GetBoolean());

            client.DefaultRequestHeaders.Authorization = null;
            var refresh = await client.PostAsJsonAsync(Prefix + "/refresh", new { tokens.RefreshToken });
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            var renewed = (await refresh.Content.ReadFromJsonAsync<Tokens>())!;
            Assert.NotEqual(tokens.RefreshToken, renewed.RefreshToken);
            client.DefaultRequestHeaders.Authorization = new("Bearer", renewed.AccessToken);
            me = await client.GetFromJsonAsync<JsonElement>(Prefix + "/me");
            Assert.Equal(email, me.GetProperty("email").GetString());

            Assert.Equal(HttpStatusCode.NoContent,
                (await client.PostAsJsonAsync(Prefix + "/logout", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync(Prefix + "/refresh", new { renewed.RefreshToken })).StatusCode);
        }
    }

    [Fact]
    public async Task RegistrationRequiresVerificationAndLoginProtectsMe()
    {
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
        var email = await Register(client, confirm: false);
        var response = await client.PostAsJsonAsync(Prefix + "/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(fixture.Email.Confirmations[email]).PathAndQuery)).StatusCode);
        await Login(client, email);
        var me = await client.GetFromJsonAsync<JsonElement>(Prefix + "/me");
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("emailConfirmed").GetBoolean());
    }

    [Fact]
    public async Task DuplicateRegistrationDoesNotRevealAccountAndWeakPasswordsFail()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Prefix + "/register",
            new { email, password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Prefix + "/register",
            new { email = "weak@example.test", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Prefix + "/register",
            new { email = "bad", password = Password })).StatusCode);
    }

    [Fact]
    public async Task ResendAndForgotPasswordHaveGenericResponses()
    {
        using var client = fixture.Client();
        var email = await Register(client, confirm: false);
        foreach (var path in new[] { "/resendConfirmationEmail", "/forgotPassword" })
        {
            var known = await client.PostAsJsonAsync(Prefix + path, new { email });
            var unknown = await client.PostAsJsonAsync(Prefix + path, new { email = "unknown@example.test" });
            Assert.Equal(known.StatusCode, unknown.StatusCode);
            Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        }
        Assert.False(fixture.Email.Resets.ContainsKey(email));
        var invalid = await client.GetAsync(Prefix + "/confirmEmail?userId=unknown&code=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("Invalid email confirmation link.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task RefreshIsSingleUseAndReplayRevokesAllIssuedTokens()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        var first = await Login(client, email);
        client.DefaultRequestHeaders.Authorization = null;
        var response = await client.PostAsJsonAsync(Prefix + "/refresh", new { first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = (await response.Content.ReadFromJsonAsync<Tokens>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/refresh",
            new { first.RefreshToken })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", second.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/refresh",
            new { second.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRefreshHasAtMostOneWinner()
    {
        using var client = fixture.Client();
        var tokens = await Login(client, await Register(client));
        client.DefaultRequestHeaders.Authorization = null;
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsJsonAsync(Prefix + "/refresh", new { tokens.RefreshToken })));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LogoutRevokesAccessAndRefreshAcrossLogins()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        var first = await Login(client, email);
        await Login(client, email);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(Prefix + "/logout", new { })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", first.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/refresh",
            new { first.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task PasswordResetIsSingleUseAndRevokesExistingTokens()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        var tokens = await Login(client, email);
        client.DefaultRequestHeaders.Authorization = null;
        await client.PostAsJsonAsync(Prefix + "/forgotPassword", new { email });
        var resetCode = fixture.Email.Resets[email];
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Prefix + "/resetPassword",
            new { email, resetCode = "invalid", newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Prefix + "/resetPassword",
            new { email, resetCode, newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Prefix + "/resetPassword",
            new { email, resetCode, newPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/refresh",
            new { tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/login",
            new { email, password = Password })).StatusCode);
        await Login(client, email, NewPassword);
    }

    [Fact]
    public async Task AccountChangesRequireReauthenticationAndOldPassword()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        await Login(client, email);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Prefix + "/manage/info",
            new { oldPassword = Password, newPassword = NewPassword })).StatusCode);
        await Reauthenticate(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Prefix + "/manage/info",
            new { oldPassword = "wrong", newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Prefix + "/manage/info",
            new { oldPassword = Password, newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
    }

    [Fact]
    public async Task RepeatedWrongPasswordsLockTheAccount()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/login",
                new { email, password = "wrong password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/login",
            new { email, password = Password })).StatusCode);
    }

    [Fact]
    public async Task MfaRequiresAuthenticatorOrSingleUseRecoveryCode()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        await Login(client, email);
        await Reauthenticate(client);
        var setup = await client.PostAsJsonAsync(Prefix + "/manage/2fa", new { });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        // Refresh credentials if initial key generation changed the security stamp.
        client.DefaultRequestHeaders.Authorization = null;
        await Login(client, email);
        await Reauthenticate(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Prefix + "/manage/2fa",
            new { enable = true, twoFactorCode = "invalid" })).StatusCode);
        var enabled = await client.PostAsJsonAsync(Prefix + "/manage/2fa",
            new { enable = true, twoFactorCode = Totp(key) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var codes = (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes")
            .EnumerateArray().Select(v => v.GetString()!).ToArray();
        Assert.Equal(10, codes.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/login",
            new { email, password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/login",
            new { email, password = Password, twoFactorCode = "invalid" })).StatusCode);
        await Login(client, email, twoFactorCode: Totp(key));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Prefix + "/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        await Login(client, email, twoFactorRecoveryCode: codes[0]);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/login",
            new { email, password = Password, twoFactorRecoveryCode = codes[0] })).StatusCode);
        await Login(client, email, twoFactorCode: Totp(key));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Prefix + "/manage/2fa",
            new { enable = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/reauthenticate",
            new { password = Password })).StatusCode);
        await Reauthenticate(client, twoFactorCode: Totp(key));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Prefix + "/manage/2fa",
            new { enable = false })).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        await Login(client, email);
    }

    [Fact]
    public async Task InvalidExpiredAndWrongPurposeTokensAreRejected()
    {
        using var client = fixture.Client();
        var tokens = await Login(client, await Register(client));
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        foreach (var token in new[] { "invalid", tokens.AccessToken })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/refresh",
                new { refreshToken = token })).StatusCode);
        var options = fixture.Factory.Services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>()
            .Get(IdentityConstants.BearerScheme);
        var ticket = options.RefreshTokenProtector.Unprotect(tokens.RefreshToken)!;
        ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Prefix + "/refresh",
            new { refreshToken = options.RefreshTokenProtector.Protect(ticket) })).StatusCode);
        ticket = options.BearerTokenProtector.Unprotect(tokens.AccessToken)!;
        ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        client.DefaultRequestHeaders.Authorization = new("Bearer", options.BearerTokenProtector.Protect(ticket));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
    }

    [Fact]
    public async Task CookieLoginIsRejected()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Prefix + "/login?useCookies=true",
            new { email, password = Password })).StatusCode);
    }

    [Fact]
    public async Task ReauthenticationProofCannotBeUsedForAnotherAccount()
    {
        using var first = fixture.Client();
        using var second = fixture.Client();
        await Login(first, await Register(first));
        await Reauthenticate(first);
        await Login(second, await Register(second));
        second.DefaultRequestHeaders.Add("X-Reauthentication-Token",
            first.DefaultRequestHeaders.GetValues("X-Reauthentication-Token").Single());
        Assert.Equal(HttpStatusCode.Forbidden, (await second.PostAsJsonAsync(Prefix + "/manage/2fa", new { })).StatusCode);
    }

    [Fact]
    public async Task EmailChangeRequiresProofAndConfirmationThenRevokesOldTokens()
    {
        using var client = fixture.Client();
        var email = await Register(client);
        var tokens = await Login(client, email);
        await Reauthenticate(client);
        var newEmail = $"{Guid.NewGuid():N}@example.test";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Prefix + "/manage/info", new { newEmail })).StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>(Prefix + "/me");
        Assert.Equal(email, me.GetProperty("email").GetString());
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(fixture.Email.Confirmations[newEmail]).PathAndQuery)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Prefix + "/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        await Login(client, newEmail);
    }

    [Fact]
    public async Task ExpiredConfirmationAndResetCodesAreRejected()
    {
        using var client = fixture.Client();
        var email = await Register(client, confirm: false);
        // Exercise expiry without waiting an hour. Tests in this class run serially.
        var options = fixture.Factory.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;
        var originalLifetime = options.TokenLifespan;
        try
        {
            options.TokenLifespan = TimeSpan.FromSeconds(-1);
            var expired = await client.GetAsync(new Uri(fixture.Email.Confirmations[email]).PathAndQuery);
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
            Assert.Equal("application/problem+json", expired.Content.Headers.ContentType?.MediaType);
        }
        finally { options.TokenLifespan = originalLifetime; }
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(new Uri(fixture.Email.Confirmations[email]).PathAndQuery)).StatusCode);
        await client.PostAsJsonAsync(Prefix + "/forgotPassword", new { email });
        try
        {
            options.TokenLifespan = TimeSpan.FromSeconds(-1);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Prefix + "/resetPassword",
                new { email, resetCode = fixture.Email.Resets[email], newPassword = NewPassword })).StatusCode);
        }
        finally { options.TokenLifespan = originalLifetime; }
    }

    [Fact]
    public async Task RateLimitReturns429AndRetryAfter()
    {
        await using var factory = new AuthFactory(fixture.Connection, fixture.Email, limit: 2);
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Prefix + "/forgotPassword",
                new { email = "nobody@example.test" })).StatusCode);
        var response = await client.PostAsJsonAsync(Prefix + "/forgotPassword", new { email = "nobody@example.test" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReauthenticationRejectsOversizedMfaInput(bool authenticatorCode)
    {
        using var client = fixture.Client();
        await Login(client, await Register(client));
        var response = await client.PostAsJsonAsync(Prefix + "/reauthenticate", new
        {
            password = Password,
            twoFactorCode = authenticatorCode ? new string('1', 33) : null,
            twoFactorRecoveryCode = authenticatorCode ? null : new string('a', 129)
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // Test-only RFC 6238 calculation; production TOTP validation is provided by Identity.
    private static string Totp(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(key.Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var bytes = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(bytes, counter);
        var offset = hash[^1] & 15;
        var value = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1000000).ToString("D6");
    }

    private sealed record Tokens(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
}

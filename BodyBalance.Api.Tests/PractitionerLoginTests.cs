using Xunit;

namespace BodyBalance.Api.Tests;

public class PractitionerLoginTests
{
    private const string Key = "a-test-only-signing-key-at-least-32-bytes";

    [Fact]
    public void CodeHashBindsTheCodeToTheAccountAndServerKey()
    {
        var email = PractitionerLogin.NormalizeEmail(" Mary@Example.com ");
        Assert.Equal("mary@example.com", email);
        var hash = PractitionerLogin.CodeHash(email, "012345", Key);
        Assert.Equal(hash, PractitionerLogin.CodeHash(email, "012345", Key));
        Assert.NotEqual(hash, PractitionerLogin.CodeHash("leslie@example.com", "012345", Key));
        Assert.NotEqual(hash, PractitionerLogin.CodeHash(email, "012346", Key));
        Assert.NotEqual(hash, PractitionerLogin.CodeHash(email, "012345", Key + "changed"));
        Assert.Throws<InvalidOperationException>(() => PractitionerLogin.CodeHash(email, "012345", "short"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("123 45")]
    [InlineData("１２３４５６")]
    public void RejectsMalformedCodes(string? value) => Assert.False(PractitionerLogin.ValidCode(value));

    [Fact]
    public void CodesPreserveLeadingZerosAndSessionsHaveIndependentRandomTokens()
    {
        Assert.True(PractitionerLogin.ValidCode("000001"));
        Assert.True(PractitionerLogin.ValidCode(PractitionerLogin.CreateCode()));
        var first = PractitionerLogin.CreateSession();
        var second = PractitionerLogin.CreateSession();
        Assert.True(PractitionerLogin.ValidSession(first));
        Assert.NotEqual(first, second);
        Assert.NotEqual(PractitionerLogin.SessionHash(first), PractitionerLogin.SessionHash(second));
    }

    [Fact]
    public void CookieParsingRejectsAmbiguousOrMalformedSessions()
    {
        var token = PractitionerLogin.CreateSession();
        var cookie = $"{PractitionerLogin.CookieName}={token}";
        Assert.Equal(token, PractitionerLogin.ReadSession($"other=value; {cookie}; unrelated=yes"));
        Assert.Null(PractitionerLogin.ReadSession($"{cookie}; {cookie}"));
        Assert.Null(PractitionerLogin.ReadSession($"prefix{cookie}"));
        Assert.Null(PractitionerLogin.ReadSession($"{PractitionerLogin.CookieName}=not-a-session"));
        Assert.Null(PractitionerLogin.ReadSession(null));
    }

    [Fact]
    public void SessionCookieIsHostBoundSecureAndUnavailableToJavascript()
    {
        var cookie = PractitionerLogin.SessionCookie(PractitionerLogin.CreateSession());
        Assert.StartsWith("__Host-", cookie);
        Assert.Contains("Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age=28800", cookie);
        Assert.DoesNotContain("Domain=", cookie);
        Assert.EndsWith("Max-Age=0", PractitionerLogin.SessionCookie("", clear: true));
    }

    [Theory]
    [InlineData(false, 8, 28800)]
    [InlineData(true, 720, 2592000)]
    public void RememberMeUsesTheSameBoundedLifetimeForSqlAndCookie(bool rememberMe, int hours, int seconds)
    {
        Assert.Equal(hours, PractitionerLogin.SessionHoursFor(rememberMe));
        var cookie = PractitionerLogin.SessionCookie(PractitionerLogin.CreateSession(), rememberMe: rememberMe);
        Assert.EndsWith($"Max-Age={seconds}", cookie);
        Assert.Contains("Secure; HttpOnly; SameSite=Strict", cookie);
        Assert.EndsWith("Max-Age=0", PractitionerLogin.SessionCookie("", clear: true, rememberMe: rememberMe));
    }

    [Fact]
    public void OlderClientsDoNotOptIntoLongerSessions()
    {
        var input = System.Text.Json.JsonSerializer.Deserialize<PractitionerLoginRequest>(
            "{\"email\":\"mary@example.com\",\"code\":\"123456\"}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(input);
        Assert.False(input.RememberMe);
    }
}

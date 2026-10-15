using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace BodyBalance.Api.Tests;

public class ConversationMailTests
{
    [Fact]
    public void SignatureBindsBodyRecipientAndTimeAndRejectsStaleReplays()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var raw = Encoding.UTF8.GetBytes("From: client@example.com\r\n\r\nHello");
        const string secret = "test-only-secret-with-at-least-32-characters";
        const string recipient = "hello@example.com";
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signed = Encoding.UTF8.GetBytes($"{timestamp}\n{recipient}\n{Convert.ToHexStringLower(SHA256.HashData(raw))}");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed));
        Assert.True(ConversationMail.VerifySignature(secret, timestamp, recipient, signature, raw, now));
        Assert.False(ConversationMail.VerifySignature(secret, timestamp, "other@example.com", signature, raw, now));
        Assert.False(ConversationMail.VerifySignature(secret, timestamp, recipient, signature, [1, 2, 3], now));
        Assert.False(ConversationMail.VerifySignature(secret, timestamp, recipient, signature, raw, now.AddMinutes(6)));
        Assert.False(ConversationMail.VerifySignature(secret, timestamp, recipient, signature, raw, now.AddMinutes(-6)));
        Assert.False(ConversationMail.VerifySignature(secret, timestamp, recipient, new string('z', 64), raw, now));
        Assert.False(ConversationMail.VerifySignature(null, timestamp, recipient, signature, raw, now));
    }

    [Fact]
    public async Task ParsesEncodedTextReplyTargetAndConversationToken()
    {
        var id = Guid.NewGuid();
        var raw = Encoding.UTF8.GetBytes($"From: Client <client@example.com>\r\nReply-To: reply@example.com\r\nSubject: Re: Website inquiry [EW:{id:N}]\r\nMessage-ID: <incoming@example.com>\r\nIn-Reply-To: <earlier@example.com>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\nSGVsbG8gTWFyeSE=\r\n");
        var message = await ConversationMail.ParseAsync(raw, default);
        Assert.Equal("Hello Mary!", message.Body.Trim());
        Assert.Equal("reply@example.com", message.Email);
        Assert.Equal("client@example.com", message.From);
        Assert.Equal(id, message.ConversationId);
        Assert.DoesNotContain("[EW:", message.Subject);
        Assert.Equal("incoming@example.com", message.InternetMessageId);
        Assert.Contains("earlier@example.com", message.References);
    }

    [Fact]
    public async Task HtmlOnlyMessagesProduceTextWithoutExecutableMarkup()
    {
        var raw = Encoding.UTF8.GetBytes("From: client@example.com\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Hello &amp; welcome</p><script>alert('bad')</script><img src='https://tracker.example/test'>");
        var message = await ConversationMail.ParseAsync(raw, default);
        Assert.Contains("Hello & welcome", message.Body);
        Assert.DoesNotContain("<script", message.Body);
        Assert.DoesNotContain("tracker.example", message.Body);
    }

    [Fact]
    public async Task AmbiguousSenderCannotBecomeAutomaticReplyTarget()
    {
        var raw = Encoding.UTF8.GetBytes("From: one@example.com, two@example.com\r\n\r\nHello");
        Assert.Equal("", (await ConversationMail.ParseAsync(raw, default)).Email);
    }

    [Fact]
    public void RepliesAreBoundedAndRequireStableRequestIdentity()
    {
        Assert.Throws<BookingException>(() => ConversationMail.Validate(new(Guid.Empty, "Hello")));
        Assert.Throws<BookingException>(() => ConversationMail.Validate(new(Guid.NewGuid(), " ")));
        Assert.Throws<BookingException>(() => ConversationMail.Validate(new(Guid.NewGuid(), new string('a', 10001))));
        var id = Guid.NewGuid();
        Assert.Equal(new(id, "Hello"), ConversationMail.Validate(new(id, " Hello ")));
    }
}

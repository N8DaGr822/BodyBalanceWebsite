using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Text;

namespace BodyBalance.Api;

public sealed record MailConversation(Guid ConversationId, string CustomerName, string CustomerEmail, string Subject, DateTime UpdatedAtUtc, bool IsTrashed = false);
public sealed record MailEntry(Guid MessageId, string Direction, string Sender, string Body, DateTime CreatedAtUtc,
    string State, string? ProviderId, bool HasOriginal, string? Actor);
public sealed record MailReply(Guid MessageId, string? Body);
public sealed record IncomingMail(string Name, string Email, string Subject, string Body, string? InternetMessageId, Guid? ConversationId, string? From, string[] References);

public static class ConversationMail
{
    public const int MaximumBytes = 25 * 1024 * 1024;
    public const int MaximumText = 100000;

    public static bool VerifySignature(string? secret, string? timestamp, string? recipient, string? signature, byte[] raw, DateTimeOffset now)
    {
        if (secret is null || Encoding.UTF8.GetByteCount(secret) < 32 || timestamp is null || recipient is null
            || signature is null || signature.Length != 64 || !long.TryParse(timestamp, out var seconds)
            || seconds < now.ToUnixTimeSeconds() - 300 || seconds > now.ToUnixTimeSeconds() + 300) return false;
        var signed = Encoding.UTF8.GetBytes($"{timestamp}\n{recipient}\n{Convert.ToHexStringLower(SHA256.HashData(raw))}");
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed), Convert.FromHexString(signature)); }
        catch (FormatException) { return false; }
    }

    public static MailReply Validate(MailReply reply)
    {
        var text = reply.Body?.Trim();
        if (reply.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(text) || text.Length > 10000)
            throw new BookingException("Enter a reply of 1–10,000 characters.");
        return reply with { Body = text };
    }

    public static async Task<IncomingMail> ParseAsync(byte[] raw, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(raw, writable: false);
        using var message = await MimeMessage.LoadAsync(stream, cancellationToken);
        var from = message.From.Mailboxes.ToArray();
        var replyTo = message.ReplyTo.Mailboxes.ToArray();
        // Reply Safety: An ambiguous sender is retained for review, but cannot become a send target.
        var address = replyTo.Length == 1 ? replyTo[0] : from.Length == 1 && replyTo.Length == 0 ? from[0] : null;
        var email = address?.Address ?? "";
        if (email.Length > 254 || email.Contains('\r') || email.Contains('\n')) email = "";
        var subject = message.Subject ?? "(No subject)";
        var match = Regex.Match(subject, @"\[EW:([0-9a-fA-F]{32})\]", RegexOptions.None, TimeSpan.FromSeconds(1));
        Guid? conversationId = match.Success ? Guid.ParseExact(match.Groups[1].Value, "N") : null;
        subject = Regex.Replace(subject, @"\s*\[EW:[0-9a-fA-F]{32}\]", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        subject = subject.Replace('\r', ' ').Replace('\n', ' ');
        var body = message.TextBody;
        if (body is null && message.HtmlBody is { } html)
            body = HtmlText(html);
        body ??= "No readable text body. Download the original email to view its contents.";
        if (body.Length > MaximumText) body = body[..MaximumText] + "\n[Display shortened; download the original email for the complete message.]";
        if (message.Attachments.Any()) body += "\n\n[Attachments included in the original email download.]";
        var id = message.MessageId;
        if (id is not null && (id.Length > 998 || id.Contains('\r') || id.Contains('\n'))) id = null;
        var name = string.IsNullOrWhiteSpace(address?.Name) ? (email.Length > 0 ? email : "Email sender") : address.Name;
        return new(name[..Math.Min(name.Length, 150)], email,
            subject[..Math.Min(subject.Length, 300)], body, id, conversationId, from.Length == 1 ? from[0].Address : null,
            message.References.Append(message.InReplyTo ?? "").Where(value => value.Length is > 0 and <= 998 && !value.Contains('\r') && !value.Contains('\n')).TakeLast(20).ToArray());
    }

    private static string HtmlText(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var text = new StringBuilder();
        string? hidden = null;
        // Email Display: Extract text only; never render sender-controlled HTML or fetch remote resources.
        while (text.Length <= MaximumText && tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                var name = tag.Name.ToLowerInvariant();
                if (hidden is not null) { if (tag.IsEndTag && name == hidden) hidden = null; continue; }
                if (!tag.IsEndTag && name is "script" or "style" or "head") hidden = name;
                else if (name is "p" or "div" or "br" or "li" or "tr") text.AppendLine();
            }
            else if (hidden is null && token.Kind == HtmlTokenKind.Data && token is HtmlDataToken data)
                text.Append(data.Data.AsSpan(0, Math.Min(data.Data.Length, MaximumText + 1 - text.Length)));
        }
        return text.ToString().Trim();
    }
}

using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace BodyBalance.Api;

public sealed record PractitionerIdentity(string IdentityProvider, string UserId, string UserDetails, string[] UserRoles);

public static class PractitionerAccess
{
    // Identity Boundary: This header is trusted only behind Static Web Apps' managed API gateway.
    public static PractitionerIdentity? ReadIdentity(string? encodedPrincipal)
    {
        if (string.IsNullOrEmpty(encodedPrincipal) || encodedPrincipal.Length > 16384) return null;
        try
        {
            var identity = JsonSerializer.Deserialize<PractitionerIdentity>(Convert.FromBase64String(encodedPrincipal),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return identity is { IdentityProvider: "aad" or "github", UserId.Length: > 0 and <= 128 }
                && identity.UserId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                && identity.UserRoles?.Contains("authenticated", StringComparer.Ordinal) == true ? identity : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException) { return null; }
    }

    public static int[] AllowedPractitioners(PractitionerIdentity identity, IConfiguration configuration) =>
        (configuration[$"PractitionerAccess:{identity.IdentityProvider}:{identity.UserId}"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0).Where(id => id > 0).Distinct().Take(100).ToArray();
}

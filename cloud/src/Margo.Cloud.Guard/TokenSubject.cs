using System.Text.Json;

namespace Margo.Cloud.Guard;

/// <summary>
/// Whose token the guard is about to present as the principal's. Not in Python: added in the security review so a
/// token exchanged for the wrong person (a wrong sender, a misconfigured handler) is refused rather than read and
/// labelled as the principal's data.
/// </summary>
/// <remarks>
/// The token is never verified here (Work IQ does that) and its contents are never recorded. A compact JWS (three
/// segments) is readable: its payload must be a JSON object whose <c>oid</c> is the bound principal's directory object
/// ID, compared without case (object IDs are GUIDs). Any other shape (an opaque or encrypted token) cannot be read and
/// is passed on unchanged, as before.
/// </remarks>
internal static class TokenSubject
{
    /// <summary>False when the token is readable and does not name <paramref name="principalId"/> as its subject.</summary>
    internal static bool MayBe(string token, string principalId)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return true;
        }

        try
        {
            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return payload.RootElement.ValueKind == JsonValueKind.Object
                   && payload.RootElement.TryGetProperty("oid", out var oid)
                   && oid.ValueKind == JsonValueKind.String
                   && string.Equals(oid.GetString(), principalId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exc) when (exc is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var text = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '='));
    }
}

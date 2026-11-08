using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Margo.Cloud.Host.Authentication;
using Microsoft.AspNetCore.Http;

namespace Margo.Cloud.Host;

/// <summary>
/// The inbound token check (Python: the <c>jwt</c> middleware of <c>web_app()</c> around the Agents SDK's
/// <c>jwt_authorization_middleware</c>). There is no platform edge in front of this process: every request's token is
/// checked here against the blueprint (behind the router, a second time), except the health paths and the voice page,
/// which authenticates the person with their own sign-in.
/// </summary>
public sealed class InboundAuthentication(RequestDelegate next, AgentTokenValidator validator)
{
    /// <summary>Whether <paramref name="path"/> is served without a platform token.</summary>
    public static bool IsOpen(string path) =>
        HostSettings.HealthPaths.Contains(path) || path == "/voice" || path.StartsWith("/voice/", StringComparison.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsOpen(context.Request.Path.Value ?? string.Empty))
        {
            await next(context);
            return;
        }

        var headers = context.Request.Headers.Authorization;
        var result = await validator.AuthorizeAsync(headers.Count == 0 ? null : headers[0], context.RequestAborted);
        if (!result.Succeeded)
        {
            context.Response.StatusCode = result.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(result.ErrorJson!), context.RequestAborted);
            return;
        }

        // request["claims_identity"]: the SDK reads the turn's identity (audience, app ID) from HttpContext.User.
        context.User = new ClaimsPrincipal(Identity(result.Claims!));
        await next(context);
    }

    /// <summary>The verified token's claims as an authenticated identity: one claim per value, arrays flattened.</summary>
    public static ClaimsIdentity Identity(JsonObject claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var list = new List<Claim>();
        foreach (var (name, value) in claims)
        {
            if (value is JsonArray array)
            {
                list.AddRange(array.Select(item => new Claim(name, Text(item))));
            }
            else
            {
                list.Add(new Claim(name, Text(value)));
            }
        }

        return new ClaimsIdentity(list, "Bearer");
    }

    private static string Text(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => scalar.GetValue<string>(),
        _ => value.ToJsonString(),
    };
}

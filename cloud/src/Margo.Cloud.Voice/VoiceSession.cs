using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Voice;

/// <summary>
/// One voice session, independent of transport (<c>voice/session.py</c>): configuration, events, tools and identity
/// checks.
/// </summary>
/// <remarks>
/// Voice is another way into the same Margo. The session's instructions are the same persona, skill index and
/// not-hosted list as a Teams turn, plus a short note on speaking. Its tools are the same function tools, and every
/// call is executed by the engine's own dispatcher, so it passes the same guard, state tool and budgets. Nothing is
/// approved or sent by voice: the guard refuses writes and the state tool keeps approvals for cards. No audio is
/// stored, and nothing here keeps a transcript; captions go to the person's own page only.
/// </remarks>
public static partial class VoiceSession
{
    /// <summary><c>VOICE_ADDENDUM</c>: appended to Margo's instructions for a spoken session.</summary>
    public const string Addendum = """
        # Speaking

        This conversation is spoken. Your manager hears you through a synthetic voice, and you hear them
        through speech recognition, which can mishear.
        - Begin your first reply by saying, in a few words, that you are Margo, an AI assistant.
        - Speak in short sentences. No tables, Markdown, links read aloud or emoji. Say times and dates the
          way people say them, in your manager's time zone.
        - Before a lookup that takes time, say in a few words what you are checking.
        - Nothing is approved or sent by voice. When something needs approval, say exactly what you would
          do and that the approval happens in Teams.
        - If you are unsure what you heard, or who is speaking, ask before acting on it.

        """;

    /// <summary><c>DISCLOSURE</c>: shown on the page when the session is ready.</summary>
    public const string Disclosure = "You're talking to Margo, an AI assistant. Her voice is synthetic. Audio is not recorded.";

    /// <summary><c>DEFAULT_VOICE</c>.</summary>
    public const string DefaultVoice = "en-US-Ava:DragonHDLatestNeural";

    /// <summary><c>MAX_AUDIO_CHARS</c>: base64 of about 1 s of 24 kHz PCM16 mono; frames are 20 ms.</summary>
    public const int MaxAudioChars = 64 * 1024;

    /// <summary><c>LOCKED_TOOLS</c>: the tools that touch Margo's state and so run under the state lease.</summary>
    public static IReadOnlySet<string> LockedTools { get; } = new HashSet<string>(StringComparer.Ordinal) { "margo-state", "office_requests" };

    /// <summary><c>PROGRESS</c>: what the page shows while a tool runs, by name prefix, in order.</summary>
    public static IReadOnlyList<(string Prefix, string Text)> Progress { get; } =
    [
        ("workiq-", "Checking Microsoft 365..."),
        ("margo-state", "Checking my notes..."),
        ("office_requests", "Checking requests to the office..."),
    ];

    /// <summary>
    /// <c>session_update(instructions, tools, settings)</c>: the Voice Live <c>session.update</c> event: Margo's
    /// instructions and tools, her voice, and the service-side noise suppression, echo cancellation and semantic turn
    /// detection.
    /// </summary>
    /// <param name="instructions">The session's instructions.</param>
    /// <param name="tools">The engine's function tools (objects with <c>name</c>, optional <c>description</c> and <c>parameters</c>).</param>
    /// <param name="settings">The voice settings.</param>
    public static JsonObject SessionUpdate(string instructions, JsonArray tools, VoiceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(settings);
        var functions = new JsonArray();
        foreach (var node in tools)
        {
            var tool = node as JsonObject ?? throw new PyTypeError("tool definitions must be objects");
            if (!tool.ContainsKey("name"))
            {
                throw new PyKeyError("name");
            }

            functions.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = PyNode.Copy(tool["name"]),
                ["description"] = tool.ContainsKey("description") ? PyNode.Copy(tool["description"]) : "",
                ["parameters"] = tool.ContainsKey("parameters")
                    ? PyNode.Copy(tool["parameters"])
                    : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            });
        }

        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["modalities"] = new JsonArray("text", "audio"),
                ["instructions"] = instructions,
                ["voice"] = settings.Voice(),
                ["input_audio_format"] = "pcm16",
                ["output_audio_format"] = "pcm16",
                ["turn_detection"] = new JsonObject { ["type"] = "azure_semantic_vad" },
                ["input_audio_noise_reduction"] = new JsonObject { ["type"] = "azure_deep_noise_suppression" },
                ["input_audio_echo_cancellation"] = new JsonObject { ["type"] = "server_echo_cancellation" },
                ["input_audio_transcription"] = new JsonObject { ["model"] = "azure-speech" },
                ["tools"] = functions,
                ["tool_choice"] = "auto",
            },
        };
    }

    /// <summary><c>_progress(name)</c>: the status text for a tool, or empty.</summary>
    public static string ProgressFor(JsonNode? name)
    {
        if (PyNode.AsString(name) is not { } text)
        {
            return "";
        }

        foreach (var (prefix, status) in Progress)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return status;
            }
        }

        return "";
    }

    /// <summary><c>base64.b64decode(audio, validate=True)</c> succeeds: only the standard alphabet, whole quads, and
    /// <c>=</c> padding only to complete the last one (checked against CPython 3.13 for every string of up to eight
    /// characters over <c>A</c>, <c>=</c> and a newline). A non-ASCII string raises <c>ValueError</c> there.</summary>
    internal static bool IsStrictBase64(string text) => StrictBase64().IsMatch(text);

    [GeneratedRegex(@"\A(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrictBase64();
}

/// <summary><c>VoiceSettings</c>: the Voice Live model, Margo's voice and the session limits.</summary>
/// <param name="Model">The realtime model deployment (<c>VOICE_LIVE_MODEL</c>).</param>
/// <param name="VoiceName">The voice (<c>MARGO_VOICE_NAME</c>).</param>
/// <param name="VoiceEndpointId">A custom voice's deployment ID (<c>MARGO_VOICE_ENDPOINT_ID</c>), or empty.</param>
/// <param name="MaxSeconds">The longest call (<c>MARGO_VOICE_MAX_SECONDS</c>).</param>
/// <param name="MaxToolCalls">Tool calls allowed per call (<c>MARGO_VOICE_MAX_TOOL_CALLS</c>).</param>
public sealed record VoiceSettings(string Model, string VoiceName = VoiceSession.DefaultVoice, string VoiceEndpointId = "",
                                   long MaxSeconds = 1800, long MaxToolCalls = 60)
{
    /// <summary>
    /// <c>VoiceSettings.from_env(env)</c>. Values are stripped (Python <c>str.strip</c>); the limits are read with
    /// Python's <c>int()</c> and are not stripped of anything else (an empty value is an error).
    /// </summary>
    /// <param name="environment">The variables; null reads the process environment.</param>
    /// <exception cref="PyValueError"><c>VOICE_LIVE_MODEL</c> is missing or blank ("VOICE_LIVE_MODEL is required for
    /// voice"), or a limit is not an integer (Python's <c>int()</c> message).</exception>
    public static VoiceSettings FromEnv(IReadOnlyDictionary<string, string>? environment = null)
    {
        string? Get(string name) => environment is null ? PyRuntime.GetEnv(name)
            : environment.TryGetValue(name, out var value) ? value : null;

        var model = PyRepr.Strip(Get("VOICE_LIVE_MODEL") ?? "");
        if (model.Length == 0)
        {
            throw new PyValueError("VOICE_LIVE_MODEL is required for voice");
        }

        var name = PyRepr.Strip(Get("MARGO_VOICE_NAME") ?? "");
        return new VoiceSettings(model, name.Length > 0 ? name : VoiceSession.DefaultVoice,
                                 PyRepr.Strip(Get("MARGO_VOICE_ENDPOINT_ID") ?? ""),
                                 Saturate(PyArgumentParser.ParsePyInt(Get("MARGO_VOICE_MAX_SECONDS") ?? "1800")),
                                 Saturate(PyArgumentParser.ParsePyInt(Get("MARGO_VOICE_MAX_TOOL_CALLS") ?? "60")));
    }

    /// <summary><c>voice()</c>: her trained custom voice when a deployment is configured (on the same Azure AI
    /// Services account), else the standard voice.</summary>
    public JsonObject Voice() => VoiceEndpointId.Length > 0
        ? new JsonObject { ["type"] = "azure-custom", ["name"] = VoiceName, ["endpoint_id"] = VoiceEndpointId }
        : new JsonObject { ["type"] = "azure-standard", ["name"] = VoiceName };

    private static long Saturate(BigInteger value) =>
        value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : (long)value;
}

/// <summary><c>TokenRejected</c>: the person's sign-in does not authorise a voice session with this Margo.</summary>
public sealed class TokenRejectedException : Exception
{
    public TokenRejectedException()
    {
    }

    public TokenRejectedException(string message)
        : base(message)
    {
    }

    public TokenRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary><c>check_claims</c>: what a validated token's claims must say for a voice session.</summary>
public static class VoiceClaims
{
    /// <summary>
    /// <c>check_claims(claims, tenant_id=, client_id=, scope=, now=)</c>: the caller's object ID if the claims allow a
    /// voice session. The signature, issuer and expiry are checked first (<see cref="Jwt.EntraTokenVerifier"/>); this checks
    /// what the claims mean: the right tenant, audience and delegated scope, and a user (not an app) caller.
    /// </summary>
    /// <param name="claims">The token's payload.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="clientId">The blueprint's application ID.</param>
    /// <param name="scope">The delegated voice scope.</param>
    /// <param name="now">Seconds since the epoch; null for the current time.</param>
    /// <exception cref="TokenRejectedException">With Python's reason.</exception>
    public static string Check(JsonNode? claims, string tenantId, string clientId, string scope, double? now = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(scope);
        var time = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (claims is not JsonObject values)
        {
            throw new TokenRejectedException("no claims");
        }

        if (!PyNode.Is(PyNode.Get(values, "tid"), tenantId))
        {
            throw new TokenRejectedException("token is from another tenant");
        }

        if (!PyNode.IsOneOf(PyNode.Get(values, "aud"), clientId, "api://" + clientId))
        {
            throw new TokenRejectedException("token is for another application");
        }

        var scopes = values.ContainsKey("scp") ? PyNode.Str(values["scp"]) : "";
        if (!SplitWhitespace(scopes).Contains(scope, StringComparer.Ordinal))
        {
            throw new TokenRejectedException("token lacks the voice scope");
        }

        var exp = PyNode.Get(values, "exp");
        if (!PyNode.IsNumber(exp) || AtOrBefore(exp!, time))
        {
            throw new TokenRejectedException("token has expired");
        }

        var oid = PyNode.AsString(PyNode.Get(values, "oid"));
        if (string.IsNullOrEmpty(oid) || PyNode.Is(PyNode.Get(values, "idtyp"), "app"))
        {
            throw new TokenRejectedException("token does not identify a person");
        }

        return oid;
    }

    /// <summary>Python <c>str.split()</c>: runs of whitespace (<c>str.isspace</c>) separate the words.</summary>
    internal static List<string> SplitWhitespace(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var index = 0; index < text.Length;)
        {
            var codePoint = PyText.CodePointAt(text, index, out var length);
            if (PyText.IsSpace(codePoint))
            {
                if (start >= 0)
                {
                    words.Add(text[start..index]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = index;
            }

            index += length;
        }

        if (start >= 0)
        {
            words.Add(text[start..]);
        }

        return words;
    }

    /// <summary>Python <c>value &lt;= now</c> for a number (an int, float or bool), compared exactly.</summary>
    private static bool AtOrBefore(JsonNode value, double now)
    {
        if (PyJson.KindOf(value) == PyKind.Float)
        {
            return PyJson.AsDouble(value) <= now;
        }

        var integer = PyJson.IsBool(value) ? (PyJson.Truthy(value) ? BigInteger.One : BigInteger.Zero) : PyJson.AsInt(value);
        return !double.IsNaN(now) && PyNumbers.CompareIntFloat(integer, now) <= 0;
    }
}

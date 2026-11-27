using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Voice;

/// <summary>
/// <c>VoiceBridge</c>: moves events between the person's page and Voice Live, and runs Margo's tools.
/// </summary>
/// <remarks>
/// <para><c>dispatch(name, raw_arguments)</c> is the engine's dispatcher bound to this session's guard, state and skills.
/// <c>run_locked(work)</c> runs a call that touches Margo's state under the state lease. <c>clip(result)</c> is the
/// engine's output limiter.</para>
/// <para>The service side (<see cref="FromServiceAsync"/>) is called by one loop and the page side
/// (<see cref="FromClientAsync"/>) by another, concurrently; the only state they share is whether the session is
/// ready.</para>
/// </remarks>
public sealed class VoiceBridge
{
    private readonly Func<JsonNode?, JsonNode?, CancellationToken, Task<JsonNode?>> _dispatch;
    private readonly Func<JsonObject, CancellationToken, Task> _up;
    private readonly Func<JsonObject, CancellationToken, Task> _down;
    private readonly Func<JsonNode?, string> _clip;
    private readonly Func<Func<CancellationToken, Task<JsonNode?>>, CancellationToken, Task<JsonNode?>>? _runLocked;
    private readonly Func<double> _clock;
    private readonly double _started;
    private volatile bool _ready;
    private bool _outputsPending;

    /// <param name="dispatch">Runs one function call: the name and raw arguments exactly as Voice Live sent them.</param>
    /// <param name="upstream">Sends an event to Voice Live.</param>
    /// <param name="downstream">Sends a message to the person's page.</param>
    /// <param name="settings">The voice settings (the tool budget and the time limit).</param>
    /// <param name="clip">The engine's output limiter (<c>EngineTools.Clip</c>).</param>
    /// <param name="runLocked">Runs a state call under the state lease, or null.</param>
    /// <param name="clock">Monotonic seconds (Python <c>time.monotonic</c>); null for the system clock.</param>
    public VoiceBridge(Func<JsonNode?, JsonNode?, CancellationToken, Task<JsonNode?>> dispatch,
                       Func<JsonObject, CancellationToken, Task> upstream, Func<JsonObject, CancellationToken, Task> downstream,
                       VoiceSettings settings, Func<JsonNode?, string> clip,
                       Func<Func<CancellationToken, Task<JsonNode?>>, CancellationToken, Task<JsonNode?>>? runLocked = null,
                       Func<double>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(downstream);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clip);
        _dispatch = dispatch;
        _up = upstream;
        _down = downstream;
        Settings = settings;
        _clip = clip;
        _runLocked = runLocked;
        _clock = clock ?? (() => TimeProvider.System.GetTimestamp() / (double)TimeProvider.System.TimestampFrequency);
        _started = _clock();
    }

    /// <summary>The voice settings.</summary>
    public VoiceSettings Settings { get; }

    /// <summary>Tool calls made so far (budget refusals included).</summary>
    public long ToolCalls { get; private set; }

    /// <summary><c>start(instructions, tools)</c>: configures the Voice Live session.</summary>
    public Task StartAsync(string instructions, JsonArray tools, CancellationToken cancellationToken = default) =>
        _up(VoiceSession.SessionUpdate(instructions, tools, Settings), cancellationToken);

    /// <summary><c>expired()</c>: more than <see cref="VoiceSettings.MaxSeconds"/> since the bridge was made.</summary>
    public bool Expired() => _clock() - _started > Settings.MaxSeconds;

    /// <summary><c>from_client(message)</c>: one message from the page. Returns <c>"stop"</c> when the person ends the
    /// call. Audio is passed on only once the session is ready, and only valid base64 of at most
    /// <see cref="VoiceSession.MaxAudioChars"/> characters.</summary>
    public async Task<string?> FromClientAsync(JsonNode? message, CancellationToken cancellationToken = default)
    {
        if (message is not JsonObject values)
        {
            return null;
        }

        var kind = PyNode.Get(values, "type");
        if (PyNode.Is(kind, "stop"))
        {
            return "stop";
        }

        if (PyNode.Is(kind, "audio") && _ready)
        {
            var audio = PyNode.AsString(PyNode.Get(values, "audio"));
            if (string.IsNullOrEmpty(audio) || PyRepr.Length(audio) > VoiceSession.MaxAudioChars || !VoiceSession.IsStrictBase64(audio))
            {
                return null;
            }

            await _up(new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = audio }, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary><c>from_service(event)</c>: one Voice Live event.</summary>
    public async Task FromServiceAsync(JsonNode? serviceEvent, CancellationToken cancellationToken = default)
    {
        var values = serviceEvent as JsonObject;
        var kind = values is null ? null : PyNode.AsString(PyNode.Get(values, "type"));
        switch (kind)
        {
            case "session.updated" when !_ready:
                _ready = true;
                await _down(new JsonObject
                {
                    ["type"] = "ready",
                    ["disclosure"] = VoiceSession.Disclosure,
                    ["voice"] = Settings.VoiceName,
                }, cancellationToken).ConfigureAwait(false);
                break;
            case "response.audio.delta" when PyNode.AsString(PyNode.Get(values!, "delta")) is { } delta:
                await _down(new JsonObject { ["type"] = "audio", ["audio"] = delta }, cancellationToken).ConfigureAwait(false);
                break;
            case "input_audio_buffer.speech_started":
                // Barge-in: stop her mid-sentence.
                await _down(new JsonObject { ["type"] = "interrupt" }, cancellationToken).ConfigureAwait(false);
                break;
            case "conversation.item.input_audio_transcription.completed":
                await CaptionAsync("you", PyNode.Get(values!, "transcript"), cancellationToken).ConfigureAwait(false);
                break;
            case "response.audio_transcript.done":
                await CaptionAsync("margo", PyNode.Get(values!, "transcript"), cancellationToken).ConfigureAwait(false);
                break;
            case "response.function_call_arguments.done":
                await CallAsync(values!, cancellationToken).ConfigureAwait(false);
                break;
            case "response.done" when _outputsPending:
                _outputsPending = false;
                await _up(new JsonObject { ["type"] = "response.create" }, cancellationToken).ConfigureAwait(false);
                await _down(new JsonObject { ["type"] = "status", ["text"] = "" }, cancellationToken).ConfigureAwait(false);
                break;
            case "error":
                await _down(new JsonObject
                {
                    ["type"] = "error",
                    ["fatal"] = false,
                    ["message"] = "The voice service reported a problem; try saying that again.",
                }, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task CaptionAsync(string who, JsonNode? transcript, CancellationToken cancellationToken)
    {
        if (PyNode.AsString(transcript) is not { } text)
        {
            return;
        }

        var stripped = PyRepr.Strip(text);
        if (stripped.Length > 0)
        {
            await _down(new JsonObject { ["type"] = "caption", ["who"] = who, ["text"] = PyText.Prefix(stripped, 2000) }, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task CallAsync(JsonObject serviceEvent, CancellationToken cancellationToken)
    {
        var name = PyNode.Get(serviceEvent, "name");
        var callId = PyNode.AsString(PyNode.Get(serviceEvent, "call_id"));
        if (string.IsNullOrEmpty(callId))
        {
            return;
        }

        ToolCalls++;
        JsonNode? result;
        if (ToolCalls > Settings.MaxToolCalls)
        {
            result = new JsonObject { ["status"] = "budget_exhausted", ["reason"] = "this voice session's lookup budget is used up" };
        }
        else
        {
            var progress = VoiceSession.ProgressFor(name);
            if (progress.Length > 0)
            {
                await _down(new JsonObject { ["type"] = "status", ["text"] = progress }, cancellationToken).ConfigureAwait(false);
            }

            var arguments = PyNode.Get(serviceEvent, "arguments");
            Task<JsonNode?> Run(CancellationToken token) => _dispatch(name, arguments, token);

            // Python: name in LOCKED_TOOLS (a list or object name raises TypeError: unhashable type).
            if (PyNode.In(name, VoiceSession.LockedTools) && _runLocked is not null)
            {
                result = await _runLocked(Run, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await Run(cancellationToken).ConfigureAwait(false);
            }
        }

        await _up(new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = _clip(result) },
        }, cancellationToken).ConfigureAwait(false);
        _outputsPending = true;
    }
}

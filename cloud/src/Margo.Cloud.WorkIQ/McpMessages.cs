using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Margo.Cloud.WorkIQ;

/// <summary>JSON-RPC 2.0 message shapes and response handling for the Work IQ MCP endpoint.</summary>
internal static class McpMessages
{
    internal const string NonJsonBody = "Work IQ returned a non-JSON body";
    internal const string Unanswered = "Work IQ response did not answer the request";
    internal const string MalformedResult = "malformed tool result";
    internal const string ToolError = "Work IQ tool error";

    // Python decodes with errors="replace": invalid UTF-8 becomes U+FFFD, a BOM stays in the text.
    private static readonly UTF8Encoding Utf8Replace = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    // json.dumps(ensure_ascii=True) sends ASCII only; the default encoder also escapes every non-ASCII character.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.Default };

    /// <summary>The <c>initialize</c> request.</summary>
    internal static byte[] Initialize(long id, string protocolVersion, string clientName, string clientVersion) =>
        Envelope(id, "initialize", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", protocolVersion);
            writer.WriteStartObject("capabilities");
            writer.WriteEndObject();
            writer.WriteStartObject("clientInfo");
            writer.WriteString("name", clientName);
            writer.WriteString("version", clientVersion);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    /// <summary>The <c>notifications/initialized</c> notification (no id).</summary>
    internal static byte[] Initialized() => Envelope(null, "notifications/initialized", null);

    /// <summary>The <c>tools/call</c> request. The caller's arguments are written, never re-parented.</summary>
    internal static byte[] ToolsCall(long id, string tool, JsonObject arguments) =>
        Envelope(id, "tools/call", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool);
            writer.WritePropertyName("arguments");
            arguments.WriteTo(writer);
            writer.WriteEndObject();
        });

    /// <summary>
    /// Return the JSON-RPC message answering <paramref name="requestId"/> from a JSON or SSE body
    /// (<c>transport.py</c> <c>_parse_body</c>).
    /// </summary>
    internal static JsonObject ParseBody(byte[] raw, string? contentType, long requestId)
    {
        var text = Utf8Replace.GetString(raw);
        var messages = new List<JsonNode?>();
        if ((contentType ?? string.Empty).Contains("text/event-stream", StringComparison.Ordinal))
        {
            foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
            {
                var data = string.Join("\n", block.Split('\n')
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => PythonJson.LStrip(line[5..])));
                if (data.Length == 0)
                {
                    continue;
                }

                try
                {
                    messages.Add(PythonJson.Loads(data));
                }
                catch (JsonException)
                {
                    // An event that is not JSON is skipped, as in Python.
                }
            }
        }
        else
        {
            JsonNode? parsed;
            try
            {
                parsed = PythonJson.Strip(text).Length > 0 ? PythonJson.Loads(text) : null;
            }
            catch (JsonException exc)
            {
                throw new TransportException(NonJsonBody, null, exc);
            }

            if (parsed is JsonArray batch)
            {
                messages.AddRange(batch);
            }
            else if (parsed is not null)
            {
                messages.Add(parsed);
            }
        }

        foreach (var message in messages)
        {
            if (message is JsonObject candidate && candidate.TryGetPropertyValue("id", out var id)
                && PythonJson.EqualsInteger(id, requestId))
            {
                return candidate;
            }
        }

        throw new TransportException(Unanswered);
    }

    /// <summary>
    /// Turn an MCP tool result into the object the guard reads (<c>statusCode</c>/<c>results</c>)
    /// (<c>transport.py</c> <c>_normalise</c>). The returned object is detached from the message.
    /// </summary>
    internal static JsonObject Normalise(JsonNode? result)
    {
        if (result is not JsonObject tool)
        {
            throw new TransportException(MalformedResult);
        }

        JsonObject payload;
        if (tool.TryGetPropertyValue("structuredContent", out var structured) && structured is JsonObject structuredObject)
        {
            payload = structuredObject.DeepClone().AsObject();
        }
        else
        {
            var joined = string.Join("\n", TextItems(tool));
            try
            {
                var parsed = joined.Length > 0 ? PythonJson.Loads(joined) : new JsonObject();
                payload = parsed as JsonObject ?? new JsonObject { ["value"] = parsed };
            }
            catch (JsonException)
            {
                payload = new JsonObject { ["text"] = joined };
            }
        }

        if (PythonJson.IsTruthy(tool["isError"]))
        {
            throw new TransportException(ToolError, StatusCodeOf(payload));
        }

        return payload;
    }

    /// <summary>
    /// <c>[item.get("text") for item in result.get("content", []) if ...]</c>, including the cases where
    /// Python iterates a string or an object (no items) or fails on a value that cannot be iterated.
    /// </summary>
    private static List<string> TextItems(JsonObject tool)
    {
        var texts = new List<string>();
        if (!tool.TryGetPropertyValue("content", out var content))
        {
            return texts;
        }

        switch (content)
        {
            case JsonArray items:
                foreach (var item in items)
                {
                    if (item is JsonObject entry
                        && PythonJson.TryGetString(entry["type"], out var type) && type == "text"
                        && PythonJson.TryGetString(entry["text"], out var text))
                    {
                        texts.Add(text);
                    }
                }

                return texts;
            case JsonObject:
                return texts; // iterating a dict yields its keys, none of them a dict
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return texts; // iterating a str yields characters, none of them a dict
            default:
                throw new TransportFaultException("tool result content is not iterable");
        }
    }

    /// <summary>
    /// <c>payload.get("statusCode") if isinstance(payload.get("statusCode"), int) else None</c>.
    /// A boolean counts as an int in Python (<c>True</c> is <c>1</c>); an integer outside the
    /// <see cref="int"/> range cannot be carried and is reported as no status.
    /// </summary>
    private static int? StatusCodeOf(JsonObject payload)
    {
        if (!PythonJson.TryGetInteger(payload["statusCode"], out var code))
        {
            return null;
        }

        return code >= int.MinValue && code <= int.MaxValue ? (int)code : null;
    }

    private static byte[] Envelope(long? id, string method, Action<Utf8JsonWriter>? writeParams)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            if (id is long value)
            {
                writer.WriteNumber("id", value);
            }

            writer.WriteString("method", method);
            if (writeParams is not null)
            {
                writer.WritePropertyName("params");
                writeParams(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}

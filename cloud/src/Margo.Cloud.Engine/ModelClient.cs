using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine;

/// <summary>
/// The Responses API as the engine uses it: <c>model.responses.create(**request)</c> with the OpenAI Responses API
/// request shape, returning the response as JSON. Tests pass a fake.
/// </summary>
public interface IModelClient
{
    /// <summary>Create one response. <paramref name="request"/> is the request body (<c>model</c>, <c>instructions</c>,
    /// <c>input</c>, <c>tools</c>, <c>previous_response_id</c>); the result is the response object.</summary>
    Task<JsonNode?> CreateResponseAsync(JsonObject request, CancellationToken cancellationToken = default);
}

/// <summary>Settings of <see cref="AzureOpenAIModelClient"/>; the defaults are the <c>openai</c> Python SDK's.</summary>
public sealed record ModelClientOptions
{
    /// <summary>Retries after the first attempt (<c>DEFAULT_MAX_RETRIES</c>).</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>The longest one attempt may take (<c>DEFAULT_TIMEOUT</c>).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>Waits between attempts; replaced by tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>A number in [0, 1) for the backoff jitter; replaced by tests.</summary>
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;
}

/// <summary>A model request failed with an HTTP status (Python: <c>openai.APIStatusError</c> and its subclasses).</summary>
public sealed class ModelApiException : HttpRequestException
{
    public ModelApiException(string message, HttpStatusCode status) : base(message, null, status)
    {
    }

    public ModelApiException()
    {
    }

    public ModelApiException(string message) : base(message)
    {
    }

    public ModelApiException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The model did not answer in time (Python: <c>openai.APITimeoutError</c>, "Request timed out.").</summary>
public sealed class ModelTimeoutException : TimeoutException
{
    public ModelTimeoutException() : base("Request timed out.")
    {
    }

    public ModelTimeoutException(string message) : base(message)
    {
    }

    public ModelTimeoutException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The model could not be reached (Python: <c>openai.APIConnectionError</c>, "Connection error.").</summary>
public sealed class ModelConnectionException : HttpRequestException
{
    public ModelConnectionException() : base("Connection error.")
    {
    }

    public ModelConnectionException(string message) : base(message)
    {
    }

    public ModelConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The Responses API on an Azure AI Services account, with Entra tokens and no keys anywhere: what
/// <c>host/app.py</c> built with <c>openai.AsyncOpenAI(base_url=model_base_url(AZURE_OPENAI_ENDPOINT),
/// api_key=get_bearer_token_provider(credential, MODEL_SCOPE))</c>.
/// </summary>
/// <remarks>
/// <para>Each call is <c>POST {AZURE_OPENAI_ENDPOINT}/openai/v1/responses</c> with the request as JSON and
/// <c>Authorization: Bearer</c> a token for <see cref="Scope"/>. Tokens are cached and refreshed five minutes before
/// they expire, as Azure's bearer token policy does.</para>
/// <para>Retries follow the SDK: up to <see cref="ModelClientOptions.MaxRetries"/> more attempts after a connection
/// error, a timeout, or a 408, 409, 429 or 5xx answer (an <c>x-should-retry</c> header decides when present; a
/// <c>retry-after</c> above 60 seconds stops retrying), waiting <c>retry-after-ms</c>/<c>retry-after</c> when it is
/// between 0 and 60 seconds, else 0.5 s doubling up to 8 s, less up to 25% jitter. One idempotency key is sent with
/// every attempt of a request. A model call is not an outward action, so a repeated attempt changes nothing outside.</para>
/// </remarks>
public sealed class AzureOpenAIModelClient : IModelClient, IDisposable
{
    /// <summary><c>MODEL_SCOPE</c>.</summary>
    public const string Scope = "https://cognitiveservices.azure.com/.default";

    /// <summary>The environment variable naming the account endpoint.</summary>
    public const string EndpointVariable = "AZURE_OPENAI_ENDPOINT";

    private const double MaxRetryAfterSeconds = 60;
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly ModelClientOptions _options;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private AccessToken? _token;

    /// <param name="http">The HTTP client (see <see cref="CreateHttpClient"/>); not disposed by this class.</param>
    /// <param name="endpoint">The account endpoint, <c>https://ACCOUNT.openai.azure.com</c>.</param>
    /// <param name="credential">The instance's managed identity (or another Entra credential).</param>
    /// <param name="options">Retry and timeout settings.</param>
    /// <exception cref="InvalidOperationException">No endpoint (Python: <c>RuntimeError</c>).</exception>
    public AzureOpenAIModelClient(HttpClient http, string? endpoint, TokenCredential credential, ModelClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credential);
        _http = http;
        _credential = credential;
        _options = options ?? new ModelClientOptions();
        BaseUrl = ModelBaseUrl(endpoint);
    }

    /// <summary>The OpenAI v1 API base URL, ending in <c>/openai/v1/</c>.</summary>
    public string BaseUrl { get; }

    /// <summary><c>model_base_url(endpoint)</c>: the OpenAI v1 API of an Azure AI Services account, from its endpoint.</summary>
    /// <exception cref="InvalidOperationException">No endpoint (Python: <c>RuntimeError</c>).</exception>
    public static string ModelBaseUrl(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required (https://ACCOUNT.openai.azure.com)");
        }

        return endpoint.TrimEnd('/') + "/openai/v1/";
    }

    /// <summary>
    /// <c>_credential()</c>: this instance's own managed identity (<c>AZURE_CLIENT_ID</c>), else
    /// <see cref="DefaultAzureCredential"/>. Constructing it makes no network call.
    /// </summary>
    public static TokenCredential InstanceCredential(string? clientId = null)
    {
        clientId ??= Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        return string.IsNullOrEmpty(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }

    /// <summary>
    /// <c>_model_client(credential)</c>: the client for <c>AZURE_OPENAI_ENDPOINT</c> with
    /// <see cref="InstanceCredential"/> unless a credential is given.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>AZURE_OPENAI_ENDPOINT</c> is not set.</exception>
    public static AzureOpenAIModelClient FromEnvironment(HttpClient http, TokenCredential? credential = null, ModelClientOptions? options = null) =>
        new(http, Environment.GetEnvironmentVariable(EndpointVariable), credential ?? InstanceCredential(), options);

    /// <summary>
    /// An <see cref="HttpClient"/> with the SDK's connection settings: a 5 second connect timeout, redirects followed
    /// (the bearer token is not forwarded to another host), no client-wide timeout (each attempt has its own).
    /// </summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AllowAutoRedirect = true })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    /// <inheritdoc />
    /// <exception cref="ModelApiException">The service answered with an error status (after any retries).</exception>
    /// <exception cref="ModelTimeoutException">No answer in time (after any retries).</exception>
    /// <exception cref="ModelConnectionException">The service could not be reached (after any retries).</exception>
    public async Task<JsonNode?> CreateResponseAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = PyIO.EncodeUtf8(PyJson.Dumps(request, itemSeparator: ",", keySeparator: ":", ensureAscii: false, allowNan: false));
        var idempotencyKey = "margo-cloud-retry-" + Guid.NewGuid().ToString("D");
        for (var attempt = 0; ; attempt++)
        {
            var remaining = _options.MaxRetries - attempt;
            var bearer = await TokenAsync(cancellationToken).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "responses");
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.Timeout);
            HttpResponseMessage response;
            byte[] content;
            try
            {
                response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
                content = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (remaining > 0)
                {
                    await _options.Delay(Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new ModelTimeoutException();
            }
            catch (HttpRequestException ex)
            {
                if (remaining > 0)
                {
                    await _options.Delay(Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new ModelConnectionException("Connection error.", ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return PyJson.LoadsBytes(content);
                }

                if (remaining > 0 && ShouldRetry(response))
                {
                    await _options.Delay(Backoff(attempt, response), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new ModelApiException(ErrorMessage(response.StatusCode, content), response.StatusCode);
            }
        }
    }

    /// <summary>Releases the token cache's lock; the <see cref="HttpClient"/> is the caller's.</summary>
    public void Dispose() => _tokenGate.Dispose();

    /// <summary><c>_should_retry</c>.</summary>
    private static bool ShouldRetry(HttpResponseMessage response)
    {
        var retryAfter = RetryAfter(response);
        if (retryAfter is { } seconds && double.IsFinite(seconds) && seconds > MaxRetryAfterSeconds)
        {
            return false;
        }

        if (response.Headers.TryGetValues("x-should-retry", out var values))
        {
            var value = values.FirstOrDefault();
            if (value == "true")
            {
                return true;
            }

            if (value == "false")
            {
                return false;
            }
        }

        var status = (int)response.StatusCode;
        return status is 408 or 409 or 429 or >= 500;
    }

    /// <summary><c>_calculate_retry_timeout</c>.</summary>
    private TimeSpan Backoff(int retriesTaken, HttpResponseMessage? response)
    {
        if (response is not null && RetryAfter(response) is { } seconds && double.IsFinite(seconds) && seconds > 0 && seconds <= MaxRetryAfterSeconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var sleep = Math.Min(0.5 * Math.Pow(2.0, Math.Min(retriesTaken, 1000)), 8.0);
        var jitter = 1 - (0.25 * _options.Random());
        return TimeSpan.FromSeconds(Math.Max(0, sleep * jitter));
    }

    /// <summary><c>_parse_retry_after_header</c>: <c>retry-after-ms</c>, then <c>retry-after</c> as seconds or an HTTP date.</summary>
    private static double? RetryAfter(HttpResponseMessage response)
    {
        if (Header(response, "retry-after-ms") is { } milliseconds && TryParseFloat(milliseconds, out var ms))
        {
            return ms / 1000;
        }

        if (Header(response, "retry-after") is not { } retry)
        {
            return null;
        }

        if (TryParseFloat(retry, out var seconds))
        {
            return seconds;
        }

        return DateTimeOffset.TryParse(retry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? (date - DateTimeOffset.UtcNow).TotalSeconds
            : null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool TryParseFloat(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// <c>_make_status_error_from_response</c>: <c>Error code: N - {body}</c> with the body's Python repr when it is
    /// JSON, else the body text, else <c>Error code: N</c>.
    /// </summary>
    private static string ErrorMessage(HttpStatusCode status, byte[] content)
    {
        var code = ((int)status).ToString(CultureInfo.InvariantCulture);
        var text = PyRepr.Strip(new System.Text.UTF8Encoding(false, false).GetString(content));
        try
        {
            return "Error code: " + code + " - " + PyNode.Repr(PyJson.Loads(text));
        }
        catch (PyException)
        {
            return text.Length > 0 ? text : "Error code: " + code;
        }
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_token is not { } cached || cached.ExpiresOn - now <= RefreshMargin || (cached.RefreshOn is { } refresh && refresh <= now))
            {
                _token = await _credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken).ConfigureAwait(false);
            }

            return _token.Value.Token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }
}

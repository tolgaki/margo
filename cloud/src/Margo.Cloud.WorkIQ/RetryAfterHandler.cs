using System.Net;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// For read sessions only: a request answered 429 or 503 with a <c>Retry-After</c> of at most <c>maxWait</c> is sent once
/// more after that wait. The server said it did not process the request, and nothing on a read session changes data.
/// Write sessions never get this handler.
/// </summary>
internal sealed class RetryAfterHandler(HttpMessageHandler inner, TimeSpan maxWait, TimeProvider time) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(Copy(request, body), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || Wait(response) is not { } wait || wait > maxWait)
        {
            return response;
        }

        response.Dispose();
        WorkIQTelemetry.ThrottleRetries.Add(1);
        await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(Copy(request, body), cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan? Wait(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retry?.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static HttpRequestMessage Copy(HttpRequestMessage request, byte[]? body)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        }

        return copy;
    }
}

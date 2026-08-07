#nullable disable

using System.Net;
using System.Text;
using Photon.Hive.Plugin;

namespace OpenPhoton.Server.Plugins;

/// <summary>What a plugin gets back from <c>IPluginHost.HttpRequest</c>.</summary>
public sealed class PluginHttpResponse : IHttpResponse
{
    public int Status { get; init; }

    public int HttpCode { get; init; }

    public string Reason { get; init; }

    public byte[] ResponseData { get; init; }

    public string ResponseText { get; init; }

    public object UserState { get; init; }

    public HttpRequest Request { get; init; }

    public ICallInfo CallInfo { get; init; }

    public int WebStatus { get; init; }

    public Dictionary<string, string> Headers { get; init; } = [];
}

/// <summary>
/// Performs the outbound calls plugins ask for.
/// </summary>
/// <remarks>
/// Photon separates two notions of "did it work" that are easy to conflate. <c>Status</c> is
/// about the request — whether it was made at all — while <c>HttpCode</c> is the server's
/// answer. A 500 from the far end is a <em>successful</em> request that returned an error, so
/// it reports <c>HttpRequestQueueResult.Success</c> with an <c>HttpCode</c> of 500. Plugins
/// branch on both, and collapsing them would make a reachable-but-unhappy endpoint
/// indistinguishable from an unreachable one.
/// </remarks>
public static class PluginHttp
{
    /// <summary>
    /// One client for every plugin call.
    /// </summary>
    /// <remarks>
    /// Shared deliberately. A fresh <c>HttpClient</c> per request holds its socket in
    /// TIME_WAIT after disposal, and a plugin calling an API on every join exhausts the
    /// ephemeral port range under load. The infinite timeout is intentional too: the
    /// per-request timeout is applied by the caller's token, so a plugin's own deadline wins
    /// rather than being clipped by a shared default.
    /// </remarks>
    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>How long a plugin call may take before it is abandoned.</summary>
    /// <remarks>Photon's own default, from its HttpQueueSettings.</remarks>
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public static PluginHttpResponse Failed(HttpRequest request, ICallInfo info, string reason) => new()
    {
        Status = HttpRequestQueueResult.Error,
        WebStatus = (int)WebExceptionStatus.UnknownError,
        Reason = reason,
        Request = request,
        CallInfo = info,
        UserState = request.UserState,
        ResponseText = string.Empty,
        ResponseData = [],
    };

    public static async Task<IHttpResponse> SendAsync(HttpRequest request, ICallInfo info, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestTimeout);

        try
        {
            using var message = Build(request);
            using var response = await Client.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline.Token);

            var body = await response.Content.ReadAsByteArrayAsync(deadline.Token);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers) headers[header.Key] = string.Join(", ", header.Value);
            foreach (var header in response.Content.Headers) headers[header.Key] = string.Join(", ", header.Value);

            return new PluginHttpResponse
            {
                Status = HttpRequestQueueResult.Success,
                WebStatus = (int)WebExceptionStatus.Success,
                HttpCode = (int)response.StatusCode,
                Reason = response.ReasonPhrase,
                ResponseData = body,
                ResponseText = Decode(body, response.Content.Headers.ContentType?.CharSet),
                Headers = headers,
                Request = request,
                CallInfo = info,
                UserState = request.UserState,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PluginHttpResponse
            {
                Status = HttpRequestQueueResult.RequestTimeout,
                WebStatus = (int)WebExceptionStatus.Timeout,
                Reason = $"Timed out after {RequestTimeout.TotalSeconds:0}s.",
                Request = request,
                CallInfo = info,
                UserState = request.UserState,
                ResponseText = string.Empty,
                ResponseData = [],
            };
        }
        catch (OperationCanceledException)
        {
            return new PluginHttpResponse
            {
                Status = HttpRequestQueueResult.Offline,
                WebStatus = (int)WebExceptionStatus.RequestCanceled,
                Reason = "The room closed before the request completed.",
                Request = request,
                CallInfo = info,
                UserState = request.UserState,
                ResponseText = string.Empty,
                ResponseData = [],
            };
        }
        catch (HttpRequestException ex)
        {
            return new PluginHttpResponse
            {
                Status = HttpRequestQueueResult.Error,
                WebStatus = (int)WebExceptionStatus.ConnectFailure,
                Reason = ex.Message,
                Request = request,
                CallInfo = info,
                UserState = request.UserState,
                ResponseText = string.Empty,
                ResponseData = [],
            };
        }
        catch (Exception ex)
        {
            return Failed(request, info, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    static HttpRequestMessage Build(HttpRequest request)
    {
        var method = string.IsNullOrWhiteSpace(request.Method)
            ? request.DataStream is { Length: > 0 } ? HttpMethod.Post : HttpMethod.Get
            : new HttpMethod(request.Method.ToUpperInvariant());

        var message = new HttpRequestMessage(method, request.Url);

        if (request.DataStream is { Length: > 0 } body)
        {
            message.Content = new ByteArrayContent(body);
            if (!string.IsNullOrWhiteSpace(request.ContentType))
                message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        }

        if (!string.IsNullOrWhiteSpace(request.Accept))
            message.Headers.TryAddWithoutValidation("Accept", request.Accept);

        if (request.CustomHeaders is not null)
        {
            foreach (var header in request.CustomHeaders)
            {
                if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return message;
    }

    static string Decode(byte[] body, string charset)
    {
        if (body is null || body.Length == 0) return string.Empty;

        try
        {
            return string.IsNullOrWhiteSpace(charset)
                ? Encoding.UTF8.GetString(body)
                : Encoding.GetEncoding(charset.Trim('"')).GetString(body);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(body);
        }
    }
}

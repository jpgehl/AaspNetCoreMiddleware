using AspNetCoreMiddleware.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System.Net.Http.Headers;

namespace AspNetCoreMiddleware.Middleware;

public sealed class ReverseProxyMiddleware
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.Connection,
        HeaderNames.KeepAlive,
        HeaderNames.ProxyAuthenticate,
        HeaderNames.ProxyAuthorization,
        HeaderNames.TE,
        HeaderNames.Trailer,
        HeaderNames.TransferEncoding,
        HeaderNames.Upgrade,
        HeaderNames.Host,
        HeaderNames.ContentLength
    };

    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _backendBaseUri;
    private readonly ILogger<ReverseProxyMiddleware> _logger;

    public ReverseProxyMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        IOptions<BackendOptions> backendOptions,
        ILogger<ReverseProxyMiddleware> logger)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _backendBaseUri = BackendOptions.CreateBaseUri(backendOptions.Value.BaseUrl);
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHealthRequest(context.Request))
        {
            await _next(context);
            return;
        }

        using var proxyRequest = CreateProxyRequest(context.Request, _backendBaseUri);
        var httpClient = _httpClientFactory.CreateClient(ReverseProxyDefaults.BackendHttpClientName);

        try
        {
            using var proxyResponse = await httpClient.SendAsync(
                proxyRequest,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            await CopyProxyResponseAsync(context, proxyResponse);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug("Proxy request was canceled by the caller.");
        }
    }

    private static bool IsHealthRequest(HttpRequest request)
    {
        return request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage CreateProxyRequest(HttpRequest request, Uri backendBaseUri)
    {
        var targetUri = BuildTargetUri(request, backendBaseUri);
        var proxyRequest = new HttpRequestMessage(new HttpMethod(request.Method), targetUri);

        if (RequestHasBody(request))
        {
            proxyRequest.Content = new StreamContent(request.Body);
        }

        foreach (var header in request.Headers)
        {
            if (ShouldSkipHeader(header.Key))
            {
                continue;
            }

            var values = header.Value.ToArray();
            if (!proxyRequest.Headers.TryAddWithoutValidation(header.Key, values))
            {
                proxyRequest.Content?.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }

        return proxyRequest;
    }

    private static Uri BuildTargetUri(HttpRequest request, Uri backendBaseUri)
    {
        var baseUrl = backendBaseUri.ToString().TrimEnd('/');
        var path = request.PathBase.ToUriComponent() + request.Path.ToUriComponent();
        var query = request.QueryString.ToUriComponent();

        return new Uri(baseUrl + path + query, UriKind.Absolute);
    }

    private static bool RequestHasBody(HttpRequest request)
    {
        return request.ContentLength is > 0 || request.Headers.ContainsKey(HeaderNames.TransferEncoding);
    }

    private static async Task CopyProxyResponseAsync(HttpContext context, HttpResponseMessage proxyResponse)
    {
        context.Response.StatusCode = (int)proxyResponse.StatusCode;

        CopyHeaders(proxyResponse.Headers, context.Response.Headers);
        CopyHeaders(proxyResponse.Content.Headers, context.Response.Headers);

        RemoveHeadersThatAspNetCoreManages(context.Response.Headers);

        await proxyResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static void CopyHeaders(HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            if (ShouldSkipHeader(header.Key))
            {
                continue;
            }

            destination[header.Key] = header.Value.ToArray();
        }
    }

    private static void RemoveHeadersThatAspNetCoreManages(IHeaderDictionary headers)
    {
        headers.Remove(HeaderNames.TransferEncoding);
        headers.Remove(HeaderNames.Connection);
        headers.Remove(HeaderNames.KeepAlive);
        headers.Remove(HeaderNames.Upgrade);
    }

    private static bool ShouldSkipHeader(string headerName)
    {
        return HopByHopHeaders.Contains(headerName)
            || headerName.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase);
    }
}

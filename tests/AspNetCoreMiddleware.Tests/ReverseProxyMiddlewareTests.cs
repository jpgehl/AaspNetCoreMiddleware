using System.Net;
using System.Security.Authentication;
using System.Text;
using AspNetCoreMiddleware.Configuration;
using AspNetCoreMiddleware.Middleware;
using AspNetCoreMiddleware.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Net.Http.Headers;

namespace AspNetCoreMiddleware.Tests;

public sealed class ReverseProxyMiddlewareTests
{
    [Fact]
    public async Task HealthEndpoint_ReturnsLocalStatusWithoutCallingBackend()
    {
        var backendHandler = new CapturingBackendHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        using var factory = CreateFactory(backendHandler);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.Empty(backendHandler.Requests);
    }

    [Fact]
    public async Task Proxy_ForwardsRequestAndCopiesBackendResponse()
    {
        var backendHandler = new CapturingBackendHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };

            response.Headers.TryAddWithoutValidation("X-Backend", "true");
            response.Headers.TryAddWithoutValidation(HeaderNames.Connection, "close");
            response.Headers.TryAddWithoutValidation(HeaderNames.ProxyAuthenticate, "Basic realm=\"proxy\"");

            return response;
        });

        using var factory = CreateFactory(backendHandler);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders/42?include=true");
        request.Content = new StringContent("{\"name\":\"test\"}", Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", "abc-123");
        request.Headers.TryAddWithoutValidation(HeaderNames.Connection, "keep-alive");
        request.Headers.TryAddWithoutValidation(HeaderNames.ProxyAuthorization, "Basic secret");
        request.Headers.TryAddWithoutValidation(HeaderNames.TE, "trailers");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.TryGetValues("X-Backend", out var backendHeader));
        Assert.Equal("true", Assert.Single(backendHeader));
        Assert.False(response.Headers.Contains(HeaderNames.Connection));
        Assert.False(response.Headers.Contains(HeaderNames.ProxyAuthenticate));

        var capturedRequest = Assert.Single(backendHandler.Requests);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("https://backend.test/base/orders/42?include=true", capturedRequest.RequestUri.ToString());
        Assert.Equal("{\"name\":\"test\"}", capturedRequest.Body);
        Assert.Equal("abc-123", Assert.Single(capturedRequest.Headers["X-Correlation-ID"]));
        Assert.Contains("application/json", Assert.Single(capturedRequest.Headers[HeaderNames.ContentType]));
        Assert.False(capturedRequest.Headers.ContainsKey(HeaderNames.Connection));
        Assert.False(capturedRequest.Headers.ContainsKey(HeaderNames.ProxyAuthorization));
        Assert.False(capturedRequest.Headers.ContainsKey(HeaderNames.TE));
    }

    [Fact]
    public void ProxyHandlerFactory_ConfiguresExplicitProxy()
    {
        using var handler = ProxyHttpMessageHandlerFactory.Create(new OutboundProxyOptions
        {
            Address = "http://proxy.example.com:8080",
            UseDefaultCredentials = true,
            BypassProxyOnLocal = true
        });

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);

        Assert.True(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(SslProtocols.None, handler.SslOptions.EnabledSslProtocols);
        Assert.True(proxy.UseDefaultCredentials);
        Assert.True(proxy.BypassProxyOnLocal);
    }

    [Fact]
    public void ProxyHandlerFactory_DisablesProxyWhenAddressIsEmpty()
    {
        using var handler = ProxyHttpMessageHandlerFactory.Create(new OutboundProxyOptions
        {
            Address = string.Empty
        });

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(SslProtocols.None, handler.SslOptions.EnabledSslProtocols);
    }

    private static WebApplicationFactory<Program> CreateFactory(CapturingBackendHandler backendHandler)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Backend:BaseUrl"] = "https://backend.test/base",
                        ["Backend:TimeoutSeconds"] = "100",
                        ["Proxy:Address"] = string.Empty
                    });
                });

                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
                        new CapturingBackendHandlerFilter(backendHandler));
                });
            });
    }

    private sealed class CapturingBackendHandlerFilter : IHttpMessageHandlerBuilderFilter
    {
        private readonly CapturingBackendHandler _handler;

        public CapturingBackendHandlerFilter(CapturingBackendHandler handler)
        {
            _handler = handler;
        }

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
        {
            return builder =>
            {
                next(builder);

                if (builder.Name == ReverseProxyDefaults.BackendHttpClientName)
                {
                    builder.PrimaryHandler = _handler;
                }
            };
        }
    }

    private sealed class CapturingBackendHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public CapturingBackendHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<CapturedBackendRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(await CapturedBackendRequest.CreateAsync(request, cancellationToken));

            return _responseFactory(request);
        }
    }

    private sealed record CapturedBackendRequest(
        HttpMethod Method,
        Uri RequestUri,
        Dictionary<string, string[]> Headers,
        string? Body)
    {
        public static async Task<CapturedBackendRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var contentHeaders = request.Content?.Headers
                ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>();

            var headers = request.Headers
                .Concat(contentHeaders)
                .ToDictionary(
                    header => header.Key,
                    header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new CapturedBackendRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Proxy request URI was not set."),
                headers,
                body);
        }
    }
}

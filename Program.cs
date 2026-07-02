using AspNetCoreMiddleware.Configuration;
using AspNetCoreMiddleware.Middleware;
using AspNetCoreMiddleware.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<BackendOptions>()
    .Bind(builder.Configuration.GetSection(BackendOptions.SectionName))
    .Validate(BackendOptions.IsValidBaseUrl, "Backend:BaseUrl must be an absolute HTTP or HTTPS URL.")
    .Validate(options => options.TimeoutSeconds > 0, "Backend:TimeoutSeconds must be greater than zero.")
    .ValidateOnStart();

builder.Services
    .AddOptions<OutboundProxyOptions>()
    .Bind(builder.Configuration.GetSection(OutboundProxyOptions.SectionName))
    .Validate(OutboundProxyOptions.IsValidProxyAddress, "Proxy:Address must be empty or an absolute HTTP or HTTPS URL.")
    .ValidateOnStart();

builder.Services.AddHealthChecks();

builder.Services
    .AddHttpClient(ReverseProxyDefaults.BackendHttpClientName)
    .ConfigureHttpClient((serviceProvider, httpClient) =>
    {
        var backendOptions = serviceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<BackendOptions>>()
            .Value;

        httpClient.BaseAddress = BackendOptions.CreateBaseUri(backendOptions.BaseUrl);
        httpClient.Timeout = TimeSpan.FromSeconds(backendOptions.TimeoutSeconds);
    })
    .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
    {
        var proxyOptions = serviceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboundProxyOptions>>()
            .Value;

        return ProxyHttpMessageHandlerFactory.Create(proxyOptions);
    });

var app = builder.Build();

app.UseMiddleware<ReverseProxyMiddleware>();

app.MapHealthChecks("/health");

app.Run();

public partial class Program;

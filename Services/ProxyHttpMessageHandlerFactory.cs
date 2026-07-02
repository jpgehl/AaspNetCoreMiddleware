using System.Net;
using System.Security.Authentication;
using AspNetCoreMiddleware.Configuration;

namespace AspNetCoreMiddleware.Services;

public static class ProxyHttpMessageHandlerFactory
{
    public static SocketsHttpHandler Create(OutboundProxyOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.None
            },
            UseProxy = false
        };

        if (string.IsNullOrWhiteSpace(options.Address))
        {
            return handler;
        }

        handler.UseProxy = true;
        handler.Proxy = new WebProxy(options.Address, options.BypassProxyOnLocal)
        {
            UseDefaultCredentials = options.UseDefaultCredentials
        };

        return handler;
    }
}

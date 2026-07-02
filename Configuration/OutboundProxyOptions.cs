namespace AspNetCoreMiddleware.Configuration;

public sealed class OutboundProxyOptions
{
    public const string SectionName = "Proxy";

    public string? Address { get; set; }

    public bool UseDefaultCredentials { get; set; } = true;

    public bool BypassProxyOnLocal { get; set; } = true;

    public static bool IsValidProxyAddress(OutboundProxyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Address))
        {
            return true;
        }

        return Uri.TryCreate(options.Address, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}

namespace AspNetCoreMiddleware.Configuration;

public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 100;

    public static bool IsValidBaseUrl(BackendOptions options)
    {
        return IsHttpOrHttpsAbsoluteUri(options.BaseUrl);
    }

    public static Uri CreateBaseUri(string baseUrl)
    {
        return new Uri(baseUrl.TrimEnd('/') + '/', UriKind.Absolute);
    }

    private static bool IsHttpOrHttpsAbsoluteUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}

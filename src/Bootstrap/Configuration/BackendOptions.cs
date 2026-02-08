namespace EndpointSignalAgent.Bootstrap.Configuration;

public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    public string BaseUrl { get; set; } = string.Empty;
    public string EnrollPath { get; set; } = "/enroll";
    public string SendPath { get; set; } = "/send";
    public string StatusPath { get; set; } = "/status";
    public string FeaturesPath { get; set; } = "/features";
    public int TimeoutSeconds { get; set; } = 30;

    public Uri GetBaseUri()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new InvalidOperationException("Backend:BaseUrl is not configured");

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Backend:BaseUrl '{BaseUrl}' is not a valid absolute URL");

        return uri;
    }
}


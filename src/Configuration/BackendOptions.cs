namespace EndpointSignalAgent.Configuration;

public sealed class BackendOptions
{
    public string BaseUrl { get; set; } = "https://httpbin.org";
    public string SendPath { get; set; } = "/send";
    public string StatusPath { get; set; } = "/status";
    public int TimeoutSeconds { get; set; } = 10;
}

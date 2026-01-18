using System.Net.Http.Json;
using EndpointSignalAgent.Configuration;
using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Clients;

public sealed class BackendClient(
    HttpClient http,
    IOptions<BackendOptions> backendOptions)
{
    private readonly BackendOptions _opts = backendOptions.Value;

    public async Task SendAsync(SignalBatchRequest req, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync(_opts.SendPath, req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Send failed status {(int)resp.StatusCode}");
    }

    public async Task<StatusResponse?> PollStatusAsync(StatusRequest req, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync(_opts.StatusPath, req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Status failed status {(int)resp.StatusCode}");

        return await resp.Content.ReadFromJsonAsync<StatusResponse>(cancellationToken: ct);
    }
}

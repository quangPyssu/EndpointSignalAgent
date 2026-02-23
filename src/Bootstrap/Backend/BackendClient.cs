using System.Net.Http.Json;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Bootstrap.Backend;

public sealed class BackendClient
{
    private readonly HttpClient _http;
    private readonly BackendOptions _opts;
    private readonly ILogger<BackendClient> _logger;

    public BackendClient(
        HttpClient http,
        IOptions<BackendOptions> backendOptions,
        ILogger<BackendClient> logger)
    {
        _http = http;
        _opts = backendOptions.Value;
        _logger = logger;
    }

    public async Task<string> EnrollAsync(string deviceName, CancellationToken ct)
    {
        if (!_opts.UseBackend)
        {
            var simulatedId = Guid.NewGuid().ToString("D");
            _logger.LogInformation("Enrollment simulated (Backend:UseBackend=false). DeviceId={DeviceId}", simulatedId);
            return simulatedId;
        }

        try
        {
            var req = new EnrollRequest(DeviceName: deviceName);
            _logger.LogDebug("Enrolling device '{DeviceName}' to {Url}", deviceName, $"{_opts.BaseUrl}{_opts.EnrollPath}");
            
            var resp = await _http.PostAsJsonAsync(_opts.EnrollPath, req, ct);
            
            if (!resp.IsSuccessStatusCode)
            {
                var errorContent = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Enrollment failed with status {StatusCode}: {Error}", 
                    (int)resp.StatusCode, errorContent);
                throw new HttpRequestException($"Enroll failed status {(int)resp.StatusCode}: {errorContent}");
            }
            
            var enrollResp = await resp.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken: ct);
            if (enrollResp == null || string.IsNullOrWhiteSpace(enrollResp.DeviceId))
            {
                _logger.LogWarning("Enrollment response missing device ID");
                throw new HttpRequestException("Enroll failed: invalid response");
            }
            
            _logger.LogInformation("Successfully enrolled device with ID: {DeviceId}", enrollResp.DeviceId);
            return enrollResp.DeviceId;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error during enrollment to {Url}", $"{_opts.BaseUrl}{_opts.EnrollPath}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during enrollment");
            throw;
        }
    }

    public async Task<SignalBatchResponse?> SendAsync(SignalBatchRequest req, CancellationToken ct)
    {
        if (!_opts.UseBackend)
        {
            _logger.LogDebug("Sending signal batch for device {DeviceId} (simulated)", req.DeviceId);
            await Task.Delay(100, ct);
            _logger.LogDebug("Signal batch sent (simulated), success={Success}", true);
            return new SignalBatchResponse(true);
        }

        try
        {
            _logger.LogDebug("Sending signal batch for device {DeviceId}", req.DeviceId);
            var resp = await _http.PostAsJsonAsync(_opts.SendPath, req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errorContent = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Signal send failed with status {StatusCode}: {Error}",
                    (int)resp.StatusCode, errorContent);
                throw new HttpRequestException($"Send failed status {(int)resp.StatusCode}: {errorContent}");
            }

            var sendResp = await resp.Content.ReadFromJsonAsync<SignalBatchResponse>(cancellationToken: ct);
            _logger.LogDebug("Signal batch sent, success={Success}", sendResp?.Success ?? false);

            return sendResp;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error sending signals to {Url}", $"{_opts.BaseUrl}{_opts.SendPath}");
            throw;
        }
    }

    public async Task<StatusResponse?> PollStatusAsync(StatusRequest req, CancellationToken ct)
    {
        if (!_opts.UseBackend)
        {
            await Task.Delay(50, ct);
            var status = new StatusResponse(Status: "active");
            _logger.LogDebug("Status poll (simulated) returned: {Status}", status.Status);
            return status;
        }

        try
        {
            _logger.LogDebug("Polling status for device {DeviceId}", req.DeviceId);
            
            var resp = await _http.PostAsJsonAsync(_opts.StatusPath, req, ct);
            
            if (!resp.IsSuccessStatusCode)
            {
                var errorContent = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Status poll failed with status {StatusCode}: {Error}", 
                    (int)resp.StatusCode, errorContent);
                throw new HttpRequestException($"Status failed status {(int)resp.StatusCode}: {errorContent}");
            }

            var status = await resp.Content.ReadFromJsonAsync<StatusResponse>(cancellationToken: ct);
            _logger.LogDebug("Status poll returned: {Status}", status?.Status ?? "null");
            
            return status;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error polling status from {Url}", $"{_opts.BaseUrl}{_opts.StatusPath}");
            throw;
        }
    }
}

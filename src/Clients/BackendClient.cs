using System.Net.Http.Json;
using EndpointSignalAgent.Configuration;
using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Clients;

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
        try
        {
            _logger.LogDebug("Sending signal batch for device {DeviceId}", req.DeviceId);
            // Simulating send - actual backend call disabled
            await Task.Delay(100, ct); // simulate network delay
            _logger.LogDebug("Signal batch sent (simulated), success={Success}", true);
            return new SignalBatchResponse(true);

            // Actual backend send (disabled)
            //var resp = await _http.PostAsJsonAsync(_opts.SendPath, req, ct);
            //
            //if (!resp.IsSuccessStatusCode)
            //{
            //    var errorContent = await resp.Content.ReadAsStringAsync(ct);
            //    _logger.LogWarning("Signal send failed with status {StatusCode}: {Error}", 
            //        (int)resp.StatusCode, errorContent);
            //    throw new HttpRequestException($"Send failed status {(int)resp.StatusCode}: {errorContent}");
            //}
            //
            //var sendResp = await resp.Content.ReadFromJsonAsync<SignalBatchResponse>(cancellationToken: ct);
            //_logger.LogDebug("Signal batch sent, success={Success}", sendResp?.Success ?? false);
            //
            //return sendResp;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error sending signals to {Url}", $"{_opts.BaseUrl}{_opts.SendPath}");
            throw;
        }
    }

    public async Task<StatusResponse?> PollStatusAsync(StatusRequest req, CancellationToken ct)
    {
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

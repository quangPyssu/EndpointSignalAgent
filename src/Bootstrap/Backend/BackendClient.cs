using System.Net.Http.Json;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Shared.Contracts;
using Microsoft.Extensions.Logging;
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

    public async Task<EnrollResponse> EnrollAsync(CancellationToken ct)
    {
        if (!_opts.UseBackend)
        {
            var simulatedId = Guid.NewGuid().ToString("D");
            _logger.LogInformation("Enrollment simulated (Backend:UseBackend=false). DeviceId={DeviceId}", simulatedId);
            return new EnrollResponse(DeviceId: simulatedId, Token: "simulated-token", ReportSeconds: 60);
        }

        try
        {
            var req = new EnrollRequest(
                DeviceId: null,
                Hostname: Environment.MachineName,
                Os: Environment.OSVersion.ToString(),
                AgentVersion: typeof(BackendClient).Assembly.GetName().Version?.ToString()
            );
            _logger.LogDebug("Enrolling to {Url}", $"{_opts.BaseUrl}{_opts.EnrollPath}");

            var resp = await _http.PostAsJsonAsync(_opts.EnrollPath, req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errorContent = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Enrollment failed {Status}: {Error}", (int)resp.StatusCode, errorContent);
                throw new HttpRequestException($"Enroll failed status {(int)resp.StatusCode}: {errorContent}");
            }

            var enrollResp = await resp.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken: ct);
            if (enrollResp is null || string.IsNullOrWhiteSpace(enrollResp.DeviceId))
            {
                _logger.LogWarning("Enrollment response missing device ID");
                throw new HttpRequestException("Enroll failed: invalid response");
            }

            _logger.LogInformation("Enrolled device {DeviceId}", enrollResp.DeviceId);
            return enrollResp;
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
            _logger.LogDebug("Signal batch for device {DeviceId} (simulated, count={Count})",
                req.DeviceId, req.Signals.Count);
            return new SignalBatchResponse(Accepted: req.Signals.Count);
        }

        try
        {
            _logger.LogDebug("Sending {Count} signals for device {DeviceId}", req.Signals.Count, req.DeviceId);
            var resp = await _http.PostAsJsonAsync(_opts.SendPath, req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Signal send failed {Status}: {Error}", (int)resp.StatusCode, err);
                throw new HttpRequestException($"Send failed status {(int)resp.StatusCode}: {err}");
            }

            return await resp.Content.ReadFromJsonAsync<SignalBatchResponse>(cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error sending signals to {Url}", $"{_opts.BaseUrl}{_opts.SendPath}");
            throw;
        }
    }

    public async Task<StatusDecision?> PollStatusAsync(string deviceId, CancellationToken ct)
    {
        if (!_opts.UseBackend)
        {
            await Task.Delay(50, ct);
            var simulated = new StatusDecision(
                DeviceId: deviceId,
                Label: KnownLabels.Allow,
                Score: 1.0,
                Reason: "simulated",
                ModelVersion: "sim",
                WindowStartTs: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                DecidedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TtlSeconds: 60);
            _logger.LogDebug("Status poll (simulated): label={Label}", simulated.Label);
            return simulated;
        }

        try
        {
            var url = $"{_opts.StatusPath}?device_id={Uri.EscapeDataString(deviceId)}";
            _logger.LogDebug("Polling status: GET {Url}", url);

            var resp = await _http.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Status poll failed {Status}: {Error}", (int)resp.StatusCode, err);
                throw new HttpRequestException($"Status failed status {(int)resp.StatusCode}: {err}");
            }

            var decision = await resp.Content.ReadFromJsonAsync<StatusDecision>(cancellationToken: ct);
            _logger.LogDebug("Status poll: label={Label}", decision?.Label ?? "null");
            return decision;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error polling status from {Url}", $"{_opts.BaseUrl}{_opts.StatusPath}");
            throw;
        }
    }
}

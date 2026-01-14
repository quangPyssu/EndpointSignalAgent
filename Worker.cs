using System.Net.Http.Json;
using EndpointSignalAgent.Contracts;

public class Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent started at {time}", DateTimeOffset.Now);

        var deviceId = Environment.MachineName; // replace later with stable device id strategy
        var sessionId = Guid.NewGuid().ToString("N");

        var reportEvery = TimeSpan.FromSeconds(10);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // TODO: replace stub events with real collectors
                var events = new List<SignalEvent>
                {
                    new(
                        DateTimeOffset.UtcNow,
                        "Heartbeat",
                        new Dictionary<string, string>
                        {
                            ["user"] = Environment.UserName,
                            ["pid"] = Environment.ProcessId.ToString()
                        })
                };

                var req = new SignalBatchRequest(
                    DeviceId: deviceId,
                    SessionId: sessionId,
                    SentAt: DateTimeOffset.UtcNow,
                    Events: events);

                var client = httpClientFactory.CreateClient("backend");

                // Temporary test endpoint while backend is being developed
                var resp = await client.PostAsJsonAsync("/post", req, stoppingToken);

                if (resp.IsSuccessStatusCode)
                {
                    var decision = await resp.Content.ReadFromJsonAsync<SignalBatchResponse>(cancellationToken: stoppingToken);
                    if (decision is not null)
                    {
                        ApplyDecision(decision, ref reportEvery, logger);
                    }
                }
                else
                {
                    logger.LogWarning("Backend returned {code}", (int)resp.StatusCode);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in agent loop");
            }

            await Task.Delay(reportEvery, stoppingToken);
        }
    }

    private static void ApplyDecision(
        SignalBatchResponse decision,
        ref TimeSpan reportEvery,
        ILogger logger)
    {
        logger.LogInformation("Decision={Decision} Risk={Risk} Msg={Msg}",
            decision.Decision, decision.RiskScore, decision.Message);

        // Adjust reporting interval if server suggests it
        if (decision.NextReportSeconds is > 0 and < 3600)
            reportEvery = TimeSpan.FromSeconds(decision.NextReportSeconds.Value);

        // React to response (your plan)
        switch (decision.Decision)
        {
            case "ALLOW":
                break;

            case "WARN":
                // TODO: optional toast notification
                break;

            case "STEP_UP":
                // TODO: launch step-up flow (browser deep-link or local UI)
                break;

            case "LOCK":
                // TODO (later): LockWorkStation via P/Invoke
                break;
        }
    }
}

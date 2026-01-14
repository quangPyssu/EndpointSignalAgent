using System.Net.Http.Json;
using System.Threading.Channels;
using EndpointSignalAgent.Contracts;

public class Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory)
    : BackgroundService
{
    // Use an atomic int so producer can read while consumer updates (from server decision).
    private int _reportEverySeconds = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent started at {time}", DateTimeOffset.Now);

        var deviceId = Environment.MachineName; // replace later with stable device id strategy
        var sessionId = Guid.NewGuid().ToString("N");

        // Bounded queue so RAM can’t grow forever if backend is down.
        // DropOldest keeps the most recent context (usually what you want for CA).
        var channel = Channel.CreateBounded<SignalBatchRequest>(new BoundedChannelOptions(capacity: 300)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        });

        // Run producer + consumer concurrently
        var producerTask = ProduceBatchesAsync(channel.Writer, deviceId, sessionId, stoppingToken);
        var consumerTask = ConsumeAndSendAsync(channel.Reader, stoppingToken);

        await Task.WhenAll(producerTask, consumerTask);
    }

    private async Task ProduceBatchesAsync(
        ChannelWriter<SignalBatchRequest> writer,
        string deviceId,
        string sessionId,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1) Collect (stub for now)
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

                // 2) Build batch
                var req = new SignalBatchRequest(
                    DeviceId: deviceId,
                    SessionId: sessionId,
                    SentAt: DateTimeOffset.UtcNow,
                    Events: events);

                // 3) Enqueue (non-blocking if possible; with DropOldest this should usually succeed)
                // If channel is completed, WriteAsync will throw -> we exit.
                await writer.WriteAsync(req, ct);

                // 4) Sleep for the current report interval (read atomically)
                var seconds = Volatile.Read(ref _reportEverySeconds);
                if (seconds < 1) seconds = 1;

                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Producer loop crashed");
        }
        finally
        {
            // Tell consumer "no more items"
            writer.TryComplete();
        }
    }

    private async Task ConsumeAndSendAsync(ChannelReader<SignalBatchRequest> reader, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("backend");

        // Simple exponential backoff (resets after a success)
        var backoff = TimeSpan.FromSeconds(1);
        var backoffMax = TimeSpan.FromSeconds(30);

        try
        {
            await foreach (var req in reader.ReadAllAsync(ct))
            {
                // Keep retrying THIS req until success or shutdown.
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Temporary test endpoint while backend is being developed
                        var resp = await client.PostAsJsonAsync("/post", req, ct);

                        if (!resp.IsSuccessStatusCode)
                        {
                            logger.LogWarning("Backend returned {code}", (int)resp.StatusCode);
                            throw new HttpRequestException($"Backend status {(int)resp.StatusCode}");
                        }

                        var decision =
                            await resp.Content.ReadFromJsonAsync<SignalBatchResponse>(cancellationToken: ct);

                        if (decision is not null)
                        {
                            ApplyDecision(decision);
                        }

                        // Success -> reset backoff and move to next queued item
                        backoff = TimeSpan.FromSeconds(1);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Send failed; retrying in {delay}s", backoff.TotalSeconds);
                        await Task.Delay(backoff, ct);

                        // Increase backoff
                        var next = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax.TotalSeconds));
                        backoff = next;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Consumer loop crashed");
        }
    }

    private void ApplyDecision(SignalBatchResponse decision)
    {
        logger.LogInformation("Decision={Decision} Risk={Risk} Msg={Msg}",
            decision.Decision, decision.RiskScore, decision.Message);

        // If server suggests next interval, update producer’s schedule atomically
        if (decision.NextReportSeconds is > 0 and < 3600)
        {
            Interlocked.Exchange(ref _reportEverySeconds, decision.NextReportSeconds.Value);
        }

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

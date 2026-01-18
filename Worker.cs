using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent;

public interface IAgentIdentity
{
    string DeviceId { get; }
    string SessionId { get; }
}

public sealed class AgentIdentity : IAgentIdentity
{
    public string DeviceId { get; } = Environment.MachineName;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
}

public interface IAgentState
{
    int GetReportSecondsOrDefault(int fallbackSeconds);
    void TrySetReportSeconds(int seconds);
}

public sealed class AgentState : IAgentState
{
    private int _reportSeconds = 0;

    public int GetReportSecondsOrDefault(int fallbackSeconds)
    {
        var v = Volatile.Read(ref _reportSeconds);
        return v > 0 ? v : fallbackSeconds;
    }

    public void TrySetReportSeconds(int seconds)
    {
        if (seconds is >= 1 and <= 3600)
            Interlocked.Exchange(ref _reportSeconds, seconds);
    }
}

public interface ISignalProvider
{
    ValueTask<IReadOnlyList<Contracts.SignalEvent>> CollectAsync(CancellationToken ct);
}

public sealed class HeartbeatSignalProvider : ISignalProvider
{
    public ValueTask<IReadOnlyList<Contracts.SignalEvent>> CollectAsync(CancellationToken ct)
    {
        IReadOnlyList<Contracts.SignalEvent> events = new[]
        {
            new Contracts.SignalEvent(
                TimestampUtc: DateTimeOffset.UtcNow,
                Type: "Heartbeat",
                Payload: new Dictionary<string, string>
                {
                    ["user"] = Environment.UserName,
                    ["pid"]  = Environment.ProcessId.ToString()
                })
        };

        return ValueTask.FromResult(events);
    }
}

public interface IDecisionHandler
{
    void Handle(Contracts.StatusResponse status);
}

public sealed class DefaultDecisionHandler(
    ILogger<DefaultDecisionHandler> logger,
    IAgentState state) : IDecisionHandler
{
    public void Handle(Contracts.StatusResponse status)
    {
        // Skeleton only: log + update next interval if present.
        logger.LogInformation("Status decision={Decision} risk={Risk} msg={Msg}",
            status.Decision, status.RiskScore, status.Message);

        if (status.NextReportSeconds is { } s)
            state.TrySetReportSeconds(s);

        // Enforcement intentionally not implemented yet.
    }
}

public sealed class BackendClient(
    HttpClient http,
    IOptions<BackendOptions> backendOptions)
{
    private readonly BackendOptions _opts = backendOptions.Value;

    // NEW: /send only acknowledges (no decision expected here)
    public async Task SendAsync(Contracts.SignalBatchRequest req, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync(_opts.SendPath, req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Send failed status {(int)resp.StatusCode}");
    }

    // NEW: /status returns decision/status
    public async Task<Contracts.StatusResponse?> PollStatusAsync(Contracts.StatusRequest req, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync(_opts.StatusPath, req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Status failed status {(int)resp.StatusCode}");

        return await resp.Content.ReadFromJsonAsync<Contracts.StatusResponse>(cancellationToken: ct);
    }
}

// 1) Producer: collects events -> outgoing queue
public sealed class BatchProducerService(
    ILogger<BatchProducerService> logger,
    Channel<Contracts.SignalBatchRequest> outgoingQueue,
    IOptions<AgentOptions> agentOptions,
    IAgentIdentity identity,
    IAgentState state,
    IEnumerable<ISignalProvider> signalProviders)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Producer started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var events = new List<Contracts.SignalEvent>(capacity: 8);

                foreach (var p in signalProviders)
                {
                    var batch = await p.CollectAsync(stoppingToken);
                    if (batch.Count > 0) events.AddRange(batch);
                }

                var req = new Contracts.SignalBatchRequest(
                    DeviceId: identity.DeviceId,
                    SessionId: identity.SessionId,
                    SentAt: DateTimeOffset.UtcNow,
                    Events: events);

                await outgoingQueue.Writer.WriteAsync(req, stoppingToken);

                var interval = state.GetReportSecondsOrDefault(agentOptions.Value.DefaultReportSeconds);
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Producer crashed");
        }
        finally
        {
            outgoingQueue.Writer.TryComplete();
            logger.LogInformation("Producer stopped");
        }
    }
}

// 2) Sender: outgoing queue -> /send (retry with backoff)
public sealed class BatchSendService(
    ILogger<BatchSendService> logger,
    Channel<Contracts.SignalBatchRequest> outgoingQueue,
    BackendClient backend)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Sender started");

        var backoff = TimeSpan.FromSeconds(1);
        var backoffMax = TimeSpan.FromSeconds(30);

        try
        {
            await foreach (var req in outgoingQueue.Reader.ReadAllAsync(stoppingToken))
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await backend.SendAsync(req, stoppingToken);

                        backoff = TimeSpan.FromSeconds(1); // reset after success
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Send failed; retrying in {delay}s", backoff.TotalSeconds);
                        await Task.Delay(backoff, stoppingToken);
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax.TotalSeconds));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sender crashed");
        }
        finally
        {
            logger.LogInformation("Sender stopped");
        }
    }
}

// 3) Status poller: /status -> decision queue (separate pipeline)
public sealed class StatusPollService(
    ILogger<StatusPollService> logger,
    Channel<Contracts.StatusResponse> decisionQueue,
    IOptions<AgentOptions> agentOptions,
    IAgentIdentity identity,
    BackendClient backend)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Status poller started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var req = new Contracts.StatusRequest(identity.DeviceId, identity.SessionId);
                    var status = await backend.PollStatusAsync(req, stoppingToken);

                    if (status is not null)
                        await decisionQueue.Writer.WriteAsync(status, stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // keep polling even if status endpoint fails
                    logger.LogWarning(ex, "Status poll failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(agentOptions.Value.StatusPollSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            decisionQueue.Writer.TryComplete();
            logger.LogInformation("Status poller stopped");
        }
    }
}

// 4) Decision processor: decision queue -> handler (state update, later enforcement)
public sealed class DecisionProcessorService(
    ILogger<DecisionProcessorService> logger,
    Channel<Contracts.StatusResponse> decisionQueue,
    IDecisionHandler handler)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Decision processor started");

        try
        {
            await foreach (var status in decisionQueue.Reader.ReadAllAsync(stoppingToken))
            {
                handler.Handle(status);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Decision processor crashed");
        }
        finally
        {
            logger.LogInformation("Decision processor stopped");
        }
    }
}
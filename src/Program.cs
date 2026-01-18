using EndpointSignalAgent.Clients;
using EndpointSignalAgent.Configuration;
using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.Handlers;
using EndpointSignalAgent.Identity;
using EndpointSignalAgent.Providers;
using EndpointSignalAgent.Services;
using EndpointSignalAgent.State;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => { options.ServiceName = "EndpointSignalAgent"; });

// Options
builder.Services.AddOptions<BackendOptions>()
    .Bind(builder.Configuration.GetSection("Backend"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Backend:BaseUrl is required")
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "Backend:BaseUrl must be an absolute URL")
    .ValidateOnStart();

builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection("Agent"))
    .Validate(o => o.OutgoingQueueCapacity is >= 10 and <= 100_000, "Agent:OutgoingQueueCapacity out of range")
    .Validate(o => o.DecisionQueueCapacity is >= 10 and <= 100_000, "Agent:DecisionQueueCapacity out of range")
    .Validate(o => o.DefaultReportSeconds is >= 1 and <= 3600, "Agent:DefaultReportSeconds out of range")
    .Validate(o => o.StatusPollSeconds is >= 1 and <= 3600, "Agent:StatusPollSeconds out of range")
    .ValidateOnStart();

// Queue A: outgoing batches
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return Channel.CreateBounded<SignalBatchRequest>(new BoundedChannelOptions(opts.OutgoingQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
        SingleReader = true
    });
});

// Queue B: incoming decisions/status
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return Channel.CreateBounded<StatusResponse>(new BoundedChannelOptions(opts.DecisionQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
        SingleReader = true
    });
});

// Shared state + identity
builder.Services.AddSingleton<IAgentState, AgentState>();
builder.Services.AddSingleton<IAgentIdentity, AgentIdentity>();

// Stub provider + stub decision handler
builder.Services.AddSingleton<ISignalProvider, HeartbeatSignalProvider>();
builder.Services.AddSingleton<IDecisionHandler, DefaultDecisionHandler>();

// Collectors

builder.Services.AddSingleton<ISignalProvider>(_ =>
    new SpoolFileSignalProvider(
        spoolPath: @"spool\signals.jsonl",
        offsetPath: @"spool\signals.offset"));


// Backend client
builder.Services.AddHttpClient<BackendClient>((sp, client) =>
{
    var b = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    client.BaseAddress = new Uri(b.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(b.TimeoutSeconds);
});

// Hosted services
builder.Services.AddHostedService<BatchProducerService>();
builder.Services.AddHostedService<BatchSendService>();
builder.Services.AddHostedService<StatusPollService>();
builder.Services.AddHostedService<DecisionProcessorService>();

var host = builder.Build();
host.Run();

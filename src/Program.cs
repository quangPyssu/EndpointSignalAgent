using EndpointSignalAgent.Bootstrap.Backend;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.FeatureExtraction.Configuration;
using EndpointSignalAgent.FeatureExtraction.Services;
using EndpointSignalAgent.FeatureExtraction.Storage;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.Handlers;
using EndpointSignalAgent.Shared.Services;
using EndpointSignalAgent.Shared.State;
using EndpointSignalAgent.SignalCollection.Collectors;
using EndpointSignalAgent.SignalCollection.Providers;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => { options.ServiceName = "EndpointSignalAgent"; });

#region Configuration & Options

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

builder.Services.AddOptions<FeatureExtractorOptions>()
    .Bind(builder.Configuration.GetSection("FeatureExtractor"))
    .Validate(o => o.WindowSizeSeconds is >= 10 and <= 3600, "FeatureExtractor:WindowSizeSeconds out of range")
    .Validate(o => o.WindowSlideSeconds is >= 5 and <= 3600, "FeatureExtractor:WindowSlideSeconds out of range")
    .Validate(o => o.MaxEventsPerWindow is >= 100 and <= 100_000, "FeatureExtractor:MaxEventsPerWindow out of range")
    .ValidateOnStart();

#endregion

#region Channels & Queues

// Separate Signal Channels for Broadcast Pattern
// SignalWriterService Channel
var signalWriterChannel = Channel.CreateBounded<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)>(
    new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleWriter = false,
        SingleReader = true
    });

// FeatureExtractorService Channel
var featureExtractorChannel = Channel.CreateBounded<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)>(
    new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleWriter = false,
        SingleReader = true
    });

// Register SignalBroadcaster (writes to both channels)
builder.Services.AddSingleton<EndpointSignalAgent.SignalCollection.Broadcasting.ISignalBroadcaster>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<EndpointSignalAgent.SignalCollection.Broadcasting.SignalBroadcaster>>();
    var writers = new[]
    {
        signalWriterChannel.Writer,
        featureExtractorChannel.Writer
    };
    
    return new EndpointSignalAgent.SignalCollection.Broadcasting.SignalBroadcaster(logger, writers);
});

// Register readers for each consumer using wrapper interfaces
builder.Services.AddSingleton<EndpointSignalAgent.SignalCollection.Broadcasting.ISignalWriterChannelReader>(
    new EndpointSignalAgent.SignalCollection.Broadcasting.SignalWriterChannelReader(signalWriterChannel.Reader));

builder.Services.AddSingleton<EndpointSignalAgent.FeatureExtraction.Broadcasting.IFeatureExtractorChannelReader>(
    new EndpointSignalAgent.FeatureExtraction.Broadcasting.FeatureExtractorChannelReader(featureExtractorChannel.Reader));

// Outgoing Signal Batches Queue
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

// Incoming Decisions/Status Queue
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

#endregion

#region Bootstrap: Identity & Backend

// Identity & Enrollment
builder.Services.AddSingleton<IAgentIdentity, AgentIdentity>();
builder.Services.AddSingleton<EnrollmentStore>();
builder.Services.AddSingleton<IEnrollmentStore>(sp => sp.GetRequiredService<EnrollmentStore>());
builder.Services.AddHostedService<EnrollOnStartupService>();

// Backend HTTP Clients
builder.Services.AddHttpClient<BackendClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
});

builder.Services.AddHttpClient("BackendClient", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
});

#endregion

#region SignalCollection: Collectors, Providers & Services

// Signal Providers
builder.Services.AddSingleton<ISignalProvider>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SpoolFileSignalProvider>>();
    return new SpoolFileSignalProvider(
        spoolPath: @"spool\signals.jsonl",
        offsetPath: @"spool\signals.offset",
        logger: logger);
});

// Signal Collectors
builder.Services.AddHostedService<SignalWriterService>();
builder.Services.AddHostedService<SessionStateCollector>();
builder.Services.AddHostedService<ApplicationUsageCollector>();
builder.Services.AddHostedService<NetworkContextCollector>();

// Signal Processing Pipeline
builder.Services.AddHostedService<BatchProducerService>();
builder.Services.AddHostedService<BatchSendService>();

#endregion

#region FeatureExtraction: Storage & Services

// Feature Storage
builder.Services.AddSingleton<IFeatureStore, FeatureStore>();

// Feature Services
builder.Services.AddHostedService<FeatureExtractorService>();
builder.Services.AddHostedService<FeatureUploadService>();
builder.Services.AddHostedService<FeatureCleanupService>();

#endregion

#region Shared: State & Decision Processing

// Shared State
builder.Services.AddSingleton<IAgentState, AgentState>();

// Decision Handlers
builder.Services.AddSingleton<IDecisionHandler, DefaultDecisionHandler>();

// Status & Decision Services
builder.Services.AddHostedService<StatusPollService>();
builder.Services.AddHostedService<DecisionProcessorService>();

#endregion

var host = builder.Build();
host.Run();

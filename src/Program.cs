using EndpointSignalAgent.Clients;
using EndpointSignalAgent.Collectors;
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

builder.Services.AddOptions<FeatureExtractorOptions>()
    .Bind(builder.Configuration.GetSection("FeatureExtractor"))
    .Validate(o => o.WindowSizeSeconds is >= 10 and <= 3600, "FeatureExtractor:WindowSizeSeconds out of range")
    .Validate(o => o.WindowSlideSeconds is >= 5 and <= 3600, "FeatureExtractor:WindowSlideSeconds out of range")
    .Validate(o => o.MaxEventsPerWindow is >= 100 and <= 100_000, "FeatureExtractor:MaxEventsPerWindow out of range")
    .ValidateOnStart();

// Signal writer channel (for collectors)
// NOTE: Changed SingleReader to false to allow both SignalWriterService and FeatureExtractorService to consume
builder.Services.AddSingleton(sp =>
{
    return Channel.CreateBounded<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
});

builder.Services.AddSingleton(sp =>
{
    var channel = sp.GetRequiredService<Channel<(SignalEventType, Dictionary<string, string>, string)>>();
    return channel.Reader;
});

builder.Services.AddSingleton(sp =>
{
    var channel = sp.GetRequiredService<Channel<(SignalEventType, Dictionary<string, string>, string)>>();
    return channel.Writer;
});

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

// Feature store
builder.Services.AddSingleton<IFeatureStore, FeatureStore>();

// Stub provider + stub decision handler
//builder.Services.AddSingleton<ISignalProvider, HeartbeatSignalProvider>();
builder.Services.AddSingleton<IDecisionHandler, DefaultDecisionHandler>();

// Collectors

builder.Services.AddSingleton<ISignalProvider>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SpoolFileSignalProvider>>();
    return new SpoolFileSignalProvider(
        spoolPath: @"spool\signals.jsonl",
        offsetPath: @"spool\signals.offset",
        logger: logger);
});


// Backend client
builder.Services.AddHttpClient<BackendClient>((sp, client) =>
{
    var b = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    client.BaseAddress = new Uri(b.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(b.TimeoutSeconds);
});

// Named HttpClient for feature upload
builder.Services.AddHttpClient("BackendClient", (sp, client) =>
{
    var b = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    client.BaseAddress = new Uri(b.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(b.TimeoutSeconds);
});

builder.Services.AddSingleton<EnrollmentStore>();
builder.Services.AddSingleton<IEnrollmentStore>(sp => sp.GetRequiredService<EnrollmentStore>());
builder.Services.AddHostedService<EnrollOnStartupService>();


// Hosted services
builder.Services.AddHostedService<SignalWriterService>(); // Must start before collectors
builder.Services.AddHostedService<FeatureExtractorService>(); // Parallel consumer with SignalWriterService
builder.Services.AddHostedService<FeatureUploadService>(); // Uploads unsent features to backend
builder.Services.AddHostedService<FeatureCleanupService>(); // Cleans up old sent features
builder.Services.AddHostedService<SessionStateCollector>();
builder.Services.AddHostedService<ApplicationUsageCollector>();
builder.Services.AddHostedService<NetworkContextCollector>();

builder.Services.AddHostedService<BatchProducerService>();
builder.Services.AddHostedService<BatchSendService>();
builder.Services.AddHostedService<StatusPollService>();
builder.Services.AddHostedService<DecisionProcessorService>();

var host = builder.Build();
host.Run();

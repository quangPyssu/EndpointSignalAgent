using EndpointSignalAgent.Bootstrap.Backend;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Services;
using EndpointSignalAgent.DatasetCollection.Storage;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace EndpointSignalAgent.Bootstrap;

public static class AgentHostBootstrap
{
    public static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureServices(builder);
        return builder.Build();
    }

    public static void ConfigureServices(HostApplicationBuilder builder)
    {
        var configuredMode = builder.Configuration["Agent:Mode"] ?? AgentModes.DatasetCollection;
        if (!AgentModes.IsValid(configuredMode))
        {
            throw new InvalidOperationException("Agent:Mode must be either 'Normal' or 'DatasetCollection'.");
        }

        var isDatasetMode = AgentModes.IsDatasetCollection(configuredMode);

        builder.Services.AddOptions<BackendOptions>()
            .Bind(builder.Configuration.GetSection("Backend"))
            .PostConfigure(o =>
            {
                if (isDatasetMode)
                {
                    o.UseBackend = false;
                }
            })
            .Validate(o => !o.UseBackend || !string.IsNullOrWhiteSpace(o.BaseUrl), "Backend:BaseUrl is required when Backend:UseBackend is true")
            .Validate(o => !o.UseBackend || Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "Backend:BaseUrl must be an absolute URL when Backend:UseBackend is true")
            .ValidateOnStart();

        builder.Services.AddOptions<AgentOptions>()
            .Bind(builder.Configuration.GetSection("Agent"))
            .PostConfigure(o =>
            {
                o.Mode = string.IsNullOrWhiteSpace(o.Mode) ? AgentModes.DatasetCollection : o.Mode;
            })
            .Validate(o => AgentModes.IsValid(o.Mode), "Agent:Mode must be either 'Normal' or 'DatasetCollection'")
            .Validate(o => o.OutgoingQueueCapacity is >= 10 and <= 100_000, "Agent:OutgoingQueueCapacity out of range")
            .Validate(o => o.DecisionQueueCapacity is >= 10 and <= 100_000, "Agent:DecisionQueueCapacity out of range")
            .Validate(o => o.DefaultReportSeconds is >= 1 and <= 3600, "Agent:DefaultReportSeconds out of range")
            .Validate(o => o.StatusPollSeconds is >= 1 and <= 3600, "Agent:StatusPollSeconds out of range")
            .ValidateOnStart();

        builder.Services.AddOptions<FeatureExtractorOptions>()
            .Bind(builder.Configuration.GetSection("FeatureExtractor"))
            .PostConfigure(o =>
            {
                if (isDatasetMode)
                {
                    o.EnableLiveExtraction = false;
                }
            })
            .Validate(o => o.WindowSizeSeconds is >= 10 and <= 3600, "FeatureExtractor:WindowSizeSeconds out of range")
            .Validate(o => o.WindowSlideSeconds is >= 5 and <= 3600, "FeatureExtractor:WindowSlideSeconds out of range")
            .Validate(o => o.MaxEventsPerWindow is >= 100 and <= 100_000, "FeatureExtractor:MaxEventsPerWindow out of range")
            .ValidateOnStart();

        builder.Services.AddOptions<DatasetCollectionOptions>()
            .Bind(builder.Configuration.GetSection(DatasetCollectionOptions.SectionName))
            .Validate(o => o.DailyActiveHourTarget >= 0, "DatasetCollection:DailyActiveHourTarget must be >= 0")
            .Validate(o => o.WeeklyActiveDayTarget is >= 0 and <= 7, "DatasetCollection:WeeklyActiveDayTarget out of range")
            .Validate(o => o.StudyWeekTarget >= 0, "DatasetCollection:StudyWeekTarget must be >= 0")
            .ValidateOnStart();

        var signalWriterChannel = Channel.CreateBounded<EndpointSignalAgent.SignalCollection.Broadcasting.BroadcastSignal>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true
            });

        var featureExtractorChannel = Channel.CreateBounded<EndpointSignalAgent.SignalCollection.Broadcasting.BroadcastSignal>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true
            });

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

        builder.Services.AddSingleton<EndpointSignalAgent.SignalCollection.Broadcasting.ISignalWriterChannelReader>(
            new EndpointSignalAgent.SignalCollection.Broadcasting.SignalWriterChannelReader(signalWriterChannel.Reader));

        builder.Services.AddSingleton<EndpointSignalAgent.FeatureExtraction.Broadcasting.IFeatureExtractorChannelReader>(
            new EndpointSignalAgent.FeatureExtraction.Broadcasting.FeatureExtractorChannelReader(featureExtractorChannel.Reader));

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

        builder.Services.AddSingleton<IAgentIdentity, AgentIdentity>();
        builder.Services.AddSingleton<EnrollmentStore>();
        builder.Services.AddSingleton<IEnrollmentStore>(sp => sp.GetRequiredService<EnrollmentStore>());
        builder.Services.AddHostedService<EnrollOnStartupService>();

        builder.Services.AddHttpClient<BackendClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
            if (opts.UseBackend)
            {
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
            }
        });

        builder.Services.AddHttpClient("BackendClient", (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
            if (opts.UseBackend)
            {
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
            }
        });

        builder.Services.AddSingleton<ISignalProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SpoolFileSignalProvider>>();
            return new SpoolFileSignalProvider(
                spoolPath: @"spool\signals.jsonl",
                offsetPath: @"spool\signals.offset",
                logger: logger);
        });

        builder.Services.AddSingleton<ICollectionControl, CollectionControl>();

        builder.Services.AddHostedService<SignalWriterService>();
        builder.Services.AddHostedService<SessionStateCollector>();
        builder.Services.AddHostedService<ApplicationUsageCollector>();
        builder.Services.AddHostedService<NetworkContextCollector>();
        builder.Services.AddHostedService<SystemResourceCollector>();

        builder.Services.AddSingleton<IFeatureStore, FeatureStore>();
        builder.Services.AddSingleton<FeatureExtractorService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FeatureExtractorService>());

        builder.Services.AddSingleton<KeyboardCommandService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<KeyboardCommandService>());

        if (!isDatasetMode)
        {
            builder.Services.AddHostedService<BatchProducerService>();
            builder.Services.AddHostedService<BatchSendService>();
            builder.Services.AddHostedService<FeatureUploadService>();
            builder.Services.AddHostedService<FeatureCleanupService>();

            builder.Services.AddSingleton<IAgentState, AgentState>();
            builder.Services.AddSingleton<IDecisionHandler, DefaultDecisionHandler>();
            builder.Services.AddHostedService<StatusPollService>();
            builder.Services.AddHostedService<DecisionProcessorService>();
        }
        else
        {
            builder.Services.AddSingleton<SessionManifestStore>();
            builder.Services.AddSingleton<AnnotationStore>();
            builder.Services.AddSingleton<ProgressStateStore>();
            builder.Services.AddSingleton<ICollectionManifestService, CollectionManifestService>();
            builder.Services.AddSingleton<ICollectionSessionService, CollectionSessionService>();
            builder.Services.AddSingleton<IAbnormalTaggingService, AbnormalTaggingService>();
            builder.Services.AddSingleton<IProgressTrackingService, ProgressTrackingService>();
            builder.Services.AddHostedService(sp => (ProgressTrackingService)sp.GetRequiredService<IProgressTrackingService>());
            builder.Services.AddSingleton<DatasetExportService>();
        }
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Infrastructure.Observation;

namespace Ashlar.BackgroundAgents.Observation;

/// <summary>
/// Background service that runs the observation pipeline: watches file system and processes,
/// gates events through IObservationGate, detects patterns, and stores them.
/// </summary>
public sealed class ObservationPipelineService : BackgroundService
{
    private readonly ObservationPipelineOptions _options;
    private readonly IPatternStore _patternStore;
    private readonly IObservationGate? _observationGate;
    private readonly ILogger<ObservationPipelineService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Creates the observation pipeline service.
    /// </summary>
    public ObservationPipelineService(
        IOptions<ObservationPipelineOptions> options,
        IPatternStore patternStore,
        ILogger<ObservationPipelineService> logger,
        ILoggerFactory loggerFactory,
        IObservationGate? observationGate = null)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _patternStore = patternStore ?? throw new ArgumentNullException(nameof(patternStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _observationGate = observationGate;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var repoRoot = _options.RepoRoot ?? RepoPathResolver.FindRepoRoot();
        var projectPath = repoRoot;

        var watchPaths = _options.WatchPaths
            .Select(p => Path.Combine(repoRoot ?? Directory.GetCurrentDirectory(), p.TrimStart('/', '\\')))
            .Where(Directory.Exists)
            .ToList();

        if (watchPaths.Count == 0)
        {
            _logger.LogWarning("No watch paths exist under {Repo}. Observation pipeline idle.", repoRoot);
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }

        var patternDetector = new PatternDetector(
            TimeSpan.FromSeconds(_options.PatternWindowSeconds),
            _options.RepeatedEditThreshold,
            _patternStore,
            _loggerFactory.CreateLogger<PatternDetector>());

        // Disposing the file source is only safe because CompositeEventSource joins its children
        // before SubscribeAsync returns: the watchers are already torn down by the time this
        // using runs, and a callback that slipped through is a dropped TryWrite rather than an
        // exception on a thread with no handler above it.
        using var fileSource = new FileSystemEventSource(
            watchPaths,
            projectPath,
            _options.FileFilters,
            _loggerFactory.CreateLogger<FileSystemEventSource>());
        var processSource = new ProcessEventSource(
            _options.ProcessFilters,
            projectPath,
            TimeSpan.FromSeconds(2),
            _loggerFactory.CreateLogger<ProcessEventSource>());
        var compositeSource = new CompositeEventSource(
            new IObservableEventSource[] { fileSource, processSource },
            _loggerFactory.CreateLogger<CompositeEventSource>());

        _logger.LogInformation("Observation pipeline started. Watching {Count} path(s) under {Root}", watchPaths.Count, repoRoot);

        try
        {
            await foreach (var evt in compositeSource.SubscribeAsync(stoppingToken).WithCancellation(stoppingToken))
            {
                if (_observationGate != null && !_observationGate.ShouldObserve(evt.Category, evt.SourceId, evt.ProjectPath))
                    continue;

                await patternDetector.ProcessAsync(evt, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Observation pipeline stopping");
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is IOException)
            {
                _logger.LogWarning(
                    ex,
                    "Observation pipeline degraded due to storage/permission issue. " +
                    "Service will stay active and continue retrying with next events.");
                return;
            }

            _logger.LogError(ex, "Observation pipeline failed: {Error}", ex.Message);
            throw;
        }
    }
}

using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Options;

namespace FoundryLocalLlmServer.Server;

/// <summary>Lifecycle phases of the in-process Foundry Local runtime.</summary>
public enum FoundryReadinessState
{
    NotStarted,
    CreatingManager,
    RegisteringExecutionProvider,
    DownloadingModel,
    LoadingModel,
    StartingWebService,
    Ready,
    Failed,
}

/// <summary>Point-in-time snapshot of the bootstrap state, safe to serialize.</summary>
public sealed record FoundryStatus(
    FoundryReadinessState State,
    string? ExecutionProvider,
    double EpDownloadPercent,
    double ModelDownloadPercent,
    string? ModelId,
    string? Endpoint,
    string? LastError,
    int Attempt,
    DateTimeOffset? StartedAt);

/// <summary>
/// Initializes the Foundry Local v1.2 SDK in the background: registers the GPU
/// execution provider (one-time, potentially very large download), downloads and
/// loads the configured model, and starts the SDK's embedded OpenAI-compatible
/// web service. The HTTP proxy returns 503 with readiness detail until
/// <see cref="FoundryReadinessState.Ready"/>. GPU is mandatory — there is no
/// silent CPU fallback.
/// </summary>
public sealed class FoundryLocalBootstrapper(
    IOptions<FoundryLocalOptions> options,
    ILogger<FoundryLocalBootstrapper> logger) : BackgroundService
{
    private volatile FoundryStatus _status = new(
        FoundryReadinessState.NotStarted, null, 0, 0, null, null, null, 0, null);

    /// <summary>Current bootstrap status snapshot.</summary>
    public FoundryStatus Status => _status;

    /// <summary>True once the embedded Foundry web service is accepting requests.</summary>
    public bool IsReady => _status.State == FoundryReadinessState.Ready;

    /// <summary>Base endpoint of the embedded Foundry web service once ready.</summary>
    public string? Endpoint => _status.Endpoint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (opts.UseStubResponses)
        {
            logger.LogInformation("Stub responses enabled — skipping Foundry Local SDK initialization.");
            return;
        }

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                // Generous per-attempt timeout: the EP download is hundreds of MB on first run.
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                attemptCts.CancelAfter(TimeSpan.FromMinutes(opts.InitializationTimeoutMinutes));

                await InitializeAsync(opts, attempt, attemptCts.Token);
                return; // Ready
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Foundry Local initialization attempt {Attempt} failed", attempt);
                _status = _status with { State = FoundryReadinessState.Failed, LastError = ex.Message, Attempt = attempt };

                if (attempt >= opts.MaxInitializationAttempts)
                {
                    logger.LogError("Giving up after {Attempts} initialization attempts.", attempt);
                    return;
                }

                var backoff = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, attempt - 1), 300));
                logger.LogInformation("Retrying Foundry initialization in {Backoff}...", backoff);
                try { await Task.Delay(backoff, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task InitializeAsync(FoundryLocalOptions opts, int attempt, CancellationToken ct)
    {
        _status = new FoundryStatus(
            FoundryReadinessState.CreatingManager, opts.ExecutionProvider, 0, 0, null, null, null, attempt, DateTimeOffset.UtcNow);

        // Single-threaded by design: only this BackgroundService's sequential retry
        // loop calls InitializeAsync, so the IsInitialized check cannot race.
        if (!FoundryLocalManager.IsInitialized)
        {
            await FoundryLocalManager.CreateAsync(
                new Configuration
                {
                    AppName = opts.AppName,
                    Web = new Configuration.WebService { Urls = opts.WebServiceUrls },
                },
                logger,
                ct);
        }

        var manager = FoundryLocalManager.Instance;

        // ── Execution provider: register exactly the configured EP (default CUDA).
        // Requesting only the needed EP avoids multi-GB downloads of unrelated providers.
        _status = _status with { State = FoundryReadinessState.RegisteringExecutionProvider };
        var eps = manager.DiscoverEps();
        logger.LogInformation("Discovered EPs: {Eps}",
            string.Join(", ", eps.Select(e => $"{e.Name} (registered={e.IsRegistered})")));

        var targetEp = eps.FirstOrDefault(e =>
            string.Equals(e.Name, opts.ExecutionProvider, StringComparison.OrdinalIgnoreCase));
        if (targetEp is null)
        {
            throw new InvalidOperationException(
                $"Execution provider '{opts.ExecutionProvider}' not available. " +
                $"Available: {string.Join(", ", eps.Select(e => e.Name))}");
        }

        if (!targetEp.IsRegistered)
        {
            logger.LogInformation("Downloading and registering EP {Ep} (first run may take a long time)...", targetEp.Name);
            var epResult = await manager.DownloadAndRegisterEpsAsync(
                [targetEp.Name],
                (epName, percent) => _status = _status with { EpDownloadPercent = percent },
                ct);
            if (!epResult.Success || (epResult.FailedEps?.Contains(targetEp.Name) ?? false))
            {
                // GPU is mandatory — fail loudly rather than silently falling back to CPU.
                throw new InvalidOperationException(
                    $"Failed to register execution provider '{targetEp.Name}': {epResult.Status}");
            }
        }
        _status = _status with { EpDownloadPercent = 100 };

        // ── Model: resolve alias, prefer the variant matching the configured EP/GPU.
        var catalog = await manager.GetCatalogAsync(ct);
        var model = await catalog.GetModelAsync(opts.Model, ct)
            ?? throw new InvalidOperationException($"Model '{opts.Model}' not found in the Foundry catalog.");

        var gpuVariant = SelectGpuVariant(model, opts.ExecutionProvider);
        if (gpuVariant is not null)
        {
            model.SelectVariant(gpuVariant);
        }
        else if (opts.RequireGpu)
        {
            throw new InvalidOperationException(
                $"No GPU variant of '{opts.Model}' found and FoundryLocal:RequireGpu is true. " +
                $"Variants: {string.Join(", ", model.Variants.Select(v => v.Id))}");
        }

        _status = _status with { ModelId = model.Id };
        logger.LogInformation("Selected model variant {ModelId}", model.Id);

        if (!await model.IsCachedAsync(ct))
        {
            _status = _status with { State = FoundryReadinessState.DownloadingModel };
            logger.LogInformation("Downloading model {ModelId}...", model.Id);
            await model.DownloadAsync(
                percent => _status = _status with { ModelDownloadPercent = percent },
                ct);
        }
        _status = _status with { ModelDownloadPercent = 100 };

        _status = _status with { State = FoundryReadinessState.LoadingModel };
        logger.LogInformation("Loading model {ModelId}...", model.Id);
        await model.LoadAsync(ct);

        // ── Embedded OpenAI-compatible web service the proxy forwards to.
        _status = _status with { State = FoundryReadinessState.StartingWebService };
        await manager.StartWebServiceAsync(ct);
        var endpoint = manager.Urls?.FirstOrDefault()
            ?? throw new InvalidOperationException("Foundry web service started but reported no URL.");
        endpoint = endpoint.TrimEnd('/');

        _status = _status with { State = FoundryReadinessState.Ready, Endpoint = endpoint, LastError = null };
        logger.LogInformation("Foundry Local ready: model {ModelId} on {Endpoint}", model.Id, endpoint);
    }

    /// <summary>
    /// Picks the model variant that runs on GPU, preferring the one built for the
    /// configured execution provider (e.g. the *-cuda-gpu variant for CUDA).
    /// Returns null when the model has no GPU variant.
    /// </summary>
    private static IModel? SelectGpuVariant(IModel model, string executionProvider)
    {
        var gpuVariants = model.Variants
            .Where(v => v.Info?.Runtime?.DeviceType == DeviceType.GPU)
            .ToArray();

        var epMatch = gpuVariants.FirstOrDefault(v =>
            v.Info!.Runtime!.ExecutionProvider?.Contains(executionProvider, StringComparison.OrdinalIgnoreCase) == true);

        return epMatch ?? gpuVariants.FirstOrDefault();
    }

    public override void Dispose()
    {
        try
        {
            if (FoundryLocalManager.IsInitialized)
                FoundryLocalManager.Instance.Dispose();
        }
        catch { /* best-effort native cleanup */ }
        base.Dispose();
    }
}

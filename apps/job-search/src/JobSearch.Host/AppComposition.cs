using Ashlar.Abstractions;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Authoring;
using Ashlar.WebOperations;
using Ashlar.WebOperations.Bricks;
using Ashlar.Core.Application.Workflows;
using Ashlar.Core.Domain.Execution;
using Ashlar.Hosting;
using Ashlar.Hosting.Sdk.Extensions;
using JobSearch.Accounts;
using JobSearch.Bricks;
using JobSearch.Certified;
using JobSearch.Host.Studio;
using JobSearch.Materials;
using JobSearch.Privacy;
using JobSearch.Profile;
using JobSearch.Runs;
using JobSearch.Store;
using JobSearch.Studio;
using JobSearch.Workspace;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASHLAREXP002 // IEgressGuard and EgressGuard are the experimental egress surface; the app passes a guard to its Google, web-operations and model clients by design.

namespace JobSearch.Host;

/// <summary>How the app reaches Google and employer job sites.</summary>
public sealed record AppOptions
{
    /// <summary>Where the database, secrets and (fake mode) the simulated Drive live.</summary>
    public string DataDir { get; init; } = ".jobsearch";
    /// <summary>fake (simulated Google accounts) or google (real Drive/Sheets through each user's OAuth token).</summary>
    public string GoogleMode { get; init; } = "fake";
    /// <summary>OAuth client id (google mode).</summary>
    public string? GoogleClientId { get; init; }
    /// <summary>OAuth client secret (google mode).</summary>
    public string? GoogleClientSecret { get; init; }
    /// <summary>The web-operations client (employer job sites); null builds one over the bundled specs plus DataDir/specs.</summary>
    public WebOperationClient? Web { get; init; }
    /// <summary>Simulated Google accounts to use in fake mode; null loads them from DataDir.</summary>
    public FakeGoogle? Fake { get; init; }
    /// <summary>The egress guard for the Google client; null for the process default.</summary>
    public IEgressGuard? Guard { get; init; }
    /// <summary>Wait used for retries; tests pass a no-op.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    /// <summary>Where the certified bricks are deployed; null for the copy next to the app.</summary>
    public string? CertifiedDir { get; init; }
    /// <summary>The server-hosted model for anything with personal data; null builds one from the environment.</summary>
    public IPrivateModel? PrivateModel { get; init; }
    /// <summary>The external step writer; null builds one from the environment.</summary>
    public IStepWriter? StepWriter { get; init; }
    /// <summary>Synthetic mode (QA and demos): deterministic stand-ins for both model tiers; the web host marks itself synthetic.</summary>
    public bool Synthetic { get; init; }
    /// <summary>Base64 Ed25519 private key that signs certified steps; null reads JOBSEARCH_SIGNING_KEY or a development key.</summary>
    public string? SigningKey { get; init; }
    /// <summary>Base64 AES-256 key for secrets at rest; null reads JOBSEARCH_MASTER_KEY or a development key.</summary>
    public string? MasterKey { get; init; }
    /// <summary>Where starter steps are; null for the copy next to the app.</summary>
    public string? SeedsDir { get; init; }
    /// <summary>A cache of certification verdicts keyed by source and witness (tests share one); null disables it.</summary>
    public string? CertificationCacheDir { get; init; }
    /// <summary>Limits on new steps.</summary>
    public ForgeLimits ForgeLimits { get; init; } = new();
    /// <summary>Step-host processes.</summary>
    public int StepHosts { get; init; } = 2;
    /// <summary>How long one step may run on one posting.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>Ashlar's real composition root (AddAshlarSdk + AddAshlar) with the app's bricks registered.</summary>
public sealed class AshlarRuntime : IAshlarRuntime, IDisposable
{
    /// <summary>The bricks the app registers and requires.</summary>
    public static readonly string[] RequiredBricks =
    [
        "web.call", "web.learn", "jobs.list-board", "jobs.get-posting", "jobsearch.screen", "jobsearch.decide", "jobsearch.posting-fields",
        "jobsearch.tailor", "jobsearch.validate-materials", "generative-artifact", .. CertifiedIds.All,
    ];

    private readonly ServiceProvider _provider;
    private readonly CertifiedBricks _certified;
    private readonly bool _ownsCertified;

    /// <summary>
    /// Builds the Ashlar host. <paramref name="certified"/> null loads (and owns) the deployed certified bricks;
    /// <paramref name="library"/> puts the studio's library steps in front of the executor.
    /// </summary>
    public AshlarRuntime(WebOperationClient web, CertifiedBricks? certified = null, IPrivateModel? privateModel = null, StepLibrary? library = null)
    {
        _ownsCertified = certified is null;
        _certified = certified ?? CertifiedBricks.Load(CertifiedBricks.DefaultDirectory);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(web); // the web-operation bricks take it through their constructor
        services.AddSingleton<ICertifiedRules>(_certified);
        services.AddSingleton(privateModel ?? new UnavailablePrivateModel());
        services.AddAshlarSdk(sdk => sdk
            .RegisterBrick<WebCallBrick>()
            .RegisterBrick<WebLearnBrick>()
            .RegisterBrick<JobBoardBrick>()
            .RegisterBrick<JobPostingBrick>()
            .RegisterBrick<ScreenBrick>()
            .RegisterBrick<DecideBrick>()
            .RegisterBrick<PostingFieldsBrick>()
            .RegisterBrick<TailorBrick>()
            .RegisterBrick<ValidateMaterialsBrick>());
        services.AddAshlarGenerativeAgents();
        services.AddAshlarAgentProfile<CoverLetterProfile>();
        services.AddAshlar(o => o.RegisterBackgroundAgentHostedService = false);
        OverlayCertified(services, _certified, library);
        _provider = services.BuildServiceProvider();

        // The registry skips a brick it cannot construct without saying so; fail here, loudly, instead.
        var registry = _provider.GetRequiredService<IBrickRegistry>();
        var missing = RequiredBricks.Where(id => registry.GetBrick(id) is null).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Ashlar's brick registry ({registry.GetType().Name}) does not hold: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Replaces the last IBrickRegistry registration with the same one behind <see cref="CertifiedOverlayRegistry"/>, keeping
    /// its lifetime, so the workflow executor resolves the certified bricks to the verified instances.
    /// </summary>
    private static void OverlayCertified(IServiceCollection services, CertifiedBricks certified, StepLibrary? library)
    {
        var original = services.Last(d => d.ServiceType == typeof(IBrickRegistry) && !d.IsKeyedService);
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(IBrickRegistry), sp =>
        {
            var inner = original.ImplementationInstance as IBrickRegistry
                ?? original.ImplementationFactory?.Invoke(sp) as IBrickRegistry
                ?? (IBrickRegistry)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return new CertifiedOverlayRegistry(inner, certified, library);
        }, original.Lifetime));
    }

    /// <inheritdoc />
    public ICertifiedRules Certified => _certified;

    /// <inheritdoc />
    public AshlarScope CreateScope()
    {
        var scope = _provider.CreateScope();
        return new AshlarScope(scope.ServiceProvider.GetRequiredService<WorkflowExecutor>(), scope.ServiceProvider.GetRequiredService<IBrickRegistry>(), scope);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _provider.Dispose();
        if (_ownsCertified)
            _certified.Dispose();
    }
}

/// <summary>Everything the CLI, the dispatcher and the tests use, wired once.</summary>
public sealed class App : IDisposable
{
    /// <summary>Database.</summary>
    public Db Db { get; }
    /// <summary>Access.</summary>
    public AccessControl Access { get; }
    /// <summary>Accounts.</summary>
    public AccountService Accounts { get; }
    /// <summary>Profiles.</summary>
    public ProfileService Profiles { get; }
    /// <summary>Applications.</summary>
    public ApplicationService Applications { get; }
    /// <summary>Tracker.</summary>
    public TrackerService Tracker { get; }
    /// <summary>Runs.</summary>
    public RunEngine Engine { get; }
    /// <summary>Scheduler.</summary>
    public Dispatcher Dispatcher { get; }
    /// <summary>Snapshots.</summary>
    public Snapshots Snapshots { get; }
    /// <summary>Simulated Google (fake mode only).</summary>
    public FakeGoogle? Fake { get; }
    /// <summary>Secrets.</summary>
    public SecretStore Secrets { get; }
    /// <summary>The Ashlar runtime.</summary>
    public AshlarRuntime Ashlar { get; }
    /// <summary>The verified certified bricks.</summary>
    public CertifiedBricks Certified { get; }
    /// <summary>Platform keys.</summary>
    public StudioKeys Keys { get; }
    /// <summary>Each user's workflow.</summary>
    public WorkflowStore Workflows { get; }
    /// <summary>The shared step library.</summary>
    public StepLibrary Library { get; }
    /// <summary>The isolated step hosts.</summary>
    public StepHostPool StepHosts { get; }
    /// <summary>Certifies steps in a child process.</summary>
    public StepCertifier Certifier { get; }
    /// <summary>New steps from plain words.</summary>
    public ForgeService Forge { get; }
    /// <summary>Consent, export, erasure, retention.</summary>
    public Compliance Compliance { get; }
    /// <summary>The server-hosted model for personal data.</summary>
    public IPrivateModel PrivateModel { get; }
    /// <summary>Whether this instance runs on synthetic data only.</summary>
    public bool Synthetic { get; }
    /// <summary>Where the starter steps are.</summary>
    public string SeedsDir { get; }
    /// <summary>The data directory.</summary>
    public string DataDir { get; }
    /// <summary>Completes when the web host's worker has put the starter steps in the library.</summary>
    public TaskCompletionSource SeedsReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wires the app.</summary>
    public App(AppOptions o)
    {
        Directory.CreateDirectory(o.DataDir);
        Db = new Db(Path.Combine(o.DataDir, "jobsearch.db"));
        Access = new AccessControl(Db);
        Accounts = new AccountService(Db, Access);
        Profiles = new ProfileService(Db, Access);
        Applications = new ApplicationService(Db, Access);
        Tracker = new TrackerService(Db, Applications);
        DataDir = o.DataDir;
        Synthetic = o.Synthetic;
        var certifiedDir = o.CertifiedDir ?? CertifiedBricks.DefaultDirectory;
        var alsoTrusted = CertifiedBricks.PinnedKeys(certifiedDir)
            .Concat((Environment.GetEnvironmentVariable("JOBSEARCH_TRUSTED_KEYS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Keys = StudioKeys.Load(o.DataDir, o.SigningKey ?? Environment.GetEnvironmentVariable("JOBSEARCH_SIGNING_KEY"),
            o.MasterKey ?? Environment.GetEnvironmentVariable("JOBSEARCH_MASTER_KEY"), alsoTrusted);
        Secrets = new SecretStore(Path.Combine(o.DataDir, "secrets.json"), Keys.MasterKey);
        Fake = o.GoogleMode == "fake" ? o.Fake ?? new FakeGoogle(Path.Combine(o.DataDir, "fake-google.json")) : null;
        var fake = Fake;
        Func<Connection, IGoogleWorkspace> build = fake is not null
            ? conn => fake.Client(Secrets.Get(conn.TokenRef)?.GetValueOrDefault("access_token") ?? throw new AuthExpiredException("google", "no stored credential"))
            : conn => new GoogleRestWorkspace(Secrets, conn.TokenRef,
                o.GoogleClientId ?? throw new InvalidOperationException("GOOGLE_CLIENT_ID is not set"),
                o.GoogleClientSecret, o.Guard); // null: the environment's credential for oauth2.googleapis.com authenticates the client
        Certified = CertifiedBricks.Load(certifiedDir);
        Workflows = new WorkflowStore(Db, Access);
        StepHosts = new StepHostPool(Keys.TrustedKeys, o.StepHosts, o.StepTimeout);
        Library = new StepLibrary(Db, Path.Combine(o.DataDir, "library"), Keys, StepHosts);
        Certifier = new StepCertifier(Keys, cacheDir: o.CertificationCacheDir);
        SeedsDir = o.SeedsDir ?? Path.Combine(AppContext.BaseDirectory, "library-seeds");
        // Both model tiers go through an enforcing guard: a call off this host from a frame that read PII is refused.
        var modelGuard = new EgressGuard("full", "enforce");
        PrivateModel = o.PrivateModel ?? (o.Synthetic ? new SyntheticPrivateModel()
            : Environment.GetEnvironmentVariable("JOBSEARCH_PRIVATE_MODEL_URL") is { Length: > 0 } privateUrl
                ? new OllamaPrivateModel(new Uri(privateUrl), Environment.GetEnvironmentVariable("JOBSEARCH_PRIVATE_MODEL") ?? "llama3.1:8b", modelGuard)
                : new UnavailablePrivateModel());
        var writer = o.StepWriter ?? (o.Synthetic ? new SyntheticStepWriter()
            : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } apiKey
                ? new ClaudeStepWriter(apiKey, Environment.GetEnvironmentVariable("JOBSEARCH_FORGE_MODEL"), modelGuard)
                : new UnavailableStepWriter());
        Forge = new ForgeService(Db, Profiles, Workflows, Library, Certifier, PrivateModel, writer, o.ForgeLimits, Path.Combine(o.DataDir, "forge"));
        Compliance = new Compliance(Db, Secrets, Library, System.Security.Cryptography.HMACSHA256.HashData(Keys.MasterKey, "deletion-pepper"u8));
        Ashlar = new AshlarRuntime(o.Web ?? new WebOperationClient(new SpecStore(Path.Combine(o.DataDir, "specs")), o.Guard, now: () => Clock.Now), Certified, PrivateModel, Library);
        Engine = new RunEngine(Db, Accounts, Profiles, Tracker, new WorkspaceFactory(Accounts, build), Ashlar, o.Delay, new StudioWorkflowSource(Workflows, Library));
        Dispatcher = new Dispatcher(Db, Accounts, Engine);
        Snapshots = new Snapshots(Db, Applications);
    }

    /// <summary>Fake mode: creates a simulated Google account for the user and connects it (as the user).</summary>
    public string ConnectFakeGoogle(string userId, string accountEmail)
    {
        var token = (Fake ?? throw new InvalidOperationException("not in fake mode")).AddAccount(accountEmail);
        var tokenRef = $"google:{userId}";
        Secrets.Put(tokenRef, new Dictionary<string, string> { ["access_token"] = token });
        Accounts.Connect(userId, userId, "google", accountEmail, tokenRef);
        return token;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Ashlar.Dispose();
        Certified.Dispose();
        StepHosts.Dispose();
        (PrivateModel as IDisposable)?.Dispose();
    }
}

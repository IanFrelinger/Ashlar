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
using JobSearch.Materials;
using JobSearch.Profile;
using JobSearch.Runs;
using JobSearch.Store;
using JobSearch.Workspace;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASHLAREXP002 // IEgressGuard is the experimental egress surface; the app passes a guard to its Google and web-operations clients by design.

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
    /// <summary>The model for model-worded cover letters; null for the model Ashlar's host configuration provides.</summary>
    public IModel? Model { get; init; }
}

/// <summary>Ashlar's real composition root (AddAshlarSdk + AddAshlar) with the app's bricks registered.</summary>
public sealed class AshlarRuntime : IAshlarRuntime, IDisposable
{
    /// <summary>The bricks the app registers and requires.</summary>
    public static readonly string[] RequiredBricks =
    [
        "web.call", "web.learn", "jobs.list-board", "jobs.get-posting", "jobsearch.screen", "jobsearch.decide", "jobsearch.tailor",
        "jobsearch.validate-materials", "generative-artifact", .. CertifiedIds.All,
    ];

    private readonly ServiceProvider _provider;
    private readonly CertifiedBricks _certified;
    private readonly bool _ownsCertified;

    /// <summary>Builds the Ashlar host. <paramref name="certified"/> null loads (and owns) the deployed certified bricks.</summary>
    public AshlarRuntime(WebOperationClient web, CertifiedBricks? certified = null, IModel? model = null)
    {
        _ownsCertified = certified is null;
        _certified = certified ?? CertifiedBricks.Load(CertifiedBricks.DefaultDirectory);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(web); // the web-operation bricks take it through their constructor
        services.AddSingleton<ICertifiedRules>(_certified);
        services.AddAshlarSdk(sdk => sdk
            .RegisterBrick<WebCallBrick>()
            .RegisterBrick<WebLearnBrick>()
            .RegisterBrick<JobBoardBrick>()
            .RegisterBrick<JobPostingBrick>()
            .RegisterBrick<ScreenBrick>()
            .RegisterBrick<DecideBrick>()
            .RegisterBrick<TailorBrick>()
            .RegisterBrick<ValidateMaterialsBrick>());
        services.AddAshlarGenerativeAgents();
        services.AddAshlarAgentProfile<CoverLetterProfile>();
        services.AddAshlar(o => o.RegisterBackgroundAgentHostedService = false);
        if (model is not null)
            services.AddSingleton(model);
        OverlayCertified(services, _certified);
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
    private static void OverlayCertified(IServiceCollection services, CertifiedBricks certified)
    {
        var original = services.Last(d => d.ServiceType == typeof(IBrickRegistry) && !d.IsKeyedService);
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(IBrickRegistry), sp =>
        {
            var inner = original.ImplementationInstance as IBrickRegistry
                ?? original.ImplementationFactory?.Invoke(sp) as IBrickRegistry
                ?? (IBrickRegistry)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return new CertifiedOverlayRegistry(inner, certified);
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
        Secrets = new SecretStore(Path.Combine(o.DataDir, "secrets.json"));
        Fake = o.GoogleMode == "fake" ? o.Fake ?? new FakeGoogle(Path.Combine(o.DataDir, "fake-google.json")) : null;
        var fake = Fake;
        Func<Connection, IGoogleWorkspace> build = fake is not null
            ? conn => fake.Client(Secrets.Get(conn.TokenRef)?.GetValueOrDefault("access_token") ?? throw new AuthExpiredException("google", "no stored credential"))
            : conn => new GoogleRestWorkspace(Secrets, conn.TokenRef,
                o.GoogleClientId ?? throw new InvalidOperationException("GOOGLE_CLIENT_ID is not set"),
                o.GoogleClientSecret, o.Guard); // null: the environment's credential for oauth2.googleapis.com authenticates the client
        Certified = CertifiedBricks.Load(o.CertifiedDir ?? CertifiedBricks.DefaultDirectory);
        Ashlar = new AshlarRuntime(o.Web ?? new WebOperationClient(new SpecStore(Path.Combine(o.DataDir, "specs")), o.Guard, now: () => Clock.Now), Certified, o.Model);
        Engine = new RunEngine(Db, Accounts, Profiles, Tracker, new WorkspaceFactory(Accounts, build), Ashlar, o.Delay);
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
    }
}

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet.Host;

/// <summary>
/// Fleet.Host serves Swagger exactly where Ashlar.API does: in <c>Development</c>, or anywhere
/// <c>Ashlar:Api:EnableSwagger</c> opts in, and an explicit <c>false</c> wins over the environment.
/// </summary>
/// <remarks>
/// <para>This host's pipeline was copied from <c>application/src/Ashlar.API/Program.cs</c> without the
/// API's Swagger gate, so it called <c>UseSwagger</c> and <c>UseSwaggerUI</c> unconditionally and a
/// Production container answered the OpenAPI document (as JSON and as YAML) and the Swagger UI to anyone
/// who could reach its port. No auth setting changed that: both built-in auth middlewares return early
/// for paths outside <c>/api</c>. Nothing noticed, because both other test classes here run the host in
/// Development, where Swagger is meant to be on.</para>
///
/// <para>Every case first asserts that the same host answers <c>/health</c> with 200, so a 404 can only
/// mean "not mapped", never "the host did not start". The served cases assert the document is this
/// host's (its title and a commercial fleet route) in both formats, so a 200 from some other handler
/// cannot pass, and so the YAML and redirect assertions in the not-served cases name real routes. The
/// client never follows redirects: the bare <c>/swagger</c> answers 301 when served, and a client that
/// followed it would report the UI page's status instead.</para>
///
/// <para>Two content roots. <see cref="WebApplicationFactory{TEntryPoint}"/> uses the Fleet.Host
/// SOURCE directory, which holds only this host's own <c>appsettings.json</c>. The shipped image
/// (<c>.docker/Dockerfile.fleet-host</c>) runs from its publish directory, which also carries
/// Ashlar.API's <c>appsettings.{Development,Testing}.json</c>, copied in through the ProjectReference.
/// Fleet.Host's own build output (<c>bin/&lt;configuration&gt;/&lt;tfm&gt;</c> beside its csproj)
/// carries the same set: its own base file plus the inherited ones. So the build-output cases start the
/// host with that directory as content root, and a change in <c>application/</c> that turned Swagger on
/// for an environment reaches them, where the source-directory cases cannot see it. Not this test
/// assembly's own directory: that one also receives Ashlar.API's base <c>appsettings.json</c>, which
/// overwrites Fleet.Host's there, so it is not what the image runs; the cases check the base file is
/// identical to the host's source copy before they trust the directory.
/// One inherited file does turn it on today: Ashlar.API's <c>appsettings.Testing.json</c> sets
/// <c>EnableSwagger</c> to true, so <c>ASPNETCORE_ENVIRONMENT=Testing</c> serves Swagger on this image
/// (and on the API image). No deployment sets Testing; the inventory case pins that exception so that
/// removing it, or adding another, is a reviewed change.</para>
///
/// <para>Enabling Swagger outside Development logs an <c>Ashlar.Security</c> notice, as Ashlar.API does;
/// the cases capture the host's log and pin that it appears exactly when it should.</para>
///
/// <para>This project is outside the solutions; native readiness builds and runs it on net10.0 through
/// <c>ci verify</c>'s validation sweep (<c>docs/CommercialCiCoverage.md</c>).</para>
/// </remarks>
[Trait("Category", "CommercialFleetHost")]
public sealed class FleetHostSwaggerExposureTests : IClassFixture<WebApplicationFactory<FleetHostProgram>>
{
    private const string TestApiKey = "fleet-host-swagger-test-key";
    private const string DocumentPath = "/swagger/v1/swagger.json";
    private const string YamlPath = "/swagger/v1/swagger.yaml";
    private const string UiPath = "/swagger/index.html";
    private const string BarePath = "/swagger";
    private const string SecurityCategory = "Ashlar.Security";
    private const string SecurityNotice = "Swagger UI is enabled outside Development";

    /// <summary>
    /// The document as JSON and as YAML (Swashbuckle serves both from one route), the UI page, and the
    /// bare prefix that redirects to it.
    /// </summary>
    private static readonly string[] SwaggerPaths = [DocumentPath, YamlPath, UiPath, BarePath];

    private readonly WebApplicationFactory<FleetHostProgram> _factory;

    /// <summary>Fleet host Swagger exposure tests.</summary>
    /// <param name="factory">Factory.</param>
    public FleetHostSwaggerExposureTests(WebApplicationFactory<FleetHostProgram> factory) =>
        _factory = factory;

    /// <summary>
    /// Fleet.Host's own build output, with every file the build copied beside the host, including the
    /// ones inherited from Ashlar.API. It is the project's <c>bin/&lt;configuration&gt;/&lt;tfm&gt;</c>, built
    /// with the configuration and framework this test assembly was built with (the ProjectReference
    /// builds it). <see cref="AssertIsTheHostsBuildOutput"/> refuses any other directory.
    /// </summary>
    private static (string SourceRoot, string BuildOutput) HostDirectories()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = testOutput.Name;
        var configuration = testOutput.Parent?.Name
            ?? throw new InvalidOperationException($"{testOutput.FullName} has no configuration directory above it.");
        for (var directory = testOutput; directory is not null; directory = directory.Parent)
        {
            var sourceRoot = Path.Combine(directory.FullName, "commercial", "src", "Ashlar.Commercial.Fleet.Host");
            if (File.Exists(Path.Combine(sourceRoot, "Ashlar.Commercial.Fleet.Host.csproj")))
            {
                return (sourceRoot, Path.Combine(sourceRoot, "bin", configuration, framework));
            }
        }

        throw new InvalidOperationException($"No commercial/src/Ashlar.Commercial.Fleet.Host above {testOutput.FullName}.");
    }

    /// <summary>
    /// The directory must hold the host and the host's OWN base configuration, verbatim, as the
    /// published image does; otherwise it stands in for nothing.
    /// </summary>
    /// <param name="sourceRoot">Fleet.Host's project directory.</param>
    /// <param name="buildOutput">The directory to vouch for.</param>
    private static void AssertIsTheHostsBuildOutput(string sourceRoot, string buildOutput)
    {
        File.Exists(Path.Combine(buildOutput, "Fleet.Host.dll")).Should().BeTrue($"{buildOutput} must be Fleet.Host's build output");
        var shipped = Path.Combine(buildOutput, "appsettings.json");
        File.Exists(shipped).Should().BeTrue($"{buildOutput} must carry the configuration the host ships with");
        File.ReadAllText(shipped).Should().Be(
            File.ReadAllText(Path.Combine(sourceRoot, "appsettings.json")),
            $"{shipped} must be Fleet.Host's own appsettings.json, as in the published image, not one another project copied over it");
    }

    /// <summary>
    /// The security claim itself: a Production host with the shipped configuration (no
    /// <c>Ashlar:Api:EnableSwagger</c> override) maps neither the OpenAPI document nor the UI.
    /// </summary>
    [Fact]
    public async Task Production_host_with_shipped_configuration_serves_no_swagger()
    {
        await AssertSwaggerNotServedAsync("Production", enableSwagger: null, contentRoot: null);
    }

    /// <summary>
    /// "Off" is decided by Development, not by Production: Staging is off too. An explicit
    /// <c>false</c> stays off (a set value is read, not merely detected), and it also turns Swagger
    /// off in Development, because the configured value wins over the environment default.
    /// </summary>
    /// <param name="environment">Host environment name.</param>
    /// <param name="enableSwagger">Value for <c>Ashlar:Api:EnableSwagger</c>, or null to leave the shipped value.</param>
    [Theory]
    [InlineData("Staging", null)]
    [InlineData("Production", "false")]
    [InlineData("Development", "false")]
    public async Task Swagger_is_not_served_outside_development_or_when_explicitly_disabled(
        string environment,
        string? enableSwagger)
    {
        await AssertSwaggerNotServedAsync(environment, enableSwagger, contentRoot: null);
    }

    /// <summary>
    /// The image's own view: the host started from the build output, with every inherited
    /// <c>appsettings.*.json</c> beside it, serves no Swagger in Production (the image's default, since
    /// <c>.docker/Dockerfile.fleet-host</c> sets no <c>ASPNETCORE_ENVIRONMENT</c>) or in Staging.
    /// </summary>
    /// <param name="environment">Host environment name.</param>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Host_started_from_its_build_output_serves_no_swagger(string environment)
    {
        var (sourceRoot, root) = HostDirectories();
        AssertIsTheHostsBuildOutput(sourceRoot, root);

        await AssertSwaggerNotServedAsync(environment, enableSwagger: null, contentRoot: root);
    }

    /// <summary>
    /// Inventory of the environment files the build output carries: the only one whose effective
    /// <c>Ashlar:Api:EnableSwagger</c> is true is <c>Testing</c>, inherited from Ashlar.API. A new one
    /// (say an <c>appsettings.Production.json</c> added under <c>application/src/Ashlar.API</c>) would
    /// reopen Swagger on this image without touching this project; the removal of Testing's is the fix
    /// the remarks describe, and should tighten this list to empty.
    /// </summary>
    [Fact]
    public void Build_output_turns_swagger_on_only_for_the_inherited_testing_environment()
    {
        var (sourceRoot, root) = HostDirectories();
        AssertIsTheHostsBuildOutput(sourceRoot, root);
        var baseFile = Path.Combine(root, "appsettings.json");

        var environmentsThatEnableSwagger = Directory.GetFiles(root, "appsettings.*.json")
            .Select(static file => Path.GetFileName(file))
            .Where(static name => !string.Equals(name, "appsettings.json", StringComparison.OrdinalIgnoreCase))
            .Where(name => new ConfigurationBuilder()
                .AddJsonFile(baseFile)
                .AddJsonFile(Path.Combine(root, name))
                .Build()
                .GetValue<bool?>("Ashlar:Api:EnableSwagger") == true)
            .Select(static name => name["appsettings.".Length..^".json".Length])
            .Order(StringComparer.Ordinal)
            .ToArray();

        environmentsThatEnableSwagger.Should().Equal(
            new[] { "Testing" },
            $"only Ashlar.API's appsettings.Testing.json is known to set Ashlar:Api:EnableSwagger=true among the files in {root}; "
            + "any other environment listed here serves Swagger anonymously from the fleet-host image "
            + "(a stale file from an earlier local build counts too: clean the output if its source is gone)");
    }

    /// <summary>
    /// Development serves Swagger by default, and an operator can opt a Production host in with
    /// <c>Ashlar__Api__EnableSwagger=true</c> - the same knob, with the same meaning, as Ashlar.API.
    /// Opting in outside Development logs the <c>Ashlar.Security</c> notice; Development does not.
    /// </summary>
    /// <param name="environment">Host environment name.</param>
    /// <param name="enableSwagger">Value for <c>Ashlar:Api:EnableSwagger</c>, or null to leave the shipped value.</param>
    /// <param name="expectedNotices">How many <c>Ashlar.Security</c> Swagger notices the host must log.</param>
    [Theory]
    [InlineData("Development", null, 0)]
    [InlineData("Production", "true", 1)]
    public async Task Swagger_is_served_in_development_or_when_explicitly_enabled(
        string environment,
        string? enableSwagger,
        int expectedNotices)
    {
        var log = new CapturingLoggerProvider();
        using var factory = CreateFactory(environment, enableSwagger, contentRoot: null, log);
        using var client = CreateClient(factory);
        await AssertHostIsUpAsync(client, environment);
        var setting = $"{environment} with Ashlar:Api:EnableSwagger={enableSwagger ?? "(shipped value)"}";

        using var document = await client.GetAsync(DocumentPath);
        document.StatusCode.Should().Be(HttpStatusCode.OK, $"{setting} must serve the OpenAPI document");
        var json = await document.Content.ReadAsStringAsync();
        json.Should().Contain("\"openapi\"", "the body must be an OpenAPI document");
        json.Should().Contain("Ashlar Commercial Fleet Host", "the document must be this host's, not another handler's 200");
        json.Should().Contain("/api/mesh/fleet/nodes", "the document describes the commercial fleet routes this host maps");

        using var yaml = await client.GetAsync(YamlPath);
        yaml.StatusCode.Should().Be(HttpStatusCode.OK, $"{setting} serves the same document as YAML");
        var yamlBody = await yaml.Content.ReadAsStringAsync();
        yamlBody.Should().Contain("openapi:", "the YAML body must be an OpenAPI document");
        yamlBody.Should().Contain("Ashlar Commercial Fleet Host", "the YAML must be this host's document");
        yamlBody.Should().Contain("/api/mesh/fleet/nodes", "the YAML describes the same routes as the JSON");

        using var ui = await client.GetAsync(UiPath);
        ui.StatusCode.Should().Be(HttpStatusCode.OK, "the Swagger UI is served alongside the document");
        // Not ContentType?.MediaType.Should(): a null-conditional would skip the assertion entirely.
        ui.Content.Headers.ContentType.Should().NotBeNull();
        ui.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        using var bare = await client.GetAsync(BarePath);
        bare.StatusCode.Should().Be(HttpStatusCode.MovedPermanently, "the bare /swagger redirects to the UI page when served");
        bare.Headers.Location.Should().NotBeNull();
        bare.Headers.Location!.OriginalString.Should().EndWith("swagger/index.html");

        CountSecurityNotices(log).Should().Be(
            expectedNotices,
            $"{setting} must log the {SecurityCategory} notice exactly when Swagger is on outside Development");
    }

    private async Task AssertSwaggerNotServedAsync(string environment, string? enableSwagger, string? contentRoot)
    {
        var log = new CapturingLoggerProvider();
        using var factory = CreateFactory(environment, enableSwagger, contentRoot, log);
        using var client = CreateClient(factory);
        await AssertHostIsUpAsync(client, environment);
        if (contentRoot is not null)
        {
            // UseContentRoot must have reached the host, or this case is the source-directory case again.
            factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Should().Be(contentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var setting = $"{environment} with Ashlar:Api:EnableSwagger={enableSwagger ?? "(shipped value)"}"
            + (contentRoot is null ? string.Empty : $" and content root {contentRoot}");
        // Every path is probed before anything is asserted, so a failure names each one that answered.
        var answered = new List<string>();
        foreach (var path in SwaggerPaths)
        {
            using var response = await client.GetAsync(path);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                answered.Add($"{path} answered {(int)response.StatusCode}");
            }
        }

        // Joined, not answered.Should().BeEmpty(): FluentAssertions reports only the first item of a
        // non-empty collection, which would hide every path after the first that answered.
        string.Join("; ", answered).Should().BeEmpty(
            $"no Swagger path may be mapped in {setting}: "
            + "/swagger is outside /api, so no auth middleware stands in front of it and whatever serves it serves it anonymously");

        CountSecurityNotices(log).Should().Be(0, $"{setting} serves no Swagger, so there is nothing to warn about");
    }

    private static async Task AssertHostIsUpAsync(HttpClient client, string environment)
    {
        using var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"the {environment} host must be up before its /swagger answer means anything");
    }

    private static HttpClient CreateClient(WebApplicationFactory<FleetHostProgram> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static int CountSecurityNotices(CapturingLoggerProvider log) =>
        log.Entries.Count(static e =>
            e.Category == SecurityCategory
            && e.Level == LogLevel.Information
            && e.Message.Contains(SecurityNotice, StringComparison.Ordinal));

    private WebApplicationFactory<FleetHostProgram> CreateFactory(
        string environment,
        string? enableSwagger,
        string? contentRoot,
        CapturingLoggerProvider log) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (contentRoot is not null)
            {
                builder.UseContentRoot(contentRoot);
            }

            builder.UseSetting("Ashlar:Security:ApiKey", TestApiKey);
            if (enableSwagger is not null)
            {
                builder.UseSetting("Ashlar:Api:EnableSwagger", enableSwagger);
            }

            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(log));
        });

    /// <summary>Records every log entry the host's logger factory lets through its configured filters.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public IReadOnlyCollection<(string Category, LogLevel Level, string Message)> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}

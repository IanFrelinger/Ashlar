using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ashlar.Abstractions.Security.Egress;
using Dapper;
using JobSearch.Accounts;
using JobSearch.Bricks;
using JobSearch.Host.Studio;
using JobSearch.Runs;
using JobSearch.Store;
using JobSearch.Studio;
using JobSearch.Workspace;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#pragma warning disable ASHLAREXP002 // EgressHttp and EgressFamilies are the experimental egress surface; the sign-in token exchange goes through the guard by design.

namespace JobSearch.Host.Web;

/// <summary>How the studio's web host runs.</summary>
public sealed record StudioServerOptions
{
    /// <summary>Kestrel URLs, e.g. http://0.0.0.0:8080 behind Caddy, or http://127.0.0.1:0 in tests.</summary>
    public string Urls { get; init; } = "http://0.0.0.0:8080";
    /// <summary>The address users reach the studio at (OAuth redirect URIs are built from it), e.g. https://studio.example.com.</summary>
    public string PublicUrl { get; init; } = "http://localhost:8080";
    /// <summary>Run the forge worker, the dispatcher and the retention sweep in the background.</summary>
    public bool Workers { get; init; } = true;
    /// <summary>Run the per-user schedule (the dispatcher).</summary>
    public bool Dispatcher { get; init; } = true;
    /// <summary>Retention limits.</summary>
    public RetentionRules Retention { get; init; } = new();
    /// <summary>Where the UI files are; null for the copy next to the app.</summary>
    public string? UiDir { get; init; }
}

/// <summary>
/// <c>jobsearch serve</c>: the studio's web host. Same-origin UI and JSON API, cookie session, Google sign-in (or, in
/// synthetic mode only, a sign-in that picks a made-up persona), consent before anything else, strict security headers.
/// </summary>
public static class StudioServer
{
    /// <summary>Header every state-changing API call must carry (cross-site requests cannot set it without CORS, which is off).</summary>
    public const string CsrfHeader = "X-Studio-Request";
    private const string OAuthCookie = "studio_oauth";

    /// <summary>Builds the web application over a wired <see cref="App"/>.</summary>
    public static WebApplication Build(App app, StudioServerOptions options)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls(options.Urls);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        // Request bodies and query strings are never logged: they can carry personal data.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton(app);
        builder.Services.AddSingleton(options);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(app.DataDir, "keys", "dataprotection")))
            .SetApplicationName("jobsearch-studio");
        var https = options.PublicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = https ? "__Host-studio" : "studio_session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = https ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        if (options.Workers)
            builder.Services.AddHostedService<StudioWorkers>();

        var web = builder.Build();
        web.Use(SecurityHeaders(https));
        web.Use(Errors);
        web.Use(Csrf);
        web.UseAuthentication();
        web.UseAuthorization();

        var ui = options.UiDir ?? Path.Combine(AppContext.BaseDirectory, "studio-ui");
        if (Directory.Exists(ui))
        {
            var files = new PhysicalFileProvider(ui);
            web.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            web.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                // The font files carry their version in their name, so a browser may keep them for a year.
                OnPrepareResponse = ctx =>
                {
                    if (ctx.Context.Request.Path.StartsWithSegments("/fonts", StringComparison.Ordinal))
                        ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                },
            });
        }

        MapAuth(web, app, options);
        MapApi(web, app, options);
        return web;
    }

    // ── middleware ─────────────────────────────────────────────────────────────────────────────────────

    private static Func<HttpContext, RequestDelegate, Task> SecurityHeaders(bool https) => async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        // Self-contained UI: no CDN, no fonts or scripts fetched from anyone else, no analytics; nothing on the page talks to a
        // third party (STUDIO.md I12). The one font it uses is served from this origin.
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
                                       "font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        if (https)
            h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal) || ctx.Request.Path.StartsWithSegments("/auth", StringComparison.Ordinal))
            h["Cache-Control"] = "no-store";
        await next(ctx).ConfigureAwait(false);
    };

    private static async Task Errors(HttpContext ctx, RequestDelegate next)
    {
        try
        {
            await next(ctx).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && ex is AccessDeniedException or ArgumentException or KeyNotFoundException
                                       or InvalidOperationException or JsonException or ApplicationService.EvidenceRequiredException
                                       or ForgeService.ForgeRefusal or FormatException)
        {
            ctx.Response.StatusCode = ex switch
            {
                AccessDeniedException => 403,
                KeyNotFoundException => 404,
                _ => 400,
            };
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private static async Task Csrf(HttpContext ctx, RequestDelegate next)
    {
        var path = ctx.Request.Path;
        var mutating = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method);
        if (mutating && (path.StartsWithSegments("/api", StringComparison.Ordinal) || path.StartsWithSegments("/auth", StringComparison.Ordinal))
            && ctx.Request.Headers[CsrfHeader] != "1")
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = $"missing {CsrfHeader} header" }).ConfigureAwait(false);
            return;
        }
        await next(ctx).ConfigureAwait(false);
    }

    private static string UserId(HttpContext ctx) =>
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new AccessDeniedException("not signed in");

    private static Task SignInAsync(HttpContext ctx, User user) =>
        ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.DisplayName),
        ], CookieAuthenticationDefaults.AuthenticationScheme)));

    // ── sign-in ────────────────────────────────────────────────────────────────────────────────────────

    private static void MapAuth(WebApplication web, App app, StudioServerOptions options)
    {
        var clientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
        var redirect = options.PublicUrl.TrimEnd('/') + "/auth/google/callback";
        var protector = web.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("studio.oauth");

        web.MapGet("/auth/google/start", (HttpContext ctx) =>
        {
            if (string.IsNullOrEmpty(clientId))
                return Results.Problem("Google sign-in is not configured (GOOGLE_CLIENT_ID)", statusCode: 503);
            var state = Token();
            var nonce = Token();
            var verifier = Token() + Token();
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            ctx.Response.Cookies.Append(OAuthCookie, protector.Protect($"{state}\n{nonce}\n{verifier}"), new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps || redirect.StartsWith("https", StringComparison.Ordinal),
                MaxAge = TimeSpan.FromMinutes(10), Path = "/auth/google",
            });
            // One consent: sign-in, and access to the files the app creates in the user's Drive (drive.file).
            var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code" +
                      $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                      $"&scope={Uri.EscapeDataString("openid email profile https://www.googleapis.com/auth/drive.file")}" +
                      $"&state={state}&nonce={nonce}&code_challenge={challenge}&code_challenge_method=S256&access_type=offline&prompt=consent";
            return Results.Redirect(url);
        });

        web.MapGet("/auth/google/callback", async (HttpContext ctx, string? code, string? state, string? error) =>
        {
            if (error is not null || code is null || state is null || string.IsNullOrEmpty(clientId))
                return Results.Redirect("/?signin=failed");
            if (!ctx.Request.Cookies.TryGetValue(OAuthCookie, out var sealedCookie))
                return Results.Redirect("/?signin=expired");
            ctx.Response.Cookies.Delete(OAuthCookie, new CookieOptions { Path = "/auth/google" });
            var parts = protector.Unprotect(sealedCookie).Split('\n');
            if (parts.Length != 3 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[0]), Encoding.ASCII.GetBytes(state)))
                return Results.Redirect("/?signin=failed");
            using var http = EgressHttp.CreateClient(EgressFamilies.Http, GoogleRestWorkspace.EgressSite);
            using var request = GoogleClientAuth.TokenRequest(clientId, clientSecret, new Dictionary<string, string>
            {
                ["code"] = code, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = parts[2],
            });
            using var response = await http.SendAsync(request, ctx.RequestAborted).ConfigureAwait(false);
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(ctx.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || body?["id_token"] is null || body["access_token"] is null)
                return Results.Redirect("/?signin=failed");
            // The ID token came straight from Google's token endpoint over TLS, so its signature need not be checked
            // (OpenID Connect Core 3.1.3.7); its audience, issuer, expiry and nonce still are.
            var claims = JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlDecode(body["id_token"]!.GetValue<string>().Split('.')[1])))!.AsObject();
            var iss = claims["iss"]?.GetValue<string>();
            if (claims["aud"]?.GetValue<string>() != clientId || iss is not ("https://accounts.google.com" or "accounts.google.com")
                || claims["nonce"]?.GetValue<string>() != parts[1] || claims["email_verified"]?.GetValue<bool>() != true
                || DateTimeOffset.FromUnixTimeSeconds(claims["exp"]!.GetValue<long>()) < DateTimeOffset.UtcNow)
                return Results.Redirect("/?signin=failed");
            var email = claims["email"]!.GetValue<string>();
            var user = app.Accounts.FindUser(email) ?? app.Accounts.GetUser(app.Accounts.CreateUser(email, claims["name"]?.GetValue<string>() ?? email));
            var tokenRef = $"google:{user.Id}";
            app.Secrets.Put(tokenRef, new Dictionary<string, string>
            {
                ["access_token"] = body["access_token"]!.GetValue<string>(),
                ["refresh_token"] = body["refresh_token"]?.GetValue<string>() ?? app.Secrets.Get(tokenRef)?.GetValueOrDefault("refresh_token") ?? "",
            });
            app.Accounts.Connect(user.Id, user.Id, "google", email, tokenRef);
            await SignInAsync(ctx, user).ConfigureAwait(false);
            return Results.Redirect("/");
        });

        // Synthetic mode only: sign in as a made-up persona. It does not exist on a real deployment.
        web.MapPost("/auth/dev", async (HttpContext ctx, DevSignIn body) =>
        {
            if (!app.Synthetic)
                return Results.NotFound();
            var user = app.Accounts.FindUser(body.Email) ?? throw new KeyNotFoundException("no such persona");
            await SignInAsync(ctx, user).ConfigureAwait(false);
            return Results.Ok(new { user.Id, user.DisplayName });
        });

        web.MapPost("/auth/signout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return Results.Ok();
        });
    }

    // ── API ───────────────────────────────────────────────────────────────────────────────────────────

    private static void MapApi(WebApplication web, App app, StudioServerOptions options)
    {
        web.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        // The private model on this server: the deploy script and monitoring read it; it says only ready or not.
        web.MapGet("/health/model", async (CancellationToken ct) => await app.PrivateModel.ReadyAsync(ct).ConfigureAwait(false)
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        web.MapGet("/api/meta", () => Results.Ok(new
        {
            product = "Job Search Studio",
            synthetic = app.Synthetic,
            version = typeof(StudioServer).Assembly.GetName().Version?.ToString() ?? "0",
            build = Environment.GetEnvironmentVariable("JOBSEARCH_BUILD") ?? "dev",
            privateModel = app.Forge.PrivateModel,
            stepWriter = app.Forge.Writer,
            googleSignIn = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")),
            personas = app.Synthetic ? app.Accounts.ListUsers().Select(u => new { u.Email, u.DisplayName }).ToList() : null,
            noticeVersion = Compliance.NoticeVersion,
            // False while a new server is still certifying its starter steps.
            libraryReady = app.SeedsReady.Task.IsCompletedSuccessfully || !options.Workers,
        }));

        var me = web.MapGroup("/api/me").RequireAuthorization();
        me.MapGet("", (HttpContext ctx) =>
        {
            var userId = UserId(ctx);
            var user = app.Accounts.GetUser(userId);
            var (used, limit) = app.Forge.Allowance(userId);
            return Results.Ok(new
            {
                user.Id, user.Email, user.DisplayName, user.Timezone,
                onboarded = app.Compliance.Onboarded(userId),
                consents = Compliance.ConsentKinds.ToDictionary(k => k, k => app.Compliance.Has(userId, k)),
                google = app.Accounts.GetConnection(userId, "google") is { } conn ? new { conn.AccountEmail, conn.Status } : null,
                hasResume = app.Profiles.Load(userId) is not null,
                newSteps = new { used, limit },
            });
        });
        me.MapPost("/consent", (HttpContext ctx, ConsentBody body) =>
        {
            var userId = UserId(ctx);
            foreach (var kind in body.Kinds)
                app.Compliance.Grant(userId, kind);
            if (body.Timezone is { Length: > 0 } tz)
                app.Accounts.SetTimezone(userId, userId, tz);
            return Results.Ok();
        });
        me.MapPost("/consent/withdraw", (HttpContext ctx, ConsentBody body) =>
        {
            var userId = UserId(ctx);
            foreach (var kind in body.Kinds)
            {
                app.Compliance.Withdraw(userId, kind);
                if (kind == "model-wording")
                {
                    var prefs = app.Accounts.GetPreferences(userId);
                    app.Accounts.SetPreferences(userId, userId, prefs with { Materials = prefs.Materials with { ModelCoverLetter = false } });
                }
            }
            return Results.Ok();
        });
        me.MapGet("/export", (HttpContext ctx) =>
        {
            var userId = UserId(ctx);
            var export = app.Compliance.Export(userId);
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"jobsearch-studio-export-{DateTime.UtcNow:yyyyMMdd}.json\"";
            return Results.Text(export.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "application/json");
        });
        me.MapPost("/delete", async (HttpContext ctx, DeleteBody body) =>
        {
            if (body.Confirm != "DELETE")
                throw new ArgumentException("type DELETE to confirm");
            var userId = UserId(ctx);
            var rows = app.Compliance.Erase(userId);
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return Results.Ok(new { erasedRows = rows, note = "Your account is gone. Files the studio created in your Google Drive are yours and were not touched." });
        });

        // Everything below needs the user's consent to the notice, the terms and the age confirmation.
        var api = web.MapGroup("/api").RequireAuthorization().AddEndpointFilter(async (ctx, next) =>
        {
            var userId = UserId(ctx.HttpContext);
            if (!app.Compliance.Onboarded(userId))
                return Results.Json(new { error = "consent-required" }, statusCode: 403);
            return await next(ctx).ConfigureAwait(false);
        });

        api.MapGet("/profile", (HttpContext ctx) =>
        {
            var userId = UserId(ctx);
            var p = app.Profiles.Load(userId);
            return Results.Ok(new
            {
                hasResume = p is not null,
                fields = p?.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new
                {
                    name = kv.Key, value = kv.Value.Value is { } v ? Display(v) : null,
                    kv.Value.Source, kv.Value.Status, kv.Value.Evidence,
                }).ToList(),
            });
        });
        api.MapPost("/profile/resume", (HttpContext ctx, ResumeBody body) =>
        {
            if (body.Text.Length is 0 or > 100_000)
                throw new ArgumentException("paste or upload the résumé as plain text (at most 100,000 characters)");
            var (sha, _) = app.Profiles.StoreResume(UserId(ctx), UserId(ctx), body.Filename ?? "resume.txt", body.Text);
            return Results.Ok(new { sha });
        });
        api.MapPost("/profile/field", (HttpContext ctx, FieldBody body) =>
        {
            app.Profiles.SetField(UserId(ctx), UserId(ctx), body.Field, body.Value);
            return Results.Ok();
        });
        api.MapPost("/profile/review", (HttpContext ctx, ReviewBody body) =>
        {
            app.Profiles.Review(UserId(ctx), UserId(ctx), body.Field, body.Confirm);
            return Results.Ok();
        });

        api.MapGet("/preferences", (HttpContext ctx) => Results.Ok(app.Accounts.GetPreferences(UserId(ctx))));
        api.MapPut("/preferences", (HttpContext ctx, Preferences prefs) =>
        {
            var userId = UserId(ctx);
            if (prefs.Materials.ModelCoverLetter && !app.Compliance.Has(userId, "model-wording"))
                throw new ArgumentException("model-worded cover letters need your consent first");
            if (prefs.Boards.Count > 30 || prefs.JobLinks.Count > 50 || prefs.TargetRoles.Count > 20)
                throw new ArgumentException("too many boards, links or roles");
            app.Accounts.SetPreferences(userId, userId, prefs);
            return Results.Ok();
        });

        api.MapGet("/workflow", (HttpContext ctx) => Results.Ok(WorkflowView(app, UserId(ctx))));
        api.MapPut("/workflow", (HttpContext ctx, WorkflowDocument doc) =>
        {
            var userId = UserId(ctx);
            var before = app.Workflows.Get(userId).Steps.Select(s => s.StepId).ToHashSet(StringComparer.Ordinal);
            foreach (var s in doc.Steps)
                if (app.Library.Get(s.StepId) is not { } e || !StepLibrary.VisibleTo(e, userId))
                    throw new ArgumentException($"step {s.StepId} is not in your library");
            app.Workflows.Save(userId, userId, doc);
            foreach (var added in doc.Steps.Select(s => s.StepId).Where(id => !before.Contains(id)).Distinct(StringComparer.Ordinal))
                app.Library.CountUse(added);
            return Results.Ok(WorkflowView(app, userId));
        });
        api.MapGet("/workflow/graph", (HttpContext ctx) =>
        {
            var userId = UserId(ctx);
            var steps = new StudioWorkflowSource(app.Workflows, app.Library).StepsFor(userId);
            var def = PostingWorkflow.Build("preview", "Employer", new CandidateContext { UserId = userId, Today = "2026-01-01" }, steps);
            return Results.Ok(new
            {
                nodes = def.Nodes.Select(n => new
                {
                    n.Id, n.Name,
                    type = n is Ashlar.Core.Domain.Workflows.BrickNode ? "brick" : "input",
                    brickId = (n as Ashlar.Core.Domain.Workflows.BrickNode)?.BrickId,
                }),
                edges = def.Connections.Select(c => new { from = c.FromNodeId, fromPort = c.FromPortId[4..], to = c.ToNodeId, toPort = c.ToPortId[3..] }),
            });
        });

        api.MapGet("/library", (HttpContext ctx, string? q) =>
        {
            var userId = UserId(ctx);
            var entries = string.IsNullOrWhiteSpace(q) ? app.Library.Visible(userId) : app.Library.Search(q, userId, 30);
            return Results.Ok(entries.Select(e => LibraryView(app, e, userId, examples: false)));
        });
        api.MapGet("/library/{id}", (HttpContext ctx, string id) =>
        {
            var userId = UserId(ctx);
            var e = app.Library.Get(id);
            return e is null || !StepLibrary.VisibleTo(e, userId) ? Results.NotFound() : Results.Ok(LibraryView(app, e, userId, examples: true));
        });

        api.MapPost("/forge", (HttpContext ctx, ForgeBody body) =>
        {
            var userId = UserId(ctx);
            if (app.Forge.Recent(userId, 10).Count(j => j.Status is "queued" or "designing" or "writing" or "certifying") >= 3)
                throw new ArgumentException("three requests are already in progress; wait for one to finish");
            return Results.Ok(new { id = app.Forge.Submit(userId, userId, body.Request, body.Private) });
        });
        api.MapGet("/forge", (HttpContext ctx) =>
        {
            var userId = UserId(ctx);
            var (used, limit) = app.Forge.Allowance(userId);
            return Results.Ok(new { jobs = app.Forge.Recent(userId), used, limit, privateModel = app.Forge.PrivateModel, writer = app.Forge.Writer });
        });
        api.MapGet("/forge/{id}", (HttpContext ctx, string id) => app.Forge.Get(UserId(ctx), id) is { } job ? Results.Ok(job) : Results.NotFound());

        api.MapPost("/runs", (HttpContext ctx) =>
        {
            var userId = UserId(ctx);
            var key = "web:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            _ = Task.Run(async () =>
            {
                try { await app.Engine.RunAsync(userId, userId, key).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or AccessDeniedException or IOException) { /* recorded on the run row */ }
            });
            return Results.Accepted(value: new { key });
        });
        api.MapGet("/runs", (HttpContext ctx) =>
        {
            var userId = UserId(ctx);
            return Results.Ok(app.Db.Read(c => c.Query<(string Id, string Status, string StartedAt, string? FinishedAt, string? SummaryJson, string? Error)>(
                "SELECT id, status, started_at, finished_at, summary_json, error FROM runs WHERE user_id=@userId ORDER BY started_at DESC LIMIT 20", new { userId }))
                .Select(r => new { r.Id, r.Status, r.StartedAt, r.FinishedAt, r.Error, summary = r.SummaryJson is null ? null : JsonNode.Parse(r.SummaryJson) }));
        });

        api.MapGet("/tracker", (HttpContext ctx) => Results.Ok(TrackerView(app, UserId(ctx))));
        api.MapPost("/jobs/{id}/status", (HttpContext ctx, string id, StatusBody body) =>
        {
            app.Applications.SetStatus(UserId(ctx), UserId(ctx), id, body.Status, body.Evidence, "web");
            return Results.Ok();
        });
        api.MapPost("/jobs/{id}/notes", (HttpContext ctx, string id, NotesBody body) =>
        {
            app.Applications.SetNotes(UserId(ctx), UserId(ctx), id, body.Notes, body.NextSteps, "web");
            return Results.Ok();
        });
    }

    /// <summary>A profile value as a person reads it: lists joined, jobs as "title — employer (start–end)".</summary>
    private static string Display(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Array => string.Join("; ", v.EnumerateArray().Select(Display)),
        JsonValueKind.Object when v.TryGetProperty("title", out var t) =>
            $"{Display(t)}{(v.TryGetProperty("employer", out var e) ? " — " + Display(e) : "")}" +
            (v.TryGetProperty("start", out var st) ? $" ({Display(st)}–{(v.TryGetProperty("end", out var en) ? Display(en) : "")})" : ""),
        JsonValueKind.Object => string.Join(", ", v.EnumerateObject().Select(o => $"{o.Name}: {Display(o.Value)}")),
        JsonValueKind.Null => "",
        _ => v.GetRawText(),
    };

    private static object WorkflowView(App app, string userId)
    {
        var doc = app.Workflows.Get(userId);
        return new
        {
            doc.Version,
            steps = doc.Steps.Select(s => new
            {
                s.Key, s.StepId, s.Enabled, s.Settings, s.Label,
                step = app.Library.Get(s.StepId) is { } e ? LibraryView(app, e, userId, examples: false) : null,
            }),
        };
    }

    private static object LibraryView(App app, LibraryEntry e, string userId, bool examples) => new
    {
        e.Id, e.Kind, e.Name, e.Description, e.Settings, e.Behavior, e.Status, e.StatusDetail, e.Uses, e.CreatedAt, e.Mutants,
        visibility = e.Visibility, mine = e.OwnerUserId == userId, writtenBy = e.Origin,
        contentHash = e.ContentHash,
        examples = examples ? Examples(app, e) : null,
    };

    private static List<JsonObject> Examples(App app, LibraryEntry e)
    {
        var path = Path.Combine(app.Library.Folder(e.Id), CertifyStepCommand.WitnessFile);
        if (!File.Exists(path))
            return [];
        var cases = JsonNode.Parse(File.ReadAllText(path))?["cases"]?.AsArray() ?? [];
        return cases.OfType<JsonObject>().Take(12).Select(c => new JsonObject
        {
            ["input"] = c["input"]?.DeepClone(),
            ["output"] = c["expectedOutput"]?.DeepClone(),
        }).ToList();
    }

    private static object TrackerView(App app, string userId) =>
        app.Db.Read(c => c.Query<(string Id, string Employer, string Title, string? Location, string? WorkArrangement, string? Url, string ListingStatus,
                string? Recommendation, string? ExcludedBy, string? AssessmentJson, string? Status, string? AppliedDate, string? NextSteps, string? UserNotes)>("""
            SELECT j.id, j.employer, j.title, j.location, j.work_arrangement, j.url, j.listing_status, s.recommendation, s.excluded_by, s.assessment_json,
                   a.status, a.applied_date, a.next_steps, a.user_notes
            FROM jobs j LEFT JOIN assessments s ON s.job_id=j.id LEFT JOIN applications a ON a.job_id=j.id
            WHERE j.user_id=@userId AND j.archived_at IS NULL ORDER BY j.first_seen_at DESC
            """, new { userId }))
            .Select(r =>
            {
                var a = JobSearch.Store.Json.Read<Screening.Assessment>(r.AssessmentJson);
                return new
                {
                    r.Id, r.Employer, r.Title, r.Location, arrangement = r.WorkArrangement, r.Url, listing = r.ListingStatus,
                    fit = r.Recommendation, reason = a?.RecommendationReason, excludedBy = r.ExcludedBy,
                    steps = a?.Steps ?? [], gaps = a?.Gaps.Select(g => g.Requirement).ToList() ?? [], unknowns = a?.Unknowns.Select(u => u.Requirement).ToList() ?? [],
                    status = r.Status ?? "not_applied", r.AppliedDate, r.NextSteps, notes = r.UserNotes,
                };
            }).ToList();

    private static string Token() => Base64Url(RandomNumberGenerator.GetBytes(24));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + ((4 - (t.Length % 4)) % 4), '='));
    }

    /// <summary>Dev sign-in body.</summary>
    public sealed record DevSignIn(string Email);
    /// <summary>Consent body.</summary>
    public sealed record ConsentBody(List<string> Kinds, string? Timezone);
    /// <summary>Delete body.</summary>
    public sealed record DeleteBody(string Confirm);
    /// <summary>Résumé body.</summary>
    public sealed record ResumeBody(string? Filename, string Text);
    /// <summary>Field body.</summary>
    public sealed record FieldBody(string Field, string? Value);
    /// <summary>Review body.</summary>
    public sealed record ReviewBody(string Field, bool Confirm);
    /// <summary>Forge body.</summary>
    public sealed record ForgeBody(string Request, bool Private);
    /// <summary>Status body.</summary>
    public sealed record StatusBody(string Status, string? Evidence);
    /// <summary>Notes body.</summary>
    public sealed record NotesBody(string? Notes, string? NextSteps);
}

/// <summary>
/// The studio's background work: certify the starter steps once, run the forge queue, run every user's daily search
/// on their own schedule, and sweep data past its retention.
/// </summary>
public sealed class StudioWorkers(App app, StudioServerOptions options, ILogger<StudioWorkers> log) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var added = await app.Library.EnsureSeedsAsync(app.Certifier, app.SeedsDir, Path.Combine(app.DataDir, "forge"), stoppingToken).ConfigureAwait(false);
            log.LogInformation("starter steps certified and published: {Added}", added);
            app.SeedsReady.TrySetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("starter steps could not be certified: {Error}", ex.Message);
            app.SeedsReady.TrySetException(ex);
        }
        var forge = app.Forge.WorkAsync(stoppingToken);
        var schedule = options.Dispatcher ? ScheduleAsync(stoppingToken) : Task.CompletedTask;
        await Task.WhenAll(forge, schedule).ConfigureAwait(false);
    }

    private async Task ScheduleAsync(CancellationToken ct)
    {
        var lastSweep = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await app.Dispatcher.RunDueAsync(Clock.Now, ct: ct).ConfigureAwait(false);
                if (Clock.Now - lastSweep > TimeSpan.FromHours(24))
                {
                    var removed = app.Compliance.Sweep(options.Retention);
                    log.LogInformation("retention sweep: {Removed}", string.Join(", ", removed.Select(kv => $"{kv.Key} {kv.Value}")));
                    lastSweep = Clock.Now;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("scheduled work failed: {Error}", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}

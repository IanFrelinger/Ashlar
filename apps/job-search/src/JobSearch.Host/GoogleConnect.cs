using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ashlar.Abstractions.Security.Egress;

namespace JobSearch.Host;

/// <summary>
/// The user's own Google consent (OAuth 2.0, installed-app flow, scope drive.file). Step 1 prints the consent URL; the
/// user approves in their browser and pastes the code back; step 2 exchanges it and stores the token in the secret
/// store, never in the database. NOT YET RUN against Google from this repository: it needs a Google Cloud OAuth client
/// (README, "Remaining steps").
/// </summary>
public static class GoogleConnect
{
    private const string Scope = "https://www.googleapis.com/auth/drive.file";
    private const string Redirect = "http://127.0.0.1:8765/oauth2callback";

    /// <summary>Runs one step of the flow.</summary>
    public static async Task<int> RunAsync(App app, string userId, string? code)
    {
        var clientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            Console.Error.WriteLine("GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET must be set (a Google Cloud OAuth client for a desktop app).");
            return 2;
        }
        if (code is null)
        {
            Console.WriteLine("Open this URL as the user, approve, and run again with --code <the code from the redirect>:");
            Console.WriteLine($"https://accounts.google.com/o/oauth2/v2/auth?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(Redirect)}" +
                              $"&response_type=code&scope={Uri.EscapeDataString(Scope)}&access_type=offline&prompt=consent&state={Uri.EscapeDataString(userId)}");
            return 0;
        }
        using var http = EgressHttp.CreateClient(EgressFamilies.Http, Workspace.GoogleRestWorkspace.EgressSite);
        using var resp = await http.PostAsync(new Uri("https://oauth2.googleapis.com/token"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = clientId, ["client_secret"] = clientSecret, ["redirect_uri"] = Redirect, ["grant_type"] = "authorization_code",
        })).ConfigureAwait(false);
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode || body?["access_token"] is null)
        {
            Console.Error.WriteLine($"token exchange failed: HTTP {(int)resp.StatusCode} {body?["error"]}");
            return 2;
        }
        var tokenRef = $"google:{userId}";
        app.Secrets.Put(tokenRef, new Dictionary<string, string>
        {
            ["access_token"] = body["access_token"]!.GetValue<string>(),
            ["refresh_token"] = body["refresh_token"]?.GetValue<string>() ?? "",
        });
        var account = await new Workspace.GoogleRestWorkspace(app.Secrets, tokenRef, clientId, clientSecret).AccountEmailAsync().ConfigureAwait(false);
        app.Accounts.Connect(userId, userId, "google", account, tokenRef);
        Console.WriteLine($"connected Google Drive for {account} (scope drive.file: the app sees only files it creates)");
        return 0;
    }
}

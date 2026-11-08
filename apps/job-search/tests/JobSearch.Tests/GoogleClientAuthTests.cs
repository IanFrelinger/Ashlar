using System.Text;
using FluentAssertions;
using JobSearch.Workspace;
using Xunit;

namespace JobSearch.Tests;

public sealed class GoogleClientAuthTests
{
    [Fact]
    public async Task With_the_secret_it_goes_in_the_form_body_and_there_is_no_authorization_header()
    {
        using var r = GoogleClientAuth.TokenRequest("cid.apps.googleusercontent.com", "s3cret", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "rt" });
        r.Headers.Authorization.Should().BeNull();
        var body = await r.Content!.ReadAsStringAsync();
        body.Should().Contain("client_secret=s3cret").And.Contain("client_id=cid.apps.googleusercontent.com").And.Contain("refresh_token=rt");
    }

    [Fact]
    public async Task Without_it_the_request_carries_basic_client_auth_for_the_environment_to_fill_and_no_secret_in_the_body()
    {
        using var r = GoogleClientAuth.TokenRequest("cid.apps.googleusercontent.com", null, new() { ["grant_type"] = "authorization_code", ["code"] = "c" });
        r.RequestUri!.Host.Should().Be("oauth2.googleapis.com");
        r.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(r.Headers.Authorization.Parameter!)).Should().StartWith("cid.apps.googleusercontent.com:");
        (await r.Content!.ReadAsStringAsync()).Should().NotContain("client_secret");
    }
}

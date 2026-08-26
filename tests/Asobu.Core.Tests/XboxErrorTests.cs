using System.Net;
using System.Net.Http.Headers;
using Asobu.Core.Accounts;

namespace Asobu.Core.Tests;

/// <summary>
/// What a refused sign-in says.
///
/// A tester saw "Xbox sign-in failed (403)" and nothing else, which told them nothing about their
/// own account and told us nothing either. Probing the live endpoint showed why: SISU answers with
/// an empty body and puts its reason in an X-Err header, so reading only the body threw away the
/// one thing Xbox said. These pin the shapes that were actually observed.
/// </summary>
public class XboxErrorTests
{
    private static HttpResponseMessage Refusal(
        HttpStatusCode status, string body = "", string? xErr = null, string? challenge = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };

        if (xErr is not null) response.Headers.Add("X-Err", xErr);
        if (challenge is not null)
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("XBL3.0", challenge));

        return response;
    }

    private static string Explain(HttpResponseMessage response, string body = "") =>
        XboxChain.Explain("Xbox sign-in", response, body);

    // ---- the shapes the live endpoints actually send ----

    /// <summary>Exactly what sisu.xboxlive.com returns for a token it will not take.</summary>
    [Fact]
    public void A_SISU_refusal_is_read_from_its_header()
    {
        using var response = Refusal(HttpStatusCode.Unauthorized, xErr: "4294967295");

        var said = Explain(response);

        Assert.Contains("Sign out", said);
        Assert.DoesNotContain("4294967295", said);
    }

    /// <summary>And XSTS, which puts the same family of codes in a body instead.</summary>
    [Fact]
    public void An_XSTS_refusal_is_read_from_its_body()
    {
        const string body = """{"Identity":"0","XErr":2148916238,"Message":"","Redirect":"https://start.ui.xboxlive.com/"}""";
        using var response = Refusal(HttpStatusCode.Unauthorized, body);

        Assert.Contains("child account", Explain(response, body));
    }

    /// <summary>The third spelling: a challenge header, where the code has an equals sign.</summary>
    [Fact]
    public void A_challenge_header_is_read_too()
    {
        using var response = Refusal(
            HttpStatusCode.Unauthorized, challenge: """realm="xboxlive", XErr=2148916233""");

        Assert.Contains("no Xbox profile", Explain(response));
    }

    [Fact]
    public void A_code_nobody_has_seen_before_is_still_quoted()
    {
        using var response = Refusal(HttpStatusCode.Forbidden, xErr: "2148916999");

        var said = Explain(response);

        Assert.Contains("2148916999", said);
        Assert.Contains("403", said);
    }

    // ---- and the refusals that carry nothing at all ----

    /// <summary>
    /// The one the tester hit. Nothing in the body, nothing in the headers: everything Asobu can
    /// say has to come from the fact that Xbox said nothing.
    /// </summary>
    [Fact]
    public void A_silent_403_blames_the_network_rather_than_the_account()
    {
        using var response = Refusal(HttpStatusCode.Forbidden);

        var said = Explain(response);

        Assert.Contains("VPN", said);
        Assert.DoesNotContain("account", said);
    }

    /// <summary>
    /// Every Xbox request is signed with a timestamp, so a machine whose clock has drifted is
    /// refused for a reason it can be told about — and a laptop that has been asleep is exactly
    /// where that happens.
    /// </summary>
    [Fact]
    public void A_wrong_clock_is_named()
    {
        using var response = Refusal(HttpStatusCode.Forbidden);
        response.Headers.Date = DateTimeOffset.UtcNow.AddMinutes(-40);

        var said = Explain(response);

        Assert.Contains("clock", said);
        Assert.Contains("40 minutes", said);
    }

    /// <summary>A clock that is merely a little out is not the story, so it is not told as one.</summary>
    [Fact]
    public void A_clock_that_is_nearly_right_is_left_alone()
    {
        using var response = Refusal(HttpStatusCode.Forbidden);
        response.Headers.Date = DateTimeOffset.UtcNow.AddSeconds(-30);

        Assert.DoesNotContain("clock", Explain(response));
    }

    [Fact]
    public void An_ordinary_failure_keeps_whatever_it_did_say()
    {
        const string body = "Service unavailable, try later.";
        using var response = Refusal(HttpStatusCode.ServiceUnavailable, body);

        var said = Explain(response, body);

        Assert.Contains("503", said);
        Assert.Contains("try later", said);
    }
}

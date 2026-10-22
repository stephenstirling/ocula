using System.Net.Http.Headers;
using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>The User-Agent Ocula sends to Wikimedia, Wikipedia, DBpedia and MusicBrainz: it must say what it is and how to reach its maintainer.</summary>
public class ApiIdentityTests
{
    [Fact]
    public void The_user_agent_names_the_app_and_a_contact_web_address()
    {
        Assert.Equal("Ocula/0.1 (+https://github.com/stephenstirling)", ApiIdentity.UserAgent);
    }

    [Fact]
    public void The_user_agent_is_a_valid_header_value_with_a_product_and_a_comment()
    {
        var header = new HttpRequestMessage().Headers.UserAgent;

        header.ParseAdd(ApiIdentity.UserAgent);

        Assert.Equal("Ocula", header.First().Product!.Name);
        Assert.Contains(header, h => h.Comment == "(+https://github.com/stephenstirling)");
    }

    [Fact]
    public void No_email_address_is_sent()
    {
        Assert.DoesNotContain("@", ApiIdentity.UserAgent);
        Assert.DoesNotContain("example.com", ApiIdentity.UserAgent);
    }

    [Fact]
    public void The_person_lookup_client_sends_it()
    {
        using var client = PersonLookup.CreateClient();

        Assert.Equal(ApiIdentity.UserAgent, client.DefaultRequestHeaders.UserAgent.ToString());
    }
}

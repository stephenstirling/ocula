using System;
using System.Net.Http;

namespace Ocula.Services;

/// <summary>The one HTTP client for Wikidata, Wikipedia, Wikimedia Commons and DBpedia.</summary>
public static class Wikimedia
{
    public static HttpClient Http { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Wikimedia asks every app to identify itself with a way to reach its developer.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Ocula/1.0 (https://github.com/{AppInfo.GitHubRepository})");
        return client;
    }
}

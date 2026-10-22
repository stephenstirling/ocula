namespace Ocula.Services;

/// <summary>
/// How Ocula introduces itself to the public data services it asks (Wikidata, Wikimedia Commons, Wikipedia, DBpedia, MusicBrainz). Wikimedia's
/// and MusicBrainz's policies ask every app to send a User-Agent that says what it is and how to reach whoever runs it, and block
/// anonymous clients. The contact is a web address, not an email address, so no personal mailbox is sent to anyone.
///
/// TODO before release: point <see cref="Contact"/> at a page that stays up and says who to write to (this is the maintainer's public GitHub profile).
/// </summary>
public static class ApiIdentity
{
    public const string Contact = "https://github.com/stephenstirling";

    public const string Product = "Ocula/0.1";

    /// <summary>For example <c>Ocula/0.1 (+https://github.com/stephenstirling)</c>.</summary>
    public static string UserAgent => $"{Product} (+{Contact})";
}

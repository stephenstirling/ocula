using Ocula.Services;
using Xunit;

namespace Ocula.Tests;

/// <summary>ocula:person?id=...: the link Porta uses to open a person's photos in Ocula.</summary>
public class PersonLinkTests
{
    [Fact]
    public void A_person_link_carries_the_id()
    {
        var link = OculaLink.Parse("ocula:person?id=b65e86ce6eb973c3");

        Assert.NotNull(link);
        Assert.Equal("person", link!.Verb);
        Assert.Equal("b65e86ce6eb973c3", link.Id);
        Assert.Null(link.File);
        Assert.False(link.InBackground);   // it should come to the front: the user clicked it in Porta
    }

    [Theory]
    [InlineData("ocula:person?id=B65E86CE6EB973C3", "b65e86ce6eb973c3")]    // case doesn't matter
    [InlineData("OCULA:Person?ID=b65e86ce6eb973c3", "b65e86ce6eb973c3")]
    [InlineData("ocula:person?id=b65e86ce6eb973c3&future=1", "b65e86ce6eb973c3")]
    [InlineData("ocula:person?id=%62%36%35e86ce6eb973c3", "b65e86ce6eb973c3")]   // percent-encoded
    public void The_link_is_read_leniently(string text, string expected)
    {
        Assert.Equal(expected, OculaLink.Parse(text)!.Id);
    }

    [Theory]
    [InlineData("ocula:person")]
    [InlineData("ocula:person?id=")]
    [InlineData("ocula:person?id=short")]                                       // too short
    [InlineData("ocula:person?id=not-hex-not-hex-")]
    [InlineData("ocula:person?id=b65e86ce6eb973c3'%20OR%201=1--")]              // never a query
    [InlineData("ocula:person?id=b65e86ce6eb973c3%00")]
    [InlineData("ocula:person?id=0123456789abcdef0123456789abcdef0")]           // too long
    public void A_missing_or_malformed_id_is_dropped_but_the_link_still_parses(string text)
    {
        var link = OculaLink.Parse(text);

        Assert.NotNull(link);
        Assert.Equal("person", link!.Verb);
        Assert.Null(link.Id);
    }

    [Theory]
    [InlineData("ocula:person?id=b65e86ce6eb973c3")]
    [InlineData("\"ocula:person?id=b65e86ce6eb973c3\"")]     // as the registry command passes it: "%1" in quotes
    public void A_link_given_as_a_command_line_argument_is_found(string argument)
    {
        var link = OculaLink.FromArguments(new[] { @"C:\Some\File.jpg", argument });

        Assert.Equal("person", link!.Verb);
        Assert.Equal("b65e86ce6eb973c3", link.Id);
    }

    [Fact]
    public void Ordinary_arguments_are_not_links()
    {
        Assert.Null(OculaLink.FromArguments(new[] { @"D:\Photos", "--flag" }));
        Assert.Null(OculaLink.FromArguments(Array.Empty<string>()));
    }

    [Fact]
    public void Capta_links_are_unchanged()
    {
        var link = OculaLink.Parse("ocula:captures?folder=C%3A%5CCaptures");

        Assert.Equal("captures", link!.Verb);
        Assert.Null(link.Id);
        Assert.Equal(@"C:\Captures", link.Folder);
    }

    [Fact]
    public void The_id_in_the_export_file_is_what_the_link_accepts()
    {
        // PeopleExport only writes ids of the kind the link accepts, so every id in people-index.json can be used in a link.
        Assert.True(PeopleExport.IsId("b65e86ce6eb973c3"));
        Assert.NotNull(OculaLink.Parse("ocula:person?id=b65e86ce6eb973c3")!.Id);
        Assert.False(PeopleExport.IsId("B65E86CE6EB973C3"));   // the file always uses lower case
    }
}

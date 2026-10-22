using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ocula.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Ocula.Tests;

/// <summary>
/// "Share people with Porta": the people-index.json file. The format is a contract with another app, so these pin down the exact
/// fields, that nothing else from a card can reach the file, and that people with no name are never written.
/// </summary>
public class PeopleExportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 22, 30, 5, TimeSpan.FromHours(-4));
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ocula-export-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static byte[] FaceJpeg(Rgba32 colour, int size = 112)
    {
        using var image = new Image<Rgba32>(size, size, colour);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    private static string CardJson(Action<PersonFacts>? change = null)
    {
        var facts = new PersonFacts
        {
            WikidataId = "Q34424", Name = "Shakira", FullName = "Shakira Isabel Mebarak Ripoll",
            Description = "Colombian singer-songwriter (born 1977)",
            BirthDate = "1977-02-02", BirthYear = 1977, BirthPlace = "Barranquilla, Colombia", BirthPlaceChain = { "Barranquilla", "Colombia" },
            Occupations = { "singer", "actor" }, Languages = { "Spanish", "English" }, HeightCm = 157, Children = 2, Gender = "female",
            Spouses = { new Relation("Zed Spouse", "Q1", 2010, null) },
            FormerSpouses = { new Relation("Former Person", "Q2", 2000, 2005) },
            Partners = { new Relation("Partner Person", "Q3", 2011, 2022) },
            DeathDate = "2099-01-01",
            DeathPlace = "Death Place Name", DeathCause = "Death Cause Name",
            ChildrenDetails = { new ChildDetail("Child Name One", "Q9", "2011-05-06"), new ChildDetail("Child Name Two", "Q10", null) },
            RetrievedAt = Now,
        };
        change?.Invoke(facts);
        return facts.ToJson();
    }

    private JsonElement Written(IReadOnlyList<PeopleExportRow> rows)
    {
        PeopleExport.Write(_folder, rows, Now);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, PeopleExport.FileName))).RootElement.Clone();
    }

    // ---------- The format ----------

    [Fact]
    public void The_file_has_the_version_the_update_time_in_utc_and_the_people()
    {
        var root = Written(new[] { new PeopleExportRow("0123456789abcdef", "Shakira", CardJson(), 123, FaceJpeg(Color.Red)) });

        Assert.Equal(new[] { "version", "updated", "people" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("2026-10-22T02:30:05Z", root.GetProperty("updated").GetString());   // -04:00 converted to UTC
    }

    [Fact]
    public void A_person_has_exactly_the_agreed_fields()
    {
        var root = Written(new[] { new PeopleExportRow("0123456789abcdef", "Shakira", CardJson(), 123, FaceJpeg(Color.Red)) });

        var person = root.GetProperty("people")[0];

        Assert.Equal(new[] { "id", "name", "aliases", "description", "photoCount", "thumbnail" }, person.EnumerateObject().Select(p => p.Name));
        Assert.Equal("0123456789abcdef", person.GetProperty("id").GetString());
        Assert.Equal("Shakira", person.GetProperty("name").GetString());
        Assert.Equal(new[] { "Shakira Isabel Mebarak Ripoll" }, person.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("Colombian singer-songwriter", person.GetProperty("description").GetString());   // "(born 1977)" removed
        Assert.Equal(123, person.GetProperty("photoCount").GetInt32());
        Assert.StartsWith(Path.Combine(_folder, PeopleExport.ThumbnailFolder), person.GetProperty("thumbnail").GetString());
    }

    [Fact]
    public void A_person_with_no_card_and_no_picture_has_only_an_id_a_name_aliases_and_a_count()
    {
        var root = Written(new[] { new PeopleExportRow("0123456789abcdef", "Mum", null, 40, null) });

        var person = root.GetProperty("people")[0];

        Assert.Equal(new[] { "id", "name", "aliases", "photoCount" }, person.EnumerateObject().Select(p => p.Name));
        Assert.Empty(person.GetProperty("aliases").EnumerateArray());
    }

    [Fact]
    public void No_other_detail_from_a_card_can_reach_the_file()
    {
        PeopleExport.Write(_folder, new[] { new PeopleExportRow("0123456789abcdef", "Shakira", CardJson(), 123, FaceJpeg(Color.Red)) }, Now);
        var text = File.ReadAllText(Path.Combine(_folder, PeopleExport.FileName));

        foreach (var secret in new[]
                 {
                     "1977", "1977-02-02", "February", "Barranquilla", "Colombia", "singer", "actor", "Spanish", "English", "157", "1.57",
                     "Zed Spouse", "Former Person", "Partner Person", "female", "2099", "Q34424", "Mebarak" + "x",
                     "Child Name One", "Child Name Two", "2011-05-06", "Death Place Name", "Death Cause Name",
                     "height", "spouse", "children", "birth", "occupation", "language", "gender", "partner",
                 })
        {
            // "singer" appears once, inside the description "Colombian singer-songwriter"; "Colombia" inside "Colombian". Everything else must be absent.
            if (secret is "singer" or "Colombia") continue;
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_empty_library_gives_a_valid_file_with_no_people()
    {
        var root = Written(Array.Empty<PeopleExportRow>());

        Assert.Equal(0, root.GetProperty("people").GetArrayLength());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
    }

    // ---------- Who is included ----------

    [Fact]
    public void A_row_with_no_name_or_no_photos_or_a_bad_id_is_not_written()
    {
        var root = Written(new[]
        {
            new PeopleExportRow("0123456789abcdef", "   ", null, 10, null),
            new PeopleExportRow("0123456789abcde0", "No Photos", null, 0, null),
            new PeopleExportRow("not-a-hex-id!", "Bad Id", null, 5, null),
            new PeopleExportRow("0123456789abcde1", "Kept", null, 5, null),
        });

        Assert.Equal(new[] { "Kept" }, root.GetProperty("people").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    private static SqliteConnection Library()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE people (id INTEGER PRIMARY KEY, uid TEXT, name TEXT COLLATE NOCASE, hidden INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE faces (id INTEGER PRIMARY KEY, path TEXT NOT NULL, score REAL, thumb BLOB, person_id INTEGER);
            CREATE TABLE person_cards (person_id INTEGER PRIMARY KEY, json TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql, params (string, object)[] values)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Unnamed_and_hidden_groups_are_never_read_from_the_library()
    {
        using var db = Library();
        Exec(db, "INSERT INTO people (id, uid, name, hidden) VALUES (1, '00000000000000a1', 'Ann', 0), (2, '00000000000000a2', NULL, 0), " +
                 "(3, '00000000000000a3', '  ', 0), (4, '00000000000000a4', 'Hidden Hal', 1), (5, NULL, 'No Uid', 0)");
        foreach (var id in new[] { 1, 2, 3, 4, 5 }) Exec(db, $"INSERT INTO faces (path, score, person_id) VALUES ('p{id}.jpg', 1, {id})");

        var rows = PeopleExport.ReadRows(db);

        Assert.Equal(new[] { "Ann" }, rows.Select(r => r.Name));
    }

    [Fact]
    public void Photos_are_counted_once_each_and_the_best_face_is_the_picture()
    {
        using var db = Library();
        Exec(db, "INSERT INTO people (id, uid, name) VALUES (1, '00000000000000a1', 'Ann')");
        var poor = FaceJpeg(Color.Blue);
        var best = FaceJpeg(Color.Green);
        Exec(db, "INSERT INTO faces (path, score, thumb, person_id) VALUES ('a.jpg', 0.5, $t, 1), ('a.jpg', 0.2, $t, 1), ('b.jpg', 0.9, $b, 1), ('c.jpg', 0.1, $t, 1)",
             ("$t", poor), ("$b", best));
        Exec(db, "INSERT INTO person_cards (person_id, json) VALUES (1, $j)", ("$j", CardJson()));

        var row = Assert.Single(PeopleExport.ReadRows(db));

        Assert.Equal(3, row.Photos);                       // a.jpg twice is one photo
        Assert.Equal(best, row.Thumbnail);
        Assert.NotNull(row.CardJson);
    }

    [Fact]
    public void People_with_the_most_photos_come_first()
    {
        using var db = Library();
        Exec(db, "INSERT INTO people (id, uid, name) VALUES (1, '00000000000000a1', 'Few'), (2, '00000000000000a2', 'Many')");
        Exec(db, "INSERT INTO faces (path, score, person_id) VALUES ('x.jpg', 1, 1), ('a.jpg', 1, 2), ('b.jpg', 1, 2)");

        Assert.Equal(new[] { "Many", "Few" }, PeopleExport.ReadRows(db).Select(r => r.Name));
    }

    // ---------- Names, aliases and descriptions ----------

    [Fact]
    public void Aliases_are_the_other_names_wikidata_gives_and_never_the_name_itself()
    {
        var card = PersonFacts.FromJson(CardJson())!;

        Assert.Equal(new[] { "Shakira Isabel Mebarak Ripoll" }, PeopleExport.Aliases("Shakira", card));
        Assert.Equal(new[] { "Shakira Isabel Mebarak Ripoll", "Shakira" }, PeopleExport.Aliases("Shak", card));
        Assert.Equal(new[] { "Shakira" }, PeopleExport.Aliases("shakira isabel mebarak ripoll", card));
        Assert.Empty(PeopleExport.Aliases("Anyone", null));
    }

    [Theory]
    [InlineData("American actress and singer", "American actress and singer")]
    [InlineData("Colombian singer-songwriter (born 1977)", "Colombian singer-songwriter")]
    [InlineData("American singer (1989–)", "American singer")]
    [InlineData("singer born in 1977", null)]
    [InlineData("actress, wife of someone", null)]
    [InlineData("actress married to someone", null)]
    [InlineData("1.57 m tall singer", null)]
    [InlineData("American singer (born December 13, 1989) and mother of two", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_description_carrying_a_birth_year_or_personal_detail_is_cut_or_left_out(string? description, string? expected)
    {
        Assert.Equal(expected, PeopleExport.SafeDescription(description));
    }

    [Fact]
    public void A_description_that_is_too_long_is_left_out()
    {
        Assert.Null(PeopleExport.SafeDescription(new string('a', 150)));
    }

    [Fact]
    public void Text_is_cleaned_because_a_name_is_typed_by_the_user_and_a_description_comes_from_wikidata()
    {
        var card = CardJson(f => f.Description = "singer‮\0 and actress");
        var root = Written(new[] { new PeopleExportRow("0123456789abcdef", "Ann\0 ‮B", card, 3, null) });

        var person = root.GetProperty("people")[0];

        Assert.DoesNotContain('\0', person.GetProperty("name").GetString()!);
        Assert.DoesNotContain('‮', person.GetProperty("name").GetString()!);
        Assert.DoesNotContain('‮', person.GetProperty("description").GetString()!);
    }

    [Fact]
    public void Accents_are_kept_readable_in_the_file()
    {
        PeopleExport.Write(_folder, new[] { new PeopleExportRow("0123456789abcdef", "José Núñez", null, 3, null) }, Now);

        Assert.Contains("José Núñez", File.ReadAllText(Path.Combine(_folder, PeopleExport.FileName)));
    }

    // ---------- Pictures ----------

    [Fact]
    public void The_picture_is_a_small_square_png()
    {
        var root = Written(new[] { new PeopleExportRow("0123456789abcdef", "Ann", null, 3, FaceJpeg(Color.Red, 200)) });

        var path = root.GetProperty("people")[0].GetProperty("thumbnail").GetString()!;
        var info = Image.Identify(path);

        Assert.EndsWith(".png", path);
        Assert.Equal(PeopleExport.ThumbnailSize, info.Width);
        Assert.Equal(PeopleExport.ThumbnailSize, info.Height);
        Assert.Equal(Path.Combine(_folder, "people-thumbs", "0123456789abcdef.png"), path);   // people-thumbs\<id>.png, exactly
    }

    [Fact]
    public void An_unreadable_picture_leaves_the_person_without_one_rather_than_failing()
    {
        var root = Written(new[] { new PeopleExportRow("0123456789abcdef", "Ann", null, 3, new byte[] { 1, 2, 3, 4 }) });

        Assert.False(root.GetProperty("people")[0].TryGetProperty("thumbnail", out _));
    }

    [Fact]
    public void An_unchanged_picture_is_not_written_again_and_a_changed_one_replaces_the_old_under_the_same_name()
    {
        var red = new PeopleExportRow("0123456789abcdef", "Ann", null, 3, FaceJpeg(Color.Red));
        var first = Written(new[] { red }).GetProperty("people")[0].GetProperty("thumbnail").GetString()!;
        var stamp = File.GetLastWriteTimeUtc(first);
        File.SetLastWriteTimeUtc(first, stamp.AddHours(-5));
        var marked = File.GetLastWriteTimeUtc(first);

        var again = Written(new[] { red }).GetProperty("people")[0].GetProperty("thumbnail").GetString()!;
        Assert.Equal(first, again);
        Assert.Equal(marked, File.GetLastWriteTimeUtc(first));   // untouched

        var blue = red with { Thumbnail = FaceJpeg(Color.Blue) };
        var changed = Written(new[] { blue }).GetProperty("people")[0].GetProperty("thumbnail").GetString()!;
        Assert.Equal(first, changed);                            // the name is always <id>.png
        Assert.NotEqual(marked, File.GetLastWriteTimeUtc(changed));
        using var image = Image.Load<Rgba32>(changed);
        Assert.True(image[48, 48].B > 150 && image[48, 48].R < 100);   // now the blue face
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "people-thumbs")));   // no leftovers
    }

    [Fact]
    public void A_person_who_is_no_longer_listed_loses_their_picture()
    {
        var ann = new PeopleExportRow("0123456789abcdef", "Ann", null, 3, FaceJpeg(Color.Red));
        var bob = new PeopleExportRow("0123456789abcde0", "Bob", null, 3, FaceJpeg(Color.Green));
        Written(new[] { ann, bob });
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_folder, PeopleExport.ThumbnailFolder)).Length);

        Written(new[] { ann });

        Assert.Single(Directory.GetFiles(Path.Combine(_folder, PeopleExport.ThumbnailFolder)));
    }

    // ---------- Writing and deleting ----------

    [Fact]
    public void The_file_is_replaced_whole_and_no_temporary_file_is_left()
    {
        Written(new[] { new PeopleExportRow("0123456789abcdef", "Ann", null, 3, null) });
        Written(new[] { new PeopleExportRow("0123456789abcdef", "Ann", null, 9, null) });

        Assert.Equal(new[] { PeopleExport.FileName }, Directory.GetFiles(_folder).Select(Path.GetFileName));
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, PeopleExport.FileName))).RootElement;
        Assert.Equal(9, root.GetProperty("people")[0].GetProperty("photoCount").GetInt32());
    }

    [Fact]
    public void Deleting_removes_the_file_and_every_picture()
    {
        Written(new[] { new PeopleExportRow("0123456789abcdef", "Ann", null, 3, FaceJpeg(Color.Red)) });

        PeopleExport.Delete(_folder);

        Assert.False(File.Exists(Path.Combine(_folder, PeopleExport.FileName)));
        Assert.False(Directory.Exists(Path.Combine(_folder, PeopleExport.ThumbnailFolder)));
        Assert.False(Directory.Exists(_folder));           // nothing of ours is left, not even the empty folder
    }

    [Fact]
    public void Deleting_when_nothing_was_written_is_fine()
    {
        PeopleExport.Delete(Path.Combine(_folder, "never-written"));
    }

    [Fact]
    public void Other_files_in_the_folder_are_left_alone()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "someone-elses.txt"), "keep");
        Written(new[] { new PeopleExportRow("0123456789abcdef", "Ann", null, 3, null) });

        PeopleExport.Delete(_folder);

        Assert.True(File.Exists(Path.Combine(_folder, "someone-elses.txt")));
        Assert.True(Directory.Exists(_folder));            // not empty, so it stays
    }

    // ---------- Where a reader outside Ocula can find the pictures ----------

    [Fact]
    public void The_picture_paths_in_the_file_use_the_published_folder_when_it_differs_from_the_one_written_to()
    {
        var published = Path.Combine(Path.GetTempPath(), "ocula-published-" + Guid.NewGuid().ToString("N"));

        PeopleExport.Write(_folder, new[] { new PeopleExportRow("0123456789abcdef", "Ann", null, 3, FaceJpeg(Color.Red)) }, Now, published);

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, PeopleExport.FileName))).RootElement;
        Assert.Equal(Path.Combine(published, "people-thumbs", "0123456789abcdef.png"), root.GetProperty("people")[0].GetProperty("thumbnail").GetString());
        Assert.True(File.Exists(Path.Combine(_folder, "people-thumbs", "0123456789abcdef.png")));   // still written where Ocula writes
    }

    [Fact]
    public void With_no_published_folder_the_paths_are_the_written_folder()
    {
        var root = Written(new[] { new PeopleExportRow("0123456789abcdef", "Ann", null, 3, FaceJpeg(Color.Red)) });

        Assert.Equal(Path.Combine(_folder, "people-thumbs", "0123456789abcdef.png"), root.GetProperty("people")[0].GetProperty("thumbnail").GetString());
    }

    [Fact]
    public void A_write_that_was_redirected_is_published_from_where_it_really_landed()
    {
        var physical = Path.Combine(Path.GetTempPath(), "ocula-physical-" + Guid.NewGuid().ToString("N"));
        string? asked = null;

        var result = PeopleExport.PublishedFolder(_folder, physical, path => { asked = path; return true; });

        Assert.Equal(physical, result);
        Assert.Equal(Path.Combine(physical, ".location-marker"), asked);   // the marker is looked for in the physical folder
    }

    [Fact]
    public void A_write_that_was_not_redirected_is_published_from_the_folder_itself()
    {
        var physical = Path.Combine(Path.GetTempPath(), "ocula-physical-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(_folder, PeopleExport.PublishedFolder(_folder, physical, _ => false));
        Assert.Equal(_folder, PeopleExport.PublishedFolder(_folder, physical));   // really looks, finds nothing
    }

    [Fact]
    public void Without_a_physical_folder_or_when_it_is_the_same_folder_nothing_is_probed()
    {
        Assert.Equal(_folder, PeopleExport.PublishedFolder(_folder, null));
        Assert.Equal(_folder, PeopleExport.PublishedFolder(_folder, _folder.ToUpperInvariant()));
        Assert.False(Directory.Exists(_folder));   // not even created
    }

    [Fact]
    public void The_marker_is_always_removed_and_other_files_are_left_alone()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "keep.txt"), "x");

        PeopleExport.PublishedFolder(_folder, Path.Combine(_folder, "elsewhere"), _ => true);

        Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(_folder).Select(Path.GetFileName));
    }
}

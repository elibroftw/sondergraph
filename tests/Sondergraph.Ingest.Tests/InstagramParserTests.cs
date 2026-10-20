using System.Text.Json;
using Sondergraph.Ingest;
using Sondergraph.Ingest.Parsers;
using Sondergraph.Ingest.Tests.Fakes;

namespace Sondergraph.Ingest.Tests;

/// <summary>
/// Instagram parser tests, covering the three record shapes it must handle.
/// </summary>
/// <remarks>
/// Fixtures are synthetic: invented handles, because a real export contains other people's
/// personal data. The structures are copied from the observed export so the quirks are
/// preserved, which is the part that matters.
/// </remarks>
public sealed class InstagramParserTests
{
    private const string Dir = "connections/followers_and_following/";

    private static List<ObservedAccount> Parse(params (string File, string Json)[] files)
    {
        var source = new InMemoryRawSource();

        foreach (var (file, json) in files)
        {
            source.Add(Dir + file, json);
        }

        return new InstagramParser()
            .Parse(source, new ParseContext())
            .OfType<ObservedAccount>()
            .ToList();
    }

    [Fact]
    public void Parses_shape_3a_nested_under_a_key()
    {
        // following.json: an object, handle in title, and NO "value" field.
        const string json = """
            {"relationships_following":[
              {"title":"example_user","string_list_data":[
                {"href":"https://www.instagram.com/_u/example_user","timestamp":1792353389}]}]}
            """;

        var accounts = Parse(("following.json", json));

        Assert.Single(accounts);
        Assert.Equal("example_user", accounts[0].Handle);
    }

    [Fact]
    public void Mutual_only_keeps_accounts_followed_in_both_directions()
    {
        // following.json puts the handle in "title" and omits "value"; followers_1.json is
        // the reverse. Reading either shape wrongly makes every account look unmatched, so
        // both directions are exercised together here.
        const string following = """
            {"relationships_following":[
              {"title":"mutual","string_list_data":[{"href":"https://www.instagram.com/mutual"}]},
              {"title":"only_followed","string_list_data":[{"href":"https://www.instagram.com/only_followed"}]}]}
            """;

        const string followers = """
            [{"title":"","string_list_data":[{"href":"https://www.instagram.com/mutual","value":"mutual"}]},
             {"title":"","string_list_data":[{"href":"https://www.instagram.com/only_follower","value":"only_follower"}]}]
            """;

        var source = new InMemoryRawSource();
        source.Add(Dir + "following.json", following);
        source.Add(Dir + "followers_1.json", followers);

        var context = new ParseContext();
        context.Options["mutualFollowOnly"] = "true";

        var handles = new InstagramParser()
            .Parse(source, context)
            .OfType<ObservedAccount>()
            .Select(a => a.Handle)
            .ToList();

        Assert.Contains("mutual", handles);
        Assert.DoesNotContain("only_followed", handles);
        Assert.DoesNotContain("only_follower", handles);
    }

    [Fact]
    public void Mutual_filtering_is_off_unless_asked_for()
    {
        // A parser is a pure function of its input (ADR 0003), so discarding records by
        // default would make it misreport the archive. The policy lives in the CLI.
        const string following = """
            {"relationships_following":[
              {"title":"only_followed","string_list_data":[{"href":"https://www.instagram.com/only_followed"}]}]}
            """;

        var source = new InMemoryRawSource();
        source.Add(Dir + "following.json", following);

        var handles = new InstagramParser()
            .Parse(source, new ParseContext())
            .OfType<ObservedAccount>()
            .Select(a => a.Handle)
            .ToList();

        Assert.Contains("only_followed", handles);
    }

    [Fact]
    public void Parses_shape_3b_where_title_is_empty()
    {
        // followers_1.json: a bare array, title empty, handle in string_list_data[].value.
        // Reading title first would yield a blank handle for every follower.
        const string json = """
            [{"title":"","media_list_data":[],"string_list_data":[
              {"href":"https://www.instagram.com/example_user","value":"example_user","timestamp":1792353390}]}]
            """;

        var accounts = Parse(("followers_1.json", json));

        Assert.Single(accounts);
        Assert.Equal("example_user", accounts[0].Handle);
    }

    [Fact]
    public void Parses_shape_3c_label_values()
    {
        // close_friends.json: handle only inside label_values, with a display name.
        const string json = """
            [{"timestamp":1777118439,"media":[],"label_values":[
              {"label":"URL","value":""},
              {"label":"Name","value":"Example Name"},
              {"label":"Username","value":"example_user"}],"fbid":"1"}]
            """;

        var accounts = Parse(("close_friends.json", json));

        Assert.Single(accounts);
        Assert.Equal("example_user", accounts[0].Handle);
        Assert.Equal("Example Name", accounts[0].DisplayName);
    }

    [Fact]
    public void Skips_deleted_account_placeholders()
    {
        // Observed in the real export: 8 records named "__deleted__<hash>". They are
        // tombstones for erased accounts, not people, and importing them puts unreadable
        // entries into the contact list.
        const string json = """
            [{"title":"__deleted__abc123","string_list_data":[
              {"href":"https://www.instagram.com/_u/__deleted__abc123","timestamp":1792353389}]},
             {"title":"real_user","string_list_data":[
              {"href":"https://www.instagram.com/_u/real_user","timestamp":1792353389}]}]
            """;

        var accounts = Parse(("followers_1.json", json));

        Assert.Single(accounts);
        Assert.Equal("real_user", accounts[0].Handle);
    }

    [Fact]
    public void Excludes_blocked_profiles_and_removed_suggestions()
    {
        // 715 of 1976 real records (36%) are these two files. Ingesting them as contacts
        // would inflate the graph by a third with people who were blocked or dismissed.
        var accounts = Parse(
            ("followers_1.json", """[{"title":"real_user","string_list_data":[{"href":"https://www.instagram.com/real_user","timestamp":1792353389}]}]"""),
            ("blocked_profiles.json", """[{"title":"blocked_user","string_list_data":[{"href":"https://www.instagram.com/blocked_user","timestamp":1792353389}]}]"""),
            ("removed_suggestions.json", """[{"title":"suggested_user","string_list_data":[{"href":"https://www.instagram.com/suggested_user","timestamp":1792353389}]}]"""));

        Assert.Single(accounts);
        Assert.Equal("real_user", accounts[0].Handle);
    }

    [Fact]
    public void Extracts_handle_from_both_href_forms()
    {
        // Two URL shapes appear in real data: /_u/<handle> and /<handle>.
        Assert.Equal("example", InstagramParser.HandleFromHref("https://www.instagram.com/_u/example"));
        Assert.Equal("example", InstagramParser.HandleFromHref("https://www.instagram.com/example"));
        Assert.Equal("example", InstagramParser.HandleFromHref("https://www.instagram.com/example/"));
        Assert.Null(InstagramParser.HandleFromHref(""));
        Assert.Null(InstagramParser.HandleFromHref("https://www.instagram.com/_u/"));
    }

    [Fact]
    public void Marks_timestamps_as_unreliable()
    {
        // Instagram's timestamps are at or after export time and do not describe when a
        // relationship started. Recording that uncertainty is what stops the decay feature
        // treating them as facts.
        const string json = """
            [{"title":"example_user","string_list_data":[
              {"href":"https://www.instagram.com/example_user","timestamp":1792353389}]}]
            """;

        Assert.Equal("unreliable", Parse(("followers_1.json", json))[0].Extra["timestampSemantics"]);
    }
}

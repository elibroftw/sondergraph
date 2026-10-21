using Sondergraph.Ingest;
using Sondergraph.Ingest.Parsers;
using Sondergraph.Ingest.Tests.Fakes;

namespace Sondergraph.Ingest.Tests;

/// <summary>
/// Tests for the Twitter archive parser.
/// </summary>
/// <remarks>
/// The archive carries no names, so the behaviours that matter are: only mutual follows are
/// kept, and each record still resolves to a real profile despite having only an id.
/// </remarks>
public sealed class TwitterParserTests
{
    /// <summary>Builds the JS-assignment shape Twitter actually uses.</summary>
    private static string File(string key, params string[] ids) =>
        "window.YTD." + key + ".part0 = [\n"
        + string.Join(",\n", ids.Select(id =>
            $"  {{\"{key}\" : {{\"accountId\" : \"{id}\", \"userLink\" : \"https://twitter.com/intent/user?user_id={id}\"}}}}"))
        + "\n]";

    private static List<ObservedAccount> Parse(string followers, string following)
    {
        var source = new InMemoryRawSource();
        source.Add("data/follower.js", followers);
        source.Add("data/following.js", following);

        return new TwitterParser()
            .Parse(source, new ParseContext())
            .OfType<ObservedAccount>()
            .ToList();
    }

    [Fact]
    public void Only_mutual_follows_are_kept()
    {
        // 1 is mutual, 2 follows me only, 3 I follow only.
        var accounts = Parse(
            File("follower", "1", "2"),
            File("following", "1", "3"));

        Assert.Single(accounts);
        Assert.Equal("1", accounts[0].Handle);
    }

    [Fact]
    public void An_account_appearing_in_both_files_is_mutual()
    {
        var accounts = Parse(
            File("follower", "10", "11", "12"),
            File("following", "12", "13"));

        Assert.Equal("12", accounts[0].Handle);
    }

    [Fact]
    public void The_record_is_named_after_its_id_because_the_archive_has_no_names()
    {
        // Stated as a test so the limitation is visible rather than surprising.
        var account = Parse(File("follower", "42"), File("following", "42")).Single();

        Assert.Equal("42", account.DisplayName);
        Assert.Equal("true", account.Extra["nameUnavailable"]);
    }

    [Fact]
    public void Each_record_has_a_profile_url_that_identifies_the_person()
    {
        // The id is the only identifier available, so the URL is what makes a rename
        // practical: one click resolves it to a handle.
        var account = Parse(File("follower", "42"), File("following", "42")).Single();

        Assert.Equal("https://x.com/i/user/42", account.ProfileUrl);
    }

    [Fact]
    public void Without_both_files_nothing_is_imported()
    {
        // Importing one side would label one-directional follows as mutual, which is the one
        // thing this parser must not get wrong.
        var source = new InMemoryRawSource();
        source.Add("data/follower.js", File("follower", "1", "2"));

        var context = new ParseContext();
        var accounts = new TwitterParser().Parse(source, context).OfType<ObservedAccount>().ToList();

        Assert.Empty(accounts);
        Assert.NotEmpty(context.Problems);
    }

    [Fact]
    public void Malformed_input_is_reported_not_thrown()
    {
        var accounts = Parse("window.YTD.follower.part0 = not json", File("following", "1"));

        Assert.Empty(accounts);
    }

    [Fact]
    public void The_js_assignment_wrapper_is_tolerated()
    {
        // Real files are JavaScript, not JSON; a plain JSON parser fails on the prefix.
        var wrapped = "window.YTD.follower.part0 = [{\"follower\":{\"accountId\":\"7\"}}]";
        var accounts = Parse(wrapped, File("following", "7"));

        Assert.Single(accounts);
    }

    [Fact]
    public void A_real_archive_is_recognised_by_its_file_names()
    {
        var source = new InMemoryRawSource();
        source.Add("data/follower.js", File("follower", "1"));
        source.Add("data/following.js", File("following", "1"));

        Assert.True(new TwitterParser().CanParse(source));
    }

    [Fact]
    public void Unrelated_archives_are_not_recognised()
    {
        var source = new InMemoryRawSource();
        source.Add("Account/user.json", "{}");

        Assert.False(new TwitterParser().CanParse(source));
    }
}

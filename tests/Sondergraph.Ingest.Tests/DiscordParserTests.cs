using Sondergraph.Ingest;
using Sondergraph.Ingest.Parsers;
using Sondergraph.Ingest.Tests.Fakes;

namespace Sondergraph.Ingest.Tests;

/// <summary>
/// Tests for the Discord data package.
/// </summary>
/// <remarks>
/// The package is mostly account plumbing; the only file that names people is
/// Account/user.json. Its relationships array mixes friends with blocked accounts and
/// unaccepted requests, so the type field decides what becomes a contact.
/// </remarks>
public sealed class DiscordParserTests
{
    private static List<ObservedAccount> Parse(string json)
    {
        var source = new InMemoryRawSource();
        source.Add("Account/user.json", json);

        return new DiscordParser()
            .Parse(source, new ParseContext())
            .OfType<ObservedAccount>()
            .ToList();
    }

    private const string Friends = """
        {"relationships":[
          {"id":"1","type":"FRIEND","nickname":"George Lewis","user":{
            "id":"1","username":"_soup_can_2","global_name":"soupcan"}},
          {"id":"2","type":"FRIEND","nickname":null,"user":{
            "id":"2","username":"beastmode","global_name":"BEAST MODE"}}]}
        """;

    [Fact]
    public void Parses_friends()
    {
        var accounts = Parse(Friends);

        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, a => Assert.Equal("discord", a.Platform));
    }

    [Fact]
    public void A_nickname_becomes_the_display_name()
    {
        // The nickname is what the user calls this person, which is far more useful than a
        // Discord handle: "George Lewis" rather than "_soup_can_2".
        var account = Parse(Friends).Single(a => a.Handle == "_soup_can_2");

        Assert.Equal("George Lewis", account.DisplayName);
    }

    [Fact]
    public void Without_a_nickname_the_global_name_is_used()
    {
        var account = Parse(Friends).Single(a => a.Handle == "beastmode");

        Assert.Equal("BEAST MODE", account.DisplayName);
    }

    [Fact]
    public void The_username_and_global_name_are_kept_as_aliases()
    {
        // Searching for either should find the person.
        var account = Parse(Friends).Single(a => a.Handle == "beastmode");

        Assert.Contains("BEAST MODE", account.Aliases);
    }

    [Fact]
    public void Blocked_accounts_are_not_contacts()
    {
        // A blocked account is emphatically not someone you know. Importing it would be
        // worse than merely useless, and the type field is the only thing that says so.
        const string json = """
            {"relationships":[
              {"id":"3","type":"BLOCKED","nickname":null,"user":{"id":"3","username":"spammer"}},
              {"id":"4","type":"FRIEND","nickname":null,"user":{"id":"4","username":"real_friend"}}]}
            """;

        var accounts = Parse(json);

        Assert.Single(accounts);
        Assert.Equal("real_friend", accounts[0].Handle);
    }

    [Theory]
    [InlineData("PENDING_INCOMING")]
    [InlineData("PENDING_OUTGOING")]
    [InlineData("NONE")]
    public void Unaccepted_relationships_are_not_contacts(string type)
    {
        // Neither side has accepted, so there is nothing to record as a connection.
        var json = "{\"relationships\":[{\"id\":\"5\",\"type\":\"" + type
            + "\",\"nickname\":null,\"user\":{\"id\":\"5\",\"username\":\"maybe\"}}]}";

        Assert.Empty(Parse(json));
    }

    [Fact]
    public void The_relationship_type_is_recorded()
    {
        var account = Parse(Friends)[0];

        Assert.Equal("FRIEND", account.Extra["relationship"]);
    }

    [Fact]
    public void No_profile_url_is_invented()
    {
        // Discord has no public profile URL for a user id; a guessed one would 404.
        Assert.All(Parse(Friends), a => Assert.Null(a.ProfileUrl));
    }

    [Fact]
    public void The_native_id_is_scoped_to_discord()
    {
        var account = Parse(Friends)[0];

        Assert.StartsWith("discord:", account.NativeId);
    }

    [Fact]
    public void A_package_without_a_user_file_is_not_recognised()
    {
        var source = new InMemoryRawSource();
        source.Add("Account/billing/payments.json", "[]");

        Assert.False(new DiscordParser().CanParse(source));
    }
}

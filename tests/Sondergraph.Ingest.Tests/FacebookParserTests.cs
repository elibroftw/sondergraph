using Sondergraph.Core;
using Sondergraph.Ingest;
using Sondergraph.Ingest.Parsers;
using Sondergraph.Ingest.Tests.Fakes;

namespace Sondergraph.Ingest.Tests;

/// <summary>
/// Facebook parser tests, covering the distinction between a social profile and a phone-book
/// contact.
/// </summary>
/// <remarks>
/// Fixtures are synthetic: invented names and phone numbers in the reserved
/// <c>+1 555 01xx</c> range. The structures mirror the real export, which is the part that
/// matters - <c>label_values</c> arrays for imported contacts, and <c>{name, timestamp}</c>
/// objects under a varying wrapper key for friend lists.
/// </remarks>
public sealed class FacebookParserTests
{
    private static (List<ObservedAccount> Accounts, ParseContext Context) Parse(
        params (string File, string Json)[] files)
    {
        var source = new InMemoryRawSource();

        foreach (var (file, json) in files)
        {
            source = source.Add(file, json);
        }

        var context = new ParseContext();
        var accounts = new FacebookParser()
            .Parse(source, context)
            .OfType<ObservedAccount>()
            .ToList();

        return (accounts, context);
    }

    private const string FriendList = """
        {
          "friends_v2": [
            { "name": "Sample Person", "timestamp": 1400000000 },
            { "name": "Second Person", "timestamp": 1400000100 }
          ]
        }
        """;

    private const string ImportedContacts = """
        [
          { "timestamp": 1727408809,
            "label_values": [
              { "label": "Contact name", "value": "Phone Person" },
              { "label": "Contact point", "value": "+1 555 0101" } ] },
          { "timestamp": 1727408809,
            "label_values": [
              { "label": "Contact name", "value": "Nobody With Number" } ] }
        ]
        """;

    [Fact]
    public void Imported_contact_with_a_phone_is_reported_as_a_contact_not_a_facebook_profile()
    {
        // The rule: a phone number and no profile is a contact record. Reporting it as a
        // Facebook account invented a profile and stamped a "Facebook" badge on people whose
        // only Facebook trace was a phone number the user had typed in.
        var (accounts, _) = Parse((
            "personal_information/other_personal_information/your_imported_contacts.json",
            ImportedContacts));

        var phonePerson = accounts.Single(a => a.DisplayName == "Phone Person");

        Assert.Equal("vcard", phonePerson.Platform);
        Assert.Equal(["+1 555 0101"], phonePerson.Phones);
        Assert.False(phonePerson.HasProfile);
        Assert.False(phonePerson.AddsSource);
    }

    [Fact]
    public void A_name_in_a_friend_list_does_not_claim_a_facebook_profile()
    {
        // A friend list gives a name and a date, never a handle or a URL. Showing "Facebook"
        // on those people claimed a profile existed when all we had was a string.
        var (accounts, _) = Parse(("friends_and_followers/friends/your_friends.json", FriendList));

        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, account =>
        {
            Assert.Equal("facebook", account.Platform);
            Assert.False(account.HasProfile);
            Assert.False(account.AddsSource);
        });
    }

    [Fact]
    public void Friend_list_names_still_carry_a_handle_so_they_can_enrich_an_existing_contact()
    {
        // Without a handle these records have no strong identifier at all, and every one of
        // the names would be dropped by the materialiser. The name is the enrichment.
        var (accounts, _) = Parse(("friends_and_followers/friends/your_friends.json", FriendList));

        Assert.All(accounts, account =>
        {
            Assert.False(string.IsNullOrWhiteSpace(account.Handle));
            Assert.Null(account.ProfileUrl);
        });
    }

    [Fact]
    public void Imported_contact_records_keep_the_phone_as_their_identity()
    {
        // Keyed on the phone like the vCard parser, so the same human merges across sources
        // instead of appearing twice.
        var (accounts, _) = Parse((
            "personal_information/other_personal_information/your_imported_contacts.json",
            ImportedContacts));

        var phonePerson = accounts.Single(a => a.DisplayName == "Phone Person");

        Assert.Equal($"tel:{Person.DigitsOnly("+1 555 0101")}", phonePerson.NativeId);
    }

    [Fact]
    public void An_imported_contact_with_no_phone_stays_a_facebook_record()
    {
        var (accounts, _) = Parse((
            "personal_information/other_personal_information/your_imported_contacts.json",
            ImportedContacts));

        var nameless = accounts.Single(a => a.DisplayName == "Nobody With Number");

        Assert.Equal("facebook", nameless.Platform);
        Assert.False(nameless.AddsSource);
    }
}

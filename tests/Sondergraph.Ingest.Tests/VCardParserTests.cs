using Sondergraph.Ingest;
using Sondergraph.Ingest.Parsers;
using Sondergraph.Ingest.Tests.Fakes;

namespace Sondergraph.Ingest.Tests;

/// <summary>
/// vCard parser tests, covering both format versions.
/// </summary>
/// <remarks>
/// <para>
/// All fixtures are synthetic. Names are invented and phone numbers are in the reserved
/// <c>+1 555 01xx</c> range, because a real export contains other people's personal data
/// and must never enter the repository (task 0011 privacy rule).
/// </para>
/// <para>
/// The fixtures preserve the <em>structure</em> and the quirks observed in the real exports,
/// which is the part that matters: folding, both parameter syntaxes, inline and referenced
/// photos, and the empty cards.
/// </para>
/// </remarks>
public sealed class VCardParserTests
{
    private static List<ParsedRecord> Parse(string contents, string path = "contacts.vcf")
    {
        var source = new InMemoryRawSource().Add(path, contents);
        var context = new ParseContext();
        return new VCardParser().Parse(source, context).ToList();
    }

    private static List<ObservedAccount> ParseAccounts(string contents, string path = "contacts.vcf") =>
        Parse(contents, path).OfType<ObservedAccount>().ToList();

    // ---------------------------------------------------------------- vCard 2.1

    [Fact]
    public void Parses_vcard_2_1_with_bare_parameters()
    {
        // Shape taken from the real contacts.vcf: VERSION 2.1, TEL;CELL;PREF.
        const string vcf = """
            BEGIN:VCARD
            VERSION:2.1
            N:;Example Person;;;
            FN:Example Person
            TEL;CELL;PREF:+1 555 0101
            END:VCARD
            """;

        var accounts = ParseAccounts(vcf);

        Assert.Single(accounts);
        Assert.Equal("Example Person", accounts[0].DisplayName);
        Assert.Equal(["+1 555 0101"], accounts[0].Phones);
        Assert.Equal("2.1", accounts[0].Extra["vcardVersion"]);
    }

    [Fact]
    public void Reads_2_1_bare_parameter_as_a_type()
    {
        // The trap: in 2.1 `CELL` is a type, not a parameter named CELL.
        const string vcf = """
            BEGIN:VCARD
            VERSION:2.1
            FN:Example
            TEL;CELL:+1 555 0102
            END:VCARD
            """;

        var account = ParseAccounts(vcf)[0];

        Assert.Equal("CELL", account.Extra["tel.1.types"]);
    }

    [Fact]
    public void Decodes_inline_base64_photo_in_2_1()
    {
        // Minimal valid JPEG header bytes, base64-encoded.
        const string base64 = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==";

        var vcf = $"""
            BEGIN:VCARD
            VERSION:2.1
            FN:Photo Person
            PHOTO;ENCODING=BASE64;JPEG:{base64}
            END:VCARD
            """;

        var account = ParseAccounts(vcf)[0];

        Assert.NotNull(account.PhotoBytes);
        Assert.True(account.PhotoBytes!.Length > 0);
        Assert.Equal("JPEG", account.Extra["photoFormat"]);
    }

    // ---------------------------------------------------------------- vCard 3.0

    [Fact]
    public void Parses_vcard_3_0_with_named_type_parameters()
    {
        // Shape taken from real Google Takeout: VERSION 3.0, CRLF, TEL;TYPE=CELL.
        const string vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Example Person\r\nN:Person;Example;;;\r\nTEL;TYPE=CELL:+1 555 0103\r\nEND:VCARD\r\n";

        var account = ParseAccounts(vcf)[0];

        Assert.Equal("Example Person", account.DisplayName);
        Assert.Equal("3.0", account.Extra["vcardVersion"]);
        // The 2.1/3.0 normalisation: CALLER asks for TYPE and gets "CELL" either way.
        Assert.Equal("CELL", account.Extra["tel.1.types"]);
    }

    [Fact]
    public void Both_versions_yield_the_same_type_for_an_equivalent_card()
    {
        // This is the regression test for the whole version-awareness design. If 2.1 and 3.0
        // syntax ever diverge in the parsed output, this fails.
        const string v21 = """
            BEGIN:VCARD
            VERSION:2.1
            FN:Example Person
            TEL;CELL;PREF:+1 555 0104
            END:VCARD
            """;

        const string v30 = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Example Person\r\nTEL;TYPE=CELL;TYPE=PREF:+1 555 0104\r\nEND:VCARD\r\n";

        var a = ParseAccounts(v21)[0];
        var b = ParseAccounts(v30)[0];

        Assert.Equal(a.DisplayName, b.DisplayName);
        Assert.Equal(a.Phones, b.Phones);
        Assert.Equal(a.Extra["tel.1.types"], b.Extra["tel.1.types"]);
    }

    [Fact]
    public void Parses_3_0_referenced_photo_as_a_path()
    {
        const string vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Example\r\nPHOTO;VALUE=URI:photos/example.jpg\r\nEND:VCARD\r\n";

        var account = ParseAccounts(vcf)[0];

        Assert.NotNull(account.PhotoPath);
        Assert.Null(account.PhotoBytes);
    }

    [Fact]
    public void Reads_categories_into_groups()
    {
        const string vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Example\r\nCATEGORIES:myContacts,starred\r\nEND:VCARD\r\n";

        var account = ParseAccounts(vcf)[0];

        Assert.Contains("myContacts", account.Groups);
        Assert.Contains("starred", account.Groups);
    }

    // ---------------------------------------------------------------- folding

    [Fact]
    public void Unfolds_continuation_lines()
    {
        // A folded value spans lines; without unfolding, the value is truncated and the
        // remainder becomes a bogus property.
        //
        // Note the break points: real folders split mid-word and even mid-token, because the
        // split is at a fixed byte width, not at a word boundary. The Takeout export folds
        // "...2360 Motor" + " way Blvd" and "..._106519_24500_6954.sc-10_43" + " _5_195-...".
        // A fixture that folded at spaces would test an easier and less realistic case.
        const string vcf = """
            BEGIN:VCARD
            VERSION:3.0
            FN:Example
            NOTE:A deliberately long value that must be folded across more
             than one line and reassem
             bled exactly, with the break landing mid-word as it does in real exports.
            END:VCARD
            """;

        var account = ParseAccounts(vcf)[0];

        // "more" + "than" joins without a space, because the fold marker is consumed.
        // This is the behaviour that distinguishes correct unfolding from a naive
        // line-join-with-space, and it matches the real export's "Motor" + " way".
        Assert.Equal(
            "A deliberately long value that must be folded across morethan one line and reassembled exactly, with the break landing mid-word as it does in real exports.",
            account.Extra["note"]);
    }

    [Fact]
    public void Unfold_removes_exactly_one_marker_character()
    {
        // A fold is a line break plus exactly ONE space or tab. That single character is the
        // marker and is removed; anything beyond it is value content and must survive.
        //
        // Verified against the real Takeout export, where a logical value splits as
        // "...2360 Motor" + " way Blvd\\nMississauga..." — joining with the space would give
        // "Motor way Blvd", and dropping all whitespace would corrupt values that legitimately
        // contain runs of spaces.
        Assert.Equal("FN:AB", VCardParser.Unfold("FN:A\n B"));
        Assert.Equal("FN:A B", VCardParser.Unfold("FN:A\n  B"));
        Assert.Equal("FN:A  B", VCardParser.Unfold("FN:A\n   B"));
    }

    [Theory]
    [InlineData("A\r\n B", "AB")]   // CRLF fold with a space marker
    [InlineData("A\n\tB", "AB")]    // LF fold with a tab marker
    [InlineData("A\nB", "A\nB")]    // not a fold: no leading whitespace
    [InlineData("A\n\nB", "A\n\nB")] // blank line is not a fold
    public void Unfold_handles_both_line_endings_and_tab_folds(string input, string expected)
    {
        Assert.Equal(expected, VCardParser.Unfold(input));
    }

    // ---------------------------------------------------------------- escapes

    [Fact]
    public void Splits_on_escaped_separators()
    {
        // 58 escaped commas were observed in one real file; a naive split corrupts them.
        var parts = VCardParser.SplitEscaped(@"Role, Equity\, Research", ',');

        Assert.Equal(2, parts.Count);
        Assert.Equal("Role", parts[0]);
        Assert.Equal(" Equity, Research", parts[1]);
    }

    [Fact]
    public void Splits_structured_name_on_semicolons()
    {
        var parts = VCardParser.SplitEscaped("Person;Example;;;", ';');

        Assert.Equal(5, parts.Count);
        Assert.Equal("Person", parts[0]);
        Assert.Equal("Example", parts[1]);
    }

    // ---------------------------------------------------------------- identifiers

    [Fact]
    public void Prefers_phone_as_the_identifier()
    {
        // Phone beats email beats name: it is the strongest identifier available, and this
        // is what lets the same person be matched across two vCard sources.
        const string vcf = """
            BEGIN:VCARD
            VERSION:2.1
            FN:Example Person
            TEL;CELL:+1 555 0105
            EMAIL:example@example.invalid
            END:VCARD
            """;

        var account = ParseAccounts(vcf)[0];

        Assert.StartsWith("tel:", account.NativeId, StringComparison.Ordinal);
        Assert.Equal("phone", account.Extra["idStrategy"]);
    }

    [Fact]
    public void Falls_back_to_email_when_no_phone()
    {
        const string vcf = """
            BEGIN:VCARD
            VERSION:2.1
            FN:Example Person
            EMAIL:example@example.invalid
            END:VCARD
            """;

        var account = ParseAccounts(vcf)[0];

        Assert.StartsWith("email:", account.NativeId, StringComparison.Ordinal);
        Assert.Equal("email", account.Extra["idStrategy"]);
    }

    [Fact]
    public void Falls_back_to_name_and_says_so()
    {
        // A name-derived id is a guess; recording the strategy is what makes that auditable.
        const string vcf = """
            BEGIN:VCARD
            VERSION:2.1
            FN:Example Person
            END:VCARD
            """;

        var account = ParseAccounts(vcf)[0];

        Assert.StartsWith("name:", account.NativeId, StringComparison.Ordinal);
        Assert.Equal("name", account.Extra["idStrategy"]);
    }

    [Fact]
    public void Normalises_phone_digits_for_the_identifier()
    {
        // Two cards for the same person written differently must produce the same id.
        const string a = "BEGIN:VCARD\nVERSION:2.1\nFN:Example A\nTEL;CELL:+1 (555) 010-0106\nEND:VCARD\n";
        const string b = "BEGIN:VCARD\nVERSION:2.1\nFN:Example B\nTEL;CELL:+1 555 010 0106\nEND:VCARD\n";

        Assert.Equal(ParseAccounts(a)[0].NativeId, ParseAccounts(b)[0].NativeId);
    }

    [Fact]
    public void Uses_structured_name_when_FN_is_absent()
    {
        // 117 of 432 real Takeout cards had neither FN nor N; 3 had N alone.
        const string vcf = """
            BEGIN:VCARD
            VERSION:2.1
            N:Person;Example;;;
            TEL;CELL:+1 555 0107
            END:VCARD
            """;

        Assert.Equal("Example Person", ParseAccounts(vcf)[0].DisplayName);
    }

    // ---------------------------------------------------------------- resilience

    [Fact]
    public void Skips_cards_with_no_usable_name_and_reports_them()
    {
        // An account with no name is not a usable observation.
        const string vcf = """
            BEGIN:VCARD
            VERSION:3.0
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Example Person
            END:VCARD
            """;

        var source = new InMemoryRawSource().Add("contacts.vcf", vcf);
        var context = new ParseContext();
        var results = new VCardParser().Parse(source, context).ToList();

        Assert.Single(results);
        Assert.Single(context.Problems);
        Assert.Contains("neither FN nor a usable N", context.Problems[0].Reason);
    }

    [Fact]
    public void One_malformed_card_does_not_lose_the_others()
    {
        // ADR 0003 rule 4. With 432 cards in a real file, losing the lot to one bad row
        // would be unacceptable.
        var vcf = "BEGIN:VCARD\nVERSION:3.0\nFN:First Person\nEND:VCARD\n"
            + "BEGIN:VCARD\nVERSION:3.0\n\x01\x02 garbage \x03\nFN:Second Person\nEND:VCARD\n"
            + "BEGIN:VCARD\nVERSION:3.0\nFN:Third Person\nEND:VCARD\n";

        var accounts = ParseAccounts(vcf);

        Assert.Equal(3, accounts.Count);
    }

    [Fact]
    public void Treats_empty_file_as_empty_not_as_an_error()
    {
        // Three 0-byte .vcf files are normal in a real Google Takeout export.
        var source = new InMemoryRawSource().Add("empty.vcf", string.Empty);
        var context = new ParseContext();
        var results = new VCardParser().Parse(source, context).ToList();

        Assert.Empty(results);
        Assert.Single(context.Problems);
        Assert.Contains("empty file", context.Problems[0].Reason);
    }

    [Fact]
    public void Tolerates_a_missing_trailing_END()
    {
        const string vcf = "BEGIN:VCARD\nVERSION:3.0\nFN:Example Person\n";

        Assert.Single(ParseAccounts(vcf));
    }

    [Fact]
    public void Parses_CRLF_and_LF_identically()
    {
        var lf = ParseAccounts("BEGIN:VCARD\nVERSION:3.0\nFN:Example\nTEL;TYPE=CELL:+1 555 0108\nEND:VCARD\n")[0];
        var crlf = ParseAccounts("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Example\r\nTEL;TYPE=CELL:+1 555 0108\r\nEND:VCARD\r\n")[0];

        Assert.Equal(lf.Phones, crlf.Phones);
        Assert.Equal(lf.Extra["tel.1.types"], crlf.Extra["tel.1.types"]);
    }

    [Fact]
    public void Detects_version_and_defaults_to_2_1_when_absent()
    {
        Assert.Equal("3.0", VCardParser.DetectVersion("BEGIN:VCARD\nVERSION:3.0\nEND:VCARD"));
        Assert.Equal("2.1", VCardParser.DetectVersion("BEGIN:VCARD\nFN:Example\nEND:VCARD"));
    }

    // ---------------------------------------------------------------- multiple files

    [Fact]
    public void Parses_every_vcf_in_the_source()
    {
        var source = new InMemoryRawSource()
            .Add("a/one.vcf", "BEGIN:VCARD\nVERSION:2.1\nFN:One Person\nEND:VCARD\n")
            .Add("b/two.vcf", "BEGIN:VCARD\nVERSION:3.0\nFN:Two Person\nEND:VCARD\n")
            .Add("notes.txt", "ignored");

        var results = new VCardParser().Parse(source, new ParseContext()).ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void CanParse_only_when_a_vcf_is_present()
    {
        var withVcf = new InMemoryRawSource().Add("x.vcf", "BEGIN:VCARD\nEND:VCARD\n");
        var without = new InMemoryRawSource().Add("x.json", "{}");

        Assert.True(new VCardParser().CanParse(withVcf));
        Assert.False(new VCardParser().CanParse(without));
    }

    [Fact]
    public void Parses_multiple_phones_and_records_their_types()
    {
        const string vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Example\r\n"
            + "TEL;TYPE=CELL:+1 555 0109\r\nTEL;TYPE=WORK;TYPE=FAX:+1 555 0110\r\n"
            + "END:VCARD\r\n";

        var account = ParseAccounts(vcf)[0];

        Assert.Equal(2, account.Phones.Count);
        // A fax line must be distinguishable, or the UI will offer to dial it.
        Assert.Contains("FAX", account.Extra["tel.2.types"]);
    }
}

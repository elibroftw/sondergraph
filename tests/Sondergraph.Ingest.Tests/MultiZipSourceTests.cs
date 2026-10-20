using System.IO.Compression;
using Sondergraph.Ingest;
using Sondergraph.Ingest.Parsers;

namespace Sondergraph.Ingest.Tests;

/// <summary>
/// Tests for multi-part archives.
/// </summary>
/// <remarks>
/// Google Takeout splits one export across several zips by size, and not predictably by
/// service: in the observed data one export had contacts in part 1 and mail in part 2, while
/// an adjacent export had an essentially empty part 1. A user who downloads only part 1 can
/// therefore receive zero contacts with no indication anything is missing.
/// </remarks>
public sealed class MultiZipSourceTests : IDisposable
{
    private readonly string _root;

    public MultiZipSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-multizip-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string MakeZip(string name, params (string Path, string Contents)[] files)
    {
        var path = Path.Combine(_root, name);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (filePath, contents) in files)
        {
            var entry = archive.CreateEntry(filePath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(contents);
        }

        return path;
    }

    private const string OneCard = """
        BEGIN:VCARD
        VERSION:3.0
        FN:Example Person
        TEL;TYPE=CELL:+1 555 0201
        END:VCARD
        """;

    private const string OtherCard = """
        BEGIN:VCARD
        VERSION:3.0
        FN:Another Person
        TEL;TYPE=CELL:+1 555 0202
        END:VCARD
        """;

    [Fact]
    public void Aggregates_contacts_split_across_parts()
    {
        // The case that matters: contacts only in part 2. Reading part 1 alone yields nothing.
        var part1 = MakeZip("takeout-1.zip", ("Takeout/Mail/Sent.mbox", "From nobody"));
        var part2 = MakeZip("takeout-2.zip", ("Takeout/Contacts/All Contacts/All Contacts.vcf", OneCard));

        using var multi = MultiZipRawSource.Open([part1, part2], _root);
        var accounts = new VCardParser().Parse(multi, new ParseContext()).OfType<ObservedAccount>().ToList();

        Assert.Single(accounts);
        Assert.Equal("Example Person", accounts[0].Handle);
    }

    [Fact]
    public void Union_of_parts_matches_the_complete_export()
    {
        // Each part holds a different contact; the aggregate must contain both, not the
        // first part's contents only.
        var part1 = MakeZip("a.zip", ("Takeout/Contacts/one.vcf", OneCard));
        var part2 = MakeZip("b.zip", ("Takeout/Contacts/two.vcf", OtherCard));

        using var multi = MultiZipRawSource.Open([part1, part2], _root);
        var accounts = new VCardParser().Parse(multi, new ParseContext()).OfType<ObservedAccount>().ToList();

        Assert.Equal(2, accounts.Count);
    }

    [Fact]
    public void Duplicated_data_across_parts_yields_duplicates_that_the_materialiser_collapses()
    {
        // Two Takeout sets in one directory contain the same contacts. The source exposes
        // both, and deduplication is the materialiser's job - it keys on identifiers - not
        // the reader's. This documents that division of responsibility.
        var part1 = MakeZip("setA.zip", ("Takeout/Contacts/contacts.vcf", OneCard));
        var part2 = MakeZip("setB.zip", ("Takeout/Contacts/contacts.vcf", OneCard));

        using var multi = MultiZipRawSource.Open([part1, part2], _root);
        var accounts = new VCardParser().Parse(multi, new ParseContext()).OfType<ObservedAccount>().ToList();

        Assert.Equal(2, accounts.Count);
        // ...but both carry the same identifier, so the materialiser merges them.
        Assert.Single(accounts.Select(a => a.NativeId).Distinct());
    }

    [Fact]
    public void Unreadable_part_is_reported_not_ignored()
    {
        // A truncated download is a common failure. Producing a partial contact set silently
        // is worse than reporting it.
        var good = MakeZip("good.zip", ("Takeout/Contacts/contacts.vcf", OneCard));
        var bad = Path.Combine(_root, "truncated.zip");
        File.WriteAllBytes(bad, [0x50, 0x4b, 0x03, 0x04]);

        using var multi = MultiZipRawSource.Open([good, bad], _root);
        var accounts = new VCardParser().Parse(multi, new ParseContext()).OfType<ObservedAccount>().ToList();

        Assert.Single(accounts);
        Assert.Single(multi.Problems);
        Assert.Contains("truncated.zip", multi.Problems[0]);
    }

    [Fact]
    public void Empty_part_is_harmless()
    {
        // An export part can be effectively empty - the real data had a 366-byte part 1
        // containing only a 0-byte vcf.
        var empty = MakeZip("empty.zip", ("Takeout/Contacts/Starred in Android/Starred in Android.vcf", ""));
        var real = MakeZip("real.zip", ("Takeout/Contacts/All Contacts/All Contacts.vcf", OneCard));

        using var multi = MultiZipRawSource.Open([empty, real], _root);
        var accounts = new VCardParser().Parse(multi, new ParseContext()).OfType<ObservedAccount>().ToList();

        Assert.Single(accounts);
    }

    [Fact]
    public void Reads_files_from_whichever_part_contains_them()
    {
        var part1 = MakeZip("p1.zip", ("Takeout/Mail/Sent.mbox", "mail"));
        var part2 = MakeZip("p2.zip", ("Takeout/Contacts/x.vcf", OneCard));

        using var multi = MultiZipRawSource.Open([part1, part2], _root);

        Assert.Equal("mail", multi.ReadAllText("Takeout/Mail/Sent.mbox"));
        Assert.Equal(OneCard, multi.ReadAllText("Takeout/Contacts/x.vcf"));
        Assert.Null(multi.ReadAllText("Takeout/Contacts/missing.vcf"));
        Assert.True(multi.Exists("Takeout/Mail/Sent.mbox"));
        Assert.False(multi.Exists("Takeout/Mail/missing.mbox"));
    }
}

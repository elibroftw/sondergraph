using Sondergraph.Core;
using Sondergraph.Ingest;
using Sondergraph.Ingest.Parsers;

// Development probe: runs a parser against a REAL export and prints statistics only.
//
// Deliberately prints no personal data - counts, distributions and a handful of
// structural facts. Its purpose is to validate the parser against ground truth that the
// synthetic fixtures cannot represent, without leaking anyone's details into a terminal
// log or a commit.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Probe <zip|dir> <path> [vcard]");
    return 64;
}

var mode = args[0];
var path = args[1];
var parser = args.Length > 2 ? args[2] : "vcard";

IRawSource? source = null;
IDisposable? disposable = null;

if (mode == "zip")
{
    var opened = ZipRawSource.Open(path);
    if (!opened.IsOk)
    {
        Console.Error.WriteLine($"probe: {opened.Message}");
        return 1;
    }

    source = opened.ValueOrThrow();
    disposable = (IDisposable)source;
}
else
{
    source = new DirectoryRawSource(path);
}

try
{
    IPlatformParser chosen = parser switch
    {
        "vcard" => new VCardParser(),
        _ => throw new ArgumentException($"unknown parser '{parser}'"),
    };

    Console.WriteLine($"source:   {source.Description}");
    Console.WriteLine($"parser:   {chosen.DisplayName} v{chosen.ParserVersion}");
    Console.WriteLine($"canParse: {chosen.CanParse(source)}");
    Console.WriteLine();

    var files = source.EnumerateFiles().ToList();
    Console.WriteLine($"files:    {files.Count}");

    var vcfFiles = files.Count(f => f.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"vcf:      {vcfFiles}");
    Console.WriteLine();

    var context = new ParseContext();
    var accounts = new List<ObservedAccount>();
    var started = DateTime.UtcNow;

    foreach (var record in chosen.Parse(source, context))
    {
        if (record is ObservedAccount account)
        {
            accounts.Add(account);
        }
    }

    var elapsed = DateTime.UtcNow - started;

    Console.WriteLine("=== RESULTS ===");
    Console.WriteLine($"accounts:       {accounts.Count}");
    Console.WriteLine($"problems:       {context.Problems.Count}");
    Console.WriteLine($"elapsed:        {elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine();

    Console.WriteLine("=== ID STRATEGY ===");
    foreach (var group in accounts.GroupBy(a => a.Extra.GetValueOrDefault("idStrategy", "?")).OrderBy(g => g.Key))
    {
        Console.WriteLine($"  {group.Key,-8} {group.Count()}");
    }

    Console.WriteLine();
    Console.WriteLine("=== VCARD VERSION ===");
    foreach (var group in accounts.GroupBy(a => a.Extra.GetValueOrDefault("vcardVersion", "?")).OrderBy(g => g.Key))
    {
        Console.WriteLine($"  {group.Key,-8} {group.Count()}");
    }

    Console.WriteLine();
    Console.WriteLine("=== FIELD COVERAGE ===");
    Console.WriteLine($"  with phone:     {accounts.Count(a => a.Phones.Count > 0)}");
    Console.WriteLine($"  with email:     {accounts.Count(a => a.Emails.Count > 0)}");
    Console.WriteLine($"  with photo:     {accounts.Count(a => a.PhotoBytes is not null || a.PhotoPath is not null)}");
    Console.WriteLine($"  with groups:    {accounts.Count(a => a.Groups.Count > 0)}");
    Console.WriteLine($"  with org:       {accounts.Count(a => a.Extra.ContainsKey("org"))}");
    Console.WriteLine($"  with address:   {accounts.Count(a => a.Extra.ContainsKey("address"))}");
    Console.WriteLine($"  with birthday:  {accounts.Count(a => a.Extra.ContainsKey("birthday"))}");
    Console.WriteLine($"  with note:      {accounts.Count(a => a.Extra.ContainsKey("note"))}");
    Console.WriteLine();

    Console.WriteLine("=== TEL PARAMETER FORMS ===");
    foreach (var group in accounts
        .SelectMany(a => a.Extra.Where(kv => kv.Key.EndsWith(".types", StringComparison.Ordinal)))
        .GroupBy(kv => kv.Value)
        .OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"  {group.Key,-24} {group.Count()}");
    }

    var multiPhone = accounts.Count(a => a.Phones.Count > 1);
    Console.WriteLine($"  (accounts with >1 phone: {multiPhone})");

    Console.WriteLine();
    Console.WriteLine("=== PROBLEMS (reasons only) ===");
    foreach (var group in context.Problems.GroupBy(p => p.Reason).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"  {group.Count(),5}  {group.Key}");
    }

    Console.WriteLine();
    Console.WriteLine("=== DUPLICATE NATIVE IDS ===");
    var duplicates = accounts
        .GroupBy(a => a.NativeId, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .OrderByDescending(g => g.Count())
        .ToList();

    Console.WriteLine($"  ids appearing more than once: {duplicates.Count}");
    Console.WriteLine($"  redundant records:            {duplicates.Sum(g => g.Count() - 1)}");

    var totalPhones = accounts.SelectMany(a => a.Phones).Count();
    var distinctPhoneIds = accounts.Where(a => a.NativeId.StartsWith("tel:", StringComparison.Ordinal)).Select(a => a.NativeId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    Console.WriteLine($"  total phone numbers:          {totalPhones}");
    Console.WriteLine($"  distinct phone identifiers:   {distinctPhoneIds}");

    return 0;
}
finally
{
    disposable?.Dispose();
}

using Sondergraph.Core;

namespace Sondergraph.People;

/// <summary>
/// Creates a contact by hand, with no export behind it.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in the app is derived from a dump: the parsers read a file, the materialiser
/// matches it against the store, and the record appears. That is fine for the thousand contacts
/// a Facebook export knows about and wrong for the one person you met last week, who has no
/// export to come from. Without this the only way to add somebody was to fabricate a vCard and
/// ingest it, which is not a thing to ask of a user.
/// </para>
/// <para>
/// The record is marked with the <c>manual</c> source. That is what stops a later re-import
/// from treating the contact as uninteresting, and it is what lets the UI offer "show me only
/// what I typed in". It reads as "Contacts" to the user, because that is what it is.
/// </para>
/// </remarks>
public sealed class PersonCreator
{
    /// <summary>
    /// The source recorded for hand-entered contacts.
    /// </summary>
    /// <remarks>
    /// A source rather than a flag on the record, because everything that consumes sources -
    /// the filter dropdown, the "matched across" list, the per-source counts - then works for
    /// hand-entered people without special cases.
    /// </remarks>
    public const string ManualSource = "manual";

    private readonly PersonStore _store;

    public PersonCreator(PersonStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Creates a person and returns the id chosen for them.
    /// </summary>
    /// <param name="name">What to call them. Required.</param>
    /// <param name="phones">Phone numbers, taken as typed.</param>
    /// <param name="emails">Email addresses.</param>
    /// <param name="accounts">Social accounts, already built.</param>
    public Result<Person> Create(
        string? name,
        IReadOnlyList<string>? phones = null,
        IReadOnlyList<string>? emails = null,
        IReadOnlyList<PlatformAccount>? accounts = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Fail<Person>(ErrorCode.Unknown, "A contact needs a name.");
        }

        var trimmed = name.Trim();

        // Derive the id the way ingest does, then refuse to reuse an existing one.
        //
        // AllocateId returns the existing id when the *name* matches exactly, because that is
        // what makes re-ingest idempotent. Here it would be data loss: two people really can
        // share a name, and creating "Alex Chen" when an Alex Chen already exists would have
        // silently overwritten the first one - phone numbers and all. Creating is always
        // adding, so a taken id gets a suffix.
        var id = UniqueId(trimmed);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var phoneList = CleanPhones(phones);
        var emailList = CleanEmails(emails);
        var accountList = CleanAccounts(accounts);

        var person = new Person
        {
            Id = id,
            Name = trimmed,
            Phones = phoneList,
            Emails = emailList,
            Accounts = accountList,

            // The manual source, plus any platform a hand-added account belongs to. A person
            // typed in with only a phone number is a contact; one typed in with an Instagram
            // handle is also on Instagram, and the source filter should say so.
            Sources = [ManualSource, .. accountList.Select(a => a.Platform).Distinct(StringComparer.OrdinalIgnoreCase)],
            CreatedAt = today,
            UpdatedAt = today,
        };

        var saved = _store.Save(person);

        return saved.IsOk
            ? Result.Ok(person)
            : Result.Fail<Person>(saved.Code, saved.Message, saved.Hint);
    }

    /// <summary>
    /// Derives an id from a name that no record currently holds.
    /// </summary>
    /// <remarks>
    /// A suffix rather than a refusal: the user asked for a new contact, and "there is already
    /// someone called that" is not a reason to stop them. If they meant the existing person,
    /// they can delete the duplicate or merge the two - both of which are reversible, unlike
    /// an overwritten record.
    /// </remarks>
    private string UniqueId(string name)
    {
        var baseId = Core.Person.DeriveId(name);

        if (!_store.Exists(baseId))
        {
            return baseId;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";

            if (!_store.Exists(candidate))
            {
                return candidate;
            }
        }

        // A thousand records sharing a name is not a real situation, but looping forever is
        // worse than an ugly id.
        return $"{baseId}-{Guid.NewGuid().ToString("n")[..8]}";
    }

    /// <summary>Trims, drops blanks, and removes duplicates that differ only in formatting.</summary>
    private static List<string> CleanPhones(IReadOnlyList<string>? phones)
    {
        if (phones is null)
        {
            return [];
        }

        var result = new List<string>();

        foreach (var phone in phones)
        {
            if (string.IsNullOrWhiteSpace(phone))
            {
                continue;
            }

            var value = phone.Trim();
            var digits = Person.DigitsOnly(value);

            // A "number" with no digits is not a phone number; anything shorter than seven is
            // an extension or a shortcode, which the vCard parser also refuses.
            if (digits.Length < 7)
            {
                continue;
            }

            if (!result.Any(existing => Person.DigitsOnly(existing) == digits))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static List<string> CleanEmails(IReadOnlyList<string>? emails)
    {
        if (emails is null)
        {
            return [];
        }

        var result = new List<string>();

        foreach (var email in emails)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            var value = email.Trim();

            // Typed by a human, so a typo is likely. Reject anything that is not shaped like
            // an address rather than storing it and failing to reach the person later.
            if (!value.Contains('@', StringComparison.Ordinal) || value.StartsWith('@') || value.EndsWith('@'))
            {
                continue;
            }

            if (!value.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static List<PlatformAccount> CleanAccounts(IReadOnlyList<PlatformAccount>? accounts)
    {
        if (accounts is null)
        {
            return [];
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = new List<PlatformAccount>();

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Platform) || string.IsNullOrWhiteSpace(account.Handle))
            {
                continue;
            }

            // Same platform and handle means the same account; keep the first.
            if (result.Any(existing =>
                string.Equals(existing.Platform, account.Platform, StringComparison.OrdinalIgnoreCase)
                && existing.HandleNormalised == account.HandleNormalised))
            {
                continue;
            }

            result.Add(account with { FirstSeen = account.FirstSeen ?? today });
        }

        return result;
    }
}

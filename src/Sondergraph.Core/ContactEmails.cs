namespace Sondergraph.Core;

/// <summary>
/// Decides which email addresses are worth keeping as a person's contact detail.
/// </summary>
/// <remarks>
/// <para>
/// Exports carry far more addresses than real people. GitHub is the clearest offender: every
/// thread notification arrives from a unique
/// <c>reply+AFCPYIZ...@reply.github.com</c> address, and tools that sync a mailbox into
/// contacts turn each one into a "contact" whose name is the repository path
/// (<c>PySimpleGUI/PySimpleGUI</c>).
/// </para>
/// <para>
/// The rule lives here, in Core, rather than in each parser: the same junk arrives through
/// vCard, LinkedIn and anything else that ever reads a mailbox, and one filter is easier to
/// reason about than four copies of the same condition.
/// </para>
/// </remarks>
public static class ContactEmails
{
    /// <summary>
    /// Local-part prefixes that mark a machine-generated address.
    /// </summary>
    /// <remarks>
    /// Matched after stripping separators from the local part, so "no-reply", "no_reply"
    /// and "noreply" are all covered by one entry. Compared case-insensitively.
    /// </remarks>
    /// Separators are stripped before comparison, so these are written without them:
    /// "mailer-daemon" is "mailerdaemon" here and matches the real address either way.
    private static readonly string[] MachineLocalParts =
    [
        "reply",
        "noreply",
        "donotreply",
        "notifications",
        "bounce",
        "postmaster",
        "mailerdaemon",
        "nobody",
    ];

    /// <summary>
    /// Domains that exist only to relay messages and never belong to a person.
    /// </summary>
    /// <remarks>
    /// Bare <c>github.com</c> is deliberately NOT here. A GitHub employee could legitimately
    /// have one, and the subdomains below already cover every notification address. Blocking
    /// the parent domain would be a rule that looks thorough and quietly drops real people.
    /// </remarks>
    private static readonly string[] MachineDomains =
    [
        "reply.github.com",
        "notifications.github.com",
        "reply.google.com",
    ];

    /// <summary>
    /// Whether an address should be kept as a contact detail.
    /// </summary>
    /// <param name="email">The candidate address.</param>
    /// <returns>False for machine-generated addresses and malformed input.</returns>
    /// <remarks>
    /// Deliberately conservative in the other direction too: an address that is merely
    /// unusual is kept. Discarding a real person's email is worse than storing a junk one,
    /// because the user can see and delete junk but cannot see what was silently dropped.
    /// </remarks>
    public static bool IsPersonAddress(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');

        // Not an address at all: no @, ends with it, or nothing before it.
        if (at <= 0 || at == trimmed.Length - 1)
        {
            return false;
        }

        var local = trimmed[..at];
        var domain = trimmed[(at + 1)..].TrimEnd('.');
        var localFolded = Fold(local);
        var domainFolded = domain.ToLowerInvariant();

        // "reply+AFCPYIZ...@reply.github.com": the +tag form is how GitHub, Google Groups
        // and mailing lists route a thread, so the prefix before the + is what identifies it.
        foreach (var machine in MachineLocalParts)
        {
            if (localFolded.StartsWith(machine, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var machineDomain in MachineDomains)
        {
            if (domainFolded == machineDomain || domainFolded.EndsWith("." + machineDomain, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Keeps only the addresses that look like a person's.</summary>
    public static IEnumerable<string> KeepPersonAddresses(IEnumerable<string> emails) =>
        emails.Where(IsPersonAddress).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lowercases a local part and drops separators for prefix comparison.</summary>
    private static string Fold(string local) =>
        new(local.Where(c => c is not ('.' or '-' or '_' or '+')).Select(char.ToLowerInvariant).ToArray());
}

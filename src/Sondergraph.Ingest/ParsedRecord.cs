namespace Sondergraph.Ingest;

/// <summary>
/// Something observed in a dump, before identity resolution.
/// </summary>
/// <remarks>
/// <para>
/// Parsers emit these, never <c>Person</c> (ADR 0003). A parser cannot know that
/// <c>@ada@example.social</c> and "Ada Lovelace" are the same human — that is identity
/// resolution, and putting it in parsers would mean N implementations of the riskiest logic
/// in the system.
/// </para>
/// <para>
/// <see cref="Extra"/> is the deliberate escape hatch: platform-specific fields the core
/// ignores and per-platform consumers may read. Promoting a field to a real property should
/// be evidence-driven — if several platforms need it — not speculative.
/// </para>
/// </remarks>
public abstract record ParsedRecord
{
    /// <summary>Platform key, matching the <c>data/raw/&lt;platform&gt;/</c> directory.</summary>
    public required string Platform { get; init; }

    /// <summary>
    /// The platform's own identifier for this thing.
    /// </summary>
    /// <remarks>
    /// Never synthesised from a guess (ADR 0003 rule 1). When a platform has no stable id,
    /// the parser derives one deterministically from content and records that fact in
    /// <see cref="Extra"/> under <c>idStrategy</c>.
    /// </remarks>
    public required string NativeId { get; init; }

    /// <summary>Where in the dump this came from, for provenance and error messages.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Platform-specific fields the core does not model.</summary>
    public Dictionary<string, string> Extra { get; init; } = [];
}

/// <summary>A platform account observed in a dump.</summary>
public sealed record ObservedAccount : ParsedRecord
{
    /// <summary>The account's handle, as the platform writes it.</summary>
    public required string Handle { get; init; }

    /// <summary>Display name on that platform, when the dump provides one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Profile URL, when the dump provides one.</summary>
    public string? ProfileUrl { get; init; }

    /// <summary>Phone numbers associated with this account.</summary>
    public IReadOnlyList<string> Phones { get; init; } = [];

    /// <summary>Email addresses associated with this account.</summary>
    public IReadOnlyList<string> Emails { get; init; } = [];

    /// <summary>Alternative names, e.g. a stored nickname.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Group labels the source attached, e.g. Google Contacts categories.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>
    /// Path to a media file on disk, relative to the dump root.
    /// </summary>
    /// <remarks>
    /// A path rather than bytes: the acquirer keeps media where it found it, and
    /// materialisation copies it into <c>data/people/&lt;id&gt;/media/</c>. Keeps large
    /// binaries out of the parse pipeline.
    /// </remarks>
    public string? PhotoPath { get; init; }

    /// <summary>Embedded photo bytes, for formats that inline them (vCard 2.1).</summary>
    public byte[]? PhotoBytes { get; init; }

    /// <summary>
    /// Whether this record describes a real profile on the platform, as opposed to a name or
    /// phone number the export happened to list.
    /// </summary>
    /// <remarks>
    /// A Facebook friend list gives <c>{name, timestamp}</c> and nothing else, and Facebook's
    /// imported-contacts file is a phone book. Both describe people without describing a
    /// profile. They are still worth ingesting - the name is what lets a friend list enrich a
    /// contact that arrived from elsewhere - but calling them a profile put a "facebook" badge
    /// on 74 people who have no Facebook presence recorded.
    /// </remarks>
    public bool HasProfile { get; init; } = true;

    /// <summary>
    /// Whether seeing this record should add its platform to the person's source list.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="HasProfile"/>: a record that describes no profile must not claim
    /// the person is on that platform. It can still match and enrich, which is the point of
    /// ingesting it at all.
    /// </remarks>
    public bool AddsSource { get; init; } = true;
}

/// <summary>What kind of relationship an interaction represents.</summary>
public enum InteractionKind
{
    /// <summary>Unclassified.</summary>
    Unknown = 0,

    /// <summary>Became friends.</summary>
    Friend = 1,

    /// <summary>Unfriended.</summary>
    Unfriended = 2,

    /// <summary>Sent a friend request.</summary>
    FriendRequestSent = 3,

    /// <summary>Received a friend request.</summary>
    FriendRequestReceived = 4,

    /// <summary>Rejected a friend request.</summary>
    FriendRequestRejected = 5,

    /// <summary>Started following.</summary>
    Followed = 6,

    /// <summary>Connected on a professional network.</summary>
    Connected = 7,

    /// <summary>Marked as a close friend on the platform.</summary>
    CloseFriend = 8,

    /// <summary>Blocked.</summary>
    Blocked = 9,

    /// <summary>Restricted or hidden.</summary>
    Restricted = 10,

    /// <summary>Posted something.</summary>
    Posted = 11,

    /// <summary>Commented on something.</summary>
    Commented = 12,

    /// <summary>Reacted to something.</summary>
    Reacted = 13,
}

/// <summary>A relationship or event observed in a dump.</summary>
public sealed record ObservedInteraction : ParsedRecord
{
    /// <summary>The other party's handle or name.</summary>
    public required string WithHandle { get; init; }

    /// <summary>What happened.</summary>
    public required InteractionKind Kind { get; init; }

    /// <summary>
    /// When it happened, if known.
    /// </summary>
    /// <remarks>
    /// Null is common and expected — some platforms simply do not date relationships, and
    /// inventing a timestamp from ingest time would corrupt the decay feature
    /// (DESIGN.md §4.1). Null means "unknown", not "now".
    /// </remarks>
    public DateTimeOffset? OccurredAt { get; init; }
}

/// <summary>A post observed in a dump.</summary>
public sealed record ObservedPost : ParsedRecord
{
    /// <summary>Post body text, as markdown.</summary>
    public required string Body { get; init; }

    /// <summary>When it was published.</summary>
    public required DateTimeOffset PostedAt { get; init; }

    /// <summary>Canonical URL, when the platform provides one.</summary>
    public string? CanonicalUrl { get; init; }

    /// <summary>Media paths relative to the dump root.</summary>
    public IReadOnlyList<string> MediaPaths { get; init; } = [];

    /// <summary>Tags or hashtags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// A record that could not be parsed, retained so failures are auditable.
/// </summary>
/// <remarks>
/// ADR 0003 rule 4: never fail a whole parse for one bad record. But silently dropping data
/// is worse than failing — so every skip is counted and the reason recorded, and the caller
/// reports the total.
/// </remarks>
/// <param name="SourcePath">Where the problem was.</param>
/// <param name="Reason">Why it was skipped.</param>
/// <param name="Detail">Optional raw excerpt or context.</param>
public sealed record ParseProblem(string SourcePath, string Reason, string? Detail = null);

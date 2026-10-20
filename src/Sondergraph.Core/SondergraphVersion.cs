namespace Sondergraph.Core;

/// <summary>
/// Build and protocol information shared by every front-end.
/// </summary>
/// <remarks>
/// This lives in Core so that the CLI's <c>--version</c> and the API's version endpoint
/// report from one source. Two shells reporting different versions would be a bug
/// waiting to happen (see ADR 0001 on why the shells hold no logic of their own).
/// </remarks>
public static class SondergraphVersion
{
    /// <summary>Human-readable product version.</summary>
    public const string Version = "0.0.1";

    /// <summary>
    /// Wire protocol version for the local API.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Version"/> on purpose. The CLI and API version
    /// independently; a desktop shell or third-party client cares about the wire
    /// contract, not our release cadence. Bump this only on a breaking API change —
    /// the handshake design in docs/architecture-precedents.md §3 depends on being
    /// able to detect a protocol mismatch and tell the user to restart.
    /// </remarks>
    public const int Protocol = 1;
}

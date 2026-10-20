using System.Runtime.CompilerServices;

// The parsers' text-manipulation helpers (unfolding, escape splitting, version detection) are
// deliberately internal: they are implementation details of the vCard format, not part of the
// ingest contract. They are also exactly the pieces most worth testing directly — a folding
// bug corrupts every value in the file, and pinning that behaviour to a unit test is far more
// useful than only observing it through a whole parsed card.
[assembly: InternalsVisibleTo("Sondergraph.Ingest.Tests")]

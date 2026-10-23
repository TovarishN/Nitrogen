using System.Collections.ObjectModel;

namespace Nitrogen;

// Marks read-only pattern parsing. Construction quotes must still require declared, typed holes.
internal sealed class PatternHoleContracts(IDictionary<string, QuoteHole> contracts)
    : ReadOnlyDictionary<string, QuoteHole>(contracts);

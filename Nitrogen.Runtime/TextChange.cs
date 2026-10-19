namespace Nitrogen;

/// <summary>
/// One edit of a text, in the old text's coordinates: <paramref name="OldLength"/> characters at
/// <paramref name="Start"/> were replaced by <paramref name="NewLength"/> new ones. A reparse takes its
/// changes sorted by start and non-overlapping.
/// </summary>
public readonly record struct TextChange(int Start, int OldLength, int NewLength);

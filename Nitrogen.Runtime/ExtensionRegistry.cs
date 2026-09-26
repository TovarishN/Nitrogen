namespace Nitrogen;

/// <summary>Collects extension points and extensions while a <see cref="LanguageBuilder"/> composes modules.</summary>
public sealed class ExtensionRegistry
{
    sealed class Entry
    {
        public readonly List<(string Module, int Sequence, PrefixExtension Extension)> Prefix = [];
        public readonly List<(string Module, int Sequence, PostfixExtension Extension)> Postfix = [];
        public readonly List<SyntaxModule> Contributors = [];
    }

    readonly Dictionary<ExtensionPointDecl, Entry> _points = [];
    int _sequence;

    internal SyntaxModule? Current { get; set; }

    public void Declare(ExtensionPointDecl point)
    {
        var module = CurrentModule();
        if (point.Owner != module)
            throw new LanguageCompositionException($"Module '{module.Name}' cannot declare '{point}'; only '{point.Owner.Name}' can.");
        GetEntry(point);
    }

    public void AddPrefix(ExtensionPointDecl point, PrefixExtension extension)
    {
        if (extension.Precedence is < 0 or > 255)
            throw new LanguageCompositionException($"Prefix alternative '{extension.Name}' of '{point}' has precedence {extension.Precedence}; it must be 0..255.");
        var module = CurrentModule();
        var entry = GetEntry(point);
        entry.Prefix.Add((module.Name, _sequence++, extension));
        entry.Contributors.Add(module);
    }

    public void AddPostfix(ExtensionPointDecl point, PostfixExtension extension)
    {
        if (extension.Precedence is < 1 or > 255)
            throw new LanguageCompositionException($"Postfix alternative '{extension.Name}' of '{point}' has precedence {extension.Precedence}; it must be 1..255.");
        var module = CurrentModule();
        var entry = GetEntry(point);
        entry.Postfix.Add((module.Name, _sequence++, extension));
        entry.Contributors.Add(module);
    }

    /// <summary>
    /// Validates and freezes. Alternatives are ordered by module name, then registration order,
    /// so the result does not depend on the order modules were added.
    /// </summary>
    internal ExtensionPoint?[] Freeze(IReadOnlyList<SyntaxModule> modules)
    {
        int maxId = -1;
        foreach (var (point, entry) in _points)
        {
            if (!modules.Contains(point.Owner))
            {
                string extender = entry.Contributors.Count > 0 ? entry.Contributors[0].Name : "?";
                throw new LanguageCompositionException(
                    $"Module '{extender}' extends '{point}', but module '{point.Owner.Name}' is not part of this language.");
            }
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in entry.Prefix.Select(p => (p.Module, p.Extension.Name))
                         .Concat(entry.Postfix.Select(p => (p.Module, p.Extension.Name)))
                         .OrderBy(item => item.Name, StringComparer.Ordinal)
                         .ThenBy(item => item.Module, StringComparer.Ordinal))
                if (!names.TryAdd(item.Name, item.Module))
                    throw Duplicate(point, item.Name, names[item.Name], item.Module);
            maxId = Math.Max(maxId, point.GlobalId);
        }

        var points = new ExtensionPoint?[maxId + 1];
        foreach (var (point, entry) in _points)
        {
            var prefix = entry.Prefix
                .OrderBy(p => p.Module, StringComparer.Ordinal).ThenBy(p => p.Sequence)
                .Select(p => p.Extension).ToArray();
            var postfix = entry.Postfix
                .OrderBy(p => p.Module, StringComparer.Ordinal).ThenBy(p => p.Sequence)
                .Select(p => p.Extension).ToArray();
            points[point.GlobalId] = new ExtensionPoint(point, prefix, postfix);
        }
        return points;
    }

    Entry GetEntry(ExtensionPointDecl point)
    {
        if (!_points.TryGetValue(point, out var entry)) _points[point] = entry = new Entry();
        return entry;
    }

    SyntaxModule CurrentModule() =>
        Current ?? throw new InvalidOperationException("Extensions can only be registered while a LanguageBuilder builds.");

    static LanguageCompositionException Duplicate(ExtensionPointDecl point, string name, string first, string second) =>
        new($"Extension point '{point}' has two alternatives named '{name}' from '{first}' and '{second}'.");
}

using Nitrogen.Binding;

namespace Nitrogen.Semantics;

/// <summary>
/// Semantics beside a binding <see cref="Project"/> (issue 239). It holds one <see cref="FileSemantics"/>
/// per path and resolves symbol properties in the file that declares the symbol. Any project change
/// (its <see cref="Project.Version"/> moves) drops everything; evaluation is lazy, so that is cheap.
/// </summary>
public sealed class ProjectSemantics(Project project)
{
    readonly Dictionary<string, FileSemantics> _files = new(StringComparer.Ordinal);
    readonly Dictionary<(Symbol Symbol, SymbolProperty Property), object?> _symbols = new();
    readonly HashSet<(Symbol Symbol, SymbolProperty Property)> _computing = new();
    int _version = -1;

    public Project Project { get; } = project;

    public FileSemantics this[string path]
    {
        get
        {
            Sync();
            if (!_files.TryGetValue(path, out var file)) _files[path] = file = new FileSemantics(this, Project[path]);
            return file;
        }
    }

    internal T GetSymbol<T>(Symbol symbol, SymbolProperty<T> property)
    {
        Sync();
        if (symbol.IsBuiltin || !property.Kinds.Contains(symbol.Kind)) return property.Default(symbol.Kind, symbol.Name);
        var key = (symbol, (SymbolProperty)property);
        if (_symbols.TryGetValue(key, out var cached)) return (T)cached!;
        var file = this[symbol.Path!];
        if (!_computing.Add(key))
        {
            file.Report(symbol.Node, property.Name, SemanticCodes.Cycle, $"'{property.Name}' of {symbol.Kind} '{symbol.Name}' depends on itself");
            return property.Default(symbol.Kind, symbol.Name);
        }
        T value = file.Declared(symbol, property);
        _computing.Remove(key);
        _symbols[key] = value;
        return value;
    }

    void Sync()
    {
        if (_version == Project.Version) return;
        _files.Clear();
        _symbols.Clear();
        _computing.Clear();
        _version = Project.Version;
    }
}

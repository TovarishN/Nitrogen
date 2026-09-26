namespace Nitrogen.Workspace;

/// <summary>
/// One compile of a <see cref="GrammarWorkspace"/>: its diagnostics and, when it compiled, its
/// language. The caller of <see cref="GrammarWorkspace.Compile"/> owns it. Disposing it lets the
/// generated assembly unload once no tree or result parsed with it is alive.
/// </summary>
public sealed class WorkspaceSnapshot : IDisposable
{
    WorkspaceLoadContext? _context;
    SyntaxModule[] _modules;

    internal WorkspaceSnapshot(int version, IReadOnlyList<WorkspaceDiagnostic> diagnostics,
        Language? language = null, WorkspaceLoadContext? context = null, SyntaxModule[]? modules = null)
    {
        Version = version;
        Diagnostics = diagnostics;
        Language = language;
        _context = context;
        _modules = modules ?? [];
    }

    /// <summary>The number of this compile in its workspace, from 1.</summary>
    public int Version { get; }

    public IReadOnlyList<WorkspaceDiagnostic> Diagnostics { get; }

    /// <summary>The compiled language; null when the compile failed or the snapshot is disposed.</summary>
    public Language? Language { get; private set; }

    public bool Succeeded => Language is not null;

    /// <summary>The rule <c>Module.Rule</c>; the module name may itself contain dots.</summary>
    public Rule? FindRule(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0) return null;
        string module = name[..dot], rule = name[(dot + 1)..];
        foreach (var candidate in _modules)
            if (candidate.Name == module) return candidate.GetRule(rule);
        return null;
    }

    public ParseResult Parse(string text, string rule)
    {
        var language = Language ?? throw new InvalidOperationException($"Snapshot {Version} did not compile.");
        var start = FindRule(rule) ?? throw new ArgumentException($"Snapshot {Version} has no rule '{rule}'.", nameof(rule));
        return language.Parse(text, start);
    }

    /// <summary>The snapshot's load context, weakly: the unload test's probe.</summary>
    internal WeakReference LoadContextReference() => new(_context);

    public void Dispose()
    {
        Language = null;
        _modules = [];
        var context = _context;
        _context = null;
        context?.Unload();
    }
}

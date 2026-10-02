using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Nitrogen.Grammar;
using Nitrogen.Semantic;
using CodeAnalysis = Microsoft.CodeAnalysis; // Nitrogen has its own Diagnostic types

namespace Nitrogen.Workspace;

/// <summary>
/// The grammar authoring loop (issue 236). A set of .ngr files is compiled in-process into one
/// collectible assembly per <see cref="Compile"/>. The helper sources' public static
/// <see cref="ModuleDescriptor"/> and <see cref="SemanticModule"/> fields and properties supply the
/// language's semantic modules, so its declarative typing and lowering run. Each compile yields a
/// <see cref="WorkspaceSnapshot"/> owned by the caller; <see cref="Current"/> is the latest one that
/// compiled.
/// </summary>
public sealed class GrammarWorkspace
{
    /// <summary>The C# namespace workspace grammars are generated into by default.</summary>
    public const string Namespace = "Nitrogen.Workspace.Grammar";

    static readonly Lazy<CodeAnalysis.MetadataReference[]> s_references = new(LoadReferences);

    /// <summary>The .NET SDK's implicit usings, so helper sources compile here as in the project they come from.</summary>
    const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    readonly SortedDictionary<string, string> _files = new(StringComparer.Ordinal);
    readonly Action<LanguageBuilder>? _configure;
    int _version;

    /// <param name="configure">Applied to every snapshot's <see cref="LanguageBuilder"/> before the modules, e.g. to set trivia.</param>
    public GrammarWorkspace(Action<LanguageBuilder>? configure = null) => _configure = configure;

    public WorkspaceSnapshot? Current { get; private set; }

    public IReadOnlyCollection<string> Paths => _files.Keys;

    /// <summary>The C# namespace the grammars are generated into: where helper sources find the generated modules and views.</summary>
    public string GeneratedNamespace { get; set; } = Namespace;

    /// <summary>Namespaces the generated code imports (issue 239): where semantics blocks' helper types live.</summary>
    public IList<string> Usings { get; } = new List<string>();

    /// <summary>Assemblies the generated code may use besides the framework and Nitrogen.Runtime (issue 239): the helpers' own.</summary>
    public IList<Assembly> References { get; } = new List<Assembly>();

    /// <summary>C# files compiled with the generated code, by path (issue 239): a workspace grammar's helper types.</summary>
    public IDictionary<string, string> Sources { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

    public void SetGrammar(string path, string text) => _files[path] = text;

    public bool RemoveGrammar(string path) => _files.Remove(path);

    public WorkspaceSnapshot Compile()
    {
        int version = ++_version;
        var grammar = GrammarCompiler.Compile(_files.Select(f => new GrammarInput(f.Key, f.Value, GeneratedNamespace)).ToArray());
        var diagnostics = grammar.Diagnostics.Select(FromGrammar).ToList();
        if (grammar.HasErrors) return new WorkspaceSnapshot(version, diagnostics);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = grammar.Sources.Select(s => CSharpSyntaxTree.ParseText(s.Code, parseOptions, path: s.HintName)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions, path: "ImplicitUsings.g.cs"));
        if (Usings.Count > 0)
            trees.Add(CSharpSyntaxTree.ParseText(string.Concat(Usings.Select(u => $"global using {u};\n")), parseOptions, path: "Usings.g.cs"));
        trees.AddRange(Sources.Select(s => CSharpSyntaxTree.ParseText(s.Value, parseOptions, path: s.Key)));
        var references = s_references.Value.Concat(References.Select(a => (CodeAnalysis.MetadataReference)CodeAnalysis.MetadataReference.CreateFromFile(PathOf(a))));
        var compilation = CSharpCompilation.Create($"NitrogenWorkspace{version}", trees, references,
            new CSharpCompilationOptions(CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                optimizationLevel: CodeAnalysis.OptimizationLevel.Release, nullableContextOptions: CodeAnalysis.NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        diagnostics.AddRange(emitted.Diagnostics.Where(d => d.Severity == CodeAnalysis.DiagnosticSeverity.Error).Select(FromCSharp));
        if (!emitted.Success) return new WorkspaceSnapshot(version, diagnostics);

        image.Position = 0;
        var context = new WorkspaceLoadContext();
        try
        {
            var assembly = context.LoadFromStream(image);
            var types = assembly.GetTypes();
            var modules = types
                .Where(t => t.IsSubclassOf(typeof(SyntaxModule)) && !t.IsAbstract)
                .Select(t => (SyntaxModule)t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();
            var builder = new LanguageBuilder();
            _configure?.Invoke(builder);
            foreach (var module in modules) builder.Add(module);
            foreach (var semantics in SemanticModules(types)) builder.AddSemantic(semantics);
            if (!builder.TryBuild(out var language, out var composition))
            {
                context.Unload();
                diagnostics.AddRange(composition.Select(d => new WorkspaceDiagnostic("", 0, 0, d.Code, d.Message, IsError: true)));
                return new WorkspaceSnapshot(version, diagnostics);
            }
            var snapshot = new WorkspaceSnapshot(version, diagnostics, language, context, modules);
            Current = snapshot;
            return snapshot;
        }
        catch (LanguageCompositionException error)
        {
            context.Unload();
            diagnostics.Add(new WorkspaceDiagnostic("", 0, 0, "NGR0300", error.Message, IsError: true));
            return new WorkspaceSnapshot(version, diagnostics);
        }
        catch (Exception error) when (error is TypeInitializationException or TargetInvocationException)
        {
            // A helper's static initializer or getter threw while its module was read.
            context.Unload();
            diagnostics.Add(new WorkspaceDiagnostic("", 0, 0, "NGR0301", error.GetBaseException().Message, IsError: true));
            return new WorkspaceSnapshot(version, diagnostics);
        }
    }

    /// <summary>
    /// The semantic modules of the public static <see cref="ModuleDescriptor"/> and <see cref="SemanticModule"/>
    /// fields and properties of <paramref name="types"/>, each once, ordered by type and member name.
    /// </summary>
    static List<SemanticModule> SemanticModules(IEnumerable<Type> types)
    {
        var found = new List<SemanticModule>();
        foreach (var type in types.Where(t => t.IsPublic || t.IsNestedPublic).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var values = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(ModuleDescriptor) || f.FieldType == typeof(SemanticModule))
                .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.GetValue(null))
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .Where(p => p.GetIndexParameters().Length == 0 &&
                        (p.PropertyType == typeof(ModuleDescriptor) || p.PropertyType == typeof(SemanticModule)))
                    .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.GetValue(null)));
            foreach (var value in values)
                if ((value as SemanticModule ?? (value as ModuleDescriptor)?.Semantics) is { } module &&
                    !found.Any(m => ReferenceEquals(m, module)))
                    found.Add(module);
        }
        return found;
    }

    WorkspaceDiagnostic FromGrammar(CompiledDiagnostic compiled)
    {
        var diagnostic = compiled.Diagnostic;
        var (line, column) = _files.TryGetValue(compiled.Path, out string? text) ? LineColumn(text, diagnostic.Span.Start) : (0, 0);
        return new(compiled.Path, line, column, diagnostic.Code, diagnostic.Message, diagnostic.Severity == GrammarSeverity.Error);
    }

    static (int Line, int Column) LineColumn(string text, int offset)
    {
        int line = 1, column = 1;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return (line, column);
    }

    static WorkspaceDiagnostic FromCSharp(CodeAnalysis.Diagnostic diagnostic)
    {
        // Mapped: C# in a semantics block points into its .ngr through #line (issue 239).
        var span = diagnostic.Location.GetMappedLineSpan();
        return new(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            diagnostic.Id, diagnostic.GetMessage(), IsError: true);
    }

    /// <summary>The framework assemblies plus Nitrogen.Runtime, loaded once per process (warm compiles reuse them).</summary>
    static CodeAnalysis.MetadataReference[] LoadReferences()
    {
        string framework = Path.GetDirectoryName(PathOf(typeof(object).Assembly))!;
        string[] trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator) ?? [];
        return trusted
            .Where(p => p.Length > 0 && string.Equals(Path.GetDirectoryName(p), framework, StringComparison.Ordinal))
            .Append(PathOf(typeof(Language).Assembly))
            .Distinct(StringComparer.Ordinal)
            .Select(p => (CodeAnalysis.MetadataReference)CodeAnalysis.MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    /// <summary>
    /// The file Roslyn references an assembly by. A single-file host has none unless it extracts its
    /// assemblies; Nitrogen.Cli does (IncludeAllContentForSelfExtract), and any other host gets this error.
    /// </summary>
    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Checked here: single-file hosts must extract assemblies, as Nitrogen.Cli does, or compiles fail with a clear error.")]
    static string PathOf(Assembly assembly) => assembly.Location is { Length: > 0 } path
        ? path
        : throw new InvalidOperationException(
            $"Assembly '{assembly.GetName().Name}' has no file, so grammars cannot be compiled; a single-file host must set IncludeAllContentForSelfExtract.");
}

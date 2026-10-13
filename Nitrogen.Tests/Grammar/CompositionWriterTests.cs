using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public sealed class CompositionWriterTests
{
    internal static GrammarInput Input(string text) => new("a.ngr", text, "Demo.Syntax");

    [Fact]
    public void Factory_composes_all_selected_modules_once_and_exposes_start()
    {
        var input = Input("syntax module A { syntax Program = B.Word; } syntax module B { token Word = \"ok\"; }");
        var factory = CompositionWriter.Write(new("Demo", "A.Program", "Demo.Syntax", new[] { input, input }));
        Assert.False(factory.HasErrors, string.Join("\n", factory.Diagnostics));
        var grammar = GrammarCompiler.Compile(new[] { input });
        using var compiled = CompositionTestAssembly.Create(grammar.Sources.Concat(factory.Sources));
        var type = compiled.Assembly.GetType("Demo.Syntax.DemoComposition", true)!;
        var first = (LanguageBuilder)type.GetMethod("CreateBuilder")!.Invoke(null, null)!;
        var second = (LanguageBuilder)type.GetMethod("CreateBuilder")!.Invoke(null, null)!;
        Assert.NotSame(first, second);
        var language = first.Build();
        Assert.Equal(new[] { "A", "B" }, language.Modules.Select(m => m.Name));
        var start = (Rule)type.GetProperty("Start")!.GetValue(null)!;
        using var parsed = language.Parse("ok", start);
        Assert.True(parsed.Success);
    }

    [Theory]
    [InlineData("Bad.Name", "A.Program", "syntax module A { syntax Program = \"ok\"; }")]
    [InlineData("Demo", "A.Missing", "syntax module A { syntax Program = \"ok\"; }")]
    [InlineData("Demo", "A.Word", "syntax module A { token Word = \"ok\"; }")]
    [InlineData("Demo", "A.Program", "syntax module A { syntax Program = \"ok\"; syntax Composition = | Demo = \"other\"; }")]
    public void Invalid_composition_does_not_emit_a_factory(string name, string start, string grammar)
    {
        var result = CompositionWriter.Write(new(name, start, "Demo.Syntax", new[] { Input(grammar) }));
        Assert.True(result.HasErrors);
        Assert.Empty(result.Sources);
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal("a.ngr", diagnostic.Path));
    }
}

internal sealed class CompositionTestAssembly : IDisposable
{
    readonly AssemblyLoadContext _context = new("composition-test", isCollectible: true);
    public Assembly Assembly { get; private set; } = null!;
    public static CompositionTestAssembly Create(IEnumerable<GeneratedSource> sources, string helper = "")
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Code, path: s.HintName))
            .Append(CSharpSyntaxTree.ParseText(helper));
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Language).Assembly.Location).Distinct();
        var compilation = CSharpCompilation.Create("Composition" + Guid.NewGuid().ToString("N"), trees,
            paths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        stream.Position = 0;
        var result = new CompositionTestAssembly();
        result.Assembly = result._context.LoadFromStream(stream);
        return result;
    }
    public void Dispose() => _context.Unload();
}

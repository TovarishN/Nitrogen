using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;
namespace Nitrogen.Tests;
public sealed class GrammarContextEditorTests : IDisposable
{
    const string Source="syntax module A { context Flag; context Other; syntax Root = with(A.Flag = true) context(Flag) \"x\"; }";
    readonly string root=Directory.CreateTempSubdirectory("nitrogen-context-editor-").FullName;
    string Uri=>new Uri(Path.Combine(root,"context.ngr")).AbsoluteUri;
    NitrogenLanguageService Open(string source=Source)
    {File.WriteAllText(Path.Combine(root,"context.ngr"),source);var service=new NitrogenLanguageService(LspCommand.Registry());service.Open(Uri,1,source);return service;}
    static DocumentPosition At(string source,string value,int shift=0)=>new LineMap(source).PositionOf(source.IndexOf(value,StringComparison.Ordinal)+shift);
    [Fact]public void Qualified_and_local_context_uses_share_definitions_references_hover_and_rename()
    {
        using var service=Open();var at=At(Source,"A.Flag",3);
        var definition=Assert.Single(service.Definition(Uri,at));Assert.Equal(At(Source,"context Flag",8),definition.Range.Start);
        Assert.Equal(3,service.References(Uri,at,true).Count);Assert.Contains("Boolean grammar context",service.Hover(Uri,at)?.Markdown);
        var edits=service.Rename(Uri,at,"Ready");Assert.Equal(3,edits[Uri].Count);Assert.All(edits[Uri],edit=>Assert.Equal("Ready",edit.NewText));
        Assert.Throws<RenameRefusedException>(()=>service.Rename(Uri,at,"Other"));
    }
    [Theory][InlineData("Fl")][InlineData("A.Fl")]public void Completion_offers_contexts_at_a_flag_argument(string prefix)
    {
        string source="syntax module A { context Flag; syntax Root = context("+prefix+") \"x\"; }";
        using var service=Open(source);var items=service.Completion(Uri,At(source,"context("+prefix+")",8+prefix.Length));Assert.Contains(items,i=>i.Label=="Flag");
    }
    [Theory][InlineData("\"context(Fl)\"")][InlineData("\"x\"; // context(Fl)\n syntax Tail = \"y\"")]
    public void Context_completion_does_not_treat_opaque_text_as_a_flag_argument(string body)
    {
        string source="syntax module A { context Flag; syntax Root = "+body+"; }";
        using var service=Open(source);Assert.DoesNotContain(service.Completion(Uri,At(source,"context(Fl)",10)),item=>item.Label=="Flag");
    }
    [Fact]public void Context_rename_refuses_to_capture_an_imported_unqualified_use()
    {
        const string source="syntax module A { context Flag; } syntax module B { using A; context Ready; syntax Root = context(Flag) \"x\"; }";
        using var service=Open(source);Assert.Throws<RenameRefusedException>(()=>service.Rename(Uri,At(source,"context Flag",8),"Ready"));
    }
    public void Dispose()=>Directory.Delete(root,true);
}

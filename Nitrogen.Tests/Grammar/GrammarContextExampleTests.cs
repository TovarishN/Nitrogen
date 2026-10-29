using System.Runtime.CompilerServices;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Workspace;
using Xunit;
namespace Nitrogen.Tests;
public class GrammarContextExampleTests
{
    static string Root([CallerFilePath]string source="")=>Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,"..","..","examples","ScopedWords"));
    [Fact]public void Standalone_workspace_compiles_the_actual_grammar_and_retains_distinct_owners()
    {
        string root=Root();var workspace=new GrammarWorkspace();workspace.SetGrammar(Path.Combine(root,"ScopedWords.ngr"),File.ReadAllText(Path.Combine(root,"ScopedWords.ngr")));
        using var snapshot=workspace.Compile();Assert.True(snapshot.Succeeded,string.Join(";",snapshot.Diagnostics));
        using var p=snapshot.Parse(File.ReadAllText(Path.Combine(root,"sample.scopedwords")),"ScopedWords.Document");Assert.True(p.Success&&!p.HasErrors);
        string tree=SyntaxDumper.Dump(p.Tree);Assert.Contains("LoudWord",tree);Assert.Contains("QuietWord",tree);
    }
    [Fact]public void Configured_language_loads_with_clean_sample_diagnostics_and_keyword_navigation()
    {
        string root=Root(),sample=File.ReadAllText(Path.Combine(root,"sample.scopedwords")),uri=new Uri(Path.Combine(root,"sample.scopedwords")).AbsoluteUri;
        using var service=new NitrogenLanguageService(LspCommand.Registry());service.ConfigureWorkspace(root);service.Open(uri,1,sample);Assert.Empty(service.Diagnostics(uri));
        var target=Assert.Single(service.Definition(uri,new LineMap(sample).PositionOf(sample.IndexOf("bang",StringComparison.Ordinal)+1)));Assert.EndsWith("ScopedWords.ngr",new Uri(target.Uri).LocalPath);
    }
}

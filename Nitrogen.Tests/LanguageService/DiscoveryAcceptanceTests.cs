using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DiscoveryAcceptanceTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-discovery-acceptance-").FullName;
    static string Grammar(string name = "Calc") => "#!nitrogen grammar\nlanguage " + name + " { start M.R; } syntax module M { syntax R = \"ok\"; }";
    string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root,name)).FullName;
    string UriOf(string path) => new Uri(path).AbsoluteUri;
    public void Dispose() => Directory.Delete(_root,true);

    [Fact]
    public void Duplicate_names_across_workspace_roots_are_ambiguous_and_removing_a_root_resolves_them()
    {
        string one = Folder("one"), two = Folder("two");
        File.WriteAllText(Path.Combine(one,"A.syntax"),Grammar());
        File.WriteAllText(Path.Combine(two,"B.syntax"),Grammar());
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.SetSignatureRoots([one,two]);
        service.ConfigureWorkspace(one);
        string uri = UriOf(Path.Combine(one,"sample.txt"));
        service.Open(uri,1,"#!nitrogen language Calc\nok");
        var diagnostic = Assert.Single(service.Diagnostics(uri),d => d.Message.Contains("ambiguous",StringComparison.Ordinal));
        Assert.Contains(Path.Combine(one,"A.syntax"),diagnostic.Message);
        Assert.Contains(Path.Combine(two,"B.syntax"),diagnostic.Message);
        service.SetSignatureRoots([one]);
        Assert.Empty(service.Diagnostics(uri));
    }

    [Fact]
    public void Removing_the_last_workspace_root_detaches_discovered_languages()
    {
        File.WriteAllText(Path.Combine(_root,"A.syntax"),Grammar());
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.SetSignatureRoots([_root]);
        service.ConfigureWorkspace(_root);
        string uri = UriOf(Path.Combine(_root,"sample.txt"));
        service.Open(uri,1,"#!nitrogen language Calc\nok");
        Assert.Empty(service.Diagnostics(uri));
        service.SetSignatureRoots([]);
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "NGR0402");
    }

    [Fact]
    public void Configuration_in_second_workspace_root_checks_closed_native_files()
    {
        string one = Folder("one"), two = Folder("two");
        File.WriteAllText(Path.Combine(two,"Rules.syntax"),Grammar("Rules"));
        File.WriteAllText(Path.Combine(two,"nitrogen.json"),"{\"augmentations\":[{\"language\":\"Rules\",\"files\":[\"*.cs\"]}]}");
        string host = Path.Combine(two,"Host.cs"); File.WriteAllText(host,"class C {}");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.SetSignatureRoots([one,two]); service.ConfigureWorkspace(one);
        Assert.Contains(service.Diagnostics(UriOf(host)),d => d.Source == "nitrogen:Rules");
    }

    [Fact]
    public void Malformed_augmentation_configuration_is_reported_without_a_signed_document()
    {
        string config = Path.Combine(_root,"nitrogen.json");
        File.WriteAllText(config,"{\"augmentations\":[{\"files\":[\"*.cs\"]}]}");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        Assert.Contains(service.Diagnostics(UriOf(config)),d => d.Code == "NGR0403");
    }

    [Fact]
    public void Deleting_an_explicit_definition_detaches_instead_of_using_a_deleted_language()
    {
        string grammar = Path.Combine(_root,"Calc.syntax");File.WriteAllText(grammar,Grammar());
        using var service = new NitrogenLanguageService(LspCommand.Registry());service.ConfigureWorkspace(_root);
        string uri=UriOf(Path.Combine(_root,"sample.txt"));service.Open(uri,1,"#!nitrogen language \"./Calc.syntax\"\nbad");
        Assert.Contains(service.Diagnostics(uri),d => d.Source == "nitrogen");
        File.Delete(grammar);service.FileChanged(grammar);
        Assert.Contains(service.Diagnostics(uri),d => d.Code == "NGR0402");
        Assert.DoesNotContain(service.Diagnostics(uri),d => d.Code == "NGR0405");
    }
    [Fact]
    public async Task Lsp_folder_notifications_publish_ambiguity_then_clear_it()
    {
        string one = Folder("one"), two = Folder("two");
        File.WriteAllText(Path.Combine(one,"A.syntax"),Grammar());
        File.WriteAllText(Path.Combine(two,"B.syntax"),Grammar());
        string uri = UriOf(Path.Combine(one,"sample.txt"));
        string folder = UriOf(two);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        string initialize = JsonSerializer.Serialize(new { jsonrpc="2.0", id=1, method="initialize", @params=new { workspaceFolders = new[] { new { uri=UriOf(one),name="one" } }, capabilities=new {} } });
        string open = JsonSerializer.Serialize(new { jsonrpc="2.0", method="textDocument/didOpen", @params=new { textDocument=new { uri,languageId="nitrogen-signature",version=1,text="#!nitrogen language Calc\nok" } } });
        string FolderChange(bool add) => JsonSerializer.Serialize(new { jsonrpc="2.0", method="workspace/didChangeWorkspaceFolders", @params=new { @event=new { added=add ? new[] {new {uri=folder,name="two"}} : [], removed=add ? [] : new[] {new {uri=folder,name="two"}} } } });
        var (_,messages,log)=await LspServerTests.Session(service,initialize,
            """{"jsonrpc":"2.0","method":"initialized","params":{}}""",open,FolderChange(true),FolderChange(false),
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""",
            """{"jsonrpc":"2.0","method":"exit"}""");
        Assert.Empty(log);
        Assert.True(messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("workspace").GetProperty("workspaceFolders").GetProperty("changeNotifications").GetBoolean());
        var publishes=messages.Where(m=>m.TryGetProperty("method",out var method)&&method.GetString()=="textDocument/publishDiagnostics")
            .Select(m=>m.GetProperty("params")).Where(p=>p.GetProperty("uri").GetString()==uri).ToArray();
        Assert.Contains(publishes,p=>p.GetProperty("diagnostics").EnumerateArray().Any(d=>d.GetProperty("message").GetString()!.Contains("ambiguous",StringComparison.Ordinal)));
        Assert.Empty(publishes.Last().GetProperty("diagnostics").EnumerateArray());
    }

}

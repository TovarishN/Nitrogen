using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Formatting: indentation and the edges of lines, from the syntax tree.</summary>
public class FormattingTests
{
    const string Uri = "file:///w/a.ngr";
    static readonly FormattingOptions TwoSpaces = new(2, true);

    /// <summary>The document's text after its formatting edits.</summary>
    static string Formatted(string text, FormattingOptions? options = null)
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, text);
        return Apply(text, service.Format(Uri, options ?? TwoSpaces));
    }

    internal static string Apply(string text, IReadOnlyList<DocumentEdit> edits)
    {
        var lines = new LineMap(text);
        foreach (var edit in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
        {
            int start = lines.OffsetOf(edit.Range.Start), end = lines.OffsetOf(edit.Range.End);
            text = text[..start] + edit.NewText + text[end..];
        }
        return text;
    }

    [Fact]
    public void Block_contents_indent_one_level_and_alignment_within_a_line_stays()
    {
        Assert.Equal("""
            syntax module M
            {
              token Word = ['a'..'z']+;
              syntax A   = "a"  Name:Word;
            }

            """, Formatted("""
            syntax module M
            {
            token Word = ['a'..'z']+;
                  syntax A   = "a"  Name:Word;
            }

            """));
    }

    [Fact]
    public void Nested_blocks_indent_from_the_line_holding_their_brace()
    {
        Assert.Equal("""
            syntax module M
            {
              token Word = ['a'..'z']+;
              extensible syntax E
              {
                | W = Word
              }
            }

            """, Formatted("""
            syntax module M
            {
            token Word = ['a'..'z']+;
            extensible syntax E
            {
            | W = Word
            }
            }

            """));
    }

    [Fact]
    public void A_continuation_keeps_its_offset_from_its_item()
    {
        // The semantics block one level in (DateCalc.ngr's style), and a hand-aligned continuation.
        Assert.Equal("""
            syntax module M
            {
              token Word = ['a'..'z']+;
              syntax A = Name:Word
                {
                  out Size : int = 0;
                }
              syntax B = "b"
                         Name:Word;
            }

            """, Formatted("""
            syntax module M
            {
            token Word = ['a'..'z']+;
            syntax A = Name:Word
              {
            out Size : int = 0;
                  }
            syntax B = "b"
                       Name:Word;
            }

            """));
    }

    [Fact]
    public void Comment_runs_move_as_a_block_to_the_next_code_line()
    {
        Assert.Equal("""
            // header
            syntax module M
            {
              // inside
              //   indented more
              token Word = ['a'..'z']+;
              // before the closer
            }
            // after

            """, Formatted("""
              // header
            syntax module M
            {
            // inside
            //   indented more
            token Word = ['a'..'z']+;
                 // before the closer
            }
               // after

            """));
    }

    [Fact]
    public void Trailing_whitespace_blank_lines_and_the_end_of_the_file()
    {
        Assert.Equal("syntax module M\n{\n\n  token Word = ['a'..'z']+;\n}\n",
            Formatted("\n\nsyntax module M   \n{\n\n\n  token Word = ['a'..'z']+;  \n}"));
        Assert.Equal("syntax module M\n{\n  token Word = ['a'..'z']+;\n}\n",
            Formatted("syntax module M\n{\n  token Word = ['a'..'z']+;\n}\n\n\n"));
        Assert.Equal("syntax module M\r\n{\r\n  token Word = ['a'..'z']+;\r\n}\r\n",
            Formatted("syntax module M\r\n{\r\n  token Word = ['a'..'z']+;\r\n}\r\n\r\n"));
    }

    [Fact]
    public void Tabs_and_a_tab_size_come_from_the_options()
    {
        const string text = "syntax module M\n{\ntoken Word = ['a'..'z']+;\n}\n";
        Assert.Equal("syntax module M\n{\n\ttoken Word = ['a'..'z']+;\n}\n", Formatted(text, new FormattingOptions(4, false)));
        Assert.Equal("syntax module M\n{\n    token Word = ['a'..'z']+;\n}\n", Formatted(text, new FormattingOptions(4, true)));
    }

    [Fact]
    public void Edits_are_per_line()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, "syntax module M\n{\ntoken Word = ['a'..'z']+;\ntoken Other = ['b'..'c']+;\n}\n");
        var edits = service.Format(Uri, TwoSpaces);
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal(e.Range.Start.Line, e.Range.End.Line));
    }

    [Fact]
    public void A_formatted_document_gets_no_edits()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, "syntax module M\n{\n  token Word = ['a'..'z']+;\n}\n");
        Assert.Empty(service.Format(Uri, TwoSpaces));
    }

    [Fact]
    public void A_document_with_a_syntax_error_gets_no_edits()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, "syntax module M\n{\ntoken = ;\n}\n");
        Assert.Empty(service.Format(Uri, TwoSpaces));
    }

    [Fact]
    public void A_range_formats_only_its_lines()
    {
        const string text = "syntax module M\n{\ntoken Word = ['a'..'z']+;\ntoken Other = ['b'..'c']+;\n}\n";
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, text);
        var edits = service.FormatRange(Uri, new DocumentRange(new DocumentPosition(3, 0), new DocumentPosition(3, 5)), TwoSpaces);
        Assert.Equal("syntax module M\n{\ntoken Word = ['a'..'z']+;\n  token Other = ['b'..'c']+;\n}\n", Apply(text, edits));
    }

    [Fact]
    public void Typing_a_closing_brace_reindents_its_line_and_other_characters_do_nothing()
    {
        const string text = "syntax module M\n{\n  token Word = ['a'..'z']+;\n    }\n";
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(Uri, 1, text);
        Assert.Equal("syntax module M\n{\n  token Word = ['a'..'z']+;\n}\n",
            Apply(text, service.FormatOnType(Uri, new DocumentPosition(3, 5), "}", TwoSpaces)));
        Assert.Empty(service.FormatOnType(Uri, new DocumentPosition(2, 27), ";", TwoSpaces));
    }

    [Fact]
    public void A_csharp_file_is_left_to_csharp()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open("file:///w/C.cs", 1, "class C\n{\nint x;\n}\n");
        Assert.Empty(service.Format("file:///w/C.cs", TwoSpaces));
    }

    static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Theory]
    [InlineData("Nitrogen.Ngr/Nitrogen.ngr")]
    [InlineData("examples/DateCalc/DateCalc.ngr")]
    [InlineData("Nitrogen.Tests/Grammars/Calc.ngr")]
    [InlineData("Nitrogen.Geometry/Geometry.ngr")]
    public void The_repository_grammars_are_already_formatted(string relative)
    {
        string text = File.ReadAllText(Path.Combine(RepositoryRoot(), relative));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open("file:///w/" + Path.GetFileName(relative), 1, text);
        var edits = service.Format("file:///w/" + Path.GetFileName(relative), TwoSpaces);
        Assert.True(edits.Count == 0, string.Join("\n", edits.Select(e => $"line {e.Range.Start.Line + 1}: '{e.NewText}'")));

        // Not vacuous: the file parses, and with every line shifted one space right, formatting gives it back.
        string shifted = string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? line : " " + line));
        service.Change("file:///w/" + Path.GetFileName(relative), 2, shifted);
        Assert.Equal(text, Apply(shifted, service.Format("file:///w/" + Path.GetFileName(relative), TwoSpaces)));
    }

    [Fact]
    public void The_datecalc_sample_is_already_formatted()
    {
        string root = Directory.CreateTempSubdirectory("nitrogen-format-").FullName;
        try
        {
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
                File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            using var service = new NitrogenLanguageService(LspCommand.Registry());
            service.ConfigureWorkspace(root);
            string uri = new System.Uri(Path.Combine(root, "sample.datecalc")).AbsoluteUri;
            string text = File.ReadAllText(Path.Combine(root, "sample.datecalc"));
            service.Open(uri, 1, text);
            Assert.Empty(service.Format(uri, TwoSpaces));

            string shifted = string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? line : " " + line));
            service.Change(uri, 2, shifted);
            Assert.Equal(text, Apply(shifted, service.Format(uri, TwoSpaces)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_server_answers_formatting_requests()
    {
        const string text = "syntax module M\n{\ntoken Word = ['a'..'z']+;\n    }\n";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri
            + "\",\"languageId\":\"ngr\",\"version\":1,\"text\":" + JsonSerializer.Serialize(text) + "}}}";
        const string options = "\"options\":{\"tabSize\":2,\"insertSpaces\":true}";
        string format = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/formatting\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri + "\"}," + options + "}}";
        string range = "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"textDocument/rangeFormatting\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri
            + "\"},\"range\":{\"start\":{\"line\":2,\"character\":0},\"end\":{\"line\":2,\"character\":3}}," + options + "}}";
        string onType = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"textDocument/onTypeFormatting\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri
            + "\"},\"position\":{\"line\":3,\"character\":5},\"ch\":\"}\"," + options + "}}";
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        var (_, messages, _) = await LspServerTests.Session(service,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""", open, format, range, onType,
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""", """{"jsonrpc":"2.0","method":"exit"}""");

        var capabilities = messages[0].GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("documentFormattingProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("documentRangeFormattingProvider").GetBoolean());
        Assert.Equal("}", capabilities.GetProperty("documentOnTypeFormattingProvider").GetProperty("firstTriggerCharacter").GetString());
        JsonElement Result(int id) => messages.Single(m => m.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id).GetProperty("result");
        Assert.Equal(2, Result(5).GetArrayLength());   // the token line and the closer
        Assert.Equal(1, Result(6).GetArrayLength());   // the token line
        var closer = Assert.Single(Result(7).EnumerateArray());
        Assert.Equal("", closer.GetProperty("newText").GetString());
        Assert.Equal(3, closer.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }
}

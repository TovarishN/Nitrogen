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
}

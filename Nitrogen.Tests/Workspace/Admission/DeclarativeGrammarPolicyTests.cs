using Nitrogen.Grammar;
using Nitrogen.Workspace.Admission;
using Xunit;

namespace Nitrogen.Tests.Workspace.Admission;

public sealed class DeclarativeGrammarPolicyTests
{
    static ModulePackage Package(string source) => new("test", "host", "Rules", "Rules.Doc",
        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["rules.ngr"] = source },
        [new ModuleExample("sample", "sample.txt", "x", [], "x")], [], "hash");

    [Fact]
    public void Syntax_tokens_binding_and_grammar_imports_are_allowed()
    {
        const string grammar = """
            syntax module Rules
            {
              using Other;
              symbols { thing }
              token Word = ['a'..'z']+;
              syntax Doc = Name:Word declares thing Name export;
            }
            """;
        Assert.Empty(DeclarativeGrammarPolicy.Validate(Package(grammar)));
    }

    [Theory]
    [InlineData("syntax Doc = \"x\" { out Value : int = 0; Value = 1; }")]
    [InlineData("extensible syntax Expr { | Lit = \"x\" { out Value : int = 0; Value = 1; } }")]
    [InlineData("extensible syntax Expr { out Value : int = 0; | Lit = \"x\" }")]
    [InlineData("symbol property Type for item : string = \"value\"; syntax Doc = \"x\";")]
    public void Authored_csharp_surfaces_are_rejected(string body)
    {
        var source = "syntax module Rules\n{\n  symbols { item }\n  " + body + "\n}";
        var diagnostic = Assert.Single(DeclarativeGrammarPolicy.Validate(Package(source)));
        Assert.Equal("NA0002", diagnostic.Code);
        Assert.Equal("rules.ngr", diagnostic.Path);
        Assert.Equal(4, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
    }

    [Fact]
    public void Extension_alternative_semantics_are_rejected()
    {
        const string grammar = """
            syntax module Rules
            {
              using Base;
              extend syntax Base.Expr
              {
                | Extra = "x" { Value = 1; }
              }
            }
            """;
        var diagnostic = Assert.Single(DeclarativeGrammarPolicy.Validate(Package(grammar)));
        Assert.Equal("NA0002", diagnostic.Code);
        Assert.Equal(6, diagnostic.Line);
    }

    [Fact]
    public void Grammar_syntax_error_is_source_linked_before_compilation()
    {
        const string source = "syntax module Rules\n{\n  syntax Doc = ;\n}";
        var diagnostic = Assert.Single(DeclarativeGrammarPolicy.Validate(Package(source)));
        Assert.Equal(GrammarCodes.Syntax, diagnostic.Code);
        Assert.Equal("rules.ngr", diagnostic.Path);
        Assert.Equal(3, diagnostic.Line);
    }

    [Fact]
    public void Declarative_typing_and_lowering_clauses_are_allowed()
    {
        const string grammar = """
            syntax module Rules
            {
              symbols { thing }
              token Word = ['a'..'z']+;
              token Digits = ['0'..'9']+;
              syntax Doc = "use" Name:Word ":" Kind:Word declares thing Name type Kind;
              syntax Num = Text:Digits lowers literal Core.Scalar Text;
              syntax Pair = "pair" Left:Num Right:Num lowers Rules.Pair(Left, Right);
            }
            """;
        Assert.Empty(DeclarativeGrammarPolicy.Validate(Package(grammar)));
    }
}

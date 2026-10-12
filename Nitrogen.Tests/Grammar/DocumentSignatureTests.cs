using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DocumentSignatureTests
{
    [Theory]
    [InlineData("#!nitrogen grammar\n", SignatureMode.Grammar)]
    [InlineData("// #!nitrogen language \"./Calc.syntax\"\r\n", SignatureMode.Language)]
    [InlineData("/* #!nitrogen augment \"./Rules.syntax\" */", SignatureMode.Augment)]
    [InlineData("<!-- #!nitrogen augment \"./Rules.syntax\" regions -->", SignatureMode.Augment)]
    public void Recognizes_supported_wrappers(string header, SignatureMode mode)
    {
        var result = DocumentSignatures.Read(header);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(mode, Assert.Single(result.Signatures).Mode);
        Assert.Equal(header.Length, result.MaskedText.Length);
        Assert.DoesNotContain("#!nitrogen", result.MaskedText);
    }

    [Fact]
    public void Wrapped_header_keeps_offsets_and_line_endings()
    {
        const string text = "\uFEFF\r\n// doc 😀\r\n// #!nitrogen augment \"./Rules.syntax\"\r\nclass C {}";
        var result = DocumentSignatures.Read(text);
        Assert.Empty(result.Diagnostics);
        var signature = Assert.Single(result.Signatures);
        Assert.Equal(SignatureMode.Augment, signature.Mode);
        Assert.Equal(new LanguageReference("./Rules.syntax", true), signature.Reference);
        Assert.Equal(AnalysisInputMode.Document, signature.Input);
        Assert.Equal(text.Length, result.MaskedText.Length);
        int body = text.IndexOf("class", StringComparison.Ordinal);
        Assert.Equal(text.Substring(body), result.MaskedText.Substring(body));
        Assert.Equal(text.Select((c, i) => (c, i)).Where(x => x.c is '\r' or '\n'),
            result.MaskedText.Select((c, i) => (c, i)).Where(x => x.c is '\r' or '\n'));
        Assert.Equal(text.IndexOf("#!nitrogen", StringComparison.Ordinal), signature.Start);
        Assert.Contains("// doc 😀", result.MaskedText);
    }

    [Theory]
    [InlineData("var s = \"#!nitrogen language Calc\";")]
    [InlineData("var s = \"\"\"\n// #!nitrogen augment Calc\n\"\"\";")]
    [InlineData("code\n#!nitrogen language Calc")]
    [InlineData("// documentation mentions #!nitrogen language Calc\ncode")]
    [InlineData("// #!nitrogenous grammar\ncode")]
    [InlineData("/* #!nitrogen region \"./Calc.syntax\"\n1\n#!nitrogen end\n*/")]
    public void Ignores_strings_non_headers_and_region_markers(string text)
    {
        var result = DocumentSignatures.Read(text);
        Assert.Empty(result.Signatures);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(text, result.MaskedText);
    }

    [Theory]
    [InlineData("#!nitrogen")]
    [InlineData("#!nitrogen wat")]
    [InlineData("#!nitrogen grammar extra")]
    [InlineData("#!nitrogen language")]
    [InlineData("#!nitrogen language \"\"")]
    [InlineData("#!nitrogen language \"./unterminated")]
    [InlineData("#!nitrogen language Calc regions")]
    [InlineData("#!nitrogen augment Calc unknown")]
    [InlineData("#!nitrogen augment ./unquoted.syntax")]
    [InlineData("/* #!nitrogen augment Calc")]
    public void Malformed_recognized_headers_have_local_diagnostics(string text)
    {
        var result = DocumentSignatures.Read(text);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.Equal(GrammarSeverity.Error, d.Severity);
            Assert.InRange(d.Span.Start, 0, text.Length);
            Assert.InRange(d.Span.End, d.Span.Start, text.Length);
        });
    }

    [Fact]
    public void Multiple_augmentations_keep_input_modes_and_references()
    {
        var result = DocumentSignatures.Read("""
            // doc
            /*
            #!nitrogen augment "./one.syntax"
            #!nitrogen augment Rules regions
            */
            class C {}
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Signatures.Count);
        Assert.Equal(new LanguageReference("Rules", false), result.Signatures[1].Reference);
        Assert.Equal(AnalysisInputMode.Regions, result.Signatures[1].Input);
        Assert.EndsWith("class C {}", result.MaskedText);
    }

    [Theory]
    [InlineData("#!nitrogen grammar\n#!nitrogen grammar")]
    [InlineData("#!nitrogen language Calc\n#!nitrogen language Other")]
    [InlineData("#!nitrogen grammar\n#!nitrogen augment Rules")]
    public void Conflicting_modes_are_diagnosed(string text) =>
        Assert.NotEmpty(DocumentSignatures.Read(text).Diagnostics);

    [Fact]
    public void Primary_language_can_add_augmentation()
    {
        var result = DocumentSignatures.Read("#!nitrogen language Calc\n#!nitrogen augment Rules");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Signatures.Count);
    }

    [Fact]
    public void Quoted_reference_decodes_escaped_quotes_and_backslashes()
    {
        var result = DocumentSignatures.Read("#!nitrogen language \"./a\\\"b\\\\c.syntax\"");
        Assert.Empty(result.Diagnostics);
        Assert.Equal("./a\"b\\c.syntax", Assert.Single(result.Signatures).Reference!.Value);
    }
}

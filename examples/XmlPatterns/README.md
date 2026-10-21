# Match an XML tree and report a warning in the grammar

Run from the repository root:

```sh
dotnet run --project Nitrogen.Cli -- check examples/XmlPatterns/sample.xml
```

`sample.xml` contains an ordinary XML configuration with nested services, attributes, comments, text and self-closing elements. `Xml.ngr` defines both the XML syntax and the warning rule, inside the `Paired` syntax rule's semantics block:

```ngr
match (Attrs: Attribute*, PasswordAttrs: Attribute*, Content: Node*)
  <[ <credentials ..$Attrs><password ..$PasswordAttrs>..$Content</password></credentials> ]>
  => warning XML1001 : "A password is stored directly inside credentials; use a secret-ref instead.";
```

The pattern matches the current syntax node. `..$Attrs`, `..$PasswordAttrs` and `..$Content` capture typed lists; they are pattern syntax, not input XML. Attributes and password contents can vary. Literal tag names, nesting and child order must match. The password must be the credentials element's only child apart from whitespace. Every paired element is checked automatically, so surrounding structure can vary. Matching does not rewrite the tree or inspect raw XML text.

The sample produces one diagnostic:

```text
sample.xml(6,5): warning XML1001: A password is stored directly inside credentials; use a secret-ref instead.
```

The secret reference and the password outside credentials do not match. Warnings leave the CLI exit code at zero. Malformed syntax and mismatched closing names are errors. Change `warning` to `error` to make the matched structure fail validation.

Open this folder or the Nitrogen repository root with the Nitrogen editor extension. Both folders have a `nitrogen.json` that registers `.xml` and `Xml.Document`; configuration paths are relative to that file. The same grammar-authored warning appears through the standard language service; there is no custom XML analyzer or helper source. `XmlPatterns.csproj` provides a generated-parser build fixture for the solution and tests.

This is a teaching XML subset: no DTD, CDATA, general processing instructions, entity validation/decoding or namespace resolution. Nitrogen's default whitespace trivia is ignored between syntax nodes; this is not a lossless XML editor. Quoted attribute spelling remains significant in literal patterns.

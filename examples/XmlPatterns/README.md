# Match an XML tree and report a warning in the grammar

Run from the repository root:

```sh
dotnet run --project Nitrogen.Cli -- check examples/XmlPatterns/sample.xml
```

`sample.xml` contains an ordinary XML configuration with nested services, attributes, comments, text and self-closing elements. `Xml.ngr` defines both the XML syntax and the warning rule, inside the `Paired` syntax rule's semantics block:

```ngr
match (pass: Text)
  <[ <credentials ..$_>..$_<password ..$_>$pass</password>..$_</credentials> ]>
  => warning XML1001 :
    $"A password is stored directly inside credentials; use a secret-ref instead. {pass}";
```

The pattern matches the current syntax node. Each `..$_` ignores zero or more items of the category
required by its grammar position: attributes or child nodes. These are independent typed wildcards,
not bindings or input XML. Literal tag names and nesting must match; a paired password can occur
anywhere among the credentials element's direct children. Every paired element is checked
automatically, so surrounding structure can vary. Matching does not rewrite the tree or inspect raw
XML text.

`pass` is a scalar syntax view. Its `.Text` property can be used explicitly in the message:

```ngr
match (pass: Text)
  <[ <credentials ..$_>..$_<password ..$_>$pass</password>..$_</credentials> ]>
  => warning XML1001 :
    $"A password is stored directly inside credentials; use a secret-ref instead. {pass.Text}";
```

This variant requires exactly one text node inside password. `pass.Text` is its source text;
`{pass}` uses the same view's text conversion. For empty or mixed contents, use `(content: Node*)`
and `..$content`; the message can access `content.Count` and enumerate its typed syntax views.
Each capture is visible only in its own message, and messages are skipped on mismatch. This scalar
example does not report empty passwords or passwords containing nested elements; those require a
list pattern such as `<[ <credentials ..$_>..$_<password ..$_>..$content</password>..$_</credentials> ]>`
with `(content: Node*)`.

The grammar also declares a named pattern for secrets and uses it in both directions:

```ngr
pattern Secret : Element =
  | <[ <password ..$_>..$_</password> ]>
  | <[ <api-key ..$_>..$_</api-key> ]>;

match (secret: ..Secret)
  <[ <vault ..$_>..$_ $secret ..$_</vault> ]>
  => warning XML1002 : $"A vault stores a plaintext secret: {secret}";

match <[ <api-key ..$_>..$_</api-key> ]>
  inside <[ <service ..$_>..$_</service> ]>
  => warning XML1003 : "An API key is embedded in a service definition; reference it instead.";
```

`..Secret` searches beneath the vault's children at any depth and reports once, on the vault.
`inside` reports each API key on its own, wherever it sits within a service. See
[Named patterns and tree context](../../docs/language-guide.md#named-patterns-and-tree-context).

The sample produces four diagnostics:

```text
sample.xml(6,5): warning XML1001: A password is stored directly inside credentials; use a secret-ref instead. example-only-password
sample.xml(16,15): warning XML1003: An API key is embedded in a service definition; reference it instead.
sample.xml(17,5): warning XML1003: An API key is embedded in a service definition; reference it instead.
sample.xml(19,3): warning XML1002: A vault stores a plaintext secret: <password>nested-example</password>
```

The secret reference and the password outside credentials do not match. Warnings leave the CLI exit code at zero. Malformed syntax and mismatched closing names are errors. Change `warning` to `error` to make the matched structure fail validation.

Open this folder or the Nitrogen repository root with the Nitrogen editor extension. Both folders have a `nitrogen.json` that registers `.xml` and `Xml.Document`; configuration paths are relative to that file. The same grammar-authored warning appears through the standard language service; there is no custom XML analyzer or helper source. `XmlPatterns.csproj` provides a generated-parser build fixture for the solution and tests.

This is a teaching XML subset: no DTD, CDATA, general processing instructions, entity validation/decoding or namespace resolution. Nitrogen's default whitespace trivia is ignored between syntax nodes; this is not a lossless XML editor. Quoted attribute spelling remains significant in literal patterns.

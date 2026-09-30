using System.Text;

namespace Nitrogen.Grammar;

/// <summary>
/// Emits a module's semantics (issue 239):
/// <list type="bullet">
/// <item>property and symbol-property keys and the <c>GetSemantics</c> table, inside the module class;</item>
/// <item>after the views, a semantics struct per extensible rule with properties and per rule or
/// alternative with a block. Its members are the reserved ones, the properties and the children, and
/// the block's C# becomes methods mapped to the .ngr by #line;</item>
/// <item>the module's symbol view.</item>
/// </list>
/// A module without semantics emits nothing, so its generated code is unchanged.
/// </summary>
internal sealed class SemanticsWriter
{
    const string Ns = "global::Nitrogen.Semantics.";
    static readonly string[] Reserved = { "Semantics", "Node", "Span", "Text", "IsMissing", "Parent", "Symbol" };
    static readonly string[] SymbolReserved = { "Symbol", "Name", "Kind", "IsBuiltin" };

    readonly EmitModel _model;
    readonly ModuleInfo _info;
    int[]? _lineStarts;

    public SemanticsWriter(EmitModel model, ModuleInfo info)
    {
        _model = model;
        _info = info;
    }

    enum Shape
    {
        Plain,
        Optional,
        List,
        SeparatedList,
    }

    /// <param name="Index">The element's index among the node's children.</param>
    /// <param name="Inner">The item's index inside a group element; -1 for none.</param>
    /// <param name="ItemType">The struct of the rule the child names, or SemanticNode.</param>
    sealed record Child(string Name, int Index, int Inner, Shape Shape, string ItemType, RuleDecl? Rule, ModuleInfo? Owner)
    {
        public string Type => Shape switch
        {
            Shape.Optional => ItemType + "?",
            Shape.List or Shape.SeparatedList => $"{Ns}SemanticList<{ItemType}>",
            _ => ItemType,
        };
    }

    public static bool HasSemantics(EmitModel model, ModuleInfo info) =>
        info.Module.SymbolProperties.Count > 0
        || info.Kinds.Any(k => Block(k) is not null)
        || model.Analysis.PrimaryRules(info.Module).OfType<ExtensibleRule>().Any(e => e.Properties.Count > 0);

    /// <summary>The properties a rule declares: a syntax rule in its block, an extensible rule in its braces.</summary>
    public static IReadOnlyList<PropertyDecl> PropertiesOf(RuleDecl? rule)
    {
        if (rule is SyntaxRule { Semantics: { } block }) return block.Properties;
        if (rule is ExtensibleRule extensible) return extensible.Properties;
        return Array.Empty<PropertyDecl>();
    }

    static SemanticsBlock? Block(KindInfo kind) => kind.Alternative is { } alternative ? alternative.Semantics : (kind.Rule as SyntaxRule)?.Semantics;

    static IReadOnlyList<PropertyDecl> Own(KindInfo kind) => kind.Point is { } point ? PropertiesOf(point.Rule) : PropertiesOf(kind.Rule);

    (ModuleInfo Owner, string Rule) OwnerOf(KindInfo kind) =>
        kind.Point is { } point ? (_model.Info(point.Module), point.Rule.Name) : (_info, kind.Name);

    string Key(ModuleInfo owner, string rule, string property) => $"{owner.TypeRef(_info, owner.ClassName)}.P_{rule}_{property}";

    static bool References(KindInfo kind) =>
        (kind.Alternative?.Clauses ?? ((SyntaxRule)kind.Rule!).Clauses).Any(c => c.Kind == BindingClauseKind.References);

    IEnumerable<(ModuleInfo Owner, SymbolPropertyDecl Property)> SymbolProperties()
    {
        foreach (var property in _info.Module.SymbolProperties) yield return (_info, property);
        foreach (var u in _info.Module.Usings)
            if (_model.Analysis.FindModule(u.Module) is { } used)
                foreach (var property in used.SymbolProperties) yield return (_model.Info(used), property);
    }

    string SymbolView => SymbolProperties().Any() ? _info.ShortName + "Symbol" : Ns + "SemanticSymbol";

    /// <summary>NGR0204: members of a semantics struct or symbol view that would share a name.</summary>
    public static IEnumerable<GrammarDiagnostic> Check(EmitModel model, ModuleInfo info)
    {
        if (!HasSemantics(model, info)) yield break;
        var writer = new SemanticsWriter(model, info);
        foreach (var kind in info.Kinds)
        {
            if (Block(kind) is null) continue;
            var names = new HashSet<string>(Reserved, StringComparer.Ordinal);
            foreach (string name in writer.Children(kind).Select(c => c.Name).Concat(Own(kind).Select(p => p.Name.Name)))
                if (!names.Add(name))
                    yield return new GrammarDiagnostic(GrammarCodes.SemanticsNameCollision, GrammarSeverity.Error,
                        $"'{name}' names two members of {kind.ViewName}Semantics: a child, a property, or one of {string.Join(", ", Reserved)}; rename it",
                        kind.Span, info.Module.Name);
        }
        foreach (var property in info.Module.SymbolProperties)
            if (SymbolReserved.Contains(property.Name.Name))
                yield return new GrammarDiagnostic(GrammarCodes.SemanticsNameCollision, GrammarSeverity.Error,
                    $"symbol property '{property.Name.Name}' clashes with a member of the symbol view ({string.Join(", ", SymbolReserved)})",
                    property.Name.Span, info.Module.Name);
    }

    /// <summary>Keys and the table, inside the module class.</summary>
    public void WriteMembers(StringBuilder b)
    {
        foreach (var rule in _model.Analysis.PrimaryRules(_info.Module))
            foreach (var property in PropertiesOf(rule))
            {
                b.Append($"    public static readonly {Ns}Property<\n");
                Fragment(b, property.Type, "");
                b.Append($"        > P_{rule.Name}_{property.Name.Name} =\n")
                    .Append($"        new({CSharpText.Literal(property.Name.Name)}, inherited: {(property.Direction == PropertyDirection.In ? "true" : "false")}, static () =>\n");
                Fragment(b, property.Default, "");
                b.Append("        ").Append(property.Hover ? ", hover: true" : "").Append(property.Expected ? ", expected: true" : "").Append(");\n\n");
            }
        foreach (var property in _info.Module.SymbolProperties)
        {
            b.Append($"    public static readonly {Ns}SymbolProperty<\n");
            Fragment(b, property.Type, "");
            b.Append($"        > S_{property.Name.Name} =\n")
                .Append($"        new({CSharpText.Literal(property.Name.Name)}, new[] {{ {string.Join(", ", property.Kinds.Select(k => CSharpText.Literal(k.Name)))} }}, static (kind, name) =>\n");
            Fragment(b, property.Default, "");
            b.Append("        );\n\n");
        }

        var keys = _model.Analysis.PrimaryRules(_info.Module)
            .SelectMany(rule => PropertiesOf(rule).Select(p => $"P_{rule.Name}_{p.Name.Name}"))
            .ToList();
        b.Append($"    static readonly {Ns}Property[] s_properties = {{ {string.Join(", ", keys)} }};\n\n")
            .Append($"    public override global::System.Collections.Generic.IReadOnlyList<{Ns}Property> Properties => s_properties;\n\n")
            .Append($"    static readonly {Ns}SymbolProperty[] s_symbolProperties = {{ {string.Join(", ", _info.Module.SymbolProperties.Select(p => "S_" + p.Name.Name))} }};\n\n")
            .Append($"    public override global::System.Collections.Generic.IReadOnlyList<{Ns}SymbolProperty> SymbolProperties => s_symbolProperties;\n\n");

        b.Append($"    static readonly {Ns}SemanticsRule?[] s_semantics =\n    {{\n        null,\n");
        foreach (var kind in _info.Kinds) b.Append("        ").Append(Rule(kind)).Append(", // ").Append(kind.Name).Append('\n');
        b.Append("    };\n\n")
            .Append("    public override bool HasSemantics => true;\n\n")
            .Append($"    public override {Ns}SemanticsRule? GetSemantics(int localKind) =>\n")
            .Append("        (uint)localKind < (uint)s_semantics.Length ? s_semantics[localKind] : null;\n\n");
    }

    string Rule(KindInfo kind)
    {
        if (Block(kind) is not { } block) return "null";
        string view = kind.ViewName + "Semantics";
        var (owner, ownerRule) = OwnerOf(kind);
        var children = Children(kind);
        var outs = new List<string>();
        var ins = new List<string>();
        var symbols = new List<string>();
        foreach (var assign in block.Statements.OfType<AssignStatement>())
        {
            string property = assign.Property.Name;
            switch (assign.Target)
            {
                case AssignTarget.Self:
                    outs.Add($"{Ns}SemanticsOut.Of({Key(owner, ownerRule, property)}, static (s, n) => new {view}(s, n).Out_{property}())");
                    break;
                case AssignTarget.Symbol:
                    var symbolOwner = SymbolProperties().First(p => p.Property.Name.Name == property).Owner;
                    symbols.Add($"{Ns}SemanticsSymbol.Of({symbolOwner.TypeRef(_info, symbolOwner.ClassName)}.S_{property}, static (s, n) => new {view}(s, n).Symbol_{property}())");
                    break;
                default:
                    var child = children.First(c => c.Name == assign.Child!.Name);
                    ins.Add($"{Ns}SemanticsIn.Of({Key(child.Owner!, child.Rule!.Name, property)}, {child.Index}, {child.Inner}, "
                        + $"static (s, n) => new {view}(s, n).In_{child.Name}_{property}())");
                    break;
            }
        }
        var checks = block.Statements.OfType<CheckStatement>()
            .Select((check, i) => $"new {Ns}SemanticsCheck({CSharpText.Literal(check.Code?.Name ?? "NS0100")}, "
                + $"static (s, n) => new {view}(s, n).Check_{i}(), static (s, n) => new {view}(s, n).Message_{i}()"
                + (check.At is { } at ? $", static (s, n) => new {view}(s, n).{CSharpText.Identifier(at.Name)}.Span" : "")
                + ")");
        return $"new(new {Ns}SemanticsOut[] {{ {string.Join(", ", outs)} }}, new {Ns}SemanticsIn[] {{ {string.Join(", ", ins)} }}, "
            + $"new {Ns}SemanticsSymbol[] {{ {string.Join(", ", symbols)} }}, new {Ns}SemanticsCheck[] {{ {string.Join(", ", checks)} }})";
    }

    /// <summary>The structs and the symbol view, after the views.</summary>
    public void WriteTypes(StringBuilder b)
    {
        foreach (var rule in _model.Analysis.PrimaryRules(_info.Module).OfType<ExtensibleRule>())
            if (rule.Properties.Count > 0) WriteStruct(b, rule.Name + "NodeSemantics", null, rule.Properties, _info, rule.Name);
        foreach (var kind in _info.Kinds)
            if (Block(kind) is not null)
            {
                var (owner, ownerRule) = OwnerOf(kind);
                WriteStruct(b, kind.ViewName + "Semantics", kind, Own(kind), owner, ownerRule);
            }
        if (SymbolProperties().Any()) WriteSymbolView(b);
    }

    void WriteStruct(StringBuilder b, string name, KindInfo? kind, IReadOnlyList<PropertyDecl> properties, ModuleInfo owner, string ownerRule)
    {
        b.Append($"public readonly struct {name} : {Ns}ISemanticView<{name}>\n{{\n")
            .Append($"    readonly {Ns}FileSemantics _s;\n    readonly int _node;\n\n")
            .Append($"    public {name}({Ns}FileSemantics semantics, int node)\n    {{\n        _s = semantics;\n        _node = node;\n    }}\n\n")
            .Append($"    public static {name} Create({Ns}FileSemantics semantics, int node) => new(semantics, node);\n\n")
            .Append($"    public {Ns}FileSemantics Semantics => _s;\n\n")
            .Append("    public int Node => _node;\n\n")
            .Append("    public global::Nitrogen.TextSpan Span => _s.Tree.Span(_node);\n\n")
            .Append("    public string Text => _s.Tree.GetText(_node).ToString();\n\n")
            .Append("    public bool IsMissing => (_s.Tree.Flags(_node) & global::Nitrogen.NodeFlags.Missing) != 0;\n\n")
            .Append($"    public {Ns}SemanticNode? Parent => _s.ParentOf(_node) is var parent && parent >= 0 ? new {Ns}SemanticNode(_s, parent) : null;\n\n");
        foreach (var property in properties)
            b.Append($"    public {property.Type.Text} {CSharpText.Identifier(property.Name.Name)} => _s.Get(_node, {Key(owner, ownerRule, property.Name.Name)});\n\n");
        if (kind is not null)
        {
            foreach (var child in Children(kind))
                b.Append($"    public {child.Type} {CSharpText.Identifier(child.Name)} => {Accessor(child)};\n\n");
            if (References(kind))
                b.Append($"    public {SymbolView}? Symbol => _s.SymbolOf(_node) is {{ }} symbol ? new {SymbolView}(_s, symbol) : null;\n\n");
            WriteMethods(b, kind);
        }
        b.Append("    public override string ToString() => Text;\n}\n\n");
    }

    void WriteMethods(StringBuilder b, KindInfo kind)
    {
        var block = Block(kind)!;
        var children = Children(kind);
        int check = 0;
        foreach (var statement in block.Statements)
        {
            if (statement is AssignStatement assign)
            {
                string property = assign.Property.Name;
                string type, method;
                switch (assign.Target)
                {
                    case AssignTarget.Self:
                        type = Own(kind).First(p => p.Name.Name == property).Type.Text;
                        method = "Out_" + property;
                        break;
                    case AssignTarget.Symbol:
                        type = SymbolProperties().First(p => p.Property.Name.Name == property).Property.Type.Text;
                        method = "Symbol_" + property;
                        break;
                    default:
                        var child = children.First(c => c.Name == assign.Child!.Name);
                        type = PropertiesOf(child.Rule).First(p => p.Name.Name == property).Type.Text;
                        method = $"In_{child.Name}_{property}";
                        break;
                }
                b.Append($"    internal {type} {method}() =>\n");
                Fragment(b, assign.Value, "");
                b.Append("        ;\n\n");
            }
            else if (statement is CheckStatement c)
            {
                b.Append($"    internal bool Check_{check}() =>\n");
                Fragment(b, c.Condition, "");
                b.Append("        ;\n\n")
                    .Append($"    internal string Message_{check}() =>\n");
                Fragment(b, c.Message, c.Message.Text.StartsWith("\"", StringComparison.Ordinal) ? "$" : "");
                b.Append("        ;\n\n");
                check++;
            }
        }
    }

    void WriteSymbolView(StringBuilder b)
    {
        string name = _info.ShortName + "Symbol";
        b.Append($"public readonly struct {name}\n{{\n")
            .Append($"    readonly {Ns}FileSemantics _s;\n    readonly global::Nitrogen.Binding.Symbol _symbol;\n\n")
            .Append($"    public {name}({Ns}FileSemantics semantics, global::Nitrogen.Binding.Symbol symbol)\n    {{\n        _s = semantics;\n        _symbol = symbol;\n    }}\n\n")
            .Append("    public global::Nitrogen.Binding.Symbol Symbol => _symbol;\n\n")
            .Append("    public string Name => _symbol.Name;\n\n")
            .Append("    public string Kind => _symbol.Kind;\n\n")
            .Append("    public bool IsBuiltin => _symbol.IsBuiltin;\n\n");
        foreach (var (owner, property) in SymbolProperties())
            b.Append($"    public {property.Type.Text} {CSharpText.Identifier(property.Name.Name)} => "
                + $"_s.GetSymbol(_symbol, {owner.TypeRef(_info, owner.ClassName)}.S_{property.Name.Name});\n\n");
        b.Append("    public override string ToString() => _symbol.ToString();\n}\n\n");
    }

    /// <summary>A node's named children, typed: through a label, an optional, a list, or a group holding one rule reference.</summary>
    List<Child> Children(KindInfo kind)
    {
        var body = kind.Alternative?.Body ?? ((SyntaxRule)kind.Rule!).Body;
        var children = new List<Child>();
        foreach (var (name, element, index) in ViewWriter.ChildSlots(SyntaxCodeWriter.Elements(body), kind.ViewName))
        {
            var bare = Unlabel(element);
            var shape = Shape.Plain;
            if (bare is RepeatExpr repeat)
            {
                shape = repeat.Kind == RepeatKind.Optional ? Shape.Optional : Shape.List;
                bare = Unlabel(repeat.Inner);
            }
            else if (bare is SeparatedListExpr list)
            {
                shape = Shape.SeparatedList;
                bare = Unlabel(list.Item);
            }

            int inner = -1;
            var reference = bare as ReferenceExpr;
            if (bare is SequenceExpr group)
            {
                var found = new List<(ReferenceExpr Reference, int Item)>();
                int item = 0;
                foreach (var e in group.Items.Select(Unlabel))
                {
                    if (e is PredicateExpr) continue;
                    if (e is ReferenceExpr r && _model.Resolve(r.Name, _info.Module, kind.ImplicitModule).Rule is SyntaxRule or ExtensibleRule)
                        found.Add((r, item));
                    item++;
                }
                if (found.Count == 1) (reference, inner) = found[0];
            }

            RuleDecl? rule = null;
            ModuleInfo? owner = null;
            string itemType = Ns + "SemanticNode";
            if (reference is not null)
            {
                var symbol = _model.Resolve(reference.Name, _info.Module, kind.ImplicitModule);
                if (symbol.Rule is SyntaxRule or ExtensibleRule)
                {
                    rule = symbol.Rule;
                    owner = _model.Info(symbol.Module);
                    if (StructOf(rule) is { } structName) itemType = owner.TypeRef(_info, structName);
                }
            }
            children.Add(new Child(name, index, inner, shape, itemType, rule, owner));
        }
        return children;
    }

    /// <summary>The semantics struct generated for a rule, or null when it has none.</summary>
    static string? StructOf(RuleDecl rule) => rule switch
    {
        ExtensibleRule { Properties.Count: > 0 } => rule.Name + "NodeSemantics",
        SyntaxRule { Semantics: not null } syntax when !EmitModel.IsAlias(syntax) => rule.Name + "NodeSemantics",
        _ => null,
    };

    static string Accessor(Child child)
    {
        string at = $"_s.Tree.Child(_node, {child.Index})";
        string item = child.Inner < 0 ? at : $"_s.Tree.Child({at}, {child.Inner})";
        return child.Shape switch
        {
            Shape.Plain => $"new(_s, {item})",
            Shape.Optional => $"_s.Tree.Kind({at}) == global::Nitrogen.SyntaxKinds.Empty ? null : new {child.ItemType}(_s, {item})",
            Shape.List => $"new(_s, {at}, 1, {child.Inner})",
            _ => $"new(_s, {at}, 2, {child.Inner})",
        };
    }

    static Expr Unlabel(Expr expr) => expr is LabeledExpr labeled ? labeled.Inner : expr;

    /// <summary>C# from the grammar on lines of its own, mapped back to its .ngr span by #line (issue 239).</summary>
    void Fragment(StringBuilder b, CodeText code, string prefix)
    {
        var input = _model.InputOf(_info.Module);
        if (input is not null)
        {
            var (startLine, startColumn) = Position(input.Text, code.Span.Start);
            var (endLine, endColumn) = Position(input.Text, code.Span.End);
            b.Append($"#line ({startLine}, {startColumn}) - ({endLine}, {endColumn}) {prefix.Length + 1} \"{input.Path.Replace("\"", "")}\"\n");
        }
        b.Append(prefix).Append(code.Text).Append('\n');
        if (input is not null) b.Append("#line default\n");
    }

    /// <summary>1-based line and column of an offset in the module's file.</summary>
    (int Line, int Column) Position(string text, int offset)
    {
        if (_lineStarts is null)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            _lineStarts = starts.ToArray();
        }
        int line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0) line = ~line - 1;
        return (line + 1, offset - _lineStarts[line] + 1);
    }
}

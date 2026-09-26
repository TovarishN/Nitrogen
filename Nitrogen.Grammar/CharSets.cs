using System.Text;

namespace Nitrogen.Grammar;

/// <summary>A set of possible first characters: 128 ASCII bits, a non-ASCII bit, or "any".</summary>
internal sealed class CharSet
{
    ulong _low, _high;

    public bool NonAscii { get; private set; }

    public bool Any { get; private set; }

    public void Add(char c)
    {
        if (c < 64) _low |= 1UL << c;
        else if (c < 128) _high |= 1UL << (c - 64);
        else NonAscii = true;
    }

    public void AddRange(char first, char last)
    {
        for (int c = first; c <= last && c < 128; c++) Add((char)c);
        if (last >= 128) NonAscii = true;
    }

    public void AddAny() => Any = true;

    public bool Contains(char c)
    {
        if (Any) return true;
        if (c < 64) return ((_low >> c) & 1) != 0;
        if (c < 128) return ((_high >> (c - 64)) & 1) != 0;
        return NonAscii;
    }

    public bool Overlaps(CharSet other) =>
        Any || other.Any || (_low & other._low) != 0 || (_high & other._high) != 0 || (NonAscii && other.NonAscii);

    public void Union(CharSet other)
    {
        _low |= other._low;
        _high |= other._high;
        NonAscii |= other.NonAscii;
        Any |= other.Any;
    }

    /// <summary>The runtime <c>AsciiSet</c> expression; an empty or unrestricted set is <c>AsciiSet.Any</c>.</summary>
    public string ToAsciiSetCode()
    {
        if (Any || (_low == 0 && _high == 0 && !NonAscii)) return "AsciiSet.Any";
        var chars = new StringBuilder();
        for (int c = 0; c < 128; c++)
        {
            ulong bit = c < 64 ? (_low >> c) & 1 : (_high >> (c - 64)) & 1;
            if (bit != 0) chars.Append((char)c);
        }
        string code = "AsciiSet.Of(" + CSharpText.Literal(chars.ToString()) + ")";
        return NonAscii ? code + ".WithNonAscii()" : code;
    }
}

/// <summary>First-character sets of alternatives, used as the runtime's dispatch filter.</summary>
internal sealed class FirstSets
{
    readonly EmitModel _model;

    public FirstSets(EmitModel model)
    {
        _model = model;
    }

    public CharSet OfSequence(IReadOnlyList<Expr> elements, int from, ModuleDecl module, string? implicitModule)
    {
        var set = new CharSet();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        for (int i = from; i < elements.Count; i++)
        {
            set.Union(Of(elements[i], module, implicitModule, visiting));
            if (!_model.Analysis.IsNullable(elements[i], module, implicitModule)) break;
        }
        return set;
    }

    CharSet Of(Expr expr, ModuleDecl module, string? implicitModule, HashSet<string> visiting)
    {
        var set = new CharSet();
        switch (expr)
        {
            case LiteralExpr literal:
                set.Add(literal.Value[0]);
                break;
            case CharClassExpr charClass:
                if (charClass.Negated) set.AddAny();
                else foreach (var range in charClass.Ranges) set.AddRange(range.First, range.Last);
                break;
            case AnyCharExpr:
                set.AddAny();
                break;
            case LabeledExpr labeled:
                return Of(labeled.Inner, module, implicitModule, visiting);
            case RepeatExpr repeat:
                return Of(repeat.Inner, module, implicitModule, visiting);
            case SeparatedListExpr list:
                set.Union(Of(list.Item, module, implicitModule, visiting));
                if (_model.Analysis.IsNullable(list.Item, module, implicitModule))
                    set.Union(Of(list.Separator, module, implicitModule, visiting));
                break;
            case ChoiceExpr choice:
                foreach (var alternative in choice.Alternatives) set.Union(Of(alternative, module, implicitModule, visiting));
                break;
            case SequenceExpr sequence:
                foreach (var item in sequence.Items)
                {
                    set.Union(Of(item, module, implicitModule, visiting));
                    if (!_model.Analysis.IsNullable(item, module, implicitModule)) break;
                }
                break;
            case ReferenceExpr reference:
            {
                var symbol = _model.Resolve(reference.Name, module, implicitModule);
                if (symbol.Rule is ExtensibleRule)
                {
                    set.AddAny(); // modules composed later may add alternatives
                    break;
                }
                if (!visiting.Add(symbol.Key)) break;
                var body = symbol.Rule is TokenRule token ? token.Body : ((SyntaxRule)symbol.Rule).Body;
                set.Union(Of(body, symbol.Module, null, visiting));
                visiting.Remove(symbol.Key);
                break;
            }
        }
        return set;
    }
}

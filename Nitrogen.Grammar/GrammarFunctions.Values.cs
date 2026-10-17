using System.Text;

namespace Nitrogen.Grammar;

public static partial class GrammarFunctions
{
    /// <summary>The static type of a value expression; <see cref="RuleName"/> is a rule used as a value.</summary>
    internal enum ValueType
    {
        Int,
        Bool,
        Char,
        Text,
        Class,
        Rule,
        RuleName,
    }

    internal static string Describe(ValueType type) => type switch
    {
        ValueType.Int => IntType,
        ValueType.Bool => "a condition",
        ValueType.Char => CharType,
        ValueType.Text => TextType,
        ValueType.Class => ClassType,
        ValueType.Rule => RuleType,
        _ => "a rule name",
    };

    static ValueType? ParameterType(string category) => category switch
    {
        IntType => ValueType.Int,
        CharType => ValueType.Char,
        TextType => ValueType.Text,
        ClassType => ValueType.Class,
        RuleType => ValueType.Rule,
        _ => null,
    };

    /// <summary>Whether a value of <paramref name="from"/> may be used where <paramref name="to"/> is expected.</summary>
    static bool Converts(ValueType from, ValueType to) => from == to || to switch
    {
        ValueType.Class => from is ValueType.Char or ValueType.RuleName,
        ValueType.Rule => from is ValueType.Char or ValueType.Class or ValueType.Text or ValueType.RuleName,
        _ => false,
    };

    /// <summary>A runtime value.</summary>
    internal abstract class Value
    {
        /// <summary>A canonical text: equal keys mean equal values (memoization).</summary>
        public abstract string Key { get; }
    }

    internal sealed class IntValue(long value) : Value
    {
        public long Number => value;
        public override string Key => "i" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed class BoolValue(bool value) : Value
    {
        public bool Truth => value;
        public override string Key => value ? "t" : "f";
    }

    internal sealed class CharValue(char value) : Value
    {
        public char Character => value;
        public override string Key => "c" + ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed class TextValue(string value) : Value
    {
        public string Text => value;
        public override string Key => "s" + value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
    }

    internal sealed class ClassValue(CodeSet set) : Value
    {
        public CodeSet Set => set;
        public override string Key => "k" + set.Key;
    }

    internal sealed class RuleValue(Expr rule) : Value
    {
        string? _key;
        public Expr Rule => rule;
        public override string Key => _key ??= "r" + GrammarDumper.Dump(rule);
    }

    /// <summary>A rule written as a value: a reference, or a class when the rule is one.</summary>
    internal sealed class RuleNameValue(GrammarSymbol symbol) : Value
    {
        public GrammarSymbol Symbol => symbol;
        public override string Key => "n" + symbol.Key;
    }

    /// <summary>An immutable set of UTF-16 code units as sorted, disjoint, non-adjacent inclusive ranges.</summary>
    internal sealed class CodeSet
    {
        public static readonly CodeSet Empty = new(new List<(int, int)>());
        public static readonly CodeSet All = new(new List<(int, int)> { (0, char.MaxValue) });

        readonly List<(int First, int Last)> _ranges;
        string? _key;

        CodeSet(List<(int First, int Last)> normalized) => _ranges = normalized;

        public IReadOnlyList<(int First, int Last)> Ranges => _ranges;

        public bool IsEmpty => _ranges.Count == 0;

        /// <summary>Ranges clipped to U+0000–U+FFFF; a range with <c>last &lt; first</c> is empty.</summary>
        public static CodeSet Of(IEnumerable<(long First, long Last)> ranges)
        {
            var clipped = ranges.Select(r => (First: Math.Max(r.First, 0L), Last: Math.Min(r.Last, (long)char.MaxValue)))
                .Where(r => r.First <= r.Last).OrderBy(r => r.First).ToList();
            var merged = new List<(int First, int Last)>();
            foreach (var (first, last) in clipped)
                if (merged.Count > 0 && first <= merged[merged.Count - 1].Last + 1L)
                    merged[merged.Count - 1] = (merged[merged.Count - 1].First, Math.Max((int)last, merged[merged.Count - 1].Last));
                else merged.Add(((int)first, (int)last));
            return new CodeSet(merged);
        }

        public static CodeSet Of(char c) => Of(new[] { ((long)c, (long)c) });

        public static CodeSet Of(CharClassExpr charClass)
        {
            var set = Of(charClass.Ranges.Select(r => ((long)r.First, (long)r.Last)));
            return charClass.Negated ? set.Complement() : set;
        }

        public CodeSet Union(CodeSet other) => Of(Wide(_ranges).Concat(Wide(other._ranges)));

        public CodeSet Complement()
        {
            var result = new List<(long, long)>();
            long next = 0;
            foreach (var (first, last) in _ranges)
            {
                if (first > next) result.Add((next, first - 1));
                next = last + 1;
            }
            result.Add((next, char.MaxValue));
            return Of(result);
        }

        public CodeSet Intersect(CodeSet other) => Complement().Union(other.Complement()).Complement();

        public CodeSet Except(CodeSet other) => Intersect(other.Complement());

        /// <summary>Every member moved by <paramref name="offset"/>; members leaving U+0000–U+FFFF are dropped.</summary>
        public CodeSet Shift(long offset) => Of(_ranges.Select(r => (r.First + offset, r.Last + offset)));

        public string Key
        {
            get
            {
                if (_key is not null) return _key;
                var builder = new StringBuilder();
                foreach (var (first, last) in _ranges) builder.Append(first.ToString("x")).Append('-').Append(last.ToString("x")).Append(',');
                return _key = builder.ToString();
            }
        }

        public CharClassExpr ToClass(GrammarSpan span) =>
            new(_ranges.Select(r => new CharRange((char)r.First, (char)r.Last)).ToArray(), false, span);

        static IEnumerable<(long, long)> Wide(List<(int First, int Last)> ranges) => ranges.Select(r => ((long)r.First, (long)r.Last));
    }
}

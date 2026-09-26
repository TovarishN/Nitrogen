namespace Nitrogen;

/// <summary>A named entry point: a generated rule function.</summary>
public readonly unsafe struct Rule
{
    public Rule(string name, delegate*<ref ParserState, bool> parse)
    {
        Name = name;
        Parse = parse;
    }

    public readonly string Name;

    public readonly delegate*<ref ParserState, bool> Parse;
}

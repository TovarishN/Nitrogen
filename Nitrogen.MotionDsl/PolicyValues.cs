using System.Globalization;

namespace Nitrogen.MotionDsl;

/// <summary>
/// The value rules Policy.ngr's blocks call (issue 241). Numbers, integers and counts follow
/// PolicyParser (ExpectNumber, ExpectInt, ExpectLongWithSuffix) and PolicyAstMapper. Messages are the
/// pipeline's own, without its trailing " at line:col". A null amount is a missing number, which
/// recovery reports: every rule lets it pass.
/// </summary>
public static partial class PolicyValues
{
    public static float? Parse(string text, bool negative)
    {
        float value = float.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    /// <summary>ExpectInt: any whole number, <c>2.0</c> included.</summary>
    public static bool Integer(float? value) => value is not float v || v == MathF.Floor(v);

    public static string NotInteger(float? value) => $"Expected an integer, got {value}";

    /// <summary>The integer ExpectInt returns, for messages that print it.</summary>
    public static int Whole(float? value) => (int)(value ?? 0f);

    /// <summary>PolicyCompiler.F: invariant, two decimals.</summary>
    public static string F(float? value) => (value ?? 0f).ToString("0.00", CultureInfo.InvariantCulture);

    public static long? Count(string text)
    {
        int length = NumberLength(text);
        if (!long.TryParse(text.Substring(0, length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) return null;
        return text.Substring(length) switch
        {
            "" => value,
            "k" => value * 1_000L,
            "M" => value * 1_000_000L,
            _ => null,
        };
    }

    public static string? CountProblem(string text)
    {
        int length = NumberLength(text);
        string digits = text.Substring(0, length);
        if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return $"Expected an integer count, got '{digits}'";
        string suffix = text.Substring(length);
        return suffix is "" or "k" or "M" ? null : $"Unknown number suffix '{suffix}' (use k or M)";
    }

    /// <summary>The count in an <c>over N</c> group's text.</summary>
    public static string OverCount(string group) => group.Trim().Substring("over".Length).Trim();

    /// <summary>How much of a count MotionLexer.ReadNumber takes (PolicyAstMapper.NumberLength).</summary>
    static int NumberLength(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        }
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            int look = i + 1;
            if (look < text.Length && (text[look] == '+' || text[look] == '-')) look++;
            if (look < text.Length && char.IsAsciiDigit(text[look]))
            {
                i = look;
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            }
        }
        return i;
    }

    public static string? ActuateProblem(string setting, float? value) => setting switch
    {
        "rate_limit" when value is float v && (v <= 0f || v >= 2f) => $"actuate rate_limit {F(v)} must be in (0, 2): action units span [-1, 1]",
        "scale" when value is float v && (v <= 0f || v > 1f) => $"actuate scale {F(v)} must be in (0, 1]",
        "smoothing" when value is float v && (v < 0f || v >= 1f) => $"actuate smoothing {F(v)} must be in [0, 1)",
        _ => null,
    };

    /// <summary>The sign a simple term's weight must have: goal_hit rewards, action_rate and torque penalise.</summary>
    public static string? SimpleTermProblem(string term, float? weight) => term switch
    {
        "goal_hit" when weight < 0f => "goal_hit weight must be positive",
        "action_rate" or "torque" when weight > 0f => $"regulariser '{term}' must have a non-positive weight",
        _ => null,
    };

    public static string? RandomizeProblem(string setting, float? low, float? high)
    {
        if (low is not float lo || high is not float hi) return null;
        if (setting == "latency")
            return !Integer(lo) || !Integer(hi) || !(lo < 0f || lo > hi) ? null : "randomize latency must satisfy 0 <= lo <= hi";
        return lo <= 0f || lo > hi ? $"randomize {setting} {F(lo)}..{F(hi)} must satisfy 0 < lo <= hi" : null;
    }

    public static string? ProgressProblem(string setting, float? from, float? to)
    {
        if (from is not float a || to is not float b || !(a < 0f || b > 1f || a > b)) return null;
        return setting switch
        {
            "command_box" or "posture_depth" or "push_scale" => $"{setting} is a progress and must go from [0, 1] up to at most 1",
            "family_mix" => "family_mix must go from [0, 1] up to at most 1",
            _ => null,
        };
    }

    public static bool StartKind(string word) => word is "supine" or "spawn" or "reference" or "standing" or "crumple";
}

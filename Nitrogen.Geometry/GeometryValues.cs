using System.Globalization;

namespace Nitrogen.Geometry.Syntax;

public static class GeometryValues
{
    public static float? Parse(string text, bool negative) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        float.IsFinite(value) ? (negative ? -value : value) : null;

    public static bool Positive(float? value) =>
        value is float number && float.IsFinite(number) && number > 0f;
}

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Nitrogen.Tests;

/// <summary>
/// Canonical text of a MotionDSL AST. The records hold IReadOnlyList fields that compare by
/// reference, so equivalence is checked on this dump instead. Floats are written by their bits.
/// </summary>
internal static class AstDump
{
    public static string Dump(object? value)
    {
        var builder = new StringBuilder();
        Write(value, builder);
        return builder.ToString();
    }

    static void Write(object? value, StringBuilder builder)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string s:
                builder.Append('"').Append(s).Append('"');
                return;
            case float f:
                builder.Append(f.ToString("R", CultureInfo.InvariantCulture))
                    .Append('#').Append(BitConverter.SingleToInt32Bits(f).ToString("x8"));
                return;
            case bool or int or long or Enum:
                builder.Append(value);
                return;
            case IEnumerable items:
                builder.Append('[');
                foreach (object? item in items)
                {
                    Write(item, builder);
                    builder.Append(", ");
                }
                builder.Append(']');
                return;
        }
        var type = value.GetType();
        builder.Append(type.Name).Append('{');
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            builder.Append(property.Name).Append('=');
            Write(property.GetValue(value), builder);
            builder.Append("; ");
        }
        // Vector3 and Quaternion expose their components as fields, not properties.
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            builder.Append(field.Name).Append('=');
            Write(field.GetValue(value), builder);
            builder.Append("; ");
        }
        builder.Append('}');
    }
}

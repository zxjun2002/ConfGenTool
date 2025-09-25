using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

public static partial class ConfGen
{
    static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch { return Array.Empty<Type>(); }
    }

    static string MapCsvTypeToCSharpType(string csvType)
    {
        var t = (csvType ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "int"       => "int",
            "long"      => "long",
            "float"     => "float",
            "double"    => "double",
            "bool"      => "bool",
            "int[]"     => "int[]",
            "list<int>" => "List<int>",
            _           => "string",
        };
    }

    static object ConvertValue(string? value, Type targetType)
    {
        if (string.IsNullOrWhiteSpace(value)) return GetDefaultValue(targetType);

        try
        {
            if (targetType == typeof(int))       return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            if (targetType == typeof(long))      return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vl) ? vl : 0L;
            if (targetType == typeof(float))     return float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var vf) ? vf : 0f;
            if (targetType == typeof(double))    return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var vd) ? vd : 0d;
            if (targetType == typeof(bool))
            {
                var s = value.Trim().ToLowerInvariant();
                if (s is "1" or "y" or "yes" or "true" or "是" or "真") return true;
                if (s is "0" or "n" or "no"  or "false" or "否" or "假") return false;
                return bool.TryParse(s, out var vb) && vb;
            }
            if (targetType == typeof(int[]))     return ParseIntArray(value);
            if (targetType == typeof(List<int>)) return ParseIntList(value);
        }
        catch (Exception ex)
        {
            Err($"[Error] ConvertValue: {ex.Message}");
        }
        return value!;
    }

    static object GetDefaultValue(Type t) => t.IsValueType ? Activator.CreateInstance(t)! : null!;

    static int[] ParseIntArray(string value)
    {
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
    }

    static List<int> ParseIntList(string value)
    {
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();
    }
}
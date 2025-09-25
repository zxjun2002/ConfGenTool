using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

public static partial class ConfGen
{
    // =================== schema 读取/筛选辅助 ===================
    class SchemaProperty { public string property_name { get; set; } = ""; public string property_value { get; set; } = ""; }
    class ColumnSchema
    {
        public string col_name { get; set; } = "";
        public string value_type { get; set; } = "string";
        public int?   pk_order { get; set; } = null;
        public bool?  primary_key { get; set; } = null;
        public int?   col_sort { get; set; } = null;                   // 支持 schema 指定列顺序
        public List<SchemaProperty> col_properties { get; set; } = new();
    }
    class TableSchema
    {
        public string table_name { get; set; } = "";
        public List<ColumnSchema> columns { get; set; } = new();
        public List<SchemaProperty> table_properties { get; set; } = new();
    }
    class SheetSchema
    {
        public string sheet_name { get; set; } = "";
        public List<ColumnSchema> columns { get; set; } = new();
        public List<SchemaProperty> sheet_properties { get; set; } = new();
    }
    class WorkbookSchema
    {
        public string workbook_name { get; set; } = "";
        public List<SheetSchema> sheets { get; set; } = new();
    }

    // ---- 侧别判定 ----
    static string NormalizeSide(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "ALL";
        s = s.Trim().ToUpperInvariant();
        return s switch
        {
            "C" => "C",
            "S" => "S",
            "ALL" => "ALL",
            "NONE" => "NONE",
            _ => "ALL",
        };
    }

    static string ResolveCsType(List<SchemaProperty>? props, string fallback = "ALL")
    {
        var v = props?.FirstOrDefault(p => string.Equals(p.property_name, "cs_type", StringComparison.OrdinalIgnoreCase))?.property_value;
        return NormalizeSide(string.IsNullOrWhiteSpace(v) ? fallback : v!);
    }

    // 表/Sheet 是否匹配目标侧
    static bool MatchesSide(string csType, string target)
    {
        csType = NormalizeSide(csType);
        target = NormalizeSide(target);
        if (csType == "NONE") return false;
        if (target == "ALL")  return csType != "NONE";
        return csType == "ALL" || csType == target;
    }

    static T? ReadJson<T>(string path)
    {
        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, opt);
    }

    static string SchemaPathFor(string tablePath) => tablePath + ".schema.json";
}
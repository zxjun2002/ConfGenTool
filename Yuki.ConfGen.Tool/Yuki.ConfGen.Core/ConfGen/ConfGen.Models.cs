using System;
using System.Collections.Generic;
using System.Text;

public static partial class ConfGen
{
    // =================== 模型 ===================
    public class GenCodeProp { public string name = ""; public string propType = "string"; }

    public class ClassContent
    {
        public string className = "";
        public string fileName  = "";
        public string FilePath  = "";
        public string classDef  = "";
        public List<GenCodeProp> props = new();
        public List<int?> keyPriorities = new(); // 来自 schema 的 pk_order
    }

    public class GenCodeContent { public List<ClassContent> classes = new(); }

    public static class ReadConfEditorUtil
    {
        public static string ToCamelUpper(string str)
        {
            var parts = str.Split('_', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (p.Length == 0) continue;
                sb.Append(char.ToUpper(p[0]));
                if (p.Length > 1) sb.Append(p.Substring(1));
            }
            return sb.ToString();
        }

        public static string ToCamelLower(string str)
        {
            var parts = str.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return str;
            var sb = new StringBuilder();
            sb.Append(parts[0]);
            for (int i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.Length == 0) continue;
                sb.Append(char.ToUpper(p[0]));
                if (p.Length > 1) sb.Append(p.Substring(1));
            }
            return sb.ToString();
        }
    }
}
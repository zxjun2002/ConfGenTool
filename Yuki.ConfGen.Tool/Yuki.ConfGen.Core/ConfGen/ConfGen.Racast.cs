using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

public static partial class ConfGen
{
    static void EnsureIRacastSet(string outRacastDir)
    {
        var path = Path.Combine(outRacastDir, "IRacastSet.cs");
        if (!File.Exists(path))
            File.WriteAllText(path, "public interface IRacastSet { }", new UTF8Encoding(false));
    }

    static void DoGenRacastSet(
        Dictionary<string, List<(string className, string classFieldName)>> folderClasses,
        string outRacastDir,
        GenCodeContent scan)
    {
        Directory.CreateDirectory(outRacastDir);

        var meta = scan.classes.ToDictionary(c => c.className, c => c);

        Log($"[ConfGen] RacastSets: {folderClasses.Count} set(s)");

        foreach (var kv in folderClasses)
        {
            string folderName = string.IsNullOrEmpty(kv.Key) ? "Root" : kv.Key;
            var classes = kv.Value;
            string typeName = $"{folderName}RacastSet";

            string setDir = Path.Combine(outRacastDir, typeName);
            Directory.CreateDirectory(setDir);

            var sb = new StringBuilder();
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine();
            sb.AppendLine($"public sealed partial class {typeName} : IRacastSet");
            sb.AppendLine("{");
            foreach (var (className, _) in classes)
            {
                var c = meta[className];
                var chain = ExtractKeyChain(c);
                if (chain.Count == 0)
                {
                    sb.AppendLine($"    // {className}: no key priority configured");
                    continue;
                }
                sb.AppendLine(EmitIndexFieldDeclCt(className, c.props, chain));
            }
            sb.AppendLine();
            sb.AppendLine($"    public {typeName}(ConfData data)");
            sb.AppendLine("    {");
            foreach (var (className, fieldName) in classes)
            {
                var c = meta[className];
                var chain = ExtractKeyChain(c);
                if (chain.Count == 0) continue;
                sb.AppendLine(EmitIndexCtorCt(className, fieldName, c.props, chain));
            }
            sb.AppendLine("        OnAfterInit();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    // 在 *.Logic.cs 中实现；未实现则无开销");
            sb.AppendLine("    partial void OnAfterInit();");
            sb.AppendLine("}");

            var autoPath = Path.Combine(setDir, $"{typeName}.cs");
            File.WriteAllText(autoPath, sb.ToString(), new UTF8Encoding(false));

            Log($"[ConfGen]  - {typeName} -> {Path.GetFullPath(autoPath)}  (tables: {classes.Count})");
            foreach (var (className, _) in classes)
            {
                var c = meta[className];
                var chain = ExtractKeyChain(c);
                string keys = chain.Count == 0 ? "none" : string.Join(" / ", chain);
                Log($"[ConfGen]      • {className}  keys: {keys}");
            }

            var logicPath = Path.Combine(setDir, $"{typeName}.Logic.cs");
            if (!File.Exists(logicPath))
            {
                var sbLogic = new StringBuilder();
                sbLogic.AppendLine("// 此文件仅首次生成，之后不会被覆盖");
                sbLogic.AppendLine("using System.Collections.Generic;");
                sbLogic.AppendLine("using System.Linq;");
                sbLogic.AppendLine();
                sbLogic.AppendLine($"public sealed partial class {typeName}");
                sbLogic.AppendLine("{");
                sbLogic.AppendLine("    partial void OnAfterInit()");
                sbLogic.AppendLine("    {");
                sbLogic.AppendLine("        // TODO: 在这里构建你的业务索引/缓存");
                sbLogic.AppendLine("    }");
                sbLogic.AppendLine("}");
                File.WriteAllText(logicPath, sbLogic.ToString(), new UTF8Encoding(false));
                Log($"[ConfGen]      (create once) {Path.GetFullPath(logicPath)}");
            }
        }
    }

    static List<string> ExtractKeyChain(ClassContent c)
    {
        var pairs = c.props.Zip(c.keyPriorities, (p, pr) => (p.name, pr))
                           .Where(t => t.pr.HasValue && t.pr.Value > 0)
                           .OrderBy(t => t.pr!.Value)
                           .ToList();
        return pairs.Select(x => x.name).ToList();
    }

    static string EmitIndexFieldDeclCt(string className, List<GenCodeProp> props, List<string> keys)
    {
        string KeyType(int i) => props.First(p => p.name == keys[i]).propType;
        string TailType(int i) => (i == keys.Count - 1)
            ? className
            : $"Dictionary<{KeyType(i + 1)}, {TailType(i + 1)}>";
        return $"    public Dictionary<{KeyType(0)}, {TailType(0)}> {className}Ct {{ get; private set; }}";
    }

    static string EmitIndexCtorCt(string className, string fieldName, List<GenCodeProp> props, List<string> keys)
    {
        if (keys.Count == 1)
            return $"        {className}Ct = data.{fieldName}.ToDictionary(e => e.{keys[0]}, e => e);";

        var sb = new StringBuilder();
        sb.Append($"        {className}Ct = data.{fieldName}.GroupBy(e => e.{keys[0]})");
        for (int i = 0; i < keys.Count - 2; i++)
        {
            sb.Append($".ToDictionary(g{i} => g{i}.Key, g{i} => g{i}.GroupBy(e => e.{keys[i + 1]}))");
            sb.AppendLine().Append(new string(' ', 8));
        }
        int last = keys.Count - 2;
        sb.Append($".ToDictionary(g{last} => g{last}.Key, g{last} => g{last}.ToDictionary(e => e.{keys.Last()}, e => e));");
        return sb.ToString();
    }
}
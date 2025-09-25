using System;
using System.IO;
using System.Linq;
using System.Text;

public static partial class ConfGen
{
    /// <summary>
    /// 代码生成：仅读取表的第1行列名；类型/主键/列顺序/侧别全部来自 *.schema.json；按 --side 过滤
    /// </summary>
    public static void GenerateCode(string inputDir, string outCodeFile, string outRacastDir, string side = "ALL")
    {
        var scan = ScanInput(inputDir, side);

        Directory.CreateDirectory(Path.GetDirectoryName(outCodeFile)!);
        Directory.CreateDirectory(outRacastDir);

        const string tip  = "/// <summary>\n/// 此代码为自动生成, 修改无意义, 重新生成会被覆盖\n/// </summary>\n\n";
        const string head = "using MemoryPack;\nusing System.Collections.Generic;\n\n";

        var sbField = new StringBuilder();
        sbField.AppendLine("[MemoryPackable]");
        sbField.AppendLine("public partial class ConfData");
        sbField.AppendLine("{");

        var sbDef = new StringBuilder();

        foreach (var it in scan.classes)
        {
            string fieldName = char.ToLower(it.className[0]) + it.className.Substring(1);
            sbField.AppendLine($"    public {it.className}[] {fieldName};");

            var cs = new StringBuilder();
            cs.AppendLine("[MemoryPackable]");
            cs.AppendLine($"public partial class {it.className}");
            cs.AppendLine("{");
            foreach (var p in it.props)
                cs.AppendLine($"    public {p.propType} {p.name};");
            cs.AppendLine("}");
            cs.AppendLine();

            sbDef.Append(cs);
        }
        sbField.AppendLine("}");

        var final = new StringBuilder();
        final.Append(tip);
        final.Append(head);
        final.Append(sbField);
        final.AppendLine();
        final.Append(sbDef);

        File.WriteAllText(outCodeFile, final.ToString(), new UTF8Encoding(false));

        Log($"[ConfGen] ConfData  -> {Path.GetFullPath(outCodeFile)}");
        Log($"[ConfGen] Tables({scan.classes.Count}): {string.Join(", ", scan.classes.Select(c => c.className))}");

        var folderClasses = GroupByFolder(scan);
        EnsureIRacastSet(outRacastDir);
        DoGenRacastSet(folderClasses, outRacastDir, scan);

        Log("[ConfGen] Code & Racast generated.");
    }
}
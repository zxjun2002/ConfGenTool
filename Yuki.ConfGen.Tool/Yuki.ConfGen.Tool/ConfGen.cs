using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;

public static class ConfGen
{
    // =============== 对外两个入口 =================

    // Step1: 生成代码 + RacastSet
    public static void GenerateCode(string inputDir, string outCodeFile, string outRacastDir)
    {
        var scan = ScanInput(inputDir);

        Directory.CreateDirectory(Path.GetDirectoryName(outCodeFile)!);
        Directory.CreateDirectory(outRacastDir);

        string tip = "/// <summary>\n/// 此代码为自动生成, 修改无意义, 重新生成会被覆盖\n/// </summary>\n\n";
        string codeStr = "using MemoryPack;\nusing System.Collections.Generic;\n\n";

        var sbField = new StringBuilder();
        sbField.AppendLine("[MemoryPackable]");
        sbField.AppendLine("public partial class ConfData {");

        var sbDef = new StringBuilder();

        // 生成每个表的类 & ConfData 字段
        foreach (var it in scan.classes)
        {
            string classFieldName = char.ToLower(it.className[0]) + it.className.Substring(1);
            sbField.AppendLine($"    public {it.className}[] {classFieldName};");

            var cs = new StringBuilder();
            cs.AppendLine("[MemoryPackable]");
            cs.AppendLine($"public partial class {it.className} {{");
            foreach (var p in it.props)
            {
                cs.AppendLine($"    public {p.propType} {p.name};");
            }
            cs.AppendLine("}");
            cs.AppendLine();

            sbDef.Append(cs);
        }
        sbField.AppendLine("}");

        var final = new StringBuilder();
        final.Append(tip);
        final.Append(codeStr);
        final.Append(sbField);
        final.AppendLine();
        final.Append(sbDef);

        File.WriteAllText(outCodeFile, final.ToString(), Encoding.UTF8);

        // 生成 RacastSet
        var folderClasses = GroupByFolder(scan);
        DoGenRacastSet(folderClasses, outRacastDir);

        // 可选：把扫描结果也保存成 manifest（供调试/排查）
        // 这里只保存 className 和 filePath，够用
        try
        {
            var manifestPathGuess = Path.Combine(Path.GetDirectoryName(outCodeFile)!, "..", "..", "Output", "ConfManifest.json");
            var toSave = new { classes = scan.classes.Select(c => new { c.className, c.FilePath }).ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPathGuess))!);
            File.WriteAllText(manifestPathGuess, JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 忽略 */ }

        Console.WriteLine("[ConfGen] Code & Racast generated.");
    }

    // Step2: 读取表 -> 构造 ConfData -> MemoryPack 序列化
    public static void BuildBinary(string inputDir, string outBytesFile, string outManifestFile)
    {
        var scan = ScanInput(inputDir); // 和生成代码同样的扫描逻辑

        Directory.CreateDirectory(Path.GetDirectoryName(outBytesFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(outManifestFile)!);

        var confType = FindType("ConfData")
            ?? throw new Exception("ConfData type not found. 先执行 all/gen 让项目编译并加载模型 DLL。");

        var data = Activator.CreateInstance(confType)!;

        // 逐字段读取
        var fields = confType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var field in fields)
        {
            Type elemType = field.FieldType.GetElementType()!;
            var subFields = elemType.GetFields(BindingFlags.Public | BindingFlags.Instance);

            string className = char.ToUpper(field.Name[0]) + field.Name.Substring(1);

            var cc = scan.classes.FirstOrDefault(c => c.className == className);
            if (cc == null)
            {
                Console.WriteLine($"[Warn] 未找到 ClassContent: {className}");
                continue;
            }

            string filePath = cc.FilePath;
            try
            {
                var headerNames = new List<string>();
                var confList = new List<object>();

                if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    ReadCsvFile(filePath, elemType, subFields, headerNames, confList);
                    var arr = Array.CreateInstance(elemType, confList.Count);
                    confList.ToArray().CopyTo(arr, 0);
                    field.SetValue(data, arr);
                }
                else if (filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                         filePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
                {
                    ReadExcelFile(filePath, scan, elemType, subFields, data, className, confType);
                }
                else
                {
                    Console.WriteLine($"[Warn] 不支持的文件格式: {filePath}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Error] 解析文件 {filePath} 时出错: {e.Message}");
            }
        }

        // —— MemoryPack 序列化（兼容不同版本签名）——
        byte[] bytes;

        var mpType = typeof(MemoryPack.MemoryPackSerializer);

        // 1) 优先找非泛型：Serialize(Type type, object? value, MemoryPackSerializerOptions? options = null)
        var nonGeneric = mpType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == "Serialize" && !m.IsGenericMethod &&
                m.GetParameters().Length >= 2 &&
                m.GetParameters()[0].ParameterType == typeof(Type));

        if (nonGeneric != null)
        {
            bytes = (byte[])nonGeneric.Invoke(null, new object[] { confType, data, null })!;
        }
        else
        {
            // 2) 回退到泛型 Serialize<T>(T value [ , options? ])
            var gen = mpType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Serialize" && m.IsGenericMethodDefinition);

            var gm = gen.MakeGenericMethod(confType);
            var ps = gm.GetParameters();

            if (ps.Length == 1)
                bytes = (byte[])gm.Invoke(null, new object?[] { data })!;
            else
                bytes = (byte[])gm.Invoke(null, new object?[] { data, null })!;
        }

        File.WriteAllBytes(outBytesFile, bytes);
    }

    // =============== 扫描 & 生成 RacastSet =================
    private static GenCodeContent ScanInput(string inputDir)
    {
        // 注册 .xls 代码页（外部 Program 也做了，这里兜底）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var result = new GenCodeContent { classes = new List<ClassContent>() };

        string[] csvFiles  = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.csv",  SearchOption.AllDirectories) : Array.Empty<string>();
        string[] xlsxFiles = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.xlsx", SearchOption.AllDirectories) : Array.Empty<string>();
        string[] xlsFiles  = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.xls",  SearchOption.AllDirectories) : Array.Empty<string>();

        // CSV
        foreach (var file in csvFiles)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                Delimiter = ",",
                TrimOptions = TrimOptions.None,
                PrepareHeaderForMatch = args => args.Header.Trim(),
                BadDataFound = null
            };

            List<string> propertyNames;
            List<string> propertyTypes;

            using (var reader = new StreamReader(file))
            using (var csv = new CsvReader(reader, config))
            {
                if (!csv.Read() || !csv.ReadHeader())
                {
                    Console.WriteLine($"[Warn] CSV {file} 缺少表头");
                    continue;
                }
                propertyNames = csv.HeaderRecord.ToList();

                if (!csv.Read())
                {
                    Console.WriteLine($"[Warn] CSV {file} 缺少类型行");
                    continue;
                }

                propertyTypes = new List<string>();
                foreach (var header in propertyNames)
                    propertyTypes.Add(csv.GetField(header));
            }

            if (propertyNames.Count != propertyTypes.Count)
            {
                Console.WriteLine($"[Warn] CSV {file} 属性名与类型数量不一致");
                continue;
            }

            string className = ReadConfEditorUtil.ToCamelUpper(Path.GetFileNameWithoutExtension(file));
            var props = new List<GenCodeProp>();
            for (int i = 0; i < propertyNames.Count; i++)
            {
                var propName = ReadConfEditorUtil.ToCamelLower(propertyNames[i]);
                var propType = MapCsvTypeToCSharpType(propertyTypes[i]);
                props.Add(new GenCodeProp { name = propName, propType = propType });
            }

            result.classes.Add(new ClassContent
            {
                className = className,
                fileName  = className,
                FilePath  = file,
                classDef  = "", // 代码生成时再拼
                props     = props,
            });
        }

        // Excel（每个工作表 = 一个类）
        foreach (var file in xlsxFiles.Concat(xlsFiles))
        {
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                string sheetName = reader.Name;
                var propertyNames = new List<string>();
                var propertyTypes = new List<string>();

                bool isHeader = true;
                while (reader.Read())
                {
                    if (isHeader)
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                            propertyNames.Add(reader.GetString(i)?.Trim() ?? string.Empty);
                        isHeader = false;
                    }
                    else
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                            propertyTypes.Add(reader.GetString(i)?.Trim() ?? "string");
                        break;
                    }
                }

                if (propertyNames.Count == 0) continue;
                if (propertyNames.Count != propertyTypes.Count)
                {
                    Console.WriteLine($"[Warn] Excel {file}/{sheetName} 属性名与类型数量不一致");
                    continue;
                }

                string className = ReadConfEditorUtil.ToCamelUpper(sheetName);
                var props = new List<GenCodeProp>();
                for (int i = 0; i < propertyNames.Count; i++)
                {
                    var propName = ReadConfEditorUtil.ToCamelLower(propertyNames[i]);
                    var propType = MapCsvTypeToCSharpType(propertyTypes[i]);
                    props.Add(new GenCodeProp { name = propName, propType = propType });
                }

                result.classes.Add(new ClassContent
                {
                    className = className,
                    fileName  = className,
                    FilePath  = file,
                    classDef  = "",
                    props     = props
                });

            } while (reader.NextResult());
        }

        return result;
    }

    private static Dictionary<string, List<(string className, string classFieldName)>> GroupByFolder(GenCodeContent scan)
    {
        var dict = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in scan.classes)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(c.FilePath)) ?? "";
            string field  = char.ToLower(c.className[0]) + c.className[1..];
            if (!dict.TryGetValue(folder, out var list))
            {
                list = new List<(string, string)>();
                dict[folder] = list;
            }
            list.Add((c.className, field));
        }
        return dict;
    }

    private static void DoGenRacastSet(Dictionary<string, List<(string className, string classFieldName)>> folderClasses,
                                       string outRacastDir)
    {
        Directory.CreateDirectory(outRacastDir);

        foreach (var kv in folderClasses)
        {
            string folderName = string.IsNullOrEmpty(kv.Key) ? "Root" : kv.Key;
            var classes = kv.Value;
            string structName = $"{folderName}RacastSet";

            var sb = new StringBuilder();
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine();
            sb.AppendLine($"public struct {structName} : IRacastSet");
            sb.AppendLine("{");
            foreach (var (className, _) in classes)
                sb.AppendLine($"    public Dictionary<int, {className}> {className}Ct {{ get; private set; }}");
            sb.AppendLine();
            sb.AppendLine($"    public {structName}(ConfData data)");
            sb.AppendLine("    {");
            foreach (var (className, fieldName) in classes)
                sb.AppendLine($"        {className}Ct = data.{fieldName}.ToDictionary(e => e.id);");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            var outPath = Path.Combine(outRacastDir, $"{structName}.cs");
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            Console.WriteLine($"[ConfGen] 生成 {structName}.cs");
        }
    }

    // =============== 读取 CSV / Excel & 类型转换 ===============

    private static void ReadCsvFile(string filePath, Type elemType, FieldInfo[] subFields,
        List<string> headerNames, List<object> confList)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Delimiter = ",",
            TrimOptions = TrimOptions.None,
            PrepareHeaderForMatch = args => args.Header.Trim(),
            BadDataFound = null
        };

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader())
        {
            Console.WriteLine($"[Warn] CSV {filePath} 缺少表头");
            return;
        }

        headerNames.AddRange(csv.HeaderRecord.Select(ReadConfEditorUtil.ToCamelLower));

        if (!csv.Read())
        {
            Console.WriteLine($"[Warn] CSV {filePath} 缺少类型行");
            return;
        }

        while (csv.Read())
        {
            var rowObj = Activator.CreateInstance(elemType)!;
            for (int i = 0; i < subFields.Length; i++)
            {
                try
                {
                    string name = subFields[i].Name;
                    int idx = headerNames.IndexOf(name);
                    if (idx < 0) continue;

                    string value = csv.GetField(idx);
                    object val = ConvertValue(value, subFields[i].FieldType);
                    subFields[i].SetValue(rowObj, val);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] CSV 解析错误: {ex.Message}");
                }
            }
            confList.Add(rowObj);
        }
    }

    private static void ReadExcelFile(string filePath, GenCodeContent scan,
                                      Type elemType, FieldInfo[] subFields, object data,
                                      string currentClassName, Type confDataType)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        do
        {
            string sheetName = reader.Name;
            if (ReadConfEditorUtil.ToCamelUpper(sheetName) != currentClassName)
                continue;

            var field = confDataType.GetField(currentClassName,
                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                Console.WriteLine($"[Warn] ConfData 中未找到字段: {currentClassName}");
                continue;
            }

            var headerNames = new List<string>();
            var confList = new List<object>();

            bool isHeader = true, skippedTypeRow = false;

            while (reader.Read())
            {
                if (isHeader)
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        headerNames.Add(ReadConfEditorUtil.ToCamelLower(reader.GetValue(i)?.ToString()?.Trim() ?? string.Empty));
                    isHeader = false;
                    continue;
                }

                if (!skippedTypeRow)
                {
                    skippedTypeRow = true;
                    continue;
                }

                var rowObj = Activator.CreateInstance(elemType)!;
                for (int i = 0; i < subFields.Length; i++)
                {
                    try
                    {
                        string name = subFields[i].Name;
                        int idx = headerNames.IndexOf(name);
                        if (idx < 0) continue;

                        string? value = reader.IsDBNull(idx) ? null : reader.GetValue(idx)?.ToString();
                        object val = ConvertValue(value, subFields[i].FieldType);
                        subFields[i].SetValue(rowObj, val);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Excel 解析错误: {ex.Message}");
                    }
                }
                confList.Add(rowObj);
            }

            var arr = Array.CreateInstance(elemType, confList.Count);
            confList.ToArray().CopyTo(arr, 0);
            field.SetValue(data, arr);

        } while (reader.NextResult());
    }

    private static object ConvertValue(string? value, Type targetType)
    {
        if (string.IsNullOrWhiteSpace(value)) return GetDefaultValue(targetType);

        try
        {
            if (targetType == typeof(int))    return int.TryParse(value, out var v) ? v : 0;
            if (targetType == typeof(long))   return long.TryParse(value, out var v) ? v : 0L;
            if (targetType == typeof(float))  return float.TryParse(value, out var v) ? v : 0f;
            if (targetType == typeof(double)) return double.TryParse(value, out var v) ? v : 0d;
            if (targetType == typeof(bool))   return bool.TryParse(value, out var v) ? v : false;
            if (targetType == typeof(int[]))      return ParseIntArray(value);
            if (targetType == typeof(List<int>))  return ParseIntList(value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] ConvertValue: {ex.Message}");
        }
        return value!;
    }

    private static object GetDefaultValue(Type t) => t.IsValueType ? Activator.CreateInstance(t)! : null!;

    private static int[] ParseIntArray(string value)
    {
        value = value.Trim();
        if (value.StartsWith("[") && value.EndsWith("]")) value = value[1..^1];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToArray();
    }

    private static List<int> ParseIntList(string value)
    {
        value = value.Trim();
        if (value.StartsWith("[") && value.EndsWith("]")) value = value[1..^1];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToList();
    }

    private static string MapCsvTypeToCSharpType(string csvType) => csvType switch
    {
        "int" => "int",
        "string" => "string",
        "long" => "long",
        "int[]" => "int[]",
        "float" => "float",
        "double" => "double",
        "bool" => "bool",
        "List<int>" => "List<int>",
        _ => "string",
    };

    private static Type? FindType(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t = null;
            try { t = asm.GetType(typeName, throwOnError: false, ignoreCase: false); }
            catch { }
            if (t != null) return t;

            try { t = asm.GetTypes().FirstOrDefault(x => x.Name == typeName); }
            catch { t = null; }
            if (t != null) return t;
        }
        return null;
    }
}

// =============== 下面是你原有的小模型/工具类（改为纯 .NET） ===============

public class GenCodeProp { public string name; public string propType; }

public class ClassContent
{
    public string className;
    public string fileName;
    public string FilePath;
    public string classDef;
    public List<GenCodeProp> props;
}

public class GenCodeContent { public List<ClassContent> classes; }

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

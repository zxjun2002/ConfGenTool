using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using MemoryPack;

public static class ConfGen
{
    // ================== 对外入口 ==================

    // Step1: 生成代码 + RacastSet (+ IRacastSet.cs)
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

        // 生成 RacastSet + IRacastSet
        var folderClasses = GroupByFolder(scan);
        EnsureIRacastSet(outRacastDir);
        DoGenRacastSet(folderClasses, outRacastDir);

        // 可选：落一个简易 manifest（方便排查）
        try
        {
            var manifestPathGuess = Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(outCodeFile)!, "..", "..", "Output", "ConfManifest.json"));
            var toSave = new { classes = scan.classes.Select(c => new { c.className, c.FilePath }).ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPathGuess)!);
            File.WriteAllText(manifestPathGuess,
                JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* 忽略 */ }

        Console.WriteLine("[ConfGen] Code & Racast generated.");
    }

    // Step2（旧入口）：读取表 -> 构造 ConfData -> MemoryPack 序列化（当前 AppDomain 内需能找到 ConfData）
    public static void BuildBinary(string inputDir, string outBytesFile, string outManifestFile)
    {
        var confType =
            AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => SafeGetTypes(a))
            .FirstOrDefault(t => t.Name == "ConfData") ?? throw new Exception("ConfData type not found");

        BuildBinaryCore(inputDir, outBytesFile, outManifestFile, confType);
    }

    // Step2（新入口）：不依赖业务 csproj。临时最小工程仅编译 ConfData.cs -> 加载 DLL -> 生成 .bytes
    public static void BuildBytesWithTempProject(string inputDir, string outBytesFile, string outManifestFile, string confDataCsPath)
    {
        if (!File.Exists(confDataCsPath))
            throw new FileNotFoundException("ConfData.cs not found", confDataCsPath);

        var tempRoot = Path.Combine(Path.GetTempPath(), "yuki-confgen", Guid.NewGuid().ToString("N"));
        var outDir   = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(outDir);

        // 1) 最小 csproj（只编 ConfData.cs，引用 MemoryPack 以启用 Source Generator）
        var csprojPath = Path.Combine(tempRoot, "ConfDataHost.csproj");
        File.WriteAllText(csprojPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="ConfData.cs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="MemoryPack" Version="1.21.4" />
  </ItemGroup>
</Project>
""", new UTF8Encoding(false));

        // 2) 复制 ConfData.cs
        File.Copy(confDataCsPath, Path.Combine(tempRoot, "ConfData.cs"), overwrite: true);

        // 3) dotnet build（非常快）
        var psi = new ProcessStartInfo("dotnet",
            $"build \"{csprojPath}\" -c Release -nologo -clp:DisableConsoleColor -p:UseAppHost=false -o \"{outDir}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        var p = Process.Start(psi)!;
        p.WaitForExit();

        if (p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd();
            var std = p.StandardOutput.ReadToEnd();
            throw new Exception($"Temp build failed (exit {p.ExitCode}).\n{std}\n{err}");
        }

        // 4) 加载临时 DLL，定位 ConfData 类型
        var dll = Directory.GetFiles(outDir, "*.dll").FirstOrDefault()
                  ?? throw new FileNotFoundException("No dll produced in temp build folder.", outDir);

        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        var confType = SafeGetTypes(asm).FirstOrDefault(t => t.Name == "ConfData")
                       ?? throw new Exception("ConfData type not found in temp assembly");

        // 5) 复用核心逻辑
        BuildBinaryCore(inputDir, outBytesFile, outManifestFile, confType);
    }

    // ============== 核心：读表 → 填充 → 序列化（传入 ConfData 的 Type） ==============
    private static void BuildBinaryCore(string inputDir, string outBytesFile, string outManifestFile, Type confType)
    {
        var scan = ScanInput(inputDir); // 与代码生成同一套扫描

        Directory.CreateDirectory(Path.GetDirectoryName(outBytesFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(outManifestFile)!);

        var data = Activator.CreateInstance(confType)!;

        // 逐字段读取
        var fields = confType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var field in fields)
        {
            var elemType  = field.FieldType.GetElementType()!;
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
                var confList    = new List<object>();

                if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    ReadCsvFile(filePath, elemType, subFields, headerNames, confList);
                    var arr = Array.CreateInstance(elemType, confList.Count);
                    for (int i = 0; i < confList.Count; i++) arr.SetValue(confList[i], i);
                    field.SetValue(data, arr);
                }
                else if (filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                         filePath.EndsWith(".xls",  StringComparison.OrdinalIgnoreCase))
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

        var mpType = typeof(MemoryPackSerializer);

        // 优先找：Serialize(Type type, object? value, MemoryPackSerializerOptions? options = null)
        var nonGeneric = mpType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == "Serialize" && !m.IsGenericMethod &&
                m.GetParameters().Length >= 2 &&
                m.GetParameters()[0].ParameterType == typeof(Type));

        if (nonGeneric != null)
        {
            bytes = (byte[])nonGeneric.Invoke(null, new object?[] { confType, data, null })!;
        }
        else
        {
            // 回退：Serialize<T>(T value [, options])
            var gen = mpType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Serialize" && m.IsGenericMethodDefinition);
            var gm = gen.MakeGenericMethod(confType);
            var ps = gm.GetParameters();
            bytes = (byte[])(ps.Length == 1
                ? gm.Invoke(null, new object?[] { data })!
                : gm.Invoke(null, new object?[] { data, null })!);
        }

        File.WriteAllBytes(outBytesFile, bytes);
        File.WriteAllText(outManifestFile, $$"""
        { "timeUtc":"{{DateTime.UtcNow:O}}", "schemaHash":"AUTO", "sheets":[] }
        """, new UTF8Encoding(false));
    }

    // ================= 扫描 & RacastSet 生成 =================

    private static GenCodeContent ScanInput(string inputDir)
    {
        // 支持 .xls
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
                classDef  = "",
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

    private static void EnsureIRacastSet(string outRacastDir)
    {
        var path = Path.Combine(outRacastDir, "IRacastSet.cs");
        const string content = "public interface IRacastSet { }";
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            Console.WriteLine("[ConfGen] 生成 IRacastSet.cs");
        }
    }

    // ================= 读取 CSV / Excel & 类型转换 =================

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
            for (int i = 0; i < confList.Count; i++) arr.SetValue(confList[i], i);
            field.SetValue(data, arr);

        } while (reader.NextResult());
    }

    private static object ConvertValue(string? value, Type targetType)
    {
        if (string.IsNullOrWhiteSpace(value)) return GetDefaultValue(targetType);

        try
        {
            if (targetType == typeof(int))       return int.TryParse(value, out var v) ? v : 0;
            if (targetType == typeof(long))      return long.TryParse(value, out var v) ? v : 0L;
            if (targetType == typeof(float))     return float.TryParse(value, out var v) ? v : 0f;
            if (targetType == typeof(double))    return double.TryParse(value, out var v) ? v : 0d;
            if (targetType == typeof(bool))      return bool.TryParse(value, out var v) ? v : false;
            if (targetType == typeof(int[]))     return ParseIntArray(value);
            if (targetType == typeof(List<int>)) return ParseIntList(value);
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

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch { return Array.Empty<Type>(); }
    }

    // =================== 模型 types ===================

    public class GenCodeProp { public string name = ""; public string propType = "string"; }

    public class ClassContent
    {
        public string className = "";
        public string fileName  = "";
        public string FilePath  = "";
        public string classDef  = "";
        public List<GenCodeProp> props = new();
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

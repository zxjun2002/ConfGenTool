using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using MemoryPack;

public static class ConfGen
{
    /// <summary>
    /// 总开关：是否生成 Manifest（影响 Build 阶段是否写 outManifestFile）
    /// </summary>
    public static bool EmitManifest = true;

    // ================== 对外入口 ==================

    /// <summary>
    /// Step1: 生成代码 + RacastSet (+ IRacastSet.cs)。不写 Manifest。
    /// </summary>
    public static void GenerateCode(string inputDir, string outCodeFile, string outRacastDir)
    {
        var scan = ScanInput(inputDir);

        Directory.CreateDirectory(Path.GetDirectoryName(outCodeFile)!);
        Directory.CreateDirectory(outRacastDir);

        const string tip  = "/// <summary>\n/// 此代码为自动生成, 修改无意义, 重新生成会被覆盖\n/// </summary>\n\n";
        const string head = "using MemoryPack;\nusing System.Collections.Generic;\n\n";

        var sbField = new StringBuilder();
        sbField.AppendLine("[MemoryPackable]");
        sbField.AppendLine("public partial class ConfData");
        sbField.AppendLine("{");

        var sbDef = new StringBuilder();

        // 生成每个表的类 & ConfData 字段
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

        // 生成 RacastSet（含 IRacastSet）
        var folderClasses = GroupByFolder(scan);
        EnsureIRacastSet(outRacastDir);
        DoGenRacastSet(folderClasses, outRacastDir, scan);

        Console.WriteLine("[ConfGen] Code & Racast generated.");
    }

    /// <summary>
    /// Step2（旧入口）：当前 AppDomain 已含 ConfData 类型时，直接 build。
    /// </summary>
    public static void BuildBinary(string inputDir, string outBytesFile, string outManifestFile)
    {
        var confType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(a => SafeGetTypes(a))
            .FirstOrDefault(t => t.Name == "ConfData")
            ?? throw new Exception("ConfData type not found");

        BuildBinaryCore(inputDir, outBytesFile, outManifestFile, confType);
    }

    /// <summary>
    /// Step2（新入口）：不依赖业务 csproj。用临时最小工程仅编译 ConfData.cs -> 加载 DLL -> 生成 .bytes。
    /// </summary>
    public static void BuildBytesWithTempProject(string inputDir, string outBytesFile, string outManifestFile, string confDataCsPath)
    {
        if (!File.Exists(confDataCsPath))
            throw new FileNotFoundException("ConfData.cs not found", confDataCsPath);

        var tempRoot = Path.Combine(Path.GetTempPath(), "yuki-confgen", Guid.NewGuid().ToString("N"));
        var outDir   = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(outDir);

        // 1) 最小 csproj（只编 ConfData.cs，引用 MemoryPack）
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

        // 3) dotnet build
        var psi = new ProcessStartInfo("dotnet",
            $"build \"{csprojPath}\" -c Release -nologo -clp:DisableConsoleColor -p:UseAppHost=false -o \"{outDir}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new Exception($"Temp build failed (exit {proc.ExitCode}).\n{proc.StandardOutput.ReadToEnd()}\n{proc.StandardError.ReadToEnd()}");

        // 4) 加载临时 DLL
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
        var scan = ScanInput(inputDir);

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
            var gen = mpType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .First(m => m.Name == "Serialize" && m.IsGenericMethodDefinition);
            var gm  = gen.MakeGenericMethod(confType);
            var ps  = gm.GetParameters();
            bytes = (byte[])(ps.Length == 1
                ? gm.Invoke(null, new object?[] { data })!
                : gm.Invoke(null, new object?[] { data, null })!);
        }

        File.WriteAllBytes(outBytesFile, bytes);

        // ✅ 仅当开关开启时写 Manifest
        if (EmitManifest)
        {
            File.WriteAllText(outManifestFile, $$"""
            { "timeUtc":"{{DateTime.UtcNow:O}}", "schemaHash":"AUTO", "sheets":[] }
            """, new UTF8Encoding(false));
        }
    }

    // ================= 扫描 & RacastSet 生成 =================

    private static GenCodeContent ScanInput(string inputDir)
    {
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
            List<string> propertyTypes;   // 第3行：类型
            List<int?>   keyPriorities;   // 第2行：优先级

            using (var reader = new StreamReader(file))
            using (var csv = new CsvReader(reader, config))
            {
                // 第1行：列名
                if (!csv.Read() || !csv.ReadHeader())
                {
                    Console.WriteLine($"[Warn] CSV {file} 缺少表头");
                    continue;
                }
                propertyNames = csv.HeaderRecord.ToList();

                // 第2行：优先级
                if (!csv.Read())
                {
                    Console.WriteLine($"[Warn] CSV {file} 缺少优先级行");
                    continue;
                }
                keyPriorities = ParsePriorityRow(propertyNames.Count, i => csv.GetField(i));

                // 第3行：类型
                if (!csv.Read())
                {
                    Console.WriteLine($"[Warn] CSV {file} 缺少类型行");
                    continue;
                }
                propertyTypes = new List<string>(propertyNames.Count);
                for (int i = 0; i < propertyNames.Count; i++)
                    propertyTypes.Add(csv.GetField(i)?.Trim() ?? "string");
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
                className     = className,
                fileName      = className,
                FilePath      = file,
                classDef      = "",
                props         = props,
                keyPriorities = keyPriorities
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
                var propertyTypes = new List<string>(); // 第3行
                var keyPriorities = new List<int?>();   // 第2行

                bool gotHeader = false, gotPriority = false, gotType = false;

                while (reader.Read())
                {
                    if (!gotHeader)
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                            propertyNames.Add(reader.GetString(i)?.Trim() ?? string.Empty);
                        gotHeader = true;
                        continue;
                    }

                    if (!gotPriority)
                    {
                        keyPriorities = ParsePriorityRow(
                            propertyNames.Count,
                            i => reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString()
                        );
                        gotPriority = true;
                        continue;
                    }

                    if (!gotType)
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                            propertyTypes.Add(reader.GetString(i)?.Trim() ?? "string");
                        gotType = true;
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
                    className     = className,
                    fileName      = className,
                    FilePath      = file,
                    classDef      = "",
                    props         = props,
                    keyPriorities = keyPriorities
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

    private static void DoGenRacastSet(
        Dictionary<string, List<(string className, string classFieldName)>> folderClasses,
        string outRacastDir,
        GenCodeContent scan)
    {
        Directory.CreateDirectory(outRacastDir);

        // meta：className -> ClassContent（含优先级）
        var meta = scan.classes.ToDictionary(c => c.className, c => c);

        foreach (var kv in folderClasses)
        {
            string folderName = string.IsNullOrEmpty(kv.Key) ? "Root" : kv.Key;
            var classes = kv.Value;
            string typeName = $"{folderName}RacastSet";

            // 每个 RacastSet 独立子目录
            string setDir = Path.Combine(outRacastDir, typeName);
            Directory.CreateDirectory(setDir);

            var sb = new StringBuilder();
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine();
            sb.AppendLine($"public sealed partial class {typeName} : IRacastSet");
            sb.AppendLine("{");
            // 字段：仅按优先级链生成，字段名固定 {ClassName}Ct
            foreach (var (className, _) in classes)
            {
                var c = meta[className];
                var chain = ExtractKeyChain(c);
                if (chain.Count == 0)
                {
                    sb.AppendLine($"    // {className}: 未配置优先级，未生成索引字段");
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
            Console.WriteLine($"[ConfGen] 生成 {typeName}/{typeName}.cs");

            // 首次生成 Logic 文件（不覆盖）
            var logicPath = Path.Combine(setDir, $"{typeName}.Logic.cs");
            if (!File.Exists(logicPath))
            {
                var sbLogic = new StringBuilder();
                sbLogic.AppendLine("// 自定义扩展：此文件仅首次生成，之后不会被覆盖");
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
                Console.WriteLine($"[ConfGen] 创建 {typeName}/{typeName}.Logic.cs");
            }
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

        // 第1行：表头
        if (!csv.Read() || !csv.ReadHeader())
        {
            Console.WriteLine($"[Warn] CSV {filePath} 缺少表头");
            return;
        }
        headerNames.AddRange(csv.HeaderRecord.Select(ReadConfEditorUtil.ToCamelLower));

        // 第2行：优先级（跳过）
        if (!csv.Read())
        {
            Console.WriteLine($"[Warn] CSV {filePath} 缺少优先级行");
            return;
        }
        // 第3行：类型（跳过）
        if (!csv.Read())
        {
            Console.WriteLine($"[Warn] CSV {filePath} 缺少类型行");
            return;
        }

        // 数据从第4行开始
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

            bool isHeader = true, prioritySkipped = false, typeSkipped = false;

            while (reader.Read())
            {
                if (isHeader)
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        headerNames.Add(ReadConfEditorUtil.ToCamelLower(reader.GetValue(i)?.ToString()?.Trim() ?? string.Empty));
                    isHeader = false;
                    continue;
                }

                if (!prioritySkipped) { prioritySkipped = true; continue; } // 第2行：优先级
                if (!typeSkipped)     { typeSkipped = true;     continue; } // 第3行：类型

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
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToArray();
    }

    private static List<int> ParseIntList(string value)
    {
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToList();
    }

    private static string MapCsvTypeToCSharpType(string csvType) => csvType switch
    {
        "int"       => "int",
        "string"    => "string",
        "long"      => "long",
        "int[]"     => "int[]",
        "float"     => "float",
        "double"    => "double",
        "bool"      => "bool",
        "List<int>" => "List<int>",
        _           => "string",
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
        public List<int?> keyPriorities = new(); // ★ 第二行优先级（null=不参与；1/2/3…）
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

    // ====== 生成器内部使用的小工具 ======

    private static List<int?> ParsePriorityRow(int count, Func<int, string?> getter)
    {
        var pri = new List<int?>(count);
        for (int i = 0; i < count; i++)
        {
            var cell = getter(i);
            if (string.IsNullOrWhiteSpace(cell)) { pri.Add(null); continue; }
            if (int.TryParse(cell.Trim(), out int p) && p > 0) pri.Add(p);
            else pri.Add(null);
        }
        return pri;
    }

    private static List<string> ExtractKeyChain(ClassContent c)
    {
        var result = new List<string>();
        if (c.keyPriorities == null || c.keyPriorities.Count == 0) return result;

        var pairs = c.props.Zip(c.keyPriorities, (p, pr) => (p.name, pr))
                           .Where(t => t.pr.HasValue && t.pr.Value > 0)
                           .OrderBy(t => t.pr!.Value)
                           .ToList();

        foreach (var (name, _) in pairs) result.Add(name);
        return result;
    }

    // 声明：public Dictionary<K1, Dictionary<K2, ... , T>> {ClassName}Ct { get; private set; }
    private static string EmitIndexFieldDeclCt(string className, List<GenCodeProp> props, List<string> keys)
    {
        string KeyType(int i) => props.First(p => p.name == keys[i]).propType;
        string TailType(int i) => (i == keys.Count - 1)
            ? className
            : $"Dictionary<{KeyType(i + 1)}, {TailType(i + 1)}>";

        return $"    public Dictionary<{KeyType(0)}, {TailType(0)}> {className}Ct {{ get; private set; }}";
    }

    // 赋值：{ClassName}Ct = data.field ...（根据 keys 构造嵌套 GroupBy / ToDictionary）
    private static string EmitIndexCtorCt(string className, string fieldName, List<GenCodeProp> props, List<string> keys)
    {
        if (keys.Count == 1)
            return $"        {className}Ct = data.{fieldName}.ToDictionary(e => e.{keys[0]}, e => e);";

        var sb = new StringBuilder();
        sb.Append($"        {className}Ct = data.{fieldName}.GroupBy(e => e.{keys[0]})");
        for (int i = 0; i < keys.Count - 2; i++)
        {
            sb.Append($".ToDictionary(g{i} => g{i}.Key, g{i} => g{i}.GroupBy(e => e.{keys[i + 1]}))");
            sb.AppendLine();
            sb.Append(new string(' ', 8));
        }
        int last = keys.Count - 2;
        sb.Append($".ToDictionary(g{last} => g{last}.Key, g{last} => g{last}.ToDictionary(e => e.{keys.Last()}, e => e));");
        return sb.ToString();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using MemoryPack;

public static class ConfGen
{
    /// <summary>是否生成 Manifest（受 --no-manifest 影响）</summary>
    public static bool EmitManifest = true;
    /// <summary>详细日志（--verbose 开启）。默认 false：仅错误。</summary>
    public static bool Verbose = false;

    private static void Log(string msg)  { if (Verbose) Console.WriteLine(msg); }
    private static void Warn(string msg) { if (Verbose) Console.WriteLine(msg); }
    private static void Err(string msg)  => Console.Error.WriteLine(msg);

    // ================== 对外入口 ==================

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

    public static void BuildBinary(string inputDir, string outBytesFile, string outManifestFile)
    {
        var confType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.Name == "ConfData")
            ?? throw new Exception("ConfData type not found");

        BuildBinaryCore(inputDir, outBytesFile, outManifestFile, confType);
    }

    public static void BuildBytesWithTempProject(string inputDir, string outBytesFile, string outManifestFile, string confDataCsPath)
    {
        if (!File.Exists(confDataCsPath))
            throw new FileNotFoundException("ConfData.cs not found", confDataCsPath);

        var tempRoot = Path.Combine(Path.GetTempPath(), "yuki-confgen", Guid.NewGuid().ToString("N"));
        var outDir   = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(outDir);

        // 最小 csproj（仅编 ConfData.cs，引用 MemoryPack）
        var csprojPath = Path.Combine(tempRoot, "ConfDataHost.csproj");
        File.WriteAllText(csprojPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="ConfData.cs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="MemoryPack" Version="1.21.4" />
  </ItemGroup>
</Project>
""", new UTF8Encoding(false));

        File.Copy(confDataCsPath, Path.Combine(tempRoot, "ConfData.cs"), overwrite: true);

        // 仅错误（抑制警告/普通输出）
        var psi = new ProcessStartInfo("dotnet",
            $"build \"{csprojPath}\" -c Release -nologo -clp:ErrorsOnly -p:UseAppHost=false -o \"{outDir}\"")
        { UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new Exception($"Temp build failed ({proc.ExitCode})");

        var dll = Directory.GetFiles(outDir, "*.dll").FirstOrDefault()
                  ?? throw new FileNotFoundException("No dll produced in temp build folder.", outDir);

        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        var confType = SafeGetTypes(asm).FirstOrDefault(t => t.Name == "ConfData")
                       ?? throw new Exception("ConfData type not found in temp assembly");

        BuildBinaryCore(inputDir, outBytesFile, outManifestFile, confType);
    }

    public static void BuildWithCsproj(string inputDir, string outBytesFile, string outManifestFile, string csprojPath, string configuration)
    {
        if (!File.Exists(csprojPath))
            throw new FileNotFoundException("csproj not found", csprojPath);

        var tempOut  = Path.Combine(Path.GetTempPath(), "yuki-confgen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempOut);

        var buildArgs = $"build \"{csprojPath}\" -c {configuration} -o \"{tempOut}\" -p:UseAppHost=false -nologo -clp:ErrorsOnly";
        Log($"[dotnet {buildArgs}]");
        using var p = Process.Start(new ProcessStartInfo("dotnet", buildArgs) { UseShellExecute = false })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("dotnet build failed");

        var asmPath = Directory.GetFiles(tempOut, "*.dll")
                               .OrderByDescending(File.GetLastWriteTimeUtc)
                               .FirstOrDefault()
                      ?? throw new Exception($"No dll produced in {tempOut}");
        Log($"[load {asmPath}]");
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(asmPath));

        var confType = SafeGetTypes(asm).FirstOrDefault(t => t.Name == "ConfData")
                       ?? throw new Exception("ConfData type not found in compiled assembly");

        BuildBinaryCore(inputDir, outBytesFile, outManifestFile, confType);
    }

    // ================= 核心：读表 → 填充 → 序列化 =================
    private static void BuildBinaryCore(string inputDir, string outBytesFile, string outManifestFile, Type confType)
    {
        var scan = ScanInput(inputDir);

        Directory.CreateDirectory(Path.GetDirectoryName(outBytesFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(outManifestFile)!);

        var data = Activator.CreateInstance(confType)!;

        var fields = confType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var field in fields)
        {
            var elemType  = field.FieldType.GetElementType()!;
            var subFields = elemType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            string className = char.ToUpper(field.Name[0]) + field.Name.Substring(1);

            var cc = scan.classes.FirstOrDefault(c => c.className == className);
            if (cc == null)
            {
                Warn($"[Warn] ClassContent not found: {className}");
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
                    Warn($"[Warn] Unsupported file type: {filePath}");
                }
            }
            catch (Exception e)
            {
                Err($"[Error] Parse failed for {filePath}: {e.Message}");
            }
        }

        var bytes = MpDyn.Serialize(confType, data);
        File.WriteAllBytes(outBytesFile, bytes);

        if (EmitManifest)
        {
            var manifest = $"{{ \"timeUtc\":\"{DateTime.UtcNow:O}\", \"schemaHash\":\"AUTO\", \"sheets\":[] }}";
            File.WriteAllText(outManifestFile, manifest, new UTF8Encoding(false));
        }
    }

    // ================== MemoryPack 动态调用（带缓存） ==================
    private static class MpDyn
    {
        static readonly MethodInfo? NonGenericSerialize =
            typeof(MemoryPackSerializer)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == "Serialize" && !m.IsGenericMethod &&
                m.GetParameters().Length >= 2 &&
                m.GetParameters()[0].ParameterType == typeof(Type));

        static readonly MethodInfo? GenericSerializeDef =
            typeof(MemoryPackSerializer)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Serialize" && m.IsGenericMethodDefinition);

        static readonly ConcurrentDictionary<Type, Func<object, byte[]>> Cache = new();

        public static byte[] Serialize(Type t, object value)
        {
            if (NonGenericSerialize != null)
                return (byte[])NonGenericSerialize.Invoke(null, new object?[] { t, value, null })!;
            var f = Cache.GetOrAdd(t, MakeSerializer);
            return f(value);
        }

        static Func<object, byte[]> MakeSerializer(Type t)
        {
            if (GenericSerializeDef == null)
                throw new MissingMethodException("MemoryPackSerializer.Serialize<T> not found.");

            var gm = GenericSerializeDef.MakeGenericMethod(t);
            var objParam = Expression.Parameter(typeof(object), "obj");
            var call = Expression.Call(
                gm,
                Expression.Convert(objParam, t),
                Expression.Constant(null, typeof(MemoryPackSerializerOptions))
            );
            return Expression.Lambda<Func<object, byte[]>>(call, objParam).Compile();
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
                if (!csv.Read() || !csv.ReadHeader()) { Warn($"[Warn] CSV {file} missing header"); continue; }
                propertyNames = csv.HeaderRecord.Select(ReadConfEditorUtil.ToCamelLower).ToList();

                if (!csv.Read()) { Warn($"[Warn] CSV {file} missing priority row"); continue; }
                keyPriorities = ParsePriorityRow(propertyNames.Count, i => csv.GetField(i));

                if (!csv.Read()) { Warn($"[Warn] CSV {file} missing type row"); continue; }
                propertyTypes = new List<string>(propertyNames.Count);
                for (int i = 0; i < propertyNames.Count; i++)
                    propertyTypes.Add(csv.GetField(i)?.Trim() ?? "string");
            }

            if (propertyNames.Count != propertyTypes.Count)
            {
                Warn($"[Warn] CSV {file} header/type count mismatch");
                continue;
            }

            string className = ReadConfEditorUtil.ToCamelUpper(Path.GetFileNameWithoutExtension(file));
            var props = new List<GenCodeProp>();
            for (int i = 0; i < propertyNames.Count; i++)
                props.Add(new GenCodeProp { name = propertyNames[i], propType = MapCsvTypeToCSharpType(propertyTypes[i]) });

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
                            propertyNames.Add(ReadConfEditorUtil.ToCamelLower(reader.GetString(i)?.Trim() ?? string.Empty));
                        gotHeader = true; continue;
                    }
                    if (!gotPriority)
                    {
                        keyPriorities = ParsePriorityRow(propertyNames.Count, i => reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString());
                        gotPriority = true; continue;
                    }
                    if (!gotType)
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                            propertyTypes.Add(reader.GetString(i)?.Trim() ?? "string");
                        gotType = true; break;
                    }
                }

                if (propertyNames.Count == 0) continue;
                if (propertyNames.Count != propertyTypes.Count)
                {
                    Warn($"[Warn] Excel {file}/{sheetName} header/type count mismatch");
                    continue;
                }

                string className = ReadConfEditorUtil.ToCamelUpper(sheetName);
                var props = new List<GenCodeProp>();
                for (int i = 0; i < propertyNames.Count; i++)
                    props.Add(new GenCodeProp { name = propertyNames[i], propType = MapCsvTypeToCSharpType(propertyTypes[i]) });

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
            if (!dict.TryGetValue(folder, out var list)) { list = new(); dict[folder] = list; }
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
                sbLogic.AppendLine("// Custom extension: generated once and never overwritten");
                sbLogic.AppendLine("using System.Collections.Generic;");
                sbLogic.AppendLine("using System.Linq;");
                sbLogic.AppendLine();
                sbLogic.AppendLine($"public sealed partial class {typeName}");
                sbLogic.AppendLine("{");
                sbLogic.AppendLine("    partial void OnAfterInit()");
                sbLogic.AppendLine("    {");
                sbLogic.AppendLine("        // TODO: build your additional indices/caches here");
                sbLogic.AppendLine("    }");
                sbLogic.AppendLine("}");
                File.WriteAllText(logicPath, sbLogic.ToString(), new UTF8Encoding(false));
                Log($"[ConfGen]      (create once) {Path.GetFullPath(logicPath)}");
            }
        }
    }

    private static void EnsureIRacastSet(string outRacastDir)
    {
        var path = Path.Combine(outRacastDir, "IRacastSet.cs");
        if (!File.Exists(path))
            File.WriteAllText(path, "public interface IRacastSet { }", new UTF8Encoding(false));
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

        if (!csv.Read() || !csv.ReadHeader()) { Warn($"[Warn] CSV {filePath} missing header"); return; }
        headerNames.AddRange(csv.HeaderRecord.Select(ReadConfEditorUtil.ToCamelLower));

        if (!csv.Read()) { Warn($"[Warn] CSV {filePath} missing priority row"); return; }
        if (!csv.Read()) { Warn($"[Warn] CSV {filePath} missing type row"); return; }

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
                    Err($"[Error] CSV parse error: {ex.Message}");
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
                Warn($"[Warn] ConfData field not found: {currentClassName}");
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
                    isHeader = false; continue;
                }
                if (!prioritySkipped) { prioritySkipped = true; continue; }
                if (!typeSkipped)     { typeSkipped = true;     continue; }

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
                        Err($"[Error] Excel parse error: {ex.Message}");
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
            Err($"[Error] ConvertValue: {ex.Message}");
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

    // =================== 模型 ===================
    public class GenCodeProp { public string name = ""; public string propType = "string"; }

    public class ClassContent
    {
        public string className = "";
        public string fileName  = "";
        public string FilePath  = "";
        public string classDef  = "";
        public List<GenCodeProp> props = new();
        public List<int?> keyPriorities = new(); // 第二行优先级
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

    // ====== RacastSet 生成的字符串工具 ======
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
        var pairs = c.props.Zip(c.keyPriorities, (p, pr) => (p.name, pr))
                           .Where(t => t.pr.HasValue && t.pr.Value > 0)
                           .OrderBy(t => t.pr!.Value)
                           .ToList();
        return pairs.Select(x => x.name).ToList();
    }

    private static string EmitIndexFieldDeclCt(string className, List<GenCodeProp> props, List<string> keys)
    {
        string KeyType(int i) => props.First(p => p.name == keys[i]).propType;
        string TailType(int i) => (i == keys.Count - 1)
            ? className
            : $"Dictionary<{KeyType(i + 1)}, {TailType(i + 1)}>";
        return $"    public Dictionary<{KeyType(0)}, {TailType(0)}> {className}Ct {{ get; private set; }}";
    }

    private static string EmitIndexCtorCt(string className, string fieldName, List<GenCodeProp> props, List<string> keys)
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

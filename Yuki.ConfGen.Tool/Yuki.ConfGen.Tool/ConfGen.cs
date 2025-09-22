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
using System.Text.Json;
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

    /// <summary>
    /// build 阶段：不需要 side，直接按已生成的类型读数据并序列化
    /// </summary>
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
        // build 不用 side；这里只拿文件/工作表路径等元信息
        var scan = ScanInput(inputDir, "ALL");
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
                    ReadExcelFile(filePath, elemType, subFields, data, className, confType);
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

    // ================= 扫描（仅表头） & RacastSet 生成 =================
    private static GenCodeContent ScanInput(string inputDir, string side = "ALL")
    {
        side = NormalizeSide(side);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var result = new GenCodeContent { classes = new List<ClassContent>() };

        string[] csvFiles  = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.csv",  SearchOption.AllDirectories) : Array.Empty<string>();
        string[] xlsxFiles = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.xlsx", SearchOption.AllDirectories) : Array.Empty<string>();
        string[] xlsFiles  = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.xls",  SearchOption.AllDirectories) : Array.Empty<string>();

        // ===== CSV：只读首行表头；其它都从 schema.json =====
        foreach (var file in csvFiles)
        {
            // 1) 读首行表头
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                Delimiter = ",",
                TrimOptions = TrimOptions.None,
                PrepareHeaderForMatch = args => args.Header.Trim(),
                BadDataFound = null
            };

            var headerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StreamReader(file))
            using (var csv = new CsvReader(reader, config))
            {
                if (!csv.Read() || !csv.ReadHeader()) { Warn($"[Warn] CSV {file} missing header"); continue; }
                foreach (var h in csv.HeaderRecord)
                    headerSet.Add(ReadConfEditorUtil.ToCamelLower(h));
            }

            // 2) 读 schema.json（表级 + 列级）
            var sp = SchemaPathFor(file);
            if (!File.Exists(sp))
            {
                Warn($"[Warn] Schema not found for CSV: {file}.schema.json . Skip table.");
                continue; // 强约束：必须有 schema
            }

            var ts = ReadJson<TableSchema>(sp) ?? new TableSchema();
            var tableCs = ResolveCsType(ts.table_properties, "ALL");
            // 表级不匹配 => 整张跳过
            if (!MatchesSide(tableCs, side))
            {
                if (Verbose) Log($"[Filter] CSV {Path.GetFileName(file)} table cs={tableCs} side={side} => SKIP");
                continue;
            }

            // 3) 以 schema.columns 的排序（col_sort/声明顺序）为准；列只看“列自身 cs_type”，默认 ALL
            var orderedCols = (ts.columns ?? new()).Select((c, idx) => (c, idx))
                .OrderBy(x => x.c.col_sort ?? x.idx)
                .Select(x => x.c);

            var props = new List<GenCodeProp>();
            var keyPriorities = new List<int?>();

            foreach (var col in orderedCols)
            {
                var nameLower = ReadConfEditorUtil.ToCamelLower(col.col_name ?? "");
                if (!headerSet.Contains(nameLower))
                {
                    if (Verbose) Log($"[Skip] CSV {Path.GetFileName(file)} col '{col.col_name}' not in header");
                    continue;
                }

                var colCs = ResolveCsType(col.col_properties, "ALL");     // 仅看列自身（默认 ALL）
                if (!MatchesSide(colCs, side))
                {
                    if (Verbose) Log($"[Filter] CSV {Path.GetFileName(file)} col '{col.col_name}' cs={colCs} side={side} => EXCLUDE");
                    continue;
                }

                var csharpType = MapCsvTypeToCSharpType(col.value_type);
                props.Add(new GenCodeProp { name = nameLower, propType = csharpType });
                keyPriorities.Add(col.pk_order.HasValue && col.pk_order.Value > 0 ? col.pk_order : null);
            }

            if (props.Count == 0) { Warn($"[Warn] CSV {file} has no columns after filtering."); continue; }

            string className = ReadConfEditorUtil.ToCamelUpper(Path.GetFileNameWithoutExtension(file));
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

        // ===== Excel：每个 sheet 一个类；只读首行表头；其它都从 workbook schema =====
        foreach (var file in xlsxFiles.Concat(xlsFiles))
        {
            // workbook schema
            var sp = SchemaPathFor(file);
            if (!File.Exists(sp))
            {
                Warn($"[Warn] Schema not found for Excel: {file}.schema.json . Skip workbook.");
                continue; // 强约束：必须有 schema
            }
            var wb = ReadJson<WorkbookSchema>(sp) ?? new WorkbookSchema();

            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                string sheetName = reader.Name;

                // 1) 找 sheet schema
                var sheetSch = wb.sheets?.FirstOrDefault(s =>
                    s.sheet_name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
                if (sheetSch == null)
                {
                    Warn($"[Warn] Sheet '{sheetName}' in {file} not found in workbook schema. Skipped.");
                    continue;
                }

                // 2) 读首行表头
                var headerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (reader.Read())
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        headerSet.Add(ReadConfEditorUtil.ToCamelLower(reader.GetValue(i)?.ToString()?.Trim() ?? string.Empty));
                }
                if (headerSet.Count == 0)
                {
                    Warn($"[Warn] Excel {file}/{sheetName} missing header.");
                    continue;
                }

                // 3) 先判 Sheet 侧别；不匹配直接整张跳过
                var sheetCs = ResolveCsType(sheetSch.sheet_properties, "ALL");
                if (!MatchesSide(sheetCs, side))
                {
                    if (Verbose) Log($"[Filter] XLSX {Path.GetFileName(file)}/{sheetName} sheet cs={sheetCs} side={side} => SKIP");
                    continue;
                }

                // 4) 列只看“列自身 cs_type”，默认 ALL
                var orderedCols = (sheetSch.columns ?? new()).Select((c, idx) => (c, idx))
                    .OrderBy(x => x.c.col_sort ?? x.idx)
                    .Select(x => x.c);

                var props = new List<GenCodeProp>();
                var keyPriorities = new List<int?>();

                foreach (var col in orderedCols)
                {
                    var nameLower = ReadConfEditorUtil.ToCamelLower(col.col_name ?? "");
                    if (!headerSet.Contains(nameLower))
                    {
                        if (Verbose) Log($"[Skip] XLSX {Path.GetFileName(file)}/{sheetName} col '{col.col_name}' not in header");
                        continue;
                    }

                    var colCs = ResolveCsType(col.col_properties, "ALL");   // 仅看列自身（默认 ALL）
                    if (!MatchesSide(colCs, side))
                    {
                        if (Verbose) Log($"[Filter] XLSX {Path.GetFileName(file)}/{sheetName} col '{col.col_name}' cs={colCs} side={side} => EXCLUDE");
                        continue;
                    }

                    var csharpType = MapCsvTypeToCSharpType(col.value_type);
                    props.Add(new GenCodeProp { name = nameLower, propType = csharpType });
                    keyPriorities.Add(col.pk_order.HasValue && col.pk_order.Value > 0 ? col.pk_order : null);
                }

                if (props.Count == 0) { Warn($"[Warn] Excel {file}/{sheetName} has no columns after filtering."); continue; }

                string className = ReadConfEditorUtil.ToCamelUpper(sheetName);
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

    // ================= 读取 CSV / Excel 数据（仅跳过表头） =================
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

    private static void ReadExcelFile(string filePath, Type elemType, FieldInfo[] subFields, object data,
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
            bool gotHeader = false;
            List<object> confList = new();

            while (reader.Read())
            {
                if (!gotHeader)
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        headerNames.Add(ReadConfEditorUtil.ToCamelLower(reader.GetValue(i)?.ToString()?.Trim() ?? string.Empty));
                    gotHeader = true;
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

    // ================= 值转换/类型映射 =================
    private static object ConvertValue(string? value, Type targetType)
    {
        if (string.IsNullOrWhiteSpace(value)) return GetDefaultValue(targetType);

        try
        {
            if (targetType == typeof(int))       return int.TryParse(value, out var v) ? v : 0;
            if (targetType == typeof(long))      return long.TryParse(value, out var v) ? v : 0L;
            if (targetType == typeof(float))     return float.TryParse(value, out var v) ? v : 0f;
            if (targetType == typeof(double))    return double.TryParse(value, out var v) ? v : 0d;
            if (targetType == typeof(bool))      return bool.TryParse(value, out var v) && v;
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

    private static string MapCsvTypeToCSharpType(string csvType)
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

    // ====== RacastSet 生成的字符串工具 ======
    private static List<int?> ParsePriorityRow(int count, Func<int, string?> getter)
    {
        // 不再使用旧优先级行（保留兼容）
        var pri = new List<int?>(count);
        for (int i = 0; i < count; i++) pri.Add(null);
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

    static ColumnSchema? FindColSchema(IEnumerable<ColumnSchema> cols, string headerName)
    {
        return cols?.FirstOrDefault(c =>
            headerName.Equals(ReadConfEditorUtil.ToCamelLower(c.col_name), StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals(c.col_name, StringComparison.OrdinalIgnoreCase));
    }
}

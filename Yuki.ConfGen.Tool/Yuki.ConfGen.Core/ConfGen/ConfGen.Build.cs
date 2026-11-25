using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
#if NET6_0_OR_GREATER
using System.Runtime.Loader;
#endif
using System.Text;
using MemoryPack;

public static partial class ConfGen
{
    /// <summary>
    /// build 阶段：不需要 side，直接按已生成的类型读数据并序列化
    /// </summary>
    public static void BuildBinary(string inputDir, string outBytesFile)
    {
        var confType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.Name == "ConfData")
            ?? throw new Exception("ConfData type not found");

        BuildBinaryCore(inputDir, outBytesFile, confType);
    }

#if NET6_0_OR_GREATER
    public static void BuildBytesWithTempProject(string inputDir, string outBytesFile, string confDataCsPath)
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

        BuildBinaryCore(inputDir, outBytesFile, confType);
    }

    public static void BuildWithCsproj(string inputDir, string outBytesFile, string csprojPath, string configuration)
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

        BuildBinaryCore(inputDir, outBytesFile, confType);
    }
#endif

    // ================= 核心：读表 → 填充 → 序列化 =================
    private static void BuildBinaryCore(string inputDir, string outBytesFile, Type confType)
    {
        // build 不用 side；这里只拿文件/工作表路径等元信息
        var scan = ScanInput(inputDir, "ALL");
        Directory.CreateDirectory(Path.GetDirectoryName(outBytesFile)!);

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
        Log($"[ConfGen] Bytes  -> {Path.GetFullPath(outBytesFile)}");
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

        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, byte[]>> Cache = new();

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
            var objParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "obj");
            var call = System.Linq.Expressions.Expression.Call(
                gm,
                System.Linq.Expressions.Expression.Convert(objParam, t),
                System.Linq.Expressions.Expression.Constant(null, typeof(MemoryPackSerializerOptions))
            );
            return System.Linq.Expressions.Expression.Lambda<Func<object, byte[]>>(call, objParam).Compile();
        }
    }
}
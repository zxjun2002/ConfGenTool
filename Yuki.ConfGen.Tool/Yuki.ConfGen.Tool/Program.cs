using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Loader;   // <== 新增
using System.Text;

internal static class Program
{
    static int Main(string[] args)
    {
        // 支持 .xls
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            // 可选开关：--no-manifest
            if (args.Any(a => a.Equals("--no-manifest", StringComparison.OrdinalIgnoreCase)))
                ConfGen.EmitManifest = false;
            
            var cmd  = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            switch (cmd)
            {
                case "gen":
                {
                    var o = GenOptions.Parse(rest);
                    ConfGen.GenerateCode(o.Input, o.OutCs, o.OutRacast);
                    Console.WriteLine("Gen done.");
                    return 0;
                }
                case "build":
                {
                    var o = BuildOptions.Parse(rest);
                    if (!string.IsNullOrEmpty(o.ConfData))
                    {
                        ConfGen.BuildBytesWithTempProject(
                            inputDir:       o.Input,
                            outBytesFile:   o.OutBytes,
                            outManifestFile:o.OutManifest,
                            confDataCsPath: o.ConfData
                        );
                    }
                    else
                    {
                        ConfGen.BuildBinary(o.Input, o.OutBytes, o.OutManifest);
                    }
                    Console.WriteLine("Build done.");
                    return 0;
                }
                case "all":
                {
                    var o = AllOptions.Parse(rest);

                    // 1) 生成代码
                    ConfGen.GenerateCode(o.Input, o.OutCs, o.OutRacast);

                    // 2) 编译到临时目录，且不生成 apphost（只产 DLL，避免覆盖当前正在运行的 EXE）
                    var tempOut  = Path.Combine(Path.GetTempPath(), "yuki-confgen", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempOut);
                    var buildArgs = $"build \"{o.Csproj}\" -c {o.Configuration} -o \"{tempOut}\" -p:UseAppHost=false";
                    Console.WriteLine($"[dotnet {buildArgs}]");
                    var p = Process.Start(new ProcessStartInfo("dotnet", buildArgs) { UseShellExecute = false })!;
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new Exception("dotnet build failed");

                    // 3) 从临时目录加载刚编好的 DLL，让当前进程能看到 ConfData
                    var asmPath = Path.Combine(tempOut, "Yuki.ConfGen.Tool.dll");
                    if (!File.Exists(asmPath))
                    {
                        // 兜底：挑 tempOut 下最后写入的 .dll
                        asmPath = Directory.GetFiles(tempOut, "*.dll")
                                           .OrderByDescending(File.GetLastWriteTimeUtc)
                                           .FirstOrDefault()
                                  ?? throw new Exception($"No dll produced in {tempOut}");
                    }
                    Console.WriteLine($"[load {asmPath}]");
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(asmPath));

                    // 4) 直接生成 .bytes / manifest（BuildBinary 内部会在已加载的程序集里找到 ConfData）
                    ConfGen.BuildBinary(o.Input, o.OutBytes, o.OutManifest);

                    Console.WriteLine("All done.");
                    return 0;
                }
                case "all-local":
                {
                    var o = AllLocalOptions.Parse(rest);

                    // 1) 生成代码（ConfData.cs / Racast）
                    ConfGen.GenerateCode(o.Input, o.OutCs, o.OutRacast);

                    // 2) 本地最小工程编译 ConfData.cs，并生成 bytes（不依赖业务 csproj）
                    ConfGen.BuildBytesWithTempProject(
                        inputDir: o.Input,
                        outBytesFile: o.OutBytes,
                        outManifestFile: o.OutManifest,
                        confDataCsPath: o.OutCs
                    );

                    Console.WriteLine("All-local done.");
                    return 0;
                }
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private const string Usage = 
@"Usage:
  yuki-confgen gen        --input <dir> --out-cs <file> --out-racast <dir>
  yuki-confgen build      --input <dir> --out-bytes <file> --out-manifest <file> [--confdata <ConfData.cs>] [--no-manifest]
  yuki-confgen all        --input <dir> --out-cs <file> --out-racast <dir> --out-bytes <file> --out-manifest <file> --csproj <path-to-csproj> [--configuration Debug|Release] [--no-manifest]
  yuki-confgen all-local  --input <dir> --out-cs <file> --out-racast <dir> --out-bytes <file> --out-manifest <file> [--no-manifest]";
}

internal sealed record GenOptions(string Input, string OutCs, string OutRacast)
{
    public static GenOptions Parse(string[] args) => new(
        Get(args, "--input"),
        Get(args, "--out-cs"),
        Get(args, "--out-racast")
    );

    private static string Get(string[] a, string key)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return a[i + 1];
        throw new ArgumentException($"Missing {key}");
    }
}

internal sealed record BuildOptions(string Input, string OutBytes, string OutManifest, string? ConfData)
{
    public static BuildOptions Parse(string[] args) => new(
        Get(args, "--input"),
        Get(args, "--out-bytes"),
        Get(args, "--out-manifest"),
        GetOrNull(args, "--confdata") // ✅ 新增：可选
    );

    private static string Get(string[] a, string key)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return a[i + 1];
        throw new ArgumentException($"Missing {key}");
    }

    private static string? GetOrNull(string[] a, string key)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return a[i + 1];
        return null;
    }
}

internal sealed record AllOptions(
    string Input, string OutCs, string OutRacast, string OutBytes, string OutManifest,
    string Csproj, string Configuration = "Debug")
{
    public static AllOptions Parse(string[] args) => new(
        Get(args, "--input"),
        Get(args, "--out-cs"),
        Get(args, "--out-racast"),
        Get(args, "--out-bytes"),
        Get(args, "--out-manifest"),
        Get(args, "--csproj"),
        GetOr(args, "--configuration", "Debug")
    );

    private static string Get(string[] a, string key)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return a[i + 1];
        throw new ArgumentException($"Missing {key}");
    }
    private static string GetOr(string[] a, string key, string def)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return a[i + 1];
        return def;
    }
}

internal sealed record AllLocalOptions(string Input, string OutCs, string OutRacast, string OutBytes, string OutManifest)
{
    public static AllLocalOptions Parse(string[] args) => new(
        Get(args, "--input"), Get(args, "--out-cs"), Get(args, "--out-racast"),
        Get(args, "--out-bytes"), Get(args, "--out-manifest"));
    static string Get(string[] a, string key)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
        throw new ArgumentException($"Missing {key}");
    }
}

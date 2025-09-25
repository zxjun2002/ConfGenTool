using System;
using System.IO;
using System.Linq;
using System.Text;

internal static class Program
{
    static int Main(string[] args)
    {
        // 支持 .xls
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 全局默认：详细日志 & 不生成 manifest（通过日志函数体现，未实现 manifest 任何行为）

        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            var cmd  = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            switch (cmd)
            {
                case "gen":
                {
                    var o = GenOptions.Parse(rest);
                    var outCs     = Path.Combine(o.OutDir, "ConfData.cs");
                    var outRacast = Path.Combine(o.OutDir, "RacastSet");

                    Console.WriteLine($"[gen] input={Path.GetFullPath(o.Input)}");
                    Console.WriteLine($"[gen] out-cs={Path.GetFullPath(outCs)}");
                    Console.WriteLine($"[gen] out-racast={Path.GetFullPath(outRacast)}");
                    Console.WriteLine($"[gen] side={o.Side}");

                    ConfGen.GenerateCode(o.Input, outCs, outRacast, o.Side);
                    Console.WriteLine("Gen done.");
                    return 0;
                }

                case "build":
                {
                    var o = BuildOptions.Parse(rest);
                    var outBytes = Path.Combine(o.OutDir, "ConfData.bytes");

                    Console.WriteLine($"[build] input={Path.GetFullPath(o.Input)}");
                    Console.WriteLine($"[build] out-bytes={Path.GetFullPath(outBytes)}");

                    if (!string.IsNullOrEmpty(o.ConfDataDir))
                    {
                        var confDataCs = Path.Combine(o.ConfDataDir!, "ConfData.cs");
                        Console.WriteLine($"[build] ConfData.cs = {Path.GetFullPath(confDataCs)}");
                        ConfGen.BuildBytesWithTempProject(
                            inputDir:        o.Input,
                            outBytesFile:    outBytes,
                            confDataCsPath:  confDataCs
                        );
                    }
                    else if (!string.IsNullOrEmpty(o.Csproj))
                    {
                        Console.WriteLine($"[build] csproj = {Path.GetFullPath(o.Csproj!)} ({o.Configuration})");
                        ConfGen.BuildWithCsproj(
                            inputDir:        o.Input,
                            outBytesFile:    outBytes,
                            csprojPath:      o.Csproj!,
                            configuration:   o.Configuration ?? "Debug"
                        );
                    }
                    else
                    {
                        Console.WriteLine("[build] use ConfData from loaded assemblies (no --confdata-dir / --csproj)");
                        ConfGen.BuildBinary(o.Input, outBytes);
                    }

                    Console.WriteLine("Build done.");
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
  yuki-confgen gen
      --input <dir> --out-dir <dir> [--side C|S|ALL|NONE]

  yuki-confgen build
      --input <dir> --out-dir <dir>
      [--confdata-dir <dir> | --csproj <path-to-csproj> [--configuration Debug|Release]]";
}

internal sealed record GenOptions(string Input, string OutDir, string Side)
{
    public static GenOptions Parse(string[] args) => new(
        Get(args, "--input"),
        Get(args, "--out-dir"),
        GetOr(args, "--side", "ALL") // 传给 ConfGen
    );

    // ----- 本地解析工具（支持 --k v 和 --k=v） -----
    private static string Get(string[] a, string key)
        => TryGet(a, key) ?? throw new ArgumentException($"Missing {key}");
    private static string GetOr(string[] a, string key, string def)
        => TryGet(a, key) ?? def;
    private static string? TryGet(string[] a, string key)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return (i + 1 < a.Length) ? a[i + 1] : null;
            if (a[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return a[i].Substring(key.Length + 1);
        }
        return null;
    }
}

internal sealed record BuildOptions(
    string Input, string OutDir,
    string? ConfDataDir, string? Csproj, string? Configuration)
{
    public static BuildOptions Parse(string[] args) => new(
        Get(args, "--input"),
        Get(args, "--out-dir"),
        GetOrNull(args, "--confdata-dir"),
        GetOrNull(args, "--csproj"),
        GetOr(args, "--configuration", "Debug")
    );

    // ----- 本地解析工具（支持 --k v 和 --k=v） -----
    private static string Get(string[] a, string key)
        => TryGet(a, key) ?? throw new ArgumentException($"Missing {key}");
    private static string? GetOrNull(string[] a, string key)
        => TryGet(a, key);
    private static string GetOr(string[] a, string key, string def)
        => TryGet(a, key) ?? def;
    private static string? TryGet(string[] a, string key)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return (i + 1 < a.Length) ? a[i + 1] : null;
            if (a[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return a[i].Substring(key.Length + 1);
        }
        return null;
    }
}
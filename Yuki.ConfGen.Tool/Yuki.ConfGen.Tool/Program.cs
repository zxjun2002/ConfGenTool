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

        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            // 全局开关：--no-manifest
            if (args.Any(a => a.Equals("--no-manifest", StringComparison.OrdinalIgnoreCase)))
                ConfGen.EmitManifest = false;

            // 详细日志：--verbose
            if (args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)))
                ConfGen.Verbose = true;

            var cmd  = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            switch (cmd)
            {
                case "gen":
                {
                    var o = GenOptions.Parse(rest);
                    ConfGen.GenerateCode(o.Input, o.OutCs, o.OutRacast);
                    if (ConfGen.Verbose) Console.WriteLine("Gen done.");
                    return 0;
                }

                case "build":
                {
                    var o = BuildOptions.Parse(rest);

                    if (!string.IsNullOrEmpty(o.ConfData))
                    {
                        if (ConfGen.Verbose) Console.WriteLine($"[build] ConfData.cs = {Path.GetFullPath(o.ConfData!)}");
                        ConfGen.BuildBytesWithTempProject(
                            inputDir:        o.Input,
                            outBytesFile:    o.OutBytes,
                            outManifestFile: o.OutManifest,
                            confDataCsPath:  o.ConfData!
                        );
                    }
                    else if (!string.IsNullOrEmpty(o.Csproj))
                    {
                        if (ConfGen.Verbose) Console.WriteLine($"[build] csproj = {Path.GetFullPath(o.Csproj!)} ({o.Configuration})");
                        ConfGen.BuildWithCsproj(
                            inputDir:        o.Input,
                            outBytesFile:    o.OutBytes,
                            outManifestFile: o.OutManifest,
                            csprojPath:      o.Csproj!,
                            configuration:   o.Configuration ?? "Debug"
                        );
                    }
                    else
                    {
                        if (ConfGen.Verbose) Console.WriteLine("[build] use ConfData from loaded assemblies (no --confdata / --csproj)");
                        ConfGen.BuildBinary(o.Input, o.OutBytes, o.OutManifest);
                    }

                    if (ConfGen.Verbose) Console.WriteLine("Build done.");
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
      --input <dir> --out-cs <file> --out-racast <dir> [--verbose] [--no-manifest]

  yuki-confgen build
      --input <dir> --out-bytes <file> --out-manifest <file>
      [--confdata <ConfData.cs> | --csproj <path-to-csproj> [--configuration Debug|Release]]
      [--verbose] [--no-manifest]";
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

internal sealed record BuildOptions(
    string Input, string OutBytes, string OutManifest,
    string? ConfData, string? Csproj, string? Configuration)
{
    public static BuildOptions Parse(string[] args) => new(
        Get(args, "--input"),
        Get(args, "--out-bytes"),
        Get(args, "--out-manifest"),
        GetOrNull(args, "--confdata"),
        GetOrNull(args, "--csproj"),
        GetOr(args, "--configuration", "Debug")
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
    private static string GetOr(string[] a, string key, string def)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return a[i + 1];
        return def;
    }
}

using System;

public static partial class ConfGen
{
    // 始终详细日志：直接输出到 Console
    private static void Log(string msg)  { Console.WriteLine(msg); }
    private static void Warn(string msg) { Console.WriteLine(msg); }
    private static void Err(string msg)  => Console.Error.WriteLine(msg);
}
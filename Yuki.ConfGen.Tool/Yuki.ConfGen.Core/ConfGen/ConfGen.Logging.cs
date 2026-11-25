using System;

public static partial class ConfGen
{
    /// <summary>
    /// 可选日志回调（Unity Editor 环境下可绑定到 Debug.Log / LogWarning / LogError）
    /// </summary>
    public static Action<string>? OnLog { get; set; }
    public static Action<string>? OnWarn { get; set; }
    public static Action<string>? OnError { get; set; }

    private static void Log(string msg)
    {
        if (OnLog != null) OnLog(msg);
        else Console.WriteLine(msg);
    }

    private static void Warn(string msg)
    {
        if (OnWarn != null) OnWarn(msg);
        else Console.WriteLine(msg);
    }

    private static void Err(string msg)
    {
        if (OnError != null) OnError(msg);
        else Console.Error.WriteLine(msg);
    }
}

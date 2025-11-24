using System;
using System.Diagnostics;

public static class DebugUtil
{
    [Conditional("DEBUG")]
    public static void DebugLog(string message)
    {
        Console.WriteLine($"[DEBUG] {message}");
    }
}

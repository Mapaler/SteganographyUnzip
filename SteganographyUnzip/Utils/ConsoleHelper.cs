using System;
using System.Diagnostics;

namespace SteganographyUnzip;

public static class ConsoleHelper
{
    // 核心：仅用于 stdout 的彩色输出
    public static void WriteLineStyled(params (string text, ConsoleColor? fg, ConsoleColor? bg)[] segments)
    {
        if (segments == null || segments.Length == 0)
        {
            Console.WriteLine();
            return;
        }

        var origFg = Console.ForegroundColor;
        var origBg = Console.BackgroundColor;

        try
        {
            foreach (var (text, fg, bg) in segments)
            {
                if (fg.HasValue)
                    Console.ForegroundColor = fg.Value;
                if (bg.HasValue)
                    Console.BackgroundColor = bg.Value;
                Console.Write(text);
            }
            Console.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = origFg;
            Console.BackgroundColor = origBg;
        }
    }

    // ========== 日志方法 ==========

    public static void Info(string message)
        => WriteLineStyled(
            ($"[{DateTime.Now:HH:mm:ss}] ", ConsoleColor.DarkGray, null),
            ("[信息] ", ConsoleColor.Gray, null),
            (message, null, null));

    public static void Success(string message)
        => WriteLineStyled(
            ($"[{DateTime.Now:HH:mm:ss}] ", ConsoleColor.DarkGray, null),
            ("[成功] ", ConsoleColor.Green, null),
            (message, null, null));

    public static void Warning(string message)
        => WriteLineStyled(
            ($"[{DateTime.Now:HH:mm:ss}] ", ConsoleColor.DarkGray, null),
            ("[警告] ", ConsoleColor.Yellow, null),
            (message, null, null));

    // ❗ 错误信息走 stderr，不带颜色（保证 CLI 行为正确）
    public static void Error(string message)
        => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] [错误] {message}");

    // Debug 走 stdout（带颜色，仅 DEBUG 模式）
    [Conditional("DEBUG")]
    public static void Debug(string message)
        => WriteLineStyled(
            ("[DEBUG] ", ConsoleColor.Red, ConsoleColor.White),
            ($" {message}", ConsoleColor.White, ConsoleColor.Black));
}

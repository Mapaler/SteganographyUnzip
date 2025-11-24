using System;

namespace SteganographyUnzip
{
	public static class ConsoleHelper
    {
        // 原版
        public static void WriteLine(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }
        // 带背景色版本
        public static void WriteColorLine(ConsoleColor foreground, ConsoleColor background, string format, params object[] args)
		{
			// 保存原始颜色
			ConsoleColor originalFg = Console.ForegroundColor;
			ConsoleColor originalBg = Console.BackgroundColor;
			
			try
			{
				// 设置新颜色
				Console.ForegroundColor = foreground;
				Console.BackgroundColor = background;
				
				// 格式化并输出文本
				Console.WriteLine(format, args);
			}
			finally
			{
				// 恢复原始颜色
				Console.ForegroundColor = originalFg;
				Console.BackgroundColor = originalBg;
			}
		}
		// 只有前景色重载
		public static void WriteColorLine(ConsoleColor foreground, string format, params object[] args)
		{
			// 保存原始颜色
			ConsoleColor originalBg = Console.BackgroundColor;
			WriteColorLine(foreground, originalBg, format, args);
		}
		// 无参数的简单版本重载
		public static void WriteColorLine(ConsoleColor foreground, string message)
		{
			WriteColorLine(foreground, "{0}", message);
        }

        // 带背景色版本
        public static void ErrorWriteColorLine(ConsoleColor foreground, ConsoleColor background, string format, params object[] args)
        {
            // 保存原始颜色
            ConsoleColor originalFg = Console.ForegroundColor;
            ConsoleColor originalBg = Console.BackgroundColor;

            try
            {
                // 设置新颜色
                Console.ForegroundColor = foreground;
                Console.BackgroundColor = background;

                // 格式化并输出文本
                Console.Error.WriteLine(format, args);
            }
            finally
            {
                // 恢复原始颜色
                Console.ForegroundColor = originalFg;
                Console.BackgroundColor = originalBg;
            }
        }
        // 只有前景色重载
        public static void ErrorWriteColorLine(ConsoleColor foreground, string format, params object[] args)
        {
            // 保存原始颜色
            ConsoleColor originalBg = Console.BackgroundColor;
            ErrorWriteColorLine(foreground, originalBg, format, args);
        }
        // 无参数的简单版本重载
        public static void ErrorWriteColorLine(ConsoleColor foreground, string message)
        {
            ErrorWriteColorLine(foreground, "{0}", message);
        }
        // 带格式书写错误
        public static void WriteErrorType(ConsoleColor foreground, ConsoleColor background, string message, string errorType = "ERROR", params object[] args)
        {
            ErrorWriteColorLine(foreground, background, $"[{DateTime.Now:HH:mm:ss}] [{errorType}] {message}", args);
        }
        // 直接写红色的错误，带时间的
        public static void WriteError(string message, params object[] args)
        {
            WriteErrorType(ConsoleColor.Red, Console.BackgroundColor, message, "错误", args);
        }
        // 直接写黄色的警告，带时间的
        public static void WriteWarning(string message, params object[] args)
        {
            WriteErrorType(ConsoleColor.Yellow, Console.BackgroundColor, message, "警告", args);
        }
    }
}

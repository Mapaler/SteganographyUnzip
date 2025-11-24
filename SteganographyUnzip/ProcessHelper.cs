// ProcessHelper.cs
using System.Diagnostics;
using System.Text;
using static DebugUtil;

namespace SteganographyUnzip;

public static class ProcessHelper
{
    public static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        string fileName,
        string arguments,
        bool showOutput = true,
        CancellationToken ct = default)
    {
        DebugLog($"执行命令: {fileName} {arguments}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // 👈 关键：显式重定向 stdin
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                if (showOutput)
                    Console.WriteLine(e.Data);
                outputBuilder.AppendLine(e.Data);
            }
        }

        void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                if (showOutput)
                    Console.Error.WriteLine(e.Data);
                errorBuilder.AppendLine(e.Data);
            }
        }

        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;

        process.Start();

        // 👇 关键：立即关闭 stdin，防止程序等待输入（如密码错误时）
        process.StandardInput.Close();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        process.WaitForExit(); // 确保缓冲区读完

        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }
}

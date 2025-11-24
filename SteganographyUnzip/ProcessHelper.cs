// ProcessHelper.cs
using System.Diagnostics;
using System.Text;
using SteganographyUnzip;

namespace SteganographyUnzip;

public static class ProcessHelper
{
    private const string Separator = "──────────────────────────────────────";

    public static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        string fileName,
        string arguments,
        bool showOutput = true,
        CancellationToken ct = default)
    {
        // 使用 ConsoleHelper.Debug 替代原 ConsoleHelper.Debug（带条件编译）
        ConsoleHelper.Debug($"执行命令: {fileName} {arguments}");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromMinutes(2)); // ⏱️ 2分钟超时

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
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        if (showOutput)
        {
            // ===== 显示外部程序输出开始 =====
            Console.WriteLine(Separator);
            Console.WriteLine($"[外部程序] {Path.GetFileName(fileName)} {arguments}");
            Console.WriteLine(Separator);
        }

        void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                if (showOutput)
                {
                    Console.WriteLine(e.Data); // stdout → Console.Out
                }
                outputBuilder.AppendLine(e.Data);
            }
        }

        void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                if (showOutput)
                {
                    Console.Error.WriteLine(e.Data); // stderr → Console.Error
                }
                errorBuilder.AppendLine(e.Data);
            }
        }

        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;

        process.Start();
        process.StandardInput.Close(); // 防止等待输入
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new TimeoutException("命令执行超时");
        }

        process.WaitForExit();

        if (showOutput)
        {
            // ===== 显示外部程序输出结束 =====
            Console.WriteLine(Separator);
        }

        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }
}

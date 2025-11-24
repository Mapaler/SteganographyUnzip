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

        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }
}

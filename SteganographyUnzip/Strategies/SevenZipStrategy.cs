// SevenZipStrategy.cs
using System.Diagnostics;
using System.Text;

namespace SteganographyUnzip;

public class SevenZipStrategy : IExtractorStrategy
{
    public ExtractorType Type => ExtractorType.SevenZip;

    public string BuildExtractArguments(FileInfo archive, DirectoryInfo outputDir, string password)
    {
        var args = new StringBuilder();
        args.Append("x");
        args.AppendFormat(" \"{0}\"", archive.FullName);
        args.AppendFormat(" -o\"{0}\"", outputDir.FullName);
        if (!string.IsNullOrEmpty(password))
            args.AppendFormat(" -p\"{0}\"", password);
        args.Append(" -y");
        return args.ToString();
    }

    public async Task<List<string>> ListContentsAsync(
        FileInfo archive,
        string commandName,
        IReadOnlyList<string> candidatePasswords,
        CancellationToken ct)
    {
        // 1. 先尝试无密码 list
        try
        {
            string normalArgs = $"l \"{archive.FullName}\"";
            var (exitCode, output, error) = await ProcessHelper.ExecuteAsync(commandName, normalArgs, showOutput: false, ct);
            if (exitCode == 0)
            {
                return ArchiveContentParser.ParseSevenZipNormal(output);
            }
            else if (IsPasswordRequiredFromOutput(error))
            {
                // 继续尝试密码
            }
            else
            {
                throw new InvalidOperationException($"7-Zip 列表失败: {error}");
            }
        }
        catch (Exception ex) when (IsPasswordRequired(ex))
        {
            // 兼容旧异常判断（可选）
        }

        // 2. 尝试候选密码
        var allCandidates = new List<string>(candidatePasswords ?? Enumerable.Empty<string>())
    {
        string.Empty
    }.Distinct().ToList();

        foreach (string pwd in allCandidates)
        {
            try
            {
                string pwdDisplay = string.IsNullOrEmpty(pwd) ? "(空)" : pwd;
                Console.WriteLine($"🔍 List 尝试密码: {pwdDisplay}");

                string args = $"l -p\"{pwd}\" \"{archive.FullName}\"";
                var (exitCode, output, error) = await ProcessHelper.ExecuteAsync(commandName, args, showOutput: false, ct);

                if (exitCode == 0)
                {
                    var files = ArchiveContentParser.ParseSevenZipNormal(output);
                    if (files.Count > 0 || output.Contains("Listing archive"))
                    {
                        return files;
                    }
                }

                if (IsWrongPasswordFromOutput(error))
                {
                    continue;
                }

                throw new InvalidOperationException($"7-Zip 列表出错: {error}");
            }
            catch (Exception ex)
            {
                if (IsWrongPassword(ex))
                {
                    continue;
                }
                throw;
            }
        }

        throw new InvalidOperationException("无法列出压缩包内容：所有密码均无效");
    }

    // 兼容旧接口（如果你其他地方还在用）
    public Task<List<string>> ListContentsAsync(FileInfo archive, string commandName, CancellationToken ct)
        => throw new NotSupportedException("请使用带 candidatePasswords 的重载");

    private static bool IsPasswordRequired(Exception ex)
    {
        string msg = ex.Message;
        return msg.Contains("Enter password") ||
               msg.Contains("Cannot open encrypted archive") ||
               msg.Contains("Headers Error") ||
               msg.Contains("Wrong password");
    }

    private static bool IsWrongPassword(Exception ex)
    {
        string msg = ex.Message;
        return msg.Contains("Wrong password") ||
               msg.Contains("Cannot open encrypted archive") ||
               msg.Contains("Headers Error");
    }

    private static bool IsPasswordRequiredFromOutput(string error)
    {
        return error.Contains("Enter password", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Cannot open encrypted archive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWrongPasswordFromOutput(string error)
    {
        return error.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Headers Error", StringComparison.OrdinalIgnoreCase);
    }
}

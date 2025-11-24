// BandizipStrategy.cs
using System.Diagnostics;
using System.Text;

namespace SteganographyUnzip;

public class BandizipStrategy : IExtractorStrategy
{
    public ExtractorType Type => ExtractorType.Bandizip;

    public string BuildExtractArguments(FileInfo archive, DirectoryInfo outputDir, string password)
    {
        var args = new StringBuilder("x");
        if (!string.IsNullOrEmpty(password))
            args.AppendFormat(" -p:{0}", password); // 注意 bz 的语法是 -p:密码（无引号）
        args.AppendFormat(" -o:\"{0}\"", outputDir.FullName);
        args.Append(" -y");
        args.AppendFormat(" \"{0}\"", archive.FullName);
        return args.ToString();
    }

    public async Task<List<string>> ListContentsAsync(
        FileInfo archive,
        string commandName,
        IReadOnlyList<string> candidatePasswords,
        CancellationToken ct)
    {
        // 1. 先尝试无密码
        try
        {
            string args = $"l \"{archive.FullName}\"";
            var (exitCode, output, error) = await ProcessHelper.ExecuteAsync(commandName, args, showOutput: false, ct);
            if (exitCode == 0)
            {
                return ArchiveContentParser.ParseBandizip(output);
            }
            else if (IsPasswordRequiredFromOutput(error))
            {
                // continue
            }
            else
            {
                throw new InvalidOperationException($"Bandizip 列表失败: {error}");
            }
        }
        catch (Exception ex) when (IsPasswordRequired(ex))
        {
            // fallback
        }

        // 2. 尝试密码
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

                string safePwd = pwd.Replace("\"", "\"\"");
                string args = $"l -p:{safePwd} \"{archive.FullName}\"";

                var (exitCode, output, error) = await ProcessHelper.ExecuteAsync(commandName, args, showOutput: false, ct);

                if (exitCode == 0)
                {
                    var files = ArchiveContentParser.ParseBandizip(output);
                    if (files.Count > 0 || output.Contains("Listing archive"))
                    {
                        return files;
                    }
                }

                if (IsWrongPasswordFromOutput(error))
                {
                    continue;
                }

                throw new InvalidOperationException($"Bandizip 列表出错: {error}");
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

    // 兼容旧接口（可选，建议移除或标记 Obsolete）
    public Task<List<string>> ListContentsAsync(FileInfo archive, string commandName, CancellationToken ct)
        => throw new NotSupportedException("请使用带 candidatePasswords 的重载");

    private static bool IsPasswordRequired(Exception ex)
    {
        string msg = ex.Message;
        return msg.Contains("Enter password") ||
               msg.Contains("Invalid password") ||
               msg.Contains("User break");
    }

    private static bool IsWrongPassword(Exception ex)
    {
        string msg = ex.Message;
        return msg.Contains("Invalid password");
    }

    private static bool IsPasswordRequiredFromOutput(string error) =>
    error.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
    error.Contains("encrypted", StringComparison.OrdinalIgnoreCase);

    private static bool IsWrongPasswordFromOutput(string error) =>
        error.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Invalid password", StringComparison.OrdinalIgnoreCase);
}

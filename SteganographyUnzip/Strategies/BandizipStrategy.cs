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
            args.AppendFormat(" -p:\"{0}\"", password); // Bandizip 语法：-p:密码（无引号）
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
        // 尝试每个候选密码（包括空密码）
        foreach (string pwd in candidatePasswords)
        {
            try
            {
                string pwdDisplay = string.IsNullOrEmpty(pwd) ? "(空)" : pwd;
                Console.WriteLine($"🔍 List 尝试密码: {pwdDisplay}");

                string safePwd = pwd.Replace("\"", "\"\"");
                string args = $"l -p:\"{safePwd}\" \"{archive.FullName}\"";

                var (exitCode, output, error) = await ProcessHelper.ExecuteAsync(commandName, args, showOutput: false, ct);

                if (exitCode == 0)
                {
                    var files = ArchiveContentParser.ParseBandizip(output);
                    return files;
                }

                // 密码错误，继续尝试
                if (IsWrongPasswordFromOutput(error))
                {
                    continue;
                }

                // 其他错误直接抛出
                throw new InvalidOperationException($"Bandizip 列表失败 ({exitCode}): {error.Trim()}");
            }
            catch (Exception ex) when (IsWrongPassword(ex))
            {
                continue;
            }
        }

        throw new InvalidOperationException("无法列出压缩包内容：所有密码均无效");
    }

    private static bool IsWrongPassword(Exception ex)
    {
        string msg = ex.Message;
        return msg.Contains("Invalid password");
    }

    private static bool IsWrongPasswordFromOutput(string error) =>
        error.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Invalid password", StringComparison.OrdinalIgnoreCase);
}

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
        if (!string.IsNullOrEmpty(password))
            args.AppendFormat(" -p\"{0}\"", password);
        args.AppendFormat(" -o\"{0}\"", outputDir.FullName);
        args.AppendFormat(" \"{0}\"", archive.FullName);
        args.Append(" -y");
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

                string args = $"l -p\"{pwd}\" \"{archive.FullName}\"";

                var (exitCode, output, error) = await ProcessHelper.ExecuteAsync(commandName, args, showOutput: false, ct);

                if (exitCode == 0)
                {
                    var files = ArchiveContentParser.ParseSevenZipNormal(output);
                    // 即使文件列表为空，只要命令成功就认为有效
                    return files;
                }

                // 明确的密码错误，继续尝试下一个
                if (IsWrongPasswordFromOutput(error))
                {
                    continue;
                }

                // 其他错误（如格式不支持、损坏等）直接抛出
                throw new InvalidOperationException($"7-Zip 列表失败 ({exitCode}): {error.Trim()}");
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
        return msg.Contains("Wrong password") ||
               msg.Contains("Cannot open encrypted archive") ||
               msg.Contains("Headers Error");
    }

    private static bool IsWrongPasswordFromOutput(string error)
    {
        return error.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Headers Error", StringComparison.OrdinalIgnoreCase);
    }
}

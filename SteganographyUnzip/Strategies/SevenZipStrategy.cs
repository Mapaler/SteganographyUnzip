// SevenZipStrategy.cs
using System.Diagnostics;
using System.Text;
namespace SteganographyUnzip;

public class SevenZipStrategy : IExtractorStrategy
{
    public ExtractorType Type => ExtractorType.SevenZip;
    private string? _targetFileNameForHashMode;

    public async Task<List<string>> ListContentsAsync(
        FileInfo archive,
        string commandName,
        IReadOnlyList<string> candidatePasswords,
        CancellationToken ct)
    {
        // === Step 1: 尝试普通模式 ===
        foreach (var password in candidatePasswords)
        {
            var args = $"l -p\"{password}\" \"{archive.FullName}\"";
            var (exitCode, output, error) = await ProcessHelper.ExecuteAsync(commandName, args, showOutput: false, ct: ct);

            if (exitCode == 0)
            {
                _targetFileNameForHashMode = null; // 不是 -t# 模式
                return ArchiveContentParser.ParseSevenZipNormal(output);
            }

            string fullError = (error + "\n" + output).ToLowerInvariant();
            if (fullError.Contains("cannot open the file as archive") ||
                fullError.Contains("is not supported archive"))
            {
                // 可能是隐写，继续尝试 -t#
            }
            else if (IsWrongPasswordFromOutput(error))
            {
                continue;
            }
            else
            {
                throw new InvalidOperationException($"7-Zip 列表失败 ({exitCode}): {error.Trim()}");
            }
        }

        // === Step 2: 尝试 -t# 模式 ===
        foreach (var password in candidatePasswords)
        {
            var args = $"l -t# -p\"{password}\" \"{archive.FullName}\"";
            var (exitCode, output, error) = await ProcessHelper.ExecuteAsync(commandName, args, showOutput: false, ct: ct);

            if (exitCode == 0)
            {
                // 解析 -t# 输出，找出有效的压缩文件（.zip, .7z, .rar 等）
                var hashFiles = ParseSevenZipHashMode(output);
                var target = hashFiles.FirstOrDefault(f => IsCompressedFile(f));

                if (target != null)
                {
                    _targetFileNameForHashMode = target; // 缓存目标文件名
                    // 返回一个非空列表，让上层认为“可解压”
                    // 内容可以是虚拟的，因为 Extract 会用自己的逻辑
                    return new List<string> { target };
                }
                else
                {
                    throw new InvalidOperationException($"[NOT_ARCHIVE] 文件不包含可解压内容: \"{archive.Name}\"");
                }
            }

            if (IsWrongPasswordFromOutput(error))
            {
                continue;
            }
        }

        throw new InvalidOperationException($"7-Zip 无法识别或密码错误: {archive.Name}");
    }

    public string BuildExtractArguments(FileInfo archive, DirectoryInfo outputDir, string password)
    {
        var args = new StringBuilder();
        args.Append("x");

        // 关键：如果是 -t# 模式，必须加 -t# 和 -i!
        if (_targetFileNameForHashMode != null)
        {
            args.Append(" -t#");
            args.AppendFormat(" -i!\"{0}\"", _targetFileNameForHashMode);
        }

        if (!string.IsNullOrEmpty(password))
            args.AppendFormat(" -p\"{0}\"", password);

        args.AppendFormat(" -o\"{0}\"", outputDir.FullName);
        args.Append(" -y");
        args.AppendFormat(" \"{0}\"", archive.FullName);

        return args.ToString();
    }
    // 新增：解析 -t# 模式的输出
    private static List<string> ParseSevenZipHashMode(string output)
    {
        var files = new List<string>();
        bool inListing = false;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("-----"))
            {
                inListing = true;
                continue;
            }
            if (!inListing || string.IsNullOrEmpty(trimmed))
                continue;

            // 跳过合计行
            if (trimmed.StartsWith(" "))
                continue;

            // 文件名是最后一列
            var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                var name = parts[^1];
                if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                {
                    files.Add(name);
                }
            }
        }
        return files;
    }

    // 新增：判断是否是压缩文件
    private static bool IsCompressedFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz";
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

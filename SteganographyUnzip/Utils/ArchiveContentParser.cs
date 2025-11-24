// ArchiveContentParser.cs
using System.Text.RegularExpressions;

namespace SteganographyUnzip;

public static class ArchiveContentParser
{
    // === Bandizip 解析 ===
    public static List<string> ParseBandizip(string output)
    {
        var files = new List<string>();
        using var reader = new StringReader(output);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            // 跳过标题/合计行
            if (line.Contains("files,") || line.Contains("folders") || line.Trim().Length == 0)
                continue;

            // 尝试解析日期（YYYY-MM-DD）
            if (Regex.IsMatch(line, @"^\d{4}-\d{2}-\d{2}"))
            {
                // 分割空格，取第6个及之后的部分作为文件名
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 6)
                {
                    string name = string.Join(" ", parts.Skip(5));
                    files.Add(name);
                }
            }
        }
        return files;
    }

    // === 7-Zip 正常模式解析 ===
    public static List<string> ParseSevenZipNormal(string output)
    {
        var files = new List<string>();
        using var reader = new StringReader(output);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains("files") || line.Trim().Length == 0)
                continue;

            if (Regex.IsMatch(line, @"^\d{4}-\d{2}-\d{2}"))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 6)
                {
                    string name = string.Join(" ", parts.Skip(5));
                    files.Add(name);
                }
            }
        }
        return files;
    }

    // === 7-Zip -t# raw 模式解析 ===
    public static List<string> ParseSevenZipRaw(string output)
    {
        var files = new List<string>();
        using var reader = new StringReader(output);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                string lastPart = parts[^1];
                // 接受纯数字（如 "1"）或带扩展名的（如 "2.zip"）
                if (lastPart.All(char.IsDigit) || lastPart.Contains('.'))
                {
                    files.Add(lastPart);
                }
            }
        }
        return files;
    }
}

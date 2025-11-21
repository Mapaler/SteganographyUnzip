using System.Diagnostics;
using System.IO;

namespace SteganographyUnzip;

public readonly record struct ExtractorInfo(string CommandName, ExtractorType Type);

public static class ExtractorDetector
{
    private static readonly (string name, ExtractorType type)[] Candidates =
    {
        ("bz", ExtractorType.Bandizip),
        ("7z", ExtractorType.SevenZip),
        ("7za", ExtractorType.SevenZip),
        ("NanaZipC", ExtractorType.SevenZip) // 显式支持 NanaZipC
    };

    /// <summary>
    /// 解析用户指定的解压工具（可为命令名或完整路径）
    /// </summary>
    public static ExtractorInfo ResolveExtractor(string? userSpecified = null)
    {
        // 情况1: 未指定 → 自动探测
        if (string.IsNullOrWhiteSpace(userSpecified))
        {
            return DetectExtractor();
        }

        string input = userSpecified.Trim();

        // 情况2: 是绝对路径（含盘符或以 \ 开头）
        if (Path.IsPathFullyQualified(input))
        {
            if (!File.Exists(input))
                throw new FileNotFoundException($"指定的解压工具不存在: {input}");

            // 验证类型
            if (IsCompatible(input, ExtractorType.Bandizip))
                return new ExtractorInfo(input, ExtractorType.Bandizip);
            if (IsCompatible(input, ExtractorType.SevenZip))
                return new ExtractorInfo(input, ExtractorType.SevenZip);

            throw new InvalidOperationException($"不支持的解压工具: {input}");
        }

        // 情况3: 是命令名（如 "bz", "7z", "NanaZipC"）
        // 尝试在 Candidates 中匹配（确保我们认识它）
        var (name, type) = Candidates.FirstOrDefault(c => c.name.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (name != null)
        {
            string? fullPath = FindExecutable(input);
            if (fullPath == null)
                throw new FileNotFoundException($"找不到命令: {input}");

            if (IsCompatible(fullPath, type))
                return new ExtractorInfo(input, type); // 返回原始命令名（非路径）
        }

        // 情况4: 未知命令名（如用户输错）
        throw new ArgumentException($"不支持的解压工具名称: {input}");
    }

    private static ExtractorInfo DetectExtractor()
    {
        foreach (var (name, expectedType) in Candidates)
        {
            string? fullPath = FindExecutable(name);
            if (fullPath != null && IsCompatible(fullPath, expectedType))
            {
                return new ExtractorInfo(name, expectedType);
            }
        }

        throw new FileNotFoundException(
            "未找到可用的解压工具。请安装 Bandizip (bz.exe)、7-Zip (7z.exe) 或 NanaZip (NanaZipC.exe)。");
    }

    // === 三步搜索逻辑 ===
    private static string? FindExecutable(string name)
    {
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string exeName = $"{name}.exe";
        string lnkName = $"{name}.lnk";

        // 1. 当前程序目录（含 .lnk）
        if (File.Exists(Path.Combine(currentDir, exeName)))
            return Path.Combine(currentDir, exeName);
        if (File.Exists(Path.Combine(currentDir, lnkName)))
            return Path.Combine(currentDir, lnkName);

        // 2. PATH 环境变量
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // 3. 默认安装目录
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] defaultPaths = new[]
        {
            // Bandizip
            Path.Combine(programFiles, "Bandizip", exeName),
            Path.Combine(programFilesX86, "Bandizip", exeName),
            // 7-Zip
            Path.Combine(programFiles, "7-Zip", exeName),
            Path.Combine(programFilesX86, "7-Zip", exeName),
            // NanaZip (WindowsApps 不需要这里，已在 PATH 中)
        };

        foreach (string path in defaultPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    // === 类型验证逻辑 ===
    private static bool IsCompatible(string fullPath, ExtractorType expectedType)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                Arguments = "-h",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            })!;

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return expectedType switch
            {
                ExtractorType.Bandizip => output.Contains("Bandizip"),
                ExtractorType.SevenZip => output.Contains("7-Zip") || output.Contains("NanaZip"),
                _ => false
            };
        }
        catch
        {
            return false; // 无法执行或超时
        }
    }
}

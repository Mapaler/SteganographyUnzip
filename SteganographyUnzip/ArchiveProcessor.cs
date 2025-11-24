// ArchiveProcessor.cs
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using static DebugUtil;

namespace SteganographyUnzip;

public class ArchiveProcessor
{
    private readonly DirectoryInfo _outputDir;
    private readonly DirectoryInfo _tempDir;
    private readonly string? _userProvidedPassword;
    private readonly IReadOnlyList<string>? _additionalPasswords;
    private readonly string? _userSpecifiedExtractor; // 用户可指定解压工具路径或命令

    public ArchiveProcessor(
        string outputDirectory,
        string tempDirectory,
        string? userProvidedPassword = null,
        IReadOnlyList<string>? additionalPasswords = null,
        string? userSpecifiedExtractor = null)
    {
        _outputDir = new DirectoryInfo(outputDirectory);
        _tempDir = new DirectoryInfo(tempDirectory);
        _userProvidedPassword = userProvidedPassword;
        _additionalPasswords = additionalPasswords;
        _userSpecifiedExtractor = userSpecifiedExtractor;
    }

    public async Task ProcessAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!_outputDir.Exists)
            _outputDir.Create();
        if (!_tempDir.Exists)
            _tempDir.Create();

        var initialFile = new FileInfo(inputPath);
        if (!initialFile.Exists)
            throw new FileNotFoundException($"输入文件不存在: {inputPath}");

        // 🚀 使用 ExtractorDetector 获取解压器（全局一次即可，或每层都检测？这里每层都检测更灵活）
        var extractor = ExtractorDetector.ResolveExtractor(_userSpecifiedExtractor);
        Console.WriteLine($"🔧 使用解压工具: {extractor.CommandName} ({extractor.Type})");

        var queue = new Queue<(FileInfo archive, DirectoryInfo finalOutput)>();
        queue.Enqueue((initialFile, _outputDir));

        while (queue.Count > 0)
        {
            var (currentFile, finalOutput) = queue.Dequeue();
            Console.WriteLine($"\n📦 处理: {currentFile.Name} ({currentFile.Length / 1024 / 1024} MiB)");

            var candidates = GetCandidatePasswords(currentFile);
            var strategy = CreateStrategy(extractor.Type);

            string tempSubDirName = Path.GetRandomFileName();
            var tempExtractDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, tempSubDirName));
            tempExtractDir.Create();

            try
            {
                // 🔍 列出内容（带密码尝试）
                List<string> fileList = await strategy.ListContentsAsync(
                    currentFile,
                    extractor.CommandName,
                    candidates,
                    cancellationToken);

                Console.WriteLine($"📄 内容预览: {string.Join(", ", fileList.Take(5))}{(fileList.Count > 5 ? "..." : "")}");

                if (IsContinuableArchive(fileList))
                {
                    Console.WriteLine("🔍 检测到隐写载体，尝试解压下一层...");

                    string? effectivePassword = await TryExtractWithCandidatesAsync(
                        currentFile, extractor, strategy, tempExtractDir, candidates, cancellationToken);

                    if (effectivePassword == null)
                        throw new InvalidOperationException("无法解压当前压缩包");

                    var extractedFiles = Directory.GetFiles(tempExtractDir.FullName, "*", SearchOption.TopDirectoryOnly)
                                                  .Select(f => new FileInfo(f))
                                                  .ToList();

                    foreach (var file in extractedFiles)
                    {
                        queue.Enqueue((file, finalOutput));
                    }
                }
                else
                {
                    string? effectivePassword = await TryExtractWithCandidatesAsync(
                        currentFile, extractor, strategy, tempExtractDir, candidates, cancellationToken);

                    if (effectivePassword == null)
                        throw new InvalidOperationException("无法解压当前压缩包");

                    MoveFilesToOutput(tempExtractDir, finalOutput);
                    Console.WriteLine($"✅ 已解压到: {finalOutput.FullName}");
                }
            }
            finally
            {
                try
                {
                    DebugLog($"🗑️ 删除临时文件夹 \"{tempExtractDir.Name}\"");
                    tempExtractDir.Delete(true);
                }
                catch { }
            }
        }

        Console.WriteLine("\n🎉 所有文件处理完成！");
    }

    private List<string> GetCandidatePasswords(FileInfo file)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(_userProvidedPassword))
            candidates.Add(_userProvidedPassword);
        if (ExtractPasswordFromPath(file.FullName) is string pwd)
            candidates.Add(pwd);
        if (_additionalPasswords?.Count > 0)
            candidates.AddRange(_additionalPasswords);
        candidates.Add(string.Empty);

        DebugLog($"🔍 为 \"{file.Name}\" 准备的密码候选: [{string.Join(", ", candidates.Select(p => string.IsNullOrEmpty(p) ? "(空)" : p))}]");

        return candidates.Distinct().ToList();
    }

    private static bool IsContinuableArchive(List<string> fileList)
    {
        if (fileList == null || fileList.Count == 0)
            return false;

        var archiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".7z", ".zip", ".rar", ".tar", ".gz", ".bz2", ".xz"
        };

        var stegoCarrierExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
            ".mp4", ".mov", ".avi", ".mkv", ".wmv",
            ".wav", ".mp3", ".flac",
            ".pdf"
        };

        // 情况 1：只解压出 1 个文件
        if (fileList.Count == 1)
        {
            string filePath = fileList[0];
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);

            // 是压缩包？→ 继续
            if (archiveExtensions.Contains(ext))
                return true;

            // 是隐写载体？→ 也继续（尝试当作压缩包解）
            if (stegoCarrierExtensions.Contains(ext))
                return true;

            //// 特殊：单个 .001 文件（虽然少见，但允许）
            //if (fileName.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
            //{
            //    string baseName = Path.GetFileNameWithoutExtension(fileName); // 移除 .001
            //    string baseExt = Path.GetExtension(baseName);
            //    if (archiveExtensions.Contains(baseExt))
            //        return true;
            //}

            return false;
        }

        // 情况 2：解压出多个文件 → 只检查是否含压缩包或分卷
        foreach (string filePath in fileList)
        {
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);

            // 是压缩包？
            if (archiveExtensions.Contains(ext))
                return true;

            // 是 .001 分卷？（只需检测 .001，因为解压时传它即可）
            if (fileName.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string baseExt = Path.GetExtension(baseName);
                if (archiveExtensions.Contains(baseExt))
                    return true;
            }
        }

        return false;
    }

    private static void MoveFilesToOutput(DirectoryInfo source, DirectoryInfo target)
    {
        foreach (var file in source.GetFiles("*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source.FullName, file.FullName);
            string dest = Path.Combine(target.FullName, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            file.MoveTo(dest, true);
        }
    }

    private IExtractorStrategy CreateStrategy(ExtractorType type)
    {
        return type switch
        {
            ExtractorType.SevenZip => new SevenZipStrategy(),
            ExtractorType.Bandizip => new BandizipStrategy(),
            _ => throw new NotSupportedException($"不支持的解压器类型: {type}")
        };
    }

    private async Task<string?> TryExtractWithCandidatesAsync(
        FileInfo archive,
        ExtractorInfo extractor,
        IExtractorStrategy strategy,
        DirectoryInfo outputDir,
        IReadOnlyList<string> candidates,
        CancellationToken ct)
    {
        foreach (string pwd in candidates)
        {
            try
            {
                string args = strategy.BuildExtractArguments(archive, outputDir, pwd);
                Console.WriteLine($"🔓 尝试解压密码: {(string.IsNullOrEmpty(pwd) ? "(空)" : pwd)}");

                // ✅ 使用新方法，showOutput=true 让用户看到进度！
                var (exitCode, _, error) = await ProcessHelper.ExecuteAsync(
                    extractor.CommandName, args, showOutput: true, ct);

                if (exitCode == 0)
                {
                    Console.WriteLine("✅ 解压成功");
                    return pwd;
                }

                string stderr = error.ToString();
                if (stderr.Contains("Wrong password") ||
                    stderr.Contains("Invalid password") ||
                    stderr.Contains("Cannot open encrypted archive") ||
                    stderr.Contains("Headers Error"))
                {
                    continue;
                }

                throw new InvalidOperationException($"解压失败 ({exitCode}): {stderr.Trim()}");
            }
            catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }
        return null;
    }
    #region 从路径里提取密码的逻辑
    private static readonly Regex PasswordHintRegex = new(
        @"(?:解压码|密码)(?:：|:)(?<pw>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? ExtractPasswordFromPath(string path)
    {
        // 先试文件名
        string fileName = Path.GetFileNameWithoutExtension(path);
        if (TryExtract(fileName, out string? pwd))
            return pwd;

        // 再试各级目录名
        string? dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            string dirName = Path.GetFileName(dir);
            if (TryExtract(dirName, out pwd))
                return pwd;
            dir = Path.GetDirectoryName(dir);
        }
        return null;

        bool TryExtract(string text, out string? password)
        {
            var match = PasswordHintRegex.Match(text);
            password = match.Success ? match.Groups["pw"].Value : null;
            return match.Success;
        }
    }
    #endregion
}

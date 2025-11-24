// ArchiveProcessor.cs
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SteganographyUnzip;

public class ArchiveProcessor
{
    private readonly DirectoryInfo _outputDir;
    private readonly DirectoryInfo _tempDir;
    private readonly string? _userProvidedPassword;
    private readonly IReadOnlyList<string>? _additionalPasswords;
    private readonly string? _userSpecifiedExtractor;

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

        var extractor = ExtractorDetector.ResolveExtractor(_userSpecifiedExtractor);
        Console.WriteLine($"🔧 使用解压工具: {extractor.CommandName} ({extractor.Type})");

        var queue = new Queue<(FileInfo archive, DirectoryInfo finalOutput, string? inheritedPassword)>();
        queue.Enqueue((initialFile, _outputDir, null));

        bool completedSuccessfully = false;
        try
        {
            while (queue.Count > 0)
            {
                var (currentFile, finalOutput, inheritedPassword) = queue.Dequeue();
                Console.WriteLine($"\n📦 处理: {currentFile.Name} ({currentFile.Length / 1024 / 1024} MiB)");

                var candidates = GetCandidatePasswords(currentFile, inheritedPassword);
                var strategy = CreateStrategy(extractor.Type);

                string tempSubDirName = Path.GetRandomFileName();
                var tempExtractDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, tempSubDirName));
                tempExtractDir.Create();

                try
                {
                    // === 1. 智能 List（仅用于预览）优先用继承密码，再用空密码===
                    List<string> fileList = new();
                    string? listPasswordUsed = null;
                    bool isRecognizedAsArchive = false;

                    // 尝试顺序：继承密码 -> 空密码
                    var listTryPasswords = new List<string>();
                    if (!string.IsNullOrEmpty(inheritedPassword))
                        listTryPasswords.Add(inheritedPassword);
                    listTryPasswords.Add(""); // 空密码兜底

                    foreach (string pwd in listTryPasswords)
                    {
                        try
                        {
                            var tempFileList = await strategy.ListContentsAsync(
                                currentFile, extractor.CommandName, new[] { pwd }, cancellationToken);
                            fileList = tempFileList;
                            listPasswordUsed = pwd;
                            isRecognizedAsArchive = true;
                            break; // 一旦成功就停
                        }
                        catch (Exception ex)
                        {
                            if (IsPasswordRelatedError(ex.Message))
                            {
                                continue; // 密码错误，试下一个
                            }
                            else
                            {
                                // 可能是非压缩文件（如纯MP4），先记录，不立即抛出
                                // 继续尝试其他密码（虽然大概率都失败）
                            }
                        }
                    }

                    // 如果所有密码都无法识别为压缩包，且文件是隐写载体类型 → 视为最终文件
                    if (!isRecognizedAsArchive && IsSteganographyCarrier(currentFile.Name))
                    {
                        Console.WriteLine($"📄 \"{currentFile.Name}\" 无法作为压缩包打开，视为最终内容文件。");
                        MoveFileToOutput(currentFile, finalOutput);
                        Console.WriteLine($"✅ 已保存到: {finalOutput.FullName}");
                        continue; // 跳过解压，处理下一个队列项
                    }

                    Console.WriteLine($"📄 内容预览 (使用密码: {(string.IsNullOrEmpty(listPasswordUsed) ? "(空)" : listPasswordUsed)}): " +
                                      $"{string.Join(", ", fileList.Take(5))}{(fileList.Count > 5 ? "..." : "")}");

                    // === 2. 尝试 Extract（试所有密码）===
                    string? effectivePassword = await TryExtractWithCandidatesAsync(
                        currentFile, extractor, strategy, tempExtractDir, candidates, cancellationToken);

                    if (effectivePassword == null)
                        throw new InvalidOperationException("无法解压当前压缩包");

                    // === 3. 获取真实解压后的文件列表 ===
                    var extractedFiles = Directory.GetFiles(tempExtractDir.FullName, "*", SearchOption.TopDirectoryOnly)
                                                  .Select(f => new FileInfo(f))
                                                  .ToList();

                    var realFileNames = extractedFiles.Select(f => f.Name).ToList();

                    // === 4. 根据真实文件决定是否递归 ===
                    if (IsContinuableArchive(realFileNames))
                    {
                        Console.WriteLine("🔍 检测到隐写载体，尝试解压下一层...");
                        foreach (var file in extractedFiles)
                        {
                            if (ShouldSkipAsNonFirstVolume(file.Name))
                            {
                                Console.WriteLine($"⏭️ 跳过分卷文件: \"{file.Name}\"");
                                continue;
                            }

                            queue.Enqueue((file, finalOutput, effectivePassword));
                        }

                        // ✅ 关键：已将子文件入队，当前临时目录可安全删除
                        try
                        {
                            if (tempExtractDir.Exists)
                            {
                                tempExtractDir.Delete(recursive: true);
                                ConsoleHelper.Debug($"🗑️ 已清理中间临时目录: {tempExtractDir.Name}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ 无法删除临时目录 {tempExtractDir.Name}: {ex.Message}");
                        }
                    }
                    else
                    {
                        MoveFilesToOutput(tempExtractDir, finalOutput);
                        Console.WriteLine($"✅ 已解压到: {finalOutput.FullName}");

                        // ✅ 清理已输出的临时目录（应为空）
                        try
                        {
                            if (tempExtractDir.Exists && !tempExtractDir.EnumerateFileSystemInfos().Any())
                            {
                                tempExtractDir.Delete();
                                ConsoleHelper.Debug($"🗑️ 已清理最终临时目录: {tempExtractDir.Name}");
                            }
                        }
                        catch (Exception ex)
                        {
                            // 忽略删除失败
                        }
                    }
                }
                finally
                {
                    // 不在此处统一清理，每个 tempExtractDir 已在分支中处理
                }
            }

            Console.WriteLine("\n🎉 所有文件处理完成！");
            completedSuccessfully = true;
        }
        finally
        {
            // ❌ 不再删除 _tempDir 本身（它可能是系统 Temp 目录！）
            // ✅ 所有子目录应在使用后立即清理
            if (!completedSuccessfully)
            {
                Console.WriteLine($"ℹ️ 异常发生，部分临时子目录可能保留在: {_tempDir.FullName}");
            }
            // 否则：全部已清理，无需操作
        }
    }

    private List<string> GetCandidatePasswords(FileInfo file, string? inheritedPassword)
    {
        var candidates = new List<string>();

        // 1. 用户显式提供的密码（即使为空也加入）
        if (_userProvidedPassword != null)
            candidates.Add(_userProvidedPassword);

        // 2. 从文件名/路径中提取的密码
        if (ExtractPasswordFromPath(file.FullName) is string pwdFromPath && !string.IsNullOrEmpty(pwdFromPath))
            candidates.Add(pwdFromPath);

        // 3. 从父级继承的有效密码
        if (!string.IsNullOrEmpty(inheritedPassword))
            candidates.Add(inheritedPassword);

        // 4. 额外预设的密码列表
        if (_additionalPasswords?.Count > 0)
            candidates.AddRange(_additionalPasswords.Where(p => !string.IsNullOrEmpty(p)));

        // 5. 显式添加空密码（用于尝试无密码情况）
        candidates.Add("");

        // 去重，但保留首次出现的顺序
        var seen = new HashSet<string>();
        var uniqueCandidates = new List<string>();
        foreach (var pwd in candidates)
        {
            if (seen.Add(pwd))
            {
                uniqueCandidates.Add(pwd);
            }
        }

        ConsoleHelper.Debug($"🔍 为 \"{file.Name}\" 准备的密码候选: [{string.Join(", ", uniqueCandidates.Select(p => string.IsNullOrEmpty(p) ? "(空)" : p))}]");
        return uniqueCandidates;
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

        // 情况 1：单文件
        if (fileList.Count == 1)
        {
            string filePath = fileList[0];
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);

            if (archiveExtensions.Contains(ext) || stegoCarrierExtensions.Contains(ext))
                return true;

            // 单个 .001 文件也视为可继续（虽然少见）
            if (fileName.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string baseExt = Path.GetExtension(baseName);
                if (archiveExtensions.Contains(baseExt))
                    return true;
            }

            return false;
        }

        // 情况 2：多文件 → 只检查是否含压缩包或 .001 分卷
        foreach (string filePath in fileList)
        {
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);

            if (archiveExtensions.Contains(ext))
                return true;

            // 关键：只要存在 .001 分卷，就认为可继续
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

    // 移动所有文件（递归）
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

    // 移动单个文件
    private static void MoveFileToOutput(FileInfo sourceFile, DirectoryInfo targetDir)
    {
        Directory.CreateDirectory(targetDir.FullName);
        string destPath = Path.Combine(targetDir.FullName, sourceFile.Name);

        // 防止重名
        int counter = 1;
        while (File.Exists(destPath))
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(sourceFile.Name);
            string ext = Path.GetExtension(sourceFile.Name);
            destPath = Path.Combine(targetDir.FullName, $"{nameWithoutExt} ({counter}){ext}");
            counter++;
        }

        sourceFile.MoveTo(destPath, overwrite: true);
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

                var (exitCode, _, error) = await ProcessHelper.ExecuteAsync(
                    extractor.CommandName, args, showOutput: true, ct);

                if (exitCode == 0)
                {
                    Console.WriteLine("✅ 解压成功");
                    return pwd;
                }

                string stderr = error.ToString();
                if (IsPasswordRelatedError(stderr))
                {
                    ConsoleHelper.Debug($"密码 '{pwd}' 导致密码错误，继续尝试下一个");
                    continue;
                }

                throw new InvalidOperationException($"解压失败 ({exitCode}): {stderr.Trim()}");
            }
            catch (Exception ex) when (IsPasswordRelatedError(ex.Message))
            {
                ConsoleHelper.Debug($"密码 '{pwd}' 抛出密码相关异常，继续尝试下一个: {ex.Message}");
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
        string fileName = Path.GetFileNameWithoutExtension(path);
        if (TryExtract(fileName, out string? pwd))
            return pwd;

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

    private static bool IsPasswordRelatedError(string message)
    {
        if (string.IsNullOrEmpty(message))
            return false;
        string msg = message.ToLowerInvariant();
        return msg.Contains("wrong password") ||
               msg.Contains("invalid password") ||
               msg.Contains("password is incorrect") ||
               msg.Contains("headers error") ||
               msg.Contains("data error") ||
               msg.Contains("cannot open encrypted") ||
               msg.Contains("0xa0000020"); // Bandizip 特定错误码
    }

    /// <summary>
    /// 判断是否为非首部分卷文件，若是则应跳过处理。
    /// 支持：7z/zip/rar 的各种分卷格式。
    /// </summary>
    private static bool ShouldSkipAsNonFirstVolume(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;

        // === 1. RAR 新格式: xxx.partNN.rar （NN >= 02）===
        var partRarMatch = Regex.Match(
            fileName,
            @"\.part(\d{2,})\.rar$",
            RegexOptions.IgnoreCase);
        if (partRarMatch.Success)
        {
            if (int.TryParse(partRarMatch.Groups[1].Value, out int partNum))
            {
                return partNum >= 2; // part01 是首卷，part02+ 跳过
            }
        }

        // === 2. ZIP 分卷: xxx.zNN （NN >= 01）===
        // 注意：首卷是 .zip，不是 .z00
        var zipVolMatch = Regex.Match(
            fileName,
            @"\.z(\d{2})$",
            RegexOptions.IgnoreCase);
        if (zipVolMatch.Success)
        {
            // 所有 .zXX 都是非首卷（因为首卷是 .zip）
            return true;
        }

        // === 3. RAR 旧格式: xxx.rNN （NN >= 00）===
        // 首卷是 .rar，.r00 是第二卷
        var rarVolMatch = Regex.Match(
            fileName,
            @"\.r(\d{2})$",
            RegexOptions.IgnoreCase);
        if (rarVolMatch.Success)
        {
            // 所有 .rXX 都是非首卷
            return true;
        }

        // === 4. 通用数字分卷: xxx.7z.001, xxx.zip.002 等 ===
        // 匹配结尾为 .DDD（三位数字），且 DDD != "001"
        var genericVolMatch = Regex.Match(
            fileName,
            @"\.(00[2-9]|0[1-9]\d|[1-9]\d{2})$");
        if (genericVolMatch.Success)
        {
            string baseName = fileName[..^genericVolMatch.Length];
            string baseExt = Path.GetExtension(baseName).ToLowerInvariant();
            var archiveExts = new HashSet<string> { ".7z", ".zip", ".rar", ".tar", ".gz", ".bz2", ".xz" };
            if (archiveExts.Contains(baseExt))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSteganographyCarrier(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp" or ".pdf" or ".doc" or ".docx" or ".zip" or ".7z" or ".rar";
    }
}

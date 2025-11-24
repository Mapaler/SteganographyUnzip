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
    private readonly List<string>? _additionalPasswords;
    private readonly string? _userSpecifiedExtractor;
    private readonly bool _deleteOriginalFile;

    public ArchiveProcessor(
        string outputDirectory,
        string tempDirectory,
        string? userProvidedPassword = null,
        List<string>? additionalPasswords = null,
        string? userSpecifiedExtractor = null,
        bool deleteOriginalFile = false)
    {
        _outputDir = new DirectoryInfo(outputDirectory);
        _tempDir = new DirectoryInfo(tempDirectory);
        _userProvidedPassword = userProvidedPassword;
        _additionalPasswords = additionalPasswords;
        _userSpecifiedExtractor = userSpecifiedExtractor;
        _deleteOriginalFile = deleteOriginalFile;
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

        // 关键：使用字典跟踪临时目录的引用计数
        var tempDirRefCount = new Dictionary<DirectoryInfo, int>();
        var queue = new Queue<(FileInfo archive, DirectoryInfo finalOutput, string? inheritedPassword, DirectoryInfo? sourceTempDir)>();
        queue.Enqueue((initialFile, _outputDir, null, null));

        bool completedSuccessfully = false;
        try
        {
            while (queue.Count > 0)
            {
                var (currentFile, finalOutput, inheritedPassword, sourceTempDir) = queue.Dequeue();

                // ✅ 重要修正：这里不删除临时目录！
                // 临时目录的删除在文件处理完成后进行

                // 检查当前文件是否存在
                if (!currentFile.Exists)
                {
                    throw new FileNotFoundException($"文件不存在（可能已被提前清理）: {currentFile.FullName}");
                }

                Console.WriteLine($"\n📦 处理: {currentFile.Name} ({currentFile.Length / 1024 / 1024} MiB)");

                var passwordProvider = new PasswordCandidateProvider();
                var passwordCandidates = passwordProvider.GetCandidatePasswords(
                    _userProvidedPassword,
                    inputPath,
                    inheritedPassword, // 从上层传入的继承密码
                    _additionalPasswords
                );
                var strategy = CreateStrategy(extractor.Type);

                // 创建本次解压专用的临时子目录
                string tempSubDirName = Path.GetRandomFileName();
                var tempExtractDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, tempSubDirName));
                tempExtractDir.Create();

                // ✅ 重要修正：初始化引用计数为1（当前层正在使用）
                tempDirRefCount[tempExtractDir] = 1; // 之前是0，现在改为1

                try
                {
                    // === 1. 智能 List（预览）===
                    List<string> fileList = new();
                    string? listPasswordUsed = null;
                    bool isRecognizedAsArchive = false;

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
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (IsPasswordRelatedError(ex.Message))
                                continue;
                        }
                    }

                    if (!isRecognizedAsArchive && IsSteganographyCarrier(currentFile.Name))
                    {
                        Console.WriteLine($"📄 \"{currentFile.Name}\" 无法作为压缩包打开，视为最终内容文件。");
                        MoveFileToOutput(currentFile, finalOutput);
                        Console.WriteLine($"✅ 已保存到: {finalOutput.FullName}");
                        continue;
                    }

                    Console.WriteLine($"📄 内容预览 (使用密码: {(string.IsNullOrEmpty(listPasswordUsed) ? "(空)" : listPasswordUsed)}): " +
                                      $"{string.Join(", ", fileList.Take(5))}{(fileList.Count > 5 ? "..." : "")}");

                    // === 2. 尝试 Extract（试所有密码）===
                    string? effectivePassword = await TryExtractWithCandidatesAsync(
                        currentFile, extractor, strategy, tempExtractDir, passwordCandidates, cancellationToken);

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
                        Console.WriteLine("🔍 检测到隐写载体或嵌套压缩包，尝试解压下一层...");
                        foreach (var file in extractedFiles)
                        {
                            if (ShouldSkipAsNonFirstVolume(file.Name))
                            {
                                Console.WriteLine($"⏭️ 跳过分卷文件: \"{file.Name}\"");
                                continue;
                            }

                            // ✅ 关键：将当前 tempExtractDir 作为下一层的 sourceTempDir
                            queue.Enqueue((file, finalOutput, effectivePassword, tempExtractDir));

                            // ✅ 增加 tempExtractDir 的引用计数
                            if (tempDirRefCount.TryGetValue(tempExtractDir, out int currentCount))
                            {
                                tempDirRefCount[tempExtractDir] = currentCount + 1;
                            }
                            else
                            {
                                tempDirRefCount[tempExtractDir] = 1;
                            }
                        }
                    }
                    else
                    {
                        MoveFilesToOutput(tempExtractDir, finalOutput);
                        Console.WriteLine($"✅ 已解压到: {finalOutput.FullName}");
                    }
                }
                finally
                {
                    // ✅ 重要修正：在文件处理完成后删除临时目录
                    // 1. 删除 sourceTempDir (如果存在)
                    if (sourceTempDir != null && tempDirRefCount.TryGetValue(sourceTempDir, out int sourceCount))
                    {
                        sourceCount--;
                        tempDirRefCount[sourceTempDir] = sourceCount;
                        if (sourceCount == 0)
                        {
                            try
                            {
                                sourceTempDir.Delete(recursive: true);
                                ConsoleHelper.Debug($"🗑️ 清理上一级临时目录: {sourceTempDir.Name} (引用计数归零)");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"⚠️ 无法删除上一级临时目录 {sourceTempDir.Name}: {ex.Message}");
                            }
                        }
                    }

                    // 2. 删除当前 tempExtractDir (如果它没有被引用)
                    if (tempExtractDir != null && tempDirRefCount.TryGetValue(tempExtractDir, out int currentCount))
                    {
                        currentCount--;
                        tempDirRefCount[tempExtractDir] = currentCount;
                        if (currentCount == 0)
                        {
                            try
                            {
                                tempExtractDir.Delete(recursive: true);
                                ConsoleHelper.Debug($"🗑️ 清理当前临时目录: {tempExtractDir.Name} (引用计数归零)");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"⚠️ 无法删除当前临时目录 {tempExtractDir.Name}: {ex.Message}");
                            }
                        }
                    }
                }
            }

            Console.WriteLine("\n🎉 所有文件处理完成！");
            completedSuccessfully = true;

            // ✅ 在这里删除原始文件（包括分卷）
            if (_deleteOriginalFile && completedSuccessfully)
            {
                DeleteOriginalFiles(initialFile);
            }
        }
        finally
        {
            // 清理残留的临时目录
            if (!completedSuccessfully)
            {
                foreach (var dir in tempDirRefCount.Keys)
                {
                    try
                    {
                        if (dir.Exists)
                        {
                            dir.Delete(recursive: true);
                            ConsoleHelper.Debug($"🗑️ 异常后清理残留临时目录: {dir.Name}");
                        }
                    }
                    catch { /* ignore */ }
                }
                Console.WriteLine($"ℹ️ 异常发生，已尽力清理临时子目录");
            }
        }
    }

    #region 辅助方法（保持不变）
    private void DeleteOriginalFiles(FileInfo mainFile)
    {
        try
        {
            var volumeFiles = GetVolumeFiles(mainFile);
            foreach (var file in volumeFiles)
            {
                if (file.Exists)
                {
                    file.Delete();
                    Console.WriteLine($"🗑️ 已删除原始文件: {file.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ 删除原始文件时出错: {ex.Message}");
            // 不抛出异常，避免影响主流程
        }
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

        if (fileList.Count == 1)
        {
            string filePath = fileList[0];
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);

            if (archiveExtensions.Contains(ext) || stegoCarrierExtensions.Contains(ext))
                return true;

            if (fileName.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string baseExt = Path.GetExtension(baseName);
                if (archiveExtensions.Contains(baseExt))
                    return true;
            }

            return false;
        }

        foreach (string filePath in fileList)
        {
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);

            if (archiveExtensions.Contains(ext))
                return true;

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

    private static void MoveFileToOutput(FileInfo sourceFile, DirectoryInfo targetDir)
    {
        Directory.CreateDirectory(targetDir.FullName);
        string destPath = Path.Combine(targetDir.FullName, sourceFile.Name);

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
               msg.Contains("0xa0000020");
    }

    private static bool ShouldSkipAsNonFirstVolume(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;

        var partRarMatch = Regex.Match(fileName, @"\.part(\d{2,})\.rar$", RegexOptions.IgnoreCase);
        if (partRarMatch.Success && int.TryParse(partRarMatch.Groups[1].Value, out int partNum))
            return partNum >= 2;

        if (Regex.IsMatch(fileName, @"\.z\d{2}$", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(fileName, @"\.r\d{2}$", RegexOptions.IgnoreCase))
            return true;

        var genericVolMatch = Regex.Match(fileName, @"\.(00[2-9]|0[1-9]\d|[1-9]\d{2})$");
        if (genericVolMatch.Success)
        {
            string baseName = fileName[..^genericVolMatch.Length];
            string baseExt = Path.GetExtension(baseName).ToLowerInvariant();
            var archiveExts = new HashSet<string> { ".7z", ".zip", ".rar", ".tar", ".gz", ".bz2", ".xz" };
            if (archiveExts.Contains(baseExt))
                return true;
        }

        return false;
    }

    private static bool IsSteganographyCarrier(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp" or ".pdf" or ".doc" or ".docx" or ".zip" or ".7z" or ".rar";
    }

    private static readonly Regex VolumePattern = new(
    @"^(.+?)(?:\.(?:part|vol)?(\d+)(?:\.rar|\.zip|\.7z)?)?$",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 获取分卷压缩包的所有相关文件
    /// 支持格式: .7z.001, .part1.rar, .vol01.rar, .zip.001 等
    /// </summary>
    private List<FileInfo> GetVolumeFiles(FileInfo mainFile)
    {
        var files = new List<FileInfo>();
        string dir = mainFile.DirectoryName ?? ".";

        // 尝试匹配常见分卷模式
        string fileName = mainFile.Name;

        // 模式 1: xxx.7z.001, xxx.7z.002 ...
        if (fileName.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = fileName.Substring(0, fileName.Length - 4); // 移除 .001
            int volNum = 1;
            while (true)
            {
                string volPath = Path.Combine(dir, $"{baseName}.{volNum:D3}");
                if (File.Exists(volPath))
                    files.Add(new FileInfo(volPath));
                else
                    break;
                volNum++;
            }
        }
        // 模式 2: xxx.part1.rar, xxx.part2.rar ...
        else if (Regex.IsMatch(fileName, @"\.part\d+\.rar$", RegexOptions.IgnoreCase))
        {
            string baseMatch = Regex.Match(fileName, @"^(.+?)\.part\d+\.rar$", RegexOptions.IgnoreCase).Groups[1].Value;
            if (!string.IsNullOrEmpty(baseMatch))
            {
                int partNum = 1;
                while (true)
                {
                    string partPath = Path.Combine(dir, $"{baseMatch}.part{partNum}.rar");
                    if (File.Exists(partPath))
                        files.Add(new FileInfo(partPath));
                    else
                        break;
                    partNum++;
                }
            }
        }
        // 模式 3: xxx.vol01.rar, xxx.vol02.rar ...
        else if (Regex.IsMatch(fileName, @"\.vol\d+\.rar$", RegexOptions.IgnoreCase))
        {
            string baseMatch = Regex.Match(fileName, @"^(.+?)\.vol\d+\.rar$", RegexOptions.IgnoreCase).Groups[1].Value;
            if (!string.IsNullOrEmpty(baseMatch))
            {
                int volNum = 1;
                while (true)
                {
                    string volPath = Path.Combine(dir, $"{baseMatch}.vol{volNum:D2}.rar");
                    if (File.Exists(volPath))
                        files.Add(new FileInfo(volPath));
                    else
                        break;
                    volNum++;
                }
            }
        }
        // 默认：单个文件
        else
        {
            files.Add(mainFile);
        }

        return files;
    }

    #endregion
}

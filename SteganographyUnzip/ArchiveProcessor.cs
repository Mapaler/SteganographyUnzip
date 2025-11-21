using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SteganographyUnzip;

public class InvalidPasswordException : Exception
{
    public InvalidPasswordException(string message) : base(message) { }
}

public class ArchiveProcessor
{
    private static readonly Regex PasswordHintRegex = new(
        @"(?:解压码|密码)(?:：|:)(?<pw>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly FileInfo[] _archives;
    private readonly string _userProvidedPassword; // 来自 -p
    private readonly DirectoryInfo _outputDir;
    private readonly DirectoryInfo _tempDir;
    private readonly string? _userSpecifiedExe;
    private readonly IReadOnlyList<string>? _additionalPasswords; // 来自 --try-passwords

    public ArchiveProcessor(
        FileInfo[] archives,
        string userProvidedPassword,
        DirectoryInfo outputDir,
        DirectoryInfo tempDir,
        string? userSpecifiedExe,
        IReadOnlyList<string>? additionalPasswords)
    {
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _userProvidedPassword = userProvidedPassword ?? string.Empty;
        _outputDir = outputDir ?? throw new ArgumentNullException(nameof(outputDir));
        _tempDir = tempDir ?? throw new ArgumentNullException(nameof(tempDir));
        _userSpecifiedExe = userSpecifiedExe;
        _additionalPasswords = additionalPasswords;
    }

    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        ExtractorInfo extractor = ExtractorDetector.ResolveExtractor(_userSpecifiedExe);
        Console.WriteLine($"使用 {extractor.Type} 工具: {extractor.CommandName}");

        _outputDir.Create();

        foreach (FileInfo archive in _archives)
        {
            if (!archive.Exists)
            {
                Console.WriteLine($"⚠️ 警告: 文件不存在，跳过 {archive.FullName}");
                continue;
            }

            Console.WriteLine($"\n📦 正在处理: {archive.Name}");
            try
            {
                await ExtractArchiveWithRetryAsync(archive, extractor, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n🛑 操作已取消。");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ 处理 {archive.Name} 失败: {ex.Message}");
            }
        }
    }

    private async Task ExtractArchiveWithRetryAsync(
        FileInfo archive,
        ExtractorInfo extractor,
        CancellationToken cancellationToken)
    {
        // 构建密码候选列表（保持顺序）
        var candidates = new List<string>();

        // 1. 用户通过 -p 提供的密码（最高优先级）
        if (!string.IsNullOrEmpty(_userProvidedPassword))
            candidates.Add(_userProvidedPassword);

        // 2. 从路径中提取的密码
        if (ExtractPasswordFromPath(archive.FullName) is string hintedPwd)
            candidates.Add(hintedPwd);

        // 3. --try-passwords 提供的密码
        if (_additionalPasswords?.Count > 0)
            candidates.AddRange(_additionalPasswords);

        // 4. 空密码（有些压缩包无密码）
        candidates.Add(string.Empty);

        // 去重但保持顺序
        candidates = candidates.Distinct().ToList();

        Exception? lastException = null;

        foreach (string password in candidates)
        {
            string displayPwd = string.IsNullOrEmpty(password) ? "(空)" : password;
            Console.WriteLine($"🔍 尝试密码: {displayPwd}");

            try
            {
                await TryExtractWithPasswordAsync(archive, extractor, password, cancellationToken);
                Console.WriteLine("✅ 解压成功！");
                return;
            }
            catch (InvalidPasswordException)
            {
                lastException = new InvalidPasswordException("密码错误");
                Console.WriteLine("❌ 密码错误，尝试下一个...");
                continue;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Console.WriteLine($"💥 非密码错误: {ex.Message}");
                break; // 其他错误不再重试
            }
        }

        throw new InvalidOperationException("所有密码尝试失败", lastException);
    }

    private async Task TryExtractWithPasswordAsync(
        FileInfo archive,
        ExtractorInfo extractor,
        string password,
        CancellationToken cancellationToken)
    {
        string arguments = BuildArguments(extractor.Type, archive, _outputDir, password);

        var startInfo = new ProcessStartInfo
        {
            FileName = extractor.CommandName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动解压进程");

        // ✅ 实时输出：边读边打印，不缓存！
        var outputTask = ConsumeStreamAsync(process.StandardOutput, Console.Out, cancellationToken);
        var errorTask = ConsumeStreamAsync(process.StandardError, Console.Error, cancellationToken);

        await WaitForExitAsync(process, cancellationToken);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

        if (process.ExitCode == 0)
            return;

        throw new InvalidPasswordException("密码错误或文件无效");
    }

    // 🔁 重构：支持同时打印和缓存（用于密码判断）
    // 但我们先用简单方案：靠用户肉眼判断 + 重试机制兜底
    // 如果你希望更精确，请告知，我可以加入 stderr 缓存

    private static string BuildArguments(
        ExtractorType type,
        FileInfo archive,
        DirectoryInfo outputDir,
        string password)
    {
        return type switch
        {
            ExtractorType.Bandizip => BuildBandizipArgs(archive, outputDir, password),
            ExtractorType.SevenZip => BuildSevenZipArgs(archive, outputDir, password),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static string BuildBandizipArgs(FileInfo archive, DirectoryInfo outputDir, string password)
    {
        var args = new StringBuilder();
        args.Append('x');

        if (!string.IsNullOrEmpty(password))
            args.AppendFormat(" -p:\"{0}\"", password);

        args.AppendFormat(" -o:\"{0}\"", outputDir.FullName);
        args.Append(" -y");

        args.AppendFormat(" \"{0}\"", archive.FullName);

        return args.ToString();
    }

    private static string BuildSevenZipArgs(FileInfo archive, DirectoryInfo outputDir, string password)
    {
        var args = new StringBuilder();
        args.Append('x');

        if (!string.IsNullOrEmpty(password))
            args.AppendFormat(" -p\"{0}\"", password);

        args.AppendFormat(" -o\"{0}\"", outputDir.FullName);
        args.Append(" -y");

        args.AppendFormat(" \"{0}\"", archive.FullName);

        return args.ToString();
    }

    private static string? ExtractPasswordFromPath(string path)
    {
        // 先试文件名
        string fileName = Path.GetFileName(path);
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

    // ✅ 实时流消费（关键：立即打印）
    private static async Task ConsumeStreamAsync(
        StreamReader reader,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                await writer.WriteLineAsync(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        void OnExited(object sender, EventArgs e) => tcs.TrySetResult(true);

        process.EnableRaisingEvents = true;
        process.Exited += OnExited;

        try
        {
            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                if (!process.HasExited)
                    await tcs.Task;
            }
        }
        finally
        {
            process.Exited -= OnExited;
        }

        process.WaitForExit(); // 确保资源释放
    }
}

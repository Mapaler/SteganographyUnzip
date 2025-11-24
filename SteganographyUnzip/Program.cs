using System.CommandLine;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SteganographyUnzip;

internal class Program
{
    private static readonly Argument<FileInfo[]> argumentPaths = new("archives")
    {
        Description = "要解压的文件列表",
        Arity = ArgumentArity.OneOrMore // ← 强制至少一个参数
    };
    private static readonly Option<string> optionPassword = new("--password", "-p")
    {
        Description = "解压密码"
    };
    private static readonly Option<DirectoryInfo> optionOutputDirectory = new("--output-dir", "-o")
    {
        Description = "最终解压目标目录，默认为压缩包同目录"
    };
    private static readonly Option<DirectoryInfo> optionTempDirectory = new("--temp-dir", "-t")
    {
        Description = "多层压缩包中间文件临时暂存目录（有条件可设置到内存盘，以减少磁盘磨损）",
        DefaultValueFactory = parseResult => new DirectoryInfo(Path.GetTempPath())
    };
    private static readonly Option<string> optionExeType = new("-exe")
    {
        Description = "指定解压程序",
        CompletionSources = { "7z", "7za", "NanaZipC", "bz" }
    };
    private static readonly Option<FileInfo> optionPasswordFile = new("--password-file")
    {
        Description = "从文本文件读取密码列表（每行一个密码）"
    };
    private static readonly Option<bool> optionDeleteOriginalFile = new("--delete-orig-file")
    {
        Description = "【危险！】解压完成后删除原始文件",
        DefaultValueFactory = parseResult => false
    };
    private static readonly Option<bool> optionUseClipboard = new("--use-clipboard")
    {
        Description = "从剪贴板读取密码（需为纯文本）",
        DefaultValueFactory = _ => false
    };

    private static readonly RootCommand rootCommand = new(
        $"自动解压隐写 MP4 压缩包和多层压缩包。{Environment.NewLine}" +
        "请自行安装 7-zip/NanaZip 或 Bandizip，或将他们的控制台版本可执行文件复制到本程序目录下。")
    {
        argumentPaths,
        optionPassword,
        optionOutputDirectory,
        optionTempDirectory,
        optionExeType,
        optionPasswordFile,
        optionDeleteOriginalFile,
        optionUseClipboard
    };

    static int Main(string[] args)
    {
        // 启用 UTF-8 输出（对现代终端有效）
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        Assembly assembly = Assembly.GetExecutingAssembly();
        string? titleAttr = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title;
        string projectName = titleAttr ?? assembly.GetName().Name ?? "隐写解压";
        Console.Title = projectName; //更改控制台标题

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            FileInfo[] archives = parseResult.GetValue(argumentPaths)!;
            string? password = parseResult.GetValue(optionPassword);
            DirectoryInfo? userOutputDir = parseResult.GetValue(optionOutputDirectory); // 用户指定的（可能为 null）
            DirectoryInfo tempDir = parseResult.GetValue(optionTempDirectory)!;
            string? exeName = parseResult.GetValue(optionExeType);
            bool deleteOriginalFile = parseResult.GetValue(optionDeleteOriginalFile);
            string? clipboardPassword = null;
            if (parseResult.GetValue(optionUseClipboard))
            {
                clipboardPassword = ClipboardHelper.TryGetText();
                if (!string.IsNullOrEmpty(clipboardPassword))
                {
                    Console.WriteLine($"📋 使用剪贴板密码: {clipboardPassword}");
                }
                else
                {
                    Console.WriteLine("📋 剪贴板为空或无法读取");
                }
            }


            // ✅ 从文件读取密码列表
            List<string> passwordList = new List<string>();
            if (parseResult.GetValue(optionPasswordFile) is FileInfo passwordFile)
            {
                try
                {
                    // 读取文件并过滤空行
                    var lines = File.ReadLines(passwordFile.FullName)
                                    .Select(line => line.Trim())
                                    .Where(line => !string.IsNullOrEmpty(line))
                                    .ToList();

                    passwordList.AddRange(lines);
                    Console.WriteLine($"🔑 从文件读取 {lines.Count} 个密码: {passwordFile.FullName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ 读取密码文件失败: {ex.Message}");
                    return 1;
                }
            }

            // 确保临时目录存在
            if (!tempDir.Exists)
                tempDir.Create();

            // 🔁 对每个输入的压缩包分别处理
            foreach (var archive in archives)
            {
                if (!archive.Exists)
                {
                    Console.WriteLine($"❌ 跳过不存在的文件: {archive.FullName}");
                    continue;
                }

                DirectoryInfo finalOutputDir = userOutputDir ?? archive.Directory!;
                if (!finalOutputDir.Exists)
                    finalOutputDir.Create(); // 安全创建（虽然通常已存在）

                try
                {
                    var processor = new ArchiveProcessor(
                        outputDirectory: finalOutputDir.FullName,
                        tempDirectory: tempDir.FullName,
                        userProvidedPassword: password,
                        additionalPasswords: passwordList,
                        clipboardPassword: clipboardPassword, // ✅ 传递剪贴板密码
                        userSpecifiedExtractor: exeName,
                        deleteOriginalFile: deleteOriginalFile
                    );

                    await processor.ProcessAsync(archive.FullName, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"🔥 处理 \"{archive.Name}\" 时出错: {ex.Message}");
                    // 可选择继续或退出，这里选择继续
                }
            }

            return 0;
        });

        ParseResult parseResult = rootCommand.Parse(args);
        return parseResult.Invoke();
    }
}

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
    private static readonly Option<DirectoryInfo> optionOutputDirectory = new("--output-directory", "-o")
    {
        Description = "解码目标目录",
        DefaultValueFactory = parseResult => new DirectoryInfo(Directory.GetCurrentDirectory())
    };
    private static readonly Option<DirectoryInfo> optionTempDirectory = new("--temp-directory", "-t")
    {
        Description = "多层压缩包中间文件临时暂存目录",
        DefaultValueFactory = parseResult => new DirectoryInfo(Path.GetTempPath())
    };
    private static readonly Option<string> optionExeType = new("-exe")
    {
        Description = "指定解压程序",
        CompletionSources = { "bz", "7z", "7za" }
    };
    private static readonly Option<List<string>> optionTryPasswords = new("--try-passwords")
    {
        Description = "自动尝试多个密码，用英文逗号分隔",
        Arity = ArgumentArity.ZeroOrOne,
        CustomParser = (tokenResult) =>
        {
            if (tokenResult.Tokens.Count == 0)
                return null;
            string raw = tokenResult.Tokens[0].Value;
            string[] parts = raw.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> passwords = new();
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    passwords.Add(trimmed);
                }
            }
            return passwords;
        }
    };

    private static readonly RootCommand rootCommand = new(
        $"自动解压隐写 MP4 压缩包和多层压缩包。{Environment.NewLine}" +
        "请自行安装 Bandizip 或 7-zip，或将他们的可执行文件复制到本程序目录下。")
    {
        argumentPaths,
        optionPassword,
        optionOutputDirectory,
        optionTempDirectory,
        optionExeType,
        optionTryPasswords
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
            string password = parseResult.GetValue(optionPassword) ?? string.Empty;
            DirectoryInfo outputDir = parseResult.GetValue(optionOutputDirectory)!;
            DirectoryInfo tempDir = parseResult.GetValue(optionTempDirectory)!;
            string? exeName= parseResult.GetValue(optionExeType);
            List<string>? additionalPasswords = parseResult.GetValue(optionTryPasswords);

            ArchiveProcessor processor = new(archives, password, outputDir, tempDir, exeName, additionalPasswords);
            await processor.ProcessAsync(cancellationToken);
            return 0;
        });

        ParseResult parseResult = rootCommand.Parse(args);
        return parseResult.Invoke();
    }
}

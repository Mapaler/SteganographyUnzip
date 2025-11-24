namespace SteganographyUnzip;

public interface IExtractorStrategy
{
    ExtractorType Type
    {
        get;
    }

    // 主要方法：带候选密码的 list（用于判断内容）
    Task<List<string>> ListContentsAsync(
        FileInfo archive,
        string commandName,
        IReadOnlyList<string> candidatePasswords,
        CancellationToken ct);

    // 构建解压命令
    string BuildExtractArguments(FileInfo archive, DirectoryInfo outputDir, string password);
}

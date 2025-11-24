using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SteganographyUnzip
{
    public class PasswordCandidateProvider
    {
        private static readonly Regex PasswordHintRegex = new(
            @"(?:解压码|密码)(?:：|:)?(?<pw>\S+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public List<string> GetCandidatePasswords(
            string? userProvidedPassword,
            string? path,
            string? inheritedPassword,
            List<string>? additionalPasswords,
            string? clipboardPassword) // ✅ 新增参数
        {
            var candidates = new List<string>();

            // 1. 命令行指定的密码 (最高优先级)
            if (!string.IsNullOrEmpty(userProvidedPassword))
                candidates.Add(userProvidedPassword);

            // 2. 从路径中提取的密码
            string? pwdFromPath = ExtractPasswordFromPath(path);
            if (!string.IsNullOrEmpty(pwdFromPath))
                candidates.Add(pwdFromPath);

            // 3. 剪贴板密码
            if (!string.IsNullOrEmpty(clipboardPassword))
                candidates.Add(clipboardPassword);

            // 4. 继承的密码
            if (!string.IsNullOrEmpty(inheritedPassword))
                candidates.Add(inheritedPassword);

            // 5. 额外密码列表
            if (additionalPasswords != null)
            {
                foreach (var pwd in additionalPasswords)
                {
                    if (!string.IsNullOrEmpty(pwd))
                        candidates.Add(pwd);
                }
            }

            // 添加空密码
            candidates.Add("");

            // 去重处理
            return Deduplicate(candidates);
        }

        private string? ExtractPasswordFromPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

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

        private List<string> Deduplicate(List<string> candidates)
        {
            var seen = new HashSet<string>();
            var uniqueCandidates = new List<string>();
            foreach (var pwd in candidates)
            {
                if (seen.Add(pwd))
                {
                    uniqueCandidates.Add(pwd);
                }
            }
            return uniqueCandidates;
        }
    }
}

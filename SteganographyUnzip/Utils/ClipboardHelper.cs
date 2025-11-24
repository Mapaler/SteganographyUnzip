using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SteganographyUnzip
{
    public static partial class ClipboardHelper
    {
        private const uint CF_UNICODETEXT = 13;

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool OpenClipboard(IntPtr hWndNewOwner);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseClipboard();

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr GetClipboardData(uint uFormat);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr GlobalLock(IntPtr hMem);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GlobalUnlock(IntPtr hMem);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial UIntPtr GlobalSize(IntPtr hMem);

        // 兼容旧名：无超时，内部会同步等待 STA 线程完成（默认 500ms）
        public static string? TryGetText() => GetText(500);

        // 主入口，timeoutMs 毫秒超时（线程 Join 超时后返回 null）
        public static string? GetText(int timeoutMs = 500)
        {
            // 如果当前线程已经是 STA，可以直接调用内部实现
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                return TryGetTextCore();
            }

            string? result = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = TryGetTextCore();
                }
                catch
                {
                    result = null;
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // 等待指定时间，超时则返回 null（后台 STA 线程会继续执行并退出）
            bool finished = thread.Join(timeoutMs);
            return finished ? result : null;
        }

        // 在 STA 线程中安全调用的实际实现
        private static string? TryGetTextCore()
        {
            bool opened = false;
            IntPtr hGlobal = IntPtr.Zero;
            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                    return null;
                opened = true;

                hGlobal = GetClipboardData(CF_UNICODETEXT);
                if (hGlobal == IntPtr.Zero)
                    return null;

                ptr = GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero)
                    return null;

                UIntPtr sizeBytes = GlobalSize(hGlobal);
                if (sizeBytes == UIntPtr.Zero)
                    return null;

                // 每个 Unicode 字符 2 字节；减去末尾的 null 终止符
                uint size = sizeBytes.ToUInt32();
                int chars = (int)(size / 2);
                if (chars <= 0)
                    return null;

                // 如果末尾有 null，则避免将其包含进来
                // 使用 Marshal.PtrToStringUni(ptr, chars) 会读取 chars 个字符（可能包含终止符）
                // 所以减少 1 个字符以剔除末尾 null（若存在）
                int readChars = chars;
                // 尝试查找真实长度：如果最后一个字符为 '\0'，则减 1
                // 为避免越界，先尝试读取最后两个字节
                try
                {
                    string full = Marshal.PtrToStringUni(ptr, chars) ?? string.Empty;
                    // TrimEnd 只移除空白，需要排除末尾的 '\0' 显示存在情况
                    int idx = full.IndexOf('\0');
                    if (idx >= 0)
                        return full.Substring(0, idx);
                    return full;
                }
                catch
                {
                    // 回退策略：逐步缩短 1 个字符直到成功或为 0
                    while (readChars > 0)
                    {
                        try
                        {
                            string s = Marshal.PtrToStringUni(ptr, readChars) ?? string.Empty;
                            int idx = s.IndexOf('\0');
                            if (idx >= 0)
                                return s.Substring(0, idx);
                            return s;
                        }
                        catch
                        {
                            readChars--;
                        }
                    }
                    return null;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (ptr != IntPtr.Zero && hGlobal != IntPtr.Zero)
                {
                    try
                    {
                        GlobalUnlock(hGlobal);
                    }
                    catch { }
                }

                if (opened)
                {
                    try
                    {
                        CloseClipboard();
                    }
                    catch { }
                }
            }
        }
    }
}

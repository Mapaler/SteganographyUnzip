# SteganographyUnzip

网友都用的各种多层隐写压缩包，解压起来特别麻烦，于是写个自动化一点的工具。

为了开发快，程序编码基本使用 [千问AI](https://www.qianwen.com/) 完成，我主要负责 Debug 和逻辑梳理。（其实我也不会这些高级写法）

## 推荐用法
在 Total Commander 里设置`suz -t R:\RamDiskFolder\ -o %T %P%S`，可以将当前选中文件解压到对面文件夹。当然需要你的文件名里有密码，或者使用`--password-file`传入文本文件。

<div align="center">
  <video src="https://github.com/user-attachments/assets/81efac16-48de-4810-b5e5-a819c55b78ff" width="70%" poster=""> </video>
</div>

## 详细用法
<pre>
Description:
  自动解压隐写 MP4 压缩包和多层压缩包。
  请自行安装 7-zip/NanaZip 或 Bandizip，或将他们的控制台版本可执行文件复制到本程序目录下。

Usage:
  suz <archives>... [options]

Arguments:
  <archives>  要解压的文件列表

Options:
  -p, --password <password>        解压密码
  -o, --output-dir <output-dir>    最终解压目标目录，默认为压缩包同目录
  -t, --temp-dir <temp-dir>        多层压缩包中间文件临时暂存目录（有条件可设置到内存盘，以减少磁盘磨损） [default: C:\Users\Mapaler\AppData\Local\Temp\]
  -exe <7z|7za|bz|NanaZipC>        指定解压程序
  --password-file <password-file>  从文本文件读取密码列表（每行一个密码）
  --delete-orig-file               【危险！】解压完成后删除原始文件
  --use-clipboard                  从剪贴板读取密码（需为纯文本）
  -?, -h, --help                   Show help and usage information
  --version                        Show version information
</pre>

### 密码候选策略：
1. 命令行参数`-p`提供的指定密码。
2. 文件路径内（比如文件名或文件夹名）存在“解压码：XXX”的无空格密码。
3. 命令行参数`--password-file`传入的文本文件内，每行一个密码，可作为常用密码候选库。

### 解压程序候选策略：
1. 命令行参数`-exe`提供的指定程序。
2. 7zip系（7z, 7za, NanaZipC）
   1. 当前目录下的 7z
   2. 环境变量的 7z
   3. Windows 默认安装位置的 7z
3. BandiZip
   1. 当前目录下的 bz
   2. 环境变量的 bz
   3. Windows 默认安装位置的 bz


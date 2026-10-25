# Windows 发布构建

运行 `Build-Release.ps1`，生成自包含 x64 应用目录、单 EXE 安装包、解压 ZIP、使用说明和 SHA256SUMS.txt。默认输出 artifacts/releases/<Directory.Build.props 中的版本>/，已有发布目录不会被覆盖。完整文件列表及验证在对应发布记录中。

需要 Windows、.NET 10 SDK 和官方 Inno Setup 7.1.0。编译器可从 [官方页面](https://jrsoftware.org/isdl.php) 获取，数字签名发布者为 Pyrsys B.V.；使用官方 `/PORTABLE=1 /CURRENTUSER` 模式可把构建工具解压到项目 artifacts/tools/inno-7.1.0，不修改文件关联。也可通过 `-Compiler` 指定已有 ISCC.exe。

```powershell
dotnet restore PCWatt.sln --locked-mode
dotnet build PCWatt.sln -c Release --no-restore
dotnet test PCWatt.sln -c Release --no-build --no-restore
.\packaging\Build-Release.ps1
```

应用和运行时采用独立文件安装，以保持硬件库可替换及许可要求；分发给用户的是一个 EXE 安装包，无需预装 .NET。没有 WPF 裁剪或把硬件依赖藏入不可替换的单文件。安装器为当前用户安装，不注册服务、不关闭运行中的程序、不自动重启；安装前先退出托盘。卸载保留用户数据，只移除确实指向本安装目录的自启动项。

RuntimeFrameworkVersion 在脚本中固定为 10.0.12，更新运行时需同步验证和许可。Inno Setup 原始许可位于 licenses/Inno-Setup-LICENSE.txt。当前发布未作 Authenticode 代码签名，不声称已经过 Windows 10、所有硬件或多 DPI 实机认证。

# 启动失败修复与实际启动验证

日期：2026-10-25，Asia/Shanghai。

## 原因与改动

原提示为无法创建 `%LOCALAPPDATA%\PCWatt\settings.json.<guid>.tmp`。实际检查确认：

- .NET 10 Windows Desktop Runtime 已安装，已发布程序集与 Release 构建一致。
- 本地数据目录的当前 Windows 用户有完整访问权限。
- 工作区及生成 exe 继承了低完整性标签；失败进程的完整性等级为 `S-1-16-4096`，因此不能写入正常完整性的数据目录。仅在资源管理器中重新双击同一 exe 不能解决文件标签带来的限制。

根目录 `启动.cmd` 现调用发布目录的 `Launch.cmd`，使用已安装的 .NET 运行时加载 `PCWatt.App.dll`；短暂的 Windows PowerShell 启动辅助使用 `CreateNoWindow`，启动后不常驻。用户级自启动也调用同一个启动入口，命令正确引用带空格路径，长度小于 260 字符。

程序在读写设置和数据库前检查启动环境，受限的直接 exe 启动提供明确中文指引。没有修改工作区或用户目录的权限标签，也没有要求管理员权限。

## 自动化验证

```powershell
dotnet build PCWatt.sln --no-restore
dotnet test PCWatt.sln --no-build --no-restore --logger "trx;LogFileName=startup-fix-debug.trx" --results-directory artifacts/TestResults
dotnet build PCWatt.sln -c Release --no-restore
dotnet test PCWatt.sln -c Release --no-build --no-restore --logger "trx;LogFileName=startup-fix-release.trx" --results-directory artifacts/TestResults
dotnet publish src/PCWatt.App/PCWatt.App.csproj -c Release --no-restore --self-contained false --output artifacts/PCWatt-preview
```

两种构建均成功，0 警告、0 错误；两种配置各 **131 通过、0 失败、0 跳过**。新增场景覆盖当前用户完整性查询、受限启动指引、普通用户启动条件及自启动入口引用。

发布包为 `artifacts/PCWatt-preview-startup-fix-win-x64-framework-dependent.zip`，包含 `Launch.cmd`，已核对发布程序集与 Release 构建一致，且未混入用户数据或测试记录。

## 实际启动与数据验证

- 已调用根目录 `启动.cmd`，实际打开标题为“PC Watt · 模拟功耗”的主窗口，进程由 `C:\Program Files\dotnet\dotnet.exe` 承载，并且窗口响应正常。
- 设置成功保存，数据库从 schema 2 升级到 3。
- 原有 **13 条**样本的全部字段与排序后 SHA-256 完全一致，用户设置各字段一致，包括暂停状态和 30 秒采样。只有设置封装版本和数据库 schema 完成正常升级。
- 已再次执行用户自启动项指向的同一命令，入口返回成功，仍只有一个 PC Watt 实例。
- 启动前备份与前后校验记录存于 `artifacts/recovery/startup-fix-20261025-0040/`，不放入发布包。

实际启动不等于 Windows 登录自启动验收；后者以及托盘点击、真实系统通知展示、休眠/关机、Windows 10 和动画资源占用仍需单独验收。当前数据源仍为明确标记的模拟数据。

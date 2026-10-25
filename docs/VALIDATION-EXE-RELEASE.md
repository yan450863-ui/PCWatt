# EXE 发行验证

版本 0.1.0 公开预览；2026-10-25，Windows 11 x64，SDK 10.0.401。

## 构建与验证

- Directory.Build.props 正式设置 Version 0.1.0 / FileVersion 0.1.0.0，EXE 嵌入项目原有墨青闪电图标。
- Debug / Release build 均零警告、零错误，测试各 260 通过、0 失败、0 跳过；新增验证自包含启动器、用户级自启动、授权交接、缺失 EXE 及框架依赖回退。TRX 位于 artifacts/test-results/release-package-{debug,release}.trx。
- 公开源码暂存目录单独完成锁定还原、Release 构建及 260 项测试。暂存目录继承的工作区执行标签使默认 testhost 无法监视父进程，使用测试平台正式 RunConfiguration.DotNetHostPath 参数指定已安装的 dotnet 主机后通过，未修改系统权限或 ACL。
- 安装包使用官方已核验 Pyrsys B.V. 数字签名的 Inno Setup 7.1.0 编译。编译器采用项目内便携模式，无文件关联或常驻进程。
- 自包含发布包含 .NET / Windows Desktop 10.0.12、独立可替换的硬件 DLL、SQLite 原生库、SVG/HTML、许可证和 LGPL 模块源码；没有 WPF 裁剪或硬件库单文件内嵌。
- 实际静默安装到唯一测试目录，检查当前用户卸载登记和关键文件 SHA256；从已安装 EXE 启动显式 --package-check，屏蔽系统 dotnet 查找后确认 .NET 10.0.12 来自应用目录、原生 SQLite 3.53.3 和硬件库 0.9.6.0 正常加载，版本 0.1.0.0。检查不启动采样、不读取用户设置、不发送通知、不请求管理员。
- 实际运行卸载器，确认程序文件和卸载登记移除、原用户设置和原自启动命令保持不变。记录 artifacts/diagnostics/installer-smoke-{app,result}.json，日志仅本地保存，不加入公开源码。
- 用户目录备份 artifacts/recovery/exe-release-20261025，包含更新前 16,693 条样本、两个电价版本、通知日期及设置。没有改采样频率、当前视图或旧运行实例；卸载不删除用户数据。

## 分发文件

- PCWatt-0.1.0-Setup-win-x64.exe：57,225,870 字节，SHA256 `61d3da2cfcdc4ae8beeb9fa232b8ac61bfba3871ee5da45c8532b13d7bd208ad`。
- PCWatt-0.1.0-win-x64.zip：80,747,402 字节，SHA256 `314fe801756252ea2f17faf95edf7042a28b5fe73be59d76d3586e526e831a42`。
- SHA256SUMS.txt 与使用说明.txt 一并提供；本地文件在 artifacts/releases/0.1.0。

安装器当前用户安装，不需要管理员；应用读取 CPU 等硬件时仍遵循用户偏好及正常 UAC。安装包未作 Authenticode 签名，未宣称已通过 SmartScreen 信誉认证。未实机覆盖 Windows 10、全硬件、所有 DPI、休眠/登录自启动与长时资源曲线。

公开介绍位于 README.md，原详细工程说明保留于 docs/DEVELOPMENT.md，发布说明在 docs/releases/0.1.0.md。发布使用的源码单独暂存，不包含构建缓存、诊断、备份、真实数据库、设置或凭据。

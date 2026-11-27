# PC Watt 0.3.3-preview.2 发布验证

- 日期：2026-11-28（北京时间）
- 版本：0.3.3-preview.2，预览版；文件版本 0.3.3.0。
- 已公开发布：[v0.3.3-preview.2](https://github.com/yan450863-ui/PCWatt/releases/tag/v0.3.3-preview.2)。
- 标签与发行源码提交：`eab4eca0e0c77adeffe79e0e6b834a09709d7069`；Release 为 prerelease，四个服务端附件的字节数、SHA256 均与下表一致。

## 验证

- 独立 Release 输出目录 `artifacts/build-release-validation-0.3.3-preview.2` 完成 348 项测试，0 失败、0 跳过。结果：`artifacts/test-results/0.3.3-preview.2/release-0.3.3-preview.2.trx`。
- `packaging/Build-Release.ps1` 成功生成自包含 x64 应用、EXE 安装包、ZIP 和 SHA256；自带 .NET 10.0.12。
- `packaging/Test-Release.ps1` 在隔离临时目录完成安装、文件校验、包内运行时与 SQLite 检查及卸载。用户设置 SHA256 和原自启动项未改变。
- ZIP 的 522 个条目全部可读取，未包含用户数据库、设置、历史备份或日志。
- 本机数据一致性备份已单独保存到 `历史数据/`，使用 SQLite 在线备份 API；与旧备份比对未发现旧记录丢失。详细审计留在本机，历史数据和废弃归档均由 `.gitignore` 排除。
- 本次未修改数据结构、积分、计费、传感器与采样逻辑。UI 检查范围见 [界面验证记录](VALIDATION-UI-DIRECTION-B.md)。

## 已发布附件

| 文件 | 字节数 | SHA256 |
| --- | ---: | --- |
| PCWatt-0.3.3-preview.2-Setup-win-x64.exe | 57262987 | `b52443c6018847e79fd4583a8e102ff9de88cfaabd3714cb17803197732bd76d` |
| PCWatt-0.3.3-preview.2-win-x64.zip | 80791926 | `118e52a925439013c7762b779a3527549ee8e9af4666e0daa7e7d31cfd2b2570` |
| SHA256SUMS.txt | 210 | `573b9e2c6be030e6defcb957f059cb47a71f111f26ba605532a8eb9cd2702b2b` |
| PCWatt-0.3.3-preview.2-Readme-zh-CN.txt | 5254 | `a1b81cc2dad5bb02472773615a31ed2121c3d1bdb6e77d50b8c95657063dd605` |

发布脚本先上传草稿，核对服务端字节数与 SHA256，再公开 Release。既有版本保留；安装包未作 Authenticode 代码签名。

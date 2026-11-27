# PC Watt 0.3.3-preview.1 GitHub 公开预览验证

- 日期：2026-11-27（北京时间）
- 发布页：[v0.3.3-preview.1](https://github.com/yan450863-ui/PCWatt/releases/tag/v0.3.3-preview.1)
- 类型：公开预览版（prerelease），不是稳定版；v0.3.2 与 v0.1.0 保留。
- 标签和 Release 均指向源码提交 `4aaca5b65a9ba3784843bc11e9a5f4d5d9765a2c`。
- 公开 Release API 返回四个附件；服务端字节数与 SHA256 和本地产物一致。

## 附件

| 文件 | 字节数 | SHA256 |
| --- | ---: | --- |
| PCWatt-0.3.3-preview.1-Setup-win-x64.exe | 57259786 | `f0fd60631e54921feb348043e1904f629d3aebe9d9b769eca94462f8b627d6da` |
| PCWatt-0.3.3-preview.1-win-x64.zip | 80791339 | `68a486a0056856b718b95cc895e75ff7ccbcb7d5a40307aa82974a2d9014d4af` |
| SHA256SUMS.txt | 210 | `cb875926e144aa0005b3f20782726be994043183b190eada3d1f2f4b7b548064` |
| PCWatt-0.3.3-preview.1-Readme-zh-CN.txt | 5247 | `f20f7620343fec030831aad6202b082c767b7e096ffadb50e89115d01610a812` |

## 验证

- Release 配置全量测试：348 通过，0 失败，0 跳过。
- 自包含发布目录和 Inno Setup 7.1.0 安装包构建成功。安装程序文件版本为 `0.3.3.0`，产品版本为 `0.3.3-preview.1`。
- 使用隔离临时目录完成静默安装、包内 .NET 运行时与原生 SQLite 检查，并完成卸载。用户设置和登录自启动注册项保持不变。
- ZIP 共 522 个条目，全部可读取；未发现数据库、设置、`.pcwatt` 备份或日志文件。
- GitHub 远端重新读取确认 Release 已公开、处于 prerelease 状态、包含四个附件；标签指向预期源码提交。
- 安装包未作 Authenticode 代码签名。预览版通过自动化和安装包检查；硬件 UI 尚未逐台实机验收。

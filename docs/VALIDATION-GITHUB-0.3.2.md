# PC Watt 0.3.2 公开发行验证

日期：2026-10-28。

- 仓库：https://github.com/yan450863-ui/PCWatt
- 发布页：https://github.com/yan450863-ui/PCWatt/releases/tag/v0.3.2
- 类型：公开预览版（prerelease），非草稿；旧版 v0.1.0 保留。
- 标签 v0.3.2 指向源码提交 `5f98fe85a6e3b44a20b1819d57b559e17657ecb6`。
- 同步时核对本地与 GitHub 的源码树 SHA、提交 SHA 完全一致，未强制覆盖分支。没有上传 artifacts 中的诊断、数据库或备份。
- 发布前核对本地安装包与 ZIP 的 SHA256，并检查 ZIP 不包含用户数据库、用户设置或 .pcwatt 备份。
- 上传四个附件后核对 GitHub 返回的大小、SHA256 与上传状态，全部一致后公开；再通过已连接 GitHub 独立读取确认版本和附件。

## 附件

| 文件 | 字节数 | SHA256 |
| --- | ---: | --- |
| PCWatt-0.3.2-Setup-win-x64.exe | 57264821 | 625bbef15125bca406ccd6b6210ea813d6def115444fa377ff9d2d16707531bd |
| PCWatt-0.3.2-win-x64.zip | 80780859 | f3b4e7cdafaee753c9dd0b387b70528d0fd5b427f1a0be704c0a4b250acd8efc |
| SHA256SUMS.txt | 190 | f9b6b9b03063c8540846d16df00d8c889372b31767072dc263c873e62e50323f |
| PCWatt-0.3.2-Readme-zh-CN.txt | 4650 | 4cfafb67b02c9236a4f4bb5b930343b75e072ff860b71d645e16d72569b39f8a |

本地产物和发行回执保存在 `artifacts/releases/0.3.2/`，回执为 github-release.json。中文更新说明为 `docs/releases/0.3.2.md`；README 下载入口已更新至 v0.3.2。

软件与安装验证沿用已通过的 Debug / Release 各 298 项测试、零警告零错误，以及安装、内置运行时、SQLite、卸载和保留用户设置检查，见 VALIDATION-LEAN-BACKUP.md。本次发布只更新发布工具和说明，应用二进制与已验证安装包保持一致。

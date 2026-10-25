# 第三阶段验证：人民币电价历史、通知与采样档位

执行日期：2026-10-25，Asia/Shanghai。当前工程位于 `D:\工作间\PCWatt`。

## 已实现

- 默认 30 秒采样，可选 5 秒、10 秒、30 秒、1 分钟；设置版本 1/2 均可读取，旧 2 秒迁移为 5 秒，有效旧选择保留。
- 中文人民币界面，只填写“电价（元/度）”和本地生效日期；电费显示为元，最终按分四舍五入。没有其他币种选择，不预设单价。
- SQLite schema 3，事务迁移 schema 1/2，保留原样本。电价版本按生效日期追加保存，重复日期和已有历史后的追溯改价均明确拒绝。
- 按本地午夜、电价边界裁切原始功率段；部分未计价保持未知。内部保留旧币种元数据用于恢复，但不将旧金额直接标为人民币。
- 独立启动通知与昨日报告开关；两项同时启用合并一条，通知日期持久化、每天最多尝试一次。关闭或失败不阻断采样；退出时取消通知任务。
- 昨日报告可靠门槛为覆盖率至少 80% 且有效覆盖至少 1 小时；不足不展示具体耗电及电费。报告详情包含覆盖率、缺失、模拟/估算属性和计价版本。
- 暂停/恢复或改档的延迟唤醒按设置变化的单调时间计算等待，避免重复推迟下一次采样；仍只有一个有效循环和等待定时器。

## 实际执行结果

环境：Windows 11 x64，.NET SDK 10.0.401。

```powershell
dotnet restore PCWatt.sln --locked-mode
dotnet build PCWatt.sln --no-restore
dotnet test PCWatt.sln --no-build --no-restore --logger "trx;LogFileName=phase3-debug.trx" --results-directory artifacts/TestResults
dotnet build PCWatt.sln -c Release --no-restore
dotnet test PCWatt.sln -c Release --no-build --no-restore --logger "trx;LogFileName=phase3-release.trx" --results-directory artifacts/TestResults
```

还原成功；Debug、Release 构建均为 **0 警告、0 错误**；两种配置各 **125 通过、0 失败、0 跳过**。

TRX 记录位于 `artifacts/TestResults/phase3-debug.trx` 和 `phase3-release.trx`。

Release 发布命令已成功执行：

```powershell
dotnet publish src/PCWatt.App/PCWatt.App.csproj -c Release --no-restore --self-contained false --output artifacts/PCWatt-preview
```

当前启动入口 `启动.cmd` 指向已更新的预览目录。发布程序集 SHA-256 与 Release 构建一致，包内默认采样为 30 秒，包含 SQLite x64 依赖及许可。新压缩包为 `artifacts/PCWatt-preview-phase3-win-x64-framework-dependent.zip`，17,668,147 字节；已检查未混入用户数据库、用户设置或测试记录，旧阶段包保留。

本机已有用户设置按本次明确要求从 10 秒调整到 30 秒，原暂停状态及其他开关保留；更新前原文件原子备份到 `%LOCALAPPDATA%\PCWatt\settings.json.before-30s.20261025-001520.bak`。这一明确的本机设置变更与普通升级保留有效频率的规则分开，未启动生产程序或迁移用户数据库。

测试覆盖原有积分/存储/生命周期场景，以及四档恢复与切换、1 分钟样本的间隔策略、电价切换处的功率斜坡裁切、旧数据恢复、历史不覆盖、异常价格、人民币定点舍入、旧外币不冒充人民币、通知开关组合、同日重复/下一本地日、可靠门槛、通知取消/不可用/失败和通知日期并发竞争。

构建期间发现一个新增测试的原始字符串语法错误，已修复。完整测试发现暂停恢复时存在延迟唤醒重新推迟采样的问题，已修复并补充针对性测试。上述通过结果对应修复后的最终代码。

## 界面检查

解析实际 WPF XAML，在 STA 线程用固定模拟样本离屏渲染，检查了：

- `artifacts/ui/dashboard.png`、`dashboard-minimum.png`
- `artifacts/ui/settings.png`
- `artifacts/ui/settings-tariffs.png`、`settings-tariffs-minimum.png`
- `artifacts/ui/settings-report.png`

概览和电价表单分别覆盖 1164×810 与 944×660 内容区域。日期输入框为深色，电价只显示元/度，没有货币输入或币种按钮。

## 验证边界

自动化使用临时 SQLite、临时设置、内存自启动替身与通知替身；渲染样本不写入用户数据库。没有修改真实自启动项或发送真实系统通知。

通知日期保存早于系统提交，保证每天最多一次尝试；若系统抑制展示、提交失败或中途退出，当天可能没有可见报告，不自动重复打扰。托盘接口返回成功仅表示提交给系统，不能证明用户看到了通知。

尚未实机验收系统通知展示、Windows 登录自启动、托盘点击、休眠/关机、Windows 10、缩放比例或动画资源占用。静态渲染不等于实时动效验收。真实硬件、五种历史统计、报告复制、导出及备份恢复界面尚未实现。

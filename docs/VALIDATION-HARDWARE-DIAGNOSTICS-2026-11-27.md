# 本地硬件诊断预览版验证

- 验证日期：2026-11-27（北京时间）
- 版本：0.3.3-preview.1（main 源码预览；未创建 GitHub Release）
- 范围：CPU/PawnIO 故障分类、设置页状态提示、重新检测、诊断信息复制与采样边界。

## 实现内容

- 区分 PawnIO 服务缺失、已停止、运行中但设备接口不可用，以及确认为权限不足的情况。服务状态无法查询时显示“无法确认”，不猜测驱动缺失或反复要求管理员授权。
- 设置页分别显示 CPU、显卡和整机状态。CPU/PawnIO 故障显示官方页面入口；链接指向驱动作者维护的 [PawnIO 官网](https://pawnio.eu/)，此入口只打开网页，安装由用户主动操作，应用不下载或安装驱动。
- “重新检测”复用现有硬件采样器并重新建立硬件会话。探测样本不计入历史电量；既有样本先正常写入，新会话从明确边界后继续记录。暂停状态保持暂停。
- 诊断复制仅包含应用版本、系统版本、进程位数、权限、记录状态、硬件状态及 Win32 错误码，不包含用户名、路径、电价或用电历史；只有用户点击复制后才写入剪贴板。
- 首页提示整机功率不可读及影响；不改变已有电量、电费和缺失时段规则，不新增轮询或采样循环。

## 验证结果

- 定向测试通过 61 项，0 失败、0 跳过，覆盖硬件状态分类、PawnIO 服务查询、视图模型诊断操作、重新检测与采样协调器。
- 直接从独立 artifacts 目录启动测试宿主时，宿主在读取父进程信息时遇到 Win32 5 并退出；同一组 61 项从标准测试输出目录运行通过。该异常发生在测试宿主启动阶段，没有测试断言失败。
- Release 独立构建通过，0 警告、0 错误：

  ```powershell
  dotnet build PCWatt.sln -c Release --no-restore --artifacts-path "artifacts/build-hardware-diagnostics"
  ```

- 根目录预览入口：`启动.cmd` 调用 `artifacts/build-hardware-diagnostics/bin/PCWatt.App/release/Launch.cmd`，缺少构建时将源码 Release 输出到同一目录。未生成安装包、未创建 GitHub Release，也未启动此预览程序进行手动界面验收。

定向测试命令：

```powershell
dotnet test tests\PCWatt.Tests\PCWatt.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~HardwareReadinessTests|FullyQualifiedName~PawnIoDiagnosticsTests|FullyQualifiedName~HardwareRecheckTests|FullyQualifiedName~HardwareDiagnosticsViewModelTests|FullyQualifiedName~SamplingCoordinatorTests"
```

# Brightness and Power Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 给现有控制页添加真实内外屏亮度、电源模式、现有电源计划及供电状态。

**Architecture:** 亮度、电源各自封装原生后端和按需工作队列，与声音隔离。界面显示真实读数，提交绑定明确目标，所有后台结果经过生命周期代际检查。

**Tech Stack:** C# / .NET 10 / WPF，Windows WMI、Dxva2、PowrProf、Kernel32，固定 System.Management 10.0.2。

**Spec:** [已确认方案及实现约束](../specs/2026-11-16-brightness-power-design.md)

实施状态（2026-11-16）：功能及原启动入口已更新；768 项主程序、35 项温度服务、62 项控制专项与 11 项本机实机检查通过。实机检查恢复并核对了原始亮度、原计划及 AC/DC 模式。独立审查的四项重要问题已修复。最后物理鼠标导航因当前 LockApp 锁屏待解锁补测；外接 DDC 屏与真实电池供电切换未实测。源码按项目既有授权同步，记录以 Git 为准。

## Global Constraints

- 就地修改现有工程；不另建 worktree，不复制完整项目。
- SDK 固定 10.0.403，Runtime 固定 10.0.14，保持 0.8.0-rc.37 版本标识及原启动入口。
- 不改变唤出方式、默认面板尺寸、六页导航、现有声音功能。
- 不轮询外接屏，不新增常驻 PowerShell、驱动或服务；暂停不发新硬件查询。
- 文件坞只保存屏幕偏好，不保存并自动重放亮度、模式或计划。
- 不改厂商计划内容，不加入 CPU 调参、同步多屏、自动亮度或关机重启。
- 真实写入测试先保存原值，finally 恢复并读回；无法恢复时明确报告。
- 交付前更新现有 FileDock.exe，保留配置和回退备份；整轮验证后提交推送一次，临时材料按用户规则清理。

## Review Focus

1. 屏幕在滑动中断开或重连：旧请求不写入另一个屏幕，也不在重新展开时重放。
2. DDC 亮度范围非 0～100、设备不可读、厂商调用很慢：正确归一化、禁用与隔离，声音保持可操作。
3. AC/DC 在选择与提交之间变化：旧请求被拒绝并刷新，不修改错误供电配置。
4. 系统接口缺失、策略拒绝、未知有效模式或没有电池：显示实际能力，不伪造成功或 0%。
5. 收起、锁屏、睡眠、退出、保存偏好失败：清理订阅、忽略旧结果、不重放写入、保留原保存值。

## Task 1: 亮度控制及 Windows 后端

**Files:** 新增 `源码/Features/ControlCenter/BrightnessController.cs`、`WindowsBrightnessBackend.cs`、`ControlWorkQueue.cs`、`源码/Verification/BrightnessVerification.cs`；修改 `源码/FileDockDemo.csproj`、`packages.lock.json`、`DemoVerification.cs`。

**Interfaces:**
- `BrightnessTarget(string Id, long Connection)`、`BrightnessDevice` 携带对应 Connection；稳定 Id 不使用当前列表索引，连接代际避免同屏重连后重放旧写入。
- `BrightnessSnapshot(IReadOnlyList<BrightnessDevice> Devices, string? Error)`。
- `IBrightnessBackend.Start(Action changed)`、`Read()`、`Write(BrightnessTarget target, int percent)`、`Stop()`、`Dispose()`。
- `BrightnessController` 提供 `Current`、`Changed`、`SetActive(bool)`、`Refresh()`、`SetBrightness(BrightnessTarget,int)`、`DrainAsync()`、`Dispose()`。
- `ControlWorkQueue` 封装专用 MTA 工作线程与停止；每个控制器独占一个实例。接口接受委托，代际及发布判断由控制器负责，不改音频控制器。

- [x] 创建内部测试工作目录 `work/brightness-power`，准备锁定 SDK；全部内部导出与探测材料集中于此。
- [x] 先写模拟后端测试并观察失败：隐藏时工厂调用 0 次；百分比请求 130 限制为 100；设备 A 断开后旧 A 请求不写 B；失败后显示原读数及错误；迟到通知与暂停写入不改变当前状态；慢后端不阻塞 UI Dispatcher。
- [x] 实现工作队列、亮度控制器和数据类型，使上述测试通过。
- [x] 实现活动显示路径映射、WMI 内置屏及 DDC 物理句柄后端，加入范围换算单元检查：原始 20～220 的 120 对应 50%，写入 25% 对应 70；范围无效时禁用。
- [x] 检查所有句柄/WMI 对象的失败释放、重复停止、同名屏幕及不可靠映射行为；运行新增验证。

## Task 2: 电源控制及 Windows 后端

**Files:** 新增 `源码/Features/ControlCenter/PowerController.cs`、`WindowsPowerBackend.cs`、`源码/Verification/PowerVerification.cs`；修改 `DemoVerification.cs`。

**Interfaces:**
- `PowerSupply` 区分 AC、Battery、Unknown；`PowerModeChoice` 区分 Efficiency、Balanced、Performance。
- `PowerPlan(Guid Id, string Name)`；`PowerSnapshot` 包含供电、电池百分比／充电状态、用户模式、可读有效模式、实际计划及独立能力／错误字段。
- `IPowerBackend.Start(Action changed)`、`Read()`、`SetMode(PowerModeChoice, PowerSupply expectedSupply)`、`SetPlan(Guid)`、`Stop()`、`Dispose()`。
- `PowerController` 提供 `Current`、`Changed`、`SetActive(bool)`、`Refresh()`、`SetMode(...)`、`SetPlan(Guid)`、`DrainAsync()`、`Dispose()`；复用 Task 1 的工作队列类型但不共享队列实例。

- [x] 先写模拟后端测试并观察失败：无电池不显示 0%；未知供电禁用模式写入；AC 请求遇到 DC 时拒绝；缺少模式接口不妨碍计划列表；自定义名称/GUID 原样保留；策略失败读取实际计划；隐藏后拒绝旧写入。
- [x] 实现控制器和运行时接口探测；用公开 AC/DC 模式接口、PowerEnumerate、PowerGetActiveScheme、PowerSetActiveScheme、GetSystemPowerStatus 读取及设置。
- [x] 每次写入后独立读回；区分用户模式与有效模式，无法读取有效模式时标明，不以推断值充当实测。
- [x] 测试原生分配内存 LocalFree、事件注销、供电竞争及不存在计划的处理。

## Task 3: 紧凑界面、屏幕偏好和生命周期接入

**Files:** 新增 `源码/Features/ControlCenter/BrightnessView.cs`、`PowerView.cs`、`源码/Verification/DisplayPowerViewVerification.cs`；修改 `ControlCenterView.cs`、`MainWindow.ControlCenter.cs`、`Models.cs` 及 `MainWindow.Performance.cs` 中共用的暂停活动入口。复用现有声音验证。

**Interfaces:**
- `BrightnessView` 消费 `BrightnessController`、初始屏幕 Id 及 `Func<string?,Task<bool>>` 保存回调；提供主页面控件、内部详情与暂停／恢复。
- `PowerView` 消费 `PowerController` 与 Windows 设置打开委托；提供快捷模式控件及详情。
- `AppState.PreferredBrightnessDisplayId` 为可空字符串，旧配置缺省兼容；回调沿用现有保存队列，不另建配置文件。

- [x] 先写 UI 回归并观察失败：亮度、电源入口存在；只打开对应详情；缺失值显示“—”；设备选择不写亮度；绑定读数不触发写入；松手提交正确 Id；保存失败保留原偏好；无电池文案正确。
- [x] 在现有控制页主界面插入亮度与电源控件，详情统一返回，保持声音入口和默认尺寸，必要时内部滚动。
- [x] 接入页面可见性、锁屏和睡眠通知；停止待提交滑块操作并使旧回调失效；退出时完整释放。先检查现有通知位置，再作最小接入，不改变内存与音频逻辑。
- [x] 在浅深主题、342 逻辑像素内容宽度和现有最小窗口下验证可操作、不横向溢出；保留现有音频模拟测试稳定性。

## Task 4: 实测、独立审查、交付与清理

**Files:** 新增 `源码/Verification/DisplayPowerDeviceVerification.cs`；修改 `App.cs`（独立 opt-in 测试入口）、`Build.ps1`（所需许可）、`THIRD-PARTY-NOTICES.md`、`README.md`、`使用说明.txt`。

- [x] 新增 `--display-power-test <新结果目录>`：模拟测试不接触用户系统；真实测试保存每个实际写入目标的原始亮度及原电源 GUID／对应供电模式，finally 恢复并读回核验。无相应设备的路径显式 SKIP，不算已实测。
- [x] 使用 `源码/Build.ps1` 构建到本任务新空目录，运行 `源码/Test.ps1` 完整自测及温度服务检查；新增检查全部通过。
- [ ] 运行真实设备回读与恢复测试、实际桌面控制页及详情导航，使用 PerMonitorV2 自动化坐标；不测试关机／睡眠。
- [x] 用独立只读审查核对目标身份、原生资源、代际、供电竞争、保存失败和分发许可；修复实际问题后复测受影响路径。
- [x] 完善上述必要说明；正常退出原程序，备份 FileDock.exe 和个人状态到现有更新备份目录，替换原入口并核对 hash、启动与桌面可用性。
- [ ] 检查提交范围、`git diff --check` 与 `源码/Verify-Repository.ps1`，核对远端后仅提交并推送本轮完整成果一次；不新建 Release 或标签。
- [ ] 确认测试、审查和后台进程不再依赖内部材料；验证绝对路径与无重解析点后清理本任务临时目录，检查真实删除结果。保留源码、正式设计／计划、原配置及恢复备份。

## Execution

建议 Native：主代理依次实现三个关联模块，最后独立审查一次，避免多份上下文重复理解共享页面与保存接口。开始实现前按 writing-plans 的交接规则确认本计划。

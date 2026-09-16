# 《植物大战僵尸杂交版》MOD 工程 —— 交给 AI 的完整说明书（AI 提示词）

> **这份文档解决什么问题**：本工程 90% 的知识是「反常识」的。只把源码丢给 AI，它会按通用 Godot / Unity 常识去猜，
> 于是必然猜错字段名、用被 AOT 裁掉的 API、绕过注入链路直接改 DLL、在手机上用电脑版方案 —— 结果就是「编译通过、运行全废」。
> 把本文档和源码一起交给 AI，它才能正确理解、安全改动。
>
> **怎么用**：
> 1. 把本文档**整篇**贴给 AI（3 万字，一次性喂进去最省事）。
> 2. 再附上你要改的那几个 `.cs` 文件。
> 3. 明确告诉 AI 你要做什么，并要求它**遵守第 0 章的硬性规则**与**第 15 章的红线**。
> 4. 让它先输出「改动计划 + 涉及文件 + 验证方法」，你确认后再让它写代码。
>
> **文档口径**：所有关于游戏内部结构的事实，标注了取证方式（反编译 / IL 实测 / 探针验证 / 真机测试）。
> 凡未标注的，都是**未验证的推测**，AI 不得当事实使用。

---

## 0. 给 AI 的硬性工作规则（先读这一章，违反等于交付失败）

1. **不许凭常识猜游戏内部结构**。本项目的一切反射字段名、方法签名、枚举值，都来自 0.27 版反编译或运行时探针。
   你要改涉及游戏对象的东西，必须先说明「我用什么方式验证这个字段存在、类型是什么」，再写代码。
   历史上「猜字段」导致同一个 bug 修了 3 轮（植物射速：先猜植物节点 `fireInterval`(Double) → 再猜 setter → 最后才确认是 `FireComponent.fireInterval`(Single)）。
2. **不许发明 API**。见第 7 章「可用 / 不可用 API 白名单与黑名单」。游戏是 AOT 裁剪运行时，
   很多 BCL API **编译期完全正常、运行期抛 `Method not found`**。新增任何 BCL 调用前先对照白名单。
3. **改完必须证明"真的生效了"**，不能只说"编译通过"：
   - 编译：`dotnet build` 必须 **0 error**；
   - 注入：patcher 必须打印注入点命中（`bad=0`）；
   - 落地：注入后用 **UTF-16** 字符串搜索目标 DLL，确认新方法名/新字符串真的在里面（`.NET 字符串在 DLL 里是 UTF-16`，用 UTF-8 搜会搜不到而误判）；
   - 真机：给出「进游戏 → 看哪个日志/哪个按钮 → 期望什么现象 → 不生效时看哪条日志」。
   - 历史教训：曾经因为 `.bak` 缺失导致 patcher 基于「上次注入版」合并、已存在的类型不更新，连续两天「改了但没生效」，所有"成功"都是假的。
4. **PC 版与手机版必须对称修改**。`mod/PvzheMod/` 与 `androidmod/PvzheAndroid/` 是同一套功能的两份实现
   （PC = 类型合并进主程序集；手机 = 独立 `PvzheMod.dll` 运行时加载）。改了 PC 不改手机，用户立刻会发现。
5. **每帧入口必须全包 try/catch**。`FrameDriver.OnFrame` 里任何一段抛异常都会中断主循环，表现为「MOD 所有功能同时失效」。
   catch 里**必须记日志**（不许空 catch），否则 TypeInitializationException 这类问题永远查不出来。
6. **改动只做用户要求的**。不要顺手重构、不要顺手改数值、不要删你没被要求删的东西。
   （历史：用户明确说过「不要乱改我其他逻辑就改 bug 就行」。）
7. **不要给"核心路径"方法开头注入 sleep / 强制等待**。核心路径可能被主线程同步等待链调用，插 sleep = 假死。
8. **不要定义与已有方法重名的重载**（尤其带 `object` / `double` 参数的）。patcher 的 `MergeTypes` 合并后会产生坏 IL，
   报 `InvalidCastException: TypeReference→IMethodSignature`。
9. **不要把 `System.IO.File` / `Path` 用在 MOD 的关键路径**（AOT 裁剪），一律用 Godot `FileAccess` / `DirAccess`，且路径解析手写循环。
10. **输出风格要求**：不用 LINQ、不用 `StringBuilder` 的数值重载、不用带格式串的 `ToString`、不用接口变量做跨类型调用。
    详见第 7 章。

---

## 1. 这是什么（项目定位）

### 1.1 一句话

给 **《植物大战僵尸杂交重制版》（Godot 4.7 + C# / .NET 8~9 + AOT 裁剪）** 做的**功能型 MOD**：
**编译期重写游戏主程序集注入**（Mono.Cecil，不依赖 BepInEx 之类运行时框架）+ **外置 WPF 遥控面板**（本地 HTTP `127.0.0.1:28999`），
并自带**自研联机系统**（局域网房间 + 公网中继 + PvP）与**自定义植物/僵尸/子弹/关卡**等扩展系统。

### 1.2 交付物（四个）

| 交付物 | 形态 | 说明 |
|---|---|---|
| 电脑版 MOD | 修改后的 `PlantsVsZombies.dll`（约 24.6MB，净增 ~0.3MB） | `mod/patcher` 把 `mod/PvzheMod` 的类型**合并**进游戏主程序集 |
| 手机版 MOD | `杂交版MOD_手机版.apk`（约 400MB） | `androidmod/android_patcher` 向游戏主程序集插入调用，`PvzheMod.dll` 作为**独立程序集**随包加载（**不合并**，合并会闪退） |
| 外置修改器 | 单个自包含 exe（约 77MB，WPF / net8.0-windows） | `mod/tools/外置修改器/wpf/`，通过 `127.0.0.1:28999` 控制游戏内 MOD |
| 一键注入器 | 单个自包含 exe（内嵌 MOD DLL + 外置修改器） | `mod/tools/一键注入/`，给小白用 |

另有 **联机中继服务端** `relay/PvzheRelay`（可部署到云主机，房间管理 + 加密封包转发）。

### 1.3 真实工程规模（供 AI 判断"改哪里"）

| 目录 | 文件数 | 说明 |
|---|---|---|
| `mod/PvzheMod/` | 40+ | PC 版 MOD 主体（`GameCheats.cs` 单文件 **650KB**，是功能实现总库，用 `partial` 拆多个文件） |
| `androidmod/PvzheAndroid/` | 40+ | 手机版 MOD 主体（与 PC 对称，`GameCheats.cs` **599KB**） |
| `mod/patcher/` | ~5 | PC 注入器（`Program.cs` **122KB**，IL 改写规则全在里面） |
| `androidmod/android_patcher/` | ~5 | 手机注入器（`Program.cs` **102KB**） |
| `mod/tools/外置修改器/wpf/` | 25 | WPF 外置面板（`MainWindow.xaml.cs` **157KB**、`CustomPlantWindow.xaml.cs` 129KB） |
| `relay/` | 12 | 联机中继服务端 + 测试 + 加密测试工具 |
| `mod/NetTests/` | 2 | MOD 侧协议单测 |

**AI 最容易犯的错**：以为「功能写在 UI 里」。实际上**功能实现全部在 `GameCheats.cs`**（PC/手机各一份），
`ModUI.cs` 只是游戏内 UI，`外置修改器` 只是遥控面板，三者通过 `ModSettings` 这一个静态类通信。

### 1.4 三个工程的关系（避免混淆）

| 工程 | 游戏 | 运行时 | 注入方式 | 通信通道 |
|---|---|---|---|---|
| **杂交版**（本工程） | 植物大战僵尸杂交重制版 0.27 | Godot 4.7 + C#，AOT 裁剪 | Mono.Cecil 合并/独立 DLL | HTTP `127.0.0.1:28999` |
| 抽卡版（另一个开源工程） | 抽卡版PVZ 0.60.0 | Unity 2022.3 Mono（**被链接器裁剪**） | Cecil 改 `Assembly-CSharp.dll` | **文件命令通道**（无 TcpListener） |
| 融合版（另一个开源工程） | PvZ 融合版 3.9 | Unity 2022.3 **IL2CPP** | BepInEx 6 (IL2CPP) + Harmony | HTTP `127.0.0.1:27400` |

**不要跨工程套方案**。三个工程的注入方式、通信方式、可用 API 完全不同。

---

## 2. 开源模式与许可（这是什么开源模式）

### 2.1 许可：Apache License 2.0

本工程以 **Apache-2.0** 开源。这是一个**宽松型（permissive）**许可，不是 copyleft。

**你可以（白名单）**

| 行为 | 是否允许 | 说明 |
|---|---|---|
| 个人使用、修改、自用分发 | ✅ | 随便改，不需要告诉任何人 |
| 商业使用 | ✅ | 可以拿去卖（但需自行承担风险，见免责） |
| 闭源二次开发 | ✅ | 你可以改完不开源，Apache-2.0 不要求你开源衍生代码 |
| 再许可为其他许可 | ✅ | 允许 |
| 专利使用 | ✅ | 贡献者以 Apache-2.0 第 3 条授予你专利许可 |

**你必须（义务）**

| 义务 | 具体做法 |
|---|---|
| 保留版权与许可声明 | 分发时**必须**带上 `LICENSE`、`NOTICE`，不得删除源码头部版权注释 |
| 标注修改过的文件 | 你改过的文件**必须**显著标注「已被修改」（加一行注释即可） |
| NOTICE 传递 | 若你的产品含 `NOTICE` 内容，需在你的分发物中一并保留 |
| 不得使用作者商标 | Apache-2.0 第 6 条**不授予商标权**：不得用「杂交版 MOD 作者」名义做背书/宣传 |

**免责声明（重要，必须原样理解）**
> 本软件按「现状」提供，不附带任何明示或暗示担保。作者不对游戏账号封禁、存档损坏、设备故障等后果负责。

### 2.2 这个开源**不包含**什么（AI 必须知道，否则会去要资源）

| 不包含 | 原因 |
|---|---|
| 游戏本体、`.pck` 资源、美术/音效素材 | 版权属于游戏开发者，仓库里一个字节都没有 |
| 游戏原始 DLL / APK | 同上；本仓库只有**我们自己写的** MOD 源码 |
| 签名私钥（`mod.keystore`） | 手机版签名用，不进仓库（默认口令仅供本地调试，见第 8 章） |
| 任何游戏内购/账号/联网校验相关凭据 | 无 |

所以：**AI 提出「先下载游戏资源」「读取 pck 里的贴图」这类方案时，你要意识到它跑不通** —— 只能要求用户自备正版游戏本体。

### 2.3 开源动机与边界（这是干什么的）

- 目的：让 MOD 功能可审阅、可学习、可被社区延续（作者长期维护成本高，开源让功能能被接力）。
- 边界：**只做单机增强 + 局域网/中继联机**；不提供绕过付费、不提供破坏他人游戏体验的联网作弊
  （联机部分有 `Net/CheatPolicy.cs` —— 房主可强制关闭某些开关，就是这条边界的代码化）。
- 二次开发者的道德提醒（写进你的 README 里）：**别在公共联机房里开破坏平衡的功能**。

---

## 3. 环境要求（AI 要先问清环境，再给命令）

### 3.1 必需环境

| 项 | 要求 | 为什么 | 自检命令 |
|---|---|---|---|
| 操作系统 | Windows 10 / 11 x64 | MOD 目标平台；注入器依赖 Windows 文件锁与路径语义 | — |
| PowerShell | **PowerShell 7（`pwsh`）** | ⚠️ **PS 5.1 读无 BOM 的中文 `.ps1` 会按 ANSI 解析 → 引号配对错误 / parse error**。所有构建脚本必须用 `pwsh -File 脚本.ps1` 跑 | `pwsh -v` |
| .NET SDK | **8.0**（PC/手机 MOD 均 `net8.0`；WPF 面板 `net8.0-windows`） | 与游戏自带 GodotSharp 的 TFM 对齐 | `dotnet --list-sdks` |
| `GodotSharp.dll` | **不使用 NuGet 版**，直接 HintPath 引用**游戏目录里那一份** | 版本必须与游戏完全一致，否则 `CS0246` / 运行期类型不匹配 | 见 3.2 路径 |
| Mono.Cecil | 注入器依赖（不通过 NuGet 时放同目录） | IL 读写 | 编译成功即证明 |
| Python | 3.11+（部分工具脚本、pck 解包/打包） | 工具链，不参与 MOD 运行 | `python -V` |
| ilspycmd | `dotnet "D:\杂交版关卡\tools\ilspy\ilspycmd9\tools\net8.0\any\ilspycmd.dll" -p -o <输出目录> <dll>` | **验证游戏内部结构的唯一可信手段**（猜字段 = 修 3 轮） | 能反编译出类型即 OK |
| dotnet-dump | `dotnet tool install -g dotnet-dump` | 排查「界面假死 / CPU 0% 卡住」（能抓主线程栈） | `dotnet-dump --version` |
| JDK + Android SDK | 手机版打包用，**必须显式指定**（`JAVA_HOME` 常被 Android Studio 的 jbr 占坏） | `build_apk` 依赖 | 见 8.3 |

### 3.2 游戏本体与路径（Windows）

| 用途 | 路径 |
|---|---|
| 电脑版游戏主程序集 | `D:\植物大战僵尸杂交版\植物大战僵尸杂交版0.27\植物大战僵尸杂交重制版\data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll` |
| 同目录应有 | `PlantsVsZombies.dll.bak`（**干净基准**，patcher 恢复用）、`PlantsVsZombies.dll.modded`（上一次注入版） |
| 手机版 APK | `D:\植物大战僵尸杂交版\植物大战僵尸杂交版0.27.APK`（解包后作根目录） |
| 手机版注入目标 | 解包根目录下 `assets\.godot\mono\publish\arm64\` 里的主程序集 + `PvzheMod.dll` |
| MOD 运行日志 | `D:\杂交版关卡\mod\mod_log.txt`（`Bootstrap.Log` 输出，**无时间戳、累加写入、看尾部**） |
| 设置持久化 | `D:\杂交版关卡\mod\modsettings.txt`（PC）；手机 = `user://modsettings.txt` |
| 自定义卡池文件 | `D:\杂交版关卡\mod\trickpool.txt`（外置面板写、游戏读，避免超长 URL） |

> ⚠️ **`.bak` 是命根子**：它缺失时 patcher 会基于「上次注入版」合并，**已存在的类型不会被更新** → 「改了但没生效」。
> 部署前永远先确认 `.bak` 存在且大小 = 干净原版（0.27 = 24,386,496 字节量级）。

### 3.3 环境自检清单（让 AI 先跑这个）

```powershell
pwsh -v
dotnet --list-sdks
Test-Path 'D:\植物大战僵尸杂交版\植物大战僵尸杂交版0.27\植物大战僵尸杂交重制版\data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll.bak'
(Get-Item '...\PlantsVsZombies.dll.bak').Length
Get-Process | Where-Object { $_.ProcessName -like '*植物大战僵尸*' }   # 注入前必须关游戏
```

---

## 4. 目录与文件职责（全景地图）

> 读法：先看「一句话」，需要改再看「关键点」。**风险列 = 动它之前必须读第 6、7 章**。

### 4.1 `mod/PvzheMod/` —— 电脑版 MOD 主体（核心中的核心）

| 文件 | 一句话职责 | 关键点 / 风险 |
|---|---|---|
| `Bootstrap.cs` | MOD 的日志与启动辅助 | `Bootstrap.Log()` 写 `mod_log.txt`；`FlushLog()` 用于崩溃前落盘。**任何新模块都要用它记日志** |
| `FrameDriver.cs` | **每帧入口** | 由 patcher 注入到 `SceneManager/MainMenu._PhysicsProcess`；内部降频调用 `GameCheats.OnFrame(root)`（10 帧）与 `ModUI.Ensure(root)`（30 帧）。**要加新的每帧逻辑，加在 `GameCheats.OnFrame` 里，不要在这里堆** |
| `GameCheats.cs`（650KB，partial） | **全部功能的实现总库** | `OnFrame` 是所有功能的主调度；每个功能一对 `ApplyXxx()`；`AnyModActive()` 决定要不要跑（**新开关必须加进 `AnyModActive`，否则功能"什么都不做"**）。高危：静态构造里不许抛异常（见 14 章 TypeInitializationException） |
| `GameCheats.Glove.cs` / `GameCheats.Skin.cs` | 手套 / 皮肤子模块（partial 拆分） | 皮肤要**写存档**：`WriteSkinToSave` → `EmitCharacterSkinSwitched(saveKey, skinKey)`（**第一参数是 saveKey 不是 packetId**） |
| `ModSettings.cs`（32KB） | **所有开关与数值的唯一事实源** | 静态字段（84+ 个）+ `Save()/Load()` 到 `modsettings.txt`。**三层结构缺一层就是假开关**：①字段 ②Save/Load ③有人读它并作用到游戏。加功能务必三层齐 |
| `ModUI.cs`（117KB） | 游戏内悬浮窗 + 设置面板 | ⚠️ **电脑版游戏内面板已被用户要求移除**（`FrameDriver` 里相关调用被注释），现在控制入口是**外置修改器**。改 UI 前先确认用户要的是哪个 |
| `RemoteServer.cs`（50KB） | 本地 HTTP 服务 `127.0.0.1:28999` | 给外置修改器用：`/ping` `/get` `/set` `/events` `/packets` `/entities` `/eprops` `/eset`。**回调不可以在非主线程碰游戏对象**；`/set` 成功后要 `ModSettings.Save()`；单连接、**必须有超时**（4 秒），否则一个半开连接会永久卡住 |
| `EntityTools.cs`（29KB） | 实体属性页的**语义路由** | 用户改的是"血量/攻速/大小/阳光/冷却"，要路由到真实位置：`hp→instance.hitpoints`、`attack→FireComponent.fireInterval`、`scale→transformPoint.Scale`。**直接改节点裸字段没用**（真实玩法状态在 instance/组件里） |
| `ESP.cs` | 透视 / 缩放 | 僵尸 `Scale` **会被游戏每帧重置** → 缩放必须**每帧**写（`WalkScale`），透视框可以 3 帧一次 |
| `GlobalColor.cs` | 颜色律动（16 种） | PC 走自定义 shader（`uniform float style`），手机无 shader 走主题色 switch。注意性能：颜色量化 + 降频（主界面曾因此卡到假死） |
| `BackgroundFX.cs` / `FunMessages.cs` / `SpawnUI.cs` | 背景特效 / 趣味弹幕 / 刷怪 UI | 视觉类，改动风险低；注意别每帧 new 对象 |
| `PvpOverlay.cs` | 联机 PvP 的对战覆盖层 | 与 `Net/` 强耦合 |
| `ZombieBehaviorControllers.cs` / `PassiveControllers.cs` | 僵尸行为 / 被动效果控制器 | 玩法逻辑，改动需真机验证 |
| `CustomBulletManager.cs` / `CustomPlantManager.cs` / `CustomZombieManager.cs` / `CustomCardManager.cs` / `CustomProjectManager.cs` / `CustomProjectPanel.cs` | **自定义内容系统**（子弹/植物/僵尸/卡牌/项目 + 编辑器面板） | 与 `custom_plants/`、`.pvzlevel` 等外部数据文件耦合；涉及 JSON 读写，**必须用 Godot `FileAccess`** |
| `mod/NetTests/` | 协议层单测 | 改 `Net/` 前先让它继续通过 |

### 4.2 `mod/PvzheMod/Net/` —— 自研联机系统（25 个文件，独立于游戏原生联机）

| 文件 | 职责 |
|---|---|
| `NetProtocol.cs` | 帧格式：`[type byte][4字节长度][JSON]`（用 Godot `Json` 而非 `System.Text.Json`，后者被 AOT 裁掉） |
| `NetTransport.cs` | 传输层（含中继）——⚠️ **关键路径已全部"内联"**：`PackFrame`/`TryParseLocal`/`KeyedHashLocal` 是该类自己的私有静态方法，**不许改回去调用 `NetProtocol.Frame`**（会 `Method not found`） |
| `NetCrypto.cs` / `NetAes.cs` / `NetX25519.cs` / `NetSha256.cs` / `NetSecure.cs` | 加密：密钥交换（X25519）、对称加密（AES）、哈希、握手（`NetSecure`） |
| `NetSession.cs`（43KB） / `NetSessionCore.cs` | 会话状态机 |
| `NetGate.cs` | 联机准入 / 一致性门槛 |
| `NetLobby.cs` / `NetRoomSettings.cs` / `NetPeers.cs` | 大厅 / 房间设置（人数、密码）/ 对端表 |
| `NetBattle.cs` / `NetPvp.cs` / `NetMirror.cs` | 战斗同步 / PvP 规则 / **镜像同步**（把对手操作重放到本地） |
| `NetLevelShare.cs` | 关卡共享（联机时下发关卡） |
| `NetBudget.cs` / `NetBitCodec.cs` | 带宽预算 / 位级编解码 |
| `NetCursor.cs` | 远端光标绘制 |
| `NetSun.cs` | 联机阳光同步 |
| `NetUI.cs`（49KB） | 联机界面（创建/加入/房间/聊天） |
| `NetToast.cs` / `NetLog.cs` | 联机提示 / 日志 |
| `CheatPolicy.cs` | ⚠️ **房主强制关闭作弊开关的政策表** —— 联机公平性的落点，改动前想清楚 |

### 4.3 `mod/patcher/` —— 电脑版注入器

| 文件 | 职责 | 关键点 |
|---|---|---|
| `Program.cs`（122KB） | **全部 IL 注入规则** | 流程：从 `.bak` 恢复干净 DLL → `MergeTypes` 把 MOD 类型合并进主程序集 → 按规则注入调用（`InjectFrame` / `InjectNoCooldown` / `InjectForceUsable` / `InjectAttackSpeedPatch` / `InjectCharm` / `InjectExplodeTrick` …）→ `ScanBadIL` 扫描坏 IL → 写回 |
| `cscheck/` | 校验小工具 | ⚠️ **csproj 必须排除 `cscheck/`**，否则编译报 `AssemblyInfo` 重复 |
| `e2e.py` | 端到端检查 | — |

**注入规则的五种模式**（新加注入点时照抄最接近的一种）：
1. **方法开头插调用**（`ldarg.0; call GameCheats.Xxx`）——最常用；
2. **返回值强制**（`InjectReturnZeroIf`：`GetCost` / `GetCostBeforeModifiers` / `GetWavePointCost` 返回 0）；
3. **`ret` 前插调用**（每个 `ret` 前都要插，漏一个分支就漏一半功能）；
4. **`brfalse`/`brtrue` 判定改写**（如跳过攻击判定）；
5. **替换具体指令**（如 `Shovel` 里插 `OnCellShovel`）。

⚠️ **注入顺序陷阱**（`BulletField.Spawn` 的历史事故）：`call/brfalse` 必须**最先**插入（执行顺序最前），`skip` 标签必须**最后**插入。
顺序错了在「条件为假」时进入死循环 → 植物一攻击就冻结全局。

### 4.4 `androidmod/` —— 手机版

| 文件 | 职责 | 关键点 |
|---|---|---|
| `PvzheAndroid/` | 手机版 MOD（与 PC 对称，文件同名） | ⚠️ **不合并类型**（合并会闪退）→ `PvzheMod.dll` 独立随 APK 加载；**打包前必须手动把最新 `PvzheMod.dll` 复制到 `arm64` publish 目录**，否则 APK 里是旧版 |
| `android_patcher/Program.cs`（102KB） | 手机版注入器 | 用 `main.ImportReference(hook)` 引用独立 DLL 里的方法；自动备份 `.clean` |
| `PvzheAndroid/ModUI.cs`（121KB） | 手机版游戏内 UI | 手机**保留**游戏内面板（触屏没法用外置 exe）→ 手机有 UI、PC 没有，**不要照抄** |

### 4.5 `mod/tools/外置修改器/wpf/` —— WPF 遥控面板

| 文件 | 职责 |
|---|---|
| `MainWindow.xaml.cs`（157KB） | 主窗：分类导航 + 功能开关 + 日志 + 调试终端 + 背景视频/图 |
| `MainWindow.Net.cs` | 联机页（部分类拆分） |
| `NetWindow.xaml(.cs)`（49KB） | 联机窗口 |
| `CustomPlantWindow.xaml.cs`（129KB） | 自定义植物编辑器 |
| `SaveTools.cs` | 存档工具（含满级存档生成） |
| `TrickPoolWindow.cs` | 篡改卡池选择器（写 `trickpool.txt` → 触发 `TrickReload`） |
| `ScriptWindow.xaml` / `ScriptEngine.cs` / `ScriptStore.cs` / `ScriptDef.cs` / `ScriptApi.cs` | 内嵌 Roslyn 脚本系统（用户可写脚本驱动 MOD） |
| `App.xaml`（36KB） | 全部资源/样式（换肤改这里即可全局生效） |
| `AcrylicHelper.cs` / `GlassyEffect.ps` / `ToggleSwitch.xaml` / `RainbowSlider.xaml` / `PixelCanvas.cs` | 视觉效果控件 |
| `HotKeyHelper.cs` | 全局热键（Alt+F8 显隐） |
| `remote_ui.py` | 早期 Python/tkinter 版本（已被 WPF 取代，保留参考） |

### 4.6 `relay/` —— 联机中继服务端

| 文件 | 职责 |
|---|---|
| `PvzheRelay/Program.cs` / `RelayServer.cs`（45KB） / `RoomManager.cs` / `Room.cs` | 中继：房间创建/加入、加密封包转发（NAT 穿透失败时走这里） |
| `PvzheRelayCli/Program.cs` | 命令行客户端（压测/调试） |
| `PvzheRelay.Tests/` / `PvzheCryptoTests/` / `PvzheAttackTest/` | 单测 / 加密一致性测试 / 攻击面测试 |

---

## 5. 核心设计（架构与数据流）

### 5.1 三层架构

```
┌──────────────────────────────────────────────────────────────┐
│ ① 注入层（编译期，离线）                                       │
│   mod/patcher 或 android_patcher                             │
│   Mono.Cecil：恢复干净 DLL → 合并/引用 MOD 类型 → 按规则写 IL   │
│   产物：被改写的 PlantsVsZombies.dll / APK                     │
└──────────────────────────────────────────────────────────────┘
                         ↓ 游戏启动
┌──────────────────────────────────────────────────────────────┐
│ ② 运行时层（游戏进程内）                                        │
│   FrameDriver.OnFrame —— 每帧入口（全包 try/catch）             │
│     └─ GameCheats.OnFrame(root)                                │
│          读 ModSettings → ApplyXxx() → 反射改游戏对象           │
│        ModUI.Ensure(root)（仅手机版）                           │
│        RemoteServer.Poll()  ← 处理外置面板请求（主线程执行）      │
└──────────────────────────────────────────────────────────────┘
                         ↕ HTTP 127.0.0.1:28999
┌──────────────────────────────────────────────────────────────┐
│ ③ 控制层（进程外）                                             │
│   外置修改器（WPF）/ 内嵌脚本 / 外置命令                       │
│   读：/get → 开关状态；写：/set?name=X&val=Y                   │
│   一切改动都落到 ModSettings（单一事实源）                      │
└──────────────────────────────────────────────────────────────┘
```

### 5.2 关键设计决策与「为什么」

| 决策 | 为什么这样做 | 反例（别这么干） |
|---|---|---|
| **编译期注入，不用 BepInEx/Harmony** | 该游戏是 Godot 导出 + AOT 裁剪，运行时补丁框架依赖的反射 API 大量缺失；编译期改 IL 最稳 | 别引入运行时 Hook 框架 |
| **反射访问游戏对象，不硬引用游戏类型** | MOD 编译时只引用 `GodotSharp.dll`；游戏类型全部 `FindType("TowerDefenseManager")` 反射拿 —— 这样跨游戏小版本升级不易崩 | 别 `using` 游戏命名空间去强类型调用（版本一变全红） |
| **`ModSettings` 是唯一事实源** | 三个 UI（游戏内 / 外置 / 脚本）都读写它，天然一致；`/get` `/set` 靠反射遍历它，**加字段零改动 RemoteServer** | 别在 UI 里存状态 |
| **打开关要三层齐（字段 / SaveLoad / 消费点）** | 缺消费点 = 假开关（用户会报"开了没用"，实际代码里根本没读它） | 别只加 UI |
| **回调与游戏对象隔离** | HTTP 回调跑在线程池线程，碰 IL2CPP/Godot 对象会崩 → 只解析 JSON + 写 ConfigEntry/字段，需要碰游戏对象的操作排队到主线程 | 别在回调里直接改游戏节点 |
| **一切都要能自证** | 用户报"没用"占排障时间 80%，所以要有：日志（`Bootstrap.Log`）、诊断埋点（calls/applied）、状态回报（`/events`）、探针工具 | 别写空 catch |

### 5.3 加一个功能的完整数据流（务必按此链路）

```
① ModSettings.cs   加字段（bool/int/string）+ Save()/Load() + AnyModActive() 里加条件
② GameCheats.cs    写 ApplyXxx()，在 OnFrame 里按需调用（注意降频，别每帧全量）
③ 若需要改游戏行为而非读状态 → patcher/Program.cs 加注入规则（并同步 android_patcher）
④ 外置面板 wpf/MainWindow.xaml.cs 的 Feats 表加中文名（若不加入口，用户看不到开关）
⑤ 手机版 ModUI.cs 加 CheckButton（手机没有外置面板）
⑥ 编译 → 注入 → UTF-16 校验 → 真机验证 + 日志
```

---

## 6. 游戏内部逻辑（必须知道的真实结构）

> **这一章是全文最重要的部分。** 下列事实全部来自 0.27 反编译 / IL 实测 / 探针验证。
> AI 若需要访问游戏对象，**必须**按这里给的路径走，不许自己猜。

### 6.1 三个"总入口"（几乎所有功能都要用）

| 目标 | 正确路径 | 坑 |
|---|---|---|
| 拿塔防管理器 | `TowerDefenseManager.Instance` 是**属性**（`get_Instance`），不是字段 | 用 `GetProperty("Instance", Static)` |
| 拿当前关卡控制器 | `TowerDefenseManager.Instance` → **`currentControl`（字段！）** | 历史上写成 `CurrentControl` 属性 → 恒 null → 「无当前关卡」直接 return，功能整个没执行 |
| 拿关卡 feature | `control.featureDictionary`（**public `Dictionary<StringName, TowerDefenseBattleFeature>`**，按 key 取） | 或 `GetFeature<T>(StringName)` —— ⚠️ **有泛型重载，`GetMethod("GetFeature", types)` 会抛 `AmbiguousMatchException`** → 必须用自定义 `FindMethodExact`（跳过 `IsGenericMethodDefinition`）或直接读字典 |

### 6.2 功能 ↔ 真实结构对照表（改代码前先查这张表）

| 功能 | 真实位置（0.27 实测） | 备注 |
|---|---|---|
| 无限阳光 | 阳光值在 `card_slot_battle` / `First.Sun`（版本相关） | PC 与手机实现不同 |
| 金币 | `ApplyCoin` → `SetNum(1000000000)` | 无限金币 = 10 亿 |
| 零消费 | 注入 `GetCost(bool)` / `GetCostBeforeModifiers()` / `GetWavePointCost()` | ⚠️ 金卡/波点卡走**后两个**，只注入 `GetCost` 会"没用" |
| 卡价不涨价 | `GetCostRise` | — |
| **植物射速** | **`FireComponent.fireInterval`（Single）+ `fireIntervalBase`**，射击后 `timer` 重置为 `fireInterval` | ⚠️ 植物节点上那个同名 `fireInterval`（Double）**不是射速**（只被序列化）→ 曾因此修 3 轮 |
| **僵尸攻速** | `AttackComponent.timer`（每帧 `timer -= delta*timeScale`，`<=0` 触发攻击） | 注入 `AttackComponent.BatchUpdateValidated` 开头做 `timer -= delta*(mult-1)` 最可靠 |
| 无冷却 | `TowerDefenseInGamePacketShow.set_coldDownOpen` | 用 **setter Invoke**，别用 `PropertyInfo.SetValue` |
| 强制选卡 / 无视紫卡 | `TowerDefensePacketConfig.Unlock()/IsUnlocked()` + `get_packetBankMethod` | ⚠️ 图鉴解锁也读 `Unlock()` → 注入会让**图鉴全解锁**（需 `IsAlmanacOpen()` 旁路） |
| 无视地形 / 无视警戒线 | `TowerDefenseCellInstance.CanPacketPlant`；警戒线 = **`TowerDefenseBattleFeatureWarningLine : TowerDefenseBattleComponentBase : Resource`（不在场景树里！）** | 遍历树找它永远找不到；正确做法：找 `WarningLine` 节点（Node2D）→ 读其 `feature` 字段拿 Resource → 设 `_triggered=true` |
| 手套 | `TowerDefenseBattleFeatureGlove`，`Init()` 只实例化 `GloveManager`；真正生效要额外跑 `GameInit()` → `InitializeManager()` → `GameStart()` → `RegisterTool` | 光 `AddFeature` 不出现 —— 必须手动 Invoke 那两个 async 方法；存档标记 `SetFeatureValue("Glove", 1)` |
| 皮肤 | 卡片存档 `Key.Custom` + `EmitCharacterSkinSwitched(saveKey, skinKey)` | ⚠️ 第一参数是 **saveKey**，不是 packetId |
| 魅惑 | **游戏原生 `Hypnoses` buff**（`InvokeHypnoses()`） | ⚠️ 手动反转 `camp` + 翻 `Scale.X` **没用**（移动组件不认 camp，只认 buff） |
| 子弹（追踪/随机/跟随鼠标） | `BulletField._data`（`BulletData[]`）、`BulletData.trackOpen`、`TryChangeBulletDataInPlace(int, cfg)` | ⚠️ `TryChangeBulletDataInPlace` 是 **internal**；返回的是**枚举状态码**不是 idx；换完必须**读回 `_data[idx].config` 验证**（不要信返回码） |
| 地刺类"尖刺子弹" | `TowerDefenseItemSpikeball : TowerDefenseItem`（**道具不是植物**），`ComponentAttack()` 近战 | 不走 `BulletField` → 随机子弹机制**对它无效**（这是机制限制，不是 bug） |
| 刷怪倍数 | `featureDictionary["Wave"]` → `.config` → `wave`（属性）→ 每波 `.spawn`（**小写**，字段）→ `.num`（**小写 Int32**） | 曾把结构全猜错（大写 `Spawn`/`Num`）→ 一个都没生效 |
| 雨/雷暴 | `TowerDefenseBattleFeatureScreenEffect`：`Has/Add/DeleteScreenEffect(name)`，key = `"Rain"` / `"Storm"` | 用 `new Godot.StringName("ScreenEffect")` 取 feature |
| 关卡注册表（全关卡） | 明文 `Asset/Config/Level/LevelResource.json`（551 个 `SaveKey`） | 存档字典里**没有未游玩关卡** → 只遍历存档会导致"一键通关漏新关卡" |
| 关卡选择/胜利 | `TowerDefenseBattleFeatureWave`：`waveStart=true; waveFinal=true; EmitFinal(); awaitSpawn=false`（官方 `_CmdInstantWin` 做法） | 比 KillAll 僵尸 + `EmitGameVictory` 可靠 |
| 盲盒/种子雨/传送带（篡改） | `SlotMachine._slotItems`（List）；`RainMode._packetList`（List）；`ConveyorBelt.packetList`（Godot 数组，**别直接改**，改呈现端 `Init`） | 只改 .NET List 的部分可靠；Godot 数组的部分要换路子 |
| 礼盒/抽卡植物 | `TowerDefensePlantPresentBox` / `PresentBoxGreen` / `BYWZ` / `Upgradebean` / `GardenSet` / `LampShroom` / `MagicBean` / `LuckyBlover` 的 `Explode()` | `LuckyBlover` 走 `AddPacket`（进卡槽），其余走 `SpawnPacket`（掉地上）；**`FillArgs` 只填类型默认值**，`useRandf` 不显式传会变 false |
| 存档（PC） | 二进制 RSRC 格式：`%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\Csharp\save.res` | ⚠️ 根目录还有个同名 `save.res` 是**旧版**，改错文件 = 白干；格式为 Godot 4.7 RSRC v6（工具 `rsrc.py`） |
| 修改器设置 | `mod/modsettings.txt`（PC）/ `user://modsettings.txt`（手机） | 纯文本 key=value |

### 6.3 Godot 4.7 特有 API 名称陷阱（写错就是编译不过 / 运行崩）

| 你以为 | 实际 |
|---|---|
| `UDP` | `PacketPeerUdp` |
| `TCP` / `TCP_Server` | `StreamPeerTcp` / `TcpServer` |
| `TcpServer.Listen(port, ...)` | 端口参数是 **`ushort`**，必须强转 `(ushort)` |
| `StreamPeer.GetData(n)` 返回 `byte[]` | 返回 **`Godot.Collections.Array`**，`arr[0]`=Error、`arr[1]` 要 **`.AsByteArray()`** |
| `IsConnectedToHost()` | 不存在；用 `GetStatus()` → `Godot.StreamPeerSocket.Status`（None/Connecting/Connected/Error），且**必须持续 `Poll()` 才能推进到 Connected** |
| `Godot.Key.Tilde` / `Key.QuoteLeft` | **`Godot.Key.Quoteleft`**（小写 l）/ `Key.Asciitilde` / `Key.Volumedown` / `Key.Capslock` |
| `new Variant(long)` | `Godot.Variant.From(long)` |
| `json.Data` 直接当 `Dictionary` | 它是 **`Variant`**，必须先 `data is Godot.Variant vv` 再按 `VariantType` 调 `AsGodotDictionary()` / `AsGodotArray()`（不解包会收集到 0 个元素） |
| `GodotObject.QueueFree()` | `QueueFree` 是 **`Node`** 的方法（先 `is Godot.Node n0`） |
| `Image.GetRegion()/Resize()/Crop()` 返回新图 | **原地修改（void）** |
| `DirAccess.GetFiles()` 返回 `List<string>` | 返回 **`string[]`**，用 `.Length`（用 `.Count` 会 CS0019） |
| `ItemList.GetSelectedItems()` 返回集合 | 返回 `int[]`（用 `.Length`） |
| `IP.GetLocalAddresses()` | 返回 `string[]` |
| `Label.AddThemeFontSizeOverride(...)` | StringName 绑定，**别用**；改 `label.Modulate` + 默认字号 |

### 6.4 Godot 4.7 网络类（`System.Net.Sockets` 被裁，必须用 Godot 原生）

- 服务器：`Godot.TcpServer`：`Listen((ushort)port, "127.0.0.1")` → `Error`；`TakeConnection()` → `StreamPeerTcp`（**无连接时返回 null**）；`IsNode=false`，是 RefCounted，**不用进场景树**。
- 连接：`StreamPeerTcp : StreamPeerSocket : StreamPeer`（`Poll()` / `GetStatus()` / `DisconnectFromHost()`）。
- ⚠️ `System.Net.Sockets.TcpListener.AcceptTcpClient()` 在本游戏 AOT 运行时抛 **`MissingMethodException`** —— 这就是 `RemoteServer` 用 Godot 原生类的原因。

---

## 7. 运行时限制（AOT 裁剪）与 API 白名单

> 游戏是 **Godot 4.7 mono release + AOT 裁剪**。很多 BCL API **编译期完全正常、运行期抛 `Method not found` / `EntryPointNotFoundException`**。
> 这是本工程最大的隐形杀手：**语法检查、编译、甚至单元测试都不会报警**。

### 7.1 黑名单（实测抛异常，禁止使用）

| 禁止 | 会抛什么 |
|---|---|
| `ushort.TryParse` / `byte.TryParse` / `uint.TryParse`（含 `NumberStyles` 重载） | Method not found |
| `StringBuilder.Append(ushort/byte/…)` 等**数值重载** | `Method not found: StringBuilder.Append(UInt16)` |
| `StringBuilder.Append` 带格式串的 `ToString("X8")` | 同上（带格式串的重载也可能被裁） |
| `Convert.ToInt64(string, int)` / `Convert.*`（多数） | Method not found |
| `String.LastIndexOfAny(char[])` | `Method not found: Int32 System.String.LastIndexOfAny(Char[])` |
| `System.IO.File.*` / `System.IO.Path.*`（关键路径） | Method not found（**必须换 Godot `FileAccess`/`DirAccess`**） |
| `System.Net.Sockets.*`（TcpListener/Socket） | MissingMethodException |
| `System.Text.Json` | 用 Godot `Json.Parse/Stringify` |
| `LINQ`（`Enumerable.Sum` 等） | 编译期 CS1061 / 运行期缺失 |
| MOD 新类型的**接口派发**（`INetTransport t; t.Poll()`） | `EntryPointNotFoundException`（堆栈只有 `at INetTransport.Poll()`） |
| **跨类型静态方法调用**（同一程序集内也算！`NetProtocol.Frame(...)`） | `Method not found`（随机消失，极难查） |
| `ex.GetType().Name`（运行时类型元数据） | 高风险，避免 |
| `HashSet<T>` | 避免（用 `List` + `Dictionary`） |
| `PropertyInfo.SetValue(obj, val)` 给游戏类型属性赋值 | .NET 9 下抛 `MissingMethodException` → **改用 `setter.GetSetMethod(true).Invoke(obj, args)`**（历史坑：无冷却、血量倍率、植物射速都踩过） |
| `Thread.Sleep` 注入到核心路径方法开头 | 主线程同步等待 → **假死**（只在确认的非主线程分支里才可节流） |

### 7.2 白名单（实测可用，放心用）

- 字符串：**`+` 拼接**（数值也直接拼）、`int.TryParse`（仅 int 版本）、手写十六进制解析
- 集合：`List<T>` / `Dictionary<K,V>` 的 `Add/Count/索引/TryGetValue`；`Godot.Collections.Array/Dictionary`（注意 Variant 解包）
- 反射：`GetMethod/GetField/GetProperty/Invoke/SetValue`（MOD 大量使用；**注意 `GetMethod` 默认只搜 public，`internal` 方法返回 null → 要传 `BindingFlags.NonPublic|Instance|Static`**）
- 异常：`ex.StackTrace`（可用，排障利器）、`ex.Message`
- 文件：Godot `FileAccess.Open/GetAsText/StoreString`、`DirAccess.Open/GetFiles/MakeDirAbsolute`、`OS.GetExecutablePath`
- Godot：`Node.GetViewport()`、`Viewport.GetVisibleRect()`、`Node2D.GlobalPosition`、`GodotObject.GetInstanceId()`、`Engine.GetMainLoop()`、`RenderingServer.Singleton.ForceDraw()`

### 7.3 代码风格硬要求（为规避裁剪而设，不是偏好问题）

| 要求 | 原因 |
|---|---|
| **不用 LINQ** | 被裁 |
| **数值转字符串用 `+` 拼接**，不 `ToString(格式)` | 带格式串重载可能被裁 |
| **关键路径算法内联到调用者类**（复制一份私有静态方法） | 跨类型静态调用会随机 `Method not found` |
| **网络传输字段用具体类**，不要声明成接口变量 | 接口派发 EntryPointNotFoundException |
| **每帧入口全包 try/catch + 记日志** | 一段抛异常 → 主循环中断 → "所有功能失效" |
| **反射调用一律走自研安全包装**（`FindMethodExact` / `SetPropOrField` / `TrySetField`） | 规避泛型重载歧义、`internal` 不可见、`PropertyInfo.SetValue` 崩溃 |
| **文件路径解析手写循环**（找最后一个 `\` / `/`） | `Path.*` / `LastIndexOfAny` 被裁 |

---

## 8. 构建 / 注入 / 部署（每一步都要能自证）

### 8.1 电脑版

```powershell
# 0) 关游戏（DLL 被占用会写不进去）；确认干净基准存在
Get-Process | Where-Object { $_.ProcessName -like '*植物大战僵尸*' } | Stop-Process -Force
Test-Path 'D:\植物大战僵尸杂交版\植物大战僵尸杂交版0.27\植物大战僵尸杂交重制版\data_PlantsVsZombies_windows_x86_64\PlantsVsZombies.dll.bak'

# 1) 编译 MOD（0 error 才算过）
dotnet build 'D:\杂交版关卡\mod\PvzheMod\PvzheMod.csproj' -c Release

# 2) 注入（patcher：从 .bak 恢复 → MergeTypes 合并 → 按规则写 IL → ScanBadIL → 写回）
pwsh -File 'D:\杂交版关卡\mod\build_pc.ps1'
#    或者直接跑注入器；它必须打印每个注入点命中情况，且结尾 bad=0

# 3) 验证（三重）
(Get-Item '...\PlantsVsZombies.dll').Length        # 干净 24,386,496 → 注入后约 25.7MB
[System.Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes('...\PlantsVsZombies.dll')) -match '你新加的方法名'

# 4) 复制成品到发布目录
Copy-Item '...\PlantsVsZombies.dll' 'D:\杂交版关卡\mod\release\成品DLL\'
```

**要点**
- `build_pc.ps1` **必须用 `pwsh -File`** 跑：PS 5.1 读无 BOM 的中文脚本会按 ANSI 解析 → 引号配对错 → parse error。
- `PvzheMod.csproj` 里 `GodotSharp.dll` 的 `HintPath` **必须指向当前游戏版本目录**；指向已删除的旧版本目录 → 全工程 `CS0246 找不到 Godot`。
- DLL 可能被 **VS Code 遗留的 PowerShell 终端句柄**锁住（不是游戏进程）→ 用 Restart Manager 定位（`mod/work/findlock`），杀掉那个 `pwsh`。
- 注入器 `csproj` 必须排除 `cscheck/`，否则 `AssemblyInfo` 重复编译失败。

### 8.2 手机版（顺序不能错）

```powershell
# 1) 编译（产出 PvzheMod.dll）
dotnet build 'D:\杂交版关卡\androidmod\PvzheAndroid\PvzheMod.csproj' -c Release
#    ⚠️ 注意输出在 bin\Release\net8.0\PvzheMod.dll（有 TFM 子目录）

# 2) ★ 手动把最新 PvzheMod.dll 复制到 arm64 publish 目录（否则 APK 里是旧版！）
$gameDir = 'D:\植物大战僵尸杂交版\assets\.godot\mono\publish\arm64'
Copy-Item '...\bin\Release\net8.0\PvzheMod.dll' $gameDir -Force

# 3) 注入（android_patcher：ImportReference 引用独立 DLL 里的方法）
dotnet run --project 'D:\杂交版关卡\androidmod\android_patcher\patcher.csproj' -c Release

# 4) 打 APK（必须 pwsh -File）
pwsh -File 'D:\杂交版关卡\androidmod\build_apk.ps1'
```

**要点**
- 手机版 **不合并类型**（合并必闪退）→ `PvzheMod.dll` 作为独立程序集随包加载。所以「复制 DLL」这一步是**最容易漏、最难发现**的坑（APK 里静静躺着旧版）。
- `JAVA_HOME` 常被 Android Studio 的 `jbr` 占坏 → 显式覆盖：`$env:JAVA_HOME='D:\杂交版关卡\androidmod\tools\jdk'`；build-tools 用已安装的那份。
- 打包后**核对 APK 内 DLL 的 MD5** 与刚构建的一致（可以写进脚本），否则等于没更新。
- 手机版设置写在 `user://modsettings.txt`；手机版**保留游戏内面板**（没有外置 exe）。

### 8.3 外置修改器（WPF）

```powershell
dotnet publish 'D:\杂交版关卡\mod\tools\外置修改器\wpf\PvzheRemote.csproj' -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish_sf
```

- ⚠️ **必须带 `-p:IncludeNativeLibrariesForSelfExtract=true`**。漏掉 → 产出目录会多出 5 个原生 DLL、只拷 exe 出去就**启动即崩**：
  `DllNotFoundException: Dll was not found. → MS.Win32.HwndSubclass.SubclassWndProc`，退出码 **`-1073740771`（0xC000041D）**。
- **exe 大小可当指纹**：正确 ≈ **77.3MB**（单文件内含原生库）；≈73.6MB 就是漏了参数。
- 崩溃排查：`Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-20)}` 捞 `.NET Runtime` 事件看堆栈。
- 杀进程要**按路径**匹配（进程名是中文）：`Get-Process | Where-Object { $_.Path -like '*外置修改器*' } | Stop-Process -Force`。

### 8.4 发布目录惯例（用户明确要求）

`D:\杂交版关卡\release\最终打包_YYYYMMDD\`（**每次新建当天日期目录**），内含：
`杂交版MOD_手机版.apk` + `.apk.zip`、`杂交版MOD包_电脑版.zip`、`PlantsVsZombies_电脑版注入DLL.dll`、`外置修改器\`（exe + 使用说明）、一键注入器 exe。

⚠️ **打包用 .NET 的 `ZipFile::CreateFromDirectory` / `ZipFile::Open(path, 'Create')`，不要用 `Compress-Archive`**：
`Compress-Archive -Force` 覆盖已存在的 zip 时会在目标目录生成**同名无扩展名目录**（内含完整副本，几百 MB，还删不掉）。

---

## 9. 编译不通过怎么办（标准处置 SOP）

> 原则：**先归类，再取证，最后才改**。不要看到红字就乱改签名。

### 9.1 第一步永远是：看完整错误输出

```
❌ 错误：$log | Select-Object -Last 2   ← 会把真正的错误行吞掉，只剩"0 错误"的假象
✅ 正确：$log | Select-String -Pattern 'error CS|error MSB|错误' | Select-Object -First 10
```
（真实事故：因为 `-Last 2`，连续多次以为编译成功，实际早就失败了。）

### 9.2 错误 → 根因 → 处置 对照表

| 报错 | 根因 | 处置 |
|---|---|---|
| `CS0246 找不到类型 Godot/GodotSharp` | `csproj` 的 `GodotSharp` HintPath 指向**已删除的旧版本游戏目录** | 改指当前游戏版本目录（0.27） |
| `CS0103/CS0118 名称不存在` | 没有 `using Godot;` 或 `partial` 另一半没编译进工程 | 检查 using 与 csproj 的 `Compile` 包含 |
| `CS0019 运算符不适用于 string[] 与 int` | 把 `string[]` 当 `List<>` 用 `.Count` | 改 `.Length` |
| `CS0111/CS0102 已定义成员` | 手机版工程里**已有同名方法**（如 `CollectZombies`） | 复用已有的，删掉重复定义 |
| `CS0414 字段已赋值但从未使用` | 删功能时留了残骸 | 删字段 |
| `CS0165 使用了未赋值的局部变量` | 分支里没初始化（如 `Colors.White` 分支） | 补初始化 |
| `CS0122 不可访问（protected/private）` | 跨类调用 | 改成 `public static`（如 `FileNameNoExt`） |
| `AssemblyInfo 重复` | patcher 工程没排除 `cscheck/` 子目录 | csproj 排除 |
| **Cecil 写回时** `InvalidCastException: TypeReference→IMethodSignature`（ComputeStackDelta） | **MOD 里定义了与已有方法重名的重载**（尤其 `object`/`double` 参数）→ 合并后 IL 损坏 | 改名 / 删掉那个重载，复用已有方法 |
| 运行期 `MissingMethodException / Method not found` | **AOT 裁剪**：用了第 7 章黑名单 API | 换白名单写法（`+` 拼接、Godot `FileAccess`、setter Invoke…） |
| 运行期 `AmbiguousMatchException` | `GetMethod("GetFeature", types)` 撞上泛型重载 | 用 `FindMethodExact`（跳过 `IsGenericMethodDefinition`）或直接读字典 |
| 运行期 `TypeInitializationException` | **静态类静态字段初始化器抛异常** → 该类所有成员全废（表现为"所有功能同时失效"） | 查静态初始化器（如忽略大小写字典的重复 key）；**确保 `OnFrame` 的 catch 记录异常与 InnerException** |
| 运行期 `NullReferenceException` 在功能里 | 反射拿到的对象为 null（字段名/路径错、或不在关卡内） | 用第 6 章的**正确路径** + 加"对象为空"日志 |
| 界面**假死 / CPU 0%** | 主线程同步等待（资源加载死锁）；或给核心路径注入了 `Thread.Sleep` | `dotnet-dump` 抓主线程栈定位；移除核心路径的 sleep |
| **编译 0 错误、游戏里却没变化** | ①`.bak` 缺失 → patcher 基于上次注入版合并、**已存在类型不更新**；②注入到了另一个游戏目录；③游戏没重启 | 恢复干净 `.bak` 重注入；核对目标目录；UTF-16 搜 DLL 确认新代码在里面；重进游戏 |

### 9.3 三条"不要"

1. **不要为了编过去而注释掉调用**。如果某方法找不到，说明反射路径错了或类型名变了 —— 去查（反编译/探针），不要 `// TODO` 绕过。
2. **不要删用户的功能来消除报错**。删功能必须先问。
3. **不要一次改 5 个东西再编译**。一次一个变量，坏了才知道是谁坏的。

---

## 10. 改一个功能的端到端流程（照抄这张清单）

**任务示例**：给"无限阳光"加一个"阳光上限也可以改"的数值开关。

| 步 | 动作 | 自证 |
|---|---|---|
| 1 | **读记忆/文档**：确认阳光真实位置（第 6.2 章） | 能说出取证方式 |
| 2 | `ModSettings.cs`：加字段 `public static int SunCap = 9999;` + `Save()`/`Load()` + 若影响判定则加进 `AnyModActive()` | 三层结构第①②层齐 |
| 3 | `GameCheats.cs`：写 `ApplySunCap()`，在 `OnFrame` 里按降频调用；对游戏属性赋值**用 setter Invoke** | 第③层（消费点）齐 |
| 4 | 外置面板 `wpf/MainWindow.xaml.cs` 的 `Feats` 表加中文名（否则用户看不见） | 面板能显示 |
| 5 | 手机版同步：`PvzheAndroid/ModSettings.cs` + `GameCheats.cs` + `ModUI.cs` 加 `AddSwitch` | PC/手机对称 |
| 6 | `dotnet build` 两个工程 | 0 error |
| 7 | 注入（PC + 手机，按第 8 章顺序） | patcher 打印命中 + `bad=0` |
| 8 | **UTF-16 字符串搜索**注入后的 DLL | 搜到新方法名 |
| 9 | 真机验证：进关卡 → 调开关 → 看日志 | 给出「期望现象 + 失败时看哪条日志」 |
| 10 | 更新发布目录（第 8.4 章） | zip/DLL/APK 时间戳是新 |

**降频纪律**：`OnFrame` 是每帧调用的，任何"遍历全场对象/反射扫描"的活儿必须降频
（`if (++_timer % 30 == 0)`），否则一进关卡就掉帧。已有先例：`AutoRandomizeSeedBank` 10 帧、`ApplyTrickFeatures` 15 帧、`ApplySpawnMultiplier` 15 帧、`ApplyCrystalInfinite` 120 帧。

---

## 11. 设计文档怎么写（本工程的文档规范）

### 11.1 存放位置（固定）

| 类型 | 路径 | 命名 |
|---|---|---|
| 设计规格 | `docs/superpowers/specs/` | `YYYY-MM-DD-<主题>-design.md` |
| 实施计划 | `docs/superpowers/plans/` | `YYYY-MM-DD-<主题>.md` |
| 排障记录 | `docs/superpowers/notes/` | `YYYY-MM-DD-<现象>-root-cause.md` |

### 11.2 设计文档模板（12 节，缺一节就是不合格）

```markdown
# <功能名> 设计

## 1. 背景与动机
（用户原话引用 + 现状为什么做不到）

## 2. 目标（可验收）
- [ ] 现象级：<用户能看到的可验证结果>
（禁止写"优化体验"这种不可验收的目标）

## 3. 非目标（明确不做）
（防止 AI 顺手扩展）

## 4. 现状与取证
| 事实 | 值 | 取证方式（反编译/IL/探针/实测） |
|---|---|---|
（**每条游戏内部事实都必须写取证方式**；没取证的写"待验证"并列为任务）

## 5. 方案
（主方案 + 关键代码路径：文件 → 方法 → 注入点）

## 6. 备选方案与被否原因
（至少写 1 个，说明为什么不用；历史教训：猜字段导致返工）

## 7. 数据结构 / 协议变更
（ModSettings 新字段、HTTP 新端点、文件格式）

## 8. 风险与影响面
（可能影响哪些已有功能；手机/电脑差异；性能）

## 9. 验证方法（必须是可执行的命令或步骤）
1. 编译：`dotnet build ...` → 0 error
2. 注入：patcher 输出 `<注入点>` + `bad=0`
3. 落地：UTF-16 搜索到 `<方法名>`
4. 真机：进 X 界面 → 开 Y 开关 → 期望 Z；失败看日志关键字 `...`

## 10. 回滚方案
（改哪个文件能退回；备份在哪）

## 11. 任务拆解（每步一个可独立验证的提交）
- [ ] T1 ...
## 12. 验收标准
（对着第 2 节的清单逐条打勾）
```

### 11.3 写文档的三条硬要求

1. **写死取证方式**："`FireComponent.fireInterval` 是 Single（ilspycmd 反编译 0.27 `FireComponent`）" ✅ ／ "应该是 fireInterval" ❌
2. **写死验证命令**：任何"完成了"的判定，都必须能用一条命令或一个界面动作复现。
3. **不许写"后续优化"**：要么进任务列表，要么明确进"非目标"。

### 11.4 反例（历史真实翻车）

> 设计文档里写了「`liekabao` 等五个标记置 **true** = 卡包全解锁」。
> 实际反编译发现：这五个标记是**排除条件**（true = 已买过 → 别再刷），置 true 会把卡包**全踢出货架**。
> **教训：文档写的是"我的理解"，不是"事实"。凡涉及游戏语义的，必须附反编译片段或 IL 证据。**

---

## 12. 工程标准（写代码必须遵守）

### 12.1 代码组织

| 标准 | 说明 |
|---|---|
| `partial` 拆分 | `GameCheats` 过大时按领域拆：`GameCheats.Glove.cs` / `GameCheats.Skin.cs`；文件名 = 主类名 + 领域 |
| 单一职责 | 每帧调度在 `FrameDriver`，**功能实现只在 `GameCheats`**，UI 只在 `ModUI`/面板 |
| 重名禁令 | **不许定义与已有方法重名的重载**（尤其 `object`/`double`）→ 会毁 patcher 合并 |
| 反射统一入口 | 所有反射走 `FindType/FindMethodExact/FindPropOrFieldVal/SetPropOrField/TrySetField` 包装，不许各处自己写 `GetMethod` |
| 复用已验证路径 | 取关卡对象统一用 `GetTdmInstance()` → `currentControl`(**字段**) → `featureDictionary`；**不许各功能自己写一套**（历史上三处各写一套，三处全错） |

### 12.2 日志与异常

| 标准 | 说明 |
|---|---|
| 每帧入口全包 try/catch | 且 **catch 里必须记日志**（空 catch 是禁忌） |
| 一次性日志去重 | 用 `LogOnce(tag, msg)` 之类，避免每帧刷屏；**首次/异常才记** |
| 分阶段诊断 | 复杂功能加阶段日志（`篡改: 找不到池字段` / `已快照 N 项` / `已锁定 N 张`），让用户能一句日志定位 |
| 不吞异常 | 不许 `catch { }` |
| 日志位置 | PC `mod/mod_log.txt`；手机 `user://mod_log.txt`；Godot 自身日志 `%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\logs\godot.log` |

### 12.3 性能

| 标准 | 说明 |
|---|---|
| 降频 | 遍历/反射扫描类一律 10~120 帧一次，不许每帧 |
| 缓存 | 类型/字段/方法/Mesh 结果缓存（`_xxxType` 静态字段）；颜色量化（同色保持约 10 帧） |
| 不 new 对象 | 每帧路径上不 new（尤其 `LinearGradientBrush`、`Image`、`byte[]`） |
| 大操作分帧 | 批量操作分帧做（如"分帧扫描皮肤：每帧 5 张"），避免一顿卡死 |

### 12.4 UI / 文案规范

| 标准 | 说明 |
|---|---|
| **不用 emoji** | 用户明确要求过：按钮/标签/提示一律纯文字（或矢量图标），不许 emoji |
| 保留但别新增推广位 | 「B站跳转」「杂交MOD（搜索关注）」保留；**「赞赏作者」已删除**，不要再加回来 |
| 面板交互两条铁律 | ①**乐观更新**（点击先认下自己的值，游戏回信再摘标记）②**不要每次 Tick 整页重建 UI**（会让点击落空） |
| 开关三层齐 | 加开关 = ①`ModSettings` 字段 ②`Save/Load` ③**有代码读它**（缺③= 假开关） |
| 总开关语义 | 若某功能有"总开关 + 子开关"，总开关要等于**全开**（历史上判定写成"总 且 子"，只开总开关 = 什么都不做，用户报"没用"） |

### 12.5 兼容与对称

| 标准 | 说明 |
|---|---|
| 版本适配 | 当前目标 **0.27**；升级游戏版本要重跑注入并核对注入点存在性（用 `probe_cmp` 对比两版 DLL） |
| PC ↔ 手机对称 | 功能改动两份都要改；**差异只在**：PC 靠合并、手机靠独立 DLL；PC 无游戏内面板、手机有；手机无 `GetTreeRoot()`（用 `_lastRoot`）；僵尸缓存字段名不同（PC `_cacheZombies` / 手机 `_zombieCache`） |
| 向后兼容设置文件 | `modsettings.txt` 加字段要能容忍旧文件（缺字段用默认值） |

---

## 13. 调试与诊断手册

### 13.1 日志的正确读法（重要）

- `mod_log.txt` 是**累加、无时间戳**的。看"最后几行"容易被旧异常误导。
- 正确做法：**先记当前行数 → 触发操作 → 只看新增的行**。
  ```powershell
  $p='D:\杂交版关卡\mod\mod_log.txt'; $n=(Get-Content $p).Count; "before=$n"
  # 触发操作...
  Get-Content $p | Select-Object -Skip $n
  ```

### 13.2 探针工具（`probe3`，改代码前先用它取证）

| 命令 | 用途 |
|---|---|
| `find:<关键字>` | 找类型 |
| `methods:<类型>[:关键字]` | 列方法完整签名（含泛型/static） |
| `[F] <类型>` / `[P] <类型>` | 列字段 / 属性（**注意只搜 public 会漏 internal/private**） |
| `il:<类型>.<方法>` | 反汇编方法体（看真实执行顺序、字段读写） |
| `ref:<方法名>[:类型过滤]` | **找调用方/订阅方 —— 最有用**（定位"种下随机给卡的植物族"就靠它） |
| `enum:<类型>[:嵌套枚举]` | 列枚举值（如 `ObjectManagerConfig.OBJECT`） |

### 13.3 反编译（唯一可信的证据来源）

```powershell
dotnet "D:\杂交版关卡\tools\ilspy\ilspycmd9\tools\net8.0\any\ilspycmd.dll" -p -o D:\tmp\dec '...\PlantsVsZombies.dll'
# 单类型/单方法
dotnet "D:\杂交版关卡\tools\ilspy\ilspycmd9\tools\net8.0\any\ilspycmd.dll" -t FireComponent '...\PlantsVsZombies.dll' | Select-String 'fireInterval'
```

### 13.4 卡死 / 假死

```powershell
dotnet tool install -g dotnet-dump
dotnet-dump collect -p <游戏PID>
dotnet-dump analyze <dump文件> -c "clrstack" -c "exit"    # 看主线程卡在哪一行
```
历史案例：`ModUI.OnSettingsPressed → GetAllPacketIdsWithSkins → GetConfig → GetPacket → LoadCharacterBindingOnMainThread → Task.Wait`（主线程无限等待 → 假死）→ 解法是去掉自动全量扫描、改分帧。

### 13.5 "功能没用"的标准排查顺序（照这个顺序问，能省 80% 时间）

1. **游戏重启了吗？** 新 DLL/新开关必须重进游戏才加载。
2. **装的是新 DLL 吗？** UTF-16 搜 DLL + 比时间戳/大小。（历史上"装了两天旧 DLL"）
3. **开关三层齐吗？** 字段 / SaveLoad / 消费点（`Select-String` 搜字段名的引用点，计数 0 = 空壳）。
4. **总开关与子开关都开了吗？**（判定条件写错会导致"只开总开关 = 什么都不做"）
5. **注入点被走到了吗？** 看诊断日志 `calls`（0 = 注入点没被游戏调用到）。
6. **对象找到了吗？** 看日志里"未找到 X 类型/字段"。
7. **赋值成功了吗？** 看回读校验日志（**别信返回码，信回读**）。
8. **是不是被游戏覆盖了？**（僵尸 `Scale`、某些 `timer` 每帧被重置 → 必须每帧写）

---

## 14. 血泪坑清单（AI 优先读这一章，避雷用）

| # | 现象 | 根因 | 正确做法 |
|---|---|---|---|
| 1 | 所有功能同时失效 | 静态类的**静态字段初始化器**抛异常 → `TypeInitializationException` 传染整个类 | 静态初始化器里不许有会抛异常的逻辑（如忽略大小写字典重复 key）；`OnFrame` catch 必须记异常 |
| 2 | 植物一攻击就冻结全局 | `BulletField.Spawn` 注入的指令顺序错（`call/brfalse` 没最先插、`skip` 标签没最后插）→ 条件为假时死循环 | 严格遵守注入顺序 |
| 3 | 界面假死、CPU 0% | ①主线程同步等待资源加载；②给核心路径方法开头插 `Thread.Sleep` | 分帧 + 不在核心路径 sleep；用 `dotnet-dump` 定位 |
| 4 | 改了功能"没生效"（连续两天） | `.bak` 缺失 → patcher 基于上次注入版合并，**已存在类型不更新** | 始终保留干净 `.bak`；用 UTF-16 搜 DLL 自证 |
| 5 | APK 里是旧功能 | 忘记把新 `PvzheMod.dll` 复制到 `arm64` publish 目录 | 打包流程固化 + 校验 APK 内 DLL 的 MD5 |
| 6 | 面板"连不上游戏" | `RemoteServer.Poll` 里的 `StringBuilder.Append(uint)` 被 AOT 裁掉 → 每次请求抛异常 | 字符串一律 `+` 拼接 |
| 7 | 切分类页后开关全变"关" | UI 重建了开关但**没从游戏同步状态**（只在首次连接同步过） | 重建后立刻同步；轮询每轮都同步 |
| 8 | 无限阳光/资源类功能导致主菜单卡死 | `CreateCurrency` 只检查全局单例（主菜单也存在）→ 在主菜单刷物触发资源加载死锁 | 先检查 `currentControl` 非空（即"在关卡内"） |
| 9 | 图鉴被"全解锁" | 注入 `Unlock()` 时忽略了图鉴也读它 | 加"图鉴打开时旁路"（`IsAlmanacOpen()` + 缓存） |
| 10 | 皮肤解锁了但没装备 | 只发事件没写存档；且事件第一参数是 **saveKey** | 写存档 `Key.Custom` + `Save` + `EmitCharacterSkinSwitched(saveKey, skinKey)` |
| 11 | 魅惑僵尸还往家走 | 手动反转 `camp`/`Scale.X` —— 移动组件只认 **Hypnoses buff** | 用游戏原生 `InvokeHypnoses()` |
| 12 | 随机子弹"偶尔有效" | 信了 API 的返回码（实际是枚举状态码） | **调用后读回校验**（`ReferenceEquals(config)` 或 saveKey 相同） |
| 13 | 植物攻速改不回去 | `mult==1` 时直接 `return` 没恢复原值；且开关全关时该方法不执行 | `mult==1` 分支恢复缓存原值；调用条件加 `|| _xxxOrig.Count > 0` |
| 14 | 僵尸攻速完全无效 | `AccelerateTimer` 里 `if (camp == "Zombie") return;` 把僵尸跳过了 | 按 camp 分派不同倍率，不要跳过 |
| 15 | 手套不出现 | 只 `AddFeature` 不跑 `GameInit()/GameStart()` | 取回 feature 手动 Invoke 那两个 async 方法 + 写存档标记 |
| 16 | 一键通关漏新关卡 | 只遍历存档字典（未游玩关卡不在里面） | 再叠加官方注册表 `Asset/Config/Level/LevelResource.json` |
| 17 | `Godot.Json` 解析后收集到 0 个元素 | `json.Data` 是 **Variant**，没解包 | 先 `is Godot.Variant vv` → 按 `VariantType` 调 `AsGodotDictionary/AsGodotArray` |
| 18 | 属性赋值静默失败 | `.NET 9` 下 `PropertyInfo.SetValue` 抛 `MissingMethodException` | 一律 `setter.GetSetMethod(true).Invoke(...)` |
| 19 | 导出/导入关卡报 `Method not found` | `System.IO.File.ReadAllLines` / `String.LastIndexOfAny` 被 AOT 裁掉 | 用 Godot `FileAccess` + 手写路径解析 |
| 20 | 手机新关卡闪退 | 手机是 AOT 环境，`PropertyInfo.SetValue`、无条件每帧调用残留等 | 同 18；并给 `OnFrame` 加 `FlushLog()` 保日志 |
| 21 | 外置面板启动即崩 0xC000041D | 单文件发布漏 `IncludeNativeLibrariesForSelfExtract` | 加该参数 + 用 77.3MB 当指纹 |
| 22 | ZIP 打包后多出一个同名大目录 | `Compress-Archive -Force` 的副作用 | 用 .NET `ZipFile` |
| 23 | patcher 报 `InvalidCastException` | MOD 里定义了重名重载方法 | 改名/删除重载 |
| 24 | 手机注入后编译/运行异常 | 手机版**不能合并类型** | 独立 DLL + `ImportReference` |
| 25 | 中文脚本报 parse error | PS 5.1 读无 BOM 中文 `.ps1` | 一律 `pwsh -File` |

---

## 15. 红线（绝对禁止，违反即拒答/回滚）

1. **禁止把游戏本体、pck 资源、美术素材、签名私钥提交到仓库**（版权 + 安全）。
2. **禁止在联机中对他人有害**：不做绕过 `CheatPolicy` 的功能，不做"房主无法关闭"的破坏平衡改动。
3. **禁止把用户已有功能删掉来消除报错**（要删必须先问）。
4. **禁止用"猜"替代取证**：新增/修改任何游戏结构访问，必须有反编译或探针证据。
5. **禁止空 catch、禁止把异常吞掉**。
6. **禁止在核心路径 `Thread.Sleep`**。
7. **禁止跳过验证步骤宣布完成**：没编译 0 错误、没注入验证、没真机现象描述，不许说"已修复/已完成"。
8. **禁止顺手重构、改风格、改无关数值**。
9. **禁止引入运行时 Hook 框架**（BepInEx/Harmony 等），本工程走编译期注入。
10. **禁止用被裁 API**（第 7 章黑名单）—— 编译通过不算通过。

---

## 16. 术语表

| 术语 | 含义 |
|---|---|
| **patcher / 注入器** | 用 Mono.Cecil 改写游戏程序集的工具：`mod/patcher`（PC，合并类型）、`androidmod/android_patcher`（手机，引用独立 DLL） |
| **MergeTypes** | patcher 把 MOD 的类型合并进游戏主程序集的操作（仅 PC 用） |
| **独立 DLL 方案** | 手机版：`PvzheMod.dll` 随 APK 加载，主程序集只插调用（合并会闪退） |
| **干净基准 `.bak` / `.clean`** | 未注入的原始游戏 DLL 备份，patcher 每次都从它恢复后再注入 |
| **AOT 裁剪** | Godot 导出时把未用到的 .NET API 剪掉 → 运行期 `Method not found` |
| **探针 / probe3** | 离线分析游戏 DLL 的自研工具（find/il/ref/enum/methods） |
| **三层开关** | ①ModSettings 字段 ②Save/Load ③消费点；缺③是"假开关" |
| **降频** | 每 N 帧才执行一次（`++_t % 30 == 0`），防掉帧 |
| **旁路（bypass）** | 功能 A 依赖的方法同时是功能 B 的注入点 → B 生效时把 A 的枚举过程旁路，避免 A 算错 |
| **语义路由** | 把"用户语义"（血量/攻速）映射到游戏真实位置（instance/组件） |
| **篡改（Trick）** | 锁定盲盒/种子雨/传送带/礼盒的产出内容 |
| **诊断埋点** | calls / applied / runs 计数，用来区分"没走到"和"走到了没生效" |
| **真机验证** | 必须进游戏实际看现象，不接受"代码看起来对" |

---

## 17. 给 AI 的第一个任务（自检：证明你真的读懂了）

在动手改任何功能前，请先回答下面 8 个问题（不许编，不确定就写"需取证"）：

1. 本工程的"每帧入口"是哪个文件、哪个方法？它被注入到游戏的哪个方法里？
2. 加一个新开关，必须动哪几个文件？三层结构分别是什么？
3. 植物射速到底改哪个类的哪个字段（写明类型）？为什么不能改植物的同名属性？
4. 手机版和电脑版的注入方式有什么本质区别？哪个环节最容易漏、导致"APK 里是旧版"？
5. 举出 3 个被 AOT 裁掉、必须规避的 API，并给出替代写法。
6. 外置修改器的单文件发布命令里，哪个参数漏了会导致启动即崩？崩溃退出码是多少？
7. 用户报"功能开了没用"，你的排查顺序是什么（至少 5 步）？
8. 写设计文档时，关于"游戏内部事实"的硬性要求是什么？

**答完这 8 题，再开始改代码。** 之后每次交付，请按第 10 章的 10 步清单自证，并按第 15 章红线自查。

---

> 本文档随工程演进持续更新。若你发现文档与源码不一致，**以源码 + 反编译取证为准**，并请修正文档（改哪里就更新哪一节）。

---
---

# 附录 1 · 逐子系统详解（本附录是全文最长、AI 最该逐条对齐的部分）

> **为什么要写这么细**：`GameCheats.cs` 有 650KB、几千个方法，AI 只看到文件名会以为"这是个作弊工具集合"。
> 实际上每个子系统都有一条**独立的实现链路**（开关 → 每帧调度 → 反射定位 → 注入点 → 存档/日志），
> 而且**每个都踩过至少一个反常识的坑**。下面按子系统逐个展开，格式固定为 8 小节：
> 用户看到什么 / 开关与数据流 / 真实游戏结构（含取证）/ 实现链路 / 代码骨架 / 历史坑 / 验证方法 / 扩展指引。
>
> AI 读法建议：**先读 8 个小节标题建立索引**，动手改哪个功能再精读哪一节，不要试图一次记住全部。

---

## 1.1 无限阳光（InfiniteSun）

### 1.1.1 用户看到什么
阳光数值不再下降：种任何植物后阳光数字立刻回到上限值，界面上看起来"花不完"。

### 1.1.2 开关与数据流
- 开关：`ModSettings.InfiniteSun`（bool，默认 false）
- 三层是否齐：字段 ✅ / `Save()Load()` ✅ / 消费点 ✅（`GameCheats.OnFrame` 里的阳光段）
- 是否进 `AnyModActive()`：**是**（漏了它 = 功能完全不跑，这是历史高频事故）

### 1.1.3 真实游戏结构（含取证）
| 事实 | 值 | 取证方式 |
|---|---|---|
| 阳光容器 | 关卡内阳光由**卡槽/战斗界面脚本**持有（0.27 下 PC 与手机路径不同） | 反编译 + 运行期探针 |
| 写入方式 | 反射设数值字段/属性 | IL 实测 |
| 关键陷阱 | 阳光相关属性在 **.NET 9 下 `PropertyInfo.SetValue` 会抛 `MissingMethodException`** | 真机日志 |

> AI 注意：**阳光的具体持有者会随游戏版本变化**。0.26 → 0.27 期间资源加载系统整个重写过。
> 所以本节给的是**方法学**不是死路径：先 `il:` 反汇编阳光显示控件，找它每帧读的是哪个字段，再决定写哪里。

### 1.1.4 实现链路
```
ModSettings.InfiniteSun = true
  → RemoteServer /set?name=InfiniteSun&val=1（外置面板点击）
  → ModSettings.Save()（写 modsettings.txt，重启仍生效）
  → FrameDriver._PhysicsProcess（每帧）
      → GameCheats.OnFrame(root)
          → if (AnyModActive())  ← 必须包含 InfiniteSun
              → ApplySun()      ← 本子系统消费点
                  → 反射：找到阳光持有者
                  → setter.Invoke(obj, new object[]{ bigValue })
```

### 1.1.5 代码骨架（照抄这个模式，所有"数值类"功能都一样）
```csharp
// ① 开关（ModSettings.cs）
public static bool InfiniteSun = false;

// ② 存档（ModSettings.cs，Save/Load 里各加一行）
//   Save: sw.WriteLine("InfiniteSun=" + InfiniteSun);
//   Load: InfiniteSun = ReadBool(lines, "InfiniteSun", false);

// ③ 消费点（GameCheats.cs）
static int _sunTimer;
static void ApplySun(Node root)
{
    // 降频：数值类不需要每帧写（游戏每帧会重算，写太勤反而与游戏打架）
    if (++_sunTimer % 5 != 0) return;
    try
    {
        var holder = FindSunHolder(root);           // 反射找持有者
        if (holder == null) { LogOnce("sun: 未找到阳光持有者"); return; }
        SetPropOrFieldSafe(holder, "sun", 99999);   // ★ 内部用 setter.Invoke，不用 PropertyInfo.SetValue
    }
    catch (Exception ex) { LogOnce("sun: " + ex.Message); }   // ★ 不许空 catch
}
```

### 1.1.6 历史坑
1. **`PropertyInfo.SetValue` 在 .NET 9 抛 `MissingMethodException`** → 被外层 catch 吞掉 → 用户看到"开了没用"。
   修复：所有对游戏类型属性的赋值改走 `setter.GetSetMethod(true).Invoke(...)`。**这是全局性规则，不是这一个功能的问题。**
2. **跟游戏"打架"**：如果游戏每帧重算阳光（例如结算阶段），你每帧强写会让数值抖动。
   正确做法：写值前判断"当前值是否小于目标"，或降频到 5~10 帧。
3. **在主菜单也执行** → 主菜单没有阳光对象，反射到处找 → 空转 + 日志刷屏。
   正确做法：先检查 `currentControl` 非空（"在关卡内"）。
4. **忘了加进 `AnyModActive()`** → `OnFrame` 提前 return，功能整个不执行（用户报"开了没用"，代码"看起来完全正确"）。

### 1.1.7 验证方法
- 编译：`dotnet build mod\PvzheMod\PvzheMod.csproj -c Release` → 0 error
- 注入：patcher 输出包含阳光相关注入点，`bad=0`
- 落地：`[Text.Encoding]::Unicode.GetString(...) -match 'ApplySun'`
- 真机：进关卡 → 开开关 → 种植物 → 阳光数字不降；失败看日志关键字 `sun:`

### 1.1.8 扩展指引
- 想加"阳光上限可调"：把常量 99999 换成 `ModSettings.SunCap`，并在外置面板 `Feats` 表注册一个数字项；
- 想做"阳光自动增长"：不要每帧 `+1`（会被游戏覆盖），改为每 30 帧写一次绝对值 `cur + 25`。

---

## 1.2 无限金币（CoinInfinite，取 10 亿）

### 1.2.1 用户看到什么
主界面金币显示 `1000000000`（10 亿），商店买东西不减少。

### 1.2.2 开关与数据流
- 开关：`ModSettings.CoinInfinite`（bool）
- 与阳光的**本质差别**：金库存放在**存档**里，不在关卡对象里 → 涉及"读存档 → 改 → 写回 → 通知游戏刷新"

### 1.2.3 真实游戏结构（含取证）
| 事实 | 值 | 取证方式 |
|---|---|---|
| 金币数值 | `ApplyCoin` → `SetNum(1000000000)` | 真机 + 日志 |
| 存档实体 | `GameSaveManager` 的 `KeyValue` 字典 | IL 实测 |
| 历史量级 | 早期用 `99999`，用户要求后改 **10 亿** | 记忆/提交历史 |
| 刷新时机 | 需要触发游戏的刷新（否则界面仍显示旧值） | 真机 |

### 1.2.4 实现链路
```
ApplyCoin()
  → 反射拿存档管理器（通常 GameSaveManager.Instance 或等价单例）
  → SetKeyValue("CoinNum", 1000000000)     ← 注意 Variant 构造：Godot.Variant.From(long)
  → 触发刷新（重算显示 / 重进界面可见）
  → 视实现决定是否 SaveGameConfig()
```

### 1.2.5 代码骨架
```csharp
static void ApplyCoin()
{
    try
    {
        var gsm = FindSaveManager();
        if (gsm == null) return;
        // ★ Variant 没有 1 参构造函数：必须 Godot.Variant.From(long)，否则 CS1729
        SetKeyValueSafe(gsm, "CoinNum", Godot.Variant.From(1000000000L));
        // 部分版本需要显式保存才会持久化
        // SaveGameConfigSafe(gsm);
    }
    catch (Exception ex) { LogOnce("coin: " + ex.Message); }
}
```

### 1.2.6 历史坑
1. **`new Godot.Variant(long)` 不存在** → `CS1729`。必须 `Godot.Variant.From(long)`。
2. **只改内存不落盘**：某些版本 `SetKeyValue` 只改内存，不 `Save` → 重进游戏就没了。需要确认真实行为（反编译 `SetKeyValue` 的调用方）。
3. **刷新问题**：改完存档但界面不更新 → 用户以为"没用"。需要触发游戏自身的刷新或提示"切回主界面查看"。
4. **水晶/星星等同理但不同键**：`CrystalNum` 等是另一个键，别混用（历史：水晶无限要 120 帧节流写，因为游戏会 `Load/SetUserCurrent` 时重算）。

### 1.2.7 验证方法
主界面看金币数字是否为 `1000000000`；日志 `coin:` 无异常；重启游戏后仍是 10 亿（持久化成功）。

### 1.2.8 扩展指引
- 同类"资源数值"功能（水晶 / 星星 / 竞彩币 / 背包）都套这个骨架：**找键 → 写大值 → 落盘 → 刷新**；
- 背包类（0.27 前身魔改版验证过）是**加密存储**（`d=v*61+salt`、`c=d^xor`），写明文会失效 —— 一定要先反编译确认存储格式。

---

## 1.3 零消费与卡价不涨价（ZeroCost / CostNoRise）

### 1.3.1 用户看到什么
所有植物种下去不花阳光；卡片价格不会随第几次种植上涨。

### 1.3.2 开关与数据流
- 开关：`ModSettings.ZeroCost`、`ModSettings.CostNoRise`（两个独立 bool）
- 实现方式：**不是写数值，而是改游戏的"返回值"** → 属于**注入类功能**（必须有 patcher 规则）

### 1.3.3 真实游戏结构（含取证）
| 方法 | 作用 | 取证 |
|---|---|---|
| `TowerDefensePacketConfig.GetCost(bool)` | 卡牌阳光价 | 反编译确认 |
| `TowerDefensePacketConfig.GetCostBeforeModifiers()` | **金卡/波点卡走这个** | 反编译 |
| `TowerDefensePacketConfig.GetWavePointCost()` | **波点价** | 反编译 |
| `GetCostRise` | 涨价幅度 | 反编译 |

### 1.3.4 实现链路
```
patcher/Program.cs
  → InjectReturnZeroIf(main, "GetCost", "ShouldZeroCost")
  → InjectReturnZeroIf(main, "GetCostBeforeModifiers", "ShouldZeroCost")   ← ★ 后加的关键补充
  → InjectReturnZeroIf(main, "GetWavePointCost", "ShouldZeroCost")         ← ★
  → InjectReturnZeroIf(main, "GetCostRise", "ShouldZeroRise")
GameCheats.ShouldZeroCost() → return ModSettings.ZeroCost;
GameCheats.ShouldZeroRise() → return ModSettings.CostNoRise;
```
> 模式：**patcher 在方法开头插入 `if (GameCheats.ShouldXxx()) return 0;`**，返回值由 MOD 决定 —— 开关可以实时切换，不用重新注入。

### 1.3.5 代码骨架
```csharp
// GameCheats.cs（被注入方调用的判定函数，必须 public static）
public static bool ShouldZeroCost() => ModSettings.ZeroCost;
public static bool ShouldZeroRise() => ModSettings.CostNoRise;

// patcher/Program.cs（规则）
InjectReturnZeroIf(main, "GetCost", "ShouldZeroCost");
InjectReturnZeroIf(main, "GetCostBeforeModifiers", "ShouldZeroCost");
InjectReturnZeroIf(main, "GetWavePointCost", "ShouldZeroCost");
```

### 1.3.6 历史坑
1. **只注入 `GetCost` 导致"金卡还要钱"**：金卡/波点卡的价格链路不走 `GetCost`，
   用户报"零阳光金卡没效果"。修复 = 补注 `GetCostBeforeModifiers` + `GetWavePointCost`。
   **教训：一个"价格"可能有 3 个入口，注入前先用 `ref:` 反查"谁在算价格"。**
2. **注入顺序**：`return 0` 必须插在方法**最开头**（在游戏自己读配置之前），否则读到一半的副作用还在。
3. **忘记同步手机版 patcher** → PC 生效、手机不生效（两份 patcher 是独立文件）。

### 1.3.7 验证方法
- 注入日志应出现 3~4 条 `GetCost*` 相关命中；
- 真机：金卡种植时阳光不减、波点卡不扣波点；
- 关掉开关立刻恢复原价（证明是"返值注入"而不是"写死数值"）。

### 1.3.8 扩展指引
任何"让游戏某处返回 0/true/false"的需求，都套这个模式：**找返回点 → 注入 `ReturnZeroIf` / `ReturnTrueIf` → 判定函数读开关**。
绝不要直接改配置文件，那样关不掉。

---

## 1.4 无冷却（NoCooldown）

### 1.4.1 用户看到什么
卡牌不进入灰色冷却状态，可以连续种植。

### 1.4.2 开关与数据流
- 开关：`ModSettings.NoCooldown`（bool）
- 类型：**注入类**（改 `set_coldDownOpen` 的行为）

### 1.4.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 冷却开关方法 | **`TowerDefenseInGamePacketShow.set_coldDownOpen`** | 反编译确认 |
| 易错点 | 同名概念在 `TowerDefensePacketConfig` 里也有，但**注入的是 `TowerDefenseInGamePacketShow` 那个** | 对比反编译 |
| 赋值陷阱 | 直接给该属性赋值会踩 `.NET 9 PropertyInfo.SetValue` 崩溃 | 真机 |

### 1.4.4 实现链路
```
patcher: InjectNoCooldown → 在 set_coldDownOpen 开头判定
   if (GameCheats.ShouldSkipCooldownSet()) return;   // 或强制写 false
GameCheats.ShouldSkipCooldownSet() → ModSettings.NoCooldown
```
另一种实现（手机版用过）：`Postfix` 式 —— 在 `CardClick.Update` 之后用反射把私有冷却字段 `pro.nowcoolDown` 置 0。
> ⚠️ **在本工程里优先用注入法**，反射 Postfix 属于跨工程方案（抽卡版才那样做）。

### 1.4.5 代码骨架
```csharp
public static bool ShouldSkipCooldownSet() => ModSettings.NoCooldown;

// patcher
InjectNoCooldown(main, "TowerDefenseInGamePacketShow", "set_coldDownOpen", "ShouldSkipCooldownSet");
```

### 1.4.6 历史坑
1. **注入错类**：注到 `TowerDefensePacketConfig` → 完全无效（那个是配置类，不是运行时显示类）。
   定位方法：`ref:set_coldDownOpen` 看谁调用它。
2. **不能把这个方法整个跳过**：`set_coldDownOpen` 可能兼做状态登记，直接 `ret` 会破坏其它逻辑。
   正确做法：**只改判定结果，不跳过整个方法体**（或跳过前后做补偿）。
3. **别用"另一个看起来相关的开关"代替**：某个版本里 `First.jianbukecui` 曾被当成零冷却开关，
   但它同时参与出怪与阳光逻辑 → 误改会引发连锁 bug。**"看起来相关"不等于"就是这个"。**

### 1.4.7 验证方法
进关卡 → 连续种同一张卡 → 卡牌始终不灰；关开立即恢复（运行期可切换）。

### 1.4.8 扩展指引
想加"指定几张卡无冷却"：在判定函数里读卡名（`config.saveKey`）白名单，而不是全局生效。

---

## 1.5 植物攻速（PlantAttackSpeed）—— 本工程最典型的"三轮返工"案例

### 1.5.1 用户看到什么
调高倍率后，植物攻击/射击明显变快；调回 1.0 恢复原速。

### 1.5.2 开关与数据流
- 开关：`ModSettings.PlantAttackSpeed`（float，1.0 = 原速，支持 <1 减速）
- 数值类 + 持久修改 → **必须自己能恢复**（见 1.5.6 坑 2）

### 1.5.3 真实游戏结构（含取证）—— 这一节请 AI 逐字读
| 对象 | 字段/方法 | 类型 | 说明 | 取证 |
|---|---|---|---|---|
| `FireComponent` | **`fireInterval`** | **Single** | **真正的射击间隔**：射击后 `timer` 重置为它 | 反编译 0.26.1 实测 |
| `FireComponent` | `fireIntervalBase` | Single | 基准值（双写保险） | 同上 |
| `FireComponent` | `timer` | Single | 每帧 `timer -= delta * GetTimerRunScale()`，`<=0` 触发射击 | 同上 |
| `FireComponent` | `parent` | TowerDefenseCharacter | 反向找植物 | 同上 |
| 植物节点 | `fireInterval` | **Double** | ⚠️ **同名但无关**，只被序列化/恢复调用，战斗不读 | 反编译 |
| 植物节点 | `_fireInterval` | Double | 上面属性的后备字段 | 同上 |

> ⛔ **AI 必须记住的结论**：改射速 = 改 **`FireComponent.fireInterval`（Single）**。
> 改植物节点上的同名属性**完全无效**，但代码会"看起来完全正确"地跑完并写日志。

### 1.5.4 实现链路
```
ApplyAttackSpeed(plantNode)
  → FindPlantFireComponent(plantNode)   ← 三路查找（见下）
     ① plantNode.componentManager._runtimeByInstanceId 字典 → 找 AttackComponent/FireComponent
     ② plantNode 的 fireComponent / _fireComponent 字段
     ③ 递归子节点遍历找类型名含 "FireComponent" 的节点
  → 用 _plantFireOrig（按 GetInstanceId 缓存原值）计算 target = orig / mult
  → SetFireIntervalVal(comp, "fireInterval", target)   ← setter.Invoke，不用 PropertyInfo.SetValue
  → 同时写 fireIntervalBase（双写保险，因为不确定哪个被游戏读）
  → 找不到时打印诊断："植物射速: 找不到 FireComponent"
```

### 1.5.5 代码骨架
```csharp
static readonly Dictionary<ulong, double> _plantFireOrig = new();   // instanceId -> 原值（Double）

static void ApplyAttackSpeed(Node plant)
{
    double mult = ModSettings.PlantAttackSpeed;
    var comp = FindPlantFireComponent(plant);
    if (comp == null) { LogOnce("植物射速: 找不到 FireComponent"); return; }
    ulong id = comp.GetInstanceId();

    if (mult == 1.0)
    {
        // ★ 必须能恢复：从缓存取原值写回，然后清缓存
        if (_plantFireOrig.TryGetValue(id, out var orig))
        {
            SetFireIntervalVal(comp, "fireInterval", orig);
            SetFireIntervalVal(comp, "fireIntervalBase", orig);
            _plantFireOrig.Remove(id);
        }
        return;
    }
    if (!_plantFireOrig.ContainsKey(id))
        _plantFireOrig[id] = ReadFloat(comp, "fireInterval");   // 首次记录原值
    double target = _plantFireOrig[id] / mult;                  // mult<1 = 更慢
    SetFireIntervalVal(comp, "fireInterval", target);
    SetFireIntervalVal(comp, "fireIntervalBase", target);
    LogOnce("植物射速: fireInterval " + _plantFireOrig[id] + "->" + target + " mult=" + mult);
}
```

### 1.5.6 历史坑（这一节是"AI 必须吸取"的核心教训）
1. **坑①：找错了对象（改了 3 轮）**
   - 第 1 轮：改植物节点 `fireInterval`（Double）→ 无效；
   - 第 2 轮：改 setter → 仍无效；
   - 第 3 轮：反编译搜"整个 `FireComponent` 类"才发现 `fireInterval` 是 Single 且在组件上。
   - **教训：搜字段要搜全类，不要只搜"看起来该在的那个类"。**
2. **坑②：关掉不恢复**：`mult == 1.0` 时直接 `return`（不写回原值），而调用条件又只在 `mult != 1` 时成立
   → 关闭后永远停在"高射速"。修复：①`mult==1` 分支必须恢复；②调用条件加 `|| _plantFireOrig.Count > 0`。
3. **坑③：`.NET 9 PropertyInfo.SetValue` 抛 `MissingMethodException`** → 静默失败。修复：setter.Invoke。
4. **坑④：找不到组件就 `return`**，且没日志 → 用户只看到"没用"。修复：三路查找 + 诊断日志。
5. **坑⑤：新种植物不生效**：需要每帧（或降频）对**当前场上所有植物**扫描，不能只在种植瞬间做一次。

### 1.5.7 验证方法
- 日志：`植物射速: fireInterval 1.50->0.50 mult=3`（能看到改前改后与倍率）
- 真机：开 3x 后射击频率肉眼可见变快；关回 1x 明显变慢；**新种的植物也变快**
- 反向验证：改 >1 倍率变慢能生效（证明不是单向）

### 1.5.8 扩展指引
- 想只对特定植物生效：在 `FindPlantFireComponent` 之后判断植物类型名/配置 `saveKey`；
- 想加"攻速随时间递增"：不要反复除原值（会累积误差），始终用 `_plantFireOrig` 的原值算目标值。

---

## 1.6 僵尸攻速（ZombieAttackSpeed）

### 1.6.1 用户看到什么
僵尸啃咬/攻击频率明显变快（或变慢）。

### 1.6.2 开关与数据流
- 开关：`ModSettings.ZombieAttackSpeed`（float）
- 实现方式：**每帧临时加速**（不做持久修改）→ 天然可以"开关即恢复"

### 1.6.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 组件 | `TowerDefenseZombie.attackComponent`（字段） | 反编译 |
| 字段 | `AttackComponent.attackInterval` / `attackIntervalBase` / **`timer`**（均 Double） | 反编译 |
| 刷新 | `AttackComponent.Refresh()` = `timer = attackInterval + RandRange(-offset*2, -offset)` | IL |
| 每帧 | `BatchUpdateValidated` = `timer -= delta * parent.timeScale * comp.timeScale`，`<=0` 触发攻击 | IL |
| 关键事实 | **`BatchUpdateValidated` 在攻击计时期间每帧都触发**（旧注释说"不常触发"是错的） | IL 实测 |

### 1.6.4 实现链路（对比植物：这里走"每帧扣时间"而不是"改间隔"）
```
patcher: InjectAttackSpeedPatch
  → 在 AttackComponent.BatchUpdateValidated 开头插入 AccelerateTimer(comp, delta)
GameCheats.AccelerateTimer(comp, delta):
  camp == "Plant"  → timer -= delta * (PlantAttackSpeed - 1)
  camp == "Zombie" → timer -= delta * (ZombieAttackSpeed - 1)
  其他              → return
  （mult = 1 时减去 0 → 自然恢复，无需缓存原值）
```

### 1.6.5 代码骨架
```csharp
public static void AccelerateTimer(object comp, double delta)
{
    try
    {
        var camp = GetCamp(comp);                       // 反射读阵营
        double mult = camp == "Zombie" ? ModSettings.ZombieAttackSpeed
                    : camp == "Plant"  ? ModSettings.PlantAttackSpeed
                    : 1.0;
        if (mult == 1.0) return;
        var t = ReadField<double>(comp, "timer");
        WriteField(comp, "timer", t - delta * (mult - 1.0));   // mult>1 → timer 更快归零 → 更快攻击
    }
    catch (Exception ex) { LogOnce("攻速: " + ex.Message); }
}
```

### 1.6.6 历史坑
1. **坑①僵尸完全不加速**：`AccelerateTimer` 里写了 `if (camp == "Zombie") return;`（早期只服务植物）
   → 僵尸攻速全靠改 `attackInterval`，而 patcher 注释实测"改 attackInterval 未生效" → 僵尸攻速从未生效。
   修复：按 camp 分派倍率，**不要跳过僵尸**。
2. **坑②双重加速（mult²）**：`AccelerateTimer` + `ApplyZombieAttackSpeed`（改 interval）同时开
   → 实际倍率是平方。修复：移除后者调用，只留一条链路。
3. **坑③改对字段但改错类**：`attackInterval` 在 `AttackComponent` 上，不在僵尸节点上。
4. **降频陷阱**：这是**每帧**逻辑（不能降频），因为它靠逐帧累积。

### 1.6.7 验证方法
日志关键字 `加速调用` / `加速执行` / `IL攻速触发`（每 120 帧打印一次，确认注入点确实被走到）；
真机：僵尸 3x 明显咬得快；关回 1x 立即恢复。

### 1.6.8 扩展指引
想"只加速特定僵尸"（如只加速巨人）：在 `AccelerateTimer` 里判断 `comp.parent` 的类型名/配置。

---

## 1.7 强制选卡与无视紫卡（ForcePacketUsable / IgnorePurple）

### 1.7.1 用户看到什么
所有关卡都能自由选卡（不被关卡限制），紫卡也能直接种。

### 1.7.2 开关与数据流
- 开关：`ModSettings.IgnorePurple`（无视紫卡）、强制选卡相关开关
- 类型：**注入类**（改 `Unlock / IsUnlocked / CanPacketPlant / _lock / get_packetBankMethod`）

### 1.7.3 真实游戏结构（含取证）
| 对象 | 说明 | 取证 |
|---|---|---|
| `TowerDefensePacketConfig.Unlock()` / `IsUnlocked()` | 是否解锁（注入点） | 反编译 |
| `TowerDefenseInGamePacketShow._lock`（字段） | 卡牌锁 | IL |
| `_cachedRuntimeAvailability` | **游戏每帧按阳光+前置植物算的可用性** | IL |
| `get_packetBankMethod` → `CHOOSE` | 强制自选卡 | IL |
| `ForceUnlockPackets` | 周期解锁（历史频率 300 → 60 帧） | 真机 |
| ⚠️ **图鉴也读 `Unlock()`** | 图鉴条目解锁判断 = `Almanac.plantShowAll \|\| Unlock()` | IL 铁证 |

### 1.7.4 实现链路
```
patcher:
  InjectReturnTrueIf("Unlock", "ShouldUnlockAll")
  InjectReturnTrueIf("IsUnlocked", "ShouldUnlockAll")
  InjectReturnTrueIf("HasRequiredPlantCover", "ShouldUnlockAll")     // 忽略前置植物
  InjectForceUsable("ApplyCachedRuntimeAvailability" / "_PhysicsProcess")
  InjectPacketBankMethod → 强制 CHOOSE
GameCheats:
  ShouldUnlockAll() → IgnorePurple && !IsAlmanacOpen()   ← ★ 关键旁路
  IsAlmanacOpen()   → 每 30 次调用扫场景树找**可见的 Almanac 节点**（缓存结果，避免高频遍历）
```

### 1.7.5 代码骨架
```csharp
static bool? _almanacCache;   // 缓存：null=未检测
static int _almanacProbe;

public static bool ShouldUnlockAll()
{
    if (!ModSettings.IgnorePurple) return false;
    if (IsAlmanacOpen()) return false;      // ★ 图鉴打开时不强制解锁，避免"没开图鉴却全解锁"
    return true;
}

static bool IsAlmanacOpen()
{
    if (++_almanacProbe % 30 != 0 && _almanacCache.HasValue) return _almanacCache.Value;
    bool found = false;
    WalkTree(root, child =>
    {
        if (child is Godot.CanvasItem ci && ci.IsVisibleInTree() &&
            child.GetType().Name.Contains("Almanac")) { found = true; return false; }
        return true;
    });
    _almanacCache = found;
    return found;
}
```

### 1.7.6 历史坑
1. **坑①"没开图鉴却全解锁"**（用户报的现象）：
   注入 `Unlock()` 后，图鉴条目判断也走这条 → 图鉴全解锁。
   修复：`ShouldUnlockAll()` 加 `!IsAlmanacOpen()` 旁路；图鉴全解改由**独立开关** `AlmanacAll → plantShowAll` 控制。
2. **坑②强制 `_cachedRuntimeAvailability = true` 会让卡"拿不起"**：
   那是游戏每帧算的真实可用性，强改与游戏打架 → 卡牌显示亮但点不动。
   修复：**只保留 `_lock = false`**，前置植物靠 `HasRequiredPlantCover` 注入、阳光靠无限阳光。
3. **坑③强制选卡对"新关卡配置"无效**：`NewLevelInit` 直接用 `featureData` 重建，不读 getter。
   增强：在 `_battleGraphReady == false` 的窗口里改 `levelConfig.featureData`（加 `PacketBank`/`SeedBank`）。
4. **坑④`IsVisibleInTree()` 是 `CanvasItem` 的方法**（不是 `Node`）→ 必须 `child is Godot.CanvasItem ci`。
5. **坑⑤手机版无 `GetTreeRoot()`** → 用 `_lastRoot`（`OnFrame` 每帧记录）。

### 1.7.7 验证方法
- 任意关卡能选任意卡；紫卡可种；**打开图鉴时条目按原版解锁**（这条必须专门验证，别只看"能种了"）；
- 日志：解锁相关无异常。

### 1.7.8 扩展指引
想"只解锁部分卡"：在判定函数里读 `config.saveKey` 白名单/黑名单，而不是靠图鉴旁路这种全局折中。

---

## 1.8 手套（Glove）—— feature 生命周期类功能的样板

### 1.8.1 用户看到什么
游戏里出现"手套"工具（可拖动物体），和原生手套一样。

### 1.8.2 开关与数据流
- 开关：`ModSettings.GloveMode`（bool）
- 类型：**feature 注入类**（比"改返回值"复杂一档：要管理 feature 的生命周期）

### 1.8.3 真实游戏结构（含取证）—— 完整加载链（0.26.1 IL 实测）
```
TowerDefenseBattleFeatureGlove.Init(dict)
   gloveManager = GLOVE_MANAGER.Instantiate<GloveManager>()
   control.AddUIToTopPropContainer(gloveManager)

GameInit()  (async Task) → InitializeManager()
   _mapFeature = GetFeature<Map>("Map")
   mapFeature.gloveManager = gloveManager
   gloveManager.Init(mapControl, mapFeature)

GameStart() (async Task)
   if (gloveManager && _mapFeature && packetPickControl)
       glovePickTool = new GlovePickTool(); Init(mapControl); SetGloveFeature;
       packetPickControl.RegisterTool(...)
```
> 关键：**`AddFeature` 只创建 feature（只跑 `Init`）**，而 `GameInit`/`GameStart` 的时序早已过去
> → `InitializeManager` 与 `GlovePickTool` 都不会执行 → **手套不出现**（看起来"注入了但没效果"）。

### 1.8.4 实现链路（四管齐下）
```
① featureData 注入：levelConfig.featureData["Glove"] = {}（空字典即可，feature 系统会自动初始化）
② control.AddFeature("Glove", dict)
③ 取回 feature：GetFeature("Glove")（⚠️ 用 FindMethodExact，别撞泛型重载）
      → 手动 Invoke GameInit() 和 GameStart()（fire-and-forget，不 await）
④ 存档标记：GameSaveManager.SetFeatureValue("Glove", Variant.From(1))
      （游戏判断手套开启 = GetFeatureValue("Glove") > 0，这是 0.25.5 CommandManager 的做法）
```

### 1.8.5 代码骨架
```csharp
static void ApplyGloveMode(Node root)
{
    var ctrl = GetCurrentControl();
    if (ctrl == null) return;
    var dict = new Godot.Collections.Dictionary();          // 空字典就够
    ctrl.Call("AddFeature", new Godot.StringName("Glove"), dict);

    var feat = InvokeFeature(ctrl, "Glove");                 // FindMethodExact(非泛型)
    if (feat == null) { LogOnce("手套: feature 未创建"); return; }

    // ★ 手动补跑被跳过的生命周期
    InvokeNoArg(feat, "GameInit");
    InvokeNoArg(feat, "GameStart");

    var gsm = FindSaveManager();
    if (gsm != null) SetFeatureValueSafe(gsm, "Glove", Godot.Variant.From(1));
}
```

### 1.8.6 历史坑
1. **坑①只 `AddFeature` 不出现** → 必须手动 Invoke `GameInit` + `GameStart`（见上）。
2. **坑②`GetFeature` 泛型重载歧义** → `AmbiguousMatchException`。用 `FindMethodExact`（跳过 `IsGenericMethodDefinition`）或直接读 `featureDictionary`。
3. **坑③忘了存档标记** → 部分场景（重进关卡）手套消失。三路径都要覆盖。
4. **坑④玩家以为"手套是 MOD 自己画的"**：不是 —— 加载的是游戏**原生**手套场景（`uid://...`），所以行为与原生一致，别去改它的逻辑。

### 1.8.7 验证方法
- 日志：`手套:` 段（feature 创建/生命周期补跑/存档标记）
- 真机：进关卡后手套出现在铲子旁；拖动可用；**重进关卡仍在**

### 1.8.8 扩展指引
任何"原生 feature 类"功能（手套 / 铲子 / 推车 / 雨雷暴）都套这套：
**① featureData 注入 → ② AddFeature → ③ 需要时手动补跑 Init/GameInit/GameStart → ④ 存档标记**。
先反编译该 feature 的完整生命周期（`Init/GameInit/GameStart/_Ready`），确认哪些被"AddFeature"跳过了。

---

## 1.9 皮肤（Skin）—— "写存档"类功能的样板

### 1.9.1 用户看到什么
任意植物/僵尸可以切换任意皮肤（外观），且**重进游戏仍然保持**。

### 1.9.2 开关与数据流
- 类型：**动作类 + 存档写入**（不是开关式常驻功能）
- 涉及：UI 选皮肤 → 写存档 → 发事件通知游戏刷新

### 1.9.3 真实游戏结构（含取证）—— 游戏自身"装备按钮"的完整逻辑
```
InformationPanel::EquipmentButtonPressed（反编译 IL）
  ① GetTowerDefensePacketValue(config.saveKey)      // 取该卡片的存档字典
  ② 设 Key 子字典的 Custom = 皮肤key
  ③ SetTowerDefensePacketValue(...)                 // 写回
  ④ EmitCharacterSkinSwitched(**saveKey**, skinKey) // ★ 第一参数是 saveKey！
```
| 误区 | 真相 | 取证 |
|---|---|---|
| 只要发事件就行 | 必须**先写存档**，否则重进就没了 | IL |
| 事件第一参数是 packetId | 是 **saveKey**（订阅方 `TowerDefenseCharacter`/`InGamePacketShow` 按 saveKey 匹配） | IL |
| 存档里字段叫 "Custom" | 皮肤 key 存在 `Key.Custom`；而 `PlantXXXUnlockLoveKeyCustom` 是**解锁键后缀**，两码事 | save.res 实测 |

### 1.9.4 实现链路
```
ModUI 皮肤页（分帧扫描：每帧 5 张，避免主线程卡死）
  → 用户点某张卡某个皮肤
  → GameCheats.UnlockSkin(packetId, skinKey)
      config = GetConfig(packetId)
      WriteSkinToSave(config.saveKey, skinKey)        // 写 Key.Custom
      ScheduleSave()                                  // 落盘
      EmitCharacterSkinSwitched(config.saveKey, skinKey)
```

### 1.9.5 代码骨架
```csharp
public static void UnlockSkin(string packetId, string skinKey)
{
    try
    {
        var cfg = GetConfig(packetId);
        if (cfg == null) { LogOnce("皮肤: 配置为空 " + packetId); return; }
        string saveKey = ReadStr(cfg, "saveKey");
        WriteSkinToSave(saveKey, skinKey);          // Key.Custom = skinKey
        ScheduleSave();
        EmitCharacterSkinSwitched(saveKey, skinKey); // ★ saveKey 而不是 packetId
        LogOnce("皮肤: " + saveKey + " -> " + skinKey);
    }
    catch (Exception ex) { LogOnce("皮肤异常: " + ex.Message); }
}
```

### 1.9.6 历史坑
1. **坑①只发事件不写存档** → 当场变、重进没了。修复：写存档 + `ScheduleSave()` + 事件。
2. **坑②事件参数传 packetId** → 订阅方匹配不到 → 外观不变（但事件确实发出去了，极难查）。修复：传 **saveKey**。
3. **坑③自动扫描皮肤导致主线程死锁假死**（最严重）：
   入口自动调用 `scanSkins()` → 遍历所有卡 → 每张 `GetConfig` → 反射 `GetPacketConfig` → `ResourceManager.GetPacket`
   → 主线程 `LoadCharacterBindingOnMainThread` + `Task.Wait()` **无限等待** → 配合资源节流 → **主线程死锁，CPU 0% 假死**。
   修复：**去掉自动全量扫描**，改**分帧扫描**（`StartSkinScanFrameByFrame` / `TickSkinScan` 每帧 5 张，
   完成回调 `OnSkinScanDone` 才更新 UI）。
   定位方法：`dotnet-dump` 抓主线程栈，可以看到完整调用链。
4. **坑④手机版用旧 APK 验证**：手机皮肤代码完整但用户说"没用"，实为 APK 里是旧 `PvzheMod.dll`。

### 1.9.7 验证方法
- 日志：`皮肤: <saveKey> -> <skinKey>`
- 真机：换皮肤 → 外观立刻变 → **退出重进仍保持**（这条是判断"有没有写存档"的唯一标准）

### 1.9.8 扩展指引
任何"要给游戏写持久状态"的功能都套这套：**找存档键 → 写 → 落盘 → 发事件（参数类型要对）**。
先反编译游戏自己的类似按钮（如装备、购买），照它的顺序做，不要自创。

---

## 1.10 魅惑（Charm）—— "用游戏原生机制"的样板

### 1.10.1 用户看到什么
僵尸/植物被魅惑后**转头攻击自己的阵营**（僵尸往右走并攻击僵尸，而不是往家走）。

### 1.10.2 开关与数据流
- 开关：`ModSettings.CharmZombie` / `ModSettings.CharmPlant`（bool，持续型）
- 类型：**注入类 + 每帧维持**

### 1.10.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 原生魅惑机制 | **`Hypnoses` buff**（`InvokeHypnoses()`） | IL |
| 错误做法 | 手动反转 `camp` 枚举 + 反转 `Scale.X` | 真机验证无效 |
| 为什么无效 | **移动组件不认 camp，只认 Hypnoses buff** | 真机 |

### 1.10.4 实现链路
```
patcher: InjectCharm → 在 BatchProcessUpdate 开头插入 ApplyCharmPatch(this)
GameCheats.ApplyCharmPatch(character):
  若 开关开 且 未处于 Hypnoses
      → InvokeHypnoses(character)     // 原生机制：反转阵营、转向、清目标
      → _charmedSet.Add(instanceId)
  若 开关关 且 处于 Hypnoses
      → InvokeHypnoses(character)     // 再调一次 = 移除 buff（游戏自身 toggle 语义）
  持续型：游戏可能自行移除 buff → 每帧检查并补上
```
> ⚠️ **`InjectCharm` 曾被注释掉**（早期手动改 camp 与 buff 冲突导致抽搐），注释里写"改由 CombatWalk 处理"——
> 但 **`CombatWalk` 方法根本不存在** → `ApplyCharmPatch` 从未被调用 → 持续魅惑开关全废。
> **教训：注释掉的注入点 = 死代码。要么恢复，要么删掉，不许留"看起来在工作"的注释。**

### 1.10.5 代码骨架
```csharp
static readonly HashSet<ulong> _charmed = new();   // 用 ulong + List/Dictionary 替代（HashSet 在 AOT 下慎用）

public static void ApplyCharmPatch(object c)
{
    try
    {
        var n = c as Node; if (n == null) return;
        ulong id = n.GetInstanceId();
        bool has = BuffHas(n, "Hypnoses");
        if (ModSettings.CharmZombie && !has) { InvokeHypnoses(n); _charmed.Add(id); }
        else if (!ModSettings.CharmZombie && has && _charmed.Contains(id)) { InvokeHypnoses(n); _charmed.Remove(id); }
    }
    catch (Exception ex) { LogOnce("魅惑异常: " + ex.Message + "|" + ex.StackTrace); }
}
```

### 1.10.6 历史坑
1. **坑①手动改 camp 无效**（见上）—— 移动逻辑只认 buff。
2. **坑②注入点被注释成死代码** → 功能"从未执行"，日志里连"魅惑函数入口"都没有。
   排查技巧：**在功能入口加一行日志，如果日志没有 → 说明方法根本没被调用**（不是逻辑错）。
3. **坑③手机版是"点按式"**（按钮触发 `CharmAll`），不是持续注入 → **PC/手机实现路径不同**，不要照抄。
4. **坑④刷出的植物魅惑**：`TowerDefensePacketConfig.Plant` 内部用 `CallDeferred` + `<Plant>b__0` 延迟初始化，
   直接改共享 config 的 `overrideHypnoses` 会污染其它卡 → 正确做法：**临时置 `overrideHypnoses=true` → Plant → 立即恢复**。

### 1.10.7 验证方法
日志 `魅惑函数入口`（证明注入点被走到）；真机：魅惑僵尸**转头往右走并攻击僵尸**（不是往家走）。

### 1.10.8 扩展指引
**凡是"游戏里已经有这个行为"的功能，一律优先调游戏原生方法**（buff / 事件 / feature），
不要自己改底层字段。自研实现会与游戏自身的状态机互相打架，症状往往表现为"抽搐/抖动/来回横跳"。

---

## 1.11 恢复小推车（RestoreAllMowers）

### 1.11.1 用户看到什么
已使用/已启动的小推车重新出现。

### 1.11.2 开关与数据流
- 类型：**动作类**（一次性按钮）

### 1.11.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 官方 `RestoreAllMowers` | **只在 `mowerLine[row]` 引用无效时才重建** | IL |
| 为什么官方"没用" | 小推车用掉后**引用仍有效** → 不满足重建条件 → 什么都不做 | 真机 + IL |
| feature 取法 | `featureDictionary["Mower"]` → `mowerLine`（`Array<TowerDefenseMower>`）+ `CreateMower(int,int)` | IL |
| 行数来源 | `TowerDefenseManager.GetMapLineUse(int)` / `GetMapFeature().config.gridNum` | IL |
| 清空陷阱 | `Godot.Collections.Array<T>` **可能不实现非泛型 `IList`** → `ml is IList` 判断失败，清空根本没执行 | 真机 |

### 1.11.4 实现链路（自研替代官方）
```
GameCheats.RestoreAllMowers()
  ① 遍历场景树删除场上所有 TowerDefenseMower 节点
  ② 清空 mowerLine 引用（★ 通用 ClearMowerLine：IList 或反射 Item 索引器逐个设 null）
  ③ 按 GetMapLineUse(row) 对每一行 CreateMower(row, -1)
  ④ 检查 CreateMower 返回值：null = 失败，必须计数并打日志（旧代码不管 null 都 done++ → 假成功）
```

### 1.11.5 代码骨架
```csharp
static void ClearMowerLine(object arr)   // 兼容 IList 与 Godot 数组
{
    if (arr is System.Collections.IList il) { for (int i = 0; i < il.Count; i++) il[i] = null; return; }
    var t = arr.GetType();
    var idx = t.GetProperty("Item");     // 索引器
    int n = (int)ReadMember(t, arr, "Count");
    for (int i = 0; i < n; i++) idx.SetValue(arr, null, new object[] { i });
}
```

### 1.11.6 历史坑
1. **坑①以为官方方法能用** → 用户报"恢复小推车没用"。**凡是官方 debug 方法，先反编译确认它的前置条件。**
2. **坑②`Array<T>` 不实现 `IList`** → 清空静默失败 → `CreateMower` 见引用有效直接 return null → 一辆都没重建。
3. **坑③不看返回值当成功** → 日志显示"成功 5 辆"，实际 0 辆。
4. **坑④忘了 `mowerLine` 是 feature 里的字段**（不是管理器上的）。

### 1.11.7 验证方法
日志 `恢复小推车: 成功 X 辆`（X 应等于行数）；真机：用掉小推车后点按钮 → 5 辆重新出现。

### 1.11.8 扩展指引
"恢复/重置"类功能通用套路：**先删场上实例 → 清引用 → 再按官方创建方法重建 → 检查返回值**。

---

## 1.12 刷怪倍数（SpawnMultiplier）

### 1.12.1 用户看到什么
关卡的僵尸数量按倍数放大（1~10x）。

### 1.12.2 开关与数据流
- 开关：`ModSettings.SpawnMultiplier`（int 1-10）
- 类型：**配置改写类**（改关卡波次配置）+ 每帧监控关卡切换

### 1.12.3 真实游戏结构（含取证）—— 这一节把"猜结构"的代价写清楚
```
正确结构（0.26/0.27 实测）：
TowerDefenseManager.Instance                 ← 属性
  → .currentControl                          ← ★ 字段（不是属性！）
  → control.GetFeature("Wave")               ← ★ 参数是 StringName
  → TowerDefenseBattleFeatureWave.config     ← TowerDefenseLevelWaveManagerConfig
  → .wave                                    ← 属性 get_wave，Array<TowerDefenseLevelWaveConfig>
  → 每波 .spawn                              ← ★ 字段，小写！（不是 Spawn）
  → 每条 .num                                ← ★ 小写 Int32（不是 Num/double）
           .zombie (String)  .line (Int32)
```
**当初的错误版本**（全部猜错，功能 0 生效）：
`TowerDefenseBattleFeatureWave.Instance`（其实不是 static 字段）→ `.wave[].Spawn[].Num`（大写、double）→ 改了个寂寞。

### 1.12.4 实现链路
```
ApplySpawnMultiplier()（每 15 帧）
  ① 拿 wave feature（优先按 key "Wave"，兜底遍历 featureDictionary 找类型名含 "Wave" 的值）
  ② 读 config，若 config 引用变化（换关卡）→ 清空原始值缓存
  ③ 对"当前波 + 下一波"的每条 spawn：num = 原始值(缓存) × 倍数
      首次遇到某条记录原值到 _spawnOrigNums（否则多次乘会爆炸）
  ④ gridSp（网格刷怪）没有 num 字段 → 不动
```

### 1.12.5 代码骨架
```csharp
static readonly Dictionary<string, int> _spawnOrig = new();
static object _lastWaveCfg;

static void ApplySpawnMultiplier()
{
    int mult = ModSettings.SpawnMultiplier;
    if (mult <= 1) return;
    var cfg = GetWaveConfig(out var waveArray);
    if (cfg == null || waveArray == null) { LogOnce("刷怪: 未找到 Wave feature"); return; }
    if (!ReferenceEquals(cfg, _lastWaveCfg)) { _spawnOrig.Clear(); _lastWaveCfg = cfg; }  // 换关卡重置

    foreach (var wave in Enumerate(waveArray))
        foreach (var sp in Enumerate(ReadMember(wave, "spawn")))
        {
            string key = WaveKey(wave) + "|" + ZombieKey(sp);
            int orig = _spawnOrig.TryGetValue(key, out var o) ? o : (int)ReadMember(sp, "num");
            _spawnOrig[key] = orig;
            WriteField(sp, "num", orig * mult);
        }
}
```

### 1.12.6 历史坑
1. **坑①整套结构猜错**（大写 `Spawn`/`Num`、`Instance` 当静态字段）→ 0 生效，且不报错。
   **教训：涉及游戏内部结构必须先用探针 `[F]`/`il:` 验证，再写代码。**
2. **坑②复用同一个获取路径**：历史上三处功能（刷怪/停雨/重置脑子）各写了一套"拿 control"，三套全错。
   修复：统一用 `GetTdmInstance()` → `FindFieldVal(tdm,"currentControl")` → `GetFeature(StringName)`。
3. **坑③累计相乘**：不缓存原值 → 每 15 帧乘一次 → 数值爆炸。
4. **坑④`GetFeature` 泛型重载歧义** → 用 `FindMethodExact` 或直接读 `featureDictionary`。

### 1.12.7 验证方法
日志：找到 Wave feature、当前倍数、改了几条；真机：进关卡数僵尸数量明显变多；换关卡后倍数依然生效（缓存重置正确）。

### 1.12.8 扩展指引
改配置类功能都遵循：**定位配置对象 → 缓存原值 → 按倍率写 → 换关卡重置缓存**。

---

## 1.13 三叶草吹飞（BloverClearAll）

### 1.13.1 用户看到什么
种下三叶草（Blover）后，全场僵尸被吹飞/清除。

### 1.13.2 开关与数据流
- 开关：`ModSettings.BloverClearAll`（bool）
- 类型：**种植回调类**（在"卡牌即将种植"时机判断卡名）

### 1.13.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 注入点 | `TowerDefenseInGamePacketShow.Plant(Vector2I, ...)` **开头**（无条件） | IL |
| ⚠️ 老版本陷阱 | **`TowerDefenseInGamePacketShow` 没有 `packetName`/`packetId` 字段**（那是 0.25.5 结构）→ 反射恒 null → 判断永远失败 | IL |
| 正确取卡名 | 从 **`config.saveKey`** 读（兜底 `originalSaveKey`） | IL |
| 三叶草 id | **`Blover`**（普通）+ **`LuckyBlover`**（杂交特色）——都含 "Blover" | 资源实测 |

### 1.13.4 实现链路
```
patcher: 注入 TowerDefenseInGamePacketShow.Plant 开头
  → GameCheats.OnPacketAboutToPlant(packetShow)
      → 读 config.saveKey → 若含 "Blover" 且开关开
          → RemoveAllZombies(root)
```

### 1.13.5 代码骨架
```csharp
public static void OnPacketAboutToPlant(object packetShow)
{
    try
    {
        if (!ModSettings.BloverClearAll) return;
        string id = ReadCardId(packetShow);        // config.saveKey ?? originalSaveKey
        if (id == null || id.IndexOf("Blover", StringComparison.OrdinalIgnoreCase) < 0) return;
        RemoveAllZombies(_lastRoot);
        LogOnce("三叶草: 已清除全场僵尸");
    }
    catch (Exception ex) { LogOnce("三叶草异常: " + ex.Message); }
}
```

### 1.13.6 历史坑
1. **坑①从 `packetShow` 上找 `packetName`** → 字段不存在 → 恒 false → "没用"。
   **教训：先 `[F] <类型>` 确认字段存在，再写反射。**
2. **坑②只匹配 "Blover" 漏掉 LuckyBlover** → 杂交版特色卡不生效 → 用 `IndexOf` 包含匹配。
3. **坑③注入在方法内部某分支** → 只在部分情况下触发 → 注在**方法开头**最可靠。
4. **手机版同注入点**（`ImportReference` 版），两份 patcher 都要加。

### 1.13.7 验证方法
种三叶草 → 全场僵尸消失；日志 `三叶草: 已清除全场僵尸`。

### 1.13.8 扩展指引
"某张卡触发特殊效果"类功能通用套路：**注入 `Plant` / `OnPacketAboutToPlant` → 读 `saveKey` → 匹配 → 执行效果**。

---

## 1.14 炮类多发（CannonMultiShot）

### 1.14.1 用户看到什么
玉米炮/西瓜炮等炮类植物一次发射多发（1~10 发可调）。

### 1.14.2 开关与数据流
- 开关：`ModSettings.CannonMultiShot`（int）
- 类型：**反射写属性类**（每 30 帧写 `fireNum`）

### 1.14.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 发射数量 | 不同植物各自的 **`fireNum` 属性/字段** | IL |
| 支持的原生植物 | Cornpult / CabbageCob / Melonpult / PeaShooter / PuffShroom / Cactus 等 | IL |
| 不支持的 | **`PotatoCobCannon` 没有 `fireNum`**（土豆炮） | IL |
| 炮类冷却 | 冷却在**植物节点自身**：`restTime` 属性 + `_restTime` 字段（Double），**不是 `_cannonComponent`**（该字段不存在） | IL |
| 计时器命名冲突 | 电脑版已有 `_cannonTimer`（炮无冷却）→ 新功能必须用 `_multiShotTimer`，否则 `CS0102` | 编译 |

### 1.14.4 实现链路
```
OnFrame 每 30 帧
  → 遍历场上炮类植物
      → 反射设 fireNum = ModSettings.CannonMultiShot
```

### 1.14.5 代码骨架
```csharp
static int _multiShotTimer;                       // ★ 不要复用 _cannonTimer
static void ApplyCannonMultiShot(Node root)
{
    if (ModSettings.CannonMultiShot <= 1) return;
    if (++_multiShotTimer % 30 != 0) return;
    foreach (var p in CollectPlants(root))
    {
        string tn = p.GetType().Name;
        if (tn.IndexOf("CobCannon") < 0 && tn.IndexOf("Cannon") < 0) continue;
        TrySetPropOrField(p, "fireNum", ModSettings.CannonMultiShot);   // 没有的就静默跳过
    }
}
```

### 1.14.6 历史坑
1. **坑①炮冷却找错字段**（`_cannonComponent` 不存在）→ 冷却功能一直无效；
   正确：直接查节点自身 `restTime` / `_restTime`。
2. **坑②计时器重名** → `CS0102`（编译期就报，容易发现）。
3. **坑③不支持的植物不能报错**：`fireNum` 不存在时静默跳过，否则日志刷屏。
4. **坑④`AnyModActive` 要含 `CannonMultiShot > 1`**（不是"非 0"，1 表示原速）。

### 1.14.7 验证方法
日志确认写入了哪些植物；真机：玉米炮一次打出多发炮弹；调回 1 恢复单发。

### 1.14.8 扩展指引
"同一属性在多种植物上"的功能，一律用**类型名包含匹配 + 静默跳过不支持的类型**。

---

## 1.15 果冻模式 / Q弹（JellyMode / PlantSquash）

### 1.15.1 用户看到什么
植物被压扁/抖动（Q弹 = Y 轴周期压缩；果冻 = Y 压缩 + X 左右摇摆抽搐）。

### 1.15.2 开关与数据流
- 开关：`ModSettings.PlantSquash`（Q弹）、`ModSettings.JellyMode`（果冻）
- 调用条件：`PlantSquash || JellyMode`
- 类型：**每帧缩放类**

### 1.15.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 缩放载体 | 角色节点 `Scale`（Vector2） | IL |
| ⚠️ 僵尸 Scale **每帧被游戏重置** | 植物不会被重置 | 真机实测 |
| 因此 | 僵尸缩放必须**每帧**写；3 帧一次会被打回 | 实测 |

### 1.15.4 实现链路
```
OnFrame（每帧）
  → CollectSquashRoles(root) 收集 植物 + 僵尸（camp 判断）
  → 对每个角色：
      Q弹：  scale.X 保持原值，scale.Y = 1 - 0.25*sin(phase)     周期 30 帧
      果冻： scale.Y 同 Q弹，scale.X = 1 + 0.35*sin(phase*2)     周期 12 帧（更快 → 抽搐感）
```

### 1.15.5 代码骨架
```csharp
static void ApplyPlantSquash(Node root)
{
    if (!ModSettings.PlantSquash && !ModSettings.JellyMode) return;
    _squashPhase++;
    float y = 1f - 0.25f * Mathf.Sin(_squashPhase * 0.21f);          // Q弹
    float x = ModSettings.JellyMode ? 1f + 0.35f * Mathf.Sin(_squashPhase * 0.52f) : 1f;
    // ★ 电脑版无 using System → 用 System.Math.Max/Min 全限定
    foreach (var r in CollectSquashRoles(root))        // 含 Zombie！
        SetScale(r, x, y);                             // 僵尸必须每帧写
}
```

### 1.15.6 历史坑
1. **坑①僵尸不变形**：写 Scale 的频率是 3 帧一次，而僵尸 Scale 每帧被游戏重置 → 永远看不到。
   修复：拆分逻辑 —— 缩放每帧、透视框 3 帧。
2. **坑②`Math.Max` 找不到**：电脑版 `ModUI`/`GameCheats` 某些文件**没有 `using System`** → 必须全限定 `System.Math.Max`。
3. **坑③相位不递增** → 静态压扁（动画不动）。
4. **坑④诊断埋点**：首次打印 `角色缓存 植物=X 僵尸=Y 果冻=Z`，确认僵尸真的被收集到了。

### 1.15.7 验证方法
真机：开 Q弹植物呼吸式压扁；开果冻植物左右抽搐；僵尸也要动（验证坑①是否复发）。

### 1.15.8 扩展指引
"视觉变形"类功能一律注意**游戏是否每帧重置该属性** —— 判断方法：写一次后隔 3 帧再读，看值有没有被打回。

---

## 1.16 飞贼组合技（Bungi 系列）

### 1.16.1 用户看到什么
飞贼僵尸秒抓植物、无视保护伞、偷完生成小丑、全场飞贼一起偷。

### 1.16.2 开关与数据流
开关：`ModSettings.BungiFastGrab`（秒偷）、`BungiIgnoreUmbrella`、`BungiSpawnJackbox`、`ZombiesFollowMouse`（相关）、`BungiGrabAll`（按钮）。

### 1.16.3 真实游戏结构（含取证）—— 完整状态机（IL 实测）
```
类型：TowerDefenseZombieBungi / BungiBin / BungiSpawn
状态：zombie.bungi.drop → zombie.bungi.grab → zombie.bungi.rise → Destroy
事件：IdleProcessing 发 "ToGrab"（进 grab）；AnimeCompleted("Grab") 发 "ToRise"；（无 ToDrop，drop 是初始态）
_Ready：set_z(600)（从 600 高空开始）+ isGround=false + ConnectRoleStateSignals
位置机制：x/y = GlobalPosition（生命周期不变）；高度 = ★ z 属性（不是 y！）
DropEntered：set_z(600)、_bungeeTarget 显示、_targetDropTween 瞄准下落
CompleteDropPresentationAsync：等 1s → BungeeScream → 等 1s → dropTween z→地面(1s) → Drop 动画 → 等 1s
                              → isGround=true、z=groundHeight、**waitGrab=true** → Idle
GrabEntered：AnimeEvent("grab")：cell=GetMapCell(gridPos) → GetTarget / GetCharacterTargetNear
             → 植物 die + Destroy + **将植物建模 Reparent 到飞贼**（hasPlant=true）
RiseEntered：z→600 + _bungeeTarget→(0,-585)，duration = canBlock ? 1.5 : 0.75
保护伞：TowerDefensePlantUmbrellaleaf 的 CanBlock() = canBlock && z <= 50
```

### 1.16.4 实现链路
```
秒偷：设 waitGrab=true + waitTimer=3（跳过原生 3 秒等待），不做瞬移
无视保护伞：持续 SetPropOrField(plant, "canBlock", false)
偷后生成小丑：FindJackboxId（GetPacketIds(false) 找含 "Jackbox"）→ 飞贼 gridPos 处 SpawnCharacter
全场飞贼：_bungiGridQueue 从 8×5 格子队列逐个分配 gridPos（★ 必须设 gridPos，否则全挤一点）
```

### 1.16.5 代码骨架
```csharp
// ★ 分散：飞贼高度是 z 不是 y，y-300 是错的
static void SpreadBungiToGrid(Node z, Vector2I grid)
{
    Vector2 pos = GetMapCellPlantPos(grid);     // 格子中心原始坐标
    SetPropOrField(z, "gridPos", grid);          // ★ 必须设，否则 cell 相同 → 抓同一目标
    SetLogicalGlobalPosition(z, pos);            // 用游戏原生的逻辑位置设置
    // 高度交给 Drop 状态管理（不要自己抬 y）
}
```

### 1.16.6 历史坑
1. **坑①`y-300` 抬高度** → 飞贼飞到地上方 300px 的错位置。**高度是 `z` 不是 `y`。**
2. **坑②没设 `gridPos`** → 所有飞贼 `cell` 相同 → 抓同一株植物、下落高度相同 → **全部挤在一起**。
3. **坑③开了秒偷后聚在一起**：旧实现把每个飞贼瞬移到"最近的植物上方" → 多个飞贼找同一株 → 完全重叠。
   修复：**去掉瞬移块**，飞贼抓取目标本来就是"自身 gridPos 格"。
4. **坑④关不掉秒偷**：`ApplyBungiAutoGrab` 无条件设 `waitGrab=true`（与开关无关）→ 关了也秒偷。
   修复：只在 `ModSettings.BungiFastGrab` 时设。
5. **坑⑤强制 `SendStateEvent("ToGrab")` 导致空中抓取**（Drop 中被打断）→ 移除，只靠 `waitGrab+waitTimer` 让状态机自然流转。
6. **坑⑥直接用 `GlobalPosition` 设位置会被移动组件每帧覆盖** → 必须用游戏原生
   **`TowerDefenseCharacter.SetLogicalGlobalPosition(Vector2)`**（游戏拖拽植物/击退都用它）。

### 1.16.7 验证方法
诊断日志（每 120 帧）：`飞贼诊断: 名 grid= pos= z= ground= wait= has=` —— 判断分散/下落/抓取/升空状态；
真机：秒偷开 = 各飞贼各抓自己那格（不聚）；秒偷关 = 3 秒后自然抓。

### 1.16.8 扩展指引
复杂僵尸行为功能，**先画状态机再写代码**（用 `il:` 反汇编 `ConnectRoleStateSignals` 的绑定清单），
否则一定会与游戏的异步动画流程打架。

---

## 1.17 小丑秒炸（Jackbox）

### 1.17.1 用户看到什么
小丑僵尸瞬移到目标面前立刻爆炸。

### 1.17.2 开关与数据流
- 开关：`ModSettings.JackboxFastBomb`
- 目标选择：敌方小丑 → 最近**植物**；被魅惑的小丑 → 最近**僵尸**

### 1.17.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 类型 | `TowerDefenseZombieJackbox` | IL |
| 原版爆炸路径 | `AnimeCompleted("Bomb")` → `CreateEffect()` + `Destroy()` | IL |
| 秒炸实现 | 瞬移到目标前 `(-20, 0)` → 调 `CreateEffect()` → `QueueFree()` | IL |

### 1.17.4 实现链路
```
每帧（或降频）
  遍历 Jackbox 类型僵尸
    camp == "Zombie"  → target = 最近植物
    camp != "Zombie"  → target = 最近僵尸（被魅惑）
    SetLogicalGlobalPosition(target.pos + (-20,0))
    InvokeCreateEffect(); node.QueueFree();
```

### 1.17.5 代码骨架
```csharp
static void ApplyJackboxFastBomb(Node box)
{
    var target = GetCamp(box) == "Zombie" ? NearestPlant(box) : NearestZombie(box);
    if (target == null) return;
    SetLogicalGlobalPosition(box, GetPos(target) + new Vector2(-20, 0));
    InvokeMethod(box, "CreateEffect");     // 原版爆炸就是 CreateEffect + Destroy
    box.QueueFree();
}
```
> ⚠️ `QueueFree` 是 **`Node`** 的方法：`if (box is Godot.Node n0) n0.QueueFree();`

### 1.17.6 历史坑
1. **坑①`GodotObject.QueueFree()` 不存在**（是 `Node` 的方法）→ 编译期就报，改 `is Godot.Node`。
2. **坑②忘了区分阵营** → 被魅惑的小丑去炸植物（自相残杀方向错）。
3. **坑③全场吸附鼠标的功能要排除秒炸小丑**（否则小丑被吸到鼠标而非目标）。

### 1.17.7 验证方法
日志 `小丑秒炸:` 段（打印 camp/目标/植物数僵尸数）；真机：小丑出现即瞬移爆炸。

### 1.17.8 扩展指引
"让某个僵尸立刻完成某行为"类功能：**拼出原生行为序列（瞬移 → 效果 → 销毁）**，不要自己造动画。

---

## 1.18 篡改系统（Trick）—— 最复杂、踩坑最多的子系统

### 1.18.1 用户看到什么
可以让"盲盒 / 种子雨 / 传送带 / 礼盒 / 抽卡植物"产出**用户指定的卡**（而不是随机），
卡池由外置面板的「篡改卡池选择器」可视化勾选，写到 `mod/trickpool.txt`。

### 1.18.2 开关与数据流
| 开关 | 作用 |
|---|---|
| `TrickEnabled` | **总开关**（= 全开，见坑①） |
| `TrickBox` | 盲盒/礼盒/抽卡植物 |
| `TrickRain` | 种子雨 |
| `TrickConveyor` | 传送带 |
| `TrickFixedOnly` | 只替换池外卡（不闪不卡） |
| `TrickPool` | 逗号分隔的卡 id 串 |
| `SeedBankEveryFrame` | 卡槽 1 帧 1 刷 |

数据流：`外置面板勾选 → 写 trickpool.txt → POST /set?name=TrickReload&val=1 → GameCheats.TrickReloadFile() 读入`。
> 为什么用文件接力而不是直接 `/set?TrickPool=...`：**池子可能是几百张卡 → URL 超长会被截断**。

### 1.18.3 真实游戏结构（含取证）
| 目标 | 真实位置 | 取证 |
|---|---|---|
| 老虎机（盲盒） | `TowerDefenseBattleFeatureSlotMachine._slotItems`（私有 `List<SlotItem>`）+ `_slotItemLookup`；`SlotItem`：Key/DisplayKey/Type(私有枚举)/Amount/Weight | IL |
| 种子雨 | `TowerDefenseBattleFeatureRainMode._packetList`（`List<TowerDefenseRainModePacketConfig>`：name/weight） | IL |
| 传送带 | `TowerDefenseBattleFeatureConveyorBelt.packetList`（**Godot 数组**）+ `packetPrioritySpawnList` | IL |
| 礼盒 | `TowerDefensePlantPresentBox` / `PresentBoxGreen` 的 `Explode()` | IL |
| 抽卡植物族 | `TowerDefensePlantBYWZ`(备用物质) / `Upgradebean`(升级豆，2 张) / `GardenSet`(花园套装) / `LampShroom`(路灯菇，左右各 1) / `MagicBean`(魔法豆) / `LuckyBlover`(幸运三叶草，**直接进卡槽**) 的 `Explode()` | `ref:Explode` 定位 |
| 私有嵌套类构造 | `Activator.CreateInstance(elemType, true)`（`true` = 允许非 public） | 实现需要 |

### 1.18.4 实现链路
```
① 锁池（只改 .NET List 的部分）
   TrickLockPool(featKey)
     → 快照原池（_trickPoolBackup / _trickLookupBackup）
     → Clear + Add（MakeSlotItem / MakeRainPacket 构造新元素）
     → 若 Add 后数量为 0 → 回滚还原（绝不留下空池！）
② 传送带（Godot 数组，不能直接改）
   → 改"呈现端"：conveyorBeltManager.GetPacketChildren(isMobileUI) 拿到的对象上 Init 覆盖内容
③ Explode 类（礼盒 / 抽卡植物）
   → patcher 注入每个 Explode 开头：if (GameCheats.OnPresentBoxExplode(this)) return;
   → 先产出（Plant/SpawnCharacter）成功 → 再让格 + QueueFree 礼盒（顺序不能反）
④ 卡槽
   AutoRandomizeSeedBank：TrickEnabled 且池非空 → 只替换"不在池"的卡（不闪不卡）
```

### 1.18.5 代码骨架
```csharp
// 判定：★ 总开关 = 全开（各子开关保留是为了面板能分别显示"当前会篡改哪几类"，语义上不参与判定）
static bool TrickBoxOn      => ModSettings.TrickEnabled;
static bool TrickRainOn     => ModSettings.TrickEnabled;
static bool TrickConveyorOn => ModSettings.TrickEnabled;

// 旧实现（错误示范，勿再使用）：
//   static bool TrickBoxOn => ModSettings.TrickEnabled && ModSettings.TrickBox;
//   → 只开总开关时 = false → 什么都不做，用户报"篡改没用"

// 锁池（含快照与回滚）
static void TrickLockPool(string featKey)
{
    var feat = FindFeatureByKey(featKey);
    var list = ReadMember(feat, "_slotItems") as System.Collections.IList;
    if (list == null) { LogOnce("篡改: " + featKey + " 池不是 IList"); return; }
    Snapshot(featKey, list);
    list.Clear();
    foreach (var id in PoolIds())
    {
        var item = MakeSlotItem(list, id);          // Activator.CreateInstance(elemType, true)
        if (item == null) { LogOnce("SlotItem.Key 写入失败 " + id); continue; }
        list.Add(item);
    }
    if (list.Count == 0) { RestoreTrickPool(featKey); LogOnce("重建失败（0 张），已回滚原池"); return; }
    LogOnce("已锁定 " + list.Count + " 张目标卡（池=" + ModSettings.TrickPool + "）");
}

// 礼盒：先产出后销毁
public static bool OnPresentBoxExplode(object box)
{
    if (!TrickBoxOn || PoolIds().Count == 0) return false;
    string id = PickFromPool();
    if (!Produce(id, GetPos(box))) return false;        // 产出失败 → 走原逻辑
    RemoveCharacterAt(GetGridPos(box));
    if (box is Godot.Node n0) n0.QueueFree();
    return true;
}
```

### 1.18.6 历史坑（本子系统的坑密度最高）
1. **坑①判定写成"总开关 且 子开关"** → 用户只开总开关 = **什么都不做**。
   证据：用户 `modsettings.txt` 里 `TrickEnabled/TrickBox/TrickRain/TrickConveyor 全 False`，
   日志只有"目标卡池已从文件加载"没有"已锁定"。修复：总开关 = 全开。
2. **坑②空池**：锁池失败留下空池 → 盲盒/种子雨**什么都开不出来**（比"随机"更糟）。
   修复：快照 + 失败回滚。
3. **坑③顺序反了**：先移除礼盒再产出 → 产出失败时"白吃一个礼盒"。修复：先产出成功再销毁。
4. **坑④传送带是 Godot 数组**：直接改 `.NET IList` 的那套对它无效 → 必须走呈现端 `Init` 覆盖。
5. **坑⑤`FillArgs` 只填类型默认值**：`SpawnPacket(cfg, pos, 15.0, isFall, useCost, useRandf)` 的 `useRandf`
   C# 默认是 `true`，不显式传就变成 `false` → 掉落行为不对。**显式传全部参数。**
6. **坑⑥`LuckyBlover` 走的是 `AddPacket`（进卡槽）而不是 `SpawnPacket`（掉地上）** —— 按类型分派。
7. **坑⑦"种下的植物也会随机"**：`OnPacketAboutToPlant` 里每次种卡前把卡 Init 成随机植物
   → 种出随机植物 + 破坏种植状态。修复：**移除该调用**，随机只发生在卡槽层。
8. **坑⑧`MakeSlotItem` 写 Key 后不回读** → 造出无效卡（开出来是空白）。修复：写后回读校验。

### 1.18.7 验证方法
日志阶梯：`目标卡池已从文件加载 N 字符` → `已快照 X 原始池 N 项` → `已锁定 N 张目标卡（池=...）`
→ 失败时 `重建失败（0 张），已回滚原池`；真机：开盲盒/下雨/传送带产出**指定卡**；关掉开关恢复随机。

### 1.18.8 扩展指引
篡改类功能通用套路：**① 定位产出池 → ② 快照原池 → ③ 重建为新池 → ④ 失败回滚 → ⑤ 关开关还原**。
凡是"游戏的池子"，都要先确认它是 `.NET List` 还是 `Godot 数组` —— 两者改法完全不同。

---

## 1.19 子弹追踪（BulletTrack）

### 1.19.1 用户看到什么
打出的子弹自动拐弯追踪最近的僵尸。

### 1.19.2 开关与数据流
- 开关：`ModSettings.BulletTrack`（bool）
- 类型：**运行期字段写入**（不走注入），且**必须被 `BulletModsActive()` 覆盖到**

### 1.19.3 真实游戏结构（含取证）—— 子弹系统全景（0.27 实测）
| 对象 | 结构 | 说明 | 取证 |
|---|---|---|---|
| `BulletField`（Node2D） | `_data`（`BulletData[]`） | 子弹数组（`Spawn` 里 `stelem.any BulletData` 确认） | IL |
| | `_activeCount`（Int32） | 活跃数量 | IL |
| | `_freeList` / `_freeCount` | 空闲槽（池化分配） | IL |
| | **没有 `_activeIndices`** | 旧代码依赖它会拿到 null → 需回退到"遍历整个 `_data` 查 `active`" | 探针 `[F]` |
| `BulletData`（struct） | `active` / `over` / `pos` / `vel` / `speed` / `config`(TowerDefenseProjectileConfig) / **`trackOpen`(bool)** / `trackSearchInterval`(int) / `damage` / `fireCharacter` | 追踪只读 `trackOpen` | IL |
| 追踪生效点 | `UpdateSimulation` → 若 `trackOpen` → `ProcessTrackData` | **`UpdateSimulation` 只读不重置** `trackOpen` | IL |

### 1.19.4 实现链路
```
OnFrame（降频）→ 到达"子弹段"
  → 找 BulletField（场景树 / 缓存类型）
  → 遍历 _data：active 的子弹 → SetField(bulletData, "trackOpen", true)
      ★ BulletData 是 struct：必须改完写回数组（或用 ref 语义的写法），否则改的是副本！
```

### 1.19.5 代码骨架
```csharp
static void ApplyTrackRuntime(Node root)
{
    if (!ModSettings.BulletTrack) return;
    var bf = FindBulletField(root);
    if (bf == null) { BulletDiag("未找到 BulletField"); return; }
    var arr = ReadField<Array>(bf, "_data");
    int n = ReadField<int>(bf, "_activeCount");
    for (int i = 0; i < arr.Length; i++)
    {
        object bd = arr.GetValue(i);
        if (!ReadField<bool>(bd, "active")) continue;
        WriteField(bd, "trackOpen", true);        // ★ struct：SetValue 写回数组元素
        arr.SetValue(bd, i);
    }
    BulletDiag("处理活动子弹 追踪=on 数量=" + n);
}
```

### 1.19.6 历史坑
1. **坑①`BulletData` 是 struct** → 直接改局部变量无效，必须写回数组（`arr.SetValue`）。
2. **坑②依赖 `_activeIndices`** → 该字段不存在（null）→ 遍历 0 颗。修复：遍历整个 `_data` 查 `active`（慢但正确），或改用 `_activeCount` + `_freeList` 语义。
3. **坑③"到达子弹段"但 `BulletField=null`**：在 Loading 界面是正常的（战斗才有）→ 别把这条日志当 bug。
4. **坑④开关条件被 `BulletModsActive()` 漏掉** → 只开追踪时整个子弹模块不执行。
   `BulletModsActive()` 必须包含：追踪 / 随机 / 跟随鼠标 / 自定义子弹类型（`BulletType.Length > 0`）/ 自动开火。
5. **坑⑤与"跟随鼠标"冲突** → 同时开时子弹抽搐。修复：跟随模式下**关闭追踪**（互斥）。

### 1.19.7 验证方法
日志 `子弹诊断[到达子弹段]: BulletField=... _data=ok/missing _activeCount=... _activeIndices=...`；
真机：子弹明显拐弯追僵尸。

### 1.19.8 扩展指引
`BulletField` 是子弹系统唯一入口；任何"子弹行为"功能都从这里进（除了地刺类走 `ComponentAttack`，见 1.20 坑⑤）。

---

## 1.20 随机子弹（RandomBullet）

### 1.20.1 用户看到什么
植物每次开火，子弹种类都会随机变化（外观/行为都变）。

### 1.20.2 开关与数据流
- 开关：`ModSettings.RandomBullet`（bool）+ `ModSettings.BulletType`（自定义子弹类型，字符串）
- 重摇频率：每 10 次调用（≈0.33 秒）全量重摇

### 1.20.3 真实游戏结构（含取证）—— 换种类的 API 与返回码真相
| API | 签名 | 真相 | 取证 |
|---|---|---|---|
| `BulletField.TryChangeBulletDataInPlace` | `(int, TowerDefenseProjectileConfig)` → `BulletChangeInPlaceResult` | **internal（非 public）**，`GetMethod(name)` 默认搜不到 → 返回 **null** | IL + 独立加载测试 |
| `BulletField.ChangeBulletData` | `(int, cfg, TowerDefenseCharacter, Nullable<double>)` | 传 **null character 永远返回 -1**（`ldarg.2 brtrue` 后直接 return -1） | IL |
| 返回码语义 | `-1`=active/config 无效；`1`=Changed；`2`=**E_CHANGE_RENDER_UNSUPPORTED** | **是枚举状态码，不是子弹下标！** | IL |
| 换不动的条件 | `GetOrCreatePreparedProjectileChange(config).IsValid` 取决于**目标 config 的渲染模板+行为程序**是否有效（与"当前子弹"无关） | 大多数正常投射物可换 | IL |

### 1.20.4 实现链路
```
每 10 次调用（reroll）：
  → 随机选一个候选 config（PickRandomProjectileName 随机 8 次跳过非攻击类）
  → 优先 TryChangeBulletDataInPlace（2 参，无 character 依赖）
      → ★ 调用后**读回校验**：arr[i].config 是否 ReferenceEquals(newCfg)（或 saveKey 相同）
  → 备选 ChangeBulletData（★ 必须传真实 character：从 _data[i].fireCharacter 读）
  → 都失败 → 回退只改 config 字段（只影响大小/外观，种类不变）
```

### 1.20.5 代码骨架
```csharp
static bool TryChangeBulletViaApi(object bf, int idx, object newCfg)
{
    // ① 优先 internal 的 InPlace 版本（注意 BindingFlags.NonPublic）
    var m1 = FindMethodExact(bf.GetType(), "TryChangeBulletDataInPlace",
                             new[] { typeof(int), _projCfgType });
    if (m1 != null)
    {
        m1.Invoke(bf, new object[] { idx, newCfg });
        if (VerifyBulletConfigChanged(bf, idx, newCfg)) return true;   // ★ 读回校验，不信返回码
    }
    // ② 备选：必须传真实 character（从 _data[idx].fireCharacter 拿），传 null 必然失败
    var arr = ReadField<Array>(bf, "_data");
    object bd = arr.GetValue(idx);
    object ch = ReadField<object>(bd, "fireCharacter");
    var m2 = FindMethodExact(bf.GetType(), "ChangeBulletData",
                             new[] { typeof(int), _projCfgType, _charType, typeof(double?) });
    if (m2 != null && ch != null)
    {
        m2.Invoke(bf, new object[] { idx, newCfg, ch, (double?)null });
        if (VerifyBulletConfigChanged(bf, idx, newCfg)) return true;
    }
    return false;
}

static bool VerifyBulletConfigChanged(object bf, int idx, object newCfg)
{
    var arr = ReadField<Array>(bf, "_data");
    object bd = arr.GetValue(idx);
    object cur = ReadField<object>(bd, "config");
    if (ReferenceEquals(cur, newCfg)) return true;                   // 同一引用
    return GetSaveKey(cur) == GetSaveKey(newCfg);                    // 兜底：内容相同也算成功
}
```

### 1.20.6 历史坑（本功能是本工程 bug 密度最高的之一）
1. **坑①信返回码**：把 `ChangeBulletData` 的返回值当"子弹下标"比较（`ri == idx`）→ 永远 false →
   回退只改 config 字段（**外观不变**）→ 用户说"随机子弹偶尔有效"。
2. **坑②`TryChangeBulletDataInPlace` 是 internal**：`GetMethod` 默认只搜 public → 返回 null →
   日志显示"API=无"→ 又走回坑①。**必须 `BindingFlags.NonPublic | Instance | Static`。**
3. **坑③`ChangeBulletData` 传 null character 永远失败**（IL 里 `ldarg.2 brtrue` 直接 return -1），
   必须从 `_data[idx].fireCharacter` 取真实 character。
4. **坑④随机池含非攻击子弹**（Coin / Sun / Magnet / Chest / Token）→ 随机到就"子弹没了"。
   修复：`PickRandomProjectileName()` 随机 8 次跳过非攻击类。
5. **坑⑤`BulletModsActive()` 漏 `BulletType`**：只选"自定义子弹"时 `ApplyBulletModsRuntime` 不执行 → 完全无效果。
6. **坑⑥机制限制**：`TowerDefenseItemSpikeball`（地刺）基类是 **`TowerDefenseItem`（道具，不是植物）**，
   用 `ComponentAttack()` 近战，**不走 `BulletField`** → 随机子弹对它**天然无效**。
   用户报"尖刺子弹无法随机"时，这是机制限制而不是 bug，要如实解释。`Spike*` + `CactusKnifeFull` 是投射物时可以随机。

### 1.20.7 验证方法
日志 `随机子弹: 换种类生效 新=PeaTrack` / `换种类API失败回退改config`；真机：连续开火外观不断变化。

### 1.20.8 扩展指引
**凡是"调用游戏 API 改状态"的地方，一律"调用后读回校验"，不要依赖返回值语义** —— 这是本工程用血换来的通则。

---

## 1.21 子弹跟随鼠标（BulletFollowMouse）

### 1.21.1 用户看到什么
已发射的子弹在半空中拐向鼠标位置。

### 1.21.2 开关与数据流
- 开关：`ModSettings.BulletFollowMouse`（bool）
- 与追踪互斥（同时开 → 抽搐）

### 1.21.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 移动方式 | 游戏物理用 **`vel`** 推进位置（不是直接改 `pos`） | IL `UpdateSimulation` |
| 因此 | 跟随鼠标应改 **`vel` 指向鼠标**，**不要动 `pos`** | 实现验证 |
| 追踪冲突 | `trackOpen=true` 时游戏自己会改方向 → 必须**关掉 trackOpen** | 实测 |

### 1.21.4 实现链路
```
每帧（降频）
  → 鼠标位置 = root.GetViewport().GetCamera2D().GetGlobalMousePosition()
  → 每颗 active 子弹：
      trackOpen = false                              // 防抽搐
      vel = normalize(mouse - pos) * 原速度
```

### 1.21.5 代码骨架
```csharp
static void MoveBulletsToMouse(Node root, object bf)
{
    var cam = root.GetViewport().GetCamera2D();
    if (cam == null) return;
    Vector2 mouse = cam.GetGlobalMousePosition();
    var arr = ReadField<Array>(bf, "_data");
    for (int i = 0; i < arr.Length; i++)
    {
        object bd = arr.GetValue(i);
        if (!ReadField<bool>(bd, "active")) continue;
        Vector2 pos = ReadField<Vector2>(bd, "pos");
        double sp = ReadField<double>(bd, "speed");
        Vector2 dir = (mouse - pos).Normalized();
        WriteField(bd, "vel", dir * (float)sp);
        WriteField(bd, "trackOpen", false);
        arr.SetValue(bd, i);          // ★ struct 写回
    }
}
```

### 1.21.6 历史坑
1. **坑①改 `pos` 而游戏用 `vel`** → 下一帧被覆盖（或抖动）。
2. **坑②不关追踪** → 游戏与 MOD 同时改方向 → 抽搐。
3. **坑③`BulletData` 是 struct** → 必须写回数组（同 1.19 坑①）。
4. **坑④相机为空**：`GetCamera2D()` 在非战斗场景可能为 null → 必须先判空。

### 1.21.7 验证方法
真机：子弹明显拐向鼠标；日志无异常；关掉立即恢复直线。

### 1.21.8 扩展指引
与 `1.19` 追踪共享同一个 `BulletField` 遍历骨架 —— 建议把"遍历活跃子弹"抽成一个公共方法，避免每个功能各写一遍。

---

## 1.22 自动开火（AutoFire）

### 1.22.1 用户看到什么
植物自动锁定全图僵尸并开火（不需要僵尸进入射程）。

### 1.22.2 开关与数据流
- 开关：`ModSettings.AutoFire`（bool）
- 类型：**调用游戏开火方法**（不是改字段）

### 1.22.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 开火方法 | `FireComponent.Fire()`（存在，且用 `BulletFieldSpawnOverrides`） | IL |
| 子弹去向 | 走 `BulletField.Spawn` → 进 `_data` | IL |
| 注意 | 自动开火产生的子弹**同样会被随机/追踪/跟随鼠标处理**（不要重复处理） | 实现约定 |

### 1.22.4 实现链路
```
每帧/降频
  → 收集场上植物（CollectPlants）
  → 收集场上僵尸（CollectZombies）
  → 对每株植物：找 FireComponent → 若冷却就绪 → Invoke("Fire")
      （冷却由游戏自身 timer 控制，不要自己造频率）
```

### 1.22.5 代码骨架
```csharp
static int _autoFireTimer;
static void ApplyAutoFire(Node root)
{
    if (!ModSettings.AutoFire) return;
    if (++_autoFireTimer % 10 != 0) return;          // 降频，防掉帧
    var zombies = CollectZombies(root);
    if (zombies.Count == 0) return;
    foreach (var p in CollectPlants(root))
    {
        var fc = FindFireComponent(p);
        if (fc == null) continue;
        InvokeNoArg(fc, "Fire");                     // 冷却由游戏自己管
    }
}
```

### 1.22.6 历史坑
1. **坑①自己实现"每 N 帧开一次火"** → 无视游戏冷却 → 帧率越低射速越慢（或与游戏计时器打架）。
   正确：调游戏 `Fire()`，让游戏自己判冷却。
2. **坑②自动开火 + 随机子弹**：两者都遍历 `_data` → 每帧重复处理（浪费）。建议合并成一次遍历。
3. **坑③未降频** → 植物多时明显掉帧。

### 1.22.7 验证方法
真机：植物不停向全场开火；帧率无明显下降。

### 1.22.8 扩展指引
"让游戏自己做事"优于"MOD 自己实现" —— 前者自动兼容冷却/动画/音效，后者要自己补一大堆。

---

## 1.23 植物/僵尸无敌（Invincible）

### 1.23.1 用户看到什么
植物（或僵尸）不掉血、不被消灭。

### 1.23.2 开关与数据流
- 开关：`ModSettings.PlantInvincible` / `ModSettings.ZombieInvincible`
- 类型：**注入类**（拦伤害）或**数值类**（血量写极大）

### 1.23.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 真实血量位置 | **`instance.hitpoints`** 与倍率 **`instance.hitpointScale`**（在 `TowerDefenseCharacter.instance` 上，不在节点裸字段） | 反编译 `TowerDefenseCharacter` |
| 总血量/当前血量 | `GetTotalHitPoint()` / `GetCurrentHitPoint()` | IL |
| 为什么改节点字段没用 | 节点上的是缓存/序列化值，真实状态在 instance | IL |

### 1.23.4 实现链路（两种，优先第 2 种）
```
方式 A（数值）：每帧把 instance.hitpoints 写到极大
   风险：某些"秒杀/清除"逻辑不走血量（如 RemoveAllZombies、小推车）→ 依然会被删
方式 B（注入拦截，推荐）：patcher 在伤害方法开头插入
   if (GameCheats.ShouldIgnoreDamage(target)) return;      // 不做伤害结算
   优点：免疫所有伤害路径；可实时开关
```

### 1.23.5 代码骨架
```csharp
public static bool ShouldIgnoreDamage(object target)
{
    string camp = GetCamp(target);
    if (camp == "Plant"  && ModSettings.PlantInvincible)  return true;
    if (camp == "Zombie" && ModSettings.ZombieInvincible) return true;
    return false;
}
```

### 1.23.6 历史坑
1. **坑①改错位置**（改节点字段）→ 无效且不报错（同 1.5 植物射速的同类错误）。
2. **坑②"无敌"不等于"不会被移除"**：游戏有直接删除对象的方法（`QueueFree` / `RemoveCharacter`），
   血量无敌照样被删。要拦的是**伤害**，不是**删除**。
3. **坑③无敌导致关卡无法结束**：僵尸无敌 → 玩家打不死 → 关卡卡住。UI 上要提示，或提供"一键清场"。

### 1.23.7 验证方法
真机：僵尸打植物不掉血；植物打僵尸不掉血；切换开关立即生效。

### 1.23.8 扩展指引
"免疫/免伤"类功能优先走**注入拦伤害**；"数值类"（血量/攻速/阳光）才走**反射写字段**。

---

## 1.24 禁止攻击 / 禁止移动 / 禁止出僵尸（NoAttack / NoMove / NoZombie）

### 1.24.1 用户看到什么
- 禁止攻击：双方都不攻击（植物不开火、僵尸不啃）
- 禁止移动：僵尸站着不动（含气球、巨人、车）
- 禁止出僵尸：关卡不再生成新僵尸（已有的也会被清）

### 1.24.2 开关与数据流
开关：`ModSettings.NoAttack` / `NoMove` / `NoZombieSpawn`

### 1.24.3 真实游戏结构（含取证）—— 注入点清单（patcher 维护）
| 功能 | 注入目标 | 取证 |
|---|---|---|
| 植物禁攻 | `FireComponent.IdleProcessing` | IL |
| 僵尸禁攻 | `AttackComponent.CanAttack` | IL |
| 僵尸禁移 | `TowerDefenseZombie.BatchUpdate` / `BatchUpdateValidated` | IL |
| 地面移动 | `GroundMoveComponent.BatchUpdate` / `BatchUpdateValidated` | IL |
| 气球飞行 | `TowerDefenseZombieBalloon.FlyProcessing` | IL |
| 角色移动（巨人/车） | `CharacterMoveComponent.PhysicsProcess` | IL |
| 僵王低头 | `TowerDefenseZombieBoss.HeadExitedEntered` | IL |
| 巨人投小鬼 | `ImpThrowerComponent.SpawnImp` | IL |
| 禁止出僵尸 | 每 15 帧删场上僵尸（不注入；因为生成路径太多） | 实现 |

### 1.24.4 实现链路
```
patcher：InjectReturnZeroIf / InjectReturnFalseIf 系列
  FireComponent.IdleProcessing        → if (GameCheats.ShouldBlockPlantAttack()) return;
  AttackComponent.CanAttack           → if (GameCheats.ShouldBlockZombieAttack()) return false;
  TowerDefenseZombie.BatchUpdate      → if (GameCheats.ShouldBlockZombieMove()) return;
  ...（其余同理）
GameCheats：每个判定函数读对应开关
```

### 1.24.5 代码骨架
```csharp
public static bool ShouldBlockPlantAttack()  => ModSettings.NoAttack;
public static bool ShouldBlockZombieAttack() => ModSettings.NoAttack;
public static bool ShouldBlockZombieMove()   => ModSettings.NoMove;

// 禁止出僵尸：不注入，改"每 15 帧清场"（生成路径太多，注入不现实）
static int _noZombieTimer;
static void ApplyNoZombie(Node root)
{
    if (!ModSettings.NoZombieSpawn) return;
    if (++_noZombieTimer % 15 != 0) return;
    foreach (var z in CollectZombies(root)) if (z is Godot.Node n) n.QueueFree();
}
```

### 1.24.6 历史坑
1. **坑①"禁止移动"只注了一个地方** → 气球僵尸还在飞、巨人还在走、车还在开。
   必须**按移动组件清单逐个注入**（上表 5 个）。
2. **坑②禁止攻击导致关卡无法结束**（僵尸不死、植物不打）→ UI 提示。
3. **坑③注入某个 `BatchUpdate` 可能影响其它逻辑**（它可能兼做动画/状态推进）→
   只拦"判定/推进"，不要整个 `ret`（除非确认无副作用）。
4. **坑④`ImpThrowerComponent.SpawnImp` 这类"派生行为"要单独拦**（否则巨人还在投小鬼）。

### 1.24.7 验证方法
逐项真机验证：植物不开火 / 僵尸不啃 / 僵尸不动（含气球、巨人）/ 不出新僵尸；
**特别验证"关卡能否正常结束"**（避免做成死局）。

### 1.24.8 扩展指引
"冻结"类功能都属于"多点注入"：**先列全所有相关组件，再逐个注入，最后逐项验证**（不要只测一种僵尸）。

---

## 1.25 颜色律动（GlobalColor，16 种样式）

### 1.25.1 用户看到什么
游戏画面有 16 种可选的颜色律动主题（彩虹/红金/蓝紫/霓虹/粉彩/青绿/纯白/火焰/薄荷/樱花粉/紫罗兰/海洋/日落/极光/森林/柠檬），颜色随时间流动。

### 1.25.2 开关与数据流
- 开关：`ModSettings.Enabled`（启用彩色）+ `ModSettings.Style`（0-15）+ `ModSettings.Speed`
- ⚠️ **用户报"律动没了"时，第一件事是确认 `Enabled` 是否为 true**（默认 false）

### 1.25.3 真实游戏结构（含取证）
| 平台 | 实现 | 取证 |
|---|---|---|
| PC | **自定义 shader**：`uniform float style : hint_range(0.0, 15.0)`（0..15 共 16 样式） | shader 源码 |
| 手机 | **无 shader** → 走主题色 switch（16 个 case） | 源码 |
| 手机特例 | `case 6`（纯白）必须初始化 `bcolor = Colors.White`，否则 **CS0165**（使用了未赋值的局部变量） | 编译 |

### 1.25.4 实现链路
```
FrameDriver（非战斗场景也走）
  → GlobalColor.Tick(root, delta)
      → 计算 t = 时间 * Speed
      → ★ 颜色量化：_tq = Math.Round(t*5)/5  （同色保持约 10 帧）
      → ApplyColor(_tq, style)   ← 内部按 _lastColor 缓存命中，相同直接 return
      → PC：写 shader uniform；手机：按 style switch 生成颜色
```

### 1.25.5 代码骨架
```csharp
static double _lastTq = -1; static int _lastStyle = -1;

public static void Tick(Node root, double delta)
{
    if (!ModSettings.Enabled) return;
    _acc += delta * ModSettings.Speed;
    double tq = System.Math.Round(_acc * 5.0) / 5.0;      // ★ 量化：减少 90% 重算
    if (tq == _lastTq && ModSettings.Style == _lastStyle) return;   // ★ 缓存命中
    _lastTq = tq; _lastStyle = ModSettings.Style;
    ApplyColor(tq, ModSettings.Style);
}
```

### 1.25.6 历史坑
1. **坑①主界面卡顿/假死**：全屏统一调色 + 每帧重算主题色 → CPU 打满。
   修复三件套：**颜色量化**（同色保持 ~10 帧）+ **ApplyColor 降频**（2 → 8~12 帧）+
   **UI 流光降频**（3 → 20 帧）+ **`MakeGoldStyleBox` 结果缓存复用**（只刷新 BorderColor）。
2. **坑②手机 `case 6` 未初始化** → `CS0165` 编译失败。
3. **坑③"律动没了"** → `Enabled` 默认 false（用户没开"启用彩色"）。**先问开关，再查代码。**
4. **坑④样式数与 shader 的 `hint_range` 不一致** → 第 16 种样式无效（必须 0.0~15.0）。

### 1.25.7 验证方法
PC：16 种样式逐个切换都能看出差异；手机：同样（除 shader 差异）；主界面帧率正常。

### 1.25.8 扩展指引
"全屏视觉"类功能必须做**量化 + 缓存 + 降频**三件事，否则必然拖垮主界面。

---

## 1.26 ESP 透视与缩放

### 1.26.1 用户看到什么
显示僵尸/植物的透视框与血条；可放大/缩小指定角色。

### 1.26.2 开关与数据流
开关：`ModSettings.ESP`（透视）、缩放相关开关

### 1.26.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 缩放载体 | `Scale`（Vector2） | IL |
| **僵尸 Scale 每帧被游戏重置** | 植物不会 | 真机实测 |
| 因此 | 缩放必须**每帧**写；透视框可以 3 帧一次 | 实测结论 |
| 节点遍历 | 用 `camp == "Zombie"/"Plant"` 判定（递归场景树收集） | 实现 |

### 1.26.4 实现链路（关键：拆成两个不同频率的函数）
```
OnFrame（每帧）
  if (needScale) WalkScale(root)     ← 每帧：只设 Scale（因为会被重置）
  if (needESP)   if (++_t % 3 == 0) Walk(root)   ← 3 帧：画框/血条
```

### 1.26.5 代码骨架
```csharp
static void WalkScale(Node root)          // 每帧
{
    foreach (var z in CollectZombies(root)) SetScale(z, ModSettings.ZombieScale, ModSettings.ZombieScale);
}
static void Walk(Node root)               // 3 帧一次
{
    foreach (var z in CollectZombies(root)) DrawBox(z, Color.Red);
}
```

### 1.26.6 历史坑
1. **坑①`++_frame % 3 != 0 return` 把缩放也一起限流了** → 僵尸缩放永远看不到（被打回）。
   修复：**拆函数**（缩放每帧、透视 3 帧）。
2. **坑②用 `GlobalPosition` 设位置** → 被移动组件覆盖（同飞贼，用 `SetLogicalGlobalPosition`）。
3. **坑③收集角色靠共享缓存**（`_cacheZombies` / `_zombieCache`）→ 缓存只在"僵尸趣味"功能里刷新 →
   单独开 ESP 时缓存为空 → 什么都不显示。**修复：自己递归收集（`CollectZombies` / `CollectPlants`），不依赖共享缓存时序。**

### 1.26.7 验证方法
真机：透视框/血条出现且跟随；僵尸缩放在运动中也保持（验证坑①是否复发）。

### 1.26.8 扩展指引
**任何依赖角色的功能都不要依赖"共享缓存"** —— 缓存刷新时序与其它开关耦合，是高频事故源。

---

## 1.27 一键通关 / 按模式通关 / 钻石奖杯

### 1.27.1 用户看到什么
- 一键通关：所有关卡（含未解锁的新章节）全部标记完成
- 按模式通关：只通关指定模式（冒险/挑战/小游戏/解谜/生存/IZM2）
- 钻石奖杯：选关界面每关右下角的奖牌按规则变成钻石

### 1.27.2 开关与数据流
动作类（按钮触发）+ 存档写入

### 1.27.3 真实游戏结构（含取证）—— 两套关卡来源必须叠加
| 来源 | 内容 | 取证 |
|---|---|---|
| **存档字典** | `GetLevelDictionary()` —— **只含已游玩/已解锁关卡** | IL |
| **官方注册表** | `Asset/Config/Level/LevelResource.json`（明文，**551 个 SaveKey**：Adventure 150 / Challenge 126 / Survival 84 / Puzzle 80 / MiniGames 71 / IZM2 40） | 资源实测 |
| 通关字段 | 每关 `Key.Finish=1` + `Mower=true` + `Difficult=true` + `Star=3` + `Reward` | IL + save.res |
| 即时胜利（关内） | `TowerDefenseBattleFeatureWave`：`waveStart=true; waveFinal=true; EmitFinal(); awaitSpawn=false`（官方 `_CmdInstantWin` 做法） | IL |
| 钻石奖牌规则 | `DragMenuSelectItemChapter.Init` 显示规则：done→银；done&&mower→金；done&&mower&&finish→**钻石** | IL |

### 1.27.4 实现链路
```
CompleteAllLevels()
  ① 存档字典遍历 → SetLevelFinish(每关)
  ② LoadOfficialLevelKeys()（读 LevelResource.json → 递归收集 SaveKey）
  ③ 对注册表里每个 key → SetLevelFinish
  ④ 保存存档
CompleteMode(mode) → 同上，但用 KeyMatchesMode 过滤
TryInstantWinWave() → 按官方 _CmdInstantWin 驱动 Wave（不依赖 CommandManager.Instance）
AwardDiamondOnWin() → 局内有 TowerDefenseManager 时 CreateCurrency("Diamond", 10)
```

### 1.27.5 代码骨架
```csharp
static void CompleteAllLevels()
{
    // ① 存档里已有的
    foreach (var kv in GetLevelDictionary()) SetLevelFinish(kv.Key, kv.Value);
    // ② 官方注册表（补上未游玩的新关卡）
    foreach (var key in LoadOfficialLevelKeys()) SetLevelFinish(key, null);
    SaveGame();
}

static List<string> LoadOfficialLevelKeys()
{
    var keys = new List<string>();
    using var f = Godot.FileAccess.Open("res://Asset/Config/Level/LevelResource.json", Godot.FileAccess.ModeFlags.Read);
    if (f == null) { LogOnce("通关: 打不开 LevelResource.json"); return keys; }
    var json = new Godot.Json();
    if (json.Parse(f.GetAsText()) != Godot.Error.Ok) return keys;
    CollectSaveKeys(json.Data, keys);          // ★ 必须处理 Variant
    LogOnce("通关: 官方注册表 " + keys.Count + " 个关卡");
    return keys;
}

static void CollectSaveKeys(Godot.Variant v, List<string> outKeys)
{
    // ★ Godot.Json 的 Data 是 Variant：boxed 成 object 后 `is Godot.Collections.Dictionary` 为 false！
    switch (v.VariantType)
    {
        case Godot.Variant.Type.Dictionary:
            var dict = v.AsGodotDictionary();
            foreach (var k in dict.Keys)
            {
                string ks = k.ToString();
                if (ks == "SaveKey") outKeys.Add(dict[k].ToString());
                else CollectSaveKeys(dict[k], outKeys);
            }
            break;
        case Godot.Variant.Type.Array:
            foreach (var item in v.AsGodotArray()) CollectSaveKeys(item, outKeys);
            break;
    }
}
```

### 1.27.6 历史坑
1. **坑①只遍历存档字典 → 新关卡（Chapter9 等）还是锁的**（从没游玩过就不在字典里）。
   修复：**叠加官方注册表**。
2. **坑②`Godot.Json` 的 `Data` 是 `Variant`**：不解包 → `is Godot.Collections.Dictionary` 为 false → **收集到 0 个 key**（本次踩过，第一版没解包）。
3. **坑③手机版残留旧实现**：读 `res://Core/CommandManager/<mode>Init.json`（0.27 已无此文件）→ "打开失败" → 按模式通关彻底失效。修复：手机版同步成"存档字典 + 注册表"新实现。
4. **坑④`InstantWin` 依赖 `CommandManager.Instance`**（MOD 挂载的控制台）→ 控制台没开时点了没反应。
   修复：直接按官方 `_CmdInstantWin` 驱动 Wave（`waveStart/waveFinal/EmitFinal/awaitSpawn`）。
5. **坑⑤奖杯要"钻石"**：只设 `Finish=1` 得到银牌；必须同时 `Mower=true` + `Difficult=true` 才是钻石（按上面的规则表）。
6. **坑⑥改了通关数据但界面没刷新** → 需要重进选关界面（或被游戏的重算覆盖）。

### 1.27.7 验证方法
- 日志：`通关: 官方注册表 N 个关卡`（N 应 ≈551）+ 每关设置成功计数
- 真机：选关界面（含新章节）全部显示完成；右下角奖牌变钻石；关内点"立即胜利"能出胜利结算

### 1.27.8 扩展指引
"批量改存档状态"类功能：**先枚举全部权威清单（存档 + 官方注册表），再统一写，最后触发界面刷新**。

---

## 1.28 卡槽随机与 1 帧 1 刷（SeedBankRandom / SeedBankEveryFrame）

### 1.28.1 用户看到什么
卡槽里的卡不断变成随机植物（可调到"1 帧 1 刷"这种极快速度）；卡槽里甚至能出现僵尸卡。

### 1.28.2 开关与数据流
开关：`ModSettings.SeedBankRandom` / `SeedBankRandomZombie` / `SeedBankEveryFrame`

### 1.28.3 真实游戏结构（含取证）
| 事实 | 值 | 取证 |
|---|---|---|
| 卡槽节点 | `TowerDefenseInGameSeedBank` | IL |
| 卡列表 | `seedBank.packetList` + 每个卡槽项 `Init(config)` | IL |
| 选中状态 | `TowerDefenseInGamePacketShow.select`（属性，true = 玩家正在拖/拿这张卡） | IL |
| 清空卡槽 | `seedBank.DeleteAllPacket()` | IL（`SetupCustomSeedBank` 验证过） |
| 卡 id 来源 | `GetPacketIds(true)`（植物）/ `GetPacketIds(false)`（僵尸） | IL |

### 1.28.4 实现链路
```
AutoRandomizeSeedBank（频率：SeedBankEveryFrame ? 每帧 : 10 帧）
  ① 找 SeedBank（FindSeedBank）
  ② 从卡 id 池随机选 N 个（N = 卡槽数量）
  ③ 逐个替换：跳过 select == true 的卡（★ 玩家正在操作的卡不动）
  ④ 篡改模式下：只替换"不在池里"的卡（不闪不卡；支持僵尸卡 / 固定第一张）
  ⑤ 失败重试：若本次 changed == 0 → 允许下次继续（不要卡住 _sbDone）
```

### 1.28.5 代码骨架
```csharp
static int _sbTimer; static bool _sbDone; static int _lastSbCount = -1;

static void AutoRandomizeSeedBank(Node root)
{
    bool everyFrame = ModSettings.SeedBankEveryFrame;
    if (!everyFrame && ++_sbTimer % 10 != 0) return;
    if (!ModSettings.SeedBankRandom && !ModSettings.TrickEnabled) return;

    var sb = FindSeedBank(root);
    if (sb == null) return;
    var pool = ModSettings.SeedBankRandomZombie || TrickWantsZombie()
             ? GetPacketIds(false) : GetPacketIds(true);
    if (pool.Count == 0) return;

    int changed = 0;
    foreach (var item in EnumeratePacketShows(sb))
    {
        if (ReadBool(item, "select")) continue;         // ★ 玩家正在拿的卡不刷
        string id = PickRandom(pool);
        if (TrickFixedOnly && InPool(id)) continue;     // 篡改模式：只替换池外卡
        if (InitPacketShow(item, id)) changed++;
    }
    if (changed == 0)
    {
        if (!everyFrame && _lastSbCount == 0) { /* 允许重试，不置 _sbDone */ }
        _lastSbCount = 0;
    }
    else { _lastSbCount = changed; _sbDone = true; }
}
```

### 1.28.6 历史坑
1. **坑①种下的植物也会随机**：`OnPacketAboutToPlant` 里每次种卡前把当前卡 Init 成随机植物
   → 种出随机植物 + **破坏种植状态**（"场上有植物就出 bug"的元凶）。修复：**移除该调用**。
2. **坑②刷新太慢/太快**：`_sbRetryTimer < 60`（1 秒）→ 10 帧（0.17 秒）→ 再到"1 帧 1 刷"（用户要求）。
3. **坑③玩家正在拿的卡被刷掉** → 种下的和显示的不一致。修复：跳过 `select == true`。
4. **坑④首次全部失败就永久卡住**（`_sbDone = true`）→ 修复：`changed == 0` 时允许重试（重置 `_sbDone` 和计数）。
5. **坑⑤清空卡槽 ≠ 解锁卡牌**：用户点"清除锁定卡牌"期待清空，实际只解锁（`ForceUnlockPackets` 不解锁卡槽内容）
   → 新增独立的 `ClearSeedBankAll`（调 `DeleteAllPacket()`）并把两个按钮分开。

### 1.28.7 验证方法
真机：卡槽内容不断变化（速度符合开关）；玩家拿卡时该卡不刷；种下的植物与显示一致；
清空卡槽按钮真的把卡槽清空（不是只解锁）。

### 1.28.8 扩展指引
"高频改 UI 状态"类功能：**必须尊重游戏自身的交互状态**（`select` 这类），否则玩家操作会被打断。

---
---

# 附录 2 · 游戏结构大表（AI 写代码前必查）

> **用法**：AI 要访问任何游戏对象前，**先在这张表里搜**。表中每条都给了「类型/位置、语义、正确用法、坑、取证方式」。
> 表中没有的成员 = **未验证** → 必须先用探针（`[F]` / `[P]` / `il:` / `ref:`）或反编译确认，**不许猜**。
> 版本基准：**0.27**（部分条目来自 0.26.1 实测，已注明）。

---

## 2.1 管理器与总入口

#### `TowerDefenseManager.Instance`
- **类型**：静态**属性**（`get_Instance`）
- **语义**：全局塔防管理器单例
- **用法**：`type.GetProperty("Instance", BindingFlags.Static).GetValue(null)`
- **坑**：**主菜单也存在**（非 null 不代表在关卡内）→ 判断"在关卡内"必须再看 `currentControl`
- **取证**：反编译

#### `TowerDefenseManager.currentControl`
- **类型**：**字段**（`TowerDefenseControlNew`）
- **语义**：当前关卡控制器
- **用法**：`FindFieldVal(tdm, "currentControl")`（兜底再试属性）
- **坑**：⚠️ **不存在 `CurrentControl` 属性**。历史上三处功能写了 `GetProperty("CurrentControl", Static)` → 恒 null → 功能整个不执行且不报错
- **取证**：反编译 + 探针 `[F]`

#### `TowerDefenseControlNew.featureDictionary`
- **类型**：public `Dictionary<StringName, TowerDefenseBattleFeature>`
- **语义**：关卡全部 feature（Wave/Mower/Glove/ScreenEffect/SlotMachine/RainMode/ConveyorBelt…）
- **用法**：直接读字典（**避开 `GetFeature` 泛型歧义**）
- **坑**：推荐"按 key 取 + 兜底按类型名遍历"
- **取证**：反编译

#### `TowerDefenseControlNew.GetFeature<T>(StringName)`
- **类型**：泛型方法（**存在非泛型 + 泛型两个重载**）
- **用法**：`FindMethodExact`（跳过 `IsGenericMethodDefinition`）；参数必须是 `Godot.StringName`
- **坑**：⚠️ `GetMethod("GetFeature", types)` 抛 **`AmbiguousMatchException`** → 历史上导致"恢复小推车/停雨/重置脑子"全崩
- **取证**：IL + 独立加载测试

#### `TowerDefenseControlNew.AddFeature(StringName, Dictionary)`
- **语义**：动态添加 feature
- **坑**：**只跑 `Init`**，不会跑 `GameInit` / `GameStart` → 需要手动补（见 1.8 手套）
- **取证**：IL

#### `TowerDefenseControlNew._battleGraphReady`
- **类型**：字段（bool）
- **语义**：`false` = feature 尚未创建的窗口期（control 创建到 OldLevelInit 之间，**只有 1~2 帧**）
- **用法**：改 `levelConfig.featureData` 要在窗口内；**用每帧检测，不要用 `% 5` 定时**（会错过窗口）
- **取证**：IL + 真机

#### `TowerDefenseControlNew.AddUIToTopPropContainer(Node)`
- **语义**：把 UI 节点加到顶层道具容器（手套管理器这么进去）
- **取证**：IL

#### `SceneManager/MainMenu._PhysicsProcess`
- **语义**：**MOD 的每帧注入点**（`FrameDriver.OnFrame` 挂在这里）
- **坑**：这里抛异常会中断整个主循环 → 必须全包 try/catch
- **取证**：patcher 注入规则

#### `SceneManager.Instance.ChangeScene(string, bool)`
- **语义**：切场景（`"TowerDefense"` / `"LevelChoose"`）
- **取证**：IL

#### `Global.Instance.enterLevelMode`
- **语义**：设 `"LoadLevel"` 表示"加载指定关卡"（官方 CommandManager 也这么用）
- **取证**：IL

---

## 2.2 关卡 / 地图 / 波次

#### `levelConfig.featureData`
- **类型**：`Dictionary`（关卡配置里）
- **语义**：**feature 注入的总入口**（加 `"Glove"` / `"RainMode"` / `"Fog"` / `"PacketBank"` / `"SeedBank"`）
- **坑**：`NewLevelInit` 直接用 `featureData` 重建，**不走 getter** → 只注 getter 对新关卡无效
- **取证**：IL

#### `TowerDefenseManager.GetMapFeature()`
- **语义**：取地图 feature
- **用法**：`mapFeature.stripeRow = -1` → **整体跳过红线种植限制**（比逐个判断可靠）
- **取证**：IL

#### `TowerDefenseBattleFeatureWarningLine`
- **类型**：`TowerDefenseBattleComponentBase : Resource`
- **语义**：警戒线配置
- **坑**：⚠️ **它不是 Node，不在场景树里** → 遍历树找它**永远找不到**。
  正确做法：找 `WarningLine` 节点（Node2D）→ 读其 public `feature` 字段拿 Resource → 设 `_triggered = true`
- **取证**：IL + 真机

#### `TowerDefenseBattleFeatureWave`
- **语义**：波次 feature（刷怪倍数 / 即时胜利都在这）
- **成员**：`.config`（`TowerDefenseLevelWaveManagerConfig`）、`waveStart` / `waveFinal` / `awaitSpawn` / `EmitFinal()`
- **坑**：官方 DebugCommands 用的是 `TowerDefenseBattleFeatureWave::Instance`（**static 字段**），另一条路是 `featureDictionary["Wave"]`
- **取证**：IL

#### `TowerDefenseLevelWaveManagerConfig.wave` → `TowerDefenseLevelWaveConfig.spawn` → `TowerDefenseLevelSpawnConfig.num`
- **类型**：`wave` 是**属性** `get_wave`（`Array<TowerDefenseLevelWaveConfig>`）；`spawn` 是**小写字段**；`num` 是**小写 Int32**
- **坑**：⚠️ 这三个的大小写/类型**全部踩过坑**（把 `spawn` 写成 `Spawn`、`num` 当 double → 刷怪倍数 0 生效）
- **取证**：探针 `[F]` 实测

#### `TowerDefenseBattleFeatureScreenEffect`
- **成员**：`HasScreenEffect(name)` / `AddScreenEffect(name)` / `DeleteScreenEffect(name)`，key = `"Rain"` / `"Storm"`
- **坑**：① `GetFeature` 必须传 `StringName`；② 节点名是 `ScreenEffectRain` / `ScreenEffectStorm`（可遍历树兜底删除）
- **取证**：IL

#### `TowerDefenseBattleFeatureSlotMachine`
- **成员**：`_slotItems`（私有 `List<SlotItem>`）、`_slotItemLookup`
- **产出元素**：`SlotItem`（嵌套私有类）：`Key` / `DisplayKey` / `Type`（私有枚举 `SlotRewardType`，`Packet = 0`）/ `Amount` / `Weight`
- **构造私有嵌套类**：`Activator.CreateInstance(elemType, true)`
- **取证**：IL

#### `TowerDefenseBattleFeatureRainMode`
- **成员**：`_packetList`（`List<TowerDefenseRainModePacketConfig>`，字段 name / weight）
- **坑**：`config.packetList` 是 Godot 数组（**别直接改**），改运行时的 `_packetList`
- **取证**：IL

#### `TowerDefenseBattleFeatureConveyorBelt`
- **成员**：`packetList`（**Godot 数组**）、`packetPrioritySpawnList`
- **坑**：⚠️ Godot 数组那套"Clear+Add"改法**对它无效** → 改呈现端 `conveyorBeltManager.GetPacketChildren(isMobileUI)` 的 `Init` 覆盖
- **取证**：真机验证

#### `TowerDefenseBattleFeatureMower`
- **成员**：`mowerLine`（`Array<TowerDefenseMower>`）、`CreateMower(int row, int col)`
- **坑**：① `Godot.Collections.Array<T>` 可能**不实现非泛型 `IList`** → `is IList` 判断失败；② `CreateMower` 返回 null = 失败，**必须检查**
- **取证**：真机 + IL

#### `Asset/Config/Level/LevelResource.json`
- **语义**：**官方关卡注册表**（明文），551 个 SaveKey（Adventure 150 / Challenge 126 / Survival 84 / Puzzle 80 / MiniGames 71 / IZM2 40）
- **用途**：一键通关必须叠加它（否则新章节不在存档字典里）
- **取证**：资源实测 + 计数

---

## 2.3 卡牌 / 种植 / 卡槽

#### `TowerDefenseInGamePacketShow.set_coldDownOpen`
- **语义**：卡牌冷却开关（**无冷却的注入点**）
- **坑**：别注到 `TowerDefensePacketConfig`（那是配置类，注了无效）
- **取证**：IL

#### `TowerDefenseInGamePacketShow.Plant(Vector2I, bool, bool)`
- **语义**：种植入口（注入点在**方法开头**）
- **坑**：反射调用要填 3 个参数（`FillArgs` 只填类型默认值，不会读 C# 默认参数值）
- **取证**：IL

#### `TowerDefenseInGamePacketShow.select`
- **类型**：属性（bool）
- **语义**：玩家正在拖/拿这张卡 → **随机卡槽必须跳过它**
- **取证**：IL

#### `TowerDefenseInGamePacketShow._lock`
- **类型**：字段（bool）
- **语义**：卡牌锁（强制选卡改它，**别动 `_cachedRuntimeAvailability`**）
- **取证**：真机（改后者会导致"卡亮着但拿不起"）

#### `TowerDefenseInGamePacketShow.config.saveKey`
- **语义**：**卡 id 的真源**（三叶草/篡改/皮肤都靠它判断）
- **坑**：⚠️ 该类**没有 `packetName` / `packetId` 字段**（那是 0.25.5 结构）→ 从它找会恒 null
- **取证**：探针 `[F]`

#### `TowerDefensePacketConfig` 的返回类方法与属性
- `GetCost(bool)` / `GetCostBeforeModifiers()` / `GetWavePointCost()` / `GetCostRise` —— **价格有 3 个入口**（金卡/波点卡走后两个）
- `Unlock()` / `IsUnlocked()` —— 解锁判定（**图鉴也读它** → 必须加图鉴旁路）
- `saveKey`（卡 id）/ `name`（**翻译 key**，不是中文）/ `characterConfig` / `overrideCost` / `overridePacketCooldown` / `overrideHypnoses`
- **取证**：IL

#### `TowerDefensePlantConfig.cost` / `packetCooldown`
- **语义**：override 未设时的真实价格/冷却
- **取证**：IL

#### `TowerDefenseInGameSeedBank`
- **成员**：`packetList`、每项 `Init(config)`、`DeleteAllPacket()`（清空卡槽）
- **坑**：清空卡槽 ≠ 解锁卡牌（两个不同功能，历史上用户混淆）
- **取证**：IL

#### `TowerDefenseCellInstance.CanPacketPlant` / `Shovel`
- **语义**：格子的"能否种植" / "铲除核心"（注入点）
- **取证**：IL

#### `PacketPickControl.ProcessPacketPick`
- **语义**：种植限制判定（红线限制在这里）
- **成员**：外层 `mapFeature.stripeRow != -1` 才生效；`RegisterTool`（手套注册）
- **取证**：IL

---

## 2.4 植物

#### `FireComponent.fireInterval`
- **类型**：**Single**（还有 `fireIntervalBase` / `fireIntervalOffset` / `timer` / `parent`）
- **语义**：**真正的射击间隔**（射击后 `timer` 重置为它）
- **坑**：⚠️ 植物节点上有个**同名 `fireInterval`（Double）**，那个**不是射速**（只被序列化）
- **取证**：反编译 + IL 实测

#### `FireComponent.IdleProcessing` / `PhysicsProcessValidated`
- **语义**：植物攻击判定的注入点（禁攻）
- **取证**：IL

#### `TowerDefensePlant*CobCannon.restTime` / `_restTime`
- **类型**：Double
- **语义**：炮类冷却（**在植物节点自身**，不是 `_cannonComponent` 字段——该字段不存在）
- **取证**：IL

#### `fireNum`
- **语义**：部分炮类的一次发射数（Cornpult / CabbageCob / Melonpult / PeaShooter / PuffShroom / Cactus 等）
- **坑**：`PotatoCobCannon`（土豆炮）**没有**该属性 → 静默跳过
- **取证**：IL

#### `TowerDefensePlantChomper._chomperComponent` / `chomperComponent` → `ChomperComponent`
- **成员**：`chewTimer` / `currentChewTime` / `chewTime`（Single）、`ChewProcessing`
- **语义**：大嘴花咀嚼（秒吞咽 = `chewTimer = currentChewTime + 1`）
- **取证**：IL

#### `TowerDefenseCharacter.instance`
- **类型**：字段（运行时实例数据）
- **成员**：`instance.hitpoints`（真实血量）、`instance.hitpointScale`（血量倍率）
- **坑**：⚠️ **真实玩法状态在这里**，节点上的裸字段改了没用（实体属性页曾经"改了没反应"的根因）
- **取证**：反编译 `TowerDefenseCharacter`

#### `TowerDefenseCharacter.SetLogicalGlobalPosition(Vector2)`
- **语义**：设置"逻辑位置"（游戏拖拽植物/击退都用它）
- **坑**：⚠️ 直接写 `GlobalPosition` 会被移动组件每帧覆盖（飞贼/吸附/透视都用这个坑踩过）
- **取证**：真机

#### `TowerDefenseCharacter.transformPoint.Scale`
- **语义**：角色缩放的载体（实体属性页"大小"应路由到这里）
- **取证**：IL

#### `TowerDefenseCharacter.Hypnoses` / `InvokeHypnoses()`
- **语义**：**游戏原生魅惑机制（buff）**
- **坑**：⚠️ 手动反转 `camp` / `Scale.X` **无效**（移动组件不认 camp）
- **取证**：真机

#### 抽卡/礼盒植物族（均有 `Explode()`）
- `TowerDefensePlantPresentBox` / `PresentBoxGreen` / `BYWZ`(备用物质) / `Upgradebean`(升级豆，2 张) / `GardenSet`(花园套装) / `LampShroom`(路灯菇，左右各 1) / `MagicBean`(魔法豆) / `LuckyBlover`(幸运三叶草 → **走 `AddPacket` 进卡槽**)
- **定位手法**：`ref:Explode` 列调用方 → 一眼看出哪些植物产卡
- **取证**：探针 `ref:`

#### `SpawnPacket(cfg, pos, aliveTime, isFall, useCost, useRandf, velocityOverride)`
- **坑**：⚠️ `FillArgs` 只填**类型默认值**，不会读 C# 默认参数（`useRandf` 默认 true，不显式传就变 false）
- **取证**：IL

#### `TowerDefensePlantUmbrellaleaf.CanBlock()` / `canBlock`
- **语义**：保护伞拦截（`canBlock && z <= 50`）
- **取证**：IL

#### `TowerDefenseItemSpikeball`
- **类型**：`TowerDefenseItem`（**道具，不是植物**）
- **语义**：用 `ComponentAttack()` 近战 → **不走 BulletField** → 随机子弹对它天然无效
- **取证**：IL

---

## 2.5 僵尸

#### `TowerDefenseZombie.attackComponent`
- **类型**：字段
- **成员**：`attackInterval` / `attackIntervalBase` / `timer`（均 Double）
- **语义**：僵尸攻击（`Refresh()` = `timer = attackInterval + RandRange(...)`；`BatchUpdateValidated` = `timer -= delta*...`）
- **坑**：改 `attackInterval` **实测不生效** → 用"每帧扣 `timer`"方案（注入 `BatchUpdateValidated`）
- **取证**：IL + 真机

#### `AttackComponent.CanAttack` / `BatchUpdateValidated`
- **语义**：禁攻 / 加速的注入点
- **坑**：**计时期间每帧都触发**（旧注释说"不常触发"是错的）
- **取证**：IL

#### 僵尸移动相关（禁移动必须**逐个注入**）
- `TowerDefenseZombie.BatchUpdate` / `BatchUpdateValidated`
- `GroundMoveComponent.BatchUpdate` / `BatchUpdateValidated`
- `TowerDefenseZombieBalloon.FlyProcessing`（气球）
- `CharacterMoveComponent.PhysicsProcess`（巨人/车）
- **坑**：只注一个 → 气球还在飞、巨人还在走
- **取证**：IL

#### `TowerDefenseZombieBoss.HeadExitedEntered` / `ImpThrowerComponent.SpawnImp`
- **语义**：僵王低头 / 巨人投小鬼（"派生行为"要单独拦）
- **取证**：IL

#### `TowerDefenseZombieBungi`（飞贼）完整状态机
- 状态：`zombie.bungi.drop` → `zombie.bungi.grab` → `zombie.bungi.rise` → `Destroy`
- 事件：`IdleProcessing` 发 `"ToGrab"`；`AnimeCompleted("Grab")` 发 `"ToRise"`（**无 `ToDrop`**）
- 字段：`waitGrab` / `waitTimer` / `gridPos` / `isGround` / `hasPlant` / `_bungeeTarget` / `z`（**高度是 z 不是 y**）
- **坑**：不设 `gridPos` → 所有飞贼抓同一格 → 全部挤一起
- **取证**：IL 实测（`inspect_bungi` 工具）

#### `TowerDefenseZombieJackbox`
- 成员：`AnimeCompleted("Bomb")` → `CreateEffect()` + `Destroy()`
- 语义：小丑爆炸（秒炸 = 瞬移 + `CreateEffect` + `QueueFree`）
- **取证**：IL

#### `SleepComponent.CanSleep`
- **语义**：蘑菇白天睡觉（禁睡注入点）
- **取证**：IL

#### 僵尸缓存字段（PC / 手机不同名）
- PC：`_cacheZombies`；手机：`_zombieCache`
- **坑**：⚠️ 缓存**只在"僵尸趣味"功能里刷新** → 单独开别的功能时缓存是空的
- **正确做法**：自己递归收集（`CollectZombies` / `CollectPlants`），不依赖共享缓存
- **取证**：真机

---

## 2.6 子弹

#### `BulletField`
- 成员：`_data`（`BulletData[]`）、`_activeCount`(Int32)、`_freeList` / `_freeCount`
- **坑**：⚠️ **没有 `_activeIndices`**（旧代码依赖它 → null → 遍历 0 颗）
- **取证**：探针 `[F]` + IL

#### `BulletData`（**struct**）
- 成员：`active` / `over` / `pos` / `vel` / `speed` / `config`(TowerDefenseProjectileConfig) / `trackOpen`(bool) / `trackSearchInterval`(int) / `damage` / `fireCharacter`
- **坑**：⚠️ struct → 改完必须 `arr.SetValue(bd, i)` 写回，否则改的是副本
- **取证**：IL

#### `BulletField.TryChangeBulletDataInPlace(int, TowerDefenseProjectileConfig)`
- **类型**：**internal**（`GetMethod` 默认搜不到 → null）
- 返回：`BulletChangeInPlaceResult`（**枚举状态码**：-1 = 失败 / 1 = Changed / 2 = E_CHANGE_RENDER_UNSUPPORTED）
- **坑**：⚠️ 返回码**不是子弹下标**；调用后必须**读回校验** `_data[idx].config`
- **取证**：IL + 独立加载测试

#### `BulletField.ChangeBulletData(int, cfg, TowerDefenseCharacter, Nullable<double>)`
- **坑**：⚠️ 传 **null character 永远返回 -1**（IL：`ldarg.2 brtrue` 后直接 return）
- 正确：从 `_data[idx].fireCharacter` 读真实 character
- **取证**：IL

#### `UpdateSimulation` / `ProcessTrackData`
- **语义**：追踪生效点（**只读 `trackOpen`，不重置**）
- **取证**：IL

---

## 2.7 存档

#### 真存档路径
- **`%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\Csharp\save.res`**
- **坑**：⚠️ 根目录下也有个同名 `save.res`，那是**旧版**；`GameSaveManager` 的 PATH 是 `user://Csharp/save.res` → **改错文件 = 白干**
- **取证**：真机 + 文件对比

#### 存档格式（RSRC v6）
- 头：`RSRC` + big_endian(u32) + use_real64(u32) + major + minor + format(=6) + type(string) + importmd_ofs(u64) + flags(u32) + [uid] + reserved×11 + string_table + ext_resources + internal_resources + 数据区
- Variant 类型码**自编**：NIL=1 BOOL=2 INT=3 FLOAT=4 STRING=5 COLOR=20 NODE_PATH=22 RID=23 OBJECT=24 INPUT_EVENT=25 DICTIONARY=26 ARRAY=30 PACKED_*=31+
- 字符串长度**含结尾 null**
- 工具：`rsrc.py`（读写）；验证：round-trip 解析
- **取证**：Godot 源码 + 实测

#### `GameSaveManager` 关键成员
- `SetKeyValue(name, Godot.Variant)` —— ⚠️ `new Variant(long)` 不存在 → `Godot.Variant.From(long)`
- `GetKeyValue(name)` / `SetFeatureValue(name, Variant)` / `GetFeatureValue(name)`
- **坑**：部分版本 `SetKeyValue` 只改内存 → 需要额外保存调用
- **取证**：IL

#### 存档字典结构
| 字典 | 项结构 |
|---|---|
| `TowerDefensePacket`（638 项） | `{Unlock:bool, Love:bool, Key:{Custom:'皮肤key'}}` |
| `Feature`（152） | bool |
| `Tutorial`（48） | bool |
| `Level`（611） | `{Normal, Difficult, Ultimate(bool), Mower, Reward, Key:{Like, Played, Finish, Play}}` |
| `Key`（48） | `CoinNum` / `CrystalNum`(int) / `UnlimitedFire` / `AdventureChapter*Index` |
- 默认结构定义在 pck 明文 `Asset/Config/Save/*.json`（TowerDefensePacketInit / FeatureInit / LevelInit / KeyInit / TutorialInit / ConfigInit）
- **取证**：save.res 实测 + Init JSON

#### `modsettings.txt`
- PC：`D:\杂交版关卡\mod\modsettings.txt`；手机：`user://modsettings.txt`
- **坑**：外置面板 `/set` 后要**主动 `ModSettings.Save()`**，否则重启丢失（历史事故）
- **取证**：真机

---

## 2.8 枚举与资源清单

#### `ObjectManagerConfig.OBJECT`
- 值：`COIN` / `COIN_SILVER` / `COIN_GOLD` / `COIN_DIAMOND` / `COIN_LUCKY_BAG`(福袋) / `COIN_TQ` / `COIN_YB1` / `COIN_YB2` / `COIN_GOLD_SHARD`
- **坑**：**没有"钱袋/奖杯"** → 本体在 Award 系统：`TowerDefenseManager.get_TOWER_DEFENSE_AWARD_TROPHY()` / `..._PURSE()` / `..._PACKET()` / `..._COLLECTABLE()`（static，PackedScene）+ `CreateAwardFromScene(...)`
- **取证**：探针 `enum:`

#### 资源路径速查
| 用途 | 路径 |
|---|---|
| 地图背景 | `Asset/Texture/TowerDefense/Background/TowerDefenseMap/<Map>/<Map>.jpg`（如 `Frontlawn/Frontlawn.jpg`、`Backyard/PoolBase.jpg`） |
| 卡牌框 | `Asset/Texture/TowerDefense/Packet/PC/PacketNormal.png` |
| 翻译表 | `Asset/Translate/Translate.zh.translation`（OptimizedTranslation 哈希压缩，离线难解） |
| 关卡注册表 | `Asset/Config/Level/LevelResource.json` |
| 存档默认结构 | `Asset/Config/Save/*.json` |
| 官方作弊面板场景 | `res://Core/CommandManager/CommandManager.tscn` |
| 类名缓存 | `global_script_class_cache.cfg`（明文，可查全部类名） |

#### 取中文名
```csharp
string key = GetPacketDisplayName(id);                       // 拿到的是翻译 key（如 TOWERDEFENSE_PLANT_PEASHOOTER_NAME）
string zh  = Godot.TranslationServer.Translate(new StringName(key));   // 才是真中文
```
- **取证**：IL + 真机

---
---

# 附录 3 · 任务配方（照抄即用）

> **用法**：用户给你一句"帮我加个 XX"，你先在这张表里找最接近的配方，**按配方的步骤走**，不要自己发明流程。
> 每个配方 = 场景 / 改动文件 / 步骤 / 代码骨架 / 验证 / 常见失败。
> 配方编号固定，可以在沟通里直接引用（如"按配方 3 做"）。

---

## 配方 1 · 加一个布尔开关（最基础，先学这个）

**场景**：用户说"加一个 XX 功能的开关"。
**改动文件**：`ModSettings.cs`（PC + 手机各一份）→ `GameCheats.cs`（消费点）→ `ModUI.cs`（手机 UI）/ 外置面板（PC UI）。

**步骤**
1. `ModSettings.cs` 加字段：`public static bool Xxx = false;`
2. `Save()` 里加：`sw.WriteLine("Xxx=" + Xxx);`
3. `Load()` 里加：`Xxx = ReadBool(map, "Xxx", false);`
4. `AnyModActive()` 里加 `|| Xxx`（**漏了这一步 = 功能完全不执行**）
5. `GameCheats.OnFrame` 里加消费点（按配方 17/18 选唤醒方式）
6. 手机 `ModUI.cs` 加 `AddSwitch("XX功能", v => ModSettings.Xxx = v);`
7. 外置面板 `MainWindow.xaml.cs` 的 `Feats` 表加一项（否则用户看不见）

```csharp
// ModSettings.cs
public static bool Xxx = false;
// Save():   sb.AppendLine("Xxx=" + Xxx);
// Load():   Xxx = ReadBool(dict, "Xxx", false);

// GameCheats.cs
static int _xxxTimer;
static void ApplyXxx(Node root)
{
    if (!ModSettings.Xxx) return;
    if (++_xxxTimer % 30 != 0) return;          // 按需降频
    try { /* 实际逻辑 */ }
    catch (Exception ex) { LogOnce("Xxx异常: " + ex.Message); }
}

// OnFrame 里（放在合适的功能段）
if (ModSettings.Xxx) ApplyXxx(root);
```

**验证**：编译 0 error → 注入 → UTF-16 搜 `ApplyXxx` → 真机开关生效 → **重启游戏仍保持**（验证持久化）。

**常见失败**
| 现象 | 原因 |
|---|---|
| 开了没用，代码看起来对 | 忘了加 `AnyModActive()` |
| 重启后变回默认 | `Save()` 或 `Load()` 漏了 |
| 手机能开 PC 不能 | 只改了一份 `ModSettings` |
| 面板看不到开关 | `Feats` 表没加 |

---

## 配方 2 · 加一个"数值型"开关（滑块 1~50）

**场景**："攻速可以调到 50 倍"。
**与配方 1 的区别**：数值要 **夹取范围**、要支持**恢复原值**。

**步骤**
1. `ModSettings.cs`：`public static float XxxMult = 1.0f;`
2. Save/Load：`XxxMult = ReadFloat(map, "XxxMult", 1.0f);` → 读进来后 **Clamp**
3. 消费点：**缓存原值**（按 `GetInstanceId`），按倍率写；`mult == 1` 时**恢复原值**
4. UI：外置面板用 `Number` 项；手机用 `AddSlider(min, max, step, ...)`

```csharp
static readonly Dictionary<ulong, double> _xxxOrig = new();
static void ApplyXxxMult(Node n)
{
    double m = ModSettings.XxxMult;
    if (m < 0.5) m = 0.5; if (m > 50) m = 50;            // ★ 夹取
    ulong id = n.GetInstanceId();
    if (m == 1.0)
    {
        if (_xxxOrig.TryGetValue(id, out var o)) { WriteTarget(n, o); _xxxOrig.Remove(id); }   // ★ 恢复
        return;
    }
    if (!_xxxOrig.ContainsKey(id)) _xxxOrig[id] = ReadTarget(n);
    WriteTarget(n, _xxxOrig[id] / m);
}
```

**验证**：调大生效、调回 1 恢复原速、**新生成的对象也生效**、重启保持。

**常见失败**：① 关闭不恢复（`mult==1` 直接 return）；② 多次除导致累积误差（必须用缓存原值）；③ 不夹取 → 用户输入 0 导致除零/卡死。

---

## 配方 3 · 加一个"下拉选择"开关（choice）

**场景**："颜色律动要 16 种样式可选"。
**要点**：值 → **中文标签**的映射要集中定义（面板与 mod 各一份会不同步）。

**步骤**
1. `ModSettings.cs`：`public static int XxxStyle = 0;`
2. 定义标签：`public static readonly string[] XxxStyleNames = { "彩虹", "红金", ... };`
3. 消费点 `switch (XxxStyle)`；**每个 case 都必须给所有局部变量赋值**（否则 `CS0165`）
4. 外置面板 `Feats` 用 `choice` 类型；手机 `AddOption(...)` / 按钮轮换

**验证**：逐个切换都能看出差异；越界值（手改配置文件写 99）不崩（夹取或 default 分支）。

---

## 配方 4 · 加一个"即时按钮"（一次性动作，不是开关）

**场景**："加一个一键通关按钮"。
**要点**：动作是**点一下执行一次**，不持久化；要有**执行前置检查**与**结果日志**。

**步骤**
1. `GameCheats.cs` 写 `public static void DoXxxOnce()`（方法内 try/catch + 日志）
2. 手机：`ModUI.cs` 加按钮，`Pressed += () => GameCheats.DoXxxOnce();`
3. 外置面板：`Feats` 表加 `action` 项，`OnAction` 里映射到对应 HTTP 命令
4. HTTP：`RemoteServer` 加一个命令路由（如 `/action?name=xxx`）

**验证**：点一下执行一次（不是每帧执行）；重复点不叠加副作用；**在错误场景（如主菜单）点击要有保护**（见配方 17 的前置检查）。

**常见失败**：把按钮做成"开关"（`Toggled` 事件）→ 点一下执行了、再点一下又执行（用户困惑）。

---

## 配方 5 · 加一个"总开关 + 子开关"的功能组

**场景**："篡改功能要有总开关，下面分盲盒/种子雨/传送带"。
**⚠️ 语义约定（本项目强制）**：**总开关 = 全开**（总开关打开时所有子功能都生效，子开关只用于 UI 展示"当前会动哪几类"）。

```csharp
static bool GroupOn     => ModSettings.TrickEnabled;
static bool SubBoxOn    => ModSettings.TrickEnabled;      // ★ 不含子开关，否则"只开总开关 = 什么都不做"
static bool SubRainOn   => ModSettings.TrickEnabled;
// 错误示范（历史事故）：ModSettings.TrickEnabled && ModSettings.TrickBox
```

**验证**：**只开总开关** → 全部子功能生效；关总开关 → 全部停止并回滚。

---

## 配方 6 · 让开关出现在外置面板（Feats 表）

**改动文件**：`mod/tools/外置修改器/wpf/MainWindow.xaml.cs`

```csharp
// Feats 字典：分类 key -> 功能项数组
["xxx"] = new[] {
    new Feat("Xxx",       "XX功能",      "switch"),     // 布尔
    new Feat("XxxMult",   "XX倍率",      "number", 1, 50, "x"),
    new Feat("XxxStyle",  "XX样式",      "choice", 0, XxxStyleNames),
},
```
**要点**：`Feat` 的第一个参数 = **`ModSettings` 里的字段名**（面板靠反射 `/get` `/set` 直接读写，**不用改 RemoteServer**）。
**验证**：面板能看到、点击后游戏内生效、切分类再回来状态仍正确（依赖 `SyncAllAsync`）。

---

## 配方 7 · 让开关出现在手机版游戏内面板

**改动文件**：`androidmod/PvzheAndroid/ModUI.cs`

```csharp
// MakePage("XX") 里
AddSwitch(_content, "XX功能", ModSettings.Xxx, v => { ModSettings.Xxx = v; ModSettings.Save(); });
```
**要点**：手机**没有外置面板** → 手机必须自己做 UI；`Save()` 要立即调用（否则重启丢失）。
**验证**：手机真机开关生效且重启保持。

---

## 配方 8 · 开关持久化的正确姿势

```csharp
// ModSettings.Save()
var sb = new System.Text.StringBuilder();          // ⚠️ 只用 Append(string)，不要 Append(数值)
sb.Append("Xxx=" + Xxx + "\n");                    // ★ 用 + 拼接
WriteTextGodot(Path, sb.ToString());               // ★ 用 Godot FileAccess，不用 System.IO.File

// ModSettings.Load()
var dict = new Dictionary<string,string>();
foreach (var line in ReadLinesGodot(Path)) { var i = line.IndexOf('='); if (i > 0) dict[line.Substring(0,i)] = line.Substring(i+1); }
Xxx = ReadBool(dict, "Xxx", false);
```
**坑**：① `StringBuilder.Append(数值重载)` 被 AOT 裁掉；② `System.IO.File` 被裁掉；③ 缺字段必须**用默认值**（兼容旧文件）。

---

## 配方 9 · 加一条"方法开头注入调用"规则（最常用）

**改动文件**：`mod/patcher/Program.cs`（PC）+ `androidmod/android_patcher/Program.cs`（手机）

```csharp
// patcher：在目标方法体开头插入  ldarg.0 ; call GameCheats.Hook
static void InjectXxx(ModuleDefinition main, ModuleDefinition mod)   // 手机版多一个 mod 参数
{
    var t = main.GetType("TowerDefenseInGamePacketShow");
    var m = t.Methods.FirstOrDefault(x => x.Name == "Plant");
    if (m == null || !m.HasBody) { Console.WriteLine("警告: 找不到 Plant"); return; }

    var hook = main.ImportReference(mod.GetType("GameCheats").GetMethod("OnPacketAboutToPlant"));
    var il = m.Body.GetILProcessor();
    var first = m.Body.Instructions[0];
    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));      // this
    il.InsertBefore(first, il.Create(OpCodes.Call, hook));   // static 方法调用
    Console.WriteLine("已注入 Plant 开头");
}
```
**要点**：手机版用 `main.ImportReference(...)` 引用独立 DLL 里的方法。
**验证**：patcher 打印"已注入"；`ScanBadIL bad=0`；真机功能生效。

---

## 配方 10 · 加一条"返回值强制"规则（ReturnZero / ReturnTrue）

```csharp
static void InjectReturnZeroIf(ModuleDefinition main, ModuleDefinition mod, string typeName, string methodName, string hookName)
{
    var m = main.GetType(typeName)?.Methods.FirstOrDefault(x => x.Name == methodName);
    if (m == null || !m.HasBody) { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName); return; }
    var hook = main.ImportReference(mod.GetType("GameCheats").GetMethod(hookName));
    var il = m.Body.GetILProcessor();
    var first = m.Body.Instructions[0];
    // if (!hook()) goto 原逻辑; return 0;
    var cont = il.Create(OpCodes.Nop);
    il.InsertBefore(first, il.Create(OpCodes.Call, hook));      // 压栈 bool
    il.InsertBefore(first, il.Create(OpCodes.Brfalse, cont));   // false → 继续原逻辑
    il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));        // ⚠️ 返回类型要匹配（int/long/float/bool）
    il.InsertBefore(first, il.Create(OpCodes.Ret));
    il.InsertBefore(first, cont);
}
```
**⚠️ 关键**：返回类型必须匹配！返回 `long` 要 `Ldc_I4_0 + Conv_I8`；返回 `bool` 要 `Ldc_I4_0`；
返回 `float` 要 `Ldc_R4 0`。**类型不匹配 = 运行时 InvalidProgramException。**
**验证**：开关开 → 方法返回 0；开关关 → 原行为（这条必须双向验证）。

---

## 配方 11 · 加一条"每个 ret 前注入"规则

```csharp
// 场景：需要在方法返回前做收尾（如 Postfix 统计、清理）
foreach (var ret in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
{
    var il = m.Body.GetILProcessor();
    il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
    il.InsertBefore(ret, il.Create(OpCodes.Call, hook));
}
```
**坑**：**漏一个分支就漏一半功能**（多返回值方法很常见）。必须遍历**全部** `ret`。

---

## 配方 12 · 加一条"判定改写"规则（brfalse / brtrue）

**场景**："禁止攻击" = 让 `CanAttack` 永远返回 false。
**要点**：优先用**返回值强制**（配方 10），只有无法在开头插入时才改分支。
**坑**：改分支容易破坏栈平衡 → 改完必须 `ScanBadIL` + 真机验证。

---

## 配方 13 · 加一个"产出类行为"接管（Explode 类）

**场景**："让礼盒按我的卡池产出"。
**步骤**：patcher 注入每个目标类的 `Explode()` 开头 → hook 返回 `bool`（true = 已接管，false = 走原逻辑）。

```csharp
// hook 里
public static bool OnPresentBoxExplode(object box)
{
    if (!TrickBoxOn || PoolCount == 0) return false;
    string id = PickFromPool();
    if (!Produce(id, GetPos(box))) return false;     // ★ 先产出成功
    RemoveCharacterAt(GetGridPos(box));              // 再清理
    if (box is Godot.Node n0) n0.QueueFree();
    return true;                                     // 已接管 → 原逻辑会被跳过
}
```
**要点**：**顺序不能反**（旧实现先移除后产出 → 产出失败就白吃一个礼盒）。
**注入模式**：`if (hook(this)) return;` 插在方法最开头。

---

## 配方 14 · 注入到属性 setter（`set_` 方法）

```csharp
var setter = t.Methods.FirstOrDefault(x => x.Name == "set_coldDownOpen");
```
**要点**：属性 setter 在 IL 里是 `set_Xxx` 方法（**public** 才能直接找到；`private` 要 `IsPublic==false` 也匹配）。
**坑**：**不要整个跳过 setter**（它可能兼做状态登记）→ 只改判定/入参，或跳过前后做补偿。

---

## 配方 15 · 注入顺序陷阱（`BulletField.Spawn` 事故复盘）

**规则**：
1. **判定类指令（`call` 取开关 + `brfalse`）必须最先插入**（执行顺序最前）；
2. **`skip` 标签必须最后插入**（在注入体之后）。
**反例**：顺序反了 → 开关关闭时进入**死循环** → 植物一攻击就**冻结全局**。
**验证**：注入后必须在**开关关闭状态**下测试（这才是暴露顺序错误的场景）。

---

## 配方 16 · 用 `ScanBadIL` 自证注入没坏

```powershell
# patcher 内置：写回前扫描所有方法体，找 Call/Callvirt/Newobj/Ldftn 操作数非 MethodReference 的坏指令
# 期望输出：ScanBadIL bad=0
```
**为什么必须有**：Cecil 写坏 IL 时 **编译期与写回都不会报错**，只在游戏运行到该处崩（表现为闪退/冻结）。
**配套**：任何"改了 IL"的改动，**都必须**看这一行输出。

---

## 配方 17 · 改一个"游戏数值"（心跳式，每帧/降频写）

**场景**："无限阳光 / 无限金币 / 刷怪倍数"。
```csharp
static void ApplyXxx(Node root)
{
    var ctrl = GetCurrentControl();                     // ★ 先确认在关卡内
    if (ctrl == null) return;
    var obj = FindXxxHolder(root);
    if (obj == null) { LogOnce("Xxx: 未找到持有者"); return; }
    double cur = ReadDouble(obj, "value");
    if (cur < TARGET) WriteDouble(obj, "value", TARGET);   // ★ 只在需要时写，别与游戏打架
}
```
**要点**：① 先做"在关卡内"检查；② 只在值不对时写（避免与游戏每帧重算打架）；③ 降频。

---

## 配方 18 · 改一个"持久数值"（带原值缓存与恢复）

见 **配方 2**（`_xxxOrig` 缓存 + `mult==1` 恢复 + 调用条件加 `|| _xxxOrig.Count > 0`）。

---

## 配方 19 · 改一个"配置对象数组"（倍率类）

**场景**："刷怪倍数"。
```csharp
if (!ReferenceEquals(cfg, _lastCfg)) { _orig.Clear(); _lastCfg = cfg; }   // ★ 换关卡必须重置缓存
string key = WaveKey(w) + "|" + SpawnKey(s);
int orig = _orig.TryGetValue(key, out var o) ? o : ReadInt(s, "num");
_orig[key] = orig;
WriteInt(s, "num", orig * mult);
```
**坑**：① 不缓存原值 → 每轮乘一次 → 数值爆炸；② 换关卡不重置 → 缓存脏；③ 字段名大小写（`spawn` / `num` 都是小写）。

---

## 配方 20 · 改一个"游戏属性"（必须 setter.Invoke）

```csharp
static void SetPropOrFieldSafe(object obj, string name, object val)
{
    var t = obj.GetType();
    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (p != null)
    {
        var setter = p.GetSetMethod(true);
        if (setter != null) { setter.Invoke(obj, new[] { val }); return; }   // ★ 不要用 p.SetValue
    }
    var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (f != null) { f.SetValue(obj, val); return; }
    LogOnce("SetPropOrField: 找不到 " + name);
}
```
**为什么**：`.NET 9` 下 `PropertyInfo.SetValue` 对游戏类型抛 **`MissingMethodException`**（静默失败）。

---

## 配方 21 · 读一个私有字段 / 调用 internal 方法

```csharp
// 字段（含 private）
var f = t.GetField("_slotItems", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

// internal 方法（★ GetMethod 默认只搜 public → 必须给 NonPublic）
var m = FindMethodExact(t, "TryChangeBulletDataInPlace", new[] { typeof(int), cfgType });

// 泛型重载歧义规避
static MethodInfo FindMethodExact(Type t, string name, Type[] ptypes)
{
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
    {
        if (m.Name != name || m.IsGenericMethodDefinition) continue;      // ★ 跳过泛型定义
        var ps = m.GetParameters();
        if (ps.Length != ptypes.Length) continue;
        bool ok = true;
        for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType != ptypes[i]) { ok = false; break; }
        if (ok) return m;
    }
    return null;
}
```

---

## 配方 22 · 安全处理 struct 字段（读-改-回写）

```csharp
var arr = ReadField<Array>(bf, "_data");
for (int i = 0; i < arr.Length; i++)
{
    object bd = arr.GetValue(i);                 // ★ 取出的是副本
    if (!ReadField<bool>(bd, "active")) continue;
    WriteField(bd, "trackOpen", true);           // 改副本
    arr.SetValue(bd, i);                         // ★ 必须写回
}
```
**坑**：`BulletData` 这类 struct 忘了写回 = 改了个寂寞（无异常、无日志）。

---

## 配方 23 · 给游戏内 UI 加一个按钮（PC）

**⚠️ 前提**：电脑版游戏内面板**已被用户要求移除**（改由外置面板控制）。要加 UI 先确认用户要的是哪个。
手机版才加游戏内 UI（见配方 24）。

---

## 配方 24 · 给手机版 UI 加一个开关/按钮/滑块

```csharp
// 开关
AddSwitch(_content, "XX功能", ModSettings.Xxx, v => { ModSettings.Xxx = v; ModSettings.Save(); });
// 按钮
AddButton(_content, "一键XX", () => GameCheats.DoXxxOnce());
// 滑块
AddSlider(_content, "XX倍率", 1, 50, 1, ModSettings.XxxMult, v => ModSettings.XxxMult = v);
```
**要点**：`ModUI.cs` 很大（121KB），**新增 UI 要在对应 `MakePage("...")` 里加**，不要新建页面函数。

---

## 配方 25 · 给外置面板加一个分类页

**改动文件**：`wpf/MainWindow.xaml.cs`
```csharp
// ① Cats 数组加分类
new Cat("xxx", "XX分类", "\uE7C3"),
// ② Feats 字典加该分类的功能项
["xxx"] = new[] { new Feat("Xxx", "XX功能", "switch"), ... },
```
**验证**：启动面板 → 侧栏出现新分类 → 点进去能看到开关 → 点开关游戏内生效。
**坑**：切分类会**重建** UI → 必须有 `SyncAllAsync()` 保证状态正确（历史事故："切页后开关全显示关"）。

---

# 杂交版 MOD（PvzheMod）

《植物大战僵尸 杂交版》的**非官方游戏辅助工具** —— 含**电脑版**、**手机版**、**联机对战**，外加桌面端外置修改器与一键安装器。

> 仅供个人娱乐与非对抗性场景使用；请勿用于任何竞技/排名环境。详见 [免责声明](#免责声明)。

作者：**小晓Air** · <https://space.bilibili.com/3546789573561037>

---

## 目录

- [⚠️ 先读：跑起来前必须改的地方](#-先读跑起来前必须改的地方)
- [项目结构](#项目结构)
- [构建](#构建)
- [联机：自建中继](#联机自建中继)
- [外置修改器：背景图 / 视频](#外置修改器背景图--视频)
- [本仓库不包含什么](#本仓库不包含什么)
- [参与贡献](#参与贡献)
- [许可](#许可)

---

## ⚠️ 先读：跑起来前必须改的地方

为了开源，**服务器凭据与本机路径已全部替换成占位符**。直接编译出来的版本连不上任何联机服务器，而且部分默认路径需要你按自己的机器调整。

### 1. 联机服务器（必改）

| 位置 | 占位符 | 改成什么 |
|---|---|---|
| `mod/PvzheMod/Net/NetProtocol.cs` | `ServerToken = "CHANGE_ME_RELAY_TOKEN"` | 你自建中继的鉴权令牌（须与中继端 `NetConstants.ServerToken` 一致） |
| `mod/PvzheMod/Net/NetSession.cs` | `RelayAddr = "localhost"` | 你的中继地址 |
| `mod/PvzheMod/Net/NetSession.cs` | `RegionAddrs = { "localhost", ... }` | 按区域填；不想用区域概念就全填同一个地址 |
| `androidmod/PvzheAndroid/Net/*` | 同上 | 手机端 `Net/` 是电脑端的**同源副本**，两边必须一起改 |
| `androidmod/build_apk.ps1` / `build_apk.py` | `CHANGE_ME_STORE_PASS` | 你自己的签名库口令 |

### 2. 本机路径

代码里有一批**默认输出/读取路径**，原版指向作者本机目录，开源版已改成**相对路径**。含义如下：

| 用途 | 位置 |
|---|---|
| MOD 设置与存档目录 | `ModSettings.SaveDir`（默认 `mod\`，相对游戏工作目录） |
| 运行日志 | `Bootstrap.LogPath`（默认 `mod\mod_log.txt`） |
| 对战禁用卡表 | `NetPvp.BanListPath`（默认 `mod\pvp_ban.txt`） |
| 自定义植物 / 自定义项目 | `CustomProjectManager.BaseDir`、`CustomPlantWindow.BaseRoot` |

若你的目录不同，改这些常量即可。

### 3. `GodotSharp.dll` 引用（编译手机版必改）

`androidmod/PvzheAndroid/PvzheMod.csproj` 与 `mod/PvzheMod/PvzheMod.csproj` 通过 `HintPath` 引用
游戏目录里的 `GodotSharp.dll`。**这个文件不在本仓库**（属于游戏本体的运行时），
请解包你**自己安装的游戏**，把 `HintPath` 指向你本机的那份。

---

## 项目结构

```
mod/PvzheMod/                 电脑版 MOD 主逻辑（注入进游戏程序集）
mod/PvzheMod/Net/             联机层（协议/会话/同步/对战）—— 与中继、手机端同源
mod/patcher/                  Mono.Cecil IL 注入器（把 MOD 合并进游戏 DLL）
mod/NetTests/                 联机协议层单元测试
mod/tools/外置修改器/wpf/      桌面端外置修改器（WPF）
mod/tools/一键注入/            一键注入器（把 MOD 与修改器装进游戏目录）

androidmod/PvzheAndroid/      手机版 MOD（独立工程，引用手机版 GodotSharp）
androidmod/android_patcher/   手机版注入器
androidmod/build_apk.ps1      APK 打包签名流水线

relay/PvzheRelay/             联机中继服务端（.NET 控制台，纯内存房间，无数据库）
relay/PvzheRelayCli/          中继自检命令行
relay/PvzheRelay.Tests/       中继单元测试
```

**设计要点**：联机协议层（`Net/`）在**电脑 MOD / 手机 MOD / 中继服务端**三处共用同一份源码 ——
中继通过 `<Compile Include="../mod/PvzheMod/Net/*.cs">` 链接，手机端靠目录拷贝同步。
改协议时三处必须一致。

## 构建

需要 **.NET 8 SDK**（手机版另需 Android SDK build-tools 与 JDK）。

```powershell
# 电脑版 MOD
dotnet build mod/PvzheMod -c Release

# 注入（先关闭游戏；patcher 会从 .bak 还原干净原版再合并，不会反复叠加）
dotnet run --project mod/patcher -c Release -- `
  --game "<你的游戏根目录>" --mod "mod/PvzheMod/bin/Release/net8.0/PvzheMod.dll"

# 桌面端外置修改器（自包含单文件，目标机无需装 .NET）
dotnet publish mod/tools/外置修改器/wpf/PvzheRemote.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

# 单元测试
dotnet test mod/NetTests
dotnet test relay/PvzheRelay.Tests
```

> **发布坑**：`dotnet publish` 不带 `-o` 时输出会落在 `bin/.../win-x64/publish/`，
> 容易误把旧文件当成新产物。核对时看**时间戳 + 体积 + 哈希**三项，别只比哈希。

## 联机：自建中继

中继是**纯内存**服务端，不落库，开一个进程就能用。

```powershell
# 本地起一个（默认端口 8231）
dotnet run --project relay/PvzheRelay -c Release

# 自检（11 项）
dotnet run --project relay/PvzheRelayCli -c Release -- -relay localhost:8231 -selftest
```

然后按上面「必改」表把 `RelayAddr` 指到你的地址。
公网部署记得：**安全组放行端口 + 服务器本机防火墙也要放行**（只开其中一个连不上）。

## 外置修改器：背景图 / 视频

- 把图片或 **mp4** 放进 exe 同目录的 `bg\`，或在程序内「首页 → 选择背景」。
- 视频自动**静音循环**；`bg\` 里有图片时会 8 秒淡入淡出轮播（有视频则视频优先）。
- **只支持 mp4**：`.webm` 走系统解码器播不了，请先转码。
- 不要给背景层加 `BlurEffect`：WPF 会先把视频画进位图再模糊，会导致画面发糊、视频只渲染一半。

## 本仓库不包含什么

为尊重第三方版权，以下内容一律未收录：

- **游戏本体及其任何资源与程序集**（`PlantsVsZombies.dll`、`*.pck`、图集、字体等）—— 版权归游戏原作者
- **Wallpaper Engine 素材**与任何第三方绘画 / 视频 / 音乐
- **服务器凭据**（AccessKey、真实域名、公网 IP、中继令牌、签名私钥）—— 均已移除或替换为占位符
- 聊天记录、运行日志、构建产物、模型文件

因此本仓库**无法直接产出可运行的游戏**，只提供我们自己编写的那部分源代码。

## 参与贡献

见 [CONTRIBUTING.md](./CONTRIBUTING.md)。提交前请确认：

1. 没有提交任何**凭据、真实域名/IP、本机绝对路径**
2. 没有提交游戏资源或第三方素材
3. 联机协议改动**三处同步**（电脑 MOD / 手机 MOD / 中继），并跑通 `dotnet test`

## 免责声明

**请在使用前完整阅读本节。下载、编译或运行本项目，即视为已阅读并同意以下全部内容。**

1. **用途限制** —— 本项目仅供**个人学习、研究与技术交流**，**严禁任何商业用途**，
   严禁用于盈利、代练、售卖、引流或任何形式的商业分发。
2. **非官方作品** —— 本项目为第三方爱好者作品，与《植物大战僵尸》及《植物大战僵尸 杂交版》的
   开发者、发行方及相关公司**没有任何隶属或合作关系**，**未获其授权、认可或赞助**。
3. **不含游戏资源** —— 本仓库仅包含作者自行编写的源代码，**不包含游戏本体、程序集、美术、音频、
   字体等任何资源**。使用者须自行准备**合法获得**的游戏副本；因使用本代码产生的全部后果由使用者自负。
4. **使用风险自负** —— 本工具属于**游戏辅助程序**，会**读写游戏存档、修改游戏运行时数据并进行联网通信**，
   可能导致**存档损坏、进度丢失、游戏崩溃或设备异常**。请**务必备份存档**后再使用。
5. **使用场景限定** —— 本项目仅供在**非对抗性场景**下使用（自行游玩、自建服务器、与知情的朋友联机）。
   请勿将其用于任何官方或第三方的竞技/排名环境，使用者应自行确认其行为不违反所使用服务的服务条款。
5. **联机服务** —— 作者**不提供任何公开中继服务**，也不对任何第三方搭建的联机服务的
   可用性、稳定性与安全性负责。开源版中的服务器地址与令牌均为占位符，需自行搭建。
6. **无担保** —— 本软件按**"现状"（AS IS）**提供，不附带任何明示或默示担保，
   包括但不限于适销性、特定用途适用性与非侵权担保。因使用或无法使用本软件所造成的
   任何直接、间接、附带或后果性损失，作者与贡献者**概不负责**。
7. **侵权处理** —— 若本项目无意侵犯了你的合法权益，请通过作者 B 站主页联系，我们将及时删除或更正。
8. **遵守当地法律** —— 使用者应自行确保其使用行为符合所在国家/地区的法律法规及游戏服务条款。

## 许可

代码以 **Apache License 2.0** 授权，见 [LICENSE](./LICENSE) 与 [NOTICE](./NOTICE)。

Apache 2.0 只覆盖**本仓库中我们自己编写的代码**，**不覆盖**上述任何第三方内容，
也不构成对游戏本体的任何授权。使用本代码产生的后果由使用者自行承担。

# 参与贡献

感谢你有兴趣改进这个项目。提交之前请先读完本文 —— 有几条是**硬性要求**，违反会被直接退回。

## 一、绝对不能提交的内容

这是本仓库最严格的规则，因为一旦提交进 Git 历史，**删除也无法真正清除**。

| 禁止 | 说明 |
|---|---|
| **任何凭据** | AccessKey / SecretKey、API Key、Token、密码、私钥（`*.keystore`） |
| **真实服务器信息** | 真实域名、公网 IP、中继地址、端口映射 |
| **本机绝对路径** | 如 `D:\你的目录\...`、`C:\Users\你的用户名\...` |
| **游戏本体资源** | `PlantsVsZombies.dll`、`*.pck`、图集、字体、音频、官方素材 |
| **第三方素材** | 他人的绘画、视频、音乐、模型文件 |
| **个人隐私** | 聊天记录、日志、存档、QQ 号、邮箱、真实姓名 |

服务器相关配置一律使用占位符（`CHANGE_ME_RELAY_TOKEN`、`localhost`）。
本机路径一律使用**相对路径**或从配置读取，不要写死盘符。

> 提交前请自查：`grep -rn "D:\\\\\|C:\\\\Users\|LTAI\|<你的域名>" .`

## 二、代码约定

- **语言 / 框架**：.NET 8（C#）、WPF（桌面工具）、Godot 4.x C#（游戏内 MOD）
- **命名**：类型/方法 `PascalCase`，私有字段 `_camelCase`，常量 `PascalCase`
- **注释用中文**，与现有代码保持一致；**注释要写"为什么"，不要复述"做了什么"**
- **UI 文案不要用 emoji**（桌面端使用 `Segoe MDL2 Assets` 字体图标）
- 保持既有文件结构，不要顺手大规模重构无关代码

## 三、联机协议层必须三处同步 ⚠️

`Net/` 目录（协议、会话、同步、对战）在**三个地方**共用同一份源码：

```
mod/PvzheMod/Net/                ← 电脑版（基准）
androidmod/PvzheAndroid/Net/     ← 手机版（拷贝同步）
relay/PvzheRelay/                ← 中继服务端（通过 <Compile Include> 链接）
```

**改动 `Net/` 时必须同步三处**，否则会出现「房主与客机协议不一致」这类极难排查的问题。
中继服务端对**未知消息类型**采取"忽略并记录"而非断开，所以新增消息不会踢人，
但客户端之间必须同时升级。

## 四、构建与测试

```powershell
dotnet build mod/PvzheMod -c Release        # 电脑版 MOD
dotnet build androidmod/PvzheAndroid -c Release
dotnet build relay/PvzheRelay -c Release

dotnet test mod/NetTests                    # 协议/编解码
dotnet test relay/PvzheRelay.Tests          # 中继房间/鉴权/限流/端到端
```

**提交前必须**：编译 0 错误，且两个测试工程全绿。
改动了 `Net/` 还要跑一遍中继自检：

```powershell
dotnet run --project relay/PvzheRelayCli -c Release -- -relay localhost:8231 -selftest
```

## 五、提交信息

使用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/)：

```
feat(net): 客机缺关卡文件时主动向房主索要
fix(ui): 修正桌面端下拉框在深色主题下的边框
docs: 补充自建中继的部署说明
refactor(relay): 抽出房间广播的快照逻辑
```

## 六、Pull Request 检查清单

- [ ] 编译 0 错误，两个测试工程全绿
- [ ] **没有**凭据、真实域名/IP、本机绝对路径
- [ ] **没有**游戏资源或第三方素材
- [ ] 改动了 `Net/` → 已同步电脑版 / 手机版 / 中继三处
- [ ] 新功能有对应测试（协议层优先补单测）
- [ ] 提交信息符合 Conventional Commits

## 七、报告问题

请在 Issue 里提供：

1. **复现步骤**（越具体越好：点了哪里、看到什么、期望什么）
2. 版本号（外置修改器「用户中心 → 关于」里有）
3. 相关日志（**请先自行脱敏**：删掉 IP、域名、昵称、存档路径）
4. 截图 / 录屏（如果能）

**不要**在 Issue 里贴完整的日志原文或凭据 —— 脱敏后再贴。

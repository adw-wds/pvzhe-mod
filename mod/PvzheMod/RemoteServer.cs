using Godot;
using System;
using System.Text;
using System.Globalization;
using System.Reflection;

namespace PvzheMod
{
    /// <summary>
    /// 外置修改器遥控服务：Godot 原生 TCP 监听 127.0.0.1:28999。
    /// 外置 exe（杂交版外置修改器.exe）发 /ping /get /set 指令，控制 ModSettings 各开关。
    /// 用 Godot.TcpServer + Godot.StreamPeerTcp（引擎 C++ 实现）而非 System.Net.Sockets——
    /// 游戏发布版 AOT 裁剪运行时缺少 TcpListener.AcceptTcpClient()（MissingMethodException）。
    /// Godot 4.7 API：TcpServer.Listen(port,host) / TakeConnection()；StreamPeerSocket.Poll/GetStatus/DisconnectFromHost；
    /// StreamPeer.GetData(n) 返回 Godot.Collections.Array（[0]=Error,[1]=byte[]）。
    /// 全部在主线程 OnFrame->Poll 处理；请求分帧到达，累积到完整请求行再响应。
    /// </summary>
    public static class RemoteServer
    {
        /// <summary>监听端口：默认 28999；多实例 headless 时用环境变量 PVZHE_PORT 覆盖（每实例独立端口）。</summary>
        static readonly int PORT = ResolvePort();

        static int ResolvePort()
        {
            try
            {
                string s = System.Environment.GetEnvironmentVariable("PVZHE_PORT");
                int p;
                if (!string.IsNullOrEmpty(s) && int.TryParse(s, out p) && p > 0 && p < 65536) return p;
            }
            catch { }
            return 28999;
        }

        static TcpServer _server;
        static StreamPeerTcp _client;
        static ulong _clientSinceMs;
        /// <summary>单个连接允许的最长处理时间：超时强制断开，
        /// 否则一个半开/请求不完整的连接会把单连接模型彻底卡死（后续请求全部无响应）。</summary>
        const ulong ClientTimeoutMs = 4000;
        static readonly StringBuilder _req = new StringBuilder();

        public static bool Started { get; private set; }

        /// <summary>最近聊天（供外置修改器房间界面显示）。</summary>
        static readonly System.Collections.Generic.List<string> _netChat = new System.Collections.Generic.List<string>();
        static bool _chatHooked;

        /// <summary>启动监听（幂等）。</summary>
        public static void Start()
        {
            if (Started) return;
            _server = new TcpServer();
            Error err = _server.Listen((ushort)PORT, "127.0.0.1");
            Started = (err == Error.Ok);
            Bootstrap.Log("外置修改器: 监听 127.0.0.1:" + PORT + " -> " + err);
            HookChat();
        }

        static void HookChat()
        {
            if (_chatHooked) return;
            _chatHooked = true;
            try
            {
                NetSession.OnChat += s =>
                {
                    try
                    {
                        _netChat.Add(s ?? "");
                        if (_netChat.Count > 20) _netChat.RemoveAt(0);
                    }
                    catch { }
                };
            }
            catch { }
        }

        /// <summary>主线程每帧调用：接受连接 + 累积读请求 + 处理 + 应用 /set。</summary>
        public static void Poll()
        {
            if (!Started || _server == null) return;
            try
            {
                if (_client == null)
                {
                    StreamPeerTcp c = _server.TakeConnection();
                    if (c == null) return;
                    _client = c;
                    _clientSinceMs = Time.GetTicksMsec();
                    _req.Clear();
                }
                // 超时保护：连接建立后这么久仍没拿到完整请求 → 丢弃并接受下一个
                if (Time.GetTicksMsec() - _clientSinceMs > ClientTimeoutMs)
                {
                    Bootstrap.Log("外置修改器: 连接超时丢弃（请求不完整）");
                    try { _client.DisconnectFromHost(); } catch { }
                    _client = null;
                    _req.Clear();
                    return;
                }
                _client.Poll();
                if (_client.GetStatus() != StreamPeerSocket.Status.Connected)
                {
                    if (_client.GetStatus() == StreamPeerSocket.Status.Error || _client.GetStatus() == StreamPeerSocket.Status.None)
                    {
                        try { _client.DisconnectFromHost(); } catch { }
                        _client = null;
                    }
                    return;   // 连接未就绪，等下一帧再推进
                }
                int avail = _client.GetAvailableBytes();
                while (avail > 0)
                {
                    var arr = _client.GetData(Math.Min(avail, 4096));
                    if (arr == null || arr.Count < 2) break;
                    if (arr[0].AsInt64() != (long)Error.Ok) break;
                    byte[] data = arr[1].AsByteArray();
                    if (data == null) break;
                    _req.Append(Encoding.UTF8.GetString(data));
                    avail = _client.GetAvailableBytes();
                }
                string s = _req.ToString();
                int idx = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx < 0) idx = s.IndexOf("\n\n", StringComparison.Ordinal);
                if (idx < 0) return;   // 等更多数据
                string line = s.Substring(0, idx).Trim();
                _req.Clear();
                string body = Handle(line);
                byte[] resp = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\n\r\n" + body);
                _client.PutData(resp);
                // 每请求独立连接：处理完立即断开并释放，下一帧 accept 新连接——
                // 避免 keep-alive 在单连接模型下客户端关闭后连接不释放（CLOSE_WAIT 泄漏）导致后续请求全连不上
                _client.DisconnectFromHost();
                _client = null;
            }
            catch (Exception ex)
            {
                Bootstrap.Log("外置修改器: Poll 异常 " + ex.Message);
                // 异常时必须释放连接，否则后续请求全部卡死（客户端超时 → “连不上游戏”）
                try { _client?.DisconnectFromHost(); } catch { }
                _client = null;
                _req.Clear();
            }
        }

        /// <summary>选关列表文本：首行 type=…|count=…|error=…；后续每行 lv=key|中文名|备注。</summary>
        static string LevelListText(string type)
        {
            try
            {
                if (string.IsNullOrEmpty(type)) type = "adv";
                var list = GameCheats.ListLevels(type);
                string s = "type=" + type + "|count=" + list.Count + "|error=" + NetOneLine(NetSession.LastError);
                for (int i = 0; i < list.Count; i++)
                {
                    var it = list[i];
                    s += "\nlv=" + NetOneLine(it[0]) + "|" + NetOneLine(it[1]) + "|" + NetOneLine(it.Length > 2 ? it[2] : "");
                }
                return s;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>联机：昵称来源诊断（存档名每个候选来源的取值）。</summary>
        static string NetNamesText()
        {
            try
            {
                string diag;
                string final = GameCheats.NetGetSaveNameEx(out diag);
                return "final=" + NetOneLine(final)
                    + "\nmy=" + NetOneLine(NetSession.MyNick)
                    + "\ndiag=" + NetOneLine(diag);
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>大厅房间列表文本：第 1 行 key=value；后续每行 room=码,人数,上限,已开始,需口令,剩余分,是否允许作弊,房主昵称。
        /// ★ 游戏运行时是 AOT 裁剪环境：不能用 StringBuilder.Append(数值重载) / Convert.ToInt64 等，一律用字符串拼接。"></summary>
        static string RoomListText()
        {
            try
            {
                string s = "state=" + NetSession.State
                    + "|count=" + NetSession.RoomList.Count
                    + "|inRoom=" + (NetSession.InRoom ? 1 : 0)
                    + "|auth=" + (NetSession.Authenticated ? 1 : 0)
                    + "|region=" + CurrentRegionName()
                    + "|error=" + NetOneLine(NetSession.LastError);

                for (int i = 0; i < NetSession.RoomList.Count; i++)
                {
                    var r = NetSession.RoomList[i];
                    s += "\nroom=" + (r.Code ?? "")
                       + "," + (int)r.Players
                       + "," + (int)r.Max
                       + "," + (r.Started ? 1 : 0)
                       + "," + (r.NeedPass ? 1 : 0)
                       + "," + (r.TtlMinutesLeft == RoomListCodec.TtlUnlimited ? -1 : (int)r.TtlMinutesLeft)
                       + "," + (r.AllowCheats ? 1 : 0)
                       + "," + NetOneLine(r.HostNick);
                }
                return s;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>联机：去掉换行/分隔符（状态文本用 | 与 , 分隔）。</summary></summary>
        static string NetOneLine(string s)
        {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Replace(",", " / ").Trim();
        }

        /// <summary>联机：按当前设置 + 查询参数构造 RoomSettings（未给出的项保持原值）。</summary>
        static RoomSettings BuildNetSettings(System.Collections.Generic.Dictionary<string, string> qs)
        {
            var cur = NetSession.Settings ?? new RoomSettings();
            var s = cur.Clone();
            string v;
            int iv;
            if (qs.TryGetValue("max", out v) && int.TryParse(v, out iv)) s.MaxPlayers = ClampByte(iv, 2, NetConstants.MaxPlayers);
            if (qs.TryGetValue("ttl", out v) && int.TryParse(v, out iv)) s.TtlMinutes = ClampByte(iv, 0, 240);
            if (qs.TryGetValue("allowCheats", out v)) s.AllowCheats = (v == "1" || v == "true");
            // ★ 联机一律强制关闭修改器（即使外部有人传 allowCheats=1 也不生效）：
            //   实测只要放开，玩家开的某些功能就会让两边表现不一致（不同步）。
            s.AllowCheats = false;
            if (qs.TryGetValue("late", out v)) s.AllowLateJoin = (v == "1" || v == "true");
            // 对战模式 / 人数平衡（仅对战模式有意义）
            if (qs.TryGetValue("battle", out v)) s.BattleMode = (v == "1" || v == "true");
            if (qs.TryGetValue("balance", out v)) s.BalanceTeams = (v == "1" || v == "true");
            // ★ 口令参数名兼容：外置修改器历史上建房/改设置发的是 &pass=，而这里只读 password
            //   → 建房时设的口令被静默丢弃（「设了口令但别人能直接进」）。两个名字都收。
            string pv;
            if (qs.TryGetValue("password", out pv) || qs.TryGetValue("pass", out pv)) s.Password = pv ?? "";
            if (qs.TryGetValue("mask", out v) && !string.IsNullOrEmpty(v))
            {
                // 注意：游戏运行时是 AOT 裁剪环境，不能用 Convert.ToInt64(string,int) → 手写十六进制解析
                string hv = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? v.Substring(2) : v;
                long m = 0;
                bool ok = hv.Length > 0 && hv.Length <= 8;
                for (int i = 0; ok && i < hv.Length; i++)
                {
                    int d = HexVal(hv[i]);
                    if (d < 0) { ok = false; break; }
                    m = (m << 4) | (uint)d;
                }
                if (ok) s.CheatMask = (uint)m;
            }
            return s;
        }

        /// <summary>uint → 8 位大写十六进制。★ 不能用 v.ToString("X8")：
        /// 带格式串的 ToString 重载在 AOT 裁剪运行时可能缺失（历史踩过 byte.ToString("x2")）。纯字符串拼接最稳。</summary>
        static string HexU32(uint v)
        {
            const string hex = "0123456789ABCDEF";
            string s = "";
            for (int i = 28; i >= 0; i -= 4) s += hex[(int)((v >> i) & 0xF)];
            return s;
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>整数限幅转 byte（避开 AOT 裁剪的 byte.TryParse）。</summary>
        static byte ClampByte(int v, int min, int max)
        {
            if (v < min) v = min;
            if (v > max) v = max;
            return (byte)v;
        }

        /// <summary>联机状态文本（供外置修改器「联机」分类展示）：
        /// 第 1 行 key=value 用 | 分隔；第 2 行 peers=id,颜色槽,是否房主,昵称;…；第 3 行 battle=…。</summary>
        static string NetStatusText()
        {
            try
            {
                var st = NetSession.Settings ?? new RoomSettings();
                // ★ 一律用字符串拼接：AOT 裁剪会把 StringBuilder.Append(数值重载) 也裁掉
                string s = "state=" + NetSession.State
                    + "|channel=" + NetSession.ChannelName()
                    + "|role=" + (NetSession.IsHost ? "房主" : (NetSession.State == NetState.Offline ? "-" : "客机"))
                    + "|room=" + (NetSession.RoomCode ?? "")
                    + "|myId=" + (int)NetSession.MyPlayerId
                    + "|myname=" + NetOneLine(NetSession.MyNick)
                    + "|savename=" + NetOneLine(GameCheats.NetGetSaveName())
                    + "|count=" + NetSession.PeerCount
                    + "|max=" + (int)st.MaxPlayers
                    + "|auth=" + (NetSession.Authenticated ? 1 : 0)
                    + "|started=" + (NetSession.Started ? 1 : 0)
                    + "|battleId=" + (int)NetSession.BattleId
                    + "|level=" + NetOneLine(NetSession.HostLevelKey)
                    + "|levelkey=" + NetOneLine(NetSession.LevelOverrideKey)
                    + "|levelname=" + NetOneLine(NetSession.LevelOverrideName)
                    + "|ttl=" + NetSession.TtlMinutesLeft
                    + "|allowCheats=" + (st.AllowCheats ? 1 : 0)
                    + "|late=" + (st.AllowLateJoin ? 1 : 0)
                    + "|mask=0x" + HexU32(st.CheatMask)
                    + "|haspass=" + (string.IsNullOrEmpty(st.Password) ? "0" : "1")
                    + "|inRoom=" + (NetSession.InRoom ? 1 : 0)
                    // 对战模式：房主设置 + 自己的阵营 + 两边人数（分边 UI 靠这几个值渲染）
                    + "|battleMode=" + (NetSession.IsBattleMode ? 1 : 0)
                    + "|balance=" + (NetSession.IsBalanceTeams ? 1 : 0)
                    + "|myFaction=" + (int)NetSession.MyFaction
                    + "|plantCount=" + NetSession.CountFaction(NetFaction.Plant)
                    + "|zombieCount=" + NetSession.CountFaction(NetFaction.Zombie)
                    // 对战战斗侧运行态（标靶僵尸血量/僵尸方阳光/胜负）
                    + "|" + NetPvp.Describe()
                    + "|region=" + CurrentRegionName()
                    + "|regions=" + RegionListText()
                    + "|server=" + (NetSession.RelayAddr ?? "")
                    + "|ping=" + NetSession.PingMs
                    // M5 在线统计：live=中继当前连接数（含未进房），today=当日唯一 IP 数
                    + "|live=" + NetSession.LiveCount
                    + "|today=" + NetSession.TodayCount
                    + "|roomfp=" + NetOneLine(NetSession.RoomKeyFingerprint)
                    + "|e2e=" + (NetSession.E2eReady ? 1 : 0)
                    + "|notice=" + NetOneLine(NetSession.LastNotice)
                    + "|error=" + NetOneLine(NetSession.LastError);

                string peers = "";
                for (int i = 0; i < NetSession.Players.Count; i++)
                {
                    var p = NetSession.Players[i];
                    if (i > 0) peers += ";";
                    peers += (int)p.Id + "," + (int)p.Color + "," + (p.IsHost ? 1 : 0) + "," + NetOneLine(p.Nick);
                }

                string chat = "";
                for (int i = 0; i < _netChat.Count; i++)
                {
                    if (i > 0) chat += "\u001f";
                    chat += NetOneLine(_netChat[i]);
                }

                return s + "\npeers=" + peers + "\nbattle=" + NetOneLine(NetBattle.Describe()) + "\nchat=" + chat;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        // ================= 联机：服务器区域（界面只显示区域名，不出现域名/IP） =================
        // ★ 区域表已提到 NetSession（手机版 NetUI 也要用），这里只做委托，保持单一数据源。

        /// <summary>区域名 → 域名（不是已知区域则当自定义地址原样返回）。</summary>
        static string ResolveRegion(string nameOrAddr)
        {
            if (string.IsNullOrEmpty(nameOrAddr)) return NetSession.RelayAddr;
            string s = nameOrAddr.Trim();
            for (int i = 0; i < NetSession.RegionNames.Length; i++)
                if (NetSession.RegionNames[i] == s) return NetSession.RegionAddrs[i];
            return s;
        }

        /// <summary>当前地址 → 区域名（未知则“自定义”）。</summary>
        static string CurrentRegionName() { return NetSession.CurrentRegionName(); }

        /// <summary>区域列表（单行，供界面下拉）：名称:是否开放;名称:是否开放…</summary>
        static string RegionListText() { return NetSession.RegionListText(); }

        static string _curCmd = "";

        static string Handle(string line)
        {
            try
            {
                string[] parts = line.Split(' ');
                string path = (parts.Length >= 2) ? parts[1] : "/";
                if (path.StartsWith("/ping")) return "ok";
                if (path.StartsWith("/status")) return BuildStatusBody();
                if (path.StartsWith("/get")) return BuildGetBody();
                if (path.StartsWith("/cmd"))
                {
                    var qs = ParseQuery(path);
                    string name;
                    qs.TryGetValue("name", out name);
                    _curCmd = name ?? "";
                    if (name == "OpenConsole") { GameCheats.ToggleConsoleIfIdle(); return "ok"; }
                    if (name == "OpenProjectPanel") { CustomProjectPanel.Toggle(); return "ok"; }
                    if (name == "SaveSettings") { ModSettings.Save(); return "ok"; }
                    if (name == "ApplyEnginePerf") { GameCheats.ApplyEnginePerfNow(); return "ok"; }
                    if (name == "LaunchMowers") { GameCheats.LaunchAllMowers(); return "ok"; }
                    if (name == "RestoreMowers") { GameCheats.RestoreAllMowers(); return "ok"; }
                    if (name == "InstantWinAll") { GameCheats.InstantWinAll(); return "ok"; }
                    if (name == "CompleteAllLevels") { GameCheats.CompleteAllLevels(); return "ok"; }
                    if (name == "CompleteAllDailyLevels") { int n = GameCheats.CompleteAllDailyLevels(); return n + ""; }
                    // ---- 挖掘更多游戏函数为端点（见开发文档） ----
                    if (name == "SkipWave") { GameCheats.SkipWave(); return "ok"; }
                    if (name == "SkipFinalWave") { GameCheats.SkipFinalWave(); return "ok"; }
                    if (name == "SkipWaveWait") { GameCheats.SkipWaveWait(); return "ok"; }
                    if (name == "LaunchTestLevel") { GameCheats.LaunchTestLevel(); return "ok"; }
                    if (name == "LaunchCustomLevel") { GameCheats.LaunchCustomLevel(); return "ok"; }
                    if (name == "UnlockAllFeatures") { GameCheats.UnlockAllFeatures(); return "ok"; }
                    if (name == "KillAllZombies") { GameCheats.KillAllZombies(MainRoot()); return "ok"; }
                    if (name == "KillAllPlants") { int n = GameCheats.KillAllPlants(); return n + ""; }
                    if (name == "CompleteMode") { string mode; qs.TryGetValue("mode", out mode); if (string.IsNullOrEmpty(mode)) return "err:no-mode"; GameCheats.CompleteMode(mode); return "ok"; }
                    if (name == "AwardDiamondOnWin") { string num; qs.TryGetValue("num", out num); int n = 10; int.TryParse(num, out n); GameCheats.AwardDiamondOnWin(n); return "ok"; }
                    if (name == "ImportLevelFile") { string filePath; qs.TryGetValue("path", out filePath); if (string.IsNullOrEmpty(filePath)) return "err:no-path"; GameCheats.ImportLevelFile(filePath); return "ok"; }
                    if (name == "ExportPvzData") return GameCheats.ExportPvzData();
                    if (name == "ExportLevelMakerAssets") return GameCheats.ExportLevelMakerAssets();
                    if (name == "ExportOfficialLevelList") return GameCheats.ExportOfficialLevelList();
                    if (name == "CharmAll") { string camp; qs.TryGetValue("camp", out camp); GameCheats.CharmAll(MainRoot(), camp ?? ""); return "ok"; }
                    if (name == "UncharmAll") { GameCheats.UncharmAll(MainRoot()); return "ok"; }
                    if (name == "BungiGrabAllField") { GameCheats.BungiGrabAllField(); return "ok"; }
                    if (name == "SpawnBungiAllField") { int n = GameCheats.SpawnBungiAllField(); return n + ""; }
                    if (name == "ResetAllBrains") { GameCheats.ResetAllBrains(); return "ok"; }
                    if (name == "ForceRefreshRoleCache") { GameCheats.ForceRefreshRoleCache(); return "ok"; }
                    if (name == "ClearSeedBankAll") { GameCheats.ClearSeedBankAll(MainRoot()); return "ok"; }
                    if (name == "RandomizeVases") { GameCheats.RandomizeVases(MainRoot(), true); return "ok"; }
                    if (name == "AddSun") { string num; qs.TryGetValue("num", out num); long n = 100; long.TryParse(num, out n); GameCheats.AddSun(n); return "ok"; }
                    if (name == "GetSunCount") return GameCheats.GetSunCount() + "";
                    if (name == "ToggleScreenEffect") { string fx; string add; qs.TryGetValue("fx", out fx); qs.TryGetValue("add", out add); if (string.IsNullOrEmpty(fx)) return "err:no-fx"; bool on = add == "1" || add == "true"; GameCheats.ToggleScreenEffect(fx, on); return "ok"; }
                    if (name == "ClearGlobalOverrides") { GameCheats.ClearGlobalOverrides(); return "ok"; }
                    if (name == "GetGlobalOverrideCount") return GameCheats.GetGlobalOverrideCount() + "";
                    if (name == "UnlockAllSkins") { int n = GameCheats.UnlockAllSkins(); return n + ""; }
                    if (name == "FailGame") { GameCheats.FailGameNow(); return "ok"; }
                    if (name == "DescribeScreen") return GameCheats.DescribeScreenText();
                    if (name == "FeatureDiag") return GameCheats.DescribeFeatureState();
                    if (name == "ForceWin") return GameCheats.ForceWinAll();
                    if (name == "OpenScreen") { string screen; qs.TryGetValue("screen", out screen); if (string.IsNullOrEmpty(screen)) return "err:no-screen"; GameCheats.OpenScreen(screen); return "ok:" + screen; }
                    if (name == "OnlineSearch") { string q; qs.TryGetValue("q", out q); string pg; qs.TryGetValue("page", out pg); int n = 1; int.TryParse(pg, out n); return GameCheats.OnlineSearch(q ?? "", n); }
                    if (name == "OnlineSearchResult") return GameCheats.OnlineSearchResult();
                    if (name == "OnlinePlay") { string id; qs.TryGetValue("id", out id); if (string.IsNullOrEmpty(id)) return "err:no-id"; return GameCheats.OnlinePlay(id); }
                    if (name == "OnlinePlayStatus") return GameCheats.OnlinePlayStatus();
                    if (name == "OnlinePlayFile") { string fp; string lid; qs.TryGetValue("path", out fp); qs.TryGetValue("id", out lid); if (string.IsNullOrEmpty(fp)) return "err:no-path"; return GameCheats.OnlinePlayFile(fp, lid); }
                    // ---- 联机（M2c）：外置修改器「联机」分类 ----
                    if (name == "NetStatus") return NetStatusText();
                    if (name == "NetNames") return NetNamesText();
                    if (name == "NetLobby") { NetSession.EnsureLobby(); NetSession.RequestRoomList(); return "ok"; }
                    if (name == "NetRooms") { NetSession.RequestRoomList(); return RoomListText(); }
                    if (name == "NetLobbyClose") { NetSession.CloseLobby(); return "ok"; }
                    if (name == "NetServer") { string addr; qs.TryGetValue("addr", out addr); string real = ResolveRegion(addr); if (!string.IsNullOrEmpty(real)) NetSession.RelayAddr = real; return "ok:" + CurrentRegionName(); }
                    if (name == "NetRegions") return RegionListText();
                    if (name == "NetCreate") { string nick; qs.TryGetValue("nick", out nick); var ns = BuildNetSettings(qs); return NetSession.CreateRoom(nick, ns) ? "ok" : "err:" + NetOneLine(NetSession.LastError); }
                    if (name == "NetJoin") { string code, nick, pass; qs.TryGetValue("code", out code); qs.TryGetValue("nick", out nick); if (!qs.TryGetValue("pass", out pass)) qs.TryGetValue("password", out pass); return NetSession.JoinRoom(code, nick, pass) ? "ok" : "err:" + NetOneLine(NetSession.LastError); }
                    if (name == "NetLeave") { NetSession.Leave(); return "ok"; }
                    // 对战模式分边：f=0 未选 / 1 植物方 / 2 僵尸方
                    if (name == "NetFaction")
                    {
                        string fv; qs.TryGetValue("f", out fv);
                        int fi;
                        if (!int.TryParse(fv, out fi) || fi < 0 || fi > 2) return "err:bad-faction";
                        return NetSession.SetFaction((byte)fi) ? "ok" : "err:" + NetOneLine(NetSession.LastError);
                    }
                    // 对战禁卡表：查看 / 改完 mod\pvp_ban.txt 后热更
                    if (name == "NetPvpBanList") return NetPvp.BanListText();
                    if (name == "NetPvpReload") { NetPvp.ReloadBanList(); return "ok"; }
                    // 诊断：某张卡的 id 口径（确认禁卡匹配是否对得上）
                    if (name == "PvpCardId")
                    {
                        string cid; qs.TryGetValue("id", out cid);
                        return GameCheats.PvpCardIdInfo(cid);
                    }
                    // ---- 对战：僵尸方卡槽 ----
                    // 商店数据（已过滤禁卡；价格取游戏官方 cost）
                    if (name == "PvpShop") return GameCheats.ZombieShopJson();
                    // 买卡：PvpBuy&id=ZombieNormal
                    if (name == "PvpBuy")
                    {
                        string id; qs.TryGetValue("id", out id);
                        return NetPvp.TryBuyZombie(id) ? "ok:" + NetOneLine(NetPvp.LastMsg) : "err:" + NetOneLine(NetPvp.LastMsg);
                    }
                    // 放置：PvpPlace&id=ZombieNormal&x=7&y=3（不填 id 就用手上选中的那张）
                    if (name == "PvpPlace")
                    {
                        string id, xs, ys;
                        qs.TryGetValue("id", out id);
                        if (string.IsNullOrEmpty(id)) id = NetPvp.HeldCard;
                        qs.TryGetValue("x", out xs);
                        qs.TryGetValue("y", out ys);
                        int gx, gy;
                        if (!int.TryParse(xs, out gx) || !int.TryParse(ys, out gy)) return "err:bad-pos";
                        return NetPvp.TryPlaceZombie(id, gx, gy) ? "ok:" + NetOneLine(NetPvp.LastMsg) : "err:" + NetOneLine(NetPvp.LastMsg);
                    }
                    // 调试/补偿：PvpSun&v=100
                    if (name == "PvpSun")
                    {
                        string vs; qs.TryGetValue("v", out vs);
                        int v;
                        if (!int.TryParse(vs, out v)) return "err:bad-v";
                        NetPvp.AddZombieSun(v);
                        return "ok:" + NetPvp.ZombieSun;
                    }
                    if (name == "NetLevels") { string lt; qs.TryGetValue("type", out lt); return LevelListText(lt); }
                    if (name == "NetSetLevel")
                    {
                        // 注意：URL 里的 name 就是命令名，所以关卡显示名用 lvname 传
                        string lk, ln;
                        qs.TryGetValue("key", out lk);
                        qs.TryGetValue("lvname", out ln);
                        if (string.IsNullOrEmpty(lk))
                        {
                            NetSession.LevelOverrideKey = "";
                            NetSession.LevelOverrideName = "";
                            return "ok:cleared（改为跟随房主当前所在关卡）";
                        }
                        NetSession.LevelOverrideKey = lk;
                        NetSession.LevelOverrideName = ln ?? "";
                        return "ok|key=" + NetOneLine(lk) + "|name=" + NetOneLine(ln);
                    }
                    if (name == "NetStart")
                    {
                        // 选关优先：房主指定了关卡就用它；否则用房主当前所在关卡。
                        string lv = NetSession.LevelOverrideKey;
                        if (string.IsNullOrEmpty(lv)) lv = GameCheats.NetGetLevelRef();
                        if (string.IsNullOrEmpty(lv)) return "err:请先选择关卡（或进入一个关卡）后再开始对局";
                        if (!NetSession.StartBattle(lv)) return "err:" + NetOneLine(NetSession.LastNotice);
                        // ★ 选了关（不跟随当前所在关卡）时，房主自己也进这个关卡，
                        //   否则只有客机跟着进、房主还留在主菜单（用户反馈“点了开始对局自己没进关”）
                        if (!string.IsNullOrEmpty(NetSession.LevelOverrideKey))
                            GameCheats.EnterLevelByAnyKey(NetSession.LevelOverrideKey);
                        return "ok|level=" + NetOneLine(lv) + "|name=" + NetOneLine(NetSession.LevelOverrideName);
                    }
                    if (name == "NetEnd") { NetSession.EndBattle(); return "ok"; }
                    if (name == "NetClose") { NetSession.RequestCloseRoom(); return "ok"; }
                    if (name == "NetKick") { string kid; qs.TryGetValue("id", out kid); int kv; if (!int.TryParse(kid, out kv) || kv <= 0 || kv > 65535) return "err:bad-id"; NetSession.KickPlayer((ushort)kv); return "ok"; }
                    if (name == "NetChat") { string text; qs.TryGetValue("text", out text); NetSession.SendChat(text); return "ok"; }
                    if (name == "NetSet")
                    {
                        if (!NetSession.IsHost) return "err:not-host";
                        NetSession.SendRoomSettings(BuildNetSettings(qs));
                        return "ok";
                    }
                    if (name == "NetCheatNames")
                    {
                        // 供外置修改器构建「逐项允许作弊」勾选表：位值:名称;位值:名称…
                        // ★ 绝不能写 sb.Append(order[i])：order[i] 是 uint，
                        //   `StringBuilder.Append(UInt32)` 被 AOT 裁剪掉了（Method not found），
                        //   异常会在 Poll 里抛出 → 整个 HTTP 轮询挂掉 → 外置修改器直接连不上游戏。
                        //   纯字符串拼接最安全（这里只有 15 项）。
                        var order = CheatNames.Order;
                        string names = "";
                        for (int i = 0; i < order.Length; i++)
                        {
                            if (i > 0) names += ";";
                            names += order[i].ToString() + ":" + CheatNames.Of(order[i]);
                        }
                        return "ok|names=" + names;
                    }
                    if (name == "BattleStart") return GameCheats.StartBattleNow();
                    if (name == "Stage") return GameCheats.DescribeBattleStage();
                    if (name == "CustomPlantLoad")
                    {
                        string file; qs.TryGetValue("file", out file);
                        return string.IsNullOrEmpty(file) ? "err:no-file" : CustomPlantManager.Load(file);
                    }
                    if (name == "CustomPlantRemove")
                    {
                        string pn; qs.TryGetValue("name", out pn);
                        return string.IsNullOrEmpty(pn) ? "err:no-name" : CustomPlantManager.Remove(pn);
                    }
                    if (name == "CustomPlantList") return CustomPlantManager.ListJson();
                    if (name == "CustomPlantSpawn")
                    {
                        string pn, rs, cs;
                        qs.TryGetValue("name", out pn);
                        qs.TryGetValue("row", out rs);
                        qs.TryGetValue("col", out cs);
                        int row = 0, col = 0;
                        int.TryParse(rs, out row);
                        int.TryParse(cs, out col);
                        if (string.IsNullOrEmpty(pn)) return "err:no-name";
                        return CustomPlantManager.Spawn(pn, new Vector2I(col, row)) ? "ok" : "err:spawn-fail";
                    }
                    if (name == "CustomBulletLoad")
                    {
                        string file; qs.TryGetValue("file", out file);
                        return string.IsNullOrEmpty(file) ? "err:no-file" : CustomBulletManager.CustomBulletLoad(file);
                    }
                    if (name == "CustomBulletRemove")
                    {
                        string bn; qs.TryGetValue("name", out bn);
                        return string.IsNullOrEmpty(bn) ? "err:no-name" : CustomBulletManager.CustomBulletRemove(bn);
                    }
                    if (name == "CustomBulletList") return CustomBulletManager.CustomBulletList();
                    if (name == "CustomProjectLoad")
                    {
                        string tp, file;
                        qs.TryGetValue("type", out tp);
                        qs.TryGetValue("file", out file);
                        return string.IsNullOrEmpty(tp) || string.IsNullOrEmpty(file) ? "err:bad-param" : CustomProjectManager.Load(tp, file);
                    }
                    if (name == "CustomProjectList")
                    {
                        string tp; qs.TryGetValue("type", out tp);
                        return CustomProjectManager.List(tp ?? "");
                    }
                    if (name == "CustomProjectRemove")
                    {
                        string tp, pn;
                        qs.TryGetValue("type", out tp);
                        qs.TryGetValue("name", out pn);
                        return CustomProjectManager.Remove(tp ?? "", pn ?? "");
                    }
                    if (name == "AutoLoadAll") return CustomProjectManager.AutoLoadAll();
                    if (name == "CustomZombieLoad")
                    {
                        string file; qs.TryGetValue("file", out file);
                        return string.IsNullOrEmpty(file) ? "err:no-file" : CustomZombieManager.Load(file);
                    }
                    if (name == "CustomZombieList") return CustomZombieManager.List();
                    if (name == "ZombieTemplates") return CustomZombieManager.ZombieTemplates();
                    if (name == "PlantTemplates") return CustomPlantManager.PlantTemplates();
                    if (name == "CustomZombieRemove")
                    {
                        string pn; qs.TryGetValue("name", out pn);
                        return string.IsNullOrEmpty(pn) ? "err:no-name" : CustomZombieManager.Remove(pn);
                    }
                    if (name == "CustomZombieSpawn")
                    {
                        string pn, rs, cs;
                        qs.TryGetValue("name", out pn);
                        qs.TryGetValue("row", out rs);
                        qs.TryGetValue("col", out cs);
                        int row = 0, col = 0;
                        int.TryParse(rs, out row);
                        int.TryParse(cs, out col);
                        if (string.IsNullOrEmpty(pn)) return "err:no-name";
                        return CustomZombieManager.Spawn(pn, new Godot.Vector2I(col, row)) ? "ok" : "err:spawn-fail";
                    }
                    if (name == "CustomCardLoad")
                    {
                        string file; qs.TryGetValue("file", out file);
                        return string.IsNullOrEmpty(file) ? "err:no-file" : CustomCardManager.Load(file);
                    }
                    if (name == "CustomCardList") return CustomCardManager.List();
                    if (name == "CustomCardRemove")
                    {
                        string pn; qs.TryGetValue("name", out pn);
                        return string.IsNullOrEmpty(pn) ? "err:no-name" : CustomCardManager.Remove(pn);
                    }
                    return "err:no-cmd";
                }
                if (path.StartsWith("/spawn"))
                {
                    var qs = ParseQuery(path);
                    string id, rs, cs;
                    qs.TryGetValue("id", out id);
                    qs.TryGetValue("row", out rs);
                    qs.TryGetValue("col", out cs);
                    int row = 0, col = 0;
                    int.TryParse(rs, out row);
                    int.TryParse(cs, out col);
                    if (!string.IsNullOrEmpty(id) && GameCheats.SpawnPacketToScene(id, new Vector2I(col, row), false))
                        return "ok";
                    return "err:spawn-fail";
                }
                if (path.StartsWith("/addpacket"))
                {
                    var qs = ParseQuery(path);
                    string id;
                    qs.TryGetValue("id", out id);
                    if (!string.IsNullOrEmpty(id) && GameCheats.AddPacketToInventory(id))
                        return "ok";
                    return "err:add-fail";
                }
                if (path.StartsWith("/packets"))
                {
                    var qs = ParseQuery(path);
                    string type;
                    qs.TryGetValue("type", out type);
                    bool plant = (type != "zombie");
                    var ids = GameCheats.GetPacketIds(plant);
                    StringBuilder sb = new StringBuilder("[");
                    for (int i = 0; i < ids.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"id\":\"")
                          .Append(ids[i].Replace("\\", "\\\\").Replace("\"", "\\\""))
                          .Append("\",\"name\":\"")
                          .Append(GameCheats.GetPacketDisplayNameZh(ids[i]).Replace("\\", "\\\\").Replace("\"", "\\\""))
                          .Append("\"}");
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
                if (path.StartsWith("/bullettypes"))
                {
                    var keys = GameCheats.GetBulletTypeKeys();
                    StringBuilder sb = new StringBuilder("[");
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"key\":\"")
                          .Append(keys[i].Replace("\\", "\\\\").Replace("\"", "\\\""))
                          .Append("\",\"name\":\"")
                          .Append(GameCheats.TranslateBulletName(keys[i]).Replace("\\", "\\\\").Replace("\"", "\\\""))
                          .Append("\"}");
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
                if (path.StartsWith("/types")) return EntityTools.GetTypeListJson();
                if (path.StartsWith("/entities")) return EntityTools.EnumerateJson();
                if (path.StartsWith("/eprops"))
                {
                    var qs = ParseQuery(path);
                    string id;
                    qs.TryGetValue("id", out id);
                    ulong uid;
                    return ulong.TryParse(id, out uid) ? EntityTools.GetPropsJson(uid) : "{\"err\":\"bad-id\"}";
                }
                if (path.StartsWith("/eset"))
                {
                    var qs = ParseQuery(path);
                    string id, f, v;
                    qs.TryGetValue("id", out id);
                    qs.TryGetValue("f", out f);
                    qs.TryGetValue("v", out v);
                    ulong uid;
                    if (ulong.TryParse(id, out uid))
                        return EntityTools.SetProp(uid, f ?? "", v ?? "");   // 返回 ok / err:具体原因（外置面板直接显示）
                    return "err:bad-id";
                }
                if (path.StartsWith("/gset"))
                {
                    var qs = ParseQuery(path);
                    string type, prop, val;
                    qs.TryGetValue("type", out type);
                    qs.TryGetValue("prop", out prop);
                    qs.TryGetValue("val", out val);
                    double dv;
                    if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(prop) && double.TryParse(val, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out dv))
                        return EntityTools.SetGlobalOverride(type, prop, dv);
                    return "err";
                }
                if (path.StartsWith("/gremove"))
                {
                    var qs = ParseQuery(path);
                    string type, prop;
                    qs.TryGetValue("type", out type);
                    qs.TryGetValue("prop", out prop);
                    if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(prop))
                        return EntityTools.RemoveGlobalOverride(type, prop);
                    return "err";
                }
                if (path.StartsWith("/cfgprops"))
                {
                    var qs = ParseQuery(path);
                    string id;
                    qs.TryGetValue("id", out id);
                    return EntityTools.GetPacketConfigPropsJson(id ?? "");
                }
                if (path.StartsWith("/cfgset"))
                {
                    var qs = ParseQuery(path);
                    string id, f, v;
                    qs.TryGetValue("id", out id);
                    qs.TryGetValue("f", out f);
                    qs.TryGetValue("v", out v);
                    if (EntityTools.SetPacketConfigProp(id ?? "", f ?? "", v ?? "")) return "ok";
                    return "err";
                }
                if (path.StartsWith("/gget")) return EntityTools.GetGlobalOverridesJson();
                if (path.StartsWith("/tprops"))
                {
                    var qs = ParseQuery(path);
                    string type;
                    qs.TryGetValue("type", out type);
                    return EntityTools.GetTypePropsJson(type ?? "");
                }
                if (path.StartsWith("/set"))
                {
                    var qs = ParseQuery(path);
                    string name, val;
                    qs.TryGetValue("name", out name);
                    qs.TryGetValue("val", out val);
                    if (!string.IsNullOrEmpty(name))
                    {
                        if (ApplySetting(name, val ?? "")) return "queued";
                        return "err:no-field";
                    }
                    return "err:no-name";
                }
                return "404";
            }
            catch (Exception ex) { Bootstrap.Log("外置修改器: 处理异常 [" + _curCmd + "] " + ex.Message); return "err:" + ex.Message; }
        }

        /// <summary>获取场景树根（供带 root 参数的函数使用；无场景返回 null）。</summary>
        static Node MainRoot()
        {
            try
            {
                if (Engine.GetMainLoop() is SceneTree st && st.Root != null) return st.Root;
            }
            catch { }
            return null;
        }

        static bool ApplySetting(string name, string val)
        {
            // 目标卡池由外置先写 trickpool.txt 再发极短指令加载（几百张 id 走 URL 太长会保存失败）
            if (name == "TrickReload")
            {
                try { return GameCheats.TrickReloadFile(); } catch { return false; }
            }
            FieldInfo f = typeof(ModSettings).GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (f == null) return false;
            if (f.FieldType == typeof(bool))
            {
                bool on = val == "1" || val == "true" || val == "on" || val == "True";
                f.SetValue(null, on);
            }
            else if (f.FieldType == typeof(int))
                f.SetValue(null, int.Parse(val, CultureInfo.InvariantCulture));
            else if (f.FieldType == typeof(float))
                f.SetValue(null, float.Parse(val, CultureInfo.InvariantCulture));
            else if (f.FieldType == typeof(string))
                f.SetValue(null, val);
            else return false;
            Bootstrap.Log("外置修改器: " + name + " = " + val);
            // 操作时自动持久化（写 #manual）：否则重开游戏 MOD 启动 ForceAllOff 把所有开关强制 false
            try { ModSettings.Save(); } catch { }
            return true;
        }

        static System.Collections.Generic.Dictionary<string, string> ParseQuery(string path)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = path.IndexOf('?');
            if (q < 0) return d;
            foreach (string kv in path.Substring(q + 1).Split('&'))
            {
                if (string.IsNullOrEmpty(kv)) continue;
                int eq = kv.IndexOf('=');
                if (eq < 0) d[kv] = "";
                else d[Uri.UnescapeDataString(kv.Substring(0, eq))] = Uri.UnescapeDataString(kv.Substring(eq + 1));
            }
            return d;
        }

        static string BuildGetBody()
        {
            StringBuilder sb = new StringBuilder("{");
            bool first = true;
            foreach (FieldInfo f in typeof(ModSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.IsLiteral) continue; // const
                object v = f.GetValue(null);
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(f.Name).Append("\":");
                sb.Append(JsonValue(v));
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>实时状态（外置修改器底部状态栏轮询）：已开功能数 + 游戏速度。</summary>
        static string BuildStatusBody()
        {
            int on = 0;
            foreach (FieldInfo f in typeof(ModSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.IsLiteral || f.FieldType != typeof(bool)) continue;
                if (f.GetValue(null) is bool b && b) on++;
            }
            return "{\"scene\":\"game\",\"on\":" + on.ToString(CultureInfo.InvariantCulture) +
                   ",\"speed\":" + ModSettings.GameSpeed.ToString("R", CultureInfo.InvariantCulture) + "}";
        }

        static string JsonValue(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is int) return ((int)v).ToString(CultureInfo.InvariantCulture);
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is string s)
                return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return "\"" + Convert.ToString(v, CultureInfo.InvariantCulture).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}

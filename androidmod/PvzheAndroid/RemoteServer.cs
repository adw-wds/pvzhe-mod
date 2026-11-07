using Godot;
using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace PvzheMod
{
    /// <summary>
    /// 手机版「外置修改器」服务端：Godot 原生 TCP 监听 127.0.0.1:28999。
    /// 与电脑版 RemoteServer 协议保持一致（/ping /status /get /set /cmd），
    /// 这样外置程序（androidmod\PvzheRemote）只写一套客户端。
    ///
    /// ★ 为什么用 Godot.TcpServer 而不是 System.Net.Sockets：
    ///   游戏发布运行时是裁剪过的 Mono，TcpListener 部分重载缺失（历史踩坑）。
    /// ★ 单连接模型 + 每请求独立连接（处理完立即断开），避免 CLOSE_WAIT 把后续请求卡死。
    /// </summary>
    public static class RemoteServer
    {
        public const int Port = 28999;
        // ★ 2026-11-07：主菜单/加载界面的物理帧可能很稀疏，4s 太容易把正常请求判超时 → 8s。
        const ulong ClientTimeoutMs = 8000;

        static TcpServer _server;
        static StreamPeerTcp _client;
        static ulong _clientSinceMs;
        static ulong _startMs;
        static readonly StringBuilder _req = new StringBuilder();
        static PvzheRemotePump _pump;   // 独立帧泵：不依赖场景类注入，切场景/主菜单也持续 Poll

        public static bool Started { get; private set; }
        public static string LastError = "";

        /// <summary>把远程服务挂到一个 Always 的 Node 上：即使 GlobalColor 注入点所在场景被切走，
        /// 服务端仍能每帧 Poll（修复「关卡外/主菜单开关不生效」）。</summary>
        public static void EnsurePump(Node root)
        {
            try
            {
                if (_pump != null && GodotObject.IsInstanceValid(_pump)) return;
                var tree = root != null ? root.GetTree() : (Engine.GetMainLoop() as SceneTree);
                if (tree == null || tree.Root == null) return;
                _pump = new PvzheRemotePump();
                _pump.Name = "PvzheRemotePump";
                _pump.ProcessMode = Node.ProcessModeEnum.Always;
                tree.Root.AddChild(_pump);
            }
            catch (Exception ex) { Bootstrap.Log("外置(手机): 挂帧泵失败 " + ex.Message); }
        }

        // 皮肤扫描（分帧）结果缓存
        static bool _skinScanning;
        static string _skinCardsJson = "[]";
        static bool _skinHooked;

        /// <summary>启动监听（幂等，主线程调用）。</summary>
        public static void Start()
        {
            if (Started) return;
            try
            {
                _server = new TcpServer();
                Error err = _server.Listen((ushort)Port, "127.0.0.1");
                Started = (err == Error.Ok);
                _startMs = Time.GetTicksMsec();
                Bootstrap.Log("外置修改器(手机): 监听 127.0.0.1:" + Port + " -> " + err);
            }
            catch (Exception ex)
            {
                Started = false;
                Bootstrap.Log("外置修改器(手机): 启动异常 " + ex.Message);
            }
        }

        /// <summary>主线程每帧调用：接受连接 + 累积读请求 + 处理 + 应用 /set。</summary>
        public static void Poll()
        {
            if (!Started || _server == null) { Start(); if (!Started) return; }
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
                if (Time.GetTicksMsec() - _clientSinceMs > ClientTimeoutMs)
                {
                    try { _client.DisconnectFromHost(); } catch { }
                    _client = null;
                    _req.Clear();
                    return;
                }
                _client.Poll();
                if (_client.GetStatus() != StreamPeerSocket.Status.Connected)
                {
                    var st = _client.GetStatus();
                    if (st == StreamPeerSocket.Status.Error || st == StreamPeerSocket.Status.None)
                    {
                        try { _client.DisconnectFromHost(); } catch { }
                        _client = null;
                    }
                    return;
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
                _client.DisconnectFromHost();
                _client = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { _client?.DisconnectFromHost(); } catch { }
                _client = null;
                _req.Clear();
            }
        }

        static string Handle(string line)
        {
            try
            {
                string[] parts = line.Split(' ');
                string path = (parts.Length >= 2) ? parts[1] : "/";
                if (path.StartsWith("/ping")) return "ok";
                if (path.StartsWith("/status")) return BuildStatus();
                if (path.StartsWith("/get")) return BuildGetBody();
                if (path.StartsWith("/events")) return "{}";
                if (path.StartsWith("/packets"))
                {
                    var qp = ParseQuery(path);
                    string tp; qp.TryGetValue("type", out tp);
                    return PacketsJson(tp != "zombie");
                }
                if (path.StartsWith("/set"))
                {
                    var qs = ParseQuery(path);
                    string name, val;
                    qs.TryGetValue("name", out name);
                    qs.TryGetValue("val", out val);
                    if (string.IsNullOrEmpty(name)) return "err:no-name";
                    return ApplySetting(name, val ?? "") ? "queued" : "err:no-field";
                }
                if (path.StartsWith("/cmd"))
                {
                    var qs = ParseQuery(path);
                    string name;
                    qs.TryGetValue("name", out name);
                    return RunCommand(name ?? "", qs);
                }
                return "404";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        // ===================== 查询 =====================

        static string BuildStatus()
        {
            int on = 0;
            foreach (FieldInfo f in typeof(ModSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.IsLiteral || f.FieldType != typeof(bool)) continue;
                if (f.GetValue(null) is bool b && b) on++;
            }
            ulong up = (Time.GetTicksMsec() - _startMs) / 1000UL;
            return "{\"ok\":true,\"version\":\"" + ModSettings.Version + "\",\"on\":" + on
                 + ",\"speed\":" + ModSettings.GameSpeed.ToString("R", CultureInfo.InvariantCulture)
                 + ",\"uptime\":" + up
                 + ",\"scan\":" + (_skinScanning ? 1 : 0) + "}";
        }

        static string BuildGetBody()
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (FieldInfo f in typeof(ModSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.IsLiteral) continue;
                object v;
                try { v = f.GetValue(null); } catch { continue; }
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(f.Name).Append("\":");
                sb.Append(JsonValue(v));
            }
            sb.Append('}');
            return sb.ToString();
        }

        static string JsonValue(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is int) return ((int)v).ToString(CultureInfo.InvariantCulture);
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            string s = v as string;
            if (s != null) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return "\"" + Convert.ToString(v, CultureInfo.InvariantCulture).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ===================== 设置写入 =====================

        static bool ApplySetting(string name, string val)
        {
            try
            {
                FieldInfo f = typeof(ModSettings).GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (f == null) return false;
                if (f.FieldType == typeof(bool))
                {
                    f.SetValue(null, val == "1" || val == "true" || val == "on" || val == "True");
                }
                else if (f.FieldType == typeof(int))
                {
                    int iv; if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv)) return false;
                    f.SetValue(null, iv);
                }
                else if (f.FieldType == typeof(float))
                {
                    float fv; if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out fv)) return false;
                    f.SetValue(null, fv);
                }
                else if (f.FieldType == typeof(string))
                {
                    f.SetValue(null, val ?? "");
                }
                else return false;
                Bootstrap.Log("外置(手机): " + name + " = " + val);
                try { ModSettings.Save(); } catch { }
                return true;
            }
            catch (Exception ex) { Bootstrap.Log("外置(手机) set 异常: " + ex.Message); return false; }
        }

        // ===================== 动作 =====================

        static string RunCommand(string name, System.Collections.Generic.Dictionary<string, string> qs)
        {
            string v;
            int n;
            switch (name)
            {
                case "SaveSettings": ModSettings.Save(); return "ok";
                case "SetEcho":
                    // 外置端 UI 状态写回（不参与游戏逻辑），仅回 ok
                    return "ok";
                case "AddSun":
                    qs.TryGetValue("num", out v);
                    long add = 100; long.TryParse(v, out add);
                    { long s = GameCheats.AddSun(add); return s < 0 ? "err:no-level" : ("ok:" + s); }
                case "GetSun": return "ok:" + GameCheats.GetSunCount();
                case "SpawnCurrency":
                    qs.TryGetValue("type", out v);
                    string cnum; qs.TryGetValue("num", out cnum);
                    n = 1; int.TryParse(cnum, out n);
                    if (n < 1) n = 1; if (n > 200) n = 200;
                    GameCheats.CreateCurrency(string.IsNullOrEmpty(v) ? "Gold" : v, n);
                    return "ok";
                case "SpawnAward":
                    qs.TryGetValue("tag", out v);
                    GameCheats.SpawnAward(v == "Trophy" ? "get_TOWER_DEFENSE_AWARD_TROPHY" : "get_TOWER_DEFENSE_AWARD_PURSE", v ?? "Purse");
                    return "ok";
                case "KillAllZombies": GameCheats.KillAllZombies(MainRoot()); return "ok";
                case "KillAllPlants": return "ok:" + GameCheats.KillAllPlants();
                case "CharmAll": qs.TryGetValue("camp", out v); GameCheats.CharmAll(MainRoot(), v ?? ""); return "ok";
                case "UncharmAll": GameCheats.UncharmAll(MainRoot()); return "ok";
                case "UnlockAllSkins": return "ok:" + GameCheats.UnlockAllSkins();
                case "InstantWinAll": GameCheats.InstantWinAll(); return "ok";
                case "CompleteAllLevels": GameCheats.CompleteAllLevels(); return "ok";
                case "CompleteAllDailyLevels": return "ok:" + GameCheats.CompleteAllDailyLevels();
                case "CompleteMode": qs.TryGetValue("mode", out v); if (string.IsNullOrEmpty(v)) return "err:no-mode"; GameCheats.CompleteMode(v); return "ok";
                case "SkipWaveWait": GameCheats.SkipWaveWait(); return "ok";
                case "SkipFinalWave": GameCheats.SkipFinalWave(); return "ok";
                case "LaunchMowers": return "ok:" + GameCheats.LaunchAllMowers();
                case "RestoreMowers": GameCheats.RestoreAllMowers(); return "ok";
                case "ResetAllBrains": GameCheats.ResetAllBrains(); return "ok";
                case "ShopAllItems": GameCheats.GetShopAllItems(); return "ok";
                case "UnlockAllFeatures": GameCheats.UnlockAllFeatures(); return "ok";
                case "ClearLockedCards": GameCheats.ForceUnlockPackets(MainRoot()); return "ok";
                case "ClearSeedBank": GameCheats.ClearSeedBankAll(MainRoot()); return "ok";
                case "OpenConsole": GameCheats.OpenConsolePanel(); return "ok";
                case "ClearCrater": return "ok";
                // ---- 刷物 ----
                case "SpawnAll":
                    qs.TryGetValue("type", out v);
                    return "ok:" + GameCheats.SpawnAllField(string.IsNullOrEmpty(v) ? "plant" : v);
                case "SpawnPacket":
                    {
                        string sid, xs, ys;
                        qs.TryGetValue("id", out sid);
                        qs.TryGetValue("x", out xs);
                        qs.TryGetValue("y", out ys);
                        if (string.IsNullOrEmpty(sid)) return "err:no-id";
                        int gx = 4, gy = 2;
                        int.TryParse(xs, out gx);
                        int.TryParse(ys, out gy);
                        return GameCheats.SpawnPacketToScene(sid, new Vector2I(gx, gy), false) ? "ok" : "err:spawn-fail";
                    }
                case "AddPacket":
                    qs.TryGetValue("id", out v);
                    if (string.IsNullOrEmpty(v)) return "err:no-id";
                    return GameCheats.AddPacketToInventory(v, false) ? "ok" : "err:add-fail";
                // ---- 布阵器 ----
                case "BoardList": return "ok\n" + GameCheats.BoardListJson();
                case "BoardSave":
                    qs.TryGetValue("nm", out v);
                    string parts; qs.TryGetValue("parts", out parts);
                    return "ok:" + GameCheats.BoardSaveUi(v ?? "", parts ?? "");
                case "BoardLoad":
                    qs.TryGetValue("file", out v);
                    qs.TryGetValue("parts", out parts);
                    if (string.IsNullOrEmpty(v)) return "err:no-file";
                    return "ok:" + GameCheats.BoardLoadUi(v, parts ?? "");
                case "BoardDelete":
                    qs.TryGetValue("file", out v);
                    if (string.IsNullOrEmpty(v)) return "err:no-file";
                    return "ok:" + GameCheats.BoardDelete(v);
                // ---- 皮肤 ----
                case "SkinScan":
                    HookSkinScan();
                    _skinCardsJson = "[]";
                    _skinScanning = true;
                    GameCheats.StartSkinScanFrameByFrame();
                    return "ok";
                case "SkinCards":
                    return _skinScanning ? "scanning" : _skinCardsJson;
                case "SkinList":
                    qs.TryGetValue("card", out v);
                    if (string.IsNullOrEmpty(v)) return "err:no-card";
                    return SkinListText(v);
                case "SkinApply":
                    {
                        string card, skin;
                        qs.TryGetValue("card", out card);
                        qs.TryGetValue("skin", out skin);
                        if (string.IsNullOrEmpty(card) || string.IsNullOrEmpty(skin)) return "err:bad-param";
                        return GameCheats.UnlockSkin(card, skin) ? "ok" : "err:apply-fail";
                    }
                default: return "err:no-cmd";
            }
        }

        static void HookSkinScan()
        {
            if (_skinHooked) return;
            _skinHooked = true;
            GameCheats.OnSkinScanDone = res =>
            {
                try
                {
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < res.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        string dn = "";
                        try { dn = GameCheats.GetPacketDisplayName(res[i]); } catch { }
                        sb.Append("{\"id\":\"").Append(JsonEscape(res[i]))
                          .Append("\",\"name\":\"").Append(JsonEscape(string.IsNullOrEmpty(dn) ? res[i] : dn)).Append("\"}");
                    }
                    sb.Append(']');
                    _skinCardsJson = sb.ToString();
                }
                catch { _skinCardsJson = "[]"; }
                _skinScanning = false;
            };
        }

        static string SkinListText(string card)
        {
            try
            {
                var skins = GameCheats.GetPacketSkins(card);
                var sb = new StringBuilder("[");
                for (int i = 0; i < skins.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"key\":\"").Append(JsonEscape(skins[i])).Append("\"}");
                }
                sb.Append(']');
                return sb.ToString();
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>卡牌列表 JSON：[{"id":"...","name":"..."}]。AOT 裁剪环境：不用 LINQ / 数值 Append。</summary>
        static string PacketsJson(bool plant)
        {
            try
            {
                var ids = GameCheats.GetPacketIds(plant);
                var sb = new StringBuilder("[");
                for (int i = 0; i < ids.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    string dn = "";
                    try { dn = GameCheats.GetPacketDisplayName(ids[i]); } catch { }
                    sb.Append("{\"id\":\"").Append(JsonEscape(ids[i]))
                      .Append("\",\"name\":\"").Append(JsonEscape(string.IsNullOrEmpty(dn) ? ids[i] : dn)).Append("\"}");
                }
                sb.Append(']');
                return sb.ToString();
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        static Node MainRoot()
        {
            try
            {
                if (Engine.GetMainLoop() is SceneTree st && st.Root != null) return st.Root;
            }
            catch { }
            return null;
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
    }

    /// <summary>常驻帧泵：独立于场景注入点，每帧驱动 RemoteServer（ProcessMode=Always，暂停也跑）。</summary>
    public sealed class PvzheRemotePump : Node
    {
        public override void _Process(double delta)
        {
            try { RemoteServer.Start(); RemoteServer.Poll(); } catch { }
        }
    }
}

using System;
using System.Reflection;
using Godot;

namespace PvzheMod
{
    /// <summary>控制台快捷键输入监听：_Input 事件驱动（比每帧轮询可靠，不会漏按键；手机=音量-）。</summary>
    public class ConsoleInputCatcher : Godot.Node
    {
        public override void _Input(Godot.InputEvent e)
        {
            try
            {
                if (e is Godot.InputEventKey k && k.Pressed && !k.Echo)
                {
                    if (k.PhysicalKeycode == Godot.Key.Volumedown || k.Keycode == Godot.Key.Volumedown
                        || k.PhysicalKeycode == Godot.Key.Volumeup || k.Keycode == Godot.Key.Volumeup)
                        GameCheats.ToggleConsoleIfIdle();
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 游戏修改（作弊）功能：无限阳光、无限金币。
    /// 通过反射调用游戏类型（不直接引用主程序集，避免 .NET 8/9 版本冲突）。
    /// </summary>
    public static partial class GameCheats
    {
        private static Type _tdmType;
        private static Type _rmType;   // ResourceManager（皮肤/卡包遍历用）
        private static PropertyInfo _instanceProp;
        private static MethodInfo _getSun;
        private static MethodInfo _setSun;
        private static MethodInfo _coinSetNum;
        private static int _ncTimer;
        private static int _ncLogTimer;
        private static int _almanacTimer;
        private static int _purpleTimer;
        private static int _unlockTimer;   // 清除锁定卡牌降频器
        private static Type _attackCompType;
        private static bool _asLogged;
        private static bool _hpLogged;
        private static bool _charmLogged;
        private static bool _charmImmuneLogged;   // 已清除某角色魅惑免疫位（unUseBuffFlags & 8）
        private static int _combatLogTimer;
        private static int _combatTimer;

        /// <summary>跳过当前波次等待。</summary>
        public static void SkipWaveWait() { InvokeCommand("SkipWaveWait"); }
        /// <summary>跳到最终波。</summary>
        public static void SkipFinalWave() { InvokeCommand("SkipToFinalWave"); }
        /// <summary>跳过波次（下一波）。</summary>
        public static void SkipWave() { InvokeCommand("SkipToWave"); }

        /// <summary>取单例：优先 Instance 静态属性，其次 Instance 静态字段。</summary>
        static object GetSingleton(Type t)
        {
            try
            {
                var p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(null);
                var f = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                if (f != null) return f.GetValue(null);
            }
            catch { }
            return null;
        }

        /// <summary>自制关卡数据源（关卡数据抽象层）：当前返回游戏内置测试关卡配置，
        /// 未来自制关卡格式/外部制作软件只改这一层（解析出自制格式并产出同样的配置对象）。</summary>
        public static class CustomLevelSource
        {
            /// <summary>测试关卡配置资源路径（游戏内置）。</summary>
            public const string TestLevelPath = "res://Asset/Config/Level/TowerDefense/Test/LevelTest.tres";

            /// <summary>获取测试关卡配置（加载资源）。未来返回自制关卡配置对象。</summary>
            public static object GetTestLevelConfig()
            {
                try { return Godot.GD.Load(TestLevelPath); }
                catch (System.Exception ex) { Bootstrap.Log("测试关卡: 加载配置异常 " + ex.Message); return null; }
            }
        }

        /// <summary>点「测试关卡」进入对局：加载测试关卡配置 → 复刻 CommandManager.EnterLoadedLevel 的进对局调用链
        /// （TowerDefenseManager.currentLevelConfig = 配置；Global.enterLevelMode="LoadLevel"；SceneManager.ChangeScene("TowerDefense",true)）。
        /// 纯反射，不引用游戏类型。</summary>
        public static void LaunchTestLevel()
        {
            try
            {
                object cfg = CustomLevelSource.GetTestLevelConfig();
                if (cfg == null) { Bootstrap.Log("测试关卡: 配置加载失败 " + CustomLevelSource.TestLevelPath); return; }
                EnterLevelWithConfig(cfg);
            }
            catch (System.Exception ex) { Bootstrap.Log("测试关卡异常: " + ex.Message); }
        }

        /// <summary>进入对局核心：设 TowerDefenseManager.currentLevelConfig + Global.enterLevelMode + SceneManager.ChangeScene("TowerDefense",true)。</summary>
        static void EnterLevelWithConfig(object cfg, string mode = "LoadLevel")
        {
            try
            {
                var tdm = FindType("TowerDefenseManager");
                if (tdm == null) { Bootstrap.Log("进入对局: 找不到 TowerDefenseManager"); return; }
                var tdmInst = GetSingleton(tdm);
                if (tdmInst == null) { Bootstrap.Log("进入对局: TowerDefenseManager.Instance 为空"); return; }
                var cfgF = FindFieldInfo(tdmInst, "currentLevelConfig");
                if (cfgF == null) { Bootstrap.Log("进入对局: 找不到 currentLevelConfig 字段"); return; }
                cfgF.SetValue(tdmInst, cfg);
                var gl = FindType("Global");
                if (gl != null)
                {
                    var gInst = GetSingleton(gl);
                    var modeP = gl.GetProperty("enterLevelMode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (gInst != null && modeP != null)
                    {
                        var setter = modeP.GetSetMethod(true);
                        if (setter != null) setter.Invoke(gInst, new object[] { mode });
                    }
                }
                var sm = FindType("SceneManager");
                if (sm == null) { Bootstrap.Log("进入对局: 找不到 SceneManager"); return; }
                var smInst = GetSingleton(sm);
                var cs = sm.GetMethod("ChangeScene", new Type[] { typeof(string), typeof(bool) });
                if (smInst == null || cs == null) { Bootstrap.Log("进入对局: SceneManager/ChangeScene 不可用"); return; }
                cs.Invoke(smInst, new object[] { "TowerDefense", true });
                Bootstrap.Log("进入对局 TowerDefense 配置=" + cfg.GetType().Name);
            }
            catch (System.Exception ex) { Bootstrap.Log("进入对局异常: " + ex.Message); }
        }

        /// <summary>立即胜利（跳关）：0.27 原生方式直接驱动 Wave 最终状态（TowerDefenseBattleFeatureWave.Instance 静态字段，官方 _CmdInstantWin 同款）
        /// + 通用胜利事件 EmitGameVictory + 杀光僵尸 + CommandManager.InstantWin 兜底。确保 0.27 关卡真正出胜利结算。</summary>
        public static void InstantWinAll()
        {
            bool waveOk = TryInstantWinWave();
            bool emitOk = false;
            try
            {
                var t = FindType("BattleEventBus");
                if (t == null) { Bootstrap.Log("跳关: 找不到 BattleEventBus"); return; }
                var instField = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                var inst = instField != null ? instField.GetValue(null) : null;
                if (inst == null) { Bootstrap.Log("跳关: BattleEventBus.Instance 为空"); return; }
                var m = t.GetMethod("EmitGameVictory", Type.EmptyTypes);
                if (m != null) { m.Invoke(inst, null); emitOk = true; Bootstrap.Log("跳关: EmitGameVictory 已触发"); }
                else Bootstrap.Log("跳关: 找不到 EmitGameVictory");
            }
            catch (System.Exception ex) { Bootstrap.Log("跳关异常: " + ex.Message); }
            // 兜底：官方 CommandManager.InstantWin（KillAllZombies + waveFinal + EmitFinal + awaitSpawn=false）
            InvokeCommand("InstantWin");
            // 杀光全部敌对（僵尸）替代出钱带奖励
            try { if (_lastRoot != null) KillAllZombies(_lastRoot); } catch { }
            Bootstrap.Log("跳关完成: wave=" + waveOk + " emit=" + emitOk);
        }

        /// <summary>0.27 原生即时胜利：取 TowerDefenseBattleFeatureWave.Instance（静态字段，官方 _CmdInstantWin 同款），
        /// 设 waveStart=true / waveFinal=true / awaitSpawn=false 并调 EmitFinal()。返回是否成功执行。</summary>
        static bool TryInstantWinWave()
        {
            try
            {
                var wt = FindType("TowerDefenseBattleFeatureWave");
                if (wt == null) { Bootstrap.Log("跳关: 找不到 TowerDefenseBattleFeatureWave"); return false; }
                var instF = wt.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                var wave = instF != null ? instF.GetValue(null) : null;
                if (wave == null || !GodotObject.IsInstanceValid(wave as GodotObject)) { Bootstrap.Log("跳关: Wave.Instance 为空"); return false; }
                SetPropOrField(wave, "waveStart", true);
                SetPropOrField(wave, "waveFinal", true);
                SetPropOrField(wave, "awaitSpawn", false);
                var emit = FindMethodExact(wt, "EmitFinal", Type.EmptyTypes);
                if (emit != null) emit.Invoke(wave, null);
                Bootstrap.Log("跳关: Wave 最终波已触发 (waveFinal=true + EmitFinal)");
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("跳关 Wave 异常: " + ex.Message); return false; }
        }

        /// <summary>通关钻石奖杯：胜利后在局内生成钻石（胜利奖励变钻石奖杯，替代纯金币）。不在局内则跳过。</summary>
        public static void AwardDiamondOnWin(int num = 10)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return;   // 不在局内
                CreateCurrency("Diamond", num);
                Bootstrap.Log("通关钻石奖杯: 生成钻石 x" + num);
            }
            catch (System.Exception ex) { Bootstrap.Log("钻石奖杯异常: " + ex.Message); }
        }

        /// <summary>一键通关全部：解锁全部功能 + 触发全局胜利 + 遍历所有关卡设置 Finish=1（真正通关）。</summary>
        public static void CompleteAllLevels()
        {
            UnlockAllFeatures();
            InvokeCommand("InstantWin");
            try
            {
                var t = FindType("BattleEventBus");
                if (t != null)
                {
                    var instField = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                    var inst = instField != null ? instField.GetValue(null) : null;
                    if (inst != null)
                    {
                        var m = t.GetMethod("EmitGameVictory", Type.EmptyTypes);
                        if (m != null) m.Invoke(inst, null);
                    }
                }
            }
            catch { }
            // 真正通关所有关卡：遍历 GetLevelDictionary()，对每关设置 ["Key"]["Finish"] = 1
            try
            {
                var gsmType = FindType("GameSaveManager");
                if (gsmType == null) { Bootstrap.Log("一键通关: 找不到 GameSaveManager"); return; }
                var instField = gsmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                var inst = instField != null ? instField.GetValue(null) : null;
                if (inst == null) { Bootstrap.Log("一键通关: GameSaveManager.Instance 为空"); return; }
                var getDict = gsmType.GetMethod("GetLevelDictionary", Type.EmptyTypes);
                var dict = getDict != null ? getDict.Invoke(inst, null) : null;
                if (dict == null) { Bootstrap.Log("一键通关: GetLevelDictionary 为空"); return; }
                int done = 0;
                var getVal = gsmType.GetMethod("GetLevelValue", new Type[] { typeof(string) });
                var setVal = gsmType.GetMethod("SetLevelValue", new Type[] { typeof(string), typeof(Godot.Collections.Dictionary) });
                if (dict is Godot.Collections.Dictionary gd)
                {
                    foreach (var kv in gd.Keys)
                    {
                        if (kv.VariantType == Variant.Type.Nil) continue;
                        string key = kv.ToString();
                        if (key.Length == 0 || getVal == null || setVal == null) continue;
                        var val = getVal.Invoke(inst, new object[] { key }) as Godot.Collections.Dictionary;
                        if (val == null) continue;
                        Godot.Collections.Dictionary keyDict = null;
                        if (val.ContainsKey("Key"))
                        {
                            var k = val["Key"];
                            if (k.VariantType == Variant.Type.Dictionary)
                                keyDict = k.AsGodotDictionary();
                        }
                        if (keyDict == null)
                        {
                            keyDict = new Godot.Collections.Dictionary();
                            val["Key"] = keyDict;
                        }
                        keyDict["Finish"] = 1;
                        // 钻石奖牌条件（DragMenuSelectItemChapter.Init）：done(Finish>0) + mower(Mower) + finish(Difficult/Ultimate)
                        // 都满足 → 章节右下角显示 DIMOND_MEDAL 钻石奖牌；只设 Finish → 银色奖牌
                        val["Mower"] = true;
                        val["Difficult"] = true;
                        try { setVal.Invoke(inst, new object[] { key, val }); done++; } catch { }
                    }
                    // 保存
                    var save = gsmType.GetMethod("SaveGameConfig", Type.EmptyTypes);
                    if (save != null) { try { save.Invoke(inst, null); } catch { } }
                }
                Bootstrap.Log("一键通关全部: 已通关 " + done + " 个关卡（含钻石奖牌条件 Mower+Difficult）");
            }
            catch (System.Exception ex) { Bootstrap.Log("一键通关异常: " + ex.Message); }
            // 0.27 新增关卡不在存档字典里（未游玩/未解锁不注册）→ 额外遍历官方关卡注册表 LevelResource.json
            // 的 SaveKey 全部设 Finish=1+Mower+Difficult，覆盖 0.27 全部新关卡（Chapter9 等）。
            CompleteOfficialLevels();
        }

        /// <summary>遍历官方关卡注册表 Asset/Config/Level/LevelResource.json（0.27 全部关卡含新增）设 Finish=1+Mower+Difficult。
        /// 存档字典 GetLevelDictionary 只含已注册关卡，新关卡不在其中 → 必须从注册表补齐。</summary>
        static int CompleteOfficialLevels()
        {
            int done = 0;
            try
            {
                var gsmType = FindType("GameSaveManager");
                if (gsmType == null) { Bootstrap.Log("一键通关注册表: 找不到 GameSaveManager"); return 0; }
                var inst = gsmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (inst == null) { Bootstrap.Log("一键通关注册表: GameSaveManager.Instance 为空"); return 0; }
                var getVal = gsmType.GetMethod("GetLevelValue", new Type[] { typeof(string) });
                var setVal = gsmType.GetMethod("SetLevelValue", new Type[] { typeof(string), typeof(Godot.Collections.Dictionary) });
                if (getVal == null || setVal == null) { Bootstrap.Log("一键通关注册表: GetLevelValue/SetLevelValue 缺失"); return 0; }
                var keys = LoadOfficialLevelKeys();
                foreach (var key in keys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    done += SetLevelFinish(inst, getVal, setVal, key, true, true, true);
                }
                var save = gsmType.GetMethod("SaveGameConfig", Type.EmptyTypes);
                if (save != null) { try { save.Invoke(inst, null); } catch { } }
                Bootstrap.Log("一键通关注册表: 官方关卡已通关 " + done + " 关");
            }
            catch (System.Exception ex) { Bootstrap.Log("一键通关注册表异常: " + ex.Message); }
            return done;
        }

        /// <summary>递归收集官方关卡注册表 JSON 中所有 SaveKey（Variant/Dictionary/Array 混合结构）。
        /// 注意：Godot.Json.Data 及字典取值都是 Variant，必须先按 VariantType 解包再递归，否则收集不到。</summary>
        static void CollectSaveKeys(object data, System.Collections.Generic.List<string> keys)
        {
            try
            {
                if (data == null) return;
                // Variant 解包（Json.Data 顶层 + 嵌套取值都是 Variant）
                if (data is Godot.Variant vv)
                {
                    if (vv.VariantType == Variant.Type.Dictionary) { CollectSaveKeys(vv.AsGodotDictionary(), keys); return; }
                    if (vv.VariantType == Variant.Type.Array) { CollectSaveKeys(vv.AsGodotArray(), keys); return; }
                    return;
                }
                if (data is Godot.Collections.Dictionary gd)
                {
                    foreach (var k in gd.Keys)
                    {
                        try
                        {
                            var v = gd[k];
                            if (k.VariantType == Variant.Type.String && k.AsString() == "SaveKey" && v.VariantType == Variant.Type.String)
                                keys.Add(v.AsString());
                            else CollectSaveKeys(v, keys);
                        }
                        catch { }
                    }
                }
                else if (data is Godot.Collections.Array ga)
                {
                    for (int i = 0; i < ga.Count; i++)
                    {
                        try { CollectSaveKeys(ga[i], keys); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>一键通关每日挑战：遍历 LevelDictionary，所有以 DailyLevel 开头的关卡设 Key.Finish=1 并保存。
        /// 每日挑战与在线关卡同结构存储（LevelDictionary["DailyLevel-日期-序号"]["Key"]["Finish"]）。</summary>
        public static int CompleteAllDailyLevels()
        {
            int done = 0;
            try
            {
                var gsmType = FindType("GameSaveManager");
                if (gsmType == null) { Bootstrap.Log("每日挑战: 找不到 GameSaveManager"); return 0; }
                var instField = gsmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                var inst = instField != null ? instField.GetValue(null) : null;
                if (inst == null) { Bootstrap.Log("每日挑战: GameSaveManager.Instance 为空"); return 0; }
                var getDict = gsmType.GetMethod("GetLevelDictionary", Type.EmptyTypes);
                var dict = getDict != null ? getDict.Invoke(inst, null) : null;
                if (dict == null) { Bootstrap.Log("每日挑战: GetLevelDictionary 为空"); return 0; }
                var getVal = gsmType.GetMethod("GetLevelValue", new Type[] { typeof(string) });
                var setVal = gsmType.GetMethod("SetLevelValue", new Type[] { typeof(string), typeof(Godot.Collections.Dictionary) });
                if (dict is Godot.Collections.Dictionary gd)
                {
                    foreach (var kv in gd.Keys)
                    {
                        if (kv.VariantType == Variant.Type.Nil) continue;
                        string key = kv.ToString();
                        if (key.Length == 0 || !key.StartsWith("DailyLevel") || getVal == null || setVal == null) continue;
                        var val = getVal.Invoke(inst, new object[] { key }) as Godot.Collections.Dictionary;
                        if (val == null) continue;
                        Godot.Collections.Dictionary keyDict = null;
                        if (val.ContainsKey("Key"))
                        {
                            var k = val["Key"];
                            if (k.VariantType == Variant.Type.Dictionary)
                                keyDict = k.AsGodotDictionary();
                        }
                        if (keyDict == null)
                        {
                            keyDict = new Godot.Collections.Dictionary();
                            val["Key"] = keyDict;
                        }
                        keyDict["Finish"] = 1;
                        try { setVal.Invoke(inst, new object[] { key, val }); done++; } catch { }
                    }
                    var save = gsmType.GetMethod("SaveGameConfig", Type.EmptyTypes);
                    if (save != null) { try { save.Invoke(inst, null); } catch { } }
                }
                Bootstrap.Log("一键通关每日挑战: " + done + " 个");
            }
            catch (System.Exception ex) { Bootstrap.Log("每日挑战异常: " + ex.Message); }
            return done;
        }

        /// <summary>刷怪显示名：从 GetPacketConfig(id).name 读取中文显示名（图鉴上的名字），失败回退 id。</summary>
        public static string GetPacketDisplayName(string id)
        {
            try
            {
                var cfg = GetConfig(id);
                if (cfg != null)
                {
                    var nameF = cfg.GetType().GetField("name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (nameF != null && nameF.FieldType == typeof(string))
                    {
                        var n = (string)nameF.GetValue(cfg);
                        if (!string.IsNullOrEmpty(n) && n != id) return n;
                    }
                }
            }
            catch { }
            return id;
        }

        public static void InvokeCommand(string methodName)
        {
            try
            {
                var t = FindType("CommandManager");
                if (t == null) { Bootstrap.Log("命令失败: 找不到 CommandManager"); return; }
                var instField = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                var inst = instField != null ? instField.GetValue(null) : null;
                if (inst == null) { Bootstrap.Log("命令失败: CommandManager.Instance 为空（" + methodName + "）"); return; }
                var m = t.GetMethod(methodName, Type.EmptyTypes);
                if (m != null) { m.Invoke(inst, null); Bootstrap.Log("命令已执行: " + methodName); }
                else Bootstrap.Log("命令失败: 找不到方法 " + methodName);
            }
            catch (System.Exception ex) { Bootstrap.Log("命令异常 " + methodName + ": " + ex.Message); }
        }

        /// <summary>解锁已注册全局功能（紫卡/模式等限制）。只解锁 FeatureInit.json 里真实注册的 feature——
        /// 未注册的调 Unlock 会在游戏内部 GD.PushError 刷屏（每 5 秒 20+ 条×17 行栈）导致游戏/UI 卡死。
        /// 用 IsRegistered 预检（它是安全检查，不 PushError）。</summary>
        static bool _unlockFeatLogged;
        public static void UnlockAllFeatures()
        {
            try
            {
                var t = FindType("GlobalFeatureManager");
                if (t == null) return;
                var instProp = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var inst = instProp != null ? instProp.GetValue(null) : null;
                if (inst == null) return;
                var unlock = t.GetMethod("Unlock", new Type[] { typeof(string), typeof(bool) });
                var isReg = t.GetMethod("IsRegistered", new Type[] { typeof(string) });
                if (unlock == null || isReg == null) return;
                string[] feats = new string[]
                {
                    "PacketBank", "Portal", "SeedBank", "RainMode", "ConveyorBelt",
                    "Glove", "ScreenEffect", "Sun", "Map", "Mower", "Brain",
                    "Adventure", "Challenge", "Survival", "MiniGame", "Puzzle",
                    "Shop", "Shovel", "Arena", "Garden", "Trade"
                };
                int unlocked = 0;
                for (int i = 0; i < feats.Length; i++)
                {
                    try
                    {
                        // 只解锁已注册的（未注册的 Unlock 会内部 PushError 刷屏）
                        if (!(bool)isReg.Invoke(inst, new object[] { feats[i] })) continue;
                        unlock.Invoke(inst, new object[] { feats[i], false });
                        unlocked++;
                    }
                    catch { }
                }
                // 游戏内置开关：debugPacketOpenAll=true 打开所有卡包（紫卡可选）
                try
                {
                    var cm = FindType("CommandManager");
                    if (cm != null)
                    {
                        var cmInstF = cm.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                        var cmInst = cmInstF != null ? cmInstF.GetValue(null) : null;
                        if (cmInst != null)
                        {
                            var dpoa = cm.GetField("debugPacketOpenAll", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (dpoa != null && dpoa.FieldType == typeof(bool) && !(bool)dpoa.GetValue(cmInst))
                                dpoa.SetValue(cmInst, true);
                        }
                    }
                }
                catch { }
                if (!_unlockFeatLogged) { _unlockFeatLogged = true; Bootstrap.Log("已解锁已注册功能 " + unlocked + " 个"); }
            }
            catch { }
        }

        static Type FindType(string name)
        {
            try
            {
                // 优先搜索主程序集（目标类型都在主程序集），避免其他程序集 GetType 抛异常
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.GetName().Name != "PlantsVsZombies") continue;
                        var t = asm.GetType(name);
                        if (t != null) return t;
                    }
                    catch { }
                }
                // 兜底：搜索全部程序集
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(name);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>图鉴全解：持续设置 plantShowAll=true（让图鉴打开时游戏分批加载全部条目）。
        /// 注意：默认不主动调用 InitPlant/InitZombie——那会让图鉴 Detach 并一次性重建全部条目导致卡死。
        /// forceRefresh=true（用户手动开开关）时若图鉴已打开，刷新一次让全解立即生效。</summary>
        public static void SetAlmanacAllNow(Node root, bool forceRefresh = false)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                WalkSetAlmanac(root, forceRefresh);
                // 优化：定时调用不刷日志（原每秒 2 次写盘），仅手动强制刷新记录一次
                if (forceRefresh) Bootstrap.Log("图鉴全解已设置（强制刷新）");
            }
            catch { }
        }

        static void WalkSetAlmanac(Node node, bool forceRefresh)
        {
            foreach (var child in node.GetChildren(true))
            {
                if (GodotObject.IsInstanceValid(child))
                {
                    var cn = child.GetType().FullName;
                    if (cn != null && cn.Contains("Almanac") && !cn.Contains("RuntimeTest"))
                    {
                        var f = child.GetType().GetField("plantShowAll", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (f != null && f.FieldType == typeof(bool) && !(bool)f.GetValue(child))
                            f.SetValue(child, true);
                        // 图鉴已打开且手动开启开关：主动刷新一次让全解立即生效（仅一次，用户主动操作）
                        if (forceRefresh && child.IsNodeReady())
                        {
                            var init = child.GetType().GetMethod("InitPlant", Type.EmptyTypes);
                            if (init != null) { try { init.Invoke(child, null); } catch { } }
                            var initz = child.GetType().GetMethod("InitZombie", Type.EmptyTypes);
                            if (initz != null) { try { initz.Invoke(child, null); } catch { } }
                        }
                    }
                }
                WalkSetAlmanac(child, forceRefresh);
            }
        }

        static bool _settingsLoaded;
        static bool _settingsLoadedDiag;
        static bool _onFrameErrorLogged;
        static int _logFlushTimer;
        public static void OnFrame(Node root)
        {
            // ★ 性能诊断：分段计时（开销仅几次 GetTicksUsec，可忽略）
            bool _pd = ModSettings.PerfDiag;
            ulong _pfStart = 0, _pfNet0 = 0, _pfNet1 = 0;
            if (_pd) _pfStart = Time.GetTicksUsec();
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                _lastRoot = root;
                // 进战斗标记（诊断闪退：记录进入 TowerDefense 场景）
                LogBattleStart(root);
                // 首次运行加载持久化设置（Bootstrap.Init 未被注入调用，这里补上；否则每次启动全默认）
                if (!_settingsLoaded)
                {
                    _settingsLoaded = true;
                    try { ModSettings.Load(); Bootstrap.Log("ModSettings 已加载"); }
                    catch (System.Exception ex) { Bootstrap.Log("ModSettings.Load 异常: " + ex.Message); }
                }
                else if (!_settingsLoadedDiag) { _settingsLoadedDiag = true; Bootstrap.Log("settingsLoaded 初始为 true（异常）"); }
                // 补全加载界面缺的步骤文案（让最耗时的角色资源加载阶段不再显示成「卡在 7/7」）
                RegisterLoadingTranslations();
                // ★ 后台预加载 1241 个 gameplay root（利用 OnFrame 比游戏资源加载早 ~24 秒的窗口）
                TickPrewarmGameplayResources();
                // 皮肤分帧扫描（每帧几张，不阻塞主线程；空闲时零开销）
                TickSkinScan();
                // 定期自动保存设置（每 5 秒），重进游戏不丢
                if (++_saveTimer >= 300) { _saveTimer = 0; ModSettings.Save(); }
                // 图标分帧导出（制作器资源包，每帧处理几帧渲染）
                if (_iconExporting) TickIconExport(root);
                // 定期把缓冲日志落盘（约每 10 秒）
                if (++_logFlushTimer >= 600) { _logFlushTimer = 0; Bootstrap.FlushLog(); }
                // 自制关卡（阳光豆赌博）：卡槽/波次/种阳光豆随机生成（先于总开关快速路径，模式开启即跑）
                if (ModSettings.CustomLevelActive) CustomLevelTick(root);
                // 全局植物属性覆盖（所有关卡生效，制作器导出的 plant_overrides.txt）
                if (ModSettings.GlobalOverridesEnabled) ApplyGlobalOverrides(root);
                _consoleRoot = root;
                // 游戏原生控制台（CommandManager）：主菜单/任意场景挂载开启（每 30 帧检测，切场景后重新挂）
                if (ModSettings.ConsoleEnabled && ++_consoleTimer % 30 == 0) EnableConsole(root);
                // 音量键轮询兜底（音量- 和 音量+ 任一触发，事件+轮询双保险，防漏按键）
                if (ModSettings.ConsoleEnabled)
                {
                    bool c = Input.IsPhysicalKeyPressed(Godot.Key.Volumedown) || Input.IsPhysicalKeyPressed(Godot.Key.Volumeup);
                    if (c && !_capPrev) ToggleConsoleIfIdle();
                    _capPrev = c;
                }
                // ★★★ 作弊策略强制落地：独立于中继连接 ★★★
                //   同电脑版：原先挂在 NetSession.Tick 末尾，而它开头有 `if (_t == null) return;`，
                //   连接一断作弊锁就失效。所以单独驱动一次（内部 250ms 节流）。
                try { CheatPolicy.Tick(Time.GetTicksMsec()); }
                catch (Exception ex) { LogNetOnce("作弊策略", ex); }
                // ★★★ 联机会话必须放在总开关快速路径【之前】★★★
                //   同电脑版：AnyModActive() 只统计战斗/功能开关，不含任何联机开关。
                //   玩家把作弊全关（或被 CheatPolicy 联机时强制关闭）时它会 return，
                //   NetSession.Tick 停摆 → 不 poll 中继 → 收不到服务器挑战帧 → 永远未鉴权。
                //   症状：连不上服务器、没有延迟、没有房间号、加密未启用。
                //   切场景/加载中也要保持心跳，所以放在 IsSceneLoadingFast 之前。
                if (NetSession.State != NetState.Offline)
                {
                    try { NetSession.Tick(root); }
                    catch (Exception ex) { LogNetOnce("联机会话", ex); }
                }
                if (_pd) _pfNet0 = Time.GetTicksUsec();
                try { NetUI.Tick(root); }
                catch (Exception ex) { LogNetOnce("联机面板", ex); }
                try { NetCursor.Tick(root); }
                catch (Exception ex) { LogNetOnce("联机光标", ex); }
                try { NetMirror.Tick(root); }
                catch (Exception ex) { LogNetOnce("实体镜像", ex); }
                try { NetSun.Tick(); }
                catch (Exception ex) { LogNetOnce("阳光共享", ex); }
                try { NetLevelShare.Tick(); }
                catch (Exception ex) { LogNetOnce("关卡文件", ex); }
                try { NetToast.Tick(root); }
                catch (Exception ex) { LogNetOnce("联机提示", ex); }
                try { NetGate.Tick(root); }
                catch (Exception ex) { LogNetOnce("准备门禁", ex); }
                // ★★★ 对战模式必须放在总开关快速路径【之前】★★★
                //   联机时 CheatPolicy 强制关闭全部修改器开关，AnyModActive() 随即为 false、
                //   本方法提前 return —— 对战代码就永远跑不到。
                //   症状：分边能选（属 NetSession），但标靶僵尸/红线/僵尸卡槽/禁出怪全部无效应。
                try { NetPvp.Tick(root); }
                catch (Exception ex) { LogNetOnce("对战规则", ex); }
                try { PvpOverlay.Tick(root); }
                catch (Exception ex) { LogNetOnce("对战可视化", ex); }
                // 僵尸卡槽（与电脑版一致）
                try { PvpFillZombieTray(); }
                catch (Exception ex) { LogNetOnce("对战卡槽", ex); }
                // ★ 小推车：**持续清理**，不是只删一次 —— 游戏在关卡里会重新生成小推车，
                //   一次性删掉很快被补回来。每 60 帧扫一次场景把 TowerDefenseMower* 清掉。
                if (NetPvp.Active && ++_pvpMowerTimer >= 60)
                {
                    _pvpMowerTimer = 0;
                    try
                    {
                        if (!_pvpMowerLogged)
                        {
                            _pvpMowerLogged = true;
                            string me;
                            if (PvpRemoveAllMowers(out me))
                                Bootstrap.Log("对战：已用 CommandManager.RemoveAllMowers() 删除小推车");
                            else
                                Bootstrap.Log("对战：RemoveAllMowers 失败 —— " + me);
                        }
                        int kn = NetKillMowerNodes();
                        if (kn > 0) { Bootstrap.Log("对战：清除小推车节点 " + kn + " 个"); Bootstrap.FlushLog(); }
                    }
                    catch { }
                }
                try { TickPendingEnter(); } catch { }
                if (_pd) _pfNet1 = Time.GetTicksUsec();
                // 总开关快速路径：所有战斗/功能开关全关 → 零开销退出（电脑版同步）
                if (!ModSettings.AnyModActive()) { if (_pd) PerfAcc(_pfStart, _pfNet0, _pfNet1, 0); return; }
                // 加载/切场景中：跳过所有战斗功能（减少加载界面卡顿）；强制迷雾/强制选卡仍需在关卡加载窗口注入
                if (IsSceneLoadingFast(root))
                {
                    if (ModSettings.ForceFog || ModSettings.CanChooseAll || ModSettings.GloveMode) ApplyForceLevelFeatures(root);
                    return;
                }
                if (ModSettings.InfiniteSun) ApplySun();
                if (ModSettings.InfiniteCoin) ApplyCoin(root);
                if (ModSettings.NoCooldown && ++_ncTimer % 30 == 0) ApplyNoCooldown(root);
                // 图鉴全解：持续设置 plantShowAll（不主动 Init，避免卡顿）；无论图鉴何时打开都生效
                if (ModSettings.AlmanacAll && ++_almanacTimer % 30 == 0) SetAlmanacAllNow(root);
                // 无视紫卡限制：解锁全局功能 + 卡牌解锁。
                // 注意：不能调 AddPlantGridTypeAll（给植物补 PLANT 会让 ProcessPacketPick 走合体分支，
                // FindPlantInCell 要求格子里已有植物 → 空地种不了）。紫卡能单独放靠 CanPacketPlant 恒 true（patcher 注入）。
                if (ModSettings.IgnorePurple && ++_purpleTimer % 60 == 0)
                {
                    UnlockAllFeatures();
                    ForceUnlockPackets(root);
                }
                // 迷雾透视：隐藏 fogNode（降频 30 帧，够快不卡）
                if (ModSettings.FogESP && ++_fogTimer % 30 == 0) ApplyFogESP(root);
                // 清除弹坑（降频 60 帧，弹坑删除不需要太快）
                if (ModSettings.ClearCrater && ++_craterTimer % 60 == 0) ClearCraters(root);
                // 传送带加速送卡 / 种子雨加速掉落：无条件每 30 帧调用一次，
                // 内部按开关状态设置 interval（开=压缩到 0.3，关=恢复原值），避免关闭后仍生效。
                if (++_conveyorTimer % 30 == 0) ApplyConveyorFast(root);
                if (++_rainTimer % 30 == 0) ApplyRainFast(root);
                // 刷出物篡改引擎（老虎机盲盒/种子雨 卡池锁定为目标池）：每 15 帧应用一次
                if (TrickAnyOn
                    && ++_trickTimer % 15 == 0) ApplyTrickFeatures(root);
                // 传送带卡槽篡改：TrickConveyor + 目标池时每 3 帧把传送带上的卡替换成目标池
                if (TrickConveyorOn && ++_trickCvTimer % 3 == 0) AutoTrickConveyorSlots(root);
                // 联机会话/面板已上移到总开关快速路径【之前】执行（见 OnFrame 前半段），
                // 否则关闭全部作弊开关后联机心跳停摆。
                NetCursor.Tick(root);
                // 罐子内物品随机（自动）：进罐子关卡自动随机一次 + 强制显示内容（透视，不用暂停）
                if (ModSettings.VaseRandom && ++_vaseTimer % 20 == 0) AutoRandomizeVases(root);
                // 卡槽卡牌随机 / 篡改（自动）：进关卡把底部种子卡槽/传送带槽里的卡替换成目标卡。
                // SeedBankEveryFrame=true → 每帧尝试（1 帧 1 刷）；false → 每 10 帧。
                if (ModSettings.SeedBankRandom && (ModSettings.SeedBankEveryFrame || ++_sbTimer % 10 == 0)) AutoRandomizeSeedBank(root);
                // 随机环境诊断（任一随机开关开启时每 10 秒报告一次当前罐子/卡槽数量，方便排查）
                if ((ModSettings.VaseRandom || ModSettings.SeedBankRandom) && ++_rndDiagTimer >= 600)
                {
                    _rndDiagTimer = 0;
                    RandomEnvDiag(root);
                }
                // 趣味：僵尸变色 / 跳舞（每 3 帧应用；禁止闪屏模式禁用）
                if (!ModSettings.NoFlicker && (ModSettings.ZombieColor || ModSettings.ZombieDance ||
                    ModSettings.ZombiesFollowMouse)) ApplyZombieFun(root);
                // 子弹跟随鼠标：每帧拉（平滑不卡）
                if (ModSettings.BulletFollowMouse) ApplyBulletMouse(root);
                // 运行时子弹 mod（追踪/随机/自定义）：Spawn 完全原版（不再注入，规避 readonly ref IL 风险曾致攻击卡/没子弹）
                if (BulletModsActive()) ApplyBulletModsRuntime(root);
                // 自动开火（全图锁敌）：放宽 FireComponent 射程，让游戏原生自动开火（独立开关 PlantAutoFire）
                if (ModSettings.PlantAutoFire && ++_autofireTimer % 30 == 0) ApplyPlantAutoFire(root);
                // 战斗增强：攻速 / 血量 / 无敌 / 禁攻击 / 禁移动（降频到每 5 帧，避免每帧全树递归+反射卡顿）
                if (ModSettings.PlantAttackSpeed != 1.0f || ModSettings.ZombieAttackSpeed != 1.0f ||
                    ModSettings.PlantHP != 1.0f || ModSettings.ZombieHP != 1.0f ||
                    ModSettings.PlantInvincible || ModSettings.ZombieInvincible ||
                    ModSettings.PlantNoAttack || ModSettings.ZombieNoAttack || ModSettings.ZombieNoMove ||
                    ModSettings.ChomperFastSwallow ||   // 大嘴花秒吞咽（只开这个也要调 ApplyCombatMods）
                    _plantFireOrig.Count > 0)   // 有残留 fireInterval 修改（植物攻速曾开过）→ 开关全关也要调用恢复
                {
                    if (++_combatTimer % 5 == 0) ApplyCombatMods(root);
                }
                // 手套挪动：改为加载游戏原生手套（featureData["Glove"] 注入，关卡加载时由游戏从 pck 加载 GloveManager 场景）
                // （旧鼠标/触摸模拟已废弃，见 ApplyForceLevelFeatures 的 Glove 分支）
                // 禁止出僵尸：每 15 帧清理场上僵尸。
                // ★ 联机镜像模式下只清【本地波次刷出来的】那一批，保留房主同步过来的真僵尸
                //   （旧写法无条件 RemoveAllZombies 会把镜像僵尸一起删了 → 客机永远没僵尸）。
                if ((ModSettings.NoZombieSpawn || NetMirror.FreezeSpawnActive) && ++_noZombieTimer % 8 == 0)
                    RemoveZombiesForNoSpawn(root);
                // ★ 对战模式的 Tick 已上移到总开关快速路径【之前】，此处不再重复调用
                // 炮类/投手一次发射多枚（数量可调，每 30 帧应用）
                if (ModSettings.CannonMultiShot > 1 && ++_multiTimer % 30 == 0) ApplyCannonMultiShot(root);
                // 全植物全图射程（每 60 帧应用一次）
                if (ModSettings.PlantFullRange && ++_fullRangeTimer % 60 == 0) ApplyPlantFullRange(root);
                // 无限水晶（每 2 秒检查，CrystalNum<10亿 才写档+保存）
                if (ModSettings.CrystalInfinite && ++_crystalTimer % 120 == 0) ApplyCrystalInfinite();
                // 炮类无冷却：玉米加农炮/南瓜炮等 CannonComponent 装填清零（每 15 帧）
                if (ModSettings.CannonNoCooldown && ++_cannonTimer % 15 == 0) ApplyCannonNoCooldown(root);
                // 无视僵尸进家：持续重置失败流程（僵尸进家不判失败，每 5 帧）
                if (ModSettings.IgnoreHouse && ++_houseTimer % 5 == 0) ApplyIgnoreHouse(root);
                // 僵尸碰到警戒线不失败：预置 _triggered=true 阻止 GameFail（每 5 帧）
                if (ModSettings.IgnoreWarningLine && ++_warningTimer % 5 == 0) ApplyIgnoreWarningLine(root);
                // 波次暂停：设官方 CommandManager.debugWavePaused（每帧保持，波次不推进）
                if (ModSettings.WavePaused) ApplyWavePaused();
                // 刷怪倍数：波次变化时把 Spawn Num × 倍数（每 15 帧检查）
                if (ModSettings.SpawnMultiplier > 1 && ++_spawnMultTimer % 15 == 0) ApplySpawnMultiplier();
                // 强制关卡功能（强制迷雾/强制种子雨/强制选卡）：在关卡 feature 创建前（_battleGraphReady=false 窗口）注入 featureData
                // 强制关卡功能（强制迷雾/强制选卡）：在关卡 feature 创建前（_battleGraphReady=false 窗口）注入 featureData。
                // 每帧检测（窗口很短，% 定时会错过导致注入失败）。
                if (ModSettings.ForceFog || ModSettings.CanChooseAll || ModSettings.GloveMode)
                    ApplyForceLevelFeatures(root);
                // 强制种子雨（运行时模拟，必定生效）：每 3 秒随机在地图刷一张随机植物卡掉落
                if (ModSettings.ForceRain && ++_forceRainTimer >= 180)
                {
                    _forceRainTimer = 0;
                    ApplyForceRainRuntime(root);
                }
                // 无视红线：红线（警戒线）区域也能种植物/僵尸（禁掉警戒线检测节点 + 种植本就无限制）
                if (ModSettings.IgnoreRedLine && ++_redLineTimer % 10 == 0) ApplyIgnoreRedLine(root);
                // Q弹/果冻模式：植物+僵尸向下压扁/向上拉伸 + 果冻左右摇摆（缓存后每帧设置，流畅）
                if (ModSettings.PlantSquash || ModSettings.JellyMode) ApplyPlantSquash(root);
                // 飞贼/小丑组合技（秒偷/无视保护伞/秒炸+瞬移/吸附鼠标/偷后生成小丑；内部降频）
                if (ModSettings.BungiFastGrab || ModSettings.BungiIgnoreUmbrella || ModSettings.JackboxFastBomb ||
                    ModSettings.ZombiesFollowMouse || ModSettings.BungiSpawnJackbox)
                    ApplyBungiJackboxCheats(root);
                ApplyBungiAutoGrab(root);   // 全图生成的飞贼自动抓取（waitGrab=true）
                // 禁止攻击：攻击动画/特效还播——暂停相关角色动画（显示层修复）
                if (ModSettings.ZombieNoAttack || ModSettings.PlantNoAttack) ApplyNoAttackAnimLock(root);
                // 联机功能已移除
                if (_pd) PerfAcc(_pfStart, _pfNet0, _pfNet1, Time.GetTicksUsec());
            }
            catch (System.Exception ex)
            {
                // 诊断：OnFrame 内任何异常都会导致后续功能全部失效（总开关被吞），必须暴露出来
                // 优化：只记录首次（含完整堆栈+Inner），避免每帧重复抛异常写盘拖慢游戏
                if (!_onFrameErrorLogged)
                {
                    _onFrameErrorLogged = true;
                    Bootstrap.Log("OnFrame 异常(首次): " + ex.GetType().Name + ": " + ex.Message);
                    Bootstrap.Log("  " + (ex.StackTrace != null ? ex.StackTrace.Replace("\n", "\n  ") : ""));
                    if (ex.InnerException != null)
                        Bootstrap.Log("  Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                    // 异常时立即落盘（崩溃前保留尽可能多的状态）
                    try { Bootstrap.FlushLog(); } catch { }
                }
            }
        }

        // ================= 性能诊断（ModSettings.PerfDiag）=================
        // 分段累计 OnFrame 耗时，每 10 秒输出一次均值（单位：微秒）。
        // 分段含义：准备 = 日志/设置/皮肤/控制台等前置；联机 = 9 个 Net Tick；功能 = 战斗功能段。
        // 单帧总耗时超过 ~10ms 就足以把 60fps 拖到 30fps。
        static long _pfPre, _pfNet, _pfFeat;
        static int _pfFrames;
        static ulong _pfWindowMs;

        static void PerfAcc(ulong start, ulong net0, ulong net1, ulong end)
        {
            try
            {
                if (net0 > start) _pfPre += (long)(net0 - start);
                if (net1 > net0) _pfNet += (long)(net1 - net0);
                if (end > net1) _pfFeat += (long)(end - net1);
                _pfFrames++;
                ulong now = Time.GetTicksMsec();
                if (_pfWindowMs == 0) { _pfWindowMs = now; Bootstrap.Log("性能诊断已启用（10 秒后输出均值）"); return; }
                if (now - _pfWindowMs < 10_000 || _pfFrames <= 0) return;

                long tot = _pfPre + _pfNet + _pfFeat;
                Bootstrap.Log("性能(均值us): 准备=" + (_pfPre / _pfFrames) + " 联机=" + (_pfNet / _pfFrames) +
                              " 功能=" + (_pfFeat / _pfFrames) + " 合计=" + (tot / _pfFrames) + "  帧数=" + _pfFrames);
                _pfPre = _pfNet = _pfFeat = 0;
                _pfFrames = 0;
                _pfWindowMs = now;
            }
            catch { }
        }

        // ================= 加载界面卡顿修复（patcher 注入点）=================
        // ★ 此方法必须存在且为 public static 无参：
        //   patcher 把它的调用插到 ResourceManager.LoadBuiltInRoot 开头，每 8 个资源强制绘制一帧。
        //   加载内置资源是全主线程同步的，一次 GD.Load 大量资源会占满主循环、界面完全不刷新
        //   （表现就是“每次加载资源都要好久，像卡死”）。
        //   ⚠️ 曾经因为重构时误删此方法，导致注入静默失败、加载界面不再刷新 —— 不要删。
        //   ⚙️ 帧率/开销权衡：加载时主循环被同步 GD.Load 占满，完全不渲染，只能靠 ForceDraw 手动出一帧，
        //   所以【加载期间的帧率 = 本方法的触发频率】。实测 1600 资源 ÷ 间隔8 ≈ 200 帧 ÷ 26 秒 ≈ 7.7 FPS
        //   （用户反馈「刚加载时只有几帧」就是这个）。间隔改小→帧率高但每帧同步绘制有开销→加载变慢。
        //   内部会统计累计开销并打日志，后续调参看日志里的 avgMs 再定。
        static int _forceDrawCounter;
        static long _fdCount, _fdTicks;
        static long _fdLastReportTick = -1;
        const int ForceDrawInterval = 32;  // 每 N 个资源强制绘制一帧★★ 实测调参结论（2026-09-13）★★
        //   单次 RenderingServer.ForceDraw() = 14.4ms（同步等 GPU，约等于一帧）！实测：
        //     间隔4  → 440次 → 额外 6.3s（fullWallMs 26.3→31.4s）
        //     间隔8  → 220次 → 额外 3.2s（7.7 FPS）
        //     间隔16 → 110次 → 额外 1.6s（约 4 FPS）← 当前【用户选择速度优先】
        //   即【加载帧率与加载速度直接冲突】。用户明确「只要速度快」——
        //   且加载界面已有准确文案（RegisterLoadingTranslations），低帧率也不会被当成卡死。
        public static void ForceDrawThrottled()
        {
            try
            {
                if (++_forceDrawCounter % ForceDrawInterval != 0) return;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { RenderingServer.ForceDraw(); } catch { }
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                _fdTicks += dt;
                _fdCount++;
                // 每 40 次报一次（加载期间约 10 条），便于评估开销
                if (_fdCount % 40 == 0)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    // 只在距上次报告超过 300ms 时打，避免日志本身拖慢
                    if (_fdLastReportTick < 0 || (now - _fdLastReportTick) * 1000 / System.Diagnostics.Stopwatch.Frequency > 300)
                    {
                        _fdLastReportTick = now;
                        double avgMs = _fdTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _fdCount;
                        Bootstrap.Log("[加载绘制] 次数=" + _fdCount + " 平均单次=" + avgMs.ToString("F2") + "ms 累计=" +
                            (_fdTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + "ms");
                    }
                }
            }
            catch { }
        }

        // ================= 角色场景懒加载（patcher 注入 2 处）=================
        // ★★ 目标：启动时【不】加载 563 个角色场景（原本占启动加载的 62%、约 11~17 秒），
        //   改为「GetCharacterScene(name) 真正被用到时才加载那一个」。
        //
        // 注入点：
        //   ① ResourceManager.LoadUniquePackedSceneRoots(paths, category) 开头
        //      → 传 (paths, category)，category=="CharacterScene" 时返回占位字典（见下文）
        //   ② ResourceManager.GetCharacterScene(string) 开头
        //      → 传 characterName，MOD 确保该角色真身已加载并写回字典，原逻辑随后就能查到
        //
        // 为什么可行：GetCharacterScene 的实现只是「查 TOWERDEFENSE_CHARCATERS 字典，查不到抛
        //   KeyNotFoundException」，本身不做加载。所以只要在它查字典【之前】把真身塞进字典即可。
        //
        // 为什么必须用占位符而不是返回空字典：加载完还有两道校验会拦——
        //   BindResourceNames 要求 roots.ScenePathByCharacter 的每个 path 都能在返回字典里找到
        //   且 IsInstanceValid；ValidateCompletePublication 还要求 count 相等。
        //   所以返回「path → new PackedScene()」的等长字典满足校验，真身在 ② 处替换。
        // ⚠️ 风险点：若某处在【不经过 GetCharacterScene】的情况下直接用字典里的值，
        //   会拿到空 PackedScene。实测这套流程走通后再观察是否有此情况。
        static bool _lazyCharOn;
        static readonly System.Collections.Generic.HashSet<string> _lazyPending =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        static readonly System.Collections.Generic.Dictionary<string, string> _lazyNameToPath =
            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>反射拿到 ResourceManager 单例。</summary>
        static object GetRmInstance()
        {
            try
            {
                var t = System.Type.GetType("ResourceManager") ?? FindTypeByName("ResourceManager");
                if (t == null) return null;
                var p = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (p != null) return p.GetValue(null);
                var f = t.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                return f?.GetValue(null);
            }
            catch { return null; }
        }

        static System.Type FindTypeByName(string name)
        {
            try
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(name, false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>反射读取 ResourceManager 成员（属性优先，其次字段）。</summary>
        static object RmMember(object rm, string name)
        {
            try
            {
                if (rm == null) return null;
                var t = rm.GetType();
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                if (p != null) return p.GetValue(rm);
                var f = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                return f?.GetValue(rm);
            }
            catch { return null; }
        }

        /// <summary>注入点①：LoadUniquePackedSceneRoots 开头调用。
        /// 返回非 null = 用它替代真实加载（懒加载生效）；返回 null = 走原本流程。
        /// ⚠️ 方法被调用两次会重建字典，务必用 _lazyCharOn 幂等。</summary>
        public static System.Collections.Generic.Dictionary<string, Resource> TryLazyCharacterSceneRoots(
            System.Collections.Generic.IReadOnlyList<string> paths, string category)
        {
            try
            {
                // ★★★ 2026-09-13 实测：开了会「加载失败」已回滚 ★★★
                //   失败点：ValidateCompletePublication 末尾的 PacketBank 闭包校验会用【角色名】去查
                //   characterScenes / sprites（`characterScenes.ContainsKey(value3)`，value3 来自
                //   roots.CharacterNameByPacket），而我们返回的是按 path 键的占位字典，
                //   BindPackedSceneNames 虽然能用 path 绑定成功，但后续这道闭包校验通不过 →
                //   `Expanded PacketBank candidate is not fully resident` → 加载失败。
                //   要想做成，得同时把角色名维度的字典一并填好（而不是只填 path 维度），
                //   并重新校对 sprites/characterScenes 两个维度的 key 语义，风险偏高，暂不推进。
                return null;

                if (category != "CharacterScene") return null;
                if (paths == null || paths.Count == 0) return null;

                // 首次进入：建立「角色名 → 场景路径」映射
                // ★★ 不能反射 _characterScenePaths！它在【加载之后】才赋值（ResourceManager.cs 里
                //   `_characterScenePaths = new Dictionary(coreSnapshot.RegistryRoots.ScenePathByCharacter,...)`
                //   在 ApplyCoreSnapshot 里），加载时读它必然是空的（实测就踩了这个坑）。
                //   ✅ 改读游戏自己的角色注册表 res://Asset/Config/Character/CharacterResource.json：
                //   563 个角色，每个条目有 Scene = "uid://xxx"，用 ResourceUid.UidToPath 转成 res:// 路径。
                if (!_lazyCharOn)
                {
                    _lazyCharOn = true;
                    try
                    {
                        var fa = FileAccess.Open("res://Asset/Config/Character/CharacterResource.json", FileAccess.ModeFlags.Read);
                        if (fa != null)
                        {
                            string txt = fa.GetAsText();
                            fa.Close();
                            var json = new Json();
                            if (json.Parse(txt) == Error.Ok)
                            {
                                foreach (var kv in json.Data.AsGodotDictionary())
                                {
                                    string name = kv.Key.AsString();
                                    if (string.IsNullOrEmpty(name)) continue;
                                    var body = kv.Value.AsGodotDictionary();
                                    if (body == null || !body.ContainsKey("Scene")) continue;
                                    string uid = body["Scene"].AsString();
                                    if (string.IsNullOrEmpty(uid)) continue;
                                    string path = uid;
                                    if (uid.StartsWith("uid://"))
                                    {
                                        try { path = ResourceUid.UidToPath(uid); } catch { }
                                    }
                                    if (string.IsNullOrEmpty(path)) continue;
                                    _lazyNameToPath[name] = path;
                                    _lazyPending.Add(name);
                                }
                            }
                        }
                    }
                    catch (System.Exception ex) { Bootstrap.Log("[角色懒加载] 读注册表异常: " + ex.Message); }
                    if (_lazyNameToPath.Count == 0)
                    {
                        _lazyCharOn = false;      // 读不到 → 放弃懒加载，走原流程，保证功能
                        Bootstrap.Log("[角色懒加载] 取不到角色注册表，已放弃（走原本全量加载）");
                        return null;
                    }
                    Bootstrap.Log("[角色懒加载] 已启用，跳过启动加载的角色数=" + _lazyNameToPath.Count);
                }

                // 构造等长占位字典（满足 BindResourceNames / ValidateCompletePublication 的校验）
                var d = new System.Collections.Generic.Dictionary<string, Resource>(System.StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < paths.Count; i++)
                {
                    string p = paths[i];
                    if (string.IsNullOrEmpty(p)) continue;
                    d[p] = new PackedScene();
                }
                return d;
            }
            catch (System.Exception ex)
            {
                _lazyCharOn = false;
                Bootstrap.Log("[角色懒加载] 构造占位失败，回退全量加载: " + ex.Message);
                return null;
            }
        }

        /// <summary>注入点②：GetCharacterScene(name) 开头调用——确保该角色真身已加载并写回字典。</summary>
        public static void EnsureCharacterSceneLoaded(string characterName)
        {
            try
            {
                if (!_lazyCharOn || string.IsNullOrEmpty(characterName)) return;
                if (!_lazyPending.Contains(characterName)) return;
                string path;
                if (!_lazyNameToPath.TryGetValue(characterName, out path) || string.IsNullOrEmpty(path)) return;

                var res = GD.Load(path) as PackedScene;
                if (res == null) { Bootstrap.Log("[角色懒加载] 加载失败: " + characterName + " @ " + path); return; }

                var rm = GetRmInstance();
                var dict = RmMember(rm, "TOWERDEFENSE_CHARCATERS") as System.Collections.IDictionary;
                if (dict == null) return;
                dict[characterName] = res;
                _lazyPending.Remove(characterName);
            }
            catch (System.Exception ex) { Bootstrap.Log("[角色懒加载] 异常（该角色回退为空场景）: " + ex.Message); }
        }

        // ================= 加载加速：后台预加载（OnFrame 调用，patcher 不需要注入）=================
        // ★ 原理：游戏本体的 ResourceManager 是「串行 for + 主线程同步 GD.Load」，1241 个 root
        //   （563 角色场景 + 673 packet + 5 sprite，合计才 ~2MB）每个要 ~14.5ms —— 全是 Godot
        //   资源管线的固定开销（路径解析/缓存查找/格式检测/对象构造/依赖加载），累加 17+ 秒。
        //
        //   本方法利用「MOD 的 OnFrame 比游戏资源加载早约 24 秒就开始跑」这个时间窗口：
        //   一帧帧地把这些 root 用 ResourceLoader.LoadThreadedRequest 丢给工作线程并行预处理，
        //   等游戏本体开始同步 GD.Load 时资源早已进 ResourceCache → 命中缓存几乎零耗时。
        //
        // ★ 与 2026-09-13 那次失败尝试的关键区别（那次是权限弹窗冤枉了它，但实现也确实有错）：
        //   旧实现是「发起请求后立刻同步 while 轮询等到就绪」→ 阻塞主循环，
        //   而 Godot 的 threaded-load 恰恰需要主循环推进 → 死等到超时，反而更慢。
        //   新实现彻底不等待：发起后立即返回，靠 OnFrame 每帧 pump（与 SceneManager._PhysicsProcess
        //   用 LoadThreadedGetStatus 轮询是同一套官方模式）。
        static bool _pwKick, _pwDone;
        static System.Collections.Generic.List<string> _pwPaths;
        static int _pwReqCursor, _pwLoaded, _pwFailed;
        static readonly System.Collections.Generic.List<Resource> _pwKeepAlive = new System.Collections.Generic.List<Resource>(1400);
        static ulong _pwT0;
        const int PwReqPerFrame = 32;      // 每帧发起多少个加载请求（避免一次性挤爆内存）
        // ⚙️ useSubThreads 实测（2026-09-13）：true（工作线程并行）→ 1241 个耗时 30027ms，
        //   比游戏本体同步加载（~17s）还慢！Godot 的 ResourceCache 带锁，多线程争抢反而拖慢。
        //   故改用 false（在 Godot 主循环里分时间片处理，无锁竞争）。

        public static void TickPrewarmGameplayResources()
        {
            try
            {
                if (!_pwKick) { _pwKick = true; PwKickoff(); }
                if (_pwDone || _pwPaths == null || _pwPaths.Count == 0) return;
                PwPump();
            }
            catch (System.Exception ex)
            {
                _pwDone = true;
                Bootstrap.Log("[预加载] 异常，已停止（不影响功能）: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void PwKickoff()
        {
            _pwT0 = Time.GetTicksMsec();
            _pwPaths = new System.Collections.Generic.List<string>(1400);
            try
            {
                var fa = FileAccess.Open("res://Asset/Config/Character/FullGameplayResources.json", FileAccess.ModeFlags.Read);
                if (fa == null) { Bootstrap.Log("[预加载] 打不开 manifest，跳过"); _pwDone = true; return; }
                string txt = fa.GetAsText();
                fa.Close();
                var json = new Json();
                if (json.Parse(txt) != Error.Ok) { Bootstrap.Log("[预加载] manifest 解析失败，跳过"); _pwDone = true; return; }
                var d = json.Data.AsGodotDictionary();
                // ★★ 只预加载【角色场景】—— 实测关键结论（2026-09-13）★★
                //   窗口只有 ~23 秒（OnFrame 开始 → 游戏本体开始同步加载），而预加载吞吐有限：
                //   全量 1241 个（角色563+packet673+sprite5）要 20~30 秒，**跑不完** → 白白浪费窗口，
                //   实测 fullWallMs 卡在 17.7s 上不去。而角色场景单项就占 62%（~17s），
                //   集中只加载它 → 563 个约 13~14 秒能在窗口内跑完 → 全部命中缓存，收益最大。
                //   （packet 总共才 1.6~3s，窗口不够时优先级最低，交给游戏本体自己同步加载。）
                var keys = new string[] { "characterSceneRoots" };
                foreach (var key in keys)
                {
                    if (!d.ContainsKey(key)) continue;
                    try
                    {
                        foreach (var v in d[key].AsGodotArray())
                        {
                            string s = v.AsString();
                            if (!string.IsNullOrEmpty(s)) _pwPaths.Add(s);
                        }
                    }
                    catch { }
                }
            }
            catch (System.Exception ex)
            {
                Bootstrap.Log("[预加载] 读取异常，跳过: " + ex.Message);
                _pwDone = true;
                return;
            }
            if (_pwPaths.Count == 0) { _pwDone = true; return; }
            Bootstrap.Log("[预加载] 开始，待处理 root=" + _pwPaths.Count);
        }

        static void PwPump()
        {
            // 阶段 1：分批发起线程化加载请求（立即返回，不阻塞）
            int reqEnd = _pwReqCursor + PwReqPerFrame;
            if (reqEnd > _pwPaths.Count) reqEnd = _pwPaths.Count;
            for (; _pwReqCursor < reqEnd; _pwReqCursor++)
            {
                try { ResourceLoader.LoadThreadedRequest(_pwPaths[_pwReqCursor], "", false); } catch { }
            }
            if (_pwReqCursor < _pwPaths.Count) return;   // 还没发完，先不查状态

            // 阶段 2：全部请求已发出，逐帧检查状态并把完成的资源取回（进 ResourceCache）
            int pending = 0;
            for (int i = _pwPaths.Count - 1; i >= 0; i--)
            {
                ResourceLoader.ThreadLoadStatus st;
                try { st = ResourceLoader.LoadThreadedGetStatus(_pwPaths[i]); }
                catch { _pwPaths.RemoveAt(i); continue; }
                if (st == ResourceLoader.ThreadLoadStatus.InProgress) { pending++; continue; }
                if (st == ResourceLoader.ThreadLoadStatus.Loaded)
                {
                    // ★★ 必须保存引用！Godot 资源是引用计数的：LoadThreadedGet 返回后若无人持有，
                    //   资源会被释放，游戏再 GD.Load 时就又得重新加载一遍（预加载白做）。
                    try
                    {
                        var res = ResourceLoader.LoadThreadedGet(_pwPaths[i]);
                        if (res != null) { _pwKeepAlive.Add(res); _pwLoaded++; }
                    }
                    catch { }
                }
                else _pwFailed++;
                _pwPaths.RemoveAt(i);      // 已处理，移除（列表越跑越小，遍历成本递减）
            }
            if (pending == 0)
            {
                _pwDone = true;
                Bootstrap.Log("[预加载] 完成 成功=" + _pwLoaded + " 失败=" + _pwFailed +
                    " 耗时=" + (Time.GetTicksMsec() - _pwT0) + "ms");
            }
        }

        /// <summary>补全加载界面的步骤文案（patcher 不需要注入，OnFrame 首帧调用一次）。
        /// 背景：Loading.cs 用 TranslationServer.Translate(stepName) 把内部步骤名翻成中文。
        /// 游戏自带的翻译表只覆盖了 LOAD_LEVEL 等早期步骤，**缺 LOAD_CHARACTER_SCENE_ROOTS** ——
        /// 而它正是最耗时的那一步（563 个角色场景 ~17 秒，占整个资源加载的 73%）。
        /// 缺翻译时 Godot 会原样返回 key，界面文案就停在「加载关卡: xxx 7/7」不动，
        /// 看起来完全像卡死（实测用户就是这么反馈的）。这里把缺的文案注册进去。
        /// ⚠️ 不影响性能，纯文案。</summary>
        static bool _loadingTrDone;
        public static void RegisterLoadingTranslations()
        {
            if (_loadingTrDone) return;
            _loadingTrDone = true;
            try
            {
                var t = new Translation();
                t.Locale = "zh_CN";
                void M(string k, string v) { try { t.AddMessage(k, v); } catch { } }
                M("LOAD_STARTUP", "正在启动");
                M("LOAD_MAP", "正在加载地图");
                M("LOAD_BGM", "正在索引背景音乐");
                M("LOAD_PROJECTILE", "正在加载子弹配置");
                M("LOAD_ARMOR", "正在加载护甲配置");
                M("LOAD_AUDIO", "正在加载音频");
                M("LOAD_CHARACTER_INDEX", "正在建立角色索引");
                M("LOAD_TALK", "正在加载对话");
                M("LOAD_TUTORIAL", "正在加载教程");
                M("LOAD_PACKETBANK", "正在加载卡包");
                M("LOAD_COLLECTABLE", "正在加载图鉴");
                M("LOAD_SHOVEL", "正在加载铲子");
                M("LOAD_MOWER", "正在加载小推车");
                M("LOAD_SHOP", "正在加载商店");
                M("LOAD_LEVEL", "正在加载关卡");
                // ★ 最关键的一条：这是耗时最长的一步（563 个角色场景）
                M("LOAD_CHARACTER_SCENE_ROOTS", "正在加载角色资源");
                M("LOADING_FAILED", "加载失败");
                TranslationServer.AddTranslation(t);
                Bootstrap.Log("[加载文案] 已注册缺失的步骤翻译（含 LOAD_CHARACTER_SCENE_ROOTS）");
                // ★ 主动刷新一次界面文案！
                //   因为 Loading 场景在【MOD 还没跑之前】就调了 UpdateLoadingStepText("LOAD_STARTUP")，
                //   那时翻译还没注册（实测截图：开头那 15 秒一直显示英文 LOAD_STARTUP）。
                //   而之后要等 OnLoadPercentage 触发才会重算文案，初始化期间不会触发 → 一直英文。
                //   所以这里注册完翻译后手动再刷一次（反射调 Loading.UpdateLoadingStepText）。
                try
                {
                    var tree = Engine.GetMainLoop() as SceneTree;
                    var cs = tree != null ? tree.CurrentScene : null;
                    if (cs != null && GodotObject.IsInstanceValid(cs))
                    {
                        var mi = cs.GetType().GetMethod("UpdateLoadingStepText",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (mi != null)
                        {
                            var ps = mi.GetParameters();
                            object[] argv = new object[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                                argv[i] = (ps[i].ParameterType == typeof(int)) ? (object)0 : (ps[i].ParameterType == typeof(string) ? (object)"LOAD_STARTUP" : null);
                            mi.Invoke(cs, argv);
                        }
                    }
                }
                catch { }
            }
            catch (System.Exception ex) { Bootstrap.Log("[加载文案] 注册失败（不影响功能）: " + ex.Message); }
        }

        // ================= 加载加速：线程化预热（patcher 注入点）=================
        // ★ 此方法必须存在且签名精确为 public static void PrewarmResourceRoots(IReadOnlyList<string>)：
        //   patcher 把它的调用插到 ResourceManager.LoadUniqueResourceRoots 开头（直接传第 1 个参数 paths）。
        //   ⚠️ 不要删、不要改签名 —— 删了注入会静默失败（同 ForceDrawThrottled 的教训）。
        //
        // 背景：0.28 的 LoadUniqueResourceRoots 是「串行 for + 主线程同步 GD.Load」，
        //   563 个角色场景 + 673 个 packet + 各 CoreXxx 合计 1600+ 次 GD.Load，
        //   实测每次约 14.5ms（文件本身只有 1~4KB）→ 全是 Godot 资源管线的固定开销
        //   （路径解析/缓存查找/IO/格式检测/对象构造）。串行累加就是 16+ 秒。
        //
        // 做法：先用 ResourceLoader.LoadThreadedRequest 把这些 root 丢给工作线程并行预处理，
        //   轮询到全部就绪后返回；随后的 GD.Load 直接命中 ResourceCache → 几乎零耗时。
        // 注：这是 Godot 官方的大批量加载方案，与 0.27 崩掉的「游戏自己的后台线程通道」不同。
        static bool _prewarmDisabled;          // 一旦出错自动禁用，避免反复拖慢/崩溃
        static ulong _prewarmLastMs;
        public static void PrewarmResourceRoots(System.Collections.Generic.IReadOnlyList<string> paths)
        {
            // ★★★ 2026-09-13 实测失败：线程化预热会【启动闪退】，已停用 ★★★
            //   原因：① ResourceLoader.LoadThreadedRequest(path, "", useSubThreads:true) 让资源在工作线程加载，
            //   碰到渲染/GPU 相关资源直接 native crash —— 就是 0.27 patcher 注释里写过的「后台并行实测崩溃」；
            //   ② 就算改成 useSubThreads:false，在同步循环里轮询等待会阻塞主循环，
            //   Godot 的 threaded-load pump 本身就要靠主循环推进 → 只会死等到超时，反而更慢。
            // 结论：在当前「全主线程同步 GD.Load」架构下无解，保持 no-op（方法签名务必保留，patcher 靠它注入）。
            return;
        }

        // 进战斗标记（诊断闪退：记录每次进入 TowerDefense 战斗场景）
        static string _battleSceneName;
        // ================= 场景类型缓存（性能）=================
        // 原来 LogBattleStart + IsSceneLoadingFast 每帧各调一次 cs.GetType().Name（AOT 下是反射）
        // 再做 IndexOf(OrdinalIgnoreCase) 搜索 —— 单帧合计 4 次反射 + 4 次不区分大小写查找。
        // 但场景类型名只在【切场景】时变，每帧重算是纯浪费（实测这是进关卡掉帧的固定开销之一）。
        // 这里 250ms 缓存一次判定结果；切场景最多迟 250ms 感知，对功能没有可见影响。
        enum SceneKind { Unknown, Other, Menu, Loading, TowerDefense }
        static SceneKind _sceneKind = SceneKind.Unknown;
        static ulong _sceneKindMs;
        static Node _sceneKindNode;

        static SceneKind SceneKindOf(Node root)
        {
            Node cs = null;
            try
            {
                var tree = root != null ? root.GetTree() : null;
                cs = tree != null ? tree.CurrentScene : null;
            }
            catch { }

            if (cs == null || !GodotObject.IsInstanceValid(cs)) { _sceneKind = SceneKind.Unknown; return _sceneKind; }

            ulong now = Time.GetTicksMsec();
            // 同一个场景节点且未超时 → 直接用缓存，跳过反射与字符串搜索
            if (ReferenceEquals(cs, _sceneKindNode) && now - _sceneKindMs < 250) return _sceneKind;

            _sceneKindNode = cs;
            _sceneKindMs = now;
            try
            {
                var n = cs.GetType().Name;
                if (n.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0) _sceneKind = SceneKind.Loading;
                else if (n.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0) _sceneKind = SceneKind.Menu;
                else if (n.IndexOf("TowerDefense", StringComparison.OrdinalIgnoreCase) >= 0) _sceneKind = SceneKind.TowerDefense;
                else _sceneKind = SceneKind.Other;
            }
            catch { _sceneKind = SceneKind.Other; }
            return _sceneKind;
        }

        static void LogBattleStart(Node root)
        {
            try
            {
                // 诊断用：只在真正切进战斗场景时记一次（原来每帧都跑反射 + 字符串搜索）
                if (SceneKindOf(root) != SceneKind.TowerDefense) return;
                var tree = root != null ? root.GetTree() : null;
                var cs = tree != null ? tree.CurrentScene : null;
                if (cs == null || !GodotObject.IsInstanceValid(cs)) return;
                var n = cs.GetType().Name;
                if (n != _battleSceneName)
                {
                    _battleSceneName = n;
                    Bootstrap.Log("进战斗: " + n);
                    try { Bootstrap.FlushLog(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>快速判断是否处于加载/切场景界面（走 250ms 缓存，不再每帧反射）。</summary>
        static bool IsSceneLoadingFast(Node root)
        {
            var k = SceneKindOf(root);
            return k == SceneKind.Loading;
        }

        static bool IsSceneLoadingLegacy(Node root)
        {
            try
            {
                var tree = root.GetTree();
                var cs = tree != null ? tree.CurrentScene : null;
                if (cs != null && GodotObject.IsInstanceValid(cs))
                {
                    var n = cs.GetType().Name;
                    if (n.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (n.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    if (n.IndexOf("TowerDefense", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                }
            }
            catch { }
            return false;
        }

        // ================= 手套挪动（局中移动植物，移植自电脑版） =================
        static bool _gloveDown;
        static Node2D _glovePlant;
        static Vector2 _gloveOffset;
        static System.Reflection.MethodInfo _gloveMapGridPos;

        static void ApplyGlove(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                bool down = Input.IsMouseButtonPressed(MouseButton.Left);
                if (down && !_gloveDown)
                {
                    if (_glovePlant == null || !GodotObject.IsInstanceValid(_glovePlant))
                    {
                        _glovePlant = FindPlantAtMouse(root);
                        if (_glovePlant != null)
                            _gloveOffset = _glovePlant.GlobalPosition - _glovePlant.GetGlobalMousePosition();
                    }
                    else
                    {
                        PlaceGlove(root);
                    }
                }
                _gloveDown = down;
                if (_glovePlant != null && GodotObject.IsInstanceValid(_glovePlant))
                    _glovePlant.GlobalPosition = _glovePlant.GetGlobalMousePosition() + _gloveOffset;
            }
            catch { }
        }

        static Node2D FindPlantAtMouse(Node root)
        {
            try
            {
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                Node2D best = null;
                float bestD = 80f;
                WalkGloveFind(start, ref best, ref bestD);
                return best;
            }
            catch { return null; }
        }

        static void WalkGloveFind(Node node, ref Node2D best, ref float bestD)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                    {
                        try
                        {
                            if (ESP.GetCamp(n2) == "Plant")
                            {
                                float d = n2.GlobalPosition.DistanceTo(n2.GetGlobalMousePosition());
                                if (d < bestD) { bestD = d; best = n2; }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                WalkGloveFind(child, ref best, ref bestD);
            }
        }

        static void PlaceGlove(Node root)
        {
            try
            {
                var plant = _glovePlant;
                if (plant == null || !GodotObject.IsInstanceValid(plant)) { _glovePlant = null; return; }
                Vector2 pos = plant.GetGlobalMousePosition();
                var tdm = GetTdmInstance();
                if (tdm != null)
                {
                    try
                    {
                        if (_gloveMapGridPos == null)
                            _gloveMapGridPos = tdm.GetType().GetMethod("GetMapGridPos", new Type[] { typeof(Vector2) });
                        if (_gloveMapGridPos != null && _gloveMapGridPos.Invoke(tdm, new object[] { pos }) is Vector2I gridPos)
                        {
                            if (_getMapCellPlantPos == null)
                                _getMapCellPlantPos = tdm.GetType().GetMethod("GetMapCellPlantPos", new Type[] { typeof(Vector2I) });
                            if (_getMapCellPlantPos != null)
                                pos = (Vector2)_getMapCellPlantPos.Invoke(null, new object[] { gridPos });
                            TrySetPlantGrid(plant, gridPos);
                        }
                    }
                    catch { }
                }
                plant.GlobalPosition = pos;
                // 同步逻辑位置缓存：游戏用 physicsFrame 缓存位置，不更新 SetLogicalGlobalPosition 会被拉回原位
                try
                {
                    var setM = plant.GetType().GetMethod("SetLogicalGlobalPosition", new Type[] { typeof(Vector2) });
                    if (setM != null) setM.Invoke(plant, new object[] { pos });
                }
                catch { }
                _glovePlant = null;
            }
            catch { _glovePlant = null; }
        }

        static void TrySetPlantGrid(Node2D plant, Vector2I gridPos)
        {
            try
            {
                var t = plant.GetType();
                // 读旧格子（触发 OnGridPositionChanged 通知用）
                Vector2I oldGrid = default;
                try
                {
                    var gf = t.GetField("_gridPos", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (gf != null && gf.FieldType == typeof(Vector2I)) oldGrid = (Vector2I)gf.GetValue(plant);
                }
                catch { }
                // _gridPos 是角色/植物的真实格子字段（原版用它在物理帧同步位置），必须放最前
                foreach (var name in new[] { "_gridPos", "gridPos", "theGridPos", "cell", "gridPosition", "theRow", "theCol", "row", "col", "_row", "_col" })
                {
                    try
                    {
                        var f = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (f != null)
                        {
                            if (f.FieldType == typeof(Vector2I)) f.SetValue(plant, gridPos);
                            else if (f.FieldType == typeof(int))
                            {
                                if (name.Contains("ow") || name.Contains("Row") || name.Contains("row")) f.SetValue(plant, gridPos.Y);
                                else f.SetValue(plant, gridPos.X);
                            }
                        }
                    }
                    catch { }
                }
                // 触发游戏官方的格子变化通知（更新格子注册表 + 地面高度组件），让游戏认可新格子（否则原生层按旧行把植物拉回）
                if (oldGrid != gridPos)
                {
                    try
                    {
                        var om = t.GetMethod("OnGridPositionChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        if (om != null) om.Invoke(plant, new object[] { oldGrid, gridPos });
                    }
                    catch { }
                }
                // 取消原生 cell-move 补间动画干扰（防止游戏把位置 tween 回旧格）
                try
                {
                    var cm = t.GetMethod("CancelCellMoveTween", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (cm != null) cm.Invoke(plant, null);
                }
                catch { }
            }
            catch { }
        }

        static System.Random _bulletRand = new System.Random();
        static int _mouseTimer;
        static int _autofireTimer;

        /// <summary>子弹跟随鼠标：把 BulletField._data 数组里活动子弹的 pos 拉向鼠标。
        /// 注意：子弹不是节点，是 BulletData[] 结构数组；改渲染节点位置会被游戏每帧覆盖。
        /// 缓存 BulletField 引用避免每帧遍历全树。</summary>
        static GodotObject _cachedBulletField;
        static int _bfFindTimer;
        static void ApplyBulletMouse(Node root)
        {
            try
            {
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;
                if (_cachedBulletField == null || !GodotObject.IsInstanceValid(_cachedBulletField))
                {
                    if (++_bfFindTimer % 30 != 0) return;
                    _cachedBulletField = FindBulletField(tree.Root);
                    if (_cachedBulletField == null) return;
                }
                MoveBulletsToMouse(_cachedBulletField);
            }
            catch { }
        }

        static GodotObject FindBulletField(Node node)
        {
            try
            {
                foreach (var child in node.GetChildren(true))
                {
                    if (child == null || !GodotObject.IsInstanceValid(child)) continue;
                    if (child.GetType().Name == "BulletField") return child;
                    var r = FindBulletField(child);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>运行时子弹 mod（Spawn 完全原版——不再注入，规避 readonly ref IL 风险曾致攻击卡/没子弹）。
        /// 每 2 帧遍历活动子弹：追踪开→全部追踪；跟随鼠标开→追踪失效；关闭→保持原版（追踪植物追踪、普通不追踪）。
        /// 随机/自定义 config 每 60 帧重摇一次（避免每帧抖动/弹道异常）。</summary>
        static int _bfRunTimer;
        static int _bfRerollTimer;
        static bool _bfFindDiag, _bfFoundDiag, _bfProcDiag, _bfRandomApiDiag, _bfRandomFailDiag;
        static void ApplyBulletModsRuntime(Node root)
        {
            if (++_bfRunTimer % 2 != 0) return;
            try
            {
                if (_cachedBulletField == null || !GodotObject.IsInstanceValid(_cachedBulletField))
                {
                    if (++_bfFindTimer % 30 != 0) return;
                    var tree = root != null ? root.GetTree() : null;
                    _cachedBulletField = tree != null && tree.Root != null ? FindBulletField(tree.Root) : null;
                    if (_cachedBulletField == null)
                    {
                        if (!_bfFindDiag) { _bfFindDiag = true; Bootstrap.Log("子弹mod: 未找到 BulletField（随机/追踪不会生效）"); }
                        return;
                    }
                    if (!_bfFoundDiag) { _bfFoundDiag = true; Bootstrap.Log("子弹mod: 找到 BulletField 类型=" + _cachedBulletField.GetType().Name); }
                }
                var bf = _cachedBulletField;
                if (_bfDataField == null)
                {
                    var t = bf.GetType();
                    _bfDataField = t.GetField("_data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _bfCountField = t.GetField("_activeCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _bfIdxField = t.GetField("_activeIndices", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_bfDataField == null) return;
                }
                var arr = _bfDataField.GetValue(bf) as System.Array;
                if (arr == null) return;
                if (_lastAppliedKey == null || _lastAppliedKey.Length != arr.Length)
                    _lastAppliedKey = new string[arr.Length];   // 跟踪每颗子弹是否已随机（基于 config.saveKey，idx 复用也可靠）
                int count = 0;
                if (_bfCountField != null) { try { count = (int)_bfCountField.GetValue(bf); } catch { } }
                var idxs = _bfIdxField != null ? _bfIdxField.GetValue(bf) as int[] : null;
                int loop = (idxs != null) ? Math.Min(count, idxs.Length) : arr.Length;
                _bfRerollCounter++;    // 每 2 帧处理周期 +1（随机失败重试的时钟）
                bool reroll = false;   // 随机子弹：新子弹 1 帧内随机一次，随机过的飞行中不再重摇（按需求）
                for (int i = 0; i < loop; i++)
                {
                    int idx = (idxs != null) ? idxs[i] : i;
                    if (idx < 0 || idx >= arr.Length) continue;
                    SetBulletRuntime(arr, idx, reroll);
                }
            }
            catch { }
        }

        /// <summary>设置单颗活动子弹的运行时 mod（反射操作 struct 数组，装箱/写回）。</summary>
        static void SetBulletRuntime(System.Array arr, int idx, bool reroll)
        {
            try
            {
                var boxed = arr.GetValue(idx);
                if (boxed == null) return;
                var bt = boxed.GetType();
                var af = bt.GetField("active", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (af == null) return;
                bool active = (bool)af.GetValue(boxed);
                // 子弹死亡/未激活：重置"已随机"标记，同 idx 复活即视为新子弹（发射出来立即随机种类）
                if (!active)
                {
                    if (_lastAppliedKey != null && idx >= 0 && idx < _lastAppliedKey.Length) _lastAppliedKey[idx] = null;
                    return;
                }
                if (!_bfProcDiag)
                {
                    _bfProcDiag = true;
                    Bootstrap.Log("子弹mod: 处理活动子弹 idx=" + idx + " 追踪=" + ModSettings.BulletTrack + " 随机=" + ModSettings.BulletRandom + " 跟随鼠标=" + ModSettings.BulletFollowMouse + " 自定义=" + (ModSettings.BulletType.Length > 0));
                }
                // 随机/自定义种类：基于 config.saveKey 判断是否已随机（idx 复用/子弹池满也可靠）
                // 已随机锁定（当前种类==上次应用种类）→ 跳过；失败 → RETRY@计数 每 REROLL_GAP 周期重试
                bool changed = ApplyTrackRuntime(boxed, bt);
                var cf = bt.GetField("config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cf != null)
                {
                    object curCfg = cf.GetValue(boxed);
                    string curKey = GetCfgKey(curCfg);
                    string prev = (_lastAppliedKey != null && idx >= 0 && idx < _lastAppliedKey.Length) ? _lastAppliedKey[idx] : null;
                    bool needRandom = true;
                    if (prev != null)
                    {
                        if (prev.StartsWith("RETRY@"))
                        {
                            int t = -1;
                            try { t = int.Parse(prev.Substring(6)); } catch { }
                            needRandom = t < 0 || (_bfRerollCounter - t) >= REROLL_GAP;
                        }
                        else if (curKey == prev) needRandom = false;   // 当前就是上次随机后的种类 → 锁定，不再重摇
                    }
                    if (needRandom)
                    {
                        // 优先用游戏官方 ChangeBulletData 完整换种类（外观+行为+大小+伤害），
                        // 直接改 config 字段只影响大小（hitBoxScale）外观不变（staticAtlasEntryIndex 是 Spawn 时定的）
                        object newCfg = RandomProjectileConfig(curCfg);
                        if (!ReferenceEquals(newCfg, curCfg))
                        {
                            // 读取发射角色（ChangeBulletData 必须传非 null character，否则直接返回 -1 永不生效）
                            object ch = null;
                            try
                            {
                                var fc = bt.GetField("fireCharacter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (fc != null) ch = fc.GetValue(boxed);
                            }
                            catch { }
                            if (TryChangeBulletViaApi(idx, newCfg, cf.FieldType, ch))
                            {
                                if (!_bfRandomApiDiag) { _bfRandomApiDiag = true; Bootstrap.Log("子弹mod: 随机/自定义换种类API成功"); }
                                if (_lastAppliedKey != null && idx >= 0 && idx < _lastAppliedKey.Length)
                                    _lastAppliedKey[idx] = GetCfgKey(newCfg) ?? curKey ?? "";
                                // 官方 API 已完整换种类并内部改了 _data[idx]：重新读最新快照，补追踪设置后写回
                                boxed = arr.GetValue(idx);
                                if (boxed == null) return;
                                bt = boxed.GetType();
                                ApplyTrackRuntime(boxed, bt);
                                arr.SetValue(boxed, idx);
                                return;
                            }
                            if (!_bfRandomFailDiag) { _bfRandomFailDiag = true; Bootstrap.Log("子弹mod: 换种类API失败（回退只改大小），每" + REROLL_GAP + "周期重试"); }
                            // API 不可用/被拒：回退直接改 config 字段（保底，至少大小/速度生效）
                            cf.SetValue(boxed, newCfg);
                            changed = true;
                            if (_lastAppliedKey != null && idx >= 0 && idx < _lastAppliedKey.Length)
                                _lastAppliedKey[idx] = "RETRY@" + _bfRerollCounter;   // 失败：下个周期重试换种类
                        }
                        else
                        {
                            // RandomProjectileConfig 未换（随机池空/开关关/自定义无资源）→ 锁定避免反复尝试
                            if (_lastAppliedKey != null && idx >= 0 && idx < _lastAppliedKey.Length)
                                _lastAppliedKey[idx] = curKey ?? "";
                        }
                    }
                }
                if (changed) arr.SetValue(boxed, idx);
            }
            catch { }
        }

        /// <summary>取 config 唯一标识：优先 name（TowerDefenseProjectileConfig 有 name 无 saveKey），fallback saveKey。</summary>
        static string GetCfgKey(object cfg)
        {
            try
            {
                if (cfg == null) return null;
                var v = FindPropOrFieldVal(cfg, "name");
                if (v is string s && !string.IsNullOrEmpty(s)) return s;
                v = FindPropOrFieldVal(cfg, "saveKey");
                if (v is string s2 && !string.IsNullOrEmpty(s2)) return s2;
                return null;
            }
            catch { return null; }
        }

        /// <summary>应用追踪相关字段（trackOpen / trackSearchInterval），返回是否有改动。</summary>
        static bool ApplyTrackRuntime(object boxed, Type bt)
        {
            bool changed = false;
            try
            {
                // trackOpen：追踪开=全追踪；跟随鼠标开=追踪失效；否则保持原值（关闭恢复原版追踪行为）
                var tof = bt.GetField("trackOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (tof != null && tof.FieldType == typeof(bool))
                {
                    bool cur = (bool)tof.GetValue(boxed);
                    bool target = ModSettings.BulletFollowMouse ? false : (ModSettings.BulletTrack ? true : cur);
                    if (cur != target) { tof.SetValue(boxed, target); changed = true; }
                }
                // trackSearchInterval：只有追踪开才每帧重搜；随机子弹发射时种类已定死，不强制每帧重搜（避免卡顿）
                var tif = bt.GetField("trackSearchInterval", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (tif != null && ModSettings.BulletTrack && (int)tif.GetValue(boxed) != 1)
                { tif.SetValue(boxed, 1); changed = true; }
            }
            catch { }
            return changed;
        }

        static System.Reflection.MethodInfo _bfChangeMethod;   // ChangeBulletData(int, cfg, char, double?)
        static System.Reflection.MethodInfo _bfChangeMethod2;  // 备选 TryChangeBulletDataInPlace(int, cfg)
        static bool _bfChangeDiag;
        static bool _bfInPlaceDiag;
        /// <summary>调用游戏官方 BulletField API 完整换子弹种类（外观+行为+大小+伤害）。成功返回 true。
        /// 关键：①TryChangeBulletDataInPlace 是 internal 非 public 方法，GetMethod 默认找不到 → 必须 FindMethodExact；
        /// ②ChangeBulletData 的 character 参数传 null 会直接返回 -1 永不生效 → 必须传 _data[idx].fireCharacter。</summary>
        static bool TryChangeBulletViaApi(int idx, object newCfg, Type cfgType, object character)
        {
            try
            {
                if (_bfChangeMethod2 == null && _bfChangeMethod == null && _cachedBulletField != null && GodotObject.IsInstanceValid(_cachedBulletField))
                {
                    var bft = _cachedBulletField.GetType();
                    var charType = FindType("TowerDefenseCharacter");
                    // 优先 TryChangeBulletDataInPlace（2 参，不需要 character；返回 BulletChangeInPlaceResult 整数枚举）
                    _bfChangeMethod2 = FindMethodExact(bft, "TryChangeBulletDataInPlace", new Type[] { typeof(int), cfgType });
                    _bfChangeMethod = FindMethodExact(bft, "ChangeBulletData", new Type[] { typeof(int), cfgType, charType, typeof(double?) });
                    if (!_bfChangeDiag)
                    {
                        _bfChangeDiag = true;
                        Bootstrap.Log("随机种类: API=" + (_bfChangeMethod2 != null ? "TryChangeBulletDataInPlace" : "无") + (_bfChangeMethod != null ? "+ChangeBulletData" : "") + " cfgType=" + cfgType.Name);
                    }
                }
                // 1) 优先 TryChangeBulletDataInPlace（无 char 依赖），调用后读回 _data[idx].config 验证是否真换成 newCfg
                //    （返回是整数枚举 BulletChangeInPlaceResult，不能用字符串 "Changed" 判断——那是旧 bug 导致永远失败）
                if (_bfChangeMethod2 != null)
                {
                    try
                    {
                        object ret2 = _bfChangeMethod2.Invoke(_cachedBulletField, new object[] { idx, newCfg });
                        if (!_bfInPlaceDiag)
                        {
                            _bfInPlaceDiag = true;
                            Bootstrap.Log("随机种类: inPlace结果=" + (ret2 != null ? ret2.ToString() : "null"));
                        }
                        // inPlace 返回 Changed 视为成功（不依赖后续 config 验证——内部可能 Duplicate config 导致引用不一致）
                        if (ret2 != null && ret2.ToString() == "Changed") return true;
                        if (IsBulletConfigNow(idx, newCfg)) return true;
                    }
                    catch { }
                }
                // 2) ChangeBulletData（必须传真实 character——传 null 会直接返回 -1 导致永不生效）
                if (_bfChangeMethod != null && character != null)
                {
                    try
                    {
                        var ret = _bfChangeMethod.Invoke(_cachedBulletField, new object[] { idx, newCfg, character, null });
                        if (ret is int ri && ri == idx) return true;
                        if (IsBulletConfigNow(idx, newCfg)) return true;
                    }
                    catch { }
                }
            }
            catch (System.Exception ex)
            {
                if (!_bfChangeDiag) { _bfChangeDiag = true; Bootstrap.Log("随机种类异常: " + ex.Message); }
            }
            return false;
        }

        /// <summary>读回 BulletField._data[idx].config，验证是否已换成 newCfg。
        /// 不要求同一引用（API 可能内部 Duplicate config）——比较 saveKey 内容相同也算成功。</summary>
        static bool IsBulletConfigNow(int idx, object newCfg)
        {
            try
            {
                if (_cachedBulletField == null || !GodotObject.IsInstanceValid(_cachedBulletField) || _bfDataField == null) return false;
                var arr = _bfDataField.GetValue(_cachedBulletField) as System.Array;
                if (arr == null || idx < 0 || idx >= arr.Length) return false;
                var boxed = arr.GetValue(idx);
                if (boxed == null) return false;
                var cf = boxed.GetType().GetField("config", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (cf == null) return false;
                var v = cf.GetValue(boxed);
                if (v == null) return false;
                if (ReferenceEquals(v, newCfg)) return true;
                // API 可能内部 Duplicate config——比较 saveKey 内容相同也算成功
                var sk1 = FindPropOrFieldVal(v, "saveKey") as string;
                var sk2 = FindPropOrFieldVal(newCfg, "saveKey") as string;
                if (!string.IsNullOrEmpty(sk1) && sk1 == sk2) return true;
                return false;
            }
            catch { return false; }
        }

        static string[] _lastAppliedKey;   // 按 idx 记录上次随机应用到的 config.saveKey（已随机锁定）；"RETRY@计数"=上次失败待重试；null=需随机；active=false 时清空
        static int _bfRerollCounter;       // 全局重试计数（每 2 帧 +1），随机失败后据此周期重试
        const int REROLL_GAP = 30;         // 随机失败后每 30 个计数周期重试一次（约 1 秒）
        static System.Reflection.FieldInfo _bfDataField;
        static System.Reflection.FieldInfo _bfCountField;
        static System.Reflection.FieldInfo _bfIdxField;

        /// <summary>把 BulletField._data 数组里活动子弹的 pos 向鼠标移动（反射操作 struct 数组，装箱/写回）。</summary>
        static void MoveBulletsToMouse(GodotObject bf)
        {
            try
            {
                if (_bfDataField == null)
                {
                    var t = bf.GetType();
                    _bfDataField = t.GetField("_data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _bfCountField = t.GetField("_activeCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _bfIdxField = t.GetField("_activeIndices", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_bfDataField == null) { Bootstrap.Log("跟随鼠标: 找不到 BulletField._data " + t.FullName); return; }
                }
                var arr = _bfDataField.GetValue(bf) as System.Array;
                if (arr == null) return;
                Vector2 mouse;
                try
                {
                    mouse = ((Node2D)bf).GetGlobalMousePosition();
                }
                catch { return; }
                int count = 0;
                if (_bfCountField != null) { try { count = (int)_bfCountField.GetValue(bf); } catch { } }
                var idxs = _bfIdxField != null ? _bfIdxField.GetValue(bf) as int[] : null;
                if (idxs != null)
                {
                    for (int i = 0; i < count && i < idxs.Length; i++)
                    {
                        int idx = idxs[i];
                        if (idx < 0 || idx >= arr.Length) continue;
                        MoveBulletPos(arr, idx, mouse);
                    }
                }
                else
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        object v = arr.GetValue(i);
                        if (v == null) continue;
                        try
                        {
                            var af = v.GetType().GetField("active", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (af != null && !(bool)af.GetValue(v)) continue;
                        }
                        catch { continue; }
                        MoveBulletPos(arr, i, mouse);
                    }
                }
            }
            catch { }
        }

        /// <summary>关闭子弹开关时调用：立即把所有场上追踪子弹改成直线（trackOpen=false），
        /// 否则旧追踪子弹会继续追踪直到命中（用户以为"追踪没关掉"）。</summary>
        public static void StopAllTracking()
        {
            try
            {
                var mt = Godot.Engine.GetMainLoop() as SceneTree;
                var root = mt != null ? mt.Root : null;
                if (root == null) return;
                WalkStopTracking(root);
            }
            catch { }
        }

        static void WalkStopTracking(Node node)
        {
            try
            {
                foreach (var child in node.GetChildren(true))
                {
                    if (child != null && GodotObject.IsInstanceValid(child))
                    {
                        try
                        {
                            if (child.GetType().Name == "BulletField") StopBulletTracking(child);
                        }
                        catch { }
                        WalkStopTracking(child);
                    }
                }
            }
            catch { }
        }

        static void StopBulletTracking(GodotObject bf)
        {
            try
            {
                if (_bfDataField == null)
                {
                    var t = bf.GetType();
                    _bfDataField = t.GetField("_data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _bfCountField = t.GetField("_activeCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _bfIdxField = t.GetField("_activeIndices", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_bfDataField == null) return;
                }
                var arr = _bfDataField.GetValue(bf) as System.Array;
                if (arr == null) return;
                int count = 0;
                if (_bfCountField != null) { try { count = (int)_bfCountField.GetValue(bf); } catch { } }
                var idxs = _bfIdxField != null ? _bfIdxField.GetValue(bf) as int[] : null;
                if (idxs != null)
                {
                    for (int i = 0; i < count && i < idxs.Length; i++)
                    {
                        int idx = idxs[i];
                        if (idx < 0 || idx >= arr.Length) continue;
                        ClearTrack(arr, idx);
                    }
                }
                else
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        object v = arr.GetValue(i);
                        if (v == null) continue;
                        try
                        {
                            var af = v.GetType().GetField("active", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (af != null && !(bool)af.GetValue(v)) continue;
                        }
                        catch { continue; }
                        ClearTrack(arr, i);
                    }
                }
            }
            catch { }
        }

        static void ClearTrack(System.Array arr, int idx)
        {
            try
            {
                var boxed = arr.GetValue(idx);
                if (boxed == null) return;
                var bt = boxed.GetType();
                var tof = bt.GetField("trackOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (tof != null && tof.FieldType == typeof(bool) && (bool)tof.GetValue(boxed))
                {
                    tof.SetValue(boxed, false);
                    arr.SetValue(boxed, idx);
                }
            }
            catch { }
        }

        static void MoveBulletPos(System.Array arr, int idx, Vector2 mouse)
        {
            try
            {
                var boxed = arr.GetValue(idx);
                if (boxed == null) return;
                var bt = boxed.GetType();
                var pf = bt.GetField("pos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var vf = bt.GetField("vel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pf == null || vf == null || pf.FieldType != typeof(Vector2) || vf.FieldType != typeof(Vector2)) return;
                var pos = (Vector2)pf.GetValue(boxed);
                // 跟随鼠标时：停掉追踪（防边追踪边被拉而抽搐）；不动 pos——vel 朝鼠标，游戏自己移动
                var tof = bt.GetField("trackOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (tof != null && tof.FieldType == typeof(bool) && (bool)tof.GetValue(boxed))
                    tof.SetValue(boxed, false);
                var vel = (Vector2)vf.GetValue(boxed);
                float speed = vel.Length();
                if (speed < 1f) speed = 300f;
                var dir = mouse - pos;
                if (dir.LengthSquared() <= 4f) return;   // 已在鼠标附近
                // 把 vel 设为朝鼠标方向（保留原速度大小）→ 游戏 ProcessShooterData 用它移动，平滑无偏移
                var nv = dir.Normalized() * speed;
                if (nv.DistanceSquaredTo(vel) > 1f)
                {
                    vf.SetValue(boxed, nv);
                    arr.SetValue(boxed, idx);
                }
            }
            catch { }
        }

        /// <summary>种植限制开关：由 patcher 注入 CanPacketPlant 开头——PlantOverlap/IgnoreTerrain 开才恒 true（关闭恢复原始逻辑）。</summary>
        public static bool ShouldIgnorePlantLimit()
        {
            return ModSettings.PlantOverlap || ModSettings.IgnoreTerrain;
        }

        /// <summary>无视紫卡开关：由 patcher 注入 Unlock/IsUnlocked/HasRequiredPlantCover 开头——IgnorePurple 开才恒 true。
        /// 注意：图鉴打开时返回 false！图鉴条目解锁 = plantShowAll || TowerDefensePacketConfig.Unlock()，
        /// 若 IgnorePurple 也强制 Unlock=true → "没开全图鉴图鉴却全解锁"（2026-08-21 用户反馈）。
        /// 图鉴全解由 AlmanacAll→plantShowAll 独立控制，不受 IgnorePurple 影响。</summary>
        public static bool ShouldUnlockAll()
        {
            return ModSettings.IgnorePurple && !IsAlmanacOpen();
        }

        static int _almanacOpenTimer;
        static bool _almanacOpenCached;
        /// <summary>图鉴是否打开（缓存：每 30 次调用扫描一次场景树找可见的 Almanac 节点，避免高频遍历卡顿）。</summary>
        static bool IsAlmanacOpen()
        {
            if (++_almanacOpenTimer % 30 != 0) return _almanacOpenCached;
            _almanacOpenCached = false;
            try
            {
                if (_lastRoot != null && GodotObject.IsInstanceValid(_lastRoot)) _almanacOpenCached = FindAlmanacVisible(_lastRoot);
            }
            catch { }
            return _almanacOpenCached;
        }

        static bool FindAlmanacVisible(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (GodotObject.IsInstanceValid(child))
                    {
                        var cn = child.GetType().FullName ?? "";
                        if (cn.Contains("Almanac") && !cn.Contains("RuntimeTest"))
                        {
                            if (child is Godot.CanvasItem ci && ci.IsVisibleInTree()) return true;
                        }
                    }
                }
                catch { }
                if (FindAlmanacVisible(child)) return true;
            }
            return false;
        }

        /// <summary>罐子透视开关：由 patcher 注入 CheckShow 开头——VaseESP 开才恒 true。</summary>
        public static bool ShouldVaseESP()
        {
            return ModSettings.VaseESP;
        }

        /// <summary>是否有任何子弹 mod 开启（追踪/随机/跟随鼠标）。全关时 BulletField.Spawn 注入完全跳过（零干扰）。</summary>
        /// <summary>是否有任何子弹 mod 开启（追踪/随机/跟随鼠标/自定义子弹）。全关时 BulletField.Spawn 注入完全跳过（零干扰）。
        /// 注意：自定义子弹类型（BulletType）也要算——否则只选了自定义子弹时 ApplyBulletModsRuntime 不执行，修改子弹无效。</summary>
        public static bool BulletModsActive() { return ModSettings.BulletFollowMouse || ModSettings.BulletTrack || ModSettings.BulletRandom || ModSettings.BulletType.Length > 0; }

        /// <summary>子弹追踪/随机开关：由 patcher 注入到 BulletField.Spawn 开头（data.trackOpen = 此方法返回值）。
        /// BulletFollowMouse 开 = 追踪失效（直线子弹，由跟随鼠标逻辑拉动）；BulletRandom 开 = 随机外观/速度；BulletTrack 开 = 全部追踪。
        /// 每 500 颗子弹打一次决策统计日志（确认随机/追踪在 Spawn 层生效）。
        /// original=原版 trackOpen（调用方传入）。全关时返回 original 保留原版追踪行为——
        /// 之前强制返回 false 会把所有子弹变直线打不中僵尸（原版追踪类子弹被破坏）。</summary>
        static int _trackTrue, _trackFalse, _trackLogTimer;
        public static bool ShouldTrackBullets(bool original)
        {
            bool r;
            if (ModSettings.BulletFollowMouse) r = false;   // 跟随鼠标开 → 追踪失效，避免子弹边追踪僵尸边被拉向鼠标（抽搐）
            else if (ModSettings.BulletTrack) r = true;      // 追踪开 = 全追踪
            else r = original;   // 随机子弹/全关 → 保持原版命中（随机只随机速度/种类，不打乱追踪——之前强制 50% 直线导致打不中）
            if (r) _trackTrue++; else _trackFalse++;
            if (++_trackLogTimer >= 30)
            {
                _trackLogTimer = 0;
                Bootstrap.Log("子弹决策: 追踪=" + _trackTrue + " 直线=" + _trackFalse
                    + " (鼠=" + ModSettings.BulletFollowMouse + " 随=" + ModSettings.BulletRandom + " 追=" + ModSettings.BulletTrack + ")");
                _trackTrue = _trackFalse = 0;
            }
            return r;
        }

        /// <summary>让所有植物能种地上：玩家种植检查 flag3 = characterConfig.plantGridType.Contains(PLANT)，
        /// 紫卡（如冰焰豌豆）的 plantGridType 不含 PLANT → 只能 PlantOnPlant 合体、不能单独放。
        /// 这里给所有植物卡片的 plantGridType 补上 PLANT。</summary>
        static void AddPlantGridTypeAll()
        {
            try
            {
                var ids = GetPacketIds(true);
                if (ids == null || ids.Count == 0) return;
                int fixedCount = 0, already = 0, fail1 = 0, fail2 = 0, fail3 = 0, fail4 = 0;
                foreach (var id in ids)
                {
                    try
                    {
                        var cfg = GetConfig(id);
                        if (cfg == null) { fail1++; continue; }
                        var cc = GetCharConfig(cfg);
                        if (cc == null) { fail2++; continue; }
                        var pg = FindPropOrFieldVal(cc, "plantGridType");
                        if (pg == null) { fail3++; continue; }
                        bool has = false;
                        if (pg is System.Collections.IEnumerable en)
                            foreach (var e in en) if (e != null && e.ToString() == "PLANT") { has = true; break; }
                        if (has) { already++; continue; }
                        var ga = pg.GetType().GetGenericArguments();
                        object plantEnum = null;
                        if (ga.Length == 1)
                        {
                            try { plantEnum = Enum.Parse(ga[0], "PLANT"); } catch { }
                        }
                        if (plantEnum == null) { fail4++; continue; }
                        var addM = pg.GetType().GetMethod("Add", new Type[] { ga[0] });
                        if (addM != null) { addM.Invoke(pg, new object[] { plantEnum }); fixedCount++; }
                    }
                    catch { fail4++; }
                }
                Bootstrap.Log("紫卡种植: 修复=" + fixedCount + " 已有PLANT=" + already + " 失败(config=" + fail1 + ",cc=" + fail2 + ",pg=" + fail3 + ",add=" + fail4 + ")");
            }
            catch (System.Exception ex) { Bootstrap.Log("紫卡种植异常: " + ex.Message); }
        }

        /// <summary>强制解锁卡牌：战斗中卡牌栏卡片 _lock=false（解除锁定）。
        /// 注意：不再强制 alive=true——alive 由游戏 RefreshRuntimeState 计算（阳光+前置植物），
        /// 强制改 alive 会与游戏打架导致卡牌亮暗闪烁。前置植物限制由 patcher 注入 HasRequiredPlantCover 恒 true 解决。
        /// 卡牌在 tree.Root 的 HUD/CanvasLayer 下，必须从场景树根遍历。</summary>
        static bool _unlockLogged;
        public static void ForceUnlockPackets(Node root)
        {
            try
            {
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                int n = 0;
                WalkUnlockPacket(start, ref n);
                if (n > 0 && !_unlockLogged) { _unlockLogged = true; Bootstrap.Log("卡牌解锁: 处理 " + n + " 张"); }
            }
            catch { }
        }

        static void WalkUnlockPacket(Node node, ref int n)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child.GetType().Name == "TowerDefenseInGamePacketShow")
                    {
                        if (FindPropOrFieldVal(child, "_lock") is bool lb && lb)
                            SetPropOrField(child, "_lock", false);
                        n++;
                    }
                }
                catch { }
                WalkUnlockPacket(child, ref n);
            }
        }

        /// <summary>反射获取属性值（优先）或字段值（沿基类链）。</summary>
        static object FindPropOrFieldVal(object obj, string name)
        {
            try
            {
                var t = obj.GetType();
                while (t != null)
                {
                    var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(obj);
                    var f = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (f != null) return f.GetValue(obj);
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        /// <summary>获取卡配置的角色配置：0.26 字段是私有 _characterConfig（旧版可能叫 characterConfig），兼容两者。</summary>
        static object GetCharConfig(object cfg)
        {
            if (cfg == null) return null;
            // 优先 characterConfig 属性 getter（触发懒加载，皮肤数据才从 pck 加载）——
            // 直接读 _characterConfig 私有字段可能未初始化（返回 null → 检测不到皮肤）
            var v = FindPropOrFieldVal(cfg, "characterConfig");
            if (v == null) v = FindPropOrFieldVal(cfg, "_characterConfig");
            return v;
        }

        /// <summary>反射设置属性值（优先）或字段值（沿基类链）。</summary>
        static void SetPropOrField(object obj, string name, object value)
        {
            try
            {
                var t = obj.GetType();
                while (t != null)
                {
                    var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (p != null && p.GetIndexParameters().Length == 0)
                    {
                        var setter = p.GetSetMethod(true);
                        if (setter != null) { setter.Invoke(obj, new object[] { value }); return; }
                    }
                    var f = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (f != null) { f.SetValue(obj, value); return; }
                    t = t.BaseType;
                }
            }
            catch { }
        }


        /// <summary>追踪搜索间隔：只有追踪开才每帧重搜全图目标；随机子弹发射时种类已定死，不强制每帧重搜（避免卡顿）。</summary>
        public static int TrackIntervalOverride(int original)
        {
            return ModSettings.BulletTrack ? 1 : original;
        }

        /// <summary>随机子弹速度倍率（0.7~1.3），由 patcher 注入到 BulletField.Spawn（data.speed）。
        /// 随机开时每颗子弹快慢不一，配合追踪/直线随机，随机感明显。</summary>
        public static float RandomizeSpeed(float speed)
        {
            if (!ModSettings.BulletRandom) return speed;
            return speed * (float)(0.7 + _bulletRand.NextDouble() * 0.6);
        }

        static string[] _projKeys;
        static bool _rpcLogged;
        static bool _rpcCalled;
        static int _rpcHit;

        static readonly string _bulletCachePath = "user://mod_bullets.txt";   // user:// 两端兼容（手机版无 D: 盘）
        /// <summary>内置保底子弹 key 列表（原 PROJECTILE_CONFIG 全量 136 种）：ResourceManager 未就绪/缓存缺失时也能列出选项。
        /// 惰性初始化：避免超大静态构造(cctor)导致 mono 加载合并后主 DLL 崩溃（手机版闪退根因）。</summary>
        static string[] _defaultProjKeys;
        static string[] GetDefaultProjKeys()
        {
            if (_defaultProjKeys != null) return _defaultProjKeys;
            var l = new System.Collections.Generic.List<string>(140);
            AddProjKeysA(l); AddProjKeysB(l); AddProjKeysC(l); AddProjKeysD(l);
            AddProjKeysE(l); AddProjKeysF(l); AddProjKeysG(l);
            _defaultProjKeys = l.ToArray();
            return _defaultProjKeys;
        }
        // 拆分小方法：超大方法体（136 项数组）在 Mono.Cecil 合并后 mono 加载崩溃（手机版闪退根因），每块 20 项
        static void AddProjKeysA(System.Collections.Generic.List<string> l)
        {
            l.Add("PeaDefault"); l.Add("PeaTrack"); l.Add("FirePea"); l.Add("FirePeaTrack"); l.Add("FireZombiePea");
            l.Add("SnowPea"); l.Add("SnowPeaTrack"); l.Add("SnowPeaCatapult"); l.Add("SnowPeaFull"); l.Add("SnowPeaFullBody");
            l.Add("GoldPea"); l.Add("IceFirePea"); l.Add("IceFirePeaTrack"); l.Add("WhiteFirePea"); l.Add("MegaFirePea");
            l.Add("MegaFirePeaTrack"); l.Add("PeaVase"); l.Add("PeaArmorDefault"); l.Add("SnowPeaArmor"); l.Add("FirePeaArmor");
        }
        static void AddProjKeysB(System.Collections.Generic.List<string> l)
        {
            l.Add("IceFirePeaArmor"); l.Add("MegaFirePeaArmor"); l.Add("FirePeaSDefault"); l.Add("FirePeaUDefault"); l.Add("ZombiePeaDefault");
            l.Add("SpikeDefault"); l.Add("SpikeFull"); l.Add("SpikeTrack"); l.Add("SpikeTrackMini"); l.Add("SpikeCatapult");
            l.Add("IceSpike"); l.Add("PuffDefault"); l.Add("PuffBig"); l.Add("IcePuff"); l.Add("HypnoPuff");
            l.Add("ButterDefault"); l.Add("ButterCatapult"); l.Add("ButterTrack"); l.Add("KernalDefault"); l.Add("KernalCatapult");
        }
        static void AddProjKeysC(System.Collections.Generic.List<string> l)
        {
            l.Add("KernalLine"); l.Add("KernalTrack"); l.Add("MelonDefault"); l.Add("MelonLine"); l.Add("WinterMelonDefault");
            l.Add("WinterMelonLine"); l.Add("WinterMelonTrack"); l.Add("WinterMelonCustomTrack"); l.Add("WinterMelonXDefault"); l.Add("NutDefault");
            l.Add("GloomDefault"); l.Add("IceSwordFull"); l.Add("IceSpearDefault"); l.Add("SpikeMelonDefault"); l.Add("FireNoteTrack");
            l.Add("IceFireNoteTrack"); l.Add("MegaFireNote"); l.Add("CyberDefault"); l.Add("FireCyber"); l.Add("IceFireCyber");
        }
        static void AddProjKeysD(System.Collections.Generic.List<string> l)
        {
            l.Add("MegaFireCyber"); l.Add("PowDefault"); l.Add("FirePow"); l.Add("IceFirePow"); l.Add("MegaFirePow");
            l.Add("IceBallTrack"); l.Add("IceBallCatapult"); l.Add("CabbageLine"); l.Add("CabbageTrack"); l.Add("CabbageCatapult");
            l.Add("SunshroomDefault"); l.Add("SunshroomCustom0"); l.Add("SunshroomBig"); l.Add("SunshroomBigCustom0"); l.Add("MagnetDefault");
            l.Add("MagnetDisable"); l.Add("StarDefault"); l.Add("StarMedium"); l.Add("FireStar"); l.Add("FireStarMedium");
        }
        static void AddProjKeysE(System.Collections.Generic.List<string> l)
        {
            l.Add("SnowStar"); l.Add("PotatoStar"); l.Add("BigStarDefault"); l.Add("BigFireStar"); l.Add("FireStarF");
            l.Add("StarFull"); l.Add("ShootingStartsBigStarFull"); l.Add("StarBigCatGatlingPea"); l.Add("FireStarBigCatGatlingPea"); l.Add("SunDefault");
            l.Add("SunTrack"); l.Add("SunTrackExplode"); l.Add("AxeDefault"); l.Add("CabbageCobDefault"); l.Add("CabbageCobButter");
            l.Add("GarlicDefault"); l.Add("GarlicP"); l.Add("CatapultDefault"); l.Add("PotatoCobCannonCob"); l.Add("PumpkinCannonCob");
        }
        static void AddProjKeysF(System.Collections.Generic.List<string> l)
        {
            l.Add("DoomCobCannonCob"); l.Add("PeaCobCannonCob"); l.Add("CupidDefault"); l.Add("CaskButterDefault"); l.Add("CaskDefault");
            l.Add("MeteorStar"); l.Add("MeteorStarS"); l.Add("JalaCabbageDefault"); l.Add("JalaDefault"); l.Add("CoinSilver");
            l.Add("CoinGold"); l.Add("CoinDiamond"); l.Add("CoinSilverTrack"); l.Add("CoinGoldTrack"); l.Add("CoinDiamondTrack");
            l.Add("CoinSilverTrackPlantMarigoldG"); l.Add("CoinGoldTrackPlantMarigoldG"); l.Add("CoinDiamondTrackPlantMarigoldG"); l.Add("CactusKnifeFull"); l.Add("IcePieceFullBody");
        }
        static void AddProjKeysG(System.Collections.Generic.List<string> l)
        {
            l.Add("CoinTQTrackPlantMarigoldG"); l.Add("CoinYB1TrackPlantMarigoldG"); l.Add("CoinYB2TrackPlantMarigoldG"); l.Add("SnowBulletDefault"); l.Add("SnowBulletPower");
            l.Add("SmallPotatoDefault"); l.Add("CobCannonCob"); l.Add("IceCobCannonCob"); l.Add("CaskPeaDefault"); l.Add("PeaBombDefault"); l.Add("FirePeaBomb");
            l.Add("PowShroomDefault"); l.Add("ChestDefault"); l.Add("SpikeZTrack"); l.Add("StarCaltrop"); l.Add("BossDaveJackMissile");
            l.Add("GoldPeaDefault");
        }
        static void SaveCachedKeys(string[] keys)
        {
            try
            {
                var fa = Godot.FileAccess.Open(_bulletCachePath, Godot.FileAccess.ModeFlags.Write);
                if (fa != null)
                {
                    fa.StoreString(string.Join("\n", keys));
                    fa.Close();
                }
            }
            catch { }
        }
        static string[] LoadCachedKeys()
        {
            try
            {
                if (!Godot.FileAccess.FileExists(_bulletCachePath)) return new string[0];
                var fa = Godot.FileAccess.Open(_bulletCachePath, Godot.FileAccess.ModeFlags.Read);
                if (fa == null) return new string[0];
                string all = fa.GetAsText();
                fa.Close();
                return all.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.RemoveEmptyEntries);
            }
            catch { return new string[0]; }
        }
        /// <summary>子弹配置 key 列表（ModUI 下拉选择用）。优先实时缓存；不可用读缓存文件；最后内置保底。</summary>
        public static string[] GetProjectileKeys()
        {
            try
            {
                if (_projKeys == null || _projKeys.Length == 0)
                {
                    CacheProjectileKeys();
                    if (_projKeys == null || _projKeys.Length == 0)
                        _projKeys = LoadCachedKeys();
                    if (_projKeys == null || _projKeys.Length == 0)
                        _projKeys = GetDefaultProjKeys();
                }
                return _projKeys ?? GetDefaultProjKeys();
            }
            catch { return GetDefaultProjKeys(); }
        }
        /// <summary>子弹 key → 中文名（词根映射 + 驼峰拆分，未命中显示原 key）。惰性初始化（避免大 cctor 崩）。</summary>
        static System.Collections.Generic.Dictionary<string, string> _bulletZh;
        static System.Collections.Generic.Dictionary<string, string> GetBulletZh()
        {
            if (_bulletZh != null) return _bulletZh;
            var d = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            AddBulletZhA(d); AddBulletZhB(d); AddBulletZhC(d); AddBulletZhD(d); AddBulletZhE(d);
            _bulletZh = d;
            return _bulletZh;
        }
        // 拆分小方法：超大方法体（93 项字典）在合并后 mono 加载崩溃
        static void AddBulletZhA(System.Collections.Generic.Dictionary<string, string> d)
        {
            d["Pea"] = "豌豆"; d["Cob"] = "玉米"; d["Melon"] = "西瓜"; d["WinterMelon"] = "冰瓜"; d["Snow"] = "寒冰";
            d["Ice"] = "寒冰"; d["Fire"] = "火焰"; d["Star"] = "星星"; d["Butter"] = "黄油"; d["Ball"] = "弹球";
            d["Spore"] = "孢子"; d["Garlic"] = "大蒜"; d["Basketball"] = "篮球"; d["Gold"] = "金"; d["Zombie"] = "僵尸";
            d["Plant"] = "植物"; d["Track"] = "追踪"; d["Big"] = "大"; d["Small"] = "小"; d["Fast"] = "快速";
        }
        static void AddBulletZhB(System.Collections.Generic.Dictionary<string, string> d)
        {
            d["Slow"] = "减速"; d["Basic"] = "普通"; d["Default"] = "默认"; d["Normal"] = "普通"; d["Giant"] = "巨人";
            d["Coin"] = "金币"; d["Diamond"] = "钻石"; d["Marigold"] = "金盏花"; d["Cannon"] = "炮"; d["Medium"] = "中";
            d["Large"] = "大"; d["Sun"] = "阳光"; d["Potato"] = "土豆"; d["Mine"] = "地雷"; d["Cherry"] = "樱桃";
            d["Bomb"] = "炸弹"; d["Nut"] = "坚果"; d["Wall"] = "墙"; d["Kelp"] = "海草"; d["Puff"] = "烟雾";
        }
        static void AddBulletZhC(System.Collections.Generic.Dictionary<string, string> d)
        {
            d["Shroom"] = "蘑菇"; d["Mushroom"] = "蘑菇"; d["Fume"] = "烟雾"; d["Cactus"] = "仙人掌"; d["Petal"] = "花瓣";
            d["Seed"] = "种子"; d["Fruit"] = "果实"; d["Acid"] = "酸液"; d["Venom"] = "毒液"; d["Plasma"] = "等离子";
            d["Laser"] = "激光"; d["Lightning"] = "闪电"; d["Thunder"] = "雷电"; d["Wind"] = "风"; d["Leaf"] = "叶子";
            d["Bone"] = "骨头"; d["Note"] = "音符"; d["Hammer"] = "锤子"; d["Queen"] = "女王"; d["Wave"] = "波";
        }
        static void AddBulletZhD(System.Collections.Generic.Dictionary<string, string> d)
        {
            d["Split"] = "分裂"; d["Triple"] = "三连"; d["Double"] = "双发"; d["Twin"] = "双"; d["Chain"] = "连锁";
            d["Pierce"] = "穿透"; d["Homing"] = "追踪"; d["Fly"] = "飞行"; d["Magnet"] = "磁铁"; d["Shadow"] = "影";
            d["Blood"] = "血"; d["Dark"] = "暗"; d["Light"] = "光"; d["Holy"] = "圣光"; d["Tangle"] = "缠绕";
            d["Grape"] = "葡萄"; d["Apple"] = "苹果"; d["Egg"] = "蛋"; d["Pine"] = "松果"; d["Chili"] = "辣椒";
        }
        static void AddBulletZhE(System.Collections.Generic.Dictionary<string, string> d)
        {
            d["Doughnut"] = "甜甜圈"; d["Corn"] = "玉米"; d["Squash"] = "南瓜"; d["Pumpkin"] = "南瓜"; d["Repeater"] = "连发";
            d["SnowPea"] = "寒冰豌豆"; d["FirePea"] = "火豌豆"; d["Gatling"] = "机枪"; d["TwinSunflower"] = "双子向日葵"; d["Sunflower"] = "向日葵";
            d["CherryBomb"] = "樱桃炸弹"; d["Doom"] = "毁灭"; d["FumeShroom"] = "烟雾蘑菇";
            d["CobCannonCob"] = "玉米炮炮弹"; d["IceCobCannonCob"] = "寒冰玉米炮弹";
        }
        public static string TranslateBulletName(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (GetBulletZh().TryGetValue(key, out var exact)) return exact;
            var sb = new System.Text.StringBuilder();
            var word = new System.Text.StringBuilder();
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(key[i - 1]))
                    AppendBulletWord(sb, word);
                word.Append(c);
            }
            AppendBulletWord(sb, word);
            return sb.ToString();
        }
        static void AppendBulletWord(System.Text.StringBuilder sb, System.Text.StringBuilder word)
        {
            if (word.Length == 0) return;
            string p = word.ToString();
            if (GetBulletZh().TryGetValue(p, out var zh)) sb.Append(zh);
            else sb.Append(p);
            word.Clear();
        }

        static void CacheProjectileKeys()
        {
            try
            {
                if (_projKeys != null && _projKeys.Length > 0) return;   // 已缓存
                var rmType = FindType("ResourceManager");
                if (rmType == null) { if (!_rpcLogged) { _rpcLogged = true; Bootstrap.Log("随机子弹: 找不到 ResourceManager"); } }
                string[] keys = null;
                // ① 全量清单 PROJECTILE_RESOURCE.Data（名字→路径，静态 Json 启动即有，主菜单也能拿到全量 136+ 种）
                if (rmType != null)
                {
                    try
                    {
                        var pr = rmType.GetField("PROJECTILE_RESOURCE", BindingFlags.NonPublic | BindingFlags.Static);
                        var json = pr != null ? pr.GetValue(null) : null;
                        if (json != null)
                        {
                            var dataP = json.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var data = dataP != null ? dataP.GetValue(json) : null;
                            if (data is System.Collections.IDictionary d)
                            {
                                keys = new string[d.Count];
                                int i = 0;
                                foreach (var k in d.Keys) { keys[i++] = k as string; }
                            }
                        }
                    }
                    catch { }
                }
                // ② 回退：PROJECTILE_CONFIG 字典（战斗中已加载）
                if (keys == null || keys.Length == 0)
                {
                    try
                    {
                        var instField = rmType != null ? rmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static) : null;
                        var inst = instField != null ? instField.GetValue(null) : null;
                        var prop = rmType != null ? rmType.GetProperty("PROJECTILE_CONFIG", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
                        var dict = prop != null ? prop.GetValue(inst) : null;
                        if (dict is System.Collections.Generic.Dictionary<string, Godot.Resource> gd)
                        {
                            keys = new string[gd.Count];
                            int i = 0;
                            foreach (var k in gd.Keys) { keys[i++] = k; }
                        }
                        else if (dict is System.Collections.IDictionary idict)
                        {
                            keys = new string[idict.Count];
                            int i = 0;
                            foreach (var k in idict.Keys) { keys[i++] = k as string; }
                        }
                    }
                    catch { }
                }
                // ③ 内置保底
                if (keys == null || keys.Length == 0) keys = GetDefaultProjKeys();
                _projKeys = keys;
                if (_projKeys != null && _projKeys.Length > 0)
                {
                    if (!_rpcLogged) { _rpcLogged = true; Bootstrap.Log("随机子弹: 配置池=" + _projKeys.Length + " 全部=" + string.Join(",", _projKeys)); }
                    SaveCachedKeys(_projKeys);
                }
            }
            catch (System.Exception ex) { if (!_rpcLogged) { _rpcLogged = true; Bootstrap.Log("随机子弹异常: " + ex.Message); } }
        }

        /// <summary>随机子弹 = 随机发射不同种类的子弹：由 patcher 注入到 BulletField.Spawn 开头，
        /// 随机把 data.config 换成 ResourceManager.PROJECTILE_CONFIG 里的另一种子弹配置（外观/种类随机变化）。
        /// 例如豌豆射手可能随机打出寒冰豆/火豆/西瓜等不同子弹。</summary>
        static System.Reflection.MethodInfo _rpcGetCfg; // 缓存 GetProjectileConfig，避免每个子弹都反射
        static bool _rpcHitLog;
        static object _projResData;   // 缓存 PROJECTILE_RESOURCE.Data（名字→res 路径）
        static System.Collections.Generic.Dictionary<string, object> _projPathCache = new();   // name→已加载config 缓存：避免每颗子弹重复 ResourceLoader.Load（手机内存膨胀→闪退）
        /// <summary>按 PROJECTILE_RESOURCE 清单的路径直接加载子弹配置（字典 GetProjectileConfig 失败时的兜底）。每个 key 只加载一次并缓存。</summary>
        static object LoadProjectileByPath(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (_projPathCache.TryGetValue(name, out var hit)) return hit;
                if (_projResData == null)
                {
                    var rmType = FindType("ResourceManager");
                    var pr = rmType != null ? rmType.GetField("PROJECTILE_RESOURCE", BindingFlags.NonPublic | BindingFlags.Static) : null;
                    var json = pr != null ? pr.GetValue(null) : null;
                    _projResData = json != null ? json.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(json) : null;
                }
                object result = null;
                if (_projResData is System.Collections.IDictionary d && d.Contains(name))
                {
                    // d[name] 可能是 string 或 Godot Variant（Variant 结构体 `as string` 会得 null）——两种都兼容
                    object v = null;
                    try { v = d[name]; } catch { }
                    string path = v as string;
                    if (string.IsNullOrEmpty(path) && v != null) { try { path = v.ToString(); } catch { } }
                    if (!string.IsNullOrEmpty(path))
                        result = Godot.ResourceLoader.Load(path);
                }
                if (result != null) _projPathCache[name] = result;
                return result;
            }
            catch { return null; }
        }
        static System.Collections.Generic.Dictionary<string, object> _projDictCache = new();   // name→GetProjectileConfig 结果缓存（避免每 60 帧每颗子弹重复反射 Invoke）
        static object GetProjCfgCached(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (_projDictCache.TryGetValue(name, out var c)) return c;
                object r = null;
                if (_rpcGetCfg != null) r = _rpcGetCfg.Invoke(null, new object[] { name });
                _projDictCache[name] = r;
                return r;
            }
            catch { return null; }
        }
        public static object RandomProjectileConfig(object currentConfig)
        {
            if (!_rpcCalled)
            {
                _rpcCalled = true;
                Bootstrap.Log("随机子弹: RandomProjectileConfig 被调用 随机=" + ModSettings.BulletRandom + " 当前config=" + (currentConfig != null ? currentConfig.GetType().Name : "null"));
            }
            try
            {
                // 自定义子弹类型（玩家在面板选了一种子弹）：所有植物子弹替换成该种类，优先于随机
                if (!string.IsNullOrEmpty(ModSettings.BulletType))
                {
                    if (_rpcGetCfg == null)
                    {
                        var tdmType = FindType("TowerDefenseManager");
                        _rpcGetCfg = tdmType != null ? tdmType.GetMethod("GetProjectileConfig", new Type[] { typeof(string) }) : null;
                        if (_rpcGetCfg == null) return currentConfig;
                    }
                    string bt = ModSettings.BulletType;
                    if (bt == "IceCobCannonCob") bt = "CobCannonCob";   // 寒冰玉米炮弹：游戏无独立配置 → 映射到玉米炮弹
                    var fixedCfg = GetProjCfgCached(bt);
                    if (fixedCfg == null)
                        fixedCfg = LoadProjectileByPath(bt);   // 字典没有 → 按 PROJECTILE_RESOURCE 清单路径直接加载
                    if (fixedCfg != null)
                    {
                        _rpcHit++;
                        if (_rpcHit == 1)
                            Bootstrap.Log("自定义子弹: 生效 类型=" + ModSettings.BulletType + " 旧=" + (currentConfig != null ? currentConfig.GetType().Name : "null"));
                        return fixedCfg;
                    }
                    if (!_rpcHitLog) { _rpcHitLog = true; Bootstrap.Log("自定义子弹: 未找到类型=" + ModSettings.BulletType + "（字典+清单路径均失败）"); }
                    return currentConfig;
                }
                if (!ModSettings.BulletRandom) return currentConfig;
                if (_projKeys == null || _projKeys.Length == 0) { CacheProjectileKeys(); if (_projKeys == null || _projKeys.Length == 0) return currentConfig; }
                // 随机池过滤非攻击类（硬币/阳光/宝箱等——随机到会导致"有时失效"）
                string name = PickRandomProjectileName();
                // 缓存 GetProjectileConfig（高频调用，不能每子弹都 FindType+GetMethod）
                if (_rpcGetCfg == null)
                {
                    var tdmType = FindType("TowerDefenseManager");
                    _rpcGetCfg = tdmType != null ? tdmType.GetMethod("GetProjectileConfig", new Type[] { typeof(string) }) : null;
                    if (_rpcGetCfg == null) return currentConfig;
                }
                if (string.IsNullOrEmpty(name)) return currentConfig;
                var cfg = GetProjCfgCached(name);
                if (cfg == null) cfg = LoadProjectileByPath(name);   // 字典没有 → 按清单路径直接加载（保证随机到的都生效）
                if (cfg != null)
                {
                    _rpcHit++;
                    if (_rpcHit == 1)
                        Bootstrap.Log("随机子弹: 换种类生效 新=" + name + " 旧=" + (currentConfig != null ? currentConfig.GetType().Name : "null"));
                }
                return cfg ?? currentConfig;
            }
            catch (System.Exception ex) { Bootstrap.Log("随机子弹换种类异常: " + ex.Message); return currentConfig; }
        }

        /// <summary>随机选一个攻击子弹（避开硬币/阳光/宝箱/磁铁等非攻击类，随机 8 次选不到就任意）。</summary>
        static string PickRandomProjectileName()
        {
            try
            {
                if (_projKeys == null || _projKeys.Length == 0) return "";
                for (int i = 0; i < 8; i++)
                {
                    var n = _projKeys[_bulletRand.Next(_projKeys.Length)];
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.Contains("Coin") || n.Contains("Sun") || n.Contains("Magnet") ||
                        n.Contains("Chest") || n.Contains("Token") || n.Contains("Silver") || n.Contains("Gold") ||
                        n.Contains("Catapult") || n.Contains("Cannon") || n.Contains("Cob") || n.Contains("Butter") ||
                        n.Contains("Kernal") || n.Contains("Spike") || n.Contains("Puff") ||
                        n.Contains("Squash") || n.Contains("Chomper") || n.Contains("Cabbage") || n.Contains("Doom") ||
                        n.Contains("Grenade") || n.Contains("Bomb") || n.Contains("Mine"))
                        continue;
                    return n;
                }
                return _projKeys[_bulletRand.Next(_projKeys.Length)];
            }
            catch { return _projKeys != null && _projKeys.Length > 0 ? _projKeys[0] : ""; }
        }

        /// <summary>随机子弹方向+速度：由 patcher 注入到 BulletField.Spawn（data.vel）。
        /// 随机开时每颗子弹方向偏转 ±25° + 速度 0.7~1.3 → 子弹乱飞（斜飞/直线/追踪混合），随机效果明显。
        /// 注意：追踪子弹（trackOpen=true）的 vel 会被 ProcessTrackData 覆盖，不受影响。</summary>
        public static Godot.Vector2 RandomizeVel(Godot.Vector2 vel)
        {
            if (!ModSettings.BulletRandom) return vel;
            float len = vel.Length();
            if (len < 1f) return vel;
            float ang = (float)((_bulletRand.NextDouble() * 2.0 - 1.0) * 0.44);   // ±25°
            float nx = vel.X * Mathf.Cos(ang) - vel.Y * Mathf.Sin(ang);
            float ny = vel.X * Mathf.Sin(ang) + vel.Y * Mathf.Cos(ang);
            return new Godot.Vector2(nx, ny).Normalized() * len * (float)(0.7 + _bulletRand.NextDouble() * 0.6);
        }

        static bool _bulkPlanting;
        static System.Reflection.MethodInfo _plantColumnMethod;

        /// <summary>种一列出列：由 patcher 注入到 TowerDefenseInGamePacketShow.Plant(Vector2I,...) 开头。
        /// 只对玩家种植生效（罐子内容/僵尸生成走 TowerDefensePacketConfig.Plant，不经过这里，
        /// 所以不会整列复制罐子/僵尸）。内部从 packetShow.config 拿配置，对同列其他行调 config.Plant。</summary>
        /// <summary>卡价不涨价：patcher 注入 TowerDefensePacketConfig.GetCostRise 开头，开则返回 0（金卡/重复购买不再涨价）。</summary>
        public static bool ShouldNoCostRise() { return ModSettings.NoCostRise; }
        /// <summary>零消费：patcher 注入 TowerDefensePacketConfig.GetCost 开头，开则返回 0（所有卡免费）。</summary>
        public static bool ShouldZeroCost() { return ModSettings.ZeroCost; }
        /// <summary>所有关卡可选卡：patcher 注入 TowerDefenseLevelConfig.get_packetBankMethod 开头，开则返回 CHOOSE(1)。</summary>
        public static bool ShouldForceChoose() { return ModSettings.CanChooseAll; }
        /// <summary>Boss 不鞠躬：patcher 注入 TowerDefenseZombieBoss.HeadExitedEntered 开头，开则 return。</summary>
        public static bool ShouldBossNoBow() { return ModSettings.BossNoBow; }
        /// <summary>禁止投掷小鬼：patcher 注入 ImpThrowerComponent.SpawnImp 开头，开则 return。</summary>
        public static bool ShouldNoThrowImp() { return ModSettings.NoThrowImp; }
        /// <summary>成长植物秒成熟：patcher 注入 GrowUpComponent.PhysicsProcess 开头，开则计时器拉满→当场到最大形态。</summary>
        public static bool ShouldInstantGrow() { return ModSettings.InstantGrow; }

        static int _noZombieTimer;
        static int _multiTimer;
        static int _fullRangeTimer;
        static int _spawnMultTimer;
        static int _crystalTimer;
        static Node _lastRoot;
        static int _cannonTimer;
        static int _cannonLogTimer;
        static int _houseTimer;
        static bool _houseLogged;
        static int _warningTimer;
        static bool _warningLineLogged;
        static int _squashTimer;
        static readonly System.Collections.Generic.List<Node2D> _squashCache = new();
        static int _squashRefreshTimer;
        /// <summary>禁止出僵尸：收集所有 Zombie camp 节点并删除（每 15 帧，简单可靠）。</summary>
        static void RemoveAllZombies(Node root)
        {
            try
            {
                var list = new System.Collections.Generic.List<Node2D>();
                CollectZombies(root, list);
                for (int i = 0; i < list.Count; i++)
                {
                    var n2 = list[i];
                    if (n2 == null || !GodotObject.IsInstanceValid(n2)) continue;
                    try { n2.QueueFree(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>“禁止出僵尸”的清理入口：联机镜像模式下跳过房主同步来的真僵尸。
        /// 这样客机等价于“不刷自己的怪”，但房主那边的僵尸照样在你场上走。
        /// ★ IsMirroredEntity 现在会把“正在认领中的镜像”也算进去 ——
        ///   否则刚生成还没认领的镜像会被当成自己的怪删掉，还会把 Dead 传给房主（房主真僵尸被删）。</summary>
        static void RemoveZombiesForNoSpawn(Node root)
        {
            try
            {
                if (!NetMirror.FreezeSpawnActive) { RemoveAllZombies(root); return; }
                var list = new System.Collections.Generic.List<Node2D>();
                CollectZombies(root, list);
                int n = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var n2 = list[i];
                    if (n2 == null || !GodotObject.IsInstanceValid(n2)) continue;
                    if (NetMirror.IsMirroredEntity(n2)) continue;   // 镜像僵尸（含认领中的）：留着
                    try { n2.QueueFree(); n++; } catch { }
                }
                if (n > 0 && !_netPurgeLogged)
                {
                    _netPurgeLogged = true;
                    Bootstrap.Log("联机镜像: 客机已开始清理本地刷的僵尸（房主同步来的会保留）");
                }
            }
            catch { }
        }

        static bool _netPurgeLogged;

        /// <summary>一键植物死亡：销毁场上所有植物（CollectPlants 自遍历）。</summary>
        public static int KillAllPlants()
        {
            int n = 0;
            try
            {
                var root = _lastRoot;
                if (root == null || !GodotObject.IsInstanceValid(root)) return 0;
                var list = new System.Collections.Generic.List<Node2D>();
                CollectPlants(root, list);
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p == null || !GodotObject.IsInstanceValid(p)) continue;
                    try { p.QueueFree(); n++; } catch { }
                }
                Bootstrap.Log("一键植物死亡: " + n + " 个");
            }
            catch (System.Exception ex) { Bootstrap.Log("一键植物死亡异常: " + ex.Message); }
            return n;
        }

        // ===== 官方 CommandManager 缺失功能补齐 =====

        /// <summary>按模式一键通关：遍历 LevelDictionary + 官方关卡注册表 LevelResource.json，按 key 前缀过滤当前模式关卡，
        /// 设 Finish=1 + Difficult + Star=3 + Reward。mode: Adventure / Challenge / MiniGame / PuzzleGame / Survival / IZM2。
        /// 注：0.25.5 的 res://Core/CommandManager/XXXInit.json 在 0.26/0.27 不存在（0.27 pck 只有 Asset/Config/Save/LevelInit.json），
        /// 改用遍历关卡字典 + 官方注册表（覆盖未注册的新关卡）。</summary>
        public static void CompleteMode(string mode)
        {
            int done = 0;
            try
            {
                var gsmType = FindType("GameSaveManager");
                if (gsmType == null) { Bootstrap.Log("通关" + mode + ": 找不到 GameSaveManager"); return; }
                var inst = gsmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (inst == null) { Bootstrap.Log("通关" + mode + ": GameSaveManager.Instance 为空"); return; }
                var getVal = gsmType.GetMethod("GetLevelValue", new Type[] { typeof(string) });
                var setVal = gsmType.GetMethod("SetLevelValue", new Type[] { typeof(string), typeof(Godot.Collections.Dictionary) });
                if (getVal == null || setVal == null) { Bootstrap.Log("通关" + mode + ": GetLevelValue/SetLevelValue 缺失"); return; }
                // ① 存档字典中匹配模式的关卡
                var getDict = gsmType.GetMethod("GetLevelDictionary", Type.EmptyTypes);
                var dict = getDict != null ? getDict.Invoke(inst, null) : null;
                if (dict is Godot.Collections.Dictionary gd)
                {
                    foreach (var from in gd.Keys)
                    {
                        if (from.VariantType == Variant.Type.Nil) continue;
                        string key = from.ToString();
                        if (!KeyMatchesMode(key, mode)) continue;
                        done += SetLevelFinish(inst, getVal, setVal, key, true, true, true);
                    }
                }
                // ② 官方关卡注册表 LevelResource.json 中匹配模式的关卡（覆盖 0.27 未注册的新关卡）
                var officialKeys = LoadOfficialLevelKeys();
                foreach (var key in officialKeys)
                {
                    if (string.IsNullOrEmpty(key) || !KeyMatchesMode(key, mode)) continue;
                    done += SetLevelFinish(inst, getVal, setVal, key, true, true, true);
                }
                var save = gsmType.GetMethod("SaveGameConfig", Type.EmptyTypes);
                save?.Invoke(inst, null);
                Bootstrap.Log("通关" + mode + ": " + done + " 关");
            }
            catch (System.Exception ex) { Bootstrap.Log("通关" + mode + " 异常: " + ex.Message); }
        }

        /// <summary>关卡 key 前缀匹配（save.res + LevelResource.json 实测 0.27）：
        /// 冒险=LevelX_Y（排除 TryLevel），挑战=Challenge_，小游戏=MiniGames_/Shooting_/Try_，拼图=Vase_/IZM_，生存=Survival_，IZM2=IZM2_。</summary>
        static bool KeyMatchesMode(string key, string mode)
        {
            try
            {
                switch (mode)
                {
                    case "Adventure": return key.StartsWith("Level") && !key.StartsWith("TryLevel");
                    case "Challenge": return key.StartsWith("Challenge");
                    case "MiniGame": return key.StartsWith("MiniGames") || key.StartsWith("Shooting") || key.StartsWith("Try");
                    case "PuzzleGame": return key.StartsWith("Vase") || key.StartsWith("IZM") && !key.StartsWith("IZM2");
                    case "Survival": return key.StartsWith("Survival");
                    case "IZM2": return key.StartsWith("IZM2");
                }
            }
            catch { }
            return false;
        }

        /// <summary>通用：读取官方关卡注册表 Asset/Config/Level/LevelResource.json 全部 SaveKey（0.27 共 551 关）。失败返回空表。</summary>
        static System.Collections.Generic.List<string> LoadOfficialLevelKeys()
        {
            var keys = new System.Collections.Generic.List<string>();
            try
            {
                var fa = Godot.FileAccess.Open("res://Asset/Config/Level/LevelResource.json", Godot.FileAccess.ModeFlags.Read);
                if (fa == null) { Bootstrap.Log("关卡注册表: 打开 LevelResource.json 失败"); return keys; }
                string jsonText = fa.GetAsText();
                fa.Close();
                var json = new Godot.Json();
                if (json.Parse(jsonText, false) != Godot.Error.Ok) { Bootstrap.Log("关卡注册表: LevelResource.json 解析失败"); return keys; }
                CollectSaveKeys(json.Data, keys);
            }
            catch (System.Exception ex) { Bootstrap.Log("关卡注册表异常: " + ex.Message); }
            return keys;
        }

        /// <summary>通用：对单个关卡 key 设置通关标记（Finish/Mower/Difficult），返回 1=成功 0=跳过。</summary>
        static int SetLevelFinish(object gsm, System.Reflection.MethodInfo getVal, System.Reflection.MethodInfo setVal, string key, bool mower, bool difficult, bool finish)
        {
            try
            {
                Godot.Collections.Dictionary levelValue = null;
                try { levelValue = getVal.Invoke(gsm, new object[] { key }) as Godot.Collections.Dictionary; } catch { }
                if (levelValue == null) levelValue = new Godot.Collections.Dictionary();
                if (!levelValue.ContainsKey("Key")) levelValue["Key"] = new Godot.Collections.Dictionary();
                if (finish) { try { levelValue["Key"].AsGodotDictionary()["Finish"] = 1; } catch { } }
                if (mower) levelValue["Mower"] = true;
                if (difficult) levelValue["Difficult"] = true;
                if (levelValue.ContainsKey("Star")) levelValue["Star"] = 3;
                levelValue["Reward"] = true;
                try { setVal.Invoke(gsm, new object[] { key, levelValue }); return 1; } catch { }
            }
            catch { }
            return 0;
        }

        /// <summary>商店全部物品解锁（复刻官方 CommandManager.GetShopAllItemsButtonPressed）：
        /// 遍历 ResourceManager.SHOPS 所有商店页面条目，Feature 类型 SetFeatureValue(openMaxNum)，TowerDefensePacket 类型设 Unlock=true，然后保存。</summary>
        public static void GetShopAllItems()
        {
            try
            {
                var rm = FindType("ResourceManager");
                if (rm == null) { Bootstrap.Log("商店: 找不到 ResourceManager"); return; }
                var inst = rm.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (inst == null) { Bootstrap.Log("商店: ResourceManager.Instance 为空"); return; }
                var shopsF = rm.GetField("SHOPS", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var shops = shopsF != null ? shopsF.GetValue(inst) : null;
                if (!(shops is System.Collections.IDictionary sdict)) { Bootstrap.Log("商店: SHOPS 为空"); return; }
                var gsmType = FindType("GameSaveManager");
                var gsm = gsmType?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (gsm == null || gsmType == null) { Bootstrap.Log("商店: GameSaveManager 为空"); return; }
                var setFeat = gsmType.GetMethod("SetFeatureValue", new Type[] { typeof(string), typeof(Godot.Variant) });
                var getPkt = gsmType.GetMethod("GetTowerDefensePacketValue", new Type[] { typeof(string) });
                var setPkt = gsmType.GetMethod("SetTowerDefensePacketValue", new Type[] { typeof(string), typeof(Godot.Collections.Dictionary) });
                string[] types = new string[] { "Total", "Item", "Packet", "Custom", "Garden" };
                int count = 0;
                foreach (System.Collections.DictionaryEntry kv in sdict)
                {
                    var shop = kv.Value;
                    if (shop == null) continue;
                    try
                    {
                        var initM = shop.GetType().GetMethod("Init", Type.EmptyTypes);
                        initM?.Invoke(shop, null);
                    }
                    catch { }
                    var gptl = FindMethodByPrefix(shop.GetType(), "GetPageTypeList", new Type[] { typeof(string) });
                    if (gptl == null) continue;
                    foreach (var type in types)
                    {
                        object pages = null;
                        try { pages = gptl.Invoke(shop, new object[] { type }); } catch { }
                        if (!(pages is System.Collections.IEnumerable pageEnum)) continue;
                        foreach (var pageObj in pageEnum)
                        {
                            var itemList = FindPropOrFieldVal(pageObj, "itemList");
                            if (!(itemList is System.Collections.IEnumerable itemEnum)) continue;
                            foreach (var itemObj in itemEnum)
                            {
                                var stageList = FindPropOrFieldVal(itemObj, "stageList");
                                if (!(stageList is System.Collections.IEnumerable stageEnum)) continue;
                                foreach (var stageObj in stageEnum)
                                {
                                    var saveType = FindPropOrFieldVal(stageObj, "saveType") as string;
                                    var saveKey = FindPropOrFieldVal(stageObj, "saveKey") as string;
                                    if (string.IsNullOrEmpty(saveType) || string.IsNullOrEmpty(saveKey)) continue;
                                    try
                                    {
                                        if (saveType == "Feature")
                                        {
                                            var openMaxNum = FindPropOrFieldVal(stageObj, "openMaxNum");
                                            if (openMaxNum != null && setFeat != null)
                                            {
                                                setFeat.Invoke(gsm, new object[] { saveKey, Godot.Variant.From(openMaxNum) });
                                                count++;
                                            }
                                        }
                                        else if (saveType == "TowerDefensePacket" && getPkt != null && setPkt != null)
                                        {
                                            Godot.Collections.Dictionary pv = getPkt.Invoke(gsm, new object[] { saveKey }) as Godot.Collections.Dictionary;
                                            if (pv == null) pv = new Godot.Collections.Dictionary();
                                            pv["Unlock"] = true;
                                            setPkt.Invoke(gsm, new object[] { saveKey, pv });
                                            count++;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                var saveM = gsmType.GetMethod("SaveGameConfig", Type.EmptyTypes);
                saveM?.Invoke(gsm, null);
                Bootstrap.Log("商店全部物品: " + count + " 项已解锁");
            }
            catch (System.Exception ex) { Bootstrap.Log("商店全部物品异常: " + ex.Message); }
        }

        /// <summary>重置脑子（复刻官方 CommandManager.ResetAllBrains）：清空 Brain feature 的 brainLine，再按地图行数重建。</summary>
        public static void ResetAllBrains()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                object control = null;
                try { var cp = _tdmType.GetProperty("CurrentControl", BindingFlags.Public | BindingFlags.Static); if (cp != null) control = cp.GetValue(null); } catch { }
                if (control == null) { Bootstrap.Log("重置脑子: 无当前关卡"); return; }
                var gf = FindMethodExact(control.GetType(), "GetFeature", new Type[] { typeof(Godot.StringName) });
                var brain = gf != null ? gf.Invoke(control, new object[] { new Godot.StringName("Brain") }) : null;
                if (brain == null || !GodotObject.IsInstanceValid(brain as GodotObject)) { Bootstrap.Log("重置脑子: 无 Brain feature"); return; }
                var bl = FindFieldVal(brain, "brainLine");
                if (bl is System.Collections.IList blist)
                {
                    for (int i = 0; i < blist.Count; i++)
                    {
                        if (blist[i] is Godot.Node n && GodotObject.IsInstanceValid(n)) { try { n.QueueFree(); } catch { } }
                    }
                    try { blist.Clear(); } catch { }
                }
                var cm = FindMethodByPrefix(brain.GetType(), "CreateBrain", new Type[] { typeof(int), typeof(int) });
                if (cm == null) return;
                int gridNum = 5;
                try
                {
                    var mfM = _tdmType.GetMethod("GetMapFeature", Type.EmptyTypes);
                    object mapFeat = mfM != null ? mfM.Invoke(null, null) : null;
                    if (mapFeat != null)
                    {
                        var cfg = FindPropOrFieldVal(mapFeat, "config");
                        var gn = FindPropOrFieldVal(cfg, "gridNum");
                        if (gn is Godot.Vector2I gv && gv.Y > 0 && gv.Y <= 20) gridNum = gv.Y;
                    }
                }
                catch { }
                var getLineUse = _tdmType.GetMethod("GetMapLineUse", new Type[] { typeof(int) });
                int done = 0;
                for (int j = 1; j <= gridNum; j++)
                {
                    bool lineUse = false;
                    try { if (getLineUse != null) lineUse = (bool)getLineUse.Invoke(tdm, new object[] { j }); } catch { }
                    if (!lineUse) continue;
                    try { cm.Invoke(brain, new object[] { j, -1 }); done++; } catch { }
                }
                Bootstrap.Log("重置脑子: " + done + " 行");
            }
            catch (System.Exception ex) { Bootstrap.Log("重置脑子异常: " + ex.Message); }
        }

        /// <summary>添加/移除屏幕天气效果（复刻官方 CommandManager.AddRainButtonPressed 等）。
        /// name=Rain/Storm，add=true 添加、false 移除。需关卡有 ScreenEffect feature。</summary>
        public static void ToggleScreenEffect(string name, bool add)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                object control = null;
                // 0.26：currentControl 是字段（不是 CurrentControl 属性）！
                try { var cp = _tdmType.GetProperty("CurrentControl", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); if (cp != null) control = cp.GetValue(null); } catch { }
                if (control == null) control = FindFieldVal(tdm, "currentControl");
                if (control == null) { Bootstrap.Log("屏幕效果: 无当前关卡"); return; }
                // GetFeature 参数是 StringName（不是 string）！用 FindMethodExact 避开泛型 GetFeature<T> 歧义
                var gf = FindMethodExact(control.GetType(), "GetFeature", new Type[] { typeof(Godot.StringName) });
                var se = gf != null ? gf.Invoke(control, new object[] { new Godot.StringName("ScreenEffect") }) : null;
                if (se == null) { Bootstrap.Log("屏幕效果: 无 ScreenEffect feature"); return; }
                var hasM = FindMethodByPrefix(se.GetType(), "HasScreenEffect", new Type[] { typeof(string) });
                var addM = FindMethodByPrefix(se.GetType(), "AddScreenEffect", new Type[] { typeof(string) });
                var delM = FindMethodByPrefix(se.GetType(), "DeleteScreenEffect", new Type[] { typeof(string) });
                if (hasM == null) return;
                bool exists = (bool)hasM.Invoke(se, new object[] { name });
                if (add && !exists && addM != null) { addM.Invoke(se, new object[] { name }); Bootstrap.Log("屏幕效果: 已添加 " + name); }
                else if (!add && exists && delM != null) { delM.Invoke(se, new object[] { name }); Bootstrap.Log("屏幕效果: 已移除 " + name); }
                else Bootstrap.Log("屏幕效果: " + name + (add ? " 已存在" : " 不存在"));
                // 兜底：直接删除场景里的雨/风暴效果节点（确保"直接删掉状态"——DeleteScreenEffect 可能只从字典删）
                if (!add) DeleteScreenEffectNodes(name);
            }
            catch (System.Exception ex) { Bootstrap.Log("屏幕效果异常: " + ex.Message); }
        }

        /// <summary>直接删除场景里的雨/风暴效果节点（ScreenEffectRain/ScreenEffectStorm），确保停雨/停雷暴生效。</summary>
        static void DeleteScreenEffectNodes(string name)
        {
            try
            {
                var root = _lastRoot;
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                string prefix = name == "Storm" ? "ScreenEffectStorm" : "ScreenEffectRain";
                WalkDeleteEffect(root, prefix);
            }
            catch { }
        }

        static void WalkDeleteEffect(Node node, string prefix)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child.GetType().Name.StartsWith(prefix)) child.QueueFree();
                }
                catch { }
                WalkDeleteEffect(child, prefix);
            }
        }

        /// <summary>波次暂停：设 CommandManager.debugWavePaused=true（官方 TowerDefenseBattleFeatureWave.WavePhysicsProcess 读到就跳过波次处理）。
        /// 由 OnFrame 每帧保持（防游戏重置）。</summary>
        public static void ApplyWavePaused()
        {
            try
            {
                if (!ModSettings.WavePaused) return;
                var cm = FindType("CommandManager");
                if (cm == null) return;
                var inst = cm.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (inst == null) return;
                var f = cm.GetField("debugWavePaused", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(bool) && !(bool)f.GetValue(inst))
                    f.SetValue(inst, true);
            }
            catch { }
        }

        // ===== 刷怪倍数 =====
        static readonly System.Collections.Generic.Dictionary<string, double> _spawnOrigNums = new System.Collections.Generic.Dictionary<string, double>();
        static int _spawnMultLastWave = -1;
        static object _spawnMultCfg;
        static bool _spawnMultLogged;

        /// <summary>遍历 TowerDefenseControlNew 的 features 字典，找类型为 TowerDefenseBattleFeatureWave 的实例
        /// （GetFeature("Wave") 失败时的兜底，不依赖 key 名）。</summary>
        static object FindWaveFeatureInstance(object control)
        {
            try
            {
                var t = control.GetType();
                while (t != null)
                {
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        var ft = f.FieldType;
                        if (!ft.IsGenericType) continue;
                        if (ft.GetGenericTypeDefinition() != typeof(System.Collections.Generic.Dictionary<,>)) continue;
                        var ga = ft.GetGenericArguments();
                        if (ga.Length != 2 || ga[1].Name != "TowerDefenseBattleFeature") continue;
                        var dict = f.GetValue(control);
                        if (dict == null) continue;
                        var valuesProp = ft.GetProperty("Values");
                        if (valuesProp == null) continue;
                        var vals = valuesProp.GetValue(dict) as System.Collections.IEnumerable;
                        if (vals == null) continue;
                        foreach (var v in vals)
                        {
                            if (v != null && v.GetType().Name == "TowerDefenseBattleFeatureWave") return v;
                        }
                    }
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        /// <summary>刷怪倍数：把 wave 配置每波 Spawn 的 Num 从原始值 × 倍数（用户调 ModSettings.SpawnMultiplier）。
        /// 波次变化时应用（当前波+下一波），原始 Num 首次保存，改倍数后后续波用新倍数。关卡切换重置。</summary>
        public static void ApplySpawnMultiplier()
        {
            try
            {
                int mult = ModSettings.SpawnMultiplier;
                if (mult <= 1) return;
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                object control = FindFieldVal(tdm, "currentControl");   // 字段（官方 CommandManager IL 确认）
                if (control == null) control = FindPropOrFieldVal(tdm, "currentControl");
                if (control == null) return;
                object wave = null;
                // 1) GetFeature("Wave")（官方 TowerDefenseControlNew.GetFeature(StringName) 查 features 字典；用 FindMethodExact 避开泛型歧义）
                try
                {
                    var gf = FindMethodExact(control.GetType(), "GetFeature", new Type[] { typeof(Godot.StringName) });
                    if (gf != null) wave = gf.Invoke(control, new object[] { new Godot.StringName("Wave") });
                }
                catch { }
                // 2) 兜底：遍历 features 字典找 TowerDefenseBattleFeatureWave 值
                if (wave == null) wave = FindWaveFeatureInstance(control);
                if (wave == null)
                {
                    if (!_spawnMultLogged) { _spawnMultLogged = true; Bootstrap.Log("刷怪倍数: 未找到 Wave feature"); }
                    return;
                }
                var cfg = FindPropOrFieldVal(wave, "config");
                if (cfg == null) return;
                if (!ReferenceEquals(_spawnMultCfg, cfg)) { _spawnMultCfg = cfg; _spawnMultLastWave = -1; _spawnOrigNums.Clear(); }
                int curWave = 0;
                var cw = FindFieldVal(wave, "currentWave");
                if (cw is int cwi) curWave = cwi;
                else if (cw is double cwd) curWave = (int)cwd;
                if (curWave == _spawnMultLastWave) return;
                _spawnMultLastWave = curWave;
                var waveArr = FindPropOrFieldVal(cfg, "wave");   // get_wave 属性
                if (!(waveArr is System.Collections.IEnumerable waveEnum)) return;
                int waveIdx = 0;
                foreach (var waveObj in waveEnum)
                {
                    if (waveObj == null) { waveIdx++; continue; }
                    if (waveIdx != curWave && waveIdx != curWave + 1) { waveIdx++; continue; }
                    // spawn 是小写字段（Array<TowerDefenseLevelSpawnConfig>）
                    var spawnList = FindFieldVal(waveObj, "spawn");
                    if (spawnList == null) spawnList = FindFieldVal(waveObj, "Spawn");
                    if (spawnList == null) spawnList = FindPropOrFieldVal(waveObj, "spawn");
                    if (!(spawnList is System.Collections.IEnumerable spawnEnum)) { waveIdx++; continue; }
                    int spIdx = 0;
                    foreach (var spawnObj in spawnEnum)
                    {
                        if (spawnObj == null) { spIdx++; continue; }
                        try
                        {
                            string key = waveIdx + "_" + spIdx;
                            // num 是小写 Int32 字段（0.26 实测），兜底大写 Num double
                            var numF = spawnObj.GetType().GetField("num", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (numF == null) numF = spawnObj.GetType().GetField("Num", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            double curVal = -1;
                            if (numF != null)
                            {
                                var v = numF.GetValue(spawnObj);
                                if (v is int iv) curVal = iv;
                                else if (v is double dv) curVal = dv;
                            }
                            if (curVal < 0)
                            {
                                var numP = spawnObj.GetType().GetProperty("num", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (numP == null) numP = spawnObj.GetType().GetProperty("Num", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (numP != null && numP.PropertyType == typeof(int)) curVal = (int)numP.GetValue(spawnObj);
                                else if (numP != null && numP.PropertyType == typeof(double)) curVal = (double)numP.GetValue(spawnObj);
                            }
                            if (curVal < 0) { spIdx++; continue; }
                            double orig;
                            if (!_spawnOrigNums.TryGetValue(key, out orig)) { orig = curVal; _spawnOrigNums[key] = orig; }
                            double target = Math.Round(orig * mult, 1);
                            if (Math.Abs(curVal - target) > 0.001)
                            {
                                if (numF != null && numF.FieldType == typeof(int)) numF.SetValue(spawnObj, (int)target);
                                else if (numF != null) numF.SetValue(spawnObj, target);
                                else
                                {
                                    var numP2 = spawnObj.GetType().GetProperty("num", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                    if (numP2 == null) numP2 = spawnObj.GetType().GetProperty("Num", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                    if (numP2 != null) numP2.SetValue(spawnObj, (int)target);
                                }
                            }
                        }
                        catch { }
                        spIdx++;
                    }
                    waveIdx++;
                }
                if (!_spawnMultLogged) { _spawnMultLogged = true; Bootstrap.Log("刷怪倍数: " + mult + "x（每波应用，原始值已缓存）"); }
            }
            catch { }
        }

        // ===== 自制关卡（咖啡豆赌博 / 导入关卡）状态 =====
        static bool _customLevelInit;       // 卡槽是否已改造
        static int _customWave;             // 当前波次索引
        static int _customWaveTimer;        // 波次帧计数
        static int _customExitTimer;        // 不在对局帧计数（用于退出模式）
        static bool _customNoSpawnSaved;    // 进入时 NoZombieSpawn 原值
        static bool _customNoSpawnTouched;  // 是否已临时开启禁止出怪
        static bool _coffeeGamble;          // 咖啡豆赌博模式（种咖啡豆随机生成；导入关卡为 false）
        class CustomWaveDef { public int delay; public string zombie; public int num; }
        static System.Collections.Generic.List<CustomWaveDef> _customWaves;   // 当前波次列表（导入或默认）
        static string _importedMap;
        static string _importedCardMode;
        static string _importedTemplate;      // 官方关卡模板（res:// 路径，可选）：克隆官方配置为基础
        static System.Collections.Generic.List<(string name, string val)> _importedFeatures;  // 机制 FEATURE=name:params
        static System.Collections.Generic.List<string> _importedCards;
        static System.Collections.Generic.List<CustomWaveDef> _importedWaves;
        static System.Collections.Generic.List<(string id, string prop, string val)> _importedOverrides;   // 属性覆盖 id:prop=val
        static System.Collections.Generic.List<(int x, int y, string id)> _importedPrePlant;              // 初始植物 x,y:id
        static System.Collections.Generic.List<(int x, int y, string id)> _importedPreZombie;             // 初始僵尸 x,y:id
        static bool _customPreDone;            // 初始布局是否已生成
        static int _overrideApplyTimer;        // 属性覆盖应用降频
        static int _globalOverrideTimer;       // 全局覆盖应用降频
        static bool _consoleReady;             // 整树 ProcessMode=Always 已确保（切场景重新挂载后重置）
        static int _consoleTimer;              // 控制台挂载降频
        static Node _consoleRoot;              // 最近一帧根节点（控制台查找/呼出用）
        static double _consoleKeyLastMs = -1000; // 控制台按键防重（300ms 内只触发一次）
        static bool _capPrev;                  // 音量减轮询边沿
        static System.Collections.Generic.List<(string id, string prop, string val)> _ovWalkList;      // 当前要应用的覆盖列表（OverrideWalk 用）
        static System.Collections.Generic.List<(string id, string prop, string val)> _globalOverrides; // 全局植物属性覆盖（所有关卡生效）
        static bool _globalOverridesLoaded;
        static string _globalOverridesPath;

        /// <summary>点「自制关卡（阳光豆赌博）」进入：进对局 + 开启自制关卡模式。</summary>
        public static void LaunchCustomLevel()
        {
            try
            {
                ModSettings.CustomLevelActive = true;
                _customLevelInit = false;
                _customWave = 0;
                _customWaveTimer = 0;
                _customExitTimer = 0;
                _customNoSpawnSaved = ModSettings.NoZombieSpawn;
                _customNoSpawnTouched = false;
                _coffeeGamble = true;
                _customWaves = DefaultWaves();
                object cfg = CustomLevelSource.GetTestLevelConfig();
                if (cfg == null) { Bootstrap.Log("自制关卡: 配置加载失败 " + CustomLevelSource.TestLevelPath); return; }
                // 改造配置：前院地图 + 预设固定卡槽（咖啡豆，禁自选）
                ApplyCustomLevelConfig(cfg);
                EnterLevelWithConfig(cfg);
            }
            catch (System.Exception ex) { Bootstrap.Log("自制关卡异常: " + ex.Message); }
        }

        /// <summary>改造自制关卡配置：map=Frontlawn(前院) + _packetBankMethod=PRESET(固定预设，禁自选) + packetBankList=咖啡豆x6。失败只日志不阻断。</summary>
        static void ApplyCustomLevelConfig(object cfg)
        {
            try
            {
                var mapF = FindFieldInfo(cfg, "map");
                if (mapF != null && mapF.FieldType == typeof(string)) { mapF.SetValue(cfg, "Frontlawn"); Bootstrap.Log("自制关卡: 地图=Frontlawn(前院)"); }
                var bm = FindFieldInfo(cfg, "_packetBankMethod");
                if (bm != null && bm.FieldType.IsEnum)
                {
                    bool set = false;
                    try { bm.SetValue(cfg, Enum.Parse(bm.FieldType, "PRESET", true)); set = true; } catch { }
                    if (!set) { try { bm.SetValue(cfg, System.Enum.ToObject(bm.FieldType, 2)); set = true; } catch { } }
                    if (set) Bootstrap.Log("自制关卡: 卡槽模式=PRESET(禁自选)");
                }
                var coffeeCfg = GetCoffeeBeanConfig();
                var pbl = FindFieldInfo(cfg, "packetBankList");
                if (pbl != null && pbl.FieldType.IsArray && coffeeCfg != null)
                {
                    var elem = pbl.FieldType.GetElementType();
                    var arr = System.Array.CreateInstance(elem, 6);
                    for (int i = 0; i < 6; i++) { try { arr.SetValue(coffeeCfg, i); } catch { } }
                    pbl.SetValue(cfg, arr);
                    Bootstrap.Log("自制关卡: 预设卡槽=咖啡豆 x6");
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("自制关卡配置改造异常: " + ex.Message); }
        }

        /// <summary>获取咖啡豆卡配置（GetPacketIds 里匹配 Coffeebean/CoffeeBean，fallback 匹配 Coffee 排除 leaf/Blover/Sheild）。</summary>
        static object GetCoffeeBeanConfig()
        {
            try
            {
                var plantIds = GetPacketIds(true);
                if (plantIds == null) return null;
                string id = null;
                foreach (var pid in plantIds)
                {
                    if (pid == null) continue;
                    if (pid.IndexOf("Coffeebean", StringComparison.OrdinalIgnoreCase) >= 0 || pid.IndexOf("CoffeeBean", StringComparison.OrdinalIgnoreCase) >= 0) { id = pid; break; }
                }
                if (id == null)
                {
                    foreach (var pid in plantIds)
                    {
                        if (pid == null || pid.IndexOf("Coffee", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (pid.IndexOf("leaf", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (pid.IndexOf("Blover", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (pid.IndexOf("Sheild", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        id = pid; break;
                    }
                }
                if (id == null) { Bootstrap.Log("自制关卡: 未找到咖啡豆卡 id"); return null; }
                Bootstrap.Log("自制关卡: 咖啡豆卡 id=" + id);
                return GetConfig(id);
            }
            catch (System.Exception ex) { Bootstrap.Log("自制关卡咖啡豆异常: " + ex.Message); return null; }
        }

        /// <summary>默认 3 波普通僵尸（3/5/8，每 25 秒）。</summary>
        static System.Collections.Generic.List<CustomWaveDef> DefaultWaves()
        {
            var l = new System.Collections.Generic.List<CustomWaveDef>();
            l.Add(new CustomWaveDef { delay = 25, zombie = "ZombieNormal", num = 3 });
            l.Add(new CustomWaveDef { delay = 25, zombie = "ZombieNormal", num = 5 });
            l.Add(new CustomWaveDef { delay = 25, zombie = "ZombieNormal", num = 8 });
            return l;
        }

        /// <summary>解析 .pvzlevel 关卡文件（自定义紧凑格式）为内存数据。失败返回 false。</summary>
        static bool ParsePvzLevelFile(string path)
        {
            try
            {
                if (!GodotFileExists(path)) { Bootstrap.Log("导入关卡: 文件不存在 " + path); return false; }
                string[] lines = ReadLinesGodot(path);
                _importedMap = null; _importedCardMode = null; _importedTemplate = null;
                _importedFeatures = new System.Collections.Generic.List<(string, string)>();
                _importedCards = new System.Collections.Generic.List<string>();
                _importedWaves = new System.Collections.Generic.List<CustomWaveDef>();
                _importedOverrides = new System.Collections.Generic.List<(string, string, string)>();
                _importedPrePlant = new System.Collections.Generic.List<(int, int, string)>();
                _importedPreZombie = new System.Collections.Generic.List<(int, int, string)>();
                _customPreDone = false;
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToUpperInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "MAP": _importedMap = val; break;
                        case "TEMPLATE": _importedTemplate = val; break;
                        case "FEATURE":
                            { int cidx = val.IndexOf(':'); _importedFeatures.Add((cidx > 0 ? val.Substring(0, cidx).Trim() : val.Trim(), val)); }
                            break;
                        case "CARDMODE": _importedCardMode = val.ToLowerInvariant(); break;
                        case "CARDS":
                            foreach (var c in val.Split(','))
                            {
                                var t = c.Trim();
                                if (t.Length == 0) continue;
                                int x = t.IndexOf('×');
                                if (x > 0 && int.TryParse(t.Substring(x + 1), out int cnt))
                                {
                                    string id = t.Substring(0, x).Trim();
                                    for (int i = 0; i < cnt; i++) _importedCards.Add(id);
                                }
                                else _importedCards.Add(t);
                            }
                            break;
                        case "WAVE":
                            // 格式：delay:zombie×num（或 delay:zombie，默认 num=1）
                            var d = new CustomWaveDef { delay = 25, zombie = "ZombieNormal", num = 1 };
                            string rest = val;
                            int colon = val.IndexOf(':');
                            if (colon > 0)
                            {
                                if (int.TryParse(val.Substring(0, colon).Trim(), out int dv)) d.delay = dv;
                                rest = val.Substring(colon + 1);
                            }
                            int x2 = rest.IndexOf('×');
                            if (x2 > 0)
                            {
                                d.zombie = rest.Substring(0, x2).Trim();
                                if (int.TryParse(rest.Substring(x2 + 1).Trim(), out int c2)) d.num = c2;
                            }
                            else
                            {
                                int xi = rest.ToLowerInvariant().IndexOf('x');
                                if (xi > 0)
                                {
                                    d.zombie = rest.Substring(0, xi).Trim();
                                    if (int.TryParse(rest.Substring(xi + 1).Trim(), out int c3)) d.num = c3;
                                }
                                else d.zombie = rest.Trim();
                            }
                            if (d.zombie.Length == 0) d.zombie = "ZombieNormal";
                            _importedWaves.Add(d);
                            break;
                        case "OVERRIDE":
                            // 格式：id:prop=val,prop=val （cost/cooldown/hp/fireInterval/damage/range/sunProduce/speed）
                            {
                                int co = val.IndexOf(':');
                                if (co > 0)
                                {
                                    string id = val.Substring(0, co).Trim();
                                    foreach (var kv in val.Substring(co + 1).Split(','))
                                    {
                                        var t = kv.Trim();
                                        int eq2 = t.IndexOf('=');
                                        if (eq2 > 0) _importedOverrides.Add((id, t.Substring(0, eq2).Trim().ToLowerInvariant(), t.Substring(eq2 + 1).Trim()));
                                    }
                                }
                            }
                            break;
                        case "PREPLANT":
                        case "PREZOMBIE":
                            // 格式：x,y:id
                            {
                                int cc = val.IndexOf(':');
                                if (cc > 0)
                                {
                                    var pos = val.Substring(0, cc).Trim().Split(',');
                                    string id = val.Substring(cc + 1).Trim();
                                    if (pos.Length == 2 && int.TryParse(pos[0].Trim(), out int px) && int.TryParse(pos[1].Trim(), out int py))
                                    {
                                        if (key == "PREPLANT") _importedPrePlant.Add((px, py, id));
                                        else _importedPreZombie.Add((px, py, id));
                                    }
                                }
                            }
                            break;
                    }
                }
                Bootstrap.Log("导入关卡: 解析完成 地图=" + (_importedMap ?? "默认") + " 卡=" + _importedCards.Count + " 波=" + _importedWaves.Count);
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("导入关卡解析异常: " + ex.Message); return false; }
        }

        /// <summary>导入 .pvzlevel 关卡文件：解析 → 应用地图/卡槽/波次 → 进对局。供 ModUI「导入关卡」调用。</summary>
        public static void ImportLevelFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (!ParsePvzLevelFile(path)) return;
                ModSettings.CustomLevelActive = true;
                _customLevelInit = false;
                _customWave = 0;
                _customWaveTimer = 0;
                _customExitTimer = 0;
                _customNoSpawnSaved = ModSettings.NoZombieSpawn;
                _customNoSpawnTouched = false;
                _coffeeGamble = false;
                _customWaves = (_importedWaves != null && _importedWaves.Count > 0) ? _importedWaves : DefaultWaves();
                // 基础配置：优先官方关卡模板（克隆官方全部机制），否则内置测试关卡
                object cfg;
                if (!string.IsNullOrEmpty(_importedTemplate))
                {
                    cfg = Godot.GD.Load(_importedTemplate);
                    if (cfg == null) { Bootstrap.Log("导入关卡: 官方模板加载失败 " + _importedTemplate); return; }
                    Bootstrap.Log("导入关卡: 已加载官方模板 " + _importedTemplate);
                }
                else
                {
                    cfg = CustomLevelSource.GetTestLevelConfig();
                    if (cfg == null) { Bootstrap.Log("导入关卡: 基础配置加载失败"); return; }
                }
                // 地图
                if (!string.IsNullOrEmpty(_importedMap))
                {
                    var mapF = FindFieldInfo(cfg, "map");
                    if (mapF != null && mapF.FieldType == typeof(string)) mapF.SetValue(cfg, _importedMap);
                }
                // 卡槽模式（preset/choose）
                var bm = FindFieldInfo(cfg, "_packetBankMethod");
                if (bm != null && bm.FieldType.IsEnum)
                {
                    string mode = _importedCardMode ?? "preset";
                    string enumName = mode.StartsWith("choose") ? "CHOOSE" : "PRESET";
                    try { bm.SetValue(cfg, Enum.Parse(bm.FieldType, enumName, true)); } catch { }
                }
                // 卡槽列表
                if (_importedCards != null && _importedCards.Count > 0)
                {
                    var pbl = FindFieldInfo(cfg, "packetBankList");
                    if (pbl != null && pbl.FieldType.IsArray)
                    {
                        var elem = pbl.FieldType.GetElementType();
                        var arr = System.Array.CreateInstance(elem, _importedCards.Count);
                        for (int i = 0; i < _importedCards.Count; i++)
                        {
                            var cc = GetConfig(_importedCards[i]);
                            if (cc != null) { try { arr.SetValue(cc, i); } catch { } }
                        }
                        pbl.SetValue(cfg, arr);
                    }
                }
                // 应用关卡机制（FEATURE=name），进对局开对应 MOD 功能
                if (_importedFeatures != null && _importedFeatures.Count > 0)
                {
                    foreach (var (fname, _) in _importedFeatures)
                    {
                        string fn = fname.ToUpperInvariant();
                        if (fn == "FOG") ModSettings.ForceFog = true;
                        else if (fn == "RAIN") ModSettings.ForceRain = true;
                        else if (fn == "BOSS") ModSettings.BossNoBow = true;
                        else if (fn == "SEEDRANDOM") ModSettings.SeedBankRandom = true;
                        else if (fn == "CHOOSEALL") ModSettings.CanChooseAll = true;
                        else if (fn == "NOREDLINE") ModSettings.IgnoreRedLine = true;
                        else if (fn == "NOSPAWN") ModSettings.NoZombieSpawn = true;
                        else if (fn == "WAVEPAUSE") ModSettings.WavePaused = true;
                        else if (fn == "VASERANDOM") ModSettings.VaseRandom = true;
                        else if (fn == "CONVEYOR") ModSettings.ConveyorFast = true;
                    }
                    ModSettings.Save();
                    Bootstrap.Log("导入关卡: 应用机制 " + _importedFeatures.Count + " 项");
                }
                EnterLevelWithConfig(cfg);
                Bootstrap.Log("导入关卡: 进入对局 " + FileNameNoExt(path));
            }
            catch (System.Exception ex) { Bootstrap.Log("导入关卡异常: " + ex.Message); }
        }

        /// <summary>导出图鉴数据 pvz_data.json（关卡制作器加载用）：全图鉴 id+中文名+卡层属性 + 地图清单。返回文件路径。</summary>
        public static string ExportPvzData()
        {
            try
            {
                string json = BuildPvzDataJson();
                string dir = GetExeDir();
                string path = dir + (dir.EndsWith("\\") || dir.EndsWith("/") ? "" : "\\") + "pvz_data.json";
                WriteTextGodot(path, json);
                Bootstrap.Log("导出图鉴数据: " + path);
                return path;
            }
            catch (System.Exception ex) { Bootstrap.Log("导出图鉴数据异常: " + ex.Message); return null; }
        }

        /// <summary>游戏 exe 所在目录。</summary>
        static string GetExeDir()
        {
            try { var exe = Godot.OS.GetExecutablePath(); int si = LastSlashIdx(exe); if (si > 0) return ReplaceChars(exe.Substring(0, si), '/', '\\'); } catch { }
            return Godot.ProjectSettings.GlobalizePath("user://");
        }

        /// <summary>构建图鉴数据 JSON（真中文名+属性+地图清单）。</summary>
        static string BuildPvzDataJson()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            // 植物
            sb.AppendLine("  \"plants\": [");
            var pRows = new System.Collections.Generic.List<string>();
            var plants = GetPacketIds(true);
            foreach (var id in plants) { if (string.IsNullOrEmpty(id)) continue; pRows.Add("    " + DumpCardJson(id, true)); }
            sb.AppendLine(string.Join(",\n", pRows));
            sb.AppendLine("  ],");
            // 僵尸
            sb.AppendLine("  \"zombies\": [");
            var zRows = new System.Collections.Generic.List<string>();
            var zombies = GetPacketIds(false);
            foreach (var id in zombies) { if (string.IsNullOrEmpty(id)) continue; zRows.Add("    " + DumpCardJson(id, false)); }
            sb.AppendLine(string.Join(",\n", zRows));
            sb.AppendLine("  ],");
            // 地图（内置常用 + 格子数）
            sb.AppendLine("  \"maps\": [");
            var mRows = new System.Collections.Generic.List<string>();
            var maps = new (string id, string name, int cols, int rows)[]
            {
                ("Frontlawn", "前院", 9, 5),
                ("FrontlawnNight", "前院·夜晚", 9, 5),
                ("Pool", "泳池", 9, 6),
                ("PoolNight", "泳池·夜晚", 9, 6),
                ("Roof", "屋顶", 9, 5),
                ("RoofNight", "屋顶·夜晚", 9, 5),
                ("Fog", "迷雾", 9, 6),
                ("NightFog", "迷雾·夜晚", 9, 6),
                ("TreasureIslandFrontLawn", "藏宝岛前院", 9, 5),
                ("Vase", "罐子关", 9, 5),
            };
            foreach (var m in maps) mRows.Add("    { \"id\": \"" + m.id + "\", \"name\": \"" + m.name + "\", \"cols\": " + m.cols + ", \"rows\": " + m.rows + " }");
            sb.AppendLine(string.Join(",\n", mRows));
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>导出关卡制作器资源包（levelmaker/）：图鉴数据（真中文）+地图背景+卡牌图标。返回目录。</summary>
        public static string ExportLevelMakerAssets()
        {
            try
            {
                string lmDir = GetLevelMakerDir();
                string mapsDir = lmDir + "\\maps";
                Godot.DirAccess.MakeDirAbsolute(lmDir);
                Godot.DirAccess.MakeDirAbsolute(mapsDir);
                // 图鉴数据（真中文名）
                WriteTextGodot(lmDir + "\\pvz_data.json", BuildPvzDataJson());
                // 地图背景（游戏资源渲染保存 PNG）
                var maps = new (string id, string res)[]
                {
                    ("Frontlawn", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Frontlawn/Frontlawn.jpg"),
                    ("FrontlawnNight", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Frontlawn/FrontlawnNight.jpg"),
                    ("Pool", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Backyard/PoolBase.jpg"),
                    ("PoolNight", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Backyard/PoolBaseNight.jpg"),
                    ("Roof", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Roof/Roof.jpg"),
                    ("RoofNight", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Roof/RoofNight.jpg"),
                    ("Fog", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Backyard/PoolBase.jpg"),
                    ("NightFog", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Backyard/PoolBaseNight.jpg"),
                    ("TreasureIslandFrontLawn", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/TreasureIsland/TreasureIslandFrontlawn.jpg"),
                    ("Vase", "res://Asset/Texture/TowerDefense/Background/TowerDefenseMap/Frontlawn/Frontlawn.jpg"),
                };
                int okMaps = 0;
                foreach (var m in maps)
                {
                    try
                    {
                        var tex = Godot.GD.Load<Godot.Texture2D>(m.res);
                        if (tex != null && GodotObject.IsInstanceValid(tex))
                        {
                            var img = tex.GetImage();
                            if (img != null) { img.SavePng(mapsDir + "\\" + m.id + ".png"); okMaps++; }
                        }
                    }
                    catch { }
                }
                // 卡牌图标（游戏内渲染分帧导出）
                StartIconExport();
                Bootstrap.Log("导出制作器资源包: " + lmDir + " 地图" + okMaps + "张");
                return lmDir;
            }
            catch (System.Exception ex) { Bootstrap.Log("导出制作器资源包异常: " + ex.Message); return null; }
        }

        static string GetLevelMakerDir()
        {
            string dir = GetExeDir();
            return dir + (dir.EndsWith("\\") || dir.EndsWith("/") ? "" : "\\") + "levelmaker";
        }

        /// <summary>递归收集官方关卡配置 .tres 到清单行。</summary>
        static void CollectOfficialTres(string dir, string chapter, System.Collections.Generic.List<string> rows)
        {
            var da = Godot.DirAccess.Open(dir);
            if (da == null) return;
            var files = da.GetFiles();
            for (int i = 0; i < files.Length; i++)
            {
                var f = files[i];
                if (f.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
                {
                    string id = FileNameNoExt(f);
                    rows.Add("    { \"id\": \"" + EscapeJson(id) + "\", \"name\": \"" + EscapeJson(chapter + " / " + id) + "\", \"path\": \"" + EscapeJson(dir + "/" + f) + "\" }");
                }
            }
            var dirs = da.GetDirectories();
            for (int i = 0; i < dirs.Length; i++)
                CollectOfficialTres(dir + "/" + dirs[i], chapter, rows);
        }

        /// <summary>导出官方关卡清单 official_levels.json（制作器「官方模板」下拉用）。返回路径。</summary>
        public static string ExportOfficialLevelList()
        {
            try
            {
                var rows = new System.Collections.Generic.List<string>();
                string baseDir = "res://Asset/Config/Level/TowerDefense";
                var chapters = new[] { "Chapter1", "Chapter2", "Chapter3", "Chapter4", "Chapter5", "Chapter6", "Chapter7", "Chapter8", "Chapter9", "Challenge", "MiniGames", "IZM", "IZM2", "Survival", "Quiz", "Shooting", "Vase" };
                foreach (var ch in chapters)
                    CollectOfficialTres(baseDir + "/" + ch, ch, rows);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[");
                sb.AppendLine(string.Join(",\n", rows));
                sb.AppendLine("]");
                string path = GetLevelMakerDir() + "\\official_levels.json";
                WriteTextGodot(path, sb.ToString());
                Bootstrap.Log("导出官方关卡清单: " + rows.Count + " 关 -> " + path);
                return path;
            }
            catch (System.Exception ex) { Bootstrap.Log("导出官方关卡清单异常: " + ex.Message); return null; }
        }

        // ===== 图标分帧导出（游戏内渲染卡牌图标截图，每帧 2 张）=====
        class IconJob
        {
            public Godot.SubViewport vp;
            public string path;
            public int frame;
        }
        static System.Collections.Generic.List<string> _iconQueue;
        static System.Collections.Generic.List<IconJob> _iconJobs;
        static int _iconIdx;
        static bool _iconExporting;

        public static bool IsIconExporting() { return _iconExporting; }
        public static int GetIconExportProgress() { return _iconQueue == null ? 0 : _iconIdx; }
        public static int GetIconExportTotal() { return _iconQueue == null ? 0 : _iconQueue.Count; }

        static void StartIconExport()
        {
            try
            {
                string iconsDir = GetLevelMakerDir() + "\\icons";
                Godot.DirAccess.MakeDirAbsolute(iconsDir);
                _iconQueue = new System.Collections.Generic.List<string>();
                foreach (var id in GetPacketIds(true)) if (!string.IsNullOrEmpty(id)) _iconQueue.Add(id);
                foreach (var id in GetPacketIds(false)) if (!string.IsNullOrEmpty(id)) _iconQueue.Add(id);
                _iconIdx = 0;
                _iconJobs = new System.Collections.Generic.List<IconJob>();
                _iconExporting = true;
                Bootstrap.Log("图标导出: 开始 " + _iconQueue.Count + " 张");
            }
            catch (System.Exception ex) { Bootstrap.Log("图标导出启动异常: " + ex.Message); _iconExporting = false; }
        }

        static void TickIconExport(Node root)
        {
            if (!_iconExporting) return;
            try
            {
                var tree = root != null ? root.GetTree() : null;
                // 处理已渲染 >=3 帧的 job（截图保存 + 释放）
                for (int i = _iconJobs.Count - 1; i >= 0; i--)
                {
                    var job = _iconJobs[i];
                    job.frame++;
                    if (job.frame >= 12)
                    {
                        try
                        {
                            if (GodotObject.IsInstanceValid(job.vp))
                            {
                                var img = job.vp.GetTexture().GetImage();
                                if (img != null) SaveIconCentered(img, job.path);
                            }
                        }
                        catch { }
                        if (GodotObject.IsInstanceValid(job.vp)) job.vp.QueueFree();
                        _iconJobs.RemoveAt(i);
                    }
                }
                // 创建新 job（每帧 2 张）
                if (tree != null)
                {
                    for (int k = 0; k < 2 && _iconIdx < _iconQueue.Count; k++)
                    {
                        string id = _iconQueue[_iconIdx++];
                        try { CreateIconJob(id, tree); }
                        catch (System.Exception ex) { Bootstrap.Log("图标导出 " + id + " 异常: " + ex.Message); }
                    }
                }
                if (_iconIdx >= _iconQueue.Count && _iconJobs.Count == 0)
                {
                    _iconExporting = false;
                    Bootstrap.Log("图标导出: 完成");
                }
            }
            catch { }
        }

        static void CreateIconJob(string id, SceneTree tree)
        {
            var cfg = GetConfig(id);
            if (cfg == null) return;
            var t = FindType("TowerDefenseManager");
            if (t == null) return;
            var m = FindMethodExact(t, "GetPacketSprite", new Type[] { cfg.GetType() });
            if (m == null) return;
            var sprite = m.Invoke(null, new object[] { cfg });
            if (!(sprite is Godot.Node2D n2)) return;
            // 卡牌图标渲染配置：SubViewport 离屏截图必须 CPU pose 渲染（卡牌节点同款配置）
            try
            {
                // 播放卡牌图标动画剪辑（packetAnimeClip=BodyIdle），否则 sprite 空白
                var clip = FindPropOrFieldVal(cfg, "packetAnimeClip");
                if (clip is string cs && !string.IsNullOrEmpty(cs))
                {
                    var sm = n2.GetType().GetMethod("SetClip", new Type[] { typeof(string) });
                    if (sm != null) sm.Invoke(n2, new object[] { cs });
                }
                var sc = FindPropOrFieldVal(cfg, "packetAnimeScale");
                var flip = FindPropOrFieldVal(cfg, "packetFlip");
                if (sc is Godot.Vector2 sv) n2.Scale = sv;
                if (flip is bool fb && fb) n2.Scale = new Godot.Vector2(-n2.Scale.X, n2.Scale.Y);
                SetPropOrField(n2, "forceCpuPoseRender", true);
                SetPropOrField(n2, "keepRenderSubmittedWhenPaused", true);
                SetPropOrField(n2, "forceLocalRender", true);
                SetPropOrField(n2, "ProcessMode", Godot.Node.ProcessModeEnum.Always);
            }
            catch { }
            // SubViewport 离屏渲染截图（大一点，裁剪后居中）
            var vp = new Godot.SubViewport();
            vp.Size = new Godot.Vector2I(256, 256);
            vp.RenderTargetUpdateMode = Godot.SubViewport.UpdateMode.Always;
            vp.TransparentBg = true;
            vp.AddChild(n2);
            tree.Root.AddChild(vp);
            string iconsDir = GetLevelMakerDir() + "\\icons";
            _iconJobs.Add(new IconJob { vp = vp, path = iconsDir + "\\" + id + ".png", frame = 0 });
        }

        /// <summary>图标截图：裁剪到非透明内容并居中到 160×160 透明画布。</summary>
        static void SaveIconCentered(Godot.Image img, string path)
        {
            try
            {
                var used = img.GetUsedRect();
                if (used.Size.X < 2 || used.Size.Y < 2) { img.SavePng(path); return; }
                // 裁剪到内容边界（Godot 原地 API）
                img.Crop(used.Size.X, used.Size.Y);
                int cw = img.GetWidth(), ch = img.GetHeight();
                if (cw > 150 || ch > 150)
                {
                    float sc = Mathf.Min(150f / cw, 150f / ch);
                    img.Resize((int)(cw * sc), (int)(ch * sc), Godot.Image.Interpolation.Bilinear);
                    cw = img.GetWidth(); ch = img.GetHeight();
                }
                var canvas = Godot.Image.CreateEmpty(160, 160, false, Godot.Image.Format.Rgba8);
                canvas.Fill(new Godot.Color(0, 0, 0, 0));
                canvas.BlendRect(img, new Godot.Rect2I(0, 0, cw, ch), new Godot.Vector2I((160 - cw) / 2, (160 - ch) / 2));
                canvas.SavePng(path);
            }
            catch { try { img.SavePng(path); } catch { } }
        }

        /// <summary>取卡显示名，若为翻译 key 则用游戏翻译系统转中文（供导出图鉴数据）。</summary>
        static string GetPacketDisplayNameZh(string id)
        {
            try
            {
                string name = GetPacketDisplayName(id);
                if (string.IsNullOrEmpty(name) || name == id) return id;
                if (name.StartsWith("TOWERDEFENSE_") || name.Contains("_NAME"))
                {
                    var zh = Godot.TranslationServer.Translate(new Godot.StringName(name));
                    if (!string.IsNullOrEmpty(zh) && zh != name) return zh;
                }
                return name;
            }
            catch { return id; }
        }

        /// <summary>单张卡导出 JSON（id/中文名/卡层属性 cost/cooldown）。</summary>
        static string DumpCardJson(string id, bool plant)
        {
            try
            {
                string name = GetPacketDisplayNameZh(id);
                double cost = -1, cd = -1;
                var cfg = GetConfig(id);
                if (cfg != null)
                {
                    var v = FindPropOrFieldVal(cfg, "overrideCost");
                    if (v is int ci && ci >= 0) cost = ci; else if (v is double cd2 && cd2 >= 0) cost = cd2; else if (v is float cf2 && cf2 >= 0) cost = cf2;
                    var w = FindPropOrFieldVal(cfg, "overridePacketCooldown");
                    if (w is double cdv && cdv >= 0) cd = cdv; else if (w is float cfv && cfv >= 0) cd = cfv; else if (w is int civ && civ >= 0) cd = civ;
                    // 角色配置兜底（override 未设置时用角色真实属性）
                    var cc = FindPropOrFieldVal(cfg, "characterConfig");
                    if (cost < 0 && cc != null)
                    {
                        var cv = FindPropOrFieldVal(cc, "cost");
                        if (cv is int ci2) cost = ci2; else if (cv is double dd2) cost = dd2; else if (cv is float ff2) cost = ff2;
                    }
                    if (cd < 0 && cc != null)
                    {
                        var cw = FindPropOrFieldVal(cc, "packetCooldown");
                        if (cw is double dd3) cd = dd3; else if (cw is float ff3) cd = ff3; else if (cw is int ci3) cd = ci3;
                    }
                }
                if (cost < 0) cost = 0;
                if (cd < 0) cd = 0;
                return "{ \"id\": \"" + EscapeJson(id) + "\", \"name\": \"" + EscapeJson(name) + "\", \"cost\": " + cost.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", \"cooldown\": " + cd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " }";
            }
            catch { return "{ \"id\": \"" + EscapeJson(id) + "\", \"name\": \"" + EscapeJson(id) + "\", \"cost\": 0, \"cooldown\": 0 }"; }
        }

        /// <summary>Godot FileAccess 读取所有行（规避 AOT 裁剪导致 System.IO.File.ReadAllLines 不可用）。</summary>
        static string[] ReadLinesGodot(string path)
        {
            try
            {
                using var fa = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
                if (fa == null) return new string[0];
                string text = fa.GetAsText();
                return text.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            }
            catch { return new string[0]; }
        }

        static bool GodotFileExists(string path)
        {
            try { return Godot.FileAccess.FileExists(path); } catch { return false; }
        }

        static bool WriteTextGodot(string path, string content)
        {
            try
            {
                using var fa = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
                if (fa == null) return false;
                fa.StoreString(content);
                return true;
            }
            catch { return false; }
        }

        /// <summary>手写找最后分隔符（AOT 下 System.String.LastIndexOfAny 被裁剪不可用）。</summary>
        static int LastSlashIdx(string s)
        {
            int idx = -1;
            for (int i = 0; i < s.Length; i++) if (s[i] == '\\' || s[i] == '/') idx = i;
            return idx;
        }
        /// <summary>手写找最后字符（AOT 保险，避免 LastIndexOf(char) 被裁）。</summary>
        static int LastCharIdx(string s, char c)
        {
            int idx = -1;
            for (int i = 0; i < s.Length; i++) if (s[i] == c) idx = i;
            return idx;
        }
        /// <summary>把路径统一为反斜杠（Godot DirAccess 对混合斜杠可能打不开目录）。</summary>
        static string ReplaceChars(string s, char from, char to)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) sb.Append(s[i] == from ? to : s[i]);
            return sb.ToString();
        }

        /// <summary>取文件名（去目录去扩展名）。</summary>
        public static string FileNameNoExt(string path)
        {
            try
            {
                int i = LastSlashIdx(path);
                string name = i >= 0 ? path.Substring(i + 1) : path;
                int d = LastCharIdx(name, '.');
                return d > 0 ? name.Substring(0, d) : name;
            }
            catch { return path; }
        }

        static string _levelsDir;
        /// <summary>关卡库目录（游戏 exe 目录/levels/）。</summary>
        static string GetLevelsDir()
        {
            try
            {
                if (_levelsDir != null) return _levelsDir;
                string dir = "";
                try { var exe = Godot.OS.GetExecutablePath(); int si = LastSlashIdx(exe); dir = si > 0 ? exe.Substring(0, si) : ""; } catch { }
                if (string.IsNullOrEmpty(dir)) dir = Godot.ProjectSettings.GlobalizePath("user://");
                dir = ReplaceChars(dir, '/', '\\');
                if (!dir.EndsWith("\\")) dir += "\\";
                _levelsDir = dir + "levels";
                try { Godot.DirAccess.MakeDirAbsolute(_levelsDir); } catch { }
                return _levelsDir;
            }
            catch { return null; }
        }

        /// <summary>列出关卡库（levels/*.pvzlevel）绝对路径，供「关卡栏」显示。</summary>
        public static System.Collections.Generic.List<string> ListCustomLevels()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                string dir = GetLevelsDir();
                if (string.IsNullOrEmpty(dir)) return list;
                using var da = Godot.DirAccess.Open(dir);
                if (da == null) return list;
                var files = da.GetFiles();
                for (int i = 0; i < files.Length; i++)
                {
                    string f = files[i];
                    if (f.EndsWith(".pvzlevel", StringComparison.OrdinalIgnoreCase))
                        list.Add(dir + "\\" + f);
                }
                list.Sort();
            }
            catch { }
            return list;
        }

        /// <summary>复制关卡文件到关卡库（levels/），供「关卡栏」显示。</summary>
        public static bool ImportLevelToLibrary(string srcPath)
        {
            try
            {
                string dir = GetLevelsDir();
                if (string.IsNullOrEmpty(dir)) return false;
                string dest = dir + "\\" + FileNameNoExt(srcPath) + ".pvzlevel";
                string content = "";
                using (var fr = Godot.FileAccess.Open(srcPath, Godot.FileAccess.ModeFlags.Read))
                {
                    if (fr == null) return false;
                    content = fr.GetAsText();
                }
                using (var fw = Godot.FileAccess.Open(dest, Godot.FileAccess.ModeFlags.Write))
                {
                    if (fw == null) return false;
                    fw.StoreString(content);
                }
                Bootstrap.Log("关卡库: 已导入 " + dest);
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("关卡库导入异常: " + ex.Message); return false; }
        }

        // ===== 全局植物属性覆盖（所有关卡生效，制作器导出的 plant_overrides.txt）=====
        static string GlobalOverridesPath()
        {
            if (_globalOverridesPath != null) return _globalOverridesPath;
            _globalOverridesPath = ModSettings.SaveDir + "plant_overrides.txt";
            return _globalOverridesPath;
        }

        static void EnsureGlobalOverridesLoaded()
        {
            if (_globalOverridesLoaded) return;
            _globalOverridesLoaded = true;
            try
            {
                string path = GlobalOverridesPath();
                if (!GodotFileExists(path)) return;
                var lines = ReadLinesGodot(path);
                if (lines.Length == 0) return;
                var list = new System.Collections.Generic.List<(string, string, string)>();
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 3) continue;
                    string id = parts[0].Trim();
                    string prop = parts[1].Trim().ToLowerInvariant();
                    string val = parts[2].Trim();
                    if (id.Length == 0 || val.Length == 0) continue;
                    list.Add((id, prop, val));
                }
                if (list.Count > 0)
                {
                    _globalOverrides = list;
                    Bootstrap.Log("全局属性覆盖: 已加载 " + list.Count + " 条");
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("全局属性覆盖加载异常: " + ex.Message); }
        }

        /// <summary>全局覆盖是否有效（供 UI 显示条数）。</summary>
        public static int GetGlobalOverrideCount()
        {
            try { EnsureGlobalOverridesLoaded(); return _globalOverrides == null ? 0 : _globalOverrides.Count; }
            catch { return 0; }
        }

        /// <summary>清除全局属性覆盖（清内存 + 清文件）。</summary>
        public static void ClearGlobalOverrides()
        {
            try
            {
                _globalOverrides = null;
                _globalOverridesLoaded = true;
                string path = GlobalOverridesPath();
                using (var fw = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
                {
                    if (fw != null) fw.StoreString("");
                }
                Bootstrap.Log("全局属性覆盖: 已清除");
            }
            catch { }
        }

        /// <summary>每帧应用全局属性覆盖（OnFrame 调用，降频 5 帧）。</summary>
        static void ApplyGlobalOverrides(Node root)
        {
            try
            {
                EnsureGlobalOverridesLoaded();
                if (_globalOverrides == null || _globalOverrides.Count == 0) return;
                if (++_globalOverrideTimer % 5 == 0) ApplyOverridesToPlants(root, _globalOverrides);
            }
            catch { }
        }

        /// <summary>控制台快捷键触发（事件监听+轮询共用）：300ms 防重，确保一次按键只开关一次。</summary>
        public static void ToggleConsoleIfIdle()
        {
            double now = (double)Time.GetTicksMsec();
            if (now - _consoleKeyLastMs < 300) return;
            _consoleKeyLastMs = now;
            OpenConsolePanel();
        }

        /// <summary>从 MOD 面板打开/关闭游戏原生控制台（toggle：再点一次关闭）。</summary>
        public static void OpenConsolePanel()
        {
            try
            {
                if (_consoleRoot == null || !GodotObject.IsInstanceValid(_consoleRoot)) { Bootstrap.Log("打开控制台: 无场景"); return; }
                var tree = _consoleRoot.GetTree();
                var top = (tree != null && tree.Root != null) ? tree.Root : _consoleRoot;
                var cm = FindNodeByName(top, "CommandManager");
                if (cm == null) { Bootstrap.Log("打开控制台: 控制台未加载"); return; }
                // 打开前强制整树 Always（双保险：即使 EnableConsole 首帧失败，打开时也确保暂停后控件可点）
                SetProcessModeAlways(cm);
                // toggle：读当前 _guiLayer.Visible，取反后 SetCommandLayerVisible（官方：_guiLayer.Visible + GetTree().Paused）
                bool visible = false;
                if (FindPropOrFieldVal(cm, "_guiLayer") is Godot.CanvasLayer gl) visible = gl.Visible;
                var m = FindMethodExact(cm.GetType(), "SetCommandLayerVisible", new[] { typeof(bool) });
                if (m != null)
                {
                    m.Invoke(cm, new object[] { !visible });
                    Bootstrap.Log("打开控制台: " + (visible ? "已关闭" : "已打开") + " gui=" + (!visible) + " paused=" + (cm.GetTree() != null ? cm.GetTree().Paused.ToString() : "?"));
                }
                else
                {
                    if (FindPropOrFieldVal(cm, "_guiLayer") is Godot.CanvasLayer gl2) gl2.Visible = !visible;
                    var tree2 = cm.GetTree();
                    if (tree2 != null) tree2.Paused = !visible;
                    Bootstrap.Log("打开控制台: " + (visible ? "已关闭(兜底)" : "已打开(兜底)"));
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("打开控制台异常: " + ex.Message); }
        }

        /// <summary>递归把节点整棵树 ProcessMode 设为 Always（暂停树时仍响应输入），返回设置节点数。</summary>
        static int SetProcessModeAlways(Node n)
        {
            int count = 0;
            if (n == null) return 0;
            try { n.ProcessMode = Godot.Node.ProcessModeEnum.Always; count++; } catch { }
            foreach (var c in n.GetChildren(true))
            {
                try { c.ProcessMode = Godot.Node.ProcessModeEnum.Always; count++; } catch { }
                count += SetProcessModeAlways(c);
            }
            return count;
        }

        /// <summary>场景树里按名字找节点（含子节点递归）。</summary>
        static Node FindNodeByName(Node start, string name)
        {
            if (start == null) return null;
            if (start.Name.ToString() == name) return start;
            foreach (var c in start.GetChildren(true))
            {
                var r = FindNodeByName(c, name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>开启游戏原生作弊控制台（CommandManager.tscn）：官方 debug=false 时隐藏 OpenButton+禁用整个控制台，MOD 强制 debug=true 恢复。
        /// OpenButton 已移除（不再在右下角显示按钮），开关方式：手机=音量-/+ / ModUI 按钮。</summary>
        static void EnableConsole(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                var cm = (tree != null && tree.Root != null) ? FindNodeByName(tree.Root, "CommandManager") : null;
                if (cm == null)
                {
                    // 首次挂载 或 切场景后重新挂载（每次 cm 为 null 都会重新挂载）
                    var scene = Godot.GD.Load<Godot.PackedScene>("res://Core/CommandManager/CommandManager.tscn");
                    if (scene == null) { Bootstrap.Log("控制台: 场景加载失败"); return; }
                    cm = scene.Instantiate();
                    if (cm == null) { Bootstrap.Log("控制台: 实例化失败"); return; }
                    root.AddChild(cm);   // 触发 _Ready（debug=false → 隐藏 OpenButton + 禁用）
                    // 挂按键输入监听（_Input 事件驱动，比每帧轮询可靠；手机=音量-）
                    try { cm.AddChild(new ConsoleInputCatcher()); } catch { }
                    _consoleReady = false;   // 新挂载需重新设置整树 Always
                }
                if (_consoleReady) return;   // 整树 Always 已确保 → 每 30 帧低开销跳过
                var t = cm.GetType();
                // 强制 debug=true（public bool 字段，直接反射写）
                try
                {
                    var dbgF = t.GetField("debug", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (dbgF != null && dbgF.FieldType == typeof(bool)) dbgF.SetValue(cm, true);
                }
                catch { }
                // 整棵树 ProcessMode=Always：SetCommandLayerVisible 会 GetTree().Paused=true，
                // 不设 Always 则面板控件不响应输入（“里面的东西都动不了”）
                try { cm.ProcessMode = Godot.Node.ProcessModeEnum.Always; } catch { }
                int setN = SetProcessModeAlways(cm);
                // 作弊面板置顶（GUILayer.layer 100→200，高于 MOD UI 150）
                try
                {
                    if (FindPropOrFieldVal(cm, "_guiLayer") is Godot.CanvasLayer clG) clG.Layer = 200;
                }
                catch { }
                // 删除右下角控制台按钮：官方 debug=true 时 _openButton 默认可见，须显式隐藏
                try
                {
                    var obF = FindFieldRec(t, "_openButton");
                    if (obF != null && obF.GetValue(cm) is Godot.Control ob) ob.Visible = false;
                }
                catch { }
                try { var init = t.GetMethod("Init", Type.EmptyTypes); if (init != null) init.Invoke(cm, null); } catch { }
                _consoleReady = true;
                Bootstrap.Log("控制台: 已开启（Always节点=" + setN + " layer=200）");
            }
            catch (System.Exception ex) { Bootstrap.Log("控制台开启异常: " + ex.Message); }
        }

        /// <summary>JSON 字符串转义。</summary>
        static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }

        /// <summary>应用导入关卡的属性覆盖：遍历场上植物按 id 匹配（fireInterval/hp/cost/cooldown）。</summary>
        static void ApplyImportedOverrides(Node root)
        {
            if (_importedOverrides != null && _importedOverrides.Count > 0) ApplyOverridesToPlants(root, _importedOverrides);
        }

        /// <summary>通用：对场上植物应用一组属性覆盖（fireInterval/hp/cost/cooldown）。</summary>
        static void ApplyOverridesToPlants(Node root, System.Collections.Generic.List<(string id, string prop, string val)> list)
        {
            try
            {
                if (list == null || list.Count == 0) return;
                var tree = root != null ? root.GetTree() : null;
                if (tree == null || tree.Root == null) return;
                _ovWalkList = list;
                OverrideWalk(tree.Root);
                _ovWalkList = null;
            }
            catch { }
        }

        static void OverrideWalk(Node node)
        {
            try
            {
                foreach (var child in node.GetChildren(true))
                {
                    try
                    {
                        if (child is Node2D n2 && GodotObject.IsInstanceValid(n2) && ESP.GetCamp(n2) == "Plant")
                        {
                            string id = GetPlantPacketId(n2);
                            if (string.IsNullOrEmpty(id)) continue;
                            if (_ovWalkList == null) continue;
                            foreach (var ov in _ovWalkList)
                            {
                                if (!string.Equals(ov.id, id, StringComparison.OrdinalIgnoreCase)) continue;
                                double v;
                                if (!double.TryParse(ov.val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                                string p = ov.prop;
                                if (p == "fireinterval" || p == "攻速") ApplyPlantFireIntervalDirect(n2, v);
                                else if (p == "hp" || p == "血量") ApplyPlantHpDirect(n2, v);
                                else if (p == "cost" || p == "阳光") ApplyPlantCostDirect(n2, v);
                                else if (p == "cooldown" || p == "冷却") ApplyPlantCooldownDirect(n2, v);
                            }
                        }
                    }
                    catch { }
                    OverrideWalk(child);
                }
            }
            catch { }
        }

        /// <summary>从植物节点取卡 id（config.id/name/saveKey）。</summary>
        static string GetPlantPacketId(Node2D n)
        {
            try
            {
                var cfg = FindPropOrFieldVal(n, "config");
                if (cfg == null) cfg = FindPropOrFieldVal(n, "packetConfig");
                if (cfg == null) return null;
                var id = FindPropOrFieldVal(cfg, "id") as string;
                if (string.IsNullOrEmpty(id)) id = FindPropOrFieldVal(cfg, "name") as string;
                if (string.IsNullOrEmpty(id)) id = FindPropOrFieldVal(cfg, "saveKey") as string;
                return id;
            }
            catch { return null; }
        }

        /// <summary>直接设置植物 FireComponent.fireInterval（覆盖属性用，不走倍率）。</summary>
        static void ApplyPlantFireIntervalDirect(Node2D n, double v)
        {
            try
            {
                var comp = FindPlantFireComponent(n);
                if (comp == null) return;
                var t = comp.GetType();
                var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var f = t.GetField("fireInterval", bf);
                if (f != null && f.FieldType == typeof(float)) { f.SetValue(comp, (float)v); var fb = t.GetField("fireIntervalBase", bf); if (fb != null && fb.FieldType == typeof(float)) fb.SetValue(comp, (float)v); }
                var nfp = n.GetType().GetProperty("fireInterval", bf);
                if (nfp != null && nfp.PropertyType == typeof(double)) { var st = nfp.GetSetMethod(true); if (st != null) st.Invoke(n, new object[] { (double)v }); }
            }
            catch { }
        }

        /// <summary>直接设置植物血量（instance.hitpoints + hitpointsBase）。</summary>
        static void ApplyPlantHpDirect(Node2D n, double v)
        {
            try
            {
                var inst = FindFieldVal(n, "instance");
                if (inst == null) return;
                var f = FindFieldInfo(inst, "hitpoints");
                if (f != null)
                {
                    if (f.FieldType == typeof(double)) f.SetValue(inst, (double)v);
                    else if (f.FieldType == typeof(float)) f.SetValue(inst, (float)v);
                }
                var fb = FindFieldInfo(inst, "hitpointsBase");
                if (fb != null)
                {
                    if (fb.FieldType == typeof(double)) fb.SetValue(inst, (double)v);
                    else if (fb.FieldType == typeof(float)) fb.SetValue(inst, (float)v);
                }
            }
            catch { }
        }

        /// <summary>直接设置植物卡阳光消耗（config.overrideCost）。</summary>
        static void ApplyPlantCostDirect(Node2D n, double v)
        {
            try
            {
                var cfg = FindPropOrFieldVal(n, "config");
                if (cfg == null) return;
                var f = FindFieldInfo(cfg, "overrideCost");
                if (f != null)
                {
                    if (f.FieldType == typeof(int)) f.SetValue(cfg, (int)v);
                    else if (f.FieldType == typeof(double)) f.SetValue(cfg, (double)v);
                    else if (f.FieldType == typeof(float)) f.SetValue(cfg, (float)v);
                }
            }
            catch { }
        }

        /// <summary>直接设置植物卡冷却（config.overridePacketCooldown）。</summary>
        static void ApplyPlantCooldownDirect(Node2D n, double v)
        {
            try
            {
                var cfg = FindPropOrFieldVal(n, "config");
                if (cfg == null) return;
                var f = FindFieldInfo(cfg, "overridePacketCooldown");
                if (f != null)
                {
                    if (f.FieldType == typeof(double)) f.SetValue(cfg, (double)v);
                    else if (f.FieldType == typeof(float)) f.SetValue(cfg, (float)v);
                    else if (f.FieldType == typeof(int)) f.SetValue(cfg, (int)v);
                }
            }
            catch { }
        }

        /// <summary>自制关卡每帧逻辑：卡槽改造（只留咖啡豆）+ 波次控制 + 波次完判胜。</summary>
        static void CustomLevelTick(Node root)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null)
                {
                    // 不在对局（回主菜单/加载中）→ 延迟后退出自制关卡模式并恢复 NoZombieSpawn
                    if (++_customExitTimer > 60)
                    {
                        ModSettings.CustomLevelActive = false;
                        if (_customNoSpawnTouched) { ModSettings.NoZombieSpawn = _customNoSpawnSaved; _customNoSpawnTouched = false; }
                        _coffeeGamble = false;
                    }
                    return;
                }
                _customExitTimer = 0;
                // 禁止原生出怪（自制关卡波次由 MOD 控制）
                if (!_customNoSpawnTouched) { ModSettings.NoZombieSpawn = true; _customNoSpawnTouched = true; }
                // 一次性卡槽改造
                if (!_customLevelInit)
                {
                    _customLevelInit = SetupCustomSeedBank(root);
                    if (_customLevelInit) Bootstrap.Log("自制关卡: 卡槽已改造（咖啡豆 x6）");
                }
                // 初始布局：进对局后生成文件里的 PREPLANT/PREZOMBIE
                if (!_customPreDone)
                {
                    _customPreDone = true;
                    if (_importedPrePlant != null)
                        foreach (var p in _importedPrePlant) { try { SpawnCharacter(p.id, new Vector2I(p.x, p.y)); } catch { } }
                    if (_importedPreZombie != null)
                        foreach (var z in _importedPreZombie) { try { SpawnCharacter(z.id, new Vector2I(z.x, z.y)); } catch { } }
                    if ((_importedPrePlant != null && _importedPrePlant.Count > 0) || (_importedPreZombie != null && _importedPreZombie.Count > 0))
                        Bootstrap.Log("自制关卡: 初始布局已生成");
                }
                // 属性覆盖：对场上植物按 id 应用（fireInterval/hp/cost）
                if (_importedOverrides != null && _importedOverrides.Count > 0 && ++_overrideApplyTimer % 5 == 0) ApplyImportedOverrides(root);
                // 波次：按 _customWaves（导入关卡用文件波次，否则默认 3 波）延迟刷怪；全部刷完判胜
                var waves = _customWaves ?? DefaultWaves();
                if (_customWave < waves.Count)
                {
                    var w = waves[_customWave];
                    if (++_customWaveTimer >= w.delay * 60)
                    {
                        _customWaveTimer = 0;
                        int spawned = 0;
                        for (int i = 0; i < w.num; i++)
                        {
                            var grid = new Vector2I(_bulletRand.Next(1, 9), _bulletRand.Next(1, 6));
                            if (SpawnCharacter(w.zombie, grid)) spawned++;
                        }
                        Bootstrap.Log("自制关卡: 第" + (_customWave + 1) + "波 刷 " + w.zombie + " x" + spawned);
                        _customWave++;
                    }
                }
                else if (_customWave == waves.Count)
                {
                    _customWave++;
                    Bootstrap.Log("自制关卡: 全部波次完成，胜利");
                    InvokeCommand("InstantWin");
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("自制关卡Tick异常: " + ex.Message); }
        }

        /// <summary>卡槽改造：清空所有卡，加入 6 张阳光豆（TowerDefenseInGameSeedBank.DeleteAllPacket + AddPacket）。</summary>
        static bool SetupCustomSeedBank(Node root)
        {
            try
            {
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return false;
                var sbNodes = tree.Root.FindChildren("*", "TowerDefenseInGameSeedBank", true, false);
                if (sbNodes == null || sbNodes.Count == 0) return false;
                var sb = sbNodes[0];
                var t = sb.GetType();
                var delAll = t.GetMethod("DeleteAllPacket", Type.EmptyTypes);
                if (delAll != null) { try { delAll.Invoke(sb, null); } catch { } }
                // 找咖啡豆卡配置
                var cfg = GetCoffeeBeanConfig();
                if (cfg == null) { Bootstrap.Log("自制关卡: 咖啡豆配置为空"); return false; }
                // AddPacket(TowerDefensePacketConfig, bool)
                System.Reflection.MethodInfo add = null;
                foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (mi.Name == "AddPacket" && mi.GetParameters().Length == 2) { add = mi; break; }
                if (add == null) { Bootstrap.Log("自制关卡: 找不到 AddPacket"); return false; }
                for (int i = 0; i < 6; i++)
                {
                    try { add.Invoke(sb, FillArgs(add, new object[] { cfg, true })); } catch { }
                }
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("自制关卡卡槽异常: " + ex.Message); return false; }
        }

        /// <summary>真正清空卡槽（TowerDefenseInGameSeedBank.DeleteAllPacket）。</summary>
        public static void ClearSeedBankAll(Node root)
        {
            try
            {
                var tree = root != null ? root.GetTree() : null;
                if (tree == null || tree.Root == null) { Bootstrap.Log("清空卡槽: 无场景"); return; }
                var sbNodes = tree.Root.FindChildren("*", "TowerDefenseInGameSeedBank", true, false);
                if (sbNodes == null || sbNodes.Count == 0) { Bootstrap.Log("清空卡槽: 未找到卡槽"); return; }
                var sb = sbNodes[0];
                var delAll = sb.GetType().GetMethod("DeleteAllPacket", Type.EmptyTypes);
                if (delAll == null) { Bootstrap.Log("清空卡槽: 无 DeleteAllPacket 方法"); return; }
                delAll.Invoke(sb, null);
                Bootstrap.Log("清空卡槽: 已清空");
            }
            catch (System.Exception ex) { Bootstrap.Log("清空卡槽异常: " + ex.Message); }
        }

        /// <summary>判断卡配置是否为咖啡豆（id/saveKey/name 含 Coffeebean/CoffeeBean）。</summary>
        static bool IsCoffeeBeanConfig(object config)
        {
            try
            {
                if (config == null) return false;
                foreach (var fn in new[] { "id", "saveKey", "name", "packetName" })
                {
                    var v = FindPropOrFieldVal(config, fn) as string;
                    if (string.IsNullOrEmpty(v)) continue;
                    if (v.IndexOf("Coffeebean", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("CoffeeBean", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>在指定格子随机生成一个植物或僵尸（plantChance 为植物概率）。</summary>
        static void RandomSpawnAt(Vector2I gridPos, double plantChance)
        {
            try
            {
                bool plant = _bulletRand.NextDouble() < plantChance;
                var ids = GetPacketIds(plant);
                if (ids == null || ids.Count == 0) { Bootstrap.Log("咖啡豆赌博: 无可用" + (plant ? "植物" : "僵尸") + " id"); return; }
                string id = ids[_bulletRand.Next(ids.Count)];
                bool ok = SpawnCharacter(id, gridPos);
                Bootstrap.Log("咖啡豆赌博: 原地生成" + (plant ? "植物" : "僵尸") + " " + id + (ok ? " 成功" : " 失败"));
            }
            catch (System.Exception ex) { Bootstrap.Log("咖啡豆赌博异常: " + ex.Message); }
        }

        public static void OnPlantPlaced(object packetShow, Vector2I gridPos)
        {
            try
            {
                if (!ModSettings.PlantColumn && !ModSettings.PlantTriple && !ModSettings.CustomLevelActive) return;
                if (_bulkPlanting) return;
                if (packetShow == null) return;
                var config = FindPropOrFieldVal(packetShow, "config");
                if (config == null) return;
                // 咖啡豆赌博模式：种下咖啡豆 → 原地随机生成植物/僵尸（导入关卡不触发）
                if (ModSettings.CustomLevelActive && _coffeeGamble && IsCoffeeBeanConfig(config))
                {
                    RandomSpawnAt(gridPos, 0.5);
                    return;   // 不执行种列/三倍
                }
                _bulkPlanting = true;
                try
                {
                    if (_plantColumnMethod == null)
                        _plantColumnMethod = FindMethodByPrefix(config.GetType(), "Plant", new Type[] { typeof(Vector2I), typeof(bool), typeof(bool) });
                    if (_plantColumnMethod == null) return;
                    int rows = GetMapRowCount();
                    // 种一列：地图行索引从 1 开始（GetMapGridPos 返回 gridPos + (1,1)）！所以遍历 1..rows，避免错位/少一个
                    if (ModSettings.PlantColumn)
                    {
                        for (int row = 1; row <= rows; row++)
                        {
                            if (row == gridPos.Y) continue;
                            try { _plantColumnMethod.Invoke(config, FillArgs(_plantColumnMethod, new object[] { new Vector2I(gridPos.X, row), true, true })); } catch { }
                        }
                    }
                    // 三倍种植：种到同一个格子（堆叠 3 个，不分开到相邻行）
                    if (ModSettings.PlantTriple)
                    {
                        for (int i = 0; i < 2; i++)
                            try { _plantColumnMethod.Invoke(config, FillArgs(_plantColumnMethod, new object[] { gridPos, true, true })); } catch { }
                    }
                    Bootstrap.Log("种列/三倍: 点击位置=" + gridPos + " 地图行数=" + rows);
                }
                finally { _bulkPlanting = false; }
            }
            catch { }
        }

        /// <summary>获取地图实际行数（GetMapGridNum().Y），失败默认 5。</summary>
        static int GetMapRowCount()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm != null)
                {
                    var m = tdm.GetType().GetMethod("GetMapGridNum", Type.EmptyTypes);
                    if (m != null && m.Invoke(tdm, null) is Vector2I v && v.Y > 0 && v.Y <= 60)
                        return v.Y;
                }
            }
            catch { }
            return 5;
        }

        /// <summary>迷雾透视：隐藏 fogNode / FogBatch 节点（战斗迷雾）。</summary>
        static void ApplyFogESP(Node root)
        {
            try
            {
                var tree = root.GetTree();
                Node target = tree != null && tree.Root != null ? tree.Root : root;
                int count = 0;
                WalkFogHide(target, ref count);
            }
            catch { }
        }

        static void WalkFogHide(Node node, ref int count)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    // 迷雾渲染由 TowerDefenseFog.canVisible 控制（不是节点 Visible）！
                    if (child.GetType().Name == "TowerDefenseFog")
                    {
                        count++;
                        // 三重保险：SetCanVisible(false) + canVisible=false + sprite 透明
                        try
                        {
                            var m = child.GetType().GetMethod("SetCanVisible", new Type[] { typeof(bool) });
                            if (m != null) m.Invoke(child, new object[] { false });
                        }
                        catch { }
                        try
                        {
                            var f = child.GetType().GetField("canVisible", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(child))
                                f.SetValue(child, false);
                        }
                        catch { }
                        try
                        {
                            var sf = child.GetType().GetField("sprite", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (sf != null && sf.GetValue(child) is Godot.Sprite2D spr && GodotObject.IsInstanceValid(spr))
                            {
                                var cm = spr.Modulate;
                                if (cm.A > 0.01f)
                                    spr.Modulate = new Color(cm.R, cm.G, cm.B, 0f);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                WalkFogHide(child, ref count);
            }
        }

        /// <summary>反射设置节点 Visible（兼容 Node / Node2D；Godot 4 的 Node 也有 Visible）。</summary>
        static void SetNodeVisible(Node node, bool v)
        {
            try
            {
                var t = node.GetType();
                while (t != null && t != typeof(object))
                {
                    var p = t.GetProperty("Visible", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (p != null && p.PropertyType == typeof(bool))
                    {
                        var setter = p.GetSetMethod(true);
                        if (setter != null) { setter.Invoke(node, new object[] { v }); return; }
                        var f = t.GetField("Visible", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (f != null) { f.SetValue(node, v); return; }
                    }
                    t = t.BaseType;
                }
            }
            catch { }
        }

        /// <summary>蘑菇不睡觉：由 patcher 注入到 SleepComponent.CanSleep 开头，返回 true 时直接返回 false（禁止睡眠）。</summary>
        public static bool ShouldPreventSleep(object sleepComp)
        {
            return ModSettings.NoSleep;
        }

        /// <summary>迷雾透视：由 patcher 注入到 TowerDefenseFog.SetCanVisible 开头。
        /// FogESP 开且 visible=true 时：强制 this.canVisible=false + sprite 透明，并拦截（GameEntry 会重设迷雾可见）。</summary>
        public static bool ShouldBlockFogVisible(object fog, bool visible)
        {
            if (ModSettings.FogESP && visible)
            {
                try
                {
                    if (fog != null)
                    {
                        var t = fog.GetType();
                        var f = t.GetField("canVisible", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(fog))
                            f.SetValue(fog, false);
                        // 直接让 sprite 透明（lightOverlapEnabled=false 的雾不走 UpdateFogState，必须直接设）
                        var sf = t.GetField("sprite", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (sf != null && sf.GetValue(fog) is Godot.Sprite2D spr && GodotObject.IsInstanceValid(spr))
                        {
                            var m = spr.Modulate;
                            if (m.A > 0.01f)
                                spr.Modulate = new Color(m.R, m.G, m.B, 0f);
                        }
                    }
                }
                catch { }
                return true;   // 拦截，跳过原 SetCanVisible(true)
            }
            return false;
        }

        static int _craterTimer;

        static int _zombieFunTimer;
        static int _zombieCacheTimer;
        static int _zombieFunLogTimer;
        static int _zombieApplyTimer;   // 降频应用：大量僵尸每帧改 Modulate/Rotation 打断 2D batching（部分手机闪屏根源）
        static readonly System.Collections.Generic.List<Node2D> _zombieCache = new System.Collections.Generic.List<Node2D>();

        /// <summary>供联机镜像（NetMirror）读取场上僵尸。</summary>
        public static System.Collections.Generic.List<Node2D> GetZombieCache() { return _zombieCache; }

        // ===== 联机（M2）：对局状态读写（供 NetBattle 使用）=====
        static bool _netErrLogged;

        /// <summary>联机子系统首次异常记录：只记一次，且绝不让异常中断 OnFrame。</summary>
        static void LogNetOnce(string tag, Exception ex)
        {
            if (_netErrLogged) return;
            _netErrLogged = true;
            try { Bootstrap.Log(tag + ": 首次异常 " + ex.Message); } catch { }
            try { Bootstrap.Log(tag + ": 堆栈 " + (ex.StackTrace ?? "")); } catch { }
        }

        /// <summary>联机镜像：节点类型名（供幽灵实体标签显示）。</summary>
        public static string NetGetNodeKind(Node2D n)
        {
            try { return n != null ? n.GetType().Name : ""; } catch { return ""; }
        }

        /// <summary>对战模块（NetPvp）用的反射读取桥：FindPropOrFieldVal 是私有的，
        /// 跨类访问要开个口子，避免把整个对战逻辑塞进本文件。</summary>
        public static object PvpReadField(object obj, string name)
        {
            return FindPropOrFieldVal(obj, name);
        }

        /// <summary>对战模块用的字段写入桥（探测式：命中即写，未命中返回 false，不抛）。</summary>
        public static bool PvpWriteField(object obj, string name, object value)
        {
            try
            {
                if (obj == null) return false;
                var t = obj.GetType();
                var f = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null && !f.IsInitOnly) { f.SetValue(obj, value); return true; }
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (p != null && p.CanWrite) { p.SetValue(obj, value); return true; }
            }
            catch { }
            return false;
        }

        /// <summary>取一张卡的官方阳光价格（-1 = 读不到）。
        /// 与 DumpCardJson 同一套取值顺序：卡层 overrideCost → 兜底角色层 characterConfig.cost。</summary>
        public static int PacketCost(string id)
        {
            try
            {
                var cfg = GetConfig(id);
                if (cfg == null) return -1;
                var v = FindPropOrFieldVal(cfg, "overrideCost");
                if (v is int ci && ci >= 0) return ci;
                if (v is double cd && cd >= 0) return (int)cd;
                if (v is float cf && cf >= 0) return (int)cf;
                var cc = FindPropOrFieldVal(cfg, "characterConfig");
                if (cc != null)
                {
                    var cv = FindPropOrFieldVal(cc, "cost");
                    if (cv is int ci2) return ci2;
                    if (cv is double dd2) return (int)dd2;
                    if (cv is float ff2) return (int)ff2;
                }
                return -1;
            }
            catch { return -1; }
        }

        /// <summary>僵尸卡商店数据（对战模式用）：[{id,name,cost}]，已过滤对战禁卡。</summary>
        public static string ZombieShopJson()
        {
            var sb = new System.Text.StringBuilder("[");
            bool first = true;
            try
            {
                var ids = GetPacketIds(false);
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    if (NetPvp.IsBannedCard(id, true)) continue;
                    int cost = PacketCost(id);
                    if (cost < 0) cost = 0;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":\"").Append(EscapeJson(id))
                      .Append("\",\"name\":\"").Append(EscapeJson(GetPacketDisplayNameZh(id)))
                      .Append("\",\"cost\":").Append(cost).Append('}');
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("僵尸商店枚举异常: " + ex.Message); }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>中文名与价格速查（日志/诊断用单行）。</summary>
        public static string PacketBrief(string id)
        {
            return (GetPacketDisplayNameZh(id) ?? id) + "(" + PacketCost(id) + ")";
        }

        /// <summary>对战卡槽面板用：僵尸卡 id 列表（已过滤对战禁卡表）。
        /// 不走 JSON —— AOT 环境下解析 JSON 反而麻烦，UI 直接用数组更简单。</summary>
        public static System.Collections.Generic.List<string> PvpZombieCardIds()
        {
            var outIds = new System.Collections.Generic.List<string>();
            try
            {
                var ids = GetPacketIds(false);
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    if (NetPvp.IsBannedCard(id, true)) continue;
                    outIds.Add(id);
                }
            }
            catch { }
            return outIds;
        }

        /// <summary>对战卡槽面板用：卡的官方价格（读不到返回 0）。</summary>
        public static int PvpCardCost(string id)
        {
            int c = PacketCost(id);
            return c < 0 ? 0 : c;
        }

        /// <summary>对战卡槽面板用：卡的中文名。</summary>
        public static string PvpCardName(string id)
        {
            return GetPacketDisplayNameZh(id) ?? id;
        }

        /// <summary>取一张卡（TowerDefenseInGamePacketShow）的卡牌 id。
        /// 0.26 的 PacketShow 没有 packetName/packetId 字段（那是 0.25.5 的结构），
        /// 真实 id 在 config.saveKey；兜底再试 packetName/id/originalSaveKey/自身 saveKey。</summary>
        public static string PacketShowId(object packetShow)
        {
            try
            {
                if (packetShow == null) return null;
                string pn = null;
                var cfg = FindPropOrFieldVal(packetShow, "config");
                if (cfg != null)
                {
                    pn = FindPropOrFieldVal(cfg, "saveKey") as string;
                    if (string.IsNullOrEmpty(pn)) pn = FindPropOrFieldVal(cfg, "packetName") as string;
                    if (string.IsNullOrEmpty(pn)) pn = FindPropOrFieldVal(cfg, "id") as string;
                }
                if (string.IsNullOrEmpty(pn)) pn = FindPropOrFieldVal(packetShow, "originalSaveKey") as string;
                if (string.IsNullOrEmpty(pn)) pn = FindPropOrFieldVal(packetShow, "saveKey") as string;
                return pn;
            }
            catch { return null; }
        }

        /// <summary>对战模式的禁卡表：植物方不得携带的卡（灰烬植物等）。
        /// 名单在 NetPvp 里维护（可用 mod\pvp_ban.txt 覆盖，改完执行 NetPvpReload 热更）。</summary>
        static bool IsPvpBannedPlant(string packetId)
        {
            return NetPvp.IsBannedCard(packetId, false);
        }

        /// <summary>对战模式禁卡拦截（patcher 注入 TowerDefenseInGamePacketShow.Plant 与
        /// TowerDefensePacketConfig.Plant 开头：if (ShouldBlockThisPlant(this)) return null;）。
        /// 返回 null 是安全的：Plant 自身在 !alive 与 config.Plant 失败时就是 return null。</summary>
        public static bool ShouldBlockThisPlant(object packetShow)
        {
            try
            {
                if (!NetSession.InRoom || !NetSession.IsBattleMode) return false;
                // ★ 僵尸方根本不能种植物：他没有植物卡槽，任何种植入口都应拒绝。
                if (NetSession.MyFaction == NetFaction.Zombie)
                {
                    if (!_pvpBanLogged) { _pvpBanLogged = true; Bootstrap.Log("对战模式：已拦截僵尸方种植"); }
                    return true;
                }
                string id = PacketShowId(packetShow);
                if (!IsPvpBannedPlant(id)) return false;
                if (!_pvpBanLogged)
                {
                    _pvpBanLogged = true;
                    Bootstrap.Log("对战模式：已拦截禁卡 " + id);
                }
                return true;
            }
            catch { return false; }
        }
        static bool _pvpBanLogged;
        /// <summary>分区拦截只记一次日志（否则每次越界种植都刷屏）。</summary>
        static bool _pvpZoneLogged;

        /// <summary>对战模式：种植位置与禁卡的双重校验（patcher 注入 TowerDefenseInGamePacketShow.Plant 头部，
        /// 传 this 与 gridPos）。返回 true 时注入的代码会直接 return null。
        /// ★ 分区规则的落点：植物只能种在左半场（1..NetPvp.PlantMaxColumn），右半场留给僵尸方。</summary>
        public static bool ShouldBlockPlantAt(object packetShow, Vector2I gridPos)
        {
            try
            {
                if (!NetSession.InRoom || !NetSession.IsBattleMode) return false;
                // ★ 僵尸方不能种植物
                if (NetSession.MyFaction == NetFaction.Zombie)
                {
                    if (!_pvpZoneLogged) { _pvpZoneLogged = true; Bootstrap.Log("对战模式：已拦截僵尸方种植"); }
                    return true;
                }
                // ① 禁卡（灰烬等）
                if (IsPvpBannedPlant(PacketShowId(packetShow)))
                {
                    if (!_pvpBanLogged) { _pvpBanLogged = true; Bootstrap.Log("对战模式：已拦截禁卡"); }
                    return true;
                }
                // ② 分区：越到右半场 / 交战带的种植直接拒绝
                if (gridPos.X > NetPvp.PlantMaxColumn)
                {
                    if (!_pvpZoneLogged)
                    {
                        _pvpZoneLogged = true;
                        Bootstrap.Log("对战模式：已拦截越区种植 x=" + gridPos.X +
                                      "（植物限 1.." + NetPvp.PlantMaxColumn + " 列）");
                    }
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>联机镜像：实体稳定 id（Godot 实例 id 低 32 位）。</summary>
        public static uint NetGetNodeId(Node2D n)
        {
            try { return n != null ? (uint)(n.GetInstanceId() & 0xFFFFFFFFUL) : 0u; } catch { return 0u; }
        }

        /// <summary>联机镜像：血量百分比（0-100；读不到时返回 100）。</summary>
        public static int NetGetNodeHpPercent(Node2D n)
        {
            try
            {
                if (n == null) return 100;
                double cur = -1, max = -1;
                string[] curNames = { "hp", "HP", "health", "Health", "currentHealth", "CurrentHealth", "bodyHp" };
                string[] maxNames = { "maxHp", "MaxHp", "maxHealth", "MaxHealth", "totalHp", "TotalHp" };
                for (int i = 0; i < curNames.Length && cur < 0; i++) cur = NetToDouble(FindPropOrFieldVal(n, curNames[i]));
                for (int i = 0; i < maxNames.Length && max < 0; i++) max = NetToDouble(FindPropOrFieldVal(n, maxNames[i]));
                if (cur >= 0 && max > 0) return (int)System.Math.Max(1, System.Math.Min(100, cur * 100.0 / max));
            }
            catch { }
            return 100;
        }

        // ================= M4 真·共享战场实体同步 =================

        /// <summary>植物缓存（电脑版叫 _cachePlants；手机版原本没有，为 M4 同步补上）。</summary>
        static readonly System.Collections.Generic.List<Node2D> _plantCache = new System.Collections.Generic.List<Node2D>();
        public static System.Collections.Generic.List<Node2D> GetPlantCache() { return _plantCache; }

        /// <summary>从任意角色节点反查「可再生成」的卡牌 id（植物/僵尸通用）。
        /// ★ 逐个候选名尝试并用 GetConfig 验证：卡配置上的 name 可能是 TOWERDEFENSE_* 翻译键，
        ///   直接拿去 SpawnCharacter 会失败，所以能解析成真配置的才算数。</summary>
        public static string NetGetNodePacketId(Node2D n)
        {
            try
            {
                if (n == null) return null;
                // ★ 小推车/车辆不是“可同步实体”：它们会被 ESP 判成 Plant 阵营而混进 _cachePlants，
                //   拿去另一侧 SpawnCharacter 会真的生成一辆车 → **草坪上多刷出小推车**（用户实际报过）。
                string tname = n.GetType().Name;
                if (tname.IndexOf("Mower", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tname.IndexOf("Car", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                object cfg = FindPropOrFieldVal(n, "config");
                if (cfg == null) cfg = FindPropOrFieldVal(n, "characterConfig");
                if (cfg == null) cfg = FindPropOrFieldVal(n, "packetConfig");
                if (cfg == null) return null;
                string[] names = { "saveKey", "packetSaveKey", "id", "packetId", "characterId", "name" };
                for (int i = 0; i < names.Length; i++)
                {
                    var v = FindPropOrFieldVal(cfg, names[i]) as string;
                    if (string.IsNullOrEmpty(v)) continue;
                    if (GetConfig(v) != null) return v;
                }
            }
            catch { }
            return null;
        }

        /// <summary>联机：这个格子是不是"草坪上的合法格子"。
        /// ★ 地图格索引从 1 开始（GetMapGridPos 返回的就是 1 基坐标），
        ///   **(0,0) / 负值不是草坪** —— 传进 Plant 会生成在卡槽那一带（用户实际报过）。
        ///   任何要同步出去的坐标都必须先过这一层校验。</summary>
        public static bool NetIsGridOnLawn(Vector2I g)
        {
            return g.X >= 1 && g.X <= 40 && g.Y >= 1 && g.Y <= 8;
        }

        static MethodInfo _netMapGridPos;

        /// <summary>联机：世界坐标 → 草坪格子。
        /// 优先用游戏自己的 <c>GetMapGridPos(Vector2)</c>（与 Glove 模式同一调用）；
        /// 拿不到时按 80×100 像素粗算（与 ZombieBehaviorControllers.ReadGridPos 一致）。
        /// 返回 (-1,-1) = 算不出来。</summary>
        public static Vector2I NetWorldToGrid(Vector2 world)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm != null)
                {
                    if (_netMapGridPos == null)
                        _netMapGridPos = tdm.GetType().GetMethod("GetMapGridPos", new Type[] { typeof(Vector2) });
                    if (_netMapGridPos != null && _netMapGridPos.Invoke(tdm, new object[] { world }) is Vector2I g
                        && NetIsGridOnLawn(g))
                        return g;
                }
            }
            catch { }
            try
            {
                var g2 = new Vector2I(1 + (int)(world.X / 80.0), 1 + (int)(world.Y / 100.0));
                if (NetIsGridOnLawn(g2)) return g2;
            }
            catch { }
            return new Vector2I(-1, -1);
        }

        /// <summary>联机：节点所在格子。
        /// ★ 读不到 或 读出来不在草坪上（僵尸常见：没格子 / 是 (0,0) / 是负值）时，
        ///   **用世界坐标反推一个合法格子** —— 直接把非法格子发出去会让对方生成在卡槽一带。</summary>
        public static Vector2I NetGetNodeGridPos(Node2D n)
        {
            try
            {
                if (n == null) return new Vector2I(-1, -1);
                var v = FindPropOrFieldVal(n, "gridPos");
                if (v is Vector2I gi && NetIsGridOnLawn(gi)) return gi;
                var w = NetGetNodeWorldPos(n);
                if (w.X > -1e6f) return NetWorldToGrid(w);
            }
            catch { }
            return new Vector2I(-1, -1);
        }

        /// <summary>联机：节点世界坐标（读不到返回 (-1e7,-1e7)）。</summary>
        public static Vector2 NetGetNodeWorldPos(Node2D n)
        {
            try { if (n != null && GodotObject.IsInstanceValid(n)) return n.GlobalPosition; } catch { }
            return new Vector2(-1e7f, -1e7f);
        }

        /// <summary>联机：把刚生成的镜像僵尸摆到发送方的真实位置。
        /// 格子已由 NetGetNodeGridPos 保证 ≥1（不会落到卡槽），但格子只是“整格对齐”，
        /// 僵尸实际 x 是用世界坐标给的 —— 不然同步过来的僵尸全挤在格子中心。
        /// ★ 这里**不再回写 gridPos**：Plant(gridPos) 已经用同一个格子生成过了，
        ///   重复设字段只会多触发一次格子变更回调，没好处。
        /// gx/gy 保留只是为了挡住非法值。</summary>
        public static void NetPlaceEntity(Node2D n, float wx, float wy, int gx, int gy)
        {
            try
            {
                if (n == null || !GodotObject.IsInstanceValid(n)) return;
                if (gx < 1 || gy < 1 || gx > 40 || gy > 8) return;   // 坐标非法就不动它
                if (wx > -1e5f && wx < 1e5f && wy > -1e5f && wy < 1e5f)
                    n.GlobalPosition = new Vector2(wx, wy);
            }
            catch { }
        }

        /// <summary>M4：在指定格子生成实体（植物/僵尸通用，走已验证的 SpawnCharacter）。
        /// ★ 坐标必须落在草坪上（≥1）：非法格子会被夹到 (1,1)，
        ///   否则实体会生成在卡槽那一带（用户实际报过）。</summary>
        public static bool NetSpawnEntity(string packetId, Vector2I gridPos, bool charm)
        {
            try
            {
                if (string.IsNullOrEmpty(packetId)) return false;
                if (gridPos.X < 1) gridPos.X = 1;
                if (gridPos.Y < 1) gridPos.Y = 1;
                if (gridPos.Y > 8) gridPos.Y = 8;
                return SpawnCharacter(packetId, gridPos, charm);
            }
            catch { return false; }
        }

        /// <summary>M4：删除一个实体（镜像副本，直接 QueueFree）。</summary>
        public static void NetFreeEntity(Node2D n)
        {
            try { if (n != null && GodotObject.IsInstanceValid(n)) n.QueueFree(); } catch { }
        }

        /// <summary>M4：强制刷新角色缓存。差分必须在同一帧拿到最新植物/僵尸列表，
        /// 不能等普通缓存的节流刷新（旧列表会漏掉刚生成的实体）。</summary>
        public static void NetRefreshRoleCache()
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                var root = tree != null ? tree.Root : null;
                if (root == null) return;
                _plantCache.Clear(); _zombieCache.Clear();
                CollectPlants(root, _plantCache);
                CollectZombies(root, _zombieCache);
            }
            catch { }
        }

        // ================= M4b：准备门禁 + 波次跟随（电脑/手机同源） =================

        /// <summary>M4b：本机是否已完成"选卡"阶段（游戏自己的 readySetPlantOver 标志）。
        /// 返回 -1 = 还没进关卡/读不到（等价于"未准备"），0 = 选卡中，1 = 已选好。</summary>
        public static int NetGetPlantReady()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return -1;
                var control = GetBattleControl(tdm);
                if (control == null) return -1;
                var wave = FindWaveFeatureInstance(control);
                if (wave == null) return -1;
                object v = FindFieldVal(wave, "readySetPlantOver");
                if (v is bool b) return b ? 1 : 0;
            }
            catch { }
            return -1;
        }

        /// <summary>联机：当前是否在加载/切场景。
        /// 房主“离开战斗就自动结束对局”的判定必须把这段跳过 ——
        /// 在线/每日关卡装填很慢，旧逻辑会在装填期间误判“房主已离开战斗”，
        /// 5 秒后自动结束对局 → 客机立刻看到「房主退出」（用户实际报过）。</summary>
        public static bool NetIsSceneLoading()
        {
            try
            {
                var root = GetTreeRoot();
                return root != null && IsSceneLoadingFast(root);
            }
            catch { return false; }
        }

        /// <summary>Patcher 注入到 levelControl.AwardCreate 开头：返回 true 就直接 return（不结算）。
        /// ★ 客机不自己弹通关奖杯：游戏的胜利判定是“到末波 + 场上无僵尸”，
        ///   而客机的僵尸全是镜像的（会被打光）→ 一到末波客机就自己弹奖杯。
        ///   通关只由房主判定；房主结算后会自动结束对局、双方回主菜单。</summary>
        public static bool ShouldBlockClientAward()
        {
            try
            {
                if (NetSession.State == NetState.Offline) return false;   // 单机照常结算
                if (!NetSession.InRoom) return false;
                return !NetSession.IsHost;                                // 联机中的客机：不结算
            }
            catch { return false; }
        }

        /// <summary>M4b：本机是否正处于塔防战斗关卡里（房主用它自动结束对局）。</summary>
        public static bool NetIsInBattleLevel()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return false;
                var control = GetBattleControl(tdm);
                if (control == null) return false;
                return FindWaveFeatureInstance(control) != null;
            }
            catch { return false; }
        }

        /// <summary>M4b：把客机本关**所有波次**的刷怪数量改成 0。
        /// 客机不再刷任何僵尸（僵尸全部由房主同步过来），但**波次流程本身照常跑** ——
        /// 进度条、波次旗子、波次计数显示都正常。
        ///
        /// ★ 上一版是直接掐掉波次处理（debugWavePaused / waveStart=false / awaitSpawn=false），
        ///   结果连波次显示一起没了（用户反馈「波次在客机不显示」）。
        ///   现在改成从"刷什么"下手，不碰波次流程本身。
        /// 首次清零时会把原值快照下来，NetWaveSpawnsRestore() 可以完整还原。
        /// 返回是否至少改了一处（无改动 / 读不到都返回 false）。</summary>
        public static bool NetZeroLocalSpawns()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return false;
                object control = GetBattleControl(tdm);
                if (control == null) return false;
                object wave = FindWaveFeatureInstance(control);
                if (wave == null) return false;
                var cfg = FindPropOrFieldVal(wave, "config");
                if (cfg == null) return false;
                var waveArr = FindPropOrFieldVal(cfg, "wave");       // get_wave 属性
                if (!(waveArr is System.Collections.IEnumerable waveEnum)) return false;

                int changed = 0;
                foreach (var waveObj in waveEnum)
                {
                    if (waveObj == null) continue;
                    var spawnList = FindFieldVal(waveObj, "spawn");
                    if (spawnList == null) spawnList = FindPropOrFieldVal(waveObj, "spawn");
                    if (!(spawnList is System.Collections.IEnumerable spawnEnum)) continue;
                    foreach (var spawnObj in spawnEnum)
                    {
                        if (spawnObj == null) continue;
                        try
                        {
                            var st = spawnObj.GetType();
                            const System.Reflection.BindingFlags BF =
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

                            // num 是小写 Int32 字段（0.26 实测）；兜底大写 Num / 同名的 double 与属性
                            var numF = st.GetField("num", BF) ?? st.GetField("Num", BF);
                            if (numF != null)
                            {
                                object raw = numF.GetValue(spawnObj);
                                int cv = raw is int iv ? iv : (raw is double dv ? (int)dv : -1);
                                if (cv == 0) continue;
                                if (!_netSpawnBackup.ContainsKey(spawnObj))
                                    _netSpawnBackup[spawnObj] = new NetSpawnNum { F = numF, Orig = cv };
                                if (numF.FieldType == typeof(double)) numF.SetValue(spawnObj, 0.0);
                                else numF.SetValue(spawnObj, 0);
                                changed++;
                                continue;
                            }

                            var numP = st.GetProperty("num", BF) ?? st.GetProperty("Num", BF);
                            if (numP != null && numP.CanWrite)
                            {
                                object raw = numP.GetValue(spawnObj);
                                int cv = raw is int iv2 ? iv2 : (raw is double dv2 ? (int)dv2 : -1);
                                if (cv == 0) continue;
                                if (!_netSpawnBackup.ContainsKey(spawnObj))
                                    _netSpawnBackup[spawnObj] = new NetSpawnNum { P = numP, Orig = cv };
                                if (numP.PropertyType == typeof(double)) numP.SetValue(spawnObj, 0.0);
                                else numP.SetValue(spawnObj, 0);
                                changed++;
                            }
                        }
                        catch { }
                    }
                }
                if (changed > 0)
                {
                    _netWaveZeroed = true;
                    Bootstrap.Log("联机镜像: 客机本关刷怪已清零（" + changed + " 处），僵尸全部由房主同步");
                }
                return changed > 0;
            }
            catch { return false; }
        }

        static bool _netWaveZeroed;

        /// <summary>被清零的刷怪项：记住成员与原始数量，退出联机时原样写回。</summary>
        sealed class NetSpawnNum
        {
            public System.Reflection.FieldInfo F;
            public System.Reflection.PropertyInfo P;
            public int Orig;
        }

        static readonly System.Collections.Generic.Dictionary<object, NetSpawnNum> _netSpawnBackup =
            new System.Collections.Generic.Dictionary<object, NetSpawnNum>();

        /// <summary>把被 NetZeroLocalSpawns 清零的刷怪数量还原回原值。
        /// 退出联机 / 对局结束时必须调用，否则之后单机玩同一关会一个僵尸都不刷。</summary>
        public static void NetWaveSpawnsRestore()
        {
            try
            {
                if (_netSpawnBackup.Count == 0) { _netWaveZeroed = false; return; }
                int n = 0;
                foreach (var kv in _netSpawnBackup)
                {
                    var b = kv.Value;
                    if (b == null) continue;
                    try
                    {
                        if (b.F != null)
                        {
                            if (b.F.FieldType == typeof(double)) b.F.SetValue(kv.Key, (double)b.Orig);
                            else b.F.SetValue(kv.Key, b.Orig);
                            n++;
                        }
                        else if (b.P != null && b.P.CanWrite)
                        {
                            if (b.P.PropertyType == typeof(double)) b.P.SetValue(kv.Key, (double)b.Orig);
                            else b.P.SetValue(kv.Key, b.Orig);
                            n++;
                        }
                    }
                    catch { }
                }
                _netSpawnBackup.Clear();
                _netWaveZeroed = false;
                if (n > 0) Bootstrap.Log("联机镜像: 已还原本地刷怪数量（" + n + " 处）");
            }
            catch { }
        }

        /// <summary>客机本关刷怪是否已清零（面板显示用）。</summary>
        public static bool NetWaveZeroed { get { return _netWaveZeroed; } }

        static bool _netNoWaves;

        /// <summary>客机是否处于「不跑自己的波次」状态。</summary>
        public static bool NetNoLocalWavesOn { get { return _netNoWaves; } }

        /// <summary>M4g：客机**完全不跑自己的波次**（僵尸全部由房主同步）。
        /// 做两件事，且只在开关变化时做一次（不重复扫、不反复写，避免和游戏抢状态）：
        ///   ① 快照并清零本关所有波次的刷怪数量 → 客机一个僵尸都不会自己刷出来
        ///   ② Wave feature 的 <c>waveStart</c> / <c>awaitSpawn</c> 置 false → 客机波次不再自己推进
        ///
        /// ★ 故意**不碰** <c>CommandManager.debugWavePaused</c>：
        ///   那个开关会让 <c>WavePhysicsProcess</c> 整段被跳过，而客机的
        ///   <c>readySetPlantOver</c>（准备门禁唯一信号）很可能就在那段里置位 ——
        ///   一旦被跳过，客机的准备状态永远是“没选好”，准备门禁直接卡死。
        /// ★ 不还原 waveStart/awaitSpawn（关卡重载会重建这些对象），只还原刷怪数量。</summary>
        public static bool NetNoLocalWaves(bool on)
        {
            if (on == _netNoWaves) return true;      // 幂等：状态没变什么都不做
            if (!on)
            {
                _netNoWaves = false;
                NetWaveSpawnsRestore();
                return true;
            }

            bool haveWave = false;
            try
            {
                var tdm = GetTdmInstance();
                if (tdm != null)
                {
                    var control = GetBattleControl(tdm);
                    var wave = control != null ? FindWaveFeatureInstance(control) : null;
                    if (wave != null)
                    {
                        SetPropOrField(wave, "waveStart", false);
                        SetPropOrField(wave, "awaitSpawn", false);
                        haveWave = true;
                    }
                }
            }
            catch { }

            NetZeroLocalSpawns();                   // 刷怪清零（内部有快照 + 去重）

            if (haveWave)
            {
                _netNoWaves = true;
                Bootstrap.Log("联机镜像: 客机波次已停用（不刷怪、不推进），僵尸全由房主同步");
            }
            return haveWave;
        }

        /// <summary>M4：读一个实体是不是"魅惑/我方"状态（同步时要带过去，否则镜像出来阵营是错的）。
        /// 只读布尔语义的字段名，不去猜 camp/side 这类数值阵营字段（猜错反而更糟）。</summary>
        public static bool NetGetNodeCharmed(Node2D n)
        {
            try
            {
                if (n == null) return false;
                string[] names = { "charm", "charmed", "isCharm", "isCharmed", "mindControl", "isMindControl" };
                for (int i = 0; i < names.Length; i++)
                {
                    object v = FindPropOrFieldVal(n, names[i]);
                    if (v is bool b) return b;
                }
            }
            catch { }
            return false;
        }

        /// <summary>M4b：把本机波次号对齐到房主的波次（两边进度/显示一致）。
        /// ★ 客机会被夹到“倒数第二波”：游戏的胜利判定是“到末波 + 场上无僵尸”，
        ///   而客机的僵尸全是镜像的（会被打光）→ 一到末波客机就自己弹通关奖杯。
        ///   通关只应该由房主判定（房主结算后会自动结束对局、双方回主菜单）。</summary>
        public static bool NetSetWaveIndex(int waveIndex)
        {
            try
            {
                if (waveIndex < 0) return false;
                var tdm = GetTdmInstance();
                if (tdm == null) return false;
                var control = GetBattleControl(tdm);
                if (control == null) return false;
                var wave = FindWaveFeatureInstance(control);
                if (wave == null) return false;

                if (!NetSession.IsHost)
                {
                    int total = NetGetWaveTotal(wave);
                    if (total > 1 && waveIndex >= total)
                    {
                        if (!_netWaveClampLogged)
                        {
                            _netWaveClampLogged = true;
                            Bootstrap.Log("联机镜像: 客机波次夹到 " + (total - 1) + "（总 " + total + " 波），避免客机自行通关弹奖杯");
                        }
                        waveIndex = total - 1;
                    }
                }

                var cur = FindFieldVal(wave, "currentWave");
                if (cur != null && System.Convert.ToInt32(cur) == waveIndex) return true;
                SetPropOrField(wave, "currentWave", waveIndex);
                return true;
            }
            catch { return false; }
        }

        static bool _netWaveClampLogged;

        /// <summary>本关总波数（读不到返回 -1）。优先 config.wave 数组长度，兜底 wave 对象上的几个常见总波数字段。</summary>
        static int NetGetWaveTotal(object wave)
        {
            try
            {
                var cfg = FindPropOrFieldVal(wave, "config");
                if (cfg != null)
                {
                    var arr = FindPropOrFieldVal(cfg, "wave");
                    if (arr is System.Collections.ICollection col && col.Count > 0) return col.Count;
                }
                string[] names = { "totalWave", "maxWave", "waveCount", "totalWaves", "allWaveCount" };
                for (int i = 0; i < names.Length; i++)
                {
                    object v = FindPropOrFieldVal(wave, names[i]);
                    if (v is int n && n > 0) return n;
                }
            }
            catch { }
            return -1;
        }

static MethodInfo _netGetSun, _netSetSun;

        /// <summary>联机：当前阳光（-1 = 不在战斗/读取失败）。</summary>
        public static long NetGetSun()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return -1;
                if (_netGetSun == null) _netGetSun = tdm.GetType().GetMethod("GetSun", Type.EmptyTypes);
                if (_netGetSun == null) return -1;
                return System.Convert.ToInt64(_netGetSun.Invoke(tdm, null));
            }
            catch { return -1; }
        }

        /// <summary>联机：把阳光设为指定值（客机服从房主）。</summary>
        public static bool NetSetSun(long v)
        {
            try
            {
                if (v < 0) v = 0;
                var tdm = GetTdmInstance();
                if (tdm == null) return false;
                if (_netSetSun == null) _netSetSun = tdm.GetType().GetMethod("SetSun", new Type[] { typeof(long) });
                if (_netSetSun == null) return false;
                _netSetSun.Invoke(tdm, new object[] { v });
                return true;
            }
            catch { return false; }
        }

        /// <summary>联机：是否在战斗中（有 currentControl）。</summary>
        public static bool NetInBattle()
        {
            try
            {
                var tdm = GetTdmInstance();
                return tdm != null && GetBattleControl(tdm) != null;
            }
            catch { return false; }
        }

        /// <summary>联机：当前波次（-1 = 未知）。</summary>
        public static int NetGetWave()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return -1;
                var control = GetBattleControl(tdm);
                if (control == null) return -1;
                var wave = FindWaveFeatureInstance(control);
                if (wave == null) return -1;
                var v = FindFieldVal(wave, "currentWave");
                if (v == null) return -1;
                return System.Convert.ToInt32(v);
            }
            catch { return -1; }
        }

        /// <summary>联机：场上僵尸数（-1 = 无缓存）。</summary>
        public static int NetGetZombieCount()
        {
            try { return _zombieCache != null ? _zombieCache.Count : -1; }
            catch { return -1; }
        }

        
/// <summary>联机：这个 key 是不是 user:// 下的关卡文件（每日/在线）。返回它的目录，不是则返回 ""。</summary>
        public static string NetUserLevelDir(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.StartsWith("DailyLevel-", System.StringComparison.Ordinal)) return "user://DailyLevel";
            if (key.StartsWith("OnlineLevel-", System.StringComparison.Ordinal)) return "user://OnlineLevel";
            return "";
        }

        /// <summary>联机：key 能不能安全地当文件名用。
        /// 拦的是真正危险的东西（路径分隔符、Windows 保留字符、`..` 穿越），
        /// 不是“只放行字母数字” —— 在线关卡文件名里可能有别的合法字符，
        /// 过严会让推送静默失败（用户表现为“点了开始对局客机完全没反应”）。</summary>
        static bool NetSafeLevelKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.Length > 120) return false;
            if (key == "." || key == "..") return false;
            if (key.Contains("..")) return false;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?'
                    || c == '"' || c == '<' || c == '>' || c == '|') return false;
                if (c < 32) return false;
            }
            return true;
        }

        /// <summary>联机：user:// 关卡文件的完整路径。
        /// ★ 先按 key 直拼；文件不存在时**扫目录找同名文件**（大小写/后缀差异都能对上）——
        ///   旧版只放行字母数字减号，文件名里带别的字符就直接返回 "" → 推送静默失败。</summary>
        public static string NetUserLevelPath(string key)
        {
            string dir = NetUserLevelDir(key);
            if (dir.Length == 0 || !NetSafeLevelKey(key)) return "";
            string direct = dir + "/" + key + ".json";
            try
            {
                if (Godot.FileAccess.FileExists(direct)) return direct;
                var da = Godot.DirAccess.Open(dir);
                if (da != null)
                {
                    var files = da.GetFiles();
                    for (int i = 0; i < files.Length; i++)
                    {
                        string fn = files[i];
                        if (string.IsNullOrEmpty(fn)) continue;
                        if (!fn.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)) continue;
                        string baseName = fn.Substring(0, fn.Length - 5);
                        if (string.Equals(baseName, key, System.StringComparison.OrdinalIgnoreCase))
                            return dir + "/" + fn;
                    }
                }
            }
            catch { }
            return direct;   // 文件还不存在（写入时用）
        }

        public static bool NetHasUserLevelFile(string key)
        {
            try
            {
                string p = NetUserLevelPath(key);
                return p.Length > 0 && Godot.FileAccess.FileExists(p);
            }
            catch { return false; }
        }

        public static string NetReadUserLevelFile(string key)
        {
            try
            {
                string p = NetUserLevelPath(key);
                if (p.Length == 0 || !Godot.FileAccess.FileExists(p)) return null;
                return Godot.FileAccess.GetFileAsString(p);
            }
            catch { return null; }
        }

        /// <summary>把房主推过来的关卡文件写到本地 user://（目录不存在会自动建）。</summary>
        public static bool NetWriteUserLevelFile(string key, string text)
        {
            try
            {
                string p = NetUserLevelPath(key);
                if (p.Length == 0 || string.IsNullOrEmpty(text)) return false;
                string dir = NetUserLevelDir(key);
                if (Godot.DirAccess.Open(dir) == null)
                {
                    try { Godot.DirAccess.MakeDirRecursiveAbsolute(dir); } catch { }
                }
                var fa = Godot.FileAccess.Open(p, Godot.FileAccess.ModeFlags.Write);
                if (fa == null) return false;
                fa.StoreString(text);
                fa.Close();
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("关卡文件写入异常 " + key + ": " + ex.Message); return false; }
        }

        /// <summary>联机选关：递归收集注册表里的 SaveKey（章节中文名跟随最近的 Name 字段）。</summary>
        static void WalkSaveKeys(Godot.Variant node, string chapter, System.Collections.Generic.List<string[]> outp)
        {
            try
            {
                if (node.VariantType == Variant.Type.Dictionary)
                {
                    var d = node.AsGodotDictionary();
                    string cur = chapter;
                    if (d.ContainsKey("Name"))
                    {
                        var nv = d["Name"];
                        if (nv.VariantType == Variant.Type.String)
                        {
                            string s = CleanLevelText(nv.AsString());
                            if (!string.IsNullOrEmpty(s)) cur = s;
                        }
                    }
                    if (d.ContainsKey("SaveKey"))
                    {
                        var sv = d["SaveKey"];
                        if (sv.VariantType == Variant.Type.String)
                        {
                            string key = sv.AsString();
                            if (!string.IsNullOrEmpty(key)) outp.Add(new string[] { key, cur, "" });
                        }
                    }
                    foreach (var k in d.Keys)
                    {
                        if (k.VariantType != Variant.Type.String) continue;
                        var v = d[k];
                        if (v.VariantType == Variant.Type.Dictionary || v.VariantType == Variant.Type.Array)
                            WalkSaveKeys(v, cur, outp);
                    }
                }
                else if (node.VariantType == Variant.Type.Array)
                {
                    var a = node.AsGodotArray();
                    for (int i = 0; i < a.Count; i++) WalkSaveKeys(a[i], chapter, outp);
                }
            }
            catch { }
        }

        
        /// <summary>把反射读到的值安全转 double（避开 AOT 裁剪的 Convert）。</summary>
        static double NetToDouble(object v)
        {
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is uint u) return u;
            if (v is short s) return s;
            if (v is byte b) return b;
            return -1;
        }
        /// <summary>联机选关：去掉富文本标签（如 [rainbow freq=1 …]）与换行。</summary>
        public static string CleanLevelText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                var sb = new System.Text.StringBuilder(s.Length);
                int depth = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '[') { depth++; continue; }
                    if (c == ']') { if (depth > 0) depth--; continue; }
                    if (depth > 0) continue;
                    if (c == '\n' || c == '\r' || c == '|') { sb.Append(' '); continue; }
                    sb.Append(c);
                }
                string r = sb.ToString().Trim();
                while (r.Contains("  ")) r = r.Replace("  ", " ");
                if (r.Length > 40) r = r.Substring(0, 40);
                return r;
            }
            catch { return s; }
        }
        static Node GetTreeRoot()
        {
            try
            {
                var tree = Engine.GetMainLoop();
                if (tree is SceneTree st && st.Root != null) return st.Root;
            }
            catch { }
            return null;
        }
        /// <summary>联机选关：扫描 user:// 下的关卡 json（每日/在线），取 key + 中文关卡名 + 说明。</summary>
        public static void ListLevelsFromDir(string userDir, System.Collections.Generic.List<string[]> outp)
        {
            try
            {
                var da = Godot.DirAccess.Open(userDir);
                if (da == null)
                {
                    // 目录还不存在（新号 / 手机上从没下载过每日-在线关卡）→
                    // 建出来，这样房主推过来的关卡文件有地方落盘；列表这次还是空的。
                    try { Godot.DirAccess.MakeDirRecursiveAbsolute(userDir); } catch { }
                    Bootstrap.Log("选关列表: " + userDir + " 不存在（已尝试创建；需要先在游戏内下载过，或由房主推送）");
                    return;
                }
                var files = da.GetFiles();
                for (int i = 0; i < files.Length; i++)
                {
                    string fn = files[i];
                    if (string.IsNullOrEmpty(fn)) continue;
                    if (!fn.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)) continue;
                    string key = fn.Substring(0, fn.Length - 5);
                    string full = userDir + "/" + fn;
                    string name = key, desc = "";
                    try
                    {
                        var fa = Godot.FileAccess.Open(full, Godot.FileAccess.ModeFlags.Read);
                        if (fa != null)
                        {
                            string txt = fa.GetAsText();
                            fa.Close();
                            var j = new Godot.Json();
                            if (j.Parse(txt, false) == Godot.Error.Ok && j.Data.VariantType == Variant.Type.Dictionary)
                            {
                                var d = j.Data.AsGodotDictionary();
                                if (d.ContainsKey("LevelName")) name = CleanLevelText(d["LevelName"].AsString());
                                if (d.ContainsKey("Description")) desc = CleanLevelText(d["Description"].AsString());
                            }
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(name)) name = key;
                    outp.Add(new string[] { key, name, desc });
                }
                Bootstrap.Log("选关列表: " + userDir + " → " + outp.Count + " 关");
            }
            catch (System.Exception ex) { Bootstrap.Log("选关目录异常 " + userDir + ": " + ex.Message); }
        }
        /// <summary>联机选关：按类型列出关卡（每项：key / 中文显示名 / 备注）。
        /// type: adv(冒险) fun(娱乐/小游戏) challenge(挑战) puzzle(拼图) survival(生存) daily(每日关卡) online(在线关卡)。</summary>
        public static System.Collections.Generic.List<string[]> ListLevels(string type)
        {
            var outp = new System.Collections.Generic.List<string[]>();
            try
            {
                if (type == "daily") { ListLevelsFromDir("user://DailyLevel", outp); return outp; }
                if (type == "online") { ListLevelsFromDir("user://OnlineLevel", outp); return outp; }

                string mode = "Adventure";
                if (type == "fun") mode = "MiniGames";
                else if (type == "challenge") mode = "Challenge";
                else if (type == "puzzle") mode = "Puzzle";
                else if (type == "survival") mode = "Survival";

                var fa = Godot.FileAccess.Open("res://Asset/Config/Level/LevelResource.json", Godot.FileAccess.ModeFlags.Read);
                if (fa == null) return outp;
                string jsonText = fa.GetAsText();
                fa.Close();
                var json = new Godot.Json();
                if (json.Parse(jsonText, false) != Godot.Error.Ok) return outp;
                if (json.Data.VariantType != Variant.Type.Dictionary) return outp;
                var root = json.Data.AsGodotDictionary();
                if (!root.ContainsKey(mode)) return outp;
                WalkSaveKeys(root[mode], "", outp);

                // 章节内编号：显示为「章节名 第N关」
                var counter = new System.Collections.Generic.Dictionary<string, int>();
                for (int i = 0; i < outp.Count; i++)
                {
                    string ch = outp[i][1];
                    int n;
                    if (!counter.TryGetValue(ch, out n)) n = 0;
                    n++;
                    counter[ch] = n;
                    outp[i][1] = string.IsNullOrEmpty(ch) ? outp[i][0] : ch + " 第" + n + "关";
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("选关列表异常: " + ex.Message); }
            return outp;
        }
        // ===== 共享角色缓存：所有需要遍历植物/僵尸的功能共用（每 60 帧刷新一次，避免各自全树遍历+反射——卡顿根因） =====
        /// <summary>联机：当前关卡标识（saveKey/name/levelId 依次尝试；读不到返回空串）。</summary>
        public static string NetGetLevelKey()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return "";
                var cfg = FindPropOrFieldVal(tdm, "currentLevelConfig");
                if (cfg != null)
                {
                    foreach (var nm in new string[] { "saveKey", "SaveKey", "name", "Name", "levelId", "LevelId", "id", "Id" })
                    {
                        var s = FindPropOrFieldVal(cfg, nm) as string;
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>联机：当前关卡引用（优先资源路径 res:// / uid://，读不到时回退 saveKey）。</summary>
        public static string NetGetLevelRef()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm != null)
                {
                    var cfg = FindPropOrFieldVal(tdm, "currentLevelConfig");
                    if (cfg is Godot.Resource res)
                    {
                        string rp = res.ResourcePath;
                        if (!string.IsNullOrEmpty(rp) && (rp.StartsWith("res://") || rp.StartsWith("uid://"))) return rp;
                    }
                }
            }
            catch { }
            return NetGetLevelKey();
        }

        /// <summary>联机：按关卡引用进关（支持 res:// 路径 / uid:// / saveKey 三种形式）。返回是否成功。</summary>
        public static bool EnterLevelBySaveKey(string levelRef)
        {
            try
            {
                if (string.IsNullOrEmpty(levelRef)) return false;
                object cfg = null;
                if (levelRef.StartsWith("res://"))
                    cfg = Godot.GD.Load(levelRef);
                else if (levelRef.StartsWith("uid://"))
                    cfg = LoadLevelResourceByUid(levelRef);
                else
                {
                    string uid = FindLevelUidBySaveKey(levelRef);
                    if (!string.IsNullOrEmpty(uid)) cfg = LoadLevelResourceByUid(uid);
                }

                if (cfg == null) { Bootstrap.Log("联机进关: 找不到关卡配置 " + levelRef); return false; }
                // 主菜单早期玩法图集未就绪，直接 ChangeScene 会失败 → 排队等就绪
                if (IsGameplayReady())
                {
                    EnterLevelWithConfig(cfg, "LoadLevel");
                    Bootstrap.Log("联机进关: " + levelRef + " → " + cfg.GetType().Name);
                }
                else
                {
                    _pendingEnterCfg = cfg;
                    _pendingEnterMode = "LoadLevel";
                    _pendingLoadStarted = false;
                    Bootstrap.Log("联机进关: 玩法资源未就绪，已排队 " + levelRef);
                }
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("联机进关异常: " + ex.Message); return false; }
        }

        // ---- 待进关（玩法资源未就绪时排队，就绪后自动 ChangeScene）----
        static object _pendingEnterCfg;
        static string _pendingEnterMode = "LoadLevel";
        static bool _pendingLoadStarted;
        static long _pendingTickStart;

        /// <summary>玩法图集是否就绪（主菜单早期可能未就绪，此时直接进关会失败）。</summary>
        static bool IsGameplayReady()
        {
            try
            {
                var t = FindType("ResourceManager");
                if (t == null) return false;
                var ip = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var inst = ip != null ? ip.GetValue(null) : null;
                if (inst == null) return false;
                var p = t.GetProperty("AreGameplayAtlasesReady", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null && p.CanRead) { var v = p.GetValue(inst); return v is bool b && b; }
                return false;
            }
            catch { return false; }
        }

        /// <summary>每帧：等玩法资源就绪后自动进关（排队中的关卡配置）。</summary>
        static void TickPendingEnter()
        {
            if (_pendingEnterCfg == null) return;
            if (!_pendingLoadStarted)
            {
                _pendingLoadStarted = true;
                _pendingTickStart = System.Environment.TickCount64;
                return;
            }
            if (IsGameplayReady())
            {
                var cfg = _pendingEnterCfg;
                string mode = _pendingEnterMode;
                _pendingEnterCfg = null;
                _pendingLoadStarted = false;
                EnterLevelWithConfig(cfg, mode);
                Bootstrap.Log("联机进关: 资源就绪，已进关 " + mode);
                return;
            }
            if (System.Environment.TickCount64 - _pendingTickStart > 240000)
            {
                _pendingEnterCfg = null;
                _pendingLoadStarted = false;
                Bootstrap.Log("联机进关: 等待玩法资源就绪超时（放弃）");
            }
        }

        /// <summary>回到主菜单（结束对局 / 退出关卡）。</summary>
        public static void BackToMenu()
        {
            try
            {
                var sm = FindType("SceneManager");
                if (sm == null) { NetLog.Warn("回主菜单: 找不到 SceneManager"); return; }
                var smInst = GetSingleton(sm);
                if (smInst == null) { NetLog.Warn("回主菜单: SceneManager 实例不可用"); return; }

                // ★ 之前只试了一种签名（string,bool），一旦实际签名不同就静默失效。
                //   现在依次尝试常见形式，并记录到底哪种生效/为何失败。
                var argTs = new Type[4][];
                var argVs = new object[4][];
                argTs[0] = new[] { typeof(string), typeof(bool) };
                argVs[0] = new object[] { "MainMenu", false };
                argTs[1] = new[] { typeof(string) };
                argVs[1] = new object[] { "MainMenu" };
                argTs[2] = new[] { typeof(StringName), typeof(bool) };
                argVs[2] = new object[] { new StringName("MainMenu"), false };
                argTs[3] = new[] { typeof(StringName) };
                argVs[3] = new object[] { new StringName("MainMenu") };

                var names = new[] { "ChangeScene", "ChangeSceneTo", "SwitchScene", "GoToScene", "LoadScene" };
                for (int n = 0; n < names.Length; n++)
                {
                    for (int k = 0; k < argTs.Length; k++)
                    {
                        var m = sm.GetMethod(names[n], argTs[k]);
                        if (m == null) continue;
                        try
                        {
                            m.Invoke(smInst, argVs[k]);
                            NetLog.Info("结束对局: 已用 " + names[n] + "(" + argTs[k].Length + "参) 返回主菜单");
                            return;
                        }
                        catch (System.Exception ex) { NetLog.Warn("回主菜单尝试失败 " + names[n] + "/" + argTs[k].Length + ": " + ex.Message); }
                    }
                }

                // 兜底：直接用场景树切换到主菜单场景
                var tree = Engine.GetMainLoop() as SceneTree;
                if (tree != null)
                {
                    var err = tree.ChangeSceneToFile("res://Scene/MainMenu.tscn");
                    NetLog.Info("结束对局: 兜底 ChangeSceneToFile -> " + err);
                    return;
                }
                NetLog.Warn("回主菜单: 所有方式均失败（SceneManager 无可用切场景方法）");
            }
            catch (System.Exception ex) { NetLog.Warn("回主菜单异常: " + ex.Message); }
        }

        /// <summary>联机选关：按任意关卡 key 进关（注册表 saveKey / 每日关卡 / 在线关卡）。返回是否成功。</summary>
        public static bool EnterLevelByAnyKey(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return false;
                if (key.StartsWith("DailyLevel-")) return EnterOnlineLevelFile("user://DailyLevel/" + key + ".json");
                if (key.StartsWith("OnlineLevel-")) return EnterOnlineLevelFile("user://OnlineLevel/" + key + ".json");
                return EnterLevelBySaveKey(key);
            }
            catch { return false; }
        }

        /// <summary>从本地关卡 json 进关（每日/在线关卡）：构造 TowerDefenseLevelConfig → OnlineLevel 模式进对局。</summary>
        static bool EnterOnlineLevelFile(string path)
        {
            try
            {
                if (!Godot.FileAccess.FileExists(path)) { Bootstrap.Log("联机进关: 关卡 json 不存在 " + path); return false; }
                string txt = Godot.FileAccess.GetFileAsString(path);
                if (string.IsNullOrEmpty(txt)) return false;
                var j = new Godot.Json();
                if (j.Parse(txt) != Godot.Error.Ok) { Bootstrap.Log("联机进关: json 解析失败 " + path); return false; }
                var d = j.Data;
                if (d.VariantType == Godot.Variant.Type.Dictionary)
                {
                    var dd = d.AsGodotDictionary();
                    dd["Name"] = "OnlineLevel-File";
                    j.Data = dd;
                }
                var t = FindType("TowerDefenseLevelConfig");
                if (t == null) { Bootstrap.Log("联机进关: 找不到 TowerDefenseLevelConfig"); return false; }
                object cfg = System.Activator.CreateInstance(t);
                const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var dp = t.GetProperty("data", BF);
                if (dp != null && dp.CanWrite) dp.SetValue(cfg, j.Data);
                var init = t.GetMethod("Init", System.Type.EmptyTypes);
                if (init != null) init.Invoke(cfg, null);
                if (IsGameplayReady())
                {
                    EnterLevelWithConfig(cfg, "OnlineLevel");
                    Bootstrap.Log("联机进关(文件): " + path);
                }
                else
                {
                    _pendingEnterCfg = cfg;
                    _pendingEnterMode = "OnlineLevel";
                    _pendingLoadStarted = false;
                    Bootstrap.Log("联机进关(文件): 玩法资源未就绪，已排队 " + path);
                }
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("联机进关(文件)异常: " + ex.Message); return false; }
        }

        /// <summary>按 uid:// 加载关卡资源（先查 UID 缓存路径，失败再直接 load uid）。</summary>
        static object LoadLevelResourceByUid(string uid)
        {
            try
            {
                string path = Godot.ResourceUid.UidToPath(uid);
                if (string.IsNullOrEmpty(path)) path = Godot.ResourceUid.GetIdPath(Godot.ResourceUid.TextToId(uid));
                if (!string.IsNullOrEmpty(path))
                {
                    var r = Godot.GD.Load(path);
                    if (r != null) return r;
                }
            }
            catch { }
            try { return Godot.GD.Load(uid); } catch { return null; }
        }

        /// <summary>按 saveKey 在官方关卡注册表里查关卡资源 uid（Normal → Difficult → Ultimate）。</summary>
        static string FindLevelUidBySaveKey(string saveKey)
        {
            try
            {
                var fa = Godot.FileAccess.Open("res://Asset/Config/Level/LevelResource.json", Godot.FileAccess.ModeFlags.Read);
                if (fa == null) return "";
                string jsonText = fa.GetAsText();
                fa.Close();
                var json = new Godot.Json();
                if (json.Parse(jsonText, false) != Godot.Error.Ok) return "";
                return FindLevelUid(json.Data, saveKey);
            }
            catch { return ""; }
        }

        static string FindLevelUid(object data, string saveKey)
        {
            try
            {
                if (data is Godot.Variant vv)
                {
                    if (vv.VariantType == Variant.Type.Dictionary) return FindLevelUid(vv.AsGodotDictionary(), saveKey);
                    if (vv.VariantType == Variant.Type.Array) return FindLevelUid(vv.AsGodotArray(), saveKey);
                    return "";
                }
                if (data is Godot.Collections.Dictionary gd)
                {
                    try
                    {
                        if (gd.ContainsKey("SaveKey"))
                        {
                            var sk = gd["SaveKey"];
                            if (sk.VariantType == Variant.Type.String && sk.AsString() == saveKey && gd.ContainsKey("Level"))
                            {
                                var lv = gd["Level"];
                                if (lv.VariantType == Variant.Type.Dictionary)
                                {
                                    var lvd = lv.AsGodotDictionary();
                                    foreach (var k in new string[] { "Normal", "Difficult", "Ultimate" })
                                    {
                                        if (!lvd.ContainsKey(k)) continue;
                                        var uv = lvd[k];
                                        if (uv.VariantType == Variant.Type.String && !string.IsNullOrEmpty(uv.AsString())) return uv.AsString();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                    foreach (var k in gd.Keys)
                    {
                        var r = FindLevelUid(gd[k], saveKey);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                }
                else if (data is Godot.Collections.Array ga)
                {
                    for (int i = 0; i < ga.Count; i++)
                    {
                        var r = FindLevelUid(ga[i], saveKey);
                        if (!string.IsNullOrEmpty(r)) return r;
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>趣味：僵尸变色（彩虹循环）+ 跳舞（左右摇摆）。
        /// 用缓存列表（30 帧重找一次），每帧持续设置避免被游戏覆盖。</summary>
        static void ApplyZombieFun(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                if (++_zombieCacheTimer >= 30)
                {
                    _zombieCacheTimer = 0;
                    _zombieCache.Clear();
                    // 角色挂在 internal 节点（characterRegistry）下，必须从 tree.Root 遍历（和 ESP 一致）！
                    var tree = root.GetTree();
                    Node start = tree != null && tree.Root != null ? tree.Root : root;
                    WalkZombieFun(start);
                    if (++_zombieFunLogTimer >= 4) { _zombieFunLogTimer = 0; Bootstrap.Log("僵尸趣味: 缓存僵尸=" + _zombieCache.Count); }
                }
                // 降频到每 3 帧应用（视觉效果几乎无差，渲染状态切换减少 3 倍 → 防闪屏）
                if (++_zombieApplyTimer % 3 != 0) return;
                ulong frame = Engine.GetProcessFrames();
                for (int i = 0; i < _zombieCache.Count; i++)
                {
                    var n2 = _zombieCache[i];
                    if (n2 == null || !GodotObject.IsInstanceValid(n2)) continue;
                    if (ModSettings.ZombieColor)
                        n2.Modulate = Color.FromHsv((float)((frame * 0.01) % 1.0), 0.9f, 1f);
                    if (ModSettings.ZombieDance)
                        n2.Rotation = Mathf.Sin((float)((frame % 720) * 0.01745f * 3f)) * 0.25f;
                }
            }
            catch (System.Exception ex) { if (!_zombieFunLogged) { _zombieFunLogged = true; Bootstrap.Log("僵尸趣味异常: " + ex.Message); } }
        }

        static bool _zombieFunLogged;
        static bool _bjFollowLogged, _orbitDiagLogged;

        static void WalkZombieFun(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && ESP.GetCamp(n2) == "Zombie")
                        _zombieCache.Add(n2);
                }
                catch { }
                WalkZombieFun(child);
            }
        }

        // ================= 飞贼 / 小丑 组合技（手机版：每次全树遍历，降频 15 帧） =================

        static string _jackboxId;
        static bool _jackboxIdLogged;
        /// <summary>找小丑僵尸（Jackbox）卡 id，用于"飞贼偷取后生成小丑"。</summary>
        static string FindJackboxId()
        {
            if (_jackboxId != null) return _jackboxId;
            _jackboxId = "";
            try
            {
                var ids = GetPacketIds(false);
                foreach (var id in ids)
                {
                    if (id != null && id.IndexOf("Jackbox", System.StringComparison.OrdinalIgnoreCase) >= 0) { _jackboxId = id; return id; }
                }
            }
            catch { }
            if (_jackboxId.Length == 0 && !_jackboxIdLogged)
            {
                _jackboxIdLogged = true;
                Bootstrap.Log("小丑: 未找到 Jackbox 卡 id");
            }
            return _jackboxId;
        }

        static bool _bjBungiLogged, _bjJackLogged, _bjGrabLogged, _bjBombLogged;
        static bool _bungiAutoGrab;   // 全图生成的飞贼自动抓取标志
        static System.Collections.Generic.List<Vector2I> _bungiGridQueue = new();   // 待分散的格子队列
        static int _bungiDiagTimer;   // 飞贼诊断日志节流
        static int _bjAutoTimer;
        static int _bjTimer;
        static System.Reflection.MethodInfo _setLogPos;   // TowerDefenseCharacter.SetLogicalGlobalPosition（防移动组件覆盖）
        static System.Reflection.MethodInfo _getSprite;   // TowerDefenseCharacter.get_sprite()（模型精灵跟随）
        static System.Reflection.FieldInfo _shadowField;  // TowerDefenseCharacter.shadowSprite（阴影精灵，独立节点）
        /// <summary>飞贼/小丑组合技主入口（手机版）。飞贼秒偷/无视保护伞/偷后生成小丑、小丑秒炸（瞬移+立即炸）、全场僵尸吸附鼠标。</summary>
        static void ApplyBungiJackboxCheats(Node root)
        {
            try
            {
                bool fastGrab = ModSettings.BungiFastGrab;
                bool noUmbrella = ModSettings.BungiIgnoreUmbrella;
                bool fastBomb = ModSettings.JackboxFastBomb;
                bool followMouse = ModSettings.ZombiesFollowMouse;
                if (!_bjFollowLogged) { _bjFollowLogged = true; Bootstrap.Log("吸附: 僵尸缓存=" + _zombieCache.Count + " followMouse=" + followMouse); }
                bool spawnJack = ModSettings.BungiSpawnJackbox;
                if (!(fastGrab || noUmbrella || fastBomb || followMouse || spawnJack)) return;
                if (++_bjTimer % 15 != 0) return;   // 手机版每次全树遍历，降频
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                var zs = new System.Collections.Generic.List<Node2D>();
                var ps = new System.Collections.Generic.List<Node2D>();
                CollectZombies(start, zs);
                CollectPlants(start, ps);
                Vector2 mouse = default;
                if (followMouse && zs.Count > 0)
                {
                    try
                    {
                        mouse = zs[0].GetGlobalMousePosition();
                        // 限制在视口内（防僵尸被吸出屏幕掉下去/消失）
                        var vr = root.GetViewport().GetVisibleRect().Size;
                        if (vr.X > 0) mouse.X = Mathf.Clamp(mouse.X, 30f, vr.X - 30f);
                        if (vr.Y > 0) mouse.Y = Mathf.Clamp(mouse.Y, 30f, vr.Y - 30f);
                    }
                    catch { followMouse = false; }
                }
                for (int i = 0; i < zs.Count; i++)
                {
                    var n2 = zs[i];
                    if (n2 == null || !GodotObject.IsInstanceValid(n2)) continue;
                    var tName = n2.GetType().Name;
                    bool isBungi = tName.IndexOf("Bungi", StringComparison.Ordinal) >= 0;
                    bool isJackbox = tName.IndexOf("Jackbox", StringComparison.Ordinal) >= 0;
                    // 诊断：首次发现飞贼/小丑（确认类型识别 + 收集）
                    if (isBungi && !_bjBungiLogged) { _bjBungiLogged = true; Bootstrap.Log("飞贼小丑: 发现飞贼类型=" + tName + " 总僵尸=" + zs.Count); }
                    if (isJackbox && !_bjJackLogged) { _bjJackLogged = true; Bootstrap.Log("飞贼小丑: 发现小丑类型=" + tName + " 总僵尸=" + zs.Count); }
                    // 全场僵尸吸附到鼠标（秒炸的小丑除外，它要瞬移到目标面前）
                    if (followMouse && !(isJackbox && fastBomb))
                    {
                        // 原生逻辑位置（防移动组件拉回）+ 直接设全局位置（含 y，强制跨行吸附）
                        if (_setLogPos == null)
                            _setLogPos = n2.GetType().GetMethod("SetLogicalGlobalPosition", new Type[] { typeof(Vector2) });
                        var newPos = n2.GlobalPosition.Lerp(mouse, 0.35f);
                        if (_setLogPos != null) _setLogPos.Invoke(n2, new object[] { newPos });
                        n2.GlobalPosition = newPos;
                        // 模型精灵同步跟随（GroundMoveComponent 单独控制 sprite；ShouldZombieNoMove 已暂停它）
                        if (_getSprite == null)
                            _getSprite = n2.GetType().GetMethod("get_sprite", Type.EmptyTypes);
                        if (_getSprite != null)
                        {
                            var sp = _getSprite.Invoke(n2, null);
                            if (sp is Godot.Node2D sp2 && GodotObject.IsInstanceValid(sp2)) sp2.GlobalPosition = newPos;
                        }
                        // 阴影精灵跟随（TowerDefenseCharacter.shadowSprite 独立节点，不设会留在原行）
                        if (_shadowField == null)
                            _shadowField = n2.GetType().GetField("shadowSprite", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_shadowField != null)
                        {
                            var sh = _shadowField.GetValue(n2);
                            if (sh is Godot.Node2D sh2 && GodotObject.IsInstanceValid(sh2)) sh2.GlobalPosition = newPos;
                        }
                    }
                    if (isBungi && (fastGrab || noUmbrella || spawnJack))
                    {
                        if (noUmbrella) SetPropOrField(n2, "canBlock", false);
                        if (fastGrab || spawnJack) TryBungiGrab(n2, spawnJack, ps);
                    }
                    if (isJackbox && fastBomb) TryJackboxBomb(n2, zs, ps);
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("飞贼小丑异常: " + ex.Message); }
        }

        /// <summary>全图生成的飞贼自动抓取：分散到不同格子 + 给未抓取的 Bungi 设 waitGrab=true + waitTimer 拉满（立即 ToGrab 抓植物）。
        /// 没有待抓飞贼后自动关闭标志。</summary>
        static void ApplyBungiAutoGrab(Node root)
        {
            try
            {
                if (!_bungiAutoGrab) return;
                if (++_bjAutoTimer % 15 != 0) return;   // 手机版全树遍历开销大，降频
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                var zs = new System.Collections.Generic.List<Node2D>();
                CollectZombies(start, zs);
                bool any = false;
                for (int i = 0; i < zs.Count; i++)
                {
                    var z = zs[i];
                    if (z == null || !GodotObject.IsInstanceValid(z)) continue;
                    if (z.GetType().Name.IndexOf("Bungi", StringComparison.Ordinal) < 0) continue;
                    var hasPlant = FindFieldVal(z, "hasPlant");
                    if (hasPlant is bool hp && hp) continue;
                    // 分散到不同格子（僵尸默认从右侧入口生成会聚一起）
                    if (_bungiGridQueue.Count > 0)
                    {
                        var g = _bungiGridQueue[0];
                        _bungiGridQueue.RemoveAt(0);
                        SpreadBungiToGrid(z, g);
                    }
                    // 秒偷开关控制：开=立即抓（waitTimer 拉满跳 3 秒等待）；关=不干预（原生 Drop 完成后等 3 秒自然抓取）
                    if (ModSettings.BungiFastGrab)
                    {
                        SetPropOrField(z, "waitGrab", true);
                        SetPropOrField(z, "waitTimer", 3.0);
                    }
                    any = true;
                }
                if (!any && _bungiGridQueue.Count == 0) _bungiAutoGrab = false;
                // 诊断：每 120 帧打印所有在场飞贼状态（确认分散/下落/抓取/升空）
                if (_bungiAutoGrab && ++_bungiDiagTimer % 120 == 0)
                {
                    for (int i = 0; i < zs.Count; i++)
                    {
                        var z2 = zs[i];
                        if (z2 == null || !GodotObject.IsInstanceValid(z2)) continue;
                        if (z2.GetType().Name.IndexOf("Bungi", StringComparison.Ordinal) < 0) continue;
                        try
                        {
                            Bootstrap.Log("飞贼诊断: " + z2.Name + " grid=" + FindPropOrFieldVal(z2, "gridPos") +
                                " pos=" + z2.GlobalPosition + " z=" + FindPropOrFieldVal(z2, "z") +
                                " ground=" + FindPropOrFieldVal(z2, "isGround") + " wait=" + FindPropOrFieldVal(z2, "waitGrab") +
                                " has=" + FindPropOrFieldVal(z2, "hasPlant"));
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>把飞贼定位到指定格子：设 gridPos（→cell，决定抓取目标 + 下落高度），x/y 用格子坐标。
        /// 注意：飞贼高度由 z 属性管理（Drop 状态会 set_z(600) 从天而降），不要改 y。</summary>
        static void SpreadBungiToGrid(Node2D z, Vector2I grid)
        {
            try
            {
                // 设 gridPos → set_gridPos 更新 cell + 渲染层（抓取目标按此格）
                SetPropOrField(z, "gridPos", grid);
                if (_getMapCellPlantPos == null && _tdmType != null)
                    _getMapCellPlantPos = _tdmType.GetMethod("GetMapCellPlantPos", new Type[] { typeof(Vector2I) });
                if (_getMapCellPlantPos == null) return;
                var pos = (Vector2)_getMapCellPlantPos.Invoke(null, new object[] { grid });
                if (_setLogPos == null)
                    _setLogPos = z.GetType().GetMethod("SetLogicalGlobalPosition", new Type[] { typeof(Vector2) });
                if (_setLogPos != null) _setLogPos.Invoke(z, new object[] { pos });
                z.GlobalPosition = pos;
            }
            catch { }
        }

        /// <summary>飞贼偷取：让一个飞贼立即偷走最近植物并飞走。spawnJack=true 时偷完在飞贼位置生成快炸小丑。</summary>
        static void TryBungiGrab(Node2D bungi, bool spawnJack, System.Collections.Generic.List<Node2D> ps)
        {
            try
            {
                var hasPlant = FindFieldVal(bungi, "hasPlant");
                if (hasPlant is bool hp && hp) return;
                Node2D target = null;
                double best = double.MaxValue;
                for (int i = 0; i < ps.Count; i++)
                {
                    var p = ps[i];
                    if (p == null || !GodotObject.IsInstanceValid(p)) continue;
                    var tn = p.GetType().Name;
                    if (tn.IndexOf("Mower", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("Car", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // 排除小推车/车辆
                    double d = (p.GlobalPosition - bungi.GlobalPosition).LengthSquared();
                    if (d < best) { best = d; target = p; }
                }
                if (target == null) return;
                if (!_bjGrabLogged)
                {
                    _bjGrabLogged = true;
                    Bootstrap.Log("飞贼秒偷: 触发 " + bungi.GetType().Name + " 偷 " + target.GetType().Name + " 植物数=" + ps.Count);
                }
                // 不再瞬移到最近植物上方（多个飞贼瞬移同一目标会聚到一起）——飞贼抓取目标是自身 gridPos 格的植物，
                // 全图生成时 ApplyBungiAutoGrab 已把各飞贼分散到不同格子，各自抓自己格的植物即可。
                // 触发原生抓取：waitGrab=true + waitTimer 拉满 → Drop 完成后 IdleProcessing 立即 ToGrab → 抓取 → 飞走
                SetPropOrField(bungi, "waitGrab", true);
                SetPropOrField(bungi, "waitTimer", 3.0);
                if (spawnJack) SpawnFastJackboxAt(bungi);
            }
            catch { }
        }

        /// <summary>飞贼偷取后：在飞贼位置生成一个小丑（会立即被秒炸逻辑瞬移+爆炸）。</summary>
        static void SpawnFastJackboxAt(Node2D bungi)
        {
            try
            {
                string id = FindJackboxId();
                if (id.Length == 0) return;
                var grid = FindPropOrFieldVal(bungi, "gridPos");
                if (grid is Vector2I gp && gp.X >= 1)
                {
                    SpawnCharacter(id, gp);
                    Bootstrap.Log("飞贼小丑: 偷后在 " + gp + " 生成 " + id);
                }
            }
            catch { }
        }

        /// <summary>小丑秒炸：瞬移到目标面前立即爆炸。敌方小丑→最近植物；被魅惑（我方）→最近僵尸。</summary>
        static void TryJackboxBomb(Node2D jack, System.Collections.Generic.List<Node2D> zs, System.Collections.Generic.List<Node2D> ps)
        {
            try
            {
                var over = FindFieldVal(jack, "over");
                if (over is bool ov && ov) return;
                string camp = ESP.GetCamp(jack);
                bool friendly = camp != "Zombie";   // 被魅惑后 _camp 不再是 Zombie → 去炸僵尸
                var targets = friendly ? zs : ps;
                Vector2 jp = jack.GlobalPosition;
                Node2D target = null;
                double best = double.MaxValue;
                for (int i = 0; i < targets.Count; i++)
                {
                    var t = targets[i];
                    if (t == null || !GodotObject.IsInstanceValid(t) || t == (Node2D)jack) continue;
                    double d = (t.GlobalPosition - jp).LengthSquared();
                    if (d < best) { best = d; target = t; }
                }
                if (!_bjBombLogged)
                {
                    _bjBombLogged = true;
                    var ceM = jack.GetType().GetMethod("CreateEffect", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Bootstrap.Log("小丑秒炸: 触发 " + jack.GetType().Name + " camp=" + (camp ?? "null") + " 目标=" + (target != null ? target.GetType().Name : "无") + " CreateEffect=" + (ceM != null) + " 植物数=" + ps.Count + " 僵尸数=" + zs.Count);
                }
                if (target != null)
                {
                    // 瞬移到目标面前（贴脸，爆炸范围必中）
                    jack.GlobalPosition = target.GlobalPosition + new Vector2(-20f, 0f);
                }
                try
                {
                    var ce = jack.GetType().GetMethod("CreateEffect", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (ce != null) ce.Invoke(jack, null);
                }
                catch { }
                try { jack.QueueFree(); } catch { }
            }
            catch { }
        }

        /// <summary>飞贼偷取全场（一次性按钮）：让所有在场飞贼各偷走一个最近植物并飞走。</summary>
        public static void BungiGrabAllField()
        {
            int n = 0;
            try
            {
                var root = _lastRoot;
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                var zs = new System.Collections.Generic.List<Node2D>();
                var ps = new System.Collections.Generic.List<Node2D>();
                CollectZombies(start, zs);
                CollectPlants(start, ps);
                foreach (var z in zs)
                {
                    if (z == null || !GodotObject.IsInstanceValid(z)) continue;
                    if (z.GetType().Name.IndexOf("Bungi", StringComparison.Ordinal) >= 0)
                    {
                        TryBungiGrab(z, ModSettings.BungiSpawnJackbox, ps);
                        n++;
                    }
                }
                Bootstrap.Log("飞贼偷取全场: 触发 " + n + " 个飞贼");
            }
            catch (System.Exception ex) { Bootstrap.Log("飞贼偷全场异常: " + ex.Message); }
        }

        /// <summary>全图生成飞贼（一次性按钮）：全图刷 Bungi 飞贼（出现后自动抓植物飞走）。返回生成数。</summary>
        public static int SpawnBungiAllField()
        {
            int done = 0;
            try
            {
                var ids = GetPacketIds(false);
                string bungiId = null;
                for (int i = 0; i < ids.Count; i++)
                    if (ids[i] != null && ids[i].IndexOf("Bungi", StringComparison.OrdinalIgnoreCase) >= 0) { bungiId = ids[i]; break; }
                if (bungiId == null) { Bootstrap.Log("全图生成飞贼: 未找到飞贼 id"); return 0; }
                // 全图刷飞贼：行列数按当前地图动态获取（超大地图列/行更多，硬编码 8x5 会漏刷/错位）
                int cols = 8, rows = 5;
                try
                {
                    var tdm = GetTdmInstance();
                    if (tdm != null)
                    {
                        var m = tdm.GetType().GetMethod("GetMapGridNum", Type.EmptyTypes);
                        if (m != null && m.Invoke(tdm, null) is Vector2I gv && gv.X > 0 && gv.X <= 40 && gv.Y > 0 && gv.Y <= 40)
                        { cols = gv.X; rows = gv.Y; }
                    }
                }
                catch { }
                for (int x = 1; x <= cols; x++)
                    for (int y = 1; y <= rows; y++)
                    {
                        var grid = new Vector2I(x, y);
                        if (SpawnCharacter(bungiId, grid)) done++;
                    }
                if (done > 0)
                {
                    _bungiGridQueue.Clear();
                    for (int gx = 1; gx <= cols; gx++)
                        for (int gy = 1; gy <= rows; gy++)
                            _bungiGridQueue.Add(new Vector2I(gx, gy));
                    _bungiAutoGrab = true;   // 标记：让生成的飞贼自动抓取 + 分散
                }
                Bootstrap.Log("全图生成飞贼: " + bungiId + " x" + done + " (地图 " + cols + "x" + rows + ")");
            }
            catch (System.Exception ex) { Bootstrap.Log("全图生成飞贼异常: " + ex.Message); }
            return done;
        }

        /// <summary>清除弹坑：删除 "Crater" 组节点（TowerDefenseCrater 注册在该组）。</summary>
        static void ClearCraters(Node root)
        {
            try
            {
                var tree = root.GetTree();
                if (tree == null) return;
                foreach (var n in tree.GetNodesInGroup("Crater"))
                {
                    try { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>卡片无冷却：遍历场景树（含 internal），把 TowerDefenseInGamePacketShow 的冷却清除。</summary>
        static void ApplyNoCooldown(Node root)
        {
            int count = 0;
            var tree = root.GetTree();
            Node start = tree != null && tree.Root != null ? tree.Root : root;
            WalkSetCooldown(start, ref count);
            if (++_ncLogTimer >= 120)
            {
                _ncLogTimer = 0;
                Bootstrap.Log("无冷却扫描: 卡牌节点=" + count);
            }
        }

        static void WalkSetCooldown(Node node, ref int count)
        {
            foreach (var child in node.GetChildren(true))
            {
                if (TrySetCooldown(child)) count++;
                WalkSetCooldown(child, ref count);
            }
        }

        /// <summary>杀光当前所有僵尸（跳杀）：遍历 internal 节点找僵尸并移除。</summary>
        public static void KillAllZombies(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                // 刷出的僵尸在 tree.Root 的 internal 节点（characterRegistry）下，必须从场景树根遍历！
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                KillWalk(start);
                Bootstrap.Log("杀光僵尸: 已执行");
            }
            catch { }
        }

        static void KillWalk(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                {
                    var camp = ESP.GetCamp(n2);
                    if (camp == "Zombie")
                        n2.QueueFree();
                }
                KillWalk(child);
            }
        }

        static bool TrySetCooldown(object obj)
        {
            try
            {
                var t = obj.GetType();
                if (t == null) return false;
                // 卡牌冷却：TowerDefenseInGamePacketShow 用 coldDownOpen / coldDownTimer
                if (t.Name.Contains("PacketShow"))
                {
                    var p = t.GetProperty("coldDownOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.PropertyType == typeof(bool) && (bool)p.GetValue(obj))
                    {
                        // 注意：.NET 9 下 PropertyInfo.SetValue 会 MissingMethodException，必须用 setter Invoke
                        var setter = p.GetSetMethod(true);
                        if (setter != null) setter.Invoke(obj, new object[] { false });
                    }
                    var tf = t.GetField("coldDownTimer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (tf != null && tf.FieldType == typeof(double) && (double)tf.GetValue(obj) > 0.0)
                        tf.SetValue(obj, 0.0);
                    return true;
                }
                // 旧逻辑（部分配置/其他组件）
                var f = t.GetField("overridePacketCooldown", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(double))
                {
                    f.SetValue(obj, 0.0);
                    var fs = t.GetField("overrideStartingCooldown", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fs != null && fs.FieldType == typeof(double)) fs.SetValue(obj, 0.0);
                }
            }
            catch { }
            return false;
        }

        /// <summary>无限阳光：把 TowerDefenseManager 的阳光设为 9999。</summary>
        static void ApplySun()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return;
                if (_getSun == null) _getSun = tdm.GetType().GetMethod("GetSun", Type.EmptyTypes);
                if (_setSun == null) _setSun = tdm.GetType().GetMethod("SetSun", new Type[] { typeof(long) });
                if (_getSun == null || _setSun == null) return;
                long cur = (long)_getSun.Invoke(tdm, null);
                if (cur < 9999) _setSun.Invoke(tdm, new object[] { 9999L });
            }
            catch (System.Exception ex) { Bootstrap.Log("无限阳光异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>自由加减阳光：把当前阳光 +delta（可为负，最小 0）。返回新阳光数；失败返回 -1。</summary>
        public static long AddSun(long delta)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return -1;
                if (_getSun == null) _getSun = tdm.GetType().GetMethod("GetSun", Type.EmptyTypes);
                if (_setSun == null) _setSun = tdm.GetType().GetMethod("SetSun", new Type[] { typeof(long) });
                if (_getSun == null || _setSun == null) return -1;
                long cur = (long)_getSun.Invoke(tdm, null);
                long next = System.Math.Max(0, cur + delta);
                _setSun.Invoke(tdm, new object[] { next });
                return next;
            }
            catch (System.Exception ex) { Bootstrap.Log("加减阳光异常: " + ex.Message); return -1; }
        }

        /// <summary>读取当前阳光数（失败返回 -1）。</summary>
        public static long GetSunCount()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return -1;
                if (_getSun == null) _getSun = tdm.GetType().GetMethod("GetSun", Type.EmptyTypes);
                if (_getSun == null) return -1;
                return (long)_getSun.Invoke(tdm, null);
            }
            catch { return -1; }
        }

        /// <summary>通过 AppDomain 找主程序集里的 TowerDefenseManager 实例。</summary>
        static object GetTdmInstance()
        {
            try
            {
                if (_tdmType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "PlantsVsZombies")
                        {
                            _tdmType = asm.GetType("TowerDefenseManager");
                            if (_tdmType != null)
                            {
                                _instanceProp = _tdmType.GetProperty("Instance",
                                    BindingFlags.Public | BindingFlags.Static);
                                break;
                            }
                        }
                    }
                }
                if (_tdmType == null || _instanceProp == null) return null;
                return _instanceProp.GetValue(null);
            }
            catch { return null; }
        }

        /// <summary>无限金币：把场景里 CoinBank 的金币设为 10 亿（1000000000）。</summary>
        static void ApplyCoin(Node root)
        {
            try
            {
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;
                var found = tree.Root.FindChildren("*", "CoinBank", true, false);
                foreach (var n in found)
                {
                    if (!GodotObject.IsInstanceValid(n)) continue;
                    if (_coinSetNum == null)
                        _coinSetNum = n.GetType().GetMethod("SetNum", new Type[] { typeof(long) });
                    if (_coinSetNum != null)
                        _coinSetNum.Invoke(n, new object[] { 1000000000L });   // 10 亿
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("无限金币异常: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ===== 战斗增强：攻速 / 血量 / 无敌 =====

        static void ApplyCombatMods(Node root)
        {
            try
            {
                // 僵尸无法移动关闭时：清空位置锁定（让僵尸恢复自由移动）
                if (!ModSettings.ZombieNoMove && _zombieLockPos.Count > 0)
                    _zombieLockPos.Clear();
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;
                _seenZombiesThisPass.Clear();
                CombatWalk(tree.Root);
                // 清理已消失僵尸的锁定记录（防内存泄漏：僵尸死后字典条目永久保留）
                if (_zombieLockPos.Count > _seenZombiesThisPass.Count)
                {
                    foreach (var id in new System.Collections.Generic.List<ulong>(_zombieLockPos.Keys))
                        if (!_seenZombiesThisPass.Contains(id)) _zombieLockPos.Remove(id);
                }
            }
            catch { }
        }

        static readonly System.Collections.Generic.HashSet<ulong> _seenZombiesThisPass = new();

        static void CombatWalk(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                {
                    var camp = ESP.GetCamp(n2);
                    if (camp == "Zombie" || camp == "Plant")
                    {
                        bool zombie = camp == "Zombie";
                        float aspeed = zombie ? ModSettings.ZombieAttackSpeed : ModSettings.PlantAttackSpeed;
                        float hp = zombie ? ModSettings.ZombieHP : ModSettings.PlantHP;
                        bool inv = zombie ? ModSettings.ZombieInvincible : ModSettings.PlantInvincible;
                        // 植物射速：无条件调用（内部处理 mult=1 时恢复原值——关闭后必须还原，否则持久改过的 fireInterval 保持高射速）
                        if (!zombie)
                        {
                            ApplyAttackSpeed(n2, aspeed);
                            if (ModSettings.ChomperFastSwallow) ApplyChomperFastSwallow(n2);   // 大嘴花秒吞咽
                        }
                        // 僵尸攻速：由 AccelerateTimer（注入 AttackComponent.BatchUpdateValidated 开头）每帧直接加速 timer，
                        // ApplyZombieAttackSpeed 改 attackInterval 实测未生效且双重加速 → 移除（2026-08-23）
                        if (hp != 1.0f || inv) ApplyHP(n2, hp, inv);
                        // 僵尸无法移动：每帧锁回初始位置（不依赖注入，0.26 也可靠）
                        if (zombie && ModSettings.ZombieNoMove)
                        {
                            _seenZombiesThisPass.Add(n2.GetInstanceId());
                            ApplyZombieNoMovePos(n2);
                        }
                        // 魅惑已改为点按式（ModUI 按钮触发 CharmAll/UncharmAll），不在此每帧处理——避免与游戏机制打架导致发抖/抽搐
                    }
                }
                CombatWalk(child);
            }
        }

        /// <summary>僵尸无法移动：每帧把僵尸位置锁回初始（游戏用 GlobalPosition 移动，本地 Position 锁定无效 → 瞬移根源）。
        /// 首次见到记录全局坐标，之后每帧强制恢复 + 停走路动画；开关关闭时清空锁定。</summary>
        static System.Collections.Generic.Dictionary<ulong, Vector2> _zombieLockPos = new();
        static void ApplyZombieNoMovePos(Node2D z)
        {
            try
            {
                ulong id = z.GetInstanceId();
                if (!_zombieLockPos.TryGetValue(id, out Vector2 lockPos))
                {
                    _zombieLockPos[id] = z.GlobalPosition;
                    return;
                }
                if (z.GlobalPosition != lockPos)
                    z.GlobalPosition = lockPos;
                PauseSpriteAnim(z);   // 停走路动画（视觉也完全静止）
            }
            catch { }
        }

        // ===== 无法攻击 / 无法移动（由 patcher 注入，开关开时游戏方法直接 return） =====
        /// <summary>植物无法攻击开关（patcher 注入 FireComponent.IdleProcessing 开头调用，true 时 return）。</summary>
        public static bool ShouldPlantNoAttack() { return ModSettings.PlantNoAttack; }
        /// <summary>僵尸无法攻击开关（patcher 注入 AttackComponent.TryCommitContactTarget 开头调用，true 时 return false）。
        /// 僵尸攻击通过接触提交目标触发（不走 IdleProcessing），拦这里才能阻止僵尸咬人。
        /// 带 AttackComponent 实例，判断父角色是僵尸才拦（近战植物也用 AttackComponent）。</summary>
        public static bool ShouldZombieNoAttack(GodotObject comp)
        {
            bool on = ModSettings.ZombieNoAttack;
            try
            {
                if (comp == null) { if (++_zNoAtkTimer <= 30) Bootstrap.Log("僵尸攻击: comp=null 开关=" + on); return false; }
                var p = FindFieldVal(comp, "parent");
                if (p == null)
                {
                    if (++_zNoAtkTimer <= 30) Bootstrap.Log("僵尸攻击: 找不到parent comp=" + comp.GetType().Name + " 开关=" + on);
                    return false;
                }
                bool zombie = ESP.GetCamp((Node2D)p) == "Zombie";
                // ★ 诊断日志只在开关真的开着时打：以前无条件每 180 次调用打一条，
                //   十几个僵尸就是每秒好几条 → 手机 logcat / 日志文件被刷爆。
                if (on && (++_zNoAtkTimer <= 30 || _zNoAtkTimer % 600 == 0))
                    Bootstrap.Log("僵尸攻击: 开关=" + on + " comp=" + comp.GetType().Name + " 父=" + p.GetType().Name + " zombie=" + zombie);
                return on && zombie;
            }
            catch (Exception ex) { if (++_zNoAtkTimer <= 30) Bootstrap.Log("僵尸攻击异常: " + ex.Message + " 开关=" + on); return false; }
        }
        static int _zNoAtkTimer;
        /// <summary>僵尸无法移动开关（patcher 注入 GroundMoveComponent.BatchUpdate / TowerDefenseZombieBalloon.FlyProcessing 开头调用，true 时 return）。
        /// comp 可能是 GroundMoveComponent（有 parent 字段）或僵尸节点本身（气球 FlyProcessing 的 this），两者都判断父/自身是僵尸才拦。</summary>
        public static bool ShouldZombieNoMove(GodotObject comp)
        {
            bool on = ModSettings.ZombieNoMove || ModSettings.ZombiesFollowMouse;
            try
            {
                if (comp == null) { if (++_zNoMoveTimer <= 30) Bootstrap.Log("僵尸移动: comp=null 开关=" + on); return false; }
                var p = FindFieldVal(comp, "parent");
                if (p == null) p = comp; // 僵尸节点本身（TowerDefenseZombie 无 parent 字段，回落自身）
                if (p == null) { if (++_zNoMoveTimer <= 30) Bootstrap.Log("僵尸移动: 无可判断节点 开关=" + on); return false; }
                // ★ 对战标靶必须站住不动（用户要求：掉没掉都得停住不动）。
                //   放在开关判定之前：联机时 CheatPolicy 会强制关掉所有修改器开关。
                if (NetPvp.Active && NetPvp.IsTargetZombie(p)) return true;
                bool zombie = ESP.GetCamp((Node2D)p) == "Zombie";
                // ★ 诊断日志只在开关真的开着时打（详见僵尸攻击处说明）
                if (on && (++_zNoMoveTimer <= 30 || _zNoMoveTimer % 600 == 0))
                    Bootstrap.Log("僵尸移动: 开关=" + on + " comp=" + comp.GetType().Name + " parent=" + (p != null ? p.GetType().Name : "null") + " zombie=" + zombie);
                return on && zombie;
            }
            catch (Exception ex)
            {
                if (++_zNoMoveTimer <= 30) Bootstrap.Log("僵尸移动异常: " + ex.GetType().Name + ": " + ex.Message + " 开关=" + on);
                return false;
            }
        }
        static int _zNoMoveTimer;

        // ===== 禁止攻击显示修复：暂停攻击中的角色动画（不实际攻击但动画还播 → 视觉静止） =====
        static readonly System.Collections.Generic.List<Node2D> _animLockList = new();
        static int _animLockScanTimer;
        static void ApplyNoAttackAnimLock(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                if (++_animLockScanTimer >= 60)
                {
                    _animLockScanTimer = 0;
                    _animLockList.Clear();
                    var tree = root.GetTree();
                    Node start = tree != null && tree.Root != null ? tree.Root : root;
                    WalkAnimLock(start);
                }
                for (int i = _animLockList.Count - 1; i >= 0; i--)
                {
                    var n = _animLockList[i];
                    if (n == null || !GodotObject.IsInstanceValid(n)) { _animLockList.RemoveAt(i); continue; }
                    PauseSpriteAnim(n);
                }
            }
            catch { }
        }

        static void WalkAnimLock(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                    {
                        try
                        {
                            string camp = ESP.GetCamp(n2);
                            if (camp == "Zombie" || camp == "Plant") _animLockList.Add(n2);
                        }
                        catch { }
                    }
                }
                catch { }
                WalkAnimLock(child);
            }
        }

        /// <summary>暂停角色的 sprite 动画（反射找 sprite 字段，AdobeAnimateSprite 有 pause）。</summary>
        static void PauseSpriteAnim(Node2D node)
        {
            try
            {
                var spr = FindFieldVal(node, "sprite");
                if (spr == null) return;
                var so = spr as GodotObject;
                if (so == null || !GodotObject.IsInstanceValid(so)) return;
                var f = spr.GetType().GetField("pause", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(bool))
                {
                    if (!(bool)f.GetValue(spr)) f.SetValue(spr, true);
                }
            }
            catch { }
        }

        /// <summary>自动开火（全图锁敌）：让所有植物自动朝全图僵尸开火。
        /// 方法：放宽每棵植物的 FireComponent 检测配置（射程全图 SetCheckLength + checkAllLine + checkHeight=false + 冷却清零），
        /// 让游戏原生 IdleProcessing 检测并发射；同时**有僵尸时每 30 帧直接调 Fire() 强制开火**（兜底，不依赖检测）。</summary>
        static System.Collections.Generic.HashSet<ulong> _fireConfigured = new();
        static int _fireCfgTimer;
        static bool _fireDiagNoZombie, _fireDiagFired, _fireDiagErr;
        static void ApplyPlantAutoFire(Node root)
        {
            if (!ModSettings.PlantAutoFire) return;
            try
            {
                var tree = root.GetTree();
                if (tree == null || tree.Root == null) return;
                if (_fireCompType == null) _fireCompType = FindType("FireComponent");
                if (_fireCompType == null) return;
                // 收集场上是否有僵尸（有才强制开火，避免空场刷子弹）
                bool hasZombie = false;
                var zombies = new System.Collections.Generic.List<Node2D>();
                CollectZombies(tree.Root, zombies);
                hasZombie = zombies.Count > 0;
                // 每 30 秒清空缓存，让新出现的植物重新配置射程
                if (++_fireCfgTimer >= 1800) { _fireCfgTimer = 0; _fireConfigured.Clear(); }
                WalkPlantFire(tree.Root, hasZombie);
            }
            catch { }
        }

        static readonly System.Collections.Generic.Dictionary<Type, System.Reflection.PropertyInfo> _fireNumProps = new();
        static readonly System.Collections.Generic.Dictionary<Type, System.Reflection.FieldInfo> _fireNumFields = new();

        /// <summary>收集场上所有植物（camp == Plant）。</summary>
        static void CollectPlants(Node node, System.Collections.Generic.List<Node2D> list)
        {
            try
            {
                foreach (var child in node.GetChildren(true))
                {
                    if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                    {
                        try { if (ESP.GetCamp(n2) == "Plant") list.Add(n2); } catch { }
                    }
                    CollectPlants(child, list);
                }
            }
            catch { }
        }

        /// <summary>炮类一次发射多枚：把场上所有发射植物的 fireNum（发射数量）设为用户配置值。
        /// 覆盖玉米投手/卷心菜/西瓜/豌豆/喷菇/仙人掌等（原生支持 fireNum 的植物），反射缓存避免每帧开销。</summary>
        static void ApplyCannonMultiShot(Node root)
        {
            try
            {
                var list = new System.Collections.Generic.List<Node2D>();
                CollectPlants(root, list);
                int want = ModSettings.CannonMultiShot;
                foreach (var n2 in list)
                {
                    if (n2 == null || !GodotObject.IsInstanceValid(n2)) continue;
                    var t = n2.GetType();
                    if (!_fireNumProps.TryGetValue(t, out var pi))
                    {
                        pi = t.GetProperty("fireNum", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        _fireNumProps[t] = pi;
                        if (pi == null)
                        {
                            var fi = t.GetField("fireNum", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            _fireNumFields[t] = fi;
                        }
                    }
                    if (pi != null)
                    {
                        var cur = pi.GetValue(n2);
                        if (cur is int ci && ci != want)
                        {
                            // .NET 9/Godot AOT 下 PropertyInfo.SetValue 抛 MissingMethodException，必须用 setter Invoke
                            var st = pi.GetSetMethod(true);
                            if (st != null) st.Invoke(n2, new object[] { want });
                            else pi.SetValue(n2, want);
                        }
                    }
                    else if (_fireNumFields.TryGetValue(t, out var fi) && fi != null)
                    {
                        var cur = fi.GetValue(n2);
                        if (cur is int ci && ci != want) fi.SetValue(n2, want);
                    }
                }
            }
            catch { }
        }

        static readonly System.Collections.Generic.Dictionary<Type, System.Collections.Generic.List<System.Reflection.PropertyInfo>> _rangeProps = new();

        /// <summary>全植物全图射程：把场上植物的射程属性（名含 Range/range）设为超大值（9999）。</summary>
        static void ApplyPlantFullRange(Node root)
        {
            try
            {
                var list = new System.Collections.Generic.List<Node2D>();
                CollectPlants(root, list);
                foreach (var n2 in list)
                {
                    if (n2 == null || !GodotObject.IsInstanceValid(n2)) continue;
                    var t = n2.GetType();
                    if (!_rangeProps.TryGetValue(t, out var props))
                    {
                        props = new System.Collections.Generic.List<System.Reflection.PropertyInfo>();
                        foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (p.GetMethod == null || p.SetMethod == null) continue;
                            if (!p.Name.Contains("Range") && !p.Name.Contains("range")) continue;
                            if (p.PropertyType == typeof(float) || p.PropertyType == typeof(double) || p.PropertyType == typeof(int))
                                props.Add(p);
                        }
                        _rangeProps[t] = props;
                    }
                    foreach (var p in props)
                    {
                        try
                        {
                            // .NET 9/Godot AOT 下 PropertyInfo.SetValue 抛 MissingMethodException，必须用 setter Invoke
                            var st = p.GetSetMethod(true);
                            if (st == null) continue;
                            if (p.PropertyType == typeof(float)) st.Invoke(n2, new object[] { 9999f });
                            else if (p.PropertyType == typeof(double)) st.Invoke(n2, new object[] { 9999.0 });
                            else if (p.PropertyType == typeof(int)) st.Invoke(n2, new object[] { 9999 });
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>刷全场：所有格子（1-8 列 × 1-5 行）刷满指定类型。type: plant/zombie/prop/grave。</summary>
        public static int SpawnAllField(string type)
        {
            int done = 0;
            try
            {
                System.Collections.Generic.List<string> ids = null;
                if (type == "plant") ids = GetPacketIds(true);
                else if (type == "zombie") ids = GetPacketIds(false);
                else if (type == "grave")
                {
                    ids = new System.Collections.Generic.List<string>();
                    var all = GetPacketIds(true);
                    for (int i = 0; i < all.Count; i++)
                        if (all[i] != null && all[i].IndexOf("Grave", StringComparison.OrdinalIgnoreCase) >= 0) ids.Add(all[i]);
                    if (ids.Count == 0) { Bootstrap.Log("刷墓碑: 未找到墓碑 id，改用僵尸"); ids = GetPacketIds(false); }
                }
                else { ids = GetPacketIds(true); }   // prop：刷掉落道具卡
                if (ids == null || ids.Count == 0) { Bootstrap.Log("刷全场" + type + ": 无可用 id"); return 0; }
                for (int x = 1; x <= 8; x++)
                {
                    for (int y = 1; y <= 5; y++)
                    {
                        string id = ids[_bulletRand.Next(ids.Count)];
                        var grid = new Vector2I(x, y);
                        bool ok = (type == "prop") ? SpawnPacketToScene(id, grid) : SpawnCharacter(id, grid);
                        if (ok) done++;
                    }
                }
                Bootstrap.Log("刷全场" + type + ": " + done + " 个");
            }
            catch (System.Exception ex) { Bootstrap.Log("刷全场" + type + " 异常: " + ex.Message); }
            return done;
        }

        /// <summary>刷全场自选卡：用指定卡 id 刷满全场（1-8 列 × 1-5 行）。isZombie=true 刷僵尸（SpawnCharacter），false 刷植物（SpawnPacketToScene）。</summary>
        public static int SpawnAllFieldWith(string id, bool isZombie)
        {
            int done = 0;
            try
            {
                if (string.IsNullOrEmpty(id)) return 0;
                for (int x = 1; x <= 8; x++)
                {
                    for (int y = 1; y <= 5; y++)
                    {
                        var grid = new Vector2I(x, y);
                        bool ok = isZombie ? SpawnCharacter(id, grid) : SpawnPacketToScene(id, grid);
                        if (ok) done++;
                    }
                }
                Bootstrap.Log("刷全场自选: " + id + (isZombie ? " 僵尸" : "") + " x" + done);
            }
            catch (System.Exception ex) { Bootstrap.Log("刷全场自选异常: " + ex.Message); }
            return done;
        }

        static void CollectZombies(Node node, System.Collections.Generic.List<Node2D> list)
        {
            try
            {
                foreach (var child in node.GetChildren(true))
                {
                    if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                    {
                        try
                        {
                            if (ESP.GetCamp(n2) == "Zombie") list.Add(n2);
                        }
                        catch { }
                    }
                    CollectZombies(child, list);
                }
            }
            catch { }
        }

        static void WalkPlantFire(Node node, bool hasZombie)
        {
            foreach (var child in node.GetChildren(true))
            {
                if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                {
                    try
                    {
                        if (ESP.GetCamp(n2) == "Plant") EnablePlantFullFire(n2, hasZombie);
                    }
                    catch { }
                }
                WalkPlantFire(child, hasZombie);
            }
        }

        /// <summary>放宽单棵植物的射击组件（FireComponent）+ 有僵尸时每 30 帧强制开火。</summary>
        static bool _fireDiagLogged;
        static void EnablePlantFullFire(Node2D plant, bool hasZombie)
        {
            try
            {
                var cm = FindFieldVal(plant, "componentManager");
                object comp = null;
                if (cm != null) comp = FindAttackComponent(cm, _fireCompType);
                if (comp == null) comp = FindFieldVal(plant, "fireComponent");
                if (comp == null)
                {
                    // 诊断：一次性打印为什么找不到 FireComponent（定位自动开火失效）
                    if (!_fireDiagLogged)
                    {
                        _fireDiagLogged = true;
                        var sb = new System.Text.StringBuilder();
                        try
                        {
                            var dict = FindFieldVal(cm, "_runtimeByInstanceId");
                            if (dict is System.Collections.IDictionary idict)
                            {
                                foreach (System.Collections.DictionaryEntry e in idict)
                                    if (e.Value != null && sb.Length < 250) sb.Append(e.Value.GetType().Name).Append(" ");
                            }
                        }
                        catch { }
                        Bootstrap.Log("全图开火: 无FireComponent 植物=" + plant.GetType().Name + " cm=" + (cm != null) + " 组件=[" + sb + "]");
                    }
                    return;   // 近战/无射击组件植物跳过
                }
                var ct = comp.GetType();
                // 无视行限制（checkAllLine = true）
                var clf = ct.GetField("checkAllLine", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (clf != null && clf.FieldType == typeof(bool) && !(bool)clf.GetValue(comp)) clf.SetValue(comp, true);
                // 无视高度限制（checkHeight = false，对空/对地都能打）
                var chf = ct.GetField("checkHeight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (chf != null && chf.FieldType == typeof(bool) && (bool)chf.GetValue(comp)) chf.SetValue(comp, false);
                // 冷却清零（timer = 0，让 IdleProcessing 每帧都能检测）
                var tf = ct.GetField("timer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (tf != null && tf.FieldType == typeof(float))
                {
                    float v = (float)tf.GetValue(comp);
                    if (v > 0f) tf.SetValue(comp, 0f);
                }
                // 新出现的植物：射程拉满全图（SetCheckLength，一次即可）
                ulong id = plant.GetInstanceId();
                if (_fireConfigured.Add(id))
                {
                    var sl = ct.GetMethod("SetCheckLength", new Type[] { typeof(float) });
                    if (sl != null) sl.Invoke(comp, new object[] { 2000f });   // 200→2000 真·全图（地图宽远超200）
                    Bootstrap.Log("全图开火已配置: " + plant.GetType().Name);
                }
                // 强制开火（兜底）：有僵尸时直接调 Fire() 生成子弹，不依赖游戏检测（保证"开了就开火"）
                if (hasZombie)
                {
                    try
                    {
                        var fen = ct.GetField("fireEventName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        string fe = fen != null && fen.FieldType == typeof(string) ? (string)fen.GetValue(comp) : null;
                        if (string.IsNullOrEmpty(fe)) fe = "fire";
                        var curFire = ct.GetField("currentFireEvent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (curFire != null && curFire.FieldType == typeof(string) && (string)curFire.GetValue(comp) != fe)
                            curFire.SetValue(comp, fe);
                        var fire = ct.GetMethod("Fire", Type.EmptyTypes);
                        if (fire != null) fire.Invoke(comp, null);
                        if (!_fireDiagFired) { _fireDiagFired = true; Bootstrap.Log("全图开火: 强制Fire成功 " + plant.GetType().Name); }
                    }
                    catch (Exception ex)
                    {
                        if (!_fireDiagErr) { _fireDiagErr = true; Bootstrap.Log("全图开火: Fire异常 " + plant.GetType().Name + " " + ex.Message); }
                    }
                }
                else if (!_fireDiagNoZombie)
                {
                    _fireDiagNoZombie = true;
                    Bootstrap.Log("全图开火: 场上无僵尸(CollectZombies=0) 植物=" + plant.GetType().Name + " — 有僵尸在场才会强制开火");
                }
            }
            catch { }
        }

        /// <summary>传送带加速送卡：找传送带功能，把 config.interval 缩短（更快送卡）。
        /// 开=压缩到 0.3，关=恢复记录的原值（config 是新关卡共享对象，否则关闭后仍生效）。
        /// 传送带 Process 每帧 timer+=delta，达到 interval 就 Spawn 一张卡。</summary>
        static void ApplyConveyorFast(Node root)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                var gm = _tdmType.GetMethod("GetConveyorBeltFeature", Type.EmptyTypes);
                var feat = gm != null ? gm.Invoke(tdm, null) : null;
                if (feat == null) return;
                var cfg = FindFieldVal(feat, "config");
                if (cfg == null) return;
                var t = cfg.GetType();
                var f = t.GetField("interval", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return;
                // config 对象变化（新关卡）→ 重置原值记录
                if (!ReferenceEquals(_convCfgRef, cfg)) { _convCfgRef = cfg; _convOrigSet = false; }
                if (ModSettings.ConveyorFast)
                {
                    if (!_convOrigSet) { _convOrigVal = f.GetValue(cfg); _convOrigSet = true; }
                    if (f.FieldType == typeof(double))
                    {
                        double cur = (double)f.GetValue(cfg);
                        if (cur > 0.3) f.SetValue(cfg, 0.3);   // 送卡间隔缩到 0.3 秒
                    }
                    else if (f.FieldType == typeof(float))
                    {
                        float cur = (float)f.GetValue(cfg);
                        if (cur > 0.3f) f.SetValue(cfg, 0.3f);
                    }
                    if (!_conveyorLogged) { _conveyorLogged = true; Bootstrap.Log("传送带加速: interval=" + f.GetValue(cfg)); }
                }
                else if (_convOrigSet)
                {
                    f.SetValue(cfg, _convOrigVal);   // 恢复原始间隔
                    _convOrigSet = false;
                    Bootstrap.Log("传送带加速已关闭，恢复 interval=" + _convOrigVal);
                }
            }
            catch { }
        }

        /// <summary>自动卡槽随机 / 篡改：进关卡后把底部种子卡槽（seed bank）里的卡替换成目标。
        /// 两种模式：
        /// ① 篡改模式（TrickEnabled 且目标卡池非空）：卡槽里凡是"不在目标池"的卡都替换成目标池卡
        ///    （每帧尝试，已符合目标的卡不再重复刷 → 不闪不卡）。传送带送卡 / 种子雨掉卡 / 老虎机掉卡
        ///    进入卡槽后也会立即变成目标卡。目标池可含僵尸卡（点了种出僵尸）。
        /// ② 纯随机模式（无目标池）：整槽随机成植物/僵尸（SeedBankRandomZombie）。
        ///    SeedBankEveryFrame=true → 1 帧 1 刷（极速闪换）；false → 约每秒 6 次。</summary>
        static void AutoRandomizeSeedBank(Node root)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                object sb = null;
                try
                {
                    var gm = _tdmType.GetMethod("GetSeedBankFeature", Type.EmptyTypes);
                    var feat = gm != null ? gm.Invoke(tdm, null) : null;
                    if (feat != null) sb = FindFieldVal(feat, "seedBank");
                    if (sb == null)
                    {
                        var gm2 = _tdmType.GetMethod("GetSeedBank", Type.EmptyTypes);
                        sb = gm2 != null ? gm2.Invoke(tdm, null) : null;
                    }
                }
                catch { }
                if (sb == null) { _sbDone = false; return; }
                var listObj = FindFieldVal(sb, "packetList");
                if (!(listObj is System.Collections.IEnumerable en)) { _sbDone = false; return; }
                int count = 0;
                foreach (var _ in en) count++;
                if (count == 0) { _sbDone = false; _lastSbCount = 0; return; }
                if (count > _lastSbCount) { _lastSbCount = count; _sbDone = false; }   // 新卡出现 → 重新处理
                // 篡改模式（有目标池）优先：用用户指定的植物/僵尸池；否则纯随机（植物或僵尸全池）
                var target = TrickPoolIds();
                bool useTrick = target.Count > 0;
                if (!useTrick)
                {
                    // 纯随机模式节流：成功后按节奏重刷（SeedBankEveryFrame=true → 1 帧 1 刷）
                    if (_sbDone)
                    {
                        int gap = ModSettings.SeedBankEveryFrame ? 1 : 10;
                        if (++_sbRetryTimer < gap) return;
                        _sbDone = false;
                        _sbRetryTimer = 0;
                    }
                    target = ModSettings.SeedBankRandomZombie ? GetPacketIds(false) : GetPacketIds(true);
                    if (target.Count == 0) { Bootstrap.Log("卡槽随机: 无卡池（" + (ModSettings.SeedBankRandomZombie ? "僵尸" : "植物") + "）"); return; }
                }
                var rnd = new Random();
                int changed = 0;
                int failed = 0;
                foreach (var packetShow in en)
                {
                    if (packetShow == null || !GodotObject.IsInstanceValid((GodotObject)packetShow)) { failed++; continue; }
                    // 跳过正在选中/使用的卡（select=true，玩家正在拖/拿的卡不刷新 → 种下的是卡上显示的那张）
                    try
                    {
                        var sel = FindPropOrFieldVal(packetShow, "select");
                        if (sel is bool sl && sl) continue;
                    }
                    catch { }
                    // 篡改模式：已符合目标池的卡不重复刷（避免每帧闪）
                    if (useTrick)
                    {
                        var ck = FindPropOrFieldVal(packetShow, "config");
                        var curKey = ck != null ? FindPropOrFieldVal(ck, "saveKey") as string : null;
                        if (curKey != null && target.Contains(curKey)) continue;
                    }
                    // 抽一张目标卡：固定模式=第 1 张；随机模式=池内随机
                    string id = (ModSettings.TrickFixedOnly && useTrick) ? target[0] : target[rnd.Next(target.Count)];
                    var cfg = GetConfig(id);
                    if (cfg == null) { failed++; continue; }
                    object dup = null;
                    try
                    {
                        var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                        if (dm != null) dup = dm.Invoke(cfg, new object[] { true });
                    }
                    catch { }
                    if (dup == null) dup = cfg;
                    var init = FindMethodByPrefix(packetShow.GetType(), "Init", new Type[] { dup.GetType(), typeof(bool) });
                    if (init == null) init = FindMethodByPrefix(packetShow.GetType(), "Init", new Type[] { dup.GetType() });
                    if (init == null) { failed++; continue; }
                    init.Invoke(packetShow, FillArgs(init, new object[] { dup, false }));
                    // 强制刷新显示：重建卡牌贴图 + 刷新预览（Init 的 CreateSprite 可能因 preview 未就绪跳过）
                    var rp = FindMethodByPrefix(packetShow.GetType(), "RefreshPreview", Type.EmptyTypes);
                    if (rp != null) { try { rp.Invoke(packetShow, null); } catch { } }
                    var ub = FindMethodByPrefix(packetShow.GetType(), "UpdateBackgroundTexture", Type.EmptyTypes);
                    if (ub != null) { try { ub.Invoke(packetShow, null); } catch { } }
                    // 验证：读回 config.saveKey 确认随机生效
                    var cfg2 = FindPropOrFieldVal(packetShow, "config");
                    var key = cfg2 != null ? FindPropOrFieldVal(cfg2, "saveKey") as string : null;
                    if (key == id) changed++;
                    else { failed++; Bootstrap.Log("卡槽随机: 卡未生效 期望=" + id + " 实际=" + key); }
                }
                if (changed == 0 && !useTrick) { _sbDone = false; _lastSbCount = 0; Bootstrap.Log("卡槽随机: 全部失败，稍后重试 (失败 " + failed + ")"); }
                else { _sbDone = true; _sbRetryTimer = 0; if (changed > 0) Bootstrap.Log("卡槽随机: 成功 " + changed + " 失败 " + failed + (useTrick ? " 张(目标池)" : " 张")); }
            }
            catch (System.Exception ex) { Bootstrap.Log("卡槽随机异常: " + ex.Message); }
        }

        // ===== 刷出物篡改引擎：锁定 老虎机(盲盒) / 种子雨 的自动刷出内容为目标卡池 =====
        // 另：传送带卡槽由 AutoTrickConveyorSlots 每帧覆盖；普通选卡卡槽由 AutoRandomizeSeedBank（篡改模式）覆盖。
        // 原理：feature 各自持有 .NET List 卡池，游戏按权重随机抽卡产出；我们把池清空后塞入"目标池（各 weight=1）"。
        // feature 只在关卡初始化时读 levelData 填池，这里每 15 帧重复锁定，关卡切换后也会再次锁定。

        static int _trickTimer;
        static int _trickCvTimer;
        static readonly System.Collections.Generic.Dictionary<string, object> _trickFeatRef = new();
        static readonly System.Collections.Generic.Dictionary<string, string> _trickApplied = new();

        /// <summary>解析目标卡池（逗号/空格/分号/竖线/换行/中文标点分隔，去空去重）。空=未设置。</summary>
        static System.Collections.Generic.List<string> TrickPoolIds()
        {
            var r = new System.Collections.Generic.List<string>();
            string raw = ModSettings.TrickPool ?? "";
            foreach (var part in raw.Split(new[] { ',', ' ', ';', '|', '\r', '\n', '\t', '，', '、' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (s.Length > 0 && !r.Contains(s)) r.Add(s);
            }
            return r;
        }

        // 篡改开关语义：总开关 = 全开（否则用户只开总开关会“不生效”）
        static bool TrickBoxOn => ModSettings.TrickEnabled || ModSettings.TrickBox;
        static bool TrickRainOn => ModSettings.TrickEnabled || ModSettings.TrickRain;
        static bool TrickConveyorOn => ModSettings.TrickEnabled || ModSettings.TrickConveyor;
        static bool TrickAnyOn => ModSettings.TrickEnabled || ModSettings.TrickBox || ModSettings.TrickRain || ModSettings.TrickConveyor;

        // 锁池前的原始池快照（键=featKey）——用于重建失败回滚、关闭篡改时还原
        static readonly System.Collections.Generic.Dictionary<string, object> _trickPoolFeat = new();
        static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<object>> _trickPoolBackup = new();
        static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, object>>> _trickLookupBackup = new();

        /// <summary>还原某机制被篡改前的原始池（关闭篡改 / 重建失败时调用）。</summary>
        static void RestoreTrickPool(string featKey)
        {
            try
            {
                if (!_trickPoolFeat.TryGetValue(featKey, out var feat) || feat == null) return;
                bool slot = featKey == "SlotMachine";
                var fld = feat.GetType().GetField(slot ? "_slotItems" : "_packetList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fld != null && fld.GetValue(feat) is System.Collections.IList il)
                {
                    il.Clear();
                    if (_trickPoolBackup.TryGetValue(featKey, out var items))
                        foreach (var it in items) { try { il.Add(it); } catch { } }
                }
                if (slot)
                {
                    var lk = FindFieldVal(feat, "_slotItemLookup");
                    if (lk != null)
                    {
                        var cl = lk.GetType().GetMethod("Clear");
                        if (cl != null) { try { cl.Invoke(lk, null); } catch { } }
                        var ad = lk.GetType().GetMethod("Add");
                        if (ad != null && _trickLookupBackup.TryGetValue(featKey, out var kvs))
                            foreach (var kv in kvs) { try { ad.Invoke(lk, new object[] { kv.Key, kv.Value }); } catch { } }
                    }
                }
                Bootstrap.Log("篡改: 已还原 " + featKey + " 原始池");
            }
            catch { }
            _trickPoolFeat.Remove(featKey);
            _trickPoolBackup.Remove(featKey);
            _trickLookupBackup.Remove(featKey);
        }

        /// <summary>篡改主入口（每 15 帧）：SlotMachine + RainMode 卡池锁定。</summary>
        static void ApplyTrickFeatures(Node root)
        {
            try
            {
                bool on = TrickAnyOn;
                if (!on)
                {
                    // 关闭篡改 → 还原之前被锁定的池（避免“关了还生效”/池被清空）
                    RestoreTrickPool("SlotMachine");
                    RestoreTrickPool("RainMode");
                    _trickFeatRef.Clear(); _trickApplied.Clear(); return;
                }
                var ids = TrickPoolIds();
                if (ids.Count == 0)
                {
                    RestoreTrickPool("SlotMachine");
                    RestoreTrickPool("RainMode");
                    _trickFeatRef.Clear(); _trickApplied.Clear(); return;   // 未设目标卡 → 不篡改
                }
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                object control = GetBattleControl(tdm);
                if (control == null) return;   // 不在战斗
                string sig = string.Join(",", ids) + (ModSettings.TrickFixedOnly ? "|F" : "|R");
                if (TrickBoxOn) TrickLockPool(control, "SlotMachine", ids, sig, true);
                if (TrickRainOn) TrickLockPool(control, "RainMode", ids, sig, false);
            }
            catch { }
        }

        /// <summary>读 TowerDefenseManager.currentControl（战斗 control，未开战为 null）。</summary>
        static object GetBattleControl(object tdm)
        {
            try
            {
                var cf = _tdmType.GetField("currentControl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                      ?? _tdmType.GetField("currentControl", BindingFlags.Public | BindingFlags.Static);
                return cf != null ? cf.GetValue(tdm) : null;
            }
            catch { return null; }
        }

        /// <summary>从战斗 control 读 feature（featureDictionary 公共字典）。</summary>
        static object GetControlFeature(object control, string key)
        {
            try
            {
                if (control == null) return null;
                var d = FindFieldVal(control, "featureDictionary");
                if (d is System.Collections.IDictionary idic)
                {
                    foreach (System.Collections.DictionaryEntry e in idic)
                    {
                        string k = e.Key != null ? e.Key.ToString() : "";
                        if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) return e.Value;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>锁定 feature 的运行时卡池为目标池。slotMachine=true → 老虎机 _slotItems；否则种子雨 _packetList。</summary>
        static void TrickLockPool(object control, string featKey, System.Collections.Generic.List<string> ids, string sig, bool slotMachine)
        {
            try
            {
                object feat = GetControlFeature(control, featKey);
                if (feat == null) return;   // 本关无此机制
                if (_trickFeatRef.TryGetValue(featKey, out var lf) && ReferenceEquals(lf, feat)
                    && _trickApplied.TryGetValue(featKey, out var ls) && ls == sig) return;
                string poolField = slotMachine ? "_slotItems" : "_packetList";
                var fld = feat.GetType().GetField(poolField, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fld == null) { Bootstrap.Log("篡改: " + featKey + " 找不到池字段 " + poolField); return; }
                object list = fld.GetValue(feat);
                if (!(list is System.Collections.IList il)) { Bootstrap.Log("篡改: " + featKey + " 池不是 IList"); return; }
                var add = list.GetType().GetMethod("Add");
                Type elemType = add != null && add.GetParameters().Length == 1 ? add.GetParameters()[0].ParameterType : null;
                if (elemType == null) { Bootstrap.Log("篡改: " + featKey + " 取不到元素类型"); return; }
                // 首次/feature 变化时先快照原池（失败可回滚、关闭可还原）
                if (!_trickPoolFeat.TryGetValue(featKey, out var pf) || !ReferenceEquals(pf, feat))
                {
                    _trickPoolFeat[featKey] = feat;
                    var orig = new System.Collections.Generic.List<object>();
                    foreach (var o in il) orig.Add(o);
                    _trickPoolBackup[featKey] = orig;
                    var lkb = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, object>>();
                    if (slotMachine)
                    {
                        var lk0 = FindFieldVal(feat, "_slotItemLookup");
                        if (lk0 is System.Collections.IDictionary d0)
                            foreach (System.Collections.DictionaryEntry e0 in d0)
                                lkb.Add(new System.Collections.Generic.KeyValuePair<string, object>(e0.Key != null ? e0.Key.ToString() : "", e0.Value));
                    }
                    _trickLookupBackup[featKey] = lkb;
                    Bootstrap.Log("篡改: 已快照 " + featKey + " 原始池 " + orig.Count + " 项");
                }
                il.Clear();
                var lookup = slotMachine ? FindFieldVal(feat, "_slotItemLookup") : null;
                if (lookup != null)
                {
                    var lkClear = lookup.GetType().GetMethod("Clear");
                    if (lkClear != null) { try { lkClear.Invoke(lookup, null); } catch { } }
                }
                int n = 0;
                foreach (var id in ids)
                {
                    object item = slotMachine ? MakeSlotItem(elemType, id) : MakeRainPacket(elemType, id);
                    if (item == null) continue;
                    try { il.Add(item); n++; } catch { }
                    if (lookup != null)
                    {
                        var lkAdd = lookup.GetType().GetMethod("Add");
                        if (lkAdd != null) { try { lkAdd.Invoke(lookup, new object[] { id, item }); } catch { } }
                    }
                }
                if (n > 0)
                {
                    _trickFeatRef[featKey] = feat;
                    _trickApplied[featKey] = sig;
                    Bootstrap.Log("篡改: " + featKey + " 已锁定 " + n + " 张目标卡（池=" + string.Join(",", ids) + "）");
                }
                else
                {
                    // 一张都没造出来 → 回滚原池；绝不能留空池（否则盲盒/种子雨开不出东西）
                    RestoreTrickPool(featKey);
                    Bootstrap.Log("篡改: " + featKey + " 重建失败（0 张），已回滚原池");
                }
            }
            catch { }
        }

        /// <summary>构造老虎机 SlotItem（私有嵌套类 SlotRewardType.Packet=0）。</summary>
        static object MakeSlotItem(Type elemType, string id)
        {
            try
            {
                object item = Activator.CreateInstance(elemType, true);
                SetPropOrField(item, "Key", id);
                if (!((FindPropOrFieldVal(item, "Key") as string) == id)) { Bootstrap.Log("篡改: SlotItem.Key 写入失败 " + id); return null; }
                SetPropOrField(item, "DisplayKey", id);
                var tf = elemType.GetField("Type", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (tf != null) tf.SetValue(item, Enum.ToObject(tf.FieldType, 0));
                var af = elemType.GetField("Amount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (af != null) af.SetValue(item, 1);
                var wf = elemType.GetField("Weight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (wf != null) wf.SetValue(item, 1);
                return item;
            }
            catch { return null; }
        }

        /// <summary>构造种子雨掉落卡配置（name=目标卡，weight=1，不限场上数量）。</summary>
        static object MakeRainPacket(Type elemType, string id)
        {
            try
            {
                object obj = Activator.CreateInstance(elemType, true);
                SetPropOrField(obj, "name", id);
                SetPropOrField(obj, "weight", 1);
                SetPropOrField(obj, "maxNum", -1);
                SetPropOrField(obj, "minNum", -1);
                SetPropOrField(obj, "maxMagnification", 1.0);
                SetPropOrField(obj, "minMagnification", 1.0);
                return obj;
            }
            catch { return null; }
        }

        /// <summary>传送带卡槽篡改（TrickConveyor + 目标池）：快速刷新，把传送带管理器的每张卡替换成目标池卡。</summary>
        static void AutoTrickConveyorSlots(Node root)
        {
            try
            {
                if (!TrickConveyorOn) return;
                var ids = TrickPoolIds();
                if (ids.Count == 0) return;
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                object control = GetBattleControl(tdm);
                if (control == null) return;
                object feat = GetControlFeature(control, "ConveyorBelt");
                if (feat == null) return;
                var mgr = FindFieldVal(feat, "conveyorBeltManager");
                if (mgr == null) return;
                var gp = FindMethodByPrefix(mgr.GetType(), "GetPacketChildren", new Type[] { typeof(bool) });
                if (gp == null) return;
                bool mobile = false;
                try { var mb = FindPropOrFieldVal(mgr, "isMobileUI"); if (mb is bool m) mobile = m; } catch { }
                object children = gp.Invoke(mgr, new object[] { mobile });
                if (!(children is System.Collections.IEnumerable enu)) return;
                var rnd = new Random();
                foreach (var child in enu)
                {
                    try
                    {
                        if (child == null || !GodotObject.IsInstanceValid(child as GodotObject)) continue;
                        // 正在被拿的卡跳过（拿到手上的照种）
                        try { var sel = FindPropOrFieldVal(child, "select"); if (sel is bool sl && sl) continue; } catch { }
                        var ck = FindPropOrFieldVal(child, "config");
                        var cur = ck != null ? FindPropOrFieldVal(ck, "saveKey") as string : null;
                        if (cur != null && ids.Contains(cur)) continue;
                        string id = ModSettings.TrickFixedOnly ? ids[0] : ids[rnd.Next(ids.Count)];
                        ReplacePacketShowContent(child, id);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>把单个卡槽/传送带卡（TowerDefenseInGamePacketShow）替换成指定 id：重 Init + 刷新显示。</summary>
        static void ReplacePacketShowContent(object packetShow, string id)
        {
            try
            {
                var cfg = GetConfig(id);
                if (cfg == null) return;
                object dup = null;
                try
                {
                    var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                    if (dm != null) dup = dm.Invoke(cfg, new object[] { true });
                }
                catch { }
                if (dup == null) dup = cfg;
                var init = FindMethodByPrefix(packetShow.GetType(), "Init", new Type[] { dup.GetType(), typeof(bool) });
                if (init == null) init = FindMethodByPrefix(packetShow.GetType(), "Init", new Type[] { dup.GetType() });
                if (init == null) return;
                init.Invoke(packetShow, FillArgs(init, new object[] { dup, false }));
                var rp = FindMethodByPrefix(packetShow.GetType(), "RefreshPreview", Type.EmptyTypes);
                if (rp != null) { try { rp.Invoke(packetShow, null); } catch { } }
                var ub = FindMethodByPrefix(packetShow.GetType(), "UpdateBackgroundTexture", Type.EmptyTypes);
                if (ub != null) { try { ub.Invoke(packetShow, null); } catch { } }
            }
            catch { }
        }


        /// <summary>礼盒盲盒拦截（patcher 注入 PresentBox/Green 的 Explode 开头）：
        /// 篡改（总开关或盲盒子开关）且目标池非空 → 礼盒爆炸直接开出目标池卡
        /// （植物原位种 / 僵尸原位生成），返回 true 跳过原随机开盒；否则 false 走原逻辑。</summary>
        public static bool OnPresentBoxExplode(object box)
        {
            try
            {
                if (!TrickBoxOn) return false;
                var ids = TrickPoolIds();
                if (ids.Count == 0) return false;
                string id = ModSettings.TrickFixedOnly ? ids[0] : ids[new Random().Next(ids.Count)];
                var cfg = GetConfig(id);
                if (cfg == null) return false;
                var cell = FindPropOrFieldVal(box, "cell");
                var gp = FindPropOrFieldVal(box, "gridPos");
                if (cell == null || !(gp is Vector2I g)) return false;
                // 让出礼盒占格
                try
                {
                    System.Reflection.MethodInfo rm = null;
                    foreach (var m in cell.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "RemoveCharacter" && m.GetParameters().Length == 1) { rm = m; break; }
                    if (rm != null) rm.Invoke(cell, new object[] { box });
                }
                catch { }
                bool isZombie = !id.StartsWith("Plant", StringComparison.OrdinalIgnoreCase);
                if (isZombie)
                {
                    SpawnCharacter(id, g, false);
                }
                else
                {
                    object dup = cfg;
                    try
                    {
                        var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                        if (dm != null) { var d = dm.Invoke(cfg, new object[] { true }); if (d != null) dup = d; }
                    }
                    catch { }
                    var p1 = FindMethodByPrefix(dup.GetType(), "Plant", new Type[] { typeof(Vector2I), typeof(bool), typeof(bool) });
                    if (p1 != null) p1.Invoke(dup, FillArgs(p1, new object[] { g, true, true }));
                    else { var p0 = FindMethodByPrefix(dup.GetType(), "Plant", new Type[] { typeof(Vector2I) }); if (p0 != null) p0.Invoke(dup, new object[] { g }); }
                }
                // 礼盒自身移除（已让格，原 Explode 被跳过不会自己消失）
                try { if (box is Godot.Node n0) n0.QueueFree(); } catch { }
                Bootstrap.Log("篡改: 礼盒开出 " + id + " @" + g);
                return true;
            }
            catch { return false; }
        }

        /// <summary>「种下随机给卡」类植物篡改（patcher 注入 BYWZ(备用物质)/幸运三叶草/花园套装/升级豆/路灯菇/魔法豆 的 Explode 开头）：
        /// 篡改开（总开关或盲盒）且目标池非空 → 从目标池取一张卡直接给玩家，返回 true 跳过原随机掉卡；否则 false 走原逻辑。</summary>
        public static bool OnRandomPacketPlantExplode(object plant)
        {
            try
            {
                if (!TrickBoxOn) return false;
                var ids = TrickPoolIds();
                if (ids.Count == 0) return false;
                string id = ModSettings.TrickFixedOnly ? ids[0] : ids[new Random().Next(ids.Count)];
                var cfg = GetConfig(id);
                if (cfg == null) { Bootstrap.Log("篡改: 抽卡植物目标卡无效 " + id + "，走原逻辑"); return false; }
                string tn = plant != null ? plant.GetType().Name : "";
                // 幸运三叶草原逻辑是“直接进卡槽”；战备物资/升级豆/花园套装/路灯菇/魔法豆原本是“掉一张卡在地上”
                bool toSlot = tn.IndexOf("LuckyBlover", StringComparison.OrdinalIgnoreCase) >= 0;
                if (toSlot)
                {
                    var tdm = GetTdmInstance();
                    if (tdm == null) { Bootstrap.Log("篡改: 抽卡植物无 TDM 实例，走原逻辑"); return false; }
                    System.Reflection.MethodInfo ap = null;
                    foreach (var m in tdm.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (m.Name == "AddPacket" && m.GetParameters().Length == 2) { ap = m; break; }
                    if (ap == null) { Bootstrap.Log("篡改: 抽卡植物找不到 AddPacket，走原逻辑"); return false; }
                    try { ap.Invoke(tdm, FillArgs(ap, new object[] { id, null })); }
                    catch (System.Exception ex) { Bootstrap.Log("篡改: 抽卡植物给卡异常 " + id + " → " + ex.Message); return false; }
                }
                else
                {
                    // 掉一张目标卡（保持“掉在地上的卡牌”原表现，不扣阳光）
                    System.Reflection.MethodInfo sp = null;
                    foreach (var m in plant.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (m.Name == "SpawnPacket" && m.GetParameters().Length == 7) { sp = m; break; }
                    if (sp == null) { Bootstrap.Log("篡改: 抽卡植物找不到 SpawnPacket，走原逻辑"); return false; }
                    try { sp.Invoke(plant, FillArgs(sp, new object[] { cfg, GetLogicalPos(plant), 15.0, false, false, true })); }
                    catch (System.Exception ex) { Bootstrap.Log("篡改: 抽卡植物掉卡异常 " + id + " → " + ex.Message); return false; }
                }
                try { if (plant is Godot.Node pn) pn.QueueFree(); } catch { }
                Bootstrap.Log("篡改: 抽卡植物给出 " + id + (toSlot ? "（已入卡槽）" : "（掉落卡牌）"));
                return true;
            }
            catch { return false; }
        }

        /// <summary>取角色逻辑坐标（GetLogicalGlobalPosition 优先，兜底 Node2D.GlobalPosition）。</summary>
        static Godot.Vector2 GetLogicalPos(object n)
        {
            try
            {
                var m = n.GetType().GetMethod("GetLogicalGlobalPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null && m.Invoke(n, null) is Godot.Vector2 v) return v;
            }
            catch { }
            try { if (n is Godot.Node2D n2) return n2.GlobalPosition; } catch { }
            return Godot.Vector2.Zero;
        }

        /// <summary>随机环境诊断：报告当前场景 Vase 罐子数量和卡槽卡数量（每 10 秒一次，仅在随机开关开启时）。</summary>
        static void RandomEnvDiag(Node root)
        {
            try
            {
                int vaseCount = 0, sbCount = 0;
                var tree = root != null ? root.GetTree() : null;
                if (tree != null)
                {
                    var g = tree.GetNodesInGroup("Vase");
                    vaseCount = g.Count;
                }
                try
                {
                    var tdm = GetTdmInstance();
                    if (tdm != null && _tdmType != null)
                    {
                        object sb = null;
                        var gm = _tdmType.GetMethod("GetSeedBankFeature", Type.EmptyTypes);
                        var feat = gm != null ? gm.Invoke(tdm, null) : null;
                        if (feat != null) sb = FindFieldVal(feat, "seedBank");
                        if (sb == null) { var gm2 = _tdmType.GetMethod("GetSeedBank", Type.EmptyTypes); sb = gm2 != null ? gm2.Invoke(tdm, null) : null; }
                        if (sb != null && FindFieldVal(sb, "packetList") is System.Collections.IEnumerable en)
                            foreach (var _ in en) sbCount++;
                    }
                }
                catch { }
                Bootstrap.Log("随机环境诊断: Vase罐子=" + vaseCount + " 卡槽卡=" + sbCount);
            }
            catch { }
        }

        /// <summary>自动罐子随机：进罐子关卡随机一次内容并显示（GetNodesInGroup 查找，不遍历整棵树）。
        /// 砸罐子时由 OnVaseAboutToBreak（DestroySet 注入）每次砸前重新随机，保证每次砸都随机。</summary>
        static void AutoRandomizeVases(Node root)
        {
            try
            {
                var tree = root != null ? root.GetTree() : null;
                if (tree == null) return;
                Godot.Collections.Array<Node> vases = tree.GetNodesInGroup("Vase");
                int count = vases.Count;
                if (count == 0) { _vaseDone = false; _lastVaseCount = 0; return; }
                if (count > _lastVaseCount) { _lastVaseCount = count; _vaseDone = false; }   // 新罐子出现 → 重新随机全部
                if (_vaseDone) return;
                RandomizeVases(root, true, vases);
                _vaseDone = true;
                Bootstrap.Log("罐子自动随机: 完成 " + count + " 个");
            }
            catch (System.Exception ex) { Bootstrap.Log("罐子自动随机异常: " + ex.Message); }
        }

        static System.Collections.Generic.List<string> _vasePool;
        static Random _rndVase = new Random();

        /// <summary>构建罐子随机池：植物+僵尸 ID（排除名字含 Vase 的卡，避免砸出“新罐子”）。只构建一次。</summary>
        static void EnsureVasePool()
        {
            if (_vasePool != null && _vasePool.Count > 0) return;
            _vasePool = new System.Collections.Generic.List<string>();
            foreach (var id in GetPacketIds(true)) if (id != null && !id.Contains("Vase")) _vasePool.Add(id);
            foreach (var id in GetPacketIds(false)) if (id != null && !id.Contains("Vase")) _vasePool.Add(id);
        }

        /// <summary>随机单个罐子的内容：直接设置 packetConfig/packetName（绕过 SetContent 的 Alive/Active 条件）。</summary>
        static void RandomizeOneVase(Node vaseNode)
        {
            if (vaseNode == null || !GodotObject.IsInstanceValid(vaseNode)) return;
            EnsureVasePool();
            if (_vasePool.Count == 0) return;
            var id = _vasePool[_rndVase.Next(_vasePool.Count)];
            var cfg = GetConfig(id);
            if (cfg == null) return;
            object dup = null;
            try
            {
                var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                if (dm != null) dup = dm.Invoke(cfg, new object[] { true });
            }
            catch { }
            if (dup == null) dup = cfg;
            SetPropOrField(vaseNode, "packetConfig", dup);
            SetPropOrField(vaseNode, "packetName", id);
        }

        /// <summary>砸罐子瞬间回调（由 patcher 注入 VaseContentComponent.DestroySet 开头）：
        /// 每次砸罐子前重新随机该罐子内容 → 每次砸都随机（无名版随机罐子效果）。</summary>
        public static void OnVaseAboutToBreak(object comp)
        {
            try
            {
                if (!ModSettings.VaseRandom) return;
                if (comp == null) return;
                var vase = FindFieldVal(comp, "parent");   // VaseContentComponent.parent = TowerDefenseVase
                if (vase == null) return;
                RandomizeOneVase(vase as Node);
            }
            catch { }
        }

        static System.Collections.Generic.List<string> _packetPool;
        static Random _rndPacket = new Random();

        /// <summary>构建卡牌随机池：纯植物 ID（卡槽只能种植物，僵尸放进去种不了）。只构建一次。</summary>
        static void EnsurePacketPool()
        {
            if (_packetPool != null && _packetPool.Count > 0) return;
            _packetPool = new System.Collections.Generic.List<string>();
            foreach (var id in GetPacketIds(true)) if (id != null) _packetPool.Add(id);
        }

        /// <summary>随机单张卡的内容：Init 换成随机植物（卡牌内东西随机——每次种都随机）。</summary>
        static void RandomizeOnePacket(object packetShow)
        {
            try
            {
                if (packetShow == null || !GodotObject.IsInstanceValid((GodotObject)packetShow)) return;
                EnsurePacketPool();
                if (_packetPool.Count == 0) return;
                var id = _packetPool[_rndPacket.Next(_packetPool.Count)];
                var cfg = GetConfig(id);
                if (cfg == null) return;
                object dup = null;
                try
                {
                    var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                    if (dm != null) dup = dm.Invoke(cfg, new object[] { true });
                }
                catch { }
                if (dup == null) dup = cfg;
                var init = FindMethodByPrefix(packetShow.GetType(), "Init", new Type[] { dup.GetType(), typeof(bool) });
                if (init == null) init = FindMethodByPrefix(packetShow.GetType(), "Init", new Type[] { dup.GetType() });
                if (init == null) return;
                init.Invoke(packetShow, FillArgs(init, new object[] { dup, false }));
            }
            catch { }
        }

        /// <summary>种植瞬间回调（由 patcher 注入 TowerDefenseInGamePacketShow.Plant 开头，在种一列之前）：
        /// ①三叶草吹飞所有僵尸（种 Blover 时清空场上僵尸）；②每次种卡前把卡内容随机成随机植物。</summary>
        public static void OnPacketAboutToPlant(object packetShow)
        {
            try
            {
                // 三叶草吹飞：种 Blover 时清空场上所有僵尸（原版三叶草效果）
                if (ModSettings.BloverClearAll && packetShow != null && GodotObject.IsInstanceValid((GodotObject)packetShow))
                {
                    try
                    {
                        // 0.26 PacketShow 没有 packetName/packetId 字段（那是 0.25.5 结构）！
                        // 卡 id 在 config.saveKey（Blover / LuckyBlover 都含 "Blover"）
                        string pn = null;
                        var cfg = FindPropOrFieldVal(packetShow, "config");
                        if (cfg != null)
                        {
                            pn = FindPropOrFieldVal(cfg, "saveKey") as string;
                            if (string.IsNullOrEmpty(pn)) pn = FindPropOrFieldVal(cfg, "packetName") as string;
                            if (string.IsNullOrEmpty(pn)) pn = FindPropOrFieldVal(cfg, "id") as string;
                        }
                        if (string.IsNullOrEmpty(pn)) pn = FindPropOrFieldVal(packetShow, "originalSaveKey") as string;
                        if (string.IsNullOrEmpty(pn)) pn = FindPropOrFieldVal(packetShow, "saveKey") as string;
                        if (!string.IsNullOrEmpty(pn) && pn.IndexOf("Blover", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var tree = (packetShow as Godot.Node)?.GetTree();
                            if (tree != null && tree.Root != null)
                            {
                                RemoveAllZombies(tree.Root);
                                Bootstrap.Log("三叶草吹飞: 已吹飞所有僵尸");
                            }
                        }
                    }
                    catch { }
                }
                if (!ModSettings.SeedBankRandom) return;
                if (packetShow == null) return;
                // 种植固定：随机卡槽只在卡槽层面随机（AutoRandomizeSeedBank 每帧刷新 + 跳过选中卡），
                // 种植瞬间不再把当前卡随机掉——种下的就是卡上显示的那张（用户反馈：种下的植物也会随机）
                // RandomizeOnePacket(packetShow);   ← 已移除，种固定
            }
            catch { }
        }

        /// <summary>罐子内物品随机——在原来的罐子基础上随机改变罐子里的东西：
        /// 不重新放置罐子，直接设置罐子 packetConfig/packetName 为随机植物/僵尸
        /// （砸开时游戏用 packetConfig 生成内容；绕过 SetContent 的 Alive/Active 条件）。
        /// forceShow=true 时刷新并显示罐子顶部卡牌（透视）。
        /// vases 可传入已收集的罐子列表（null 时内部用 GetNodesInGroup 查找）。</summary>
        public static void RandomizeVases(Node root, bool forceShow = false, Godot.Collections.Array<Node> vases = null)
        {
            try
            {
                if (vases == null)
                {
                    var tree = root != null ? root.GetTree() : null;
                    if (tree == null) return;
                    vases = tree.GetNodesInGroup("Vase");
                }
                if (vases.Count == 0) { Bootstrap.Log("罐子随机: 未找到罐子（Vase 组为空）"); return; }
                EnsureVasePool();
                if (_vasePool.Count == 0) { Bootstrap.Log("罐子随机: 无可用卡池"); return; }
                int changed = 0;
                int failed = 0;
                foreach (Node vaseNode in vases)
                {
                    if (vaseNode == null || !GodotObject.IsInstanceValid(vaseNode)) { failed++; continue; }
                    var id = _vasePool[_rndVase.Next(_vasePool.Count)];
                    var cfg = GetConfig(id);
                    if (cfg == null) { failed++; continue; }
                    object dup = null;
                    try
                    {
                        var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                        if (dm != null) dup = dm.Invoke(cfg, new object[] { true });
                    }
                    catch { }
                    if (dup == null) dup = cfg;
                    SetPropOrField(vaseNode, "packetConfig", dup);
                    SetPropOrField(vaseNode, "packetName", id);
                    if (forceShow)
                    {
                        // 刷新顶部卡牌显示 + 显示出来（透视，不用暂停）
                        var refresh = FindMethodByPrefix(vaseNode.GetType(), "RefreshPacketShowFromConfig", Type.EmptyTypes);
                        if (refresh != null) { try { refresh.Invoke(vaseNode, null); } catch { } }
                        SetPropOrField(vaseNode, "showPacket", true);
                    }
                    // 验证：读回 packetName 确认设置成功
                    var verify = FindPropOrFieldVal(vaseNode, "packetName") as string;
                    if (verify == id) changed++;
                    else failed++;
                }
                Bootstrap.Log("罐子随机: 成功 " + changed + " 失败 " + failed + (forceShow ? "（已显示）" : ""));
            }
            catch (System.Exception ex) { Bootstrap.Log("罐子随机异常: " + ex.Message); }
        }

        /// <summary>种子雨加速掉落：找种子雨功能，把 config.interval 缩短（更快掉种子卡）。
        /// 开=压缩到 0.3，关=恢复原值。RainMode.Process 每帧 timer+=delta，达到 interval 就 Spawn 一张卡。</summary>
        static void ApplyRainFast(Node root)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                var gm = _tdmType.GetMethod("GetRainModeFeature", Type.EmptyTypes);
                var feat = gm != null ? gm.Invoke(tdm, null) : null;
                if (feat == null) return;
                var cfg = FindFieldVal(feat, "config");
                if (cfg == null) return;
                var f = cfg.GetType().GetField("interval", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return;
                if (!ReferenceEquals(_rainCfgRef, cfg)) { _rainCfgRef = cfg; _rainOrigSet = false; }
                if (ModSettings.RainFast)
                {
                    if (!_rainOrigSet) { _rainOrigVal = f.GetValue(cfg); _rainOrigSet = true; }
                    if (f.FieldType == typeof(double))
                    {
                        double cur = (double)f.GetValue(cfg);
                        if (cur > 0.3) f.SetValue(cfg, 0.3);
                    }
                    else if (f.FieldType == typeof(float))
                    {
                        float cur = (float)f.GetValue(cfg);
                        if (cur > 0.3f) f.SetValue(cfg, 0.3f);
                    }
                    if (!_rainLogged) { _rainLogged = true; Bootstrap.Log("种子雨加速: interval=" + f.GetValue(cfg)); }
                }
                else if (_rainOrigSet)
                {
                    f.SetValue(cfg, _rainOrigVal);   // 恢复原始间隔
                    _rainOrigSet = false;
                    Bootstrap.Log("种子雨加速已关闭，恢复 interval=" + _rainOrigVal);
                }
            }
            catch { }
        }

        /// <summary>点按式：魅惑全场指定阵营角色（一次性加 Hypnoses buff，不每帧操作）。</summary>
        public static void CharmAll(Node root, string camp)
        {
            try
            {
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                int n = 0;
                CharmWalk(start, camp, ref n);
                Bootstrap.Log("魅惑" + camp + ": 已魅惑 " + n + " 个");
            }
            catch { }
        }

        static void CharmWalk(Node node, string camp, ref int n)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && GodotObject.IsInstanceValid(n2) && ESP.GetCamp(n2) == camp)
                    {
                        // 清魅惑免疫位（海妖/僵王/带头盔等），否则 Hypnoses 免疫直接 return 不生效
                        ClearHypnosisImmune(n2);
                        bool add = !BuffHas(n2, "Hypnoses");
                        if (add) { InvokeHypnoses(n2); n++; }
                        // 免疫被绕过后的兜底：强制 hypnoses=true + 清攻击目标 + 僵尸改阵营
                        SetCharmFlagAndClearTarget(n2);
                        if (IsTypeOf(n2, "TowerDefenseZombie")) SetCampEnum(n2, "PLANT");
                    }
                }
                catch { }
                CharmWalk(child, camp, ref n);
            }
        }

        /// <summary>点按式：还原全场魅惑（一次性删 Hypnoses buff）。</summary>
        public static void UncharmAll(Node root)
        {
            try
            {
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                int n = 0;
                UncharmWalk(start, ref n);
                Bootstrap.Log("还原魅惑: 已还原 " + n + " 个");
            }
            catch { }
        }

        static void UncharmWalk(Node node, ref int n)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && GodotObject.IsInstanceValid(n2) && BuffHas(n2, "Hypnoses"))
                    {
                        InvokeHypnoses(n2);   // Hypnoses() 有 buff 删、无 buff 加
                        n++;
                    }
                }
                catch { }
                UncharmWalk(child, ref n);
            }
        }

        /// <summary>检查角色是否带有指定 buff（反射调用 BuffGet）。</summary>
        static bool BuffHas(Node2D n, string buffName)
        {
            try
            {
                var m = n.GetType().GetMethod("BuffGet", new Type[] { typeof(string) });
                if (m != null)
                {
                    var r = m.Invoke(n, new object[] { buffName });
                    return r != null;
                }
            }
            catch { }
            return false;
        }

        /// <summary>调用游戏 TowerDefenseCharacter.Hypnoses()：无 buff 添加（触发魅惑外观/音频），有 buff 删除（还原）。
        /// 反射传默认参数 time=-1, canFliter=true, config=null。</summary>
        static void InvokeHypnoses(Node2D n)
        {
            try
            {
                var mi = FindMethodByPrefix(n.GetType(), "Hypnoses", new Type[] { typeof(double) });
                if (mi != null) mi.Invoke(n, FillArgs(mi, new object[] { -1.0 }));
                else if (!_asLogged) { _asLogged = true; Bootstrap.Log("魅惑buff失败: 找不到 Hypnoses 方法 " + n.GetType().Name); }
            }
            catch (System.Exception ex) { if (!_asLogged) { _asLogged = true; Bootstrap.Log("魅惑buff异常: " + ex.Message); } }
        }

        /// <summary>魅惑：设置 TowerDefenseCharacterInstance.hypnoses = true。</summary>
        static void ApplyCharm(Node2D n)
        {
            try
            {
                var inst = FindFieldVal(n, "instance");
                if (inst == null || !GodotObject.IsInstanceValid((GodotObject)inst)) return;
                var t = inst.GetType();
                var hf = t.GetField("hypnoses", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (hf != null && hf.FieldType == typeof(bool) && !(bool)hf.GetValue(inst))
                {
                    hf.SetValue(inst, true);
                    if (!_charmLogged) { _charmLogged = true; Bootstrap.Log("魅惑已生效"); }
                }
            }
            catch { }
        }

        /// <summary>攻速：AttackComponent.attackInterval = attackIntervalBase / 倍率（攻击后 timer 取 attackInterval）。
        /// 获取组件用 _runtimeByInstanceId 字典遍历（无参 GetRuntime 只查资源组件，找不到攻击组件）。</summary>
        static readonly System.Collections.Generic.Dictionary<ulong, double> _zombieAtkOrig = new();
        static bool _zombieAtkLogged;
        /// <summary>僵尸攻速：直接改 AttackComponent.attackIntervalBase/attackInterval = 原值/倍率（原值缓存防累积除法）。
        /// 攻击后 Refresh() 用 attackInterval 重置 timer，改它即改攻速；不依赖组件 dispatch（AccelerateTimer 对僵尸不可靠）。</summary>
        static void ApplyZombieAttackSpeed(Node2D n, float mult)
        {
            try
            {
                if (mult <= 0.01f) mult = 0.01f;
                var acField = n.GetType().GetField("attackComponent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (acField == null) return;
                var ac = acField.GetValue(n);
                if (ac == null || !GodotObject.IsInstanceValid((GodotObject)ac)) return;
                var t = ac.GetType();
                var baseField = t.GetField("attackIntervalBase", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var intField = t.GetField("attackInterval", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (baseField == null || intField == null || baseField.FieldType != typeof(double) || intField.FieldType != typeof(double)) return;
                ulong id = n.GetInstanceId();
                // 关闭（mult=1）：恢复原值（之前持久改过必须还原，否则关掉还是高速）
                if (Math.Abs(mult - 1.0f) < 0.001f)
                {
                    if (_zombieAtkOrig.TryGetValue(id, out double o0))
                    {
                        double cb0 = (double)baseField.GetValue(ac);
                        if (Math.Abs(cb0 - o0) > 0.001) baseField.SetValue(ac, o0);
                        double ci0 = (double)intField.GetValue(ac);
                        if (Math.Abs(ci0 - o0) > 0.001) intField.SetValue(ac, o0);
                        _zombieAtkOrig.Remove(id);
                    }
                    return;
                }
                double curB = (double)baseField.GetValue(ac);
                double curI = (double)intField.GetValue(ac);
                if (curB <= 0.0 && curI <= 0.0) return;
                double orig;
                if (!_zombieAtkOrig.TryGetValue(id, out orig)) { orig = curB > 0.0 ? curB : curI; _zombieAtkOrig[id] = orig; }
                else
                {
                    // 游戏若重置了 attackIntervalBase（关卡配置/读档），同步原值，避免累积除法
                    double expected = orig / mult;
                    if (Math.Abs(curB - expected) > 0.001 && Math.Abs(curB - orig) > 0.001) { orig = curB; _zombieAtkOrig[id] = orig; }
                }
                double target = Math.Round(orig / mult, 3);
                if (Math.Abs(curB - target) > 0.001) baseField.SetValue(ac, target);
                if (Math.Abs(curI - target) > 0.001) intField.SetValue(ac, target);
            }
            catch (System.Exception ex) { if (!_zombieAtkLogged) { _zombieAtkLogged = true; Bootstrap.Log("僵尸攻速异常: " + ex.Message); } }
        }

        /// <summary>植物射速缓存：fire=射速原值，offset=发射随机偏移原值，anime=攻击动画时间缩放原值。</summary>
        sealed class PlantFireState
        {
            public double fire;
            public float offset;
            public float anime;
            public float restore;
            public int checkMax;
            public bool fireDirect;
        }
        static readonly System.Collections.Generic.Dictionary<ulong, PlantFireState> _plantFireOrig = new();
        static bool _plantFireLogged;
        static bool _fcDiag;   // FireComponent 查找诊断
        /// <summary>植物射速：真正控制射速的是 FireComponent.fireInterval (Single)（timer 倒计时到它触发射击）。
        /// 同时兼容植物节点 fireInterval (Double)（部分机制可能读它）——双写保险。原值缓存 _plantFireOrig 恢复用。
        /// 无间隔关键：Refresh 发射后 timer = fireInterval + RandRange(-|fireIntervalOffset|, +|fireIntervalOffset|)，
        /// 所以 fireIntervalOffset 必须清零；攻击动画时长是间隔下限，fireAnimeTimeScale 须放大。
        /// 僵尸攻速由 AccelerateTimer（注入 AttackComponent.BatchUpdateValidated）负责。</summary>
        static void ApplyAttackSpeed(Node2D n, float mult)
        {
            try
            {
                if (mult <= 0.01f) mult = 0.01f;
                ulong id = n.GetInstanceId();
                // FireComponent（真正射速 Single）
                object comp = FindPlantFireComponent(n);
                System.Reflection.FieldInfo fi = null, fb = null, fo = null, fa = null, fr = null, fx = null, fd = null;
                if (comp != null)
                {
                    var t = comp.GetType();
                    System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    fi = t.GetField("fireInterval", bf);
                    fb = t.GetField("fireIntervalBase", bf);
                    fo = t.GetField("fireIntervalOffset", bf);
                    fa = t.GetField("fireAnimeTimeScale", bf);
                    fr = t.GetField("restoreTime", bf);
                    fx = t.GetField("checkIntervalMax", bf);
                    fd = t.GetField("fireDirect", bf);
                }
                // 植物节点 fireInterval (Double) 兜底
                var nfp = n.GetType().GetProperty("fireInterval", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool hasF = fi != null && fi.FieldType == typeof(float);
                bool hasD = nfp != null && nfp.PropertyType == typeof(double);
                bool hasO = fo != null && fo.FieldType == typeof(float);
                bool hasA = fa != null && fa.FieldType == typeof(float);
                bool hasR = fr != null && fr.FieldType == typeof(float);
                bool hasX = fx != null && fx.FieldType == typeof(int);
                bool hasG = fd != null && fd.FieldType == typeof(bool);
                if (!hasF && !hasD) { if (!_fcDiag) { _fcDiag = true; Bootstrap.Log("植物射速: 无 fireInterval 控制点 植物=" + n.GetType().Name); } return; }
                double cur = hasF ? (double)(float)fi.GetValue(comp) : (double)nfp.GetValue(n);
                if (cur <= 0.0) return;
                PlantFireState st;
                if (!_plantFireOrig.TryGetValue(id, out st))
                {
                    st = new PlantFireState();
                    st.fire = cur;
                    st.offset = hasO ? (float)fo.GetValue(comp) : 0f;
                    st.anime = hasA ? (float)fa.GetValue(comp) : 1f;
                    st.restore = hasR ? (float)fr.GetValue(comp) : 0f;
                    st.checkMax = hasX ? (int)fx.GetValue(comp) : 0;
                    st.fireDirect = hasG ? (bool)fd.GetValue(comp) : false;
                    _plantFireOrig[id] = st;
                }
                // 关闭（mult=1）：恢复原值
                if (Math.Abs(mult - 1.0f) < 0.001f)
                {
                    if (hasF) { fi.SetValue(comp, (float)st.fire); if (fb != null && fb.FieldType == typeof(float)) fb.SetValue(comp, (float)st.fire); }
                    if (hasD) SetFireIntervalVal(n, nfp, st.fire);
                    if (hasO) fo.SetValue(comp, st.offset);
                    if (hasA) fa.SetValue(comp, st.anime);
                    if (hasR) fr.SetValue(comp, st.restore);
                    if (hasX) fx.SetValue(comp, st.checkMax);
                    if (hasG) fd.SetValue(comp, st.fireDirect);
                    _plantFireOrig.Remove(id);
                    return;
                }
                // 游戏若重置了 fireInterval（关卡配置/读档），同步原值，避免累积除法
                {
                    double expected = st.fire / mult;
                    if (Math.Abs(cur - expected) > 0.001 && Math.Abs(cur - st.fire) > 0.001) st.fire = cur;
                }
                double target = Math.Round(st.fire / mult, 3);
                bool changed = false;
                if (hasF && Math.Abs((float)fi.GetValue(comp) - (float)target) > 0.001f) { fi.SetValue(comp, (float)target); if (fb != null && fb.FieldType == typeof(float)) fb.SetValue(comp, (float)target); changed = true; }
                if (hasD && Math.Abs((double)nfp.GetValue(n) - target) > 0.001) { SetFireIntervalVal(n, nfp, target); changed = true; }
                // 无间隔关键：随机偏移清零 + 攻击动画加速（mult 倍速）+ 恢复动画清零 + 检测间隔清零
                if (hasO && Math.Abs((float)fo.GetValue(comp)) > 0.0001f) { fo.SetValue(comp, 0f); changed = true; }
                if (hasA)
                {
                    float ta = Math.Max(1f, mult);
                    if (Math.Abs((float)fa.GetValue(comp) - ta) > 0.01f) { fa.SetValue(comp, ta); changed = true; }
                }
                if (hasR && Math.Abs((float)fr.GetValue(comp)) > 0.0001f) { fr.SetValue(comp, 0f); changed = true; }
                if (hasX && (int)fx.GetValue(comp) != 0) { fx.SetValue(comp, 0); changed = true; }
                // 禁用动画：fireDirect=true 直接发射不播攻击动画（官方机制，喷泉豌豆即此）
                if (hasG && !(bool)fd.GetValue(comp)) { fd.SetValue(comp, true); changed = true; }
                if (changed && !_plantFireLogged) { _plantFireLogged = true; Bootstrap.Log("植物射速: " + n.GetType().Name + " fireInterval " + cur.ToString("0.00") + "->" + target.ToString("0.00") + " offset=" + (hasO ? st.offset.ToString("0.00") : "n/a") + " mult=" + mult + " 源=" + (hasF ? "FireComponent" : "") + (hasD ? "+节点" : "")); }
            }
            catch (System.Exception ex) { if (!_asLogged) { _asLogged = true; Bootstrap.Log("攻速异常: " + ex.Message); } }
        }

        /// <summary>找植物的 FireComponent：①componentManager._runtimeByInstanceId 字典 ②fireComponent/_fireComponent 字段 ③子节点遍历。找不到打印一次诊断。</summary>
        static object FindPlantFireComponent(Node2D n)
        {
            try
            {
                if (_fireCompType == null) _fireCompType = FindType("FireComponent");
                if (_fireCompType == null) { if (!_fcDiag) { _fcDiag = true; Bootstrap.Log("植物射速: 找不到 FireComponent 类型"); } return null; }
                var cm = FindFieldVal(n, "componentManager");
                if (cm != null)
                {
                    var c = FindAttackComponent(cm, _fireCompType);
                    if (c != null) return c;
                }
                var f = FindFieldVal(n, "fireComponent");
                if (f == null) f = FindFieldVal(n, "_fireComponent");
                if (f != null && GodotObject.IsInstanceValid((GodotObject)f)) return f;
                foreach (var child in n.GetChildren(true))
                {
                    try
                    {
                        if (child != null && GodotObject.IsInstanceValid(child))
                        {
                            if (_fireCompType.IsInstanceOfType(child)) return child;
                            var sub = FindFireComponentRec(child);
                            if (sub != null) return sub;
                        }
                    }
                    catch { }
                }
                if (!_fcDiag) { _fcDiag = true; Bootstrap.Log("植物射速: 找不到 FireComponent 植物=" + n.GetType().Name + " cm=" + (cm != null)); }
            }
            catch { }
            return null;
        }

        /// <summary>递归查找所有后代节点的 FireComponent（组件可能嵌套在子节点下）。</summary>
        static object FindFireComponentRec(Node node)
        {
            try
            {
                foreach (var c in node.GetChildren(true))
                {
                    if (c == null || !GodotObject.IsInstanceValid(c)) continue;
                    if (_fireCompType.IsInstanceOfType(c)) return c;
                    var sub = FindFireComponentRec(c);
                    if (sub != null) return sub;
                }
            }
            catch { }
            return null;
        }

        /// <summary>设置 fireInterval 属性（.NET 9 下 PropertyInfo.SetValue 抛 MissingMethodException，必须用 setter Invoke）。</summary>
        static void SetFireIntervalVal(object n, System.Reflection.PropertyInfo fp, double val)
        {
            try
            {
                var setter = fp.GetSetMethod(true);
                if (setter != null) setter.Invoke(n, new object[] { val });
                else fp.SetValue(n, val);
            }
            catch { }
        }

        static bool _chompLogged;
        /// <summary>大嘴花秒吞咽：把 ChomperComponent.chewTimer 顶到 &gt;= currentChewTime，强制立即完成咀嚼进入吞咽。</summary>
        static void ApplyChomperFastSwallow(Node2D n)
        {
            try
            {
                if (!n.GetType().Name.Contains("Chomper")) return;
                var cf = n.GetType().GetField("_chomperComponent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cf == null) cf = n.GetType().GetField("chomperComponent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cf == null) return;
                var comp = cf.GetValue(n);
                if (comp == null) return;
                var t = comp.GetType();
                var chewT = t.GetField("chewTimer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var curT = t.GetField("currentChewTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (chewT == null || curT == null) return;
                float cur = (float)curT.GetValue(comp);
                float now = (float)chewT.GetValue(comp);
                if (now < cur) chewT.SetValue(comp, cur + 1f);   // 强制完成咀嚼 → 立即吞咽
            }
            catch (System.Exception ex) { if (!_chompLogged) { _chompLogged = true; Bootstrap.Log("大嘴花异常: " + ex.Message); } }
        }

        /// <summary>从 ComponentManager 反射获取指定组件实例：优先遍历 _runtimeByInstanceId 字典，其次无参 GetRuntime&lt;T&gt;。</summary>
        static object FindAttackComponent(object cm, Type attackType)
        {
            try
            {
                var dict = FindFieldVal(cm, "_runtimeByInstanceId");
                if (dict is System.Collections.IDictionary idict)
                {
                    foreach (System.Collections.DictionaryEntry e in idict)
                    {
                        if (e.Value != null && attackType.IsInstanceOfType(e.Value))
                            return e.Value;
                    }
                }
            }
            catch { }
            try
            {
                var gr = cm.GetType().GetMethod("GetRuntime", Type.EmptyTypes);
                if (gr != null) return gr.MakeGenericMethod(attackType).Invoke(cm, null);
            }
            catch { }
            return null;
        }

        /// <summary>血量倍率：instance.hitpointScale 倍率。无敌由 IL 补丁（伤害归零）负责，这里不再顶血。</summary>
        static void ApplyHP(Node2D n, float mult, bool invincible)
        {
            try
            {
                var inst = FindFieldVal(n, "instance");
                if (inst == null || !GodotObject.IsInstanceValid((GodotObject)inst)) return;
                var t = inst.GetType();
                if (mult != 1.0f)
                {
                    var sp = t.GetProperty("hitpointScale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sp != null && sp.PropertyType == typeof(double))
                    {
                        double cur = (double)sp.GetValue(inst);
                        double target = Math.Max(1.0, (double)mult);
                        if (Math.Abs(cur - target) > 0.01)
                        {
                            var setter = sp.GetSetMethod(true);
                            if (setter != null) setter.Invoke(inst, new object[] { target });
                        }
                    }
                }
                // 无敌由 IL 补丁（ScaleDamageInstance/IsDamageBlockedInstance 返回 0）负责，不顶血避免血条显示 10 亿
            }
            catch (System.Exception ex) { if (!_hpLogged) { _hpLogged = true; Bootstrap.Log("血量异常: " + ex.Message); } }
        }

        /// <summary>沿基类链反射取字段值。</summary>
        static object FindFieldVal(object obj, string name)
        {
            try
            {
                var t = obj.GetType();
                while (t != null)
                {
                    var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) return f.GetValue(obj);
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        // ===== 刷怪/刷卡：枚举所有植物/僵尸 ID + 三种刷出方式 =====

        static MethodInfo _getBankData;
        static MethodInfo _getPlantList;
        static MethodInfo _getZombieList;
        static MethodInfo _getPacketConfig;
        static MethodInfo _addPacket;
        static MethodInfo _spawnPacket;
        static MethodInfo _getMapCellPlantPos;
        static MethodInfo _plant;

        /// <summary>枚举所有植物或僵尸 ID（TowerDefensePacketBankData.GetPlantList/GetZombieList）。</summary>
        public static System.Collections.Generic.List<string> GetPacketIds(bool plant)
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return list;
                if (_getBankData == null)
                    _getBankData = _tdmType.GetMethod("GetPacketBankData", new Type[] { typeof(string) });
                if (_getBankData == null) return list;
                var bank = _getBankData.Invoke(null, new object[] { plant ? "GeneralPlant" : "GeneralZombie" });
                if (bank == null) { Bootstrap.Log("获取卡包ID失败: GetPacketBankData 返回 null"); return list; }
                object result = null;
                if (plant)
                {
                    if (_getPlantList == null) _getPlantList = bank.GetType().GetMethod("GetPlantList", Type.EmptyTypes);
                    if (_getPlantList == null) return list;
                    result = _getPlantList.Invoke(bank, null);
                }
                else
                {
                    // 僵尸：遍历所有卡包收集 GetZombieList()（不依赖固定卡包名，"GeneralZombie" 可能不是卡包 key 导致返回 null）
                    if (_rmType == null) _rmType = FindType("ResourceManager");
                    object rmInst = null;
                    if (_rmType != null)
                    {
                        try
                        {
                            // 坑：Instance 是 public static 字段（不是属性）——GetProperty 会返回 null 导致僵尸列表为 0
                            var fld = _rmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                            if (fld != null) rmInst = fld.GetValue(null);
                            if (rmInst == null)
                            {
                                var p = _rmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                                if (p != null) rmInst = p.GetValue(null);
                            }
                        }
                        catch { }
                    }
                    if (rmInst == null) { Bootstrap.Log("获取卡包ID: ResourceManager.Instance 为空"); return list; }
                    var banksProp = rmInst.GetType().GetProperty("TOWERDEFENSE_PACKETBANKS", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (banksProp == null) { Bootstrap.Log("获取卡包ID: 找不到 TOWERDEFENSE_PACKETBANKS"); return list; }
                    var banks = banksProp.GetValue(rmInst);
                    if (banks is System.Collections.IDictionary bdict)
                    {
                        foreach (var bk in bdict.Keys)
                        {
                            try
                            {
                                if (bk == null) continue;
                                var bank2 = _getBankData.Invoke(null, new object[] { bk });
                                if (bank2 == null) continue;
                                var zl = bank2.GetType().GetMethod("GetZombieList", Type.EmptyTypes);
                                if (zl == null) continue;
                                var r = zl.Invoke(bank2, null);
                                if (r is System.Collections.IEnumerable en2)
                                {
                                    foreach (var item in en2)
                                    {
                                        if (item == null) continue;
                                        string s = item is string str ? str : item.ToString();
                                        if (s.Length > 0 && !list.Contains(s)) list.Add(s);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                // 用 IEnumerable 遍历（合并后 Godot.Collections.Array<string> 类型用 as 转换会失败，导致列表为 0）
                if (result is System.Collections.IEnumerable en)
                {
                    foreach (var item in en)
                    {
                        if (item == null) continue;
                        string s = item is string str ? str : item.ToString();
                        if (s.Length > 0) list.Add(s);
                    }
                }
                Bootstrap.Log("获取卡包ID: " + (plant ? "植物" : "僵尸") + " 数量=" + list.Count);
            }
            catch { }
            return list;
        }

        /// <summary>添加到物品栏：TowerDefenseManager.AddPacket(packetName)。
        /// charm=true 时卡槽里的卡带魅惑（种下自动反转阵营）：AddPacket 内部会 Duplicate config（复制 overrideHypnoses），
        /// 所以临时设共享 config.overrideHypnoses=true → AddPacket → 恢复即可（卡槽副本已持有 true）。</summary>
        public static bool AddPacketToInventory(string id, bool charm = false)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return false;
                if (_addPacket == null)
                    _addPacket = tdm.GetType().GetMethod("AddPacket");   // AddPacket 只有 1 个重载（可选参），GetMethod 按名即可
                if (_addPacket == null) return false;
                if (charm)
                {
                    var cfg = GetConfig(id);
                    if (cfg != null)
                    {
                        SetConfigHypnoses(cfg, true);
                        _addPacket.Invoke(tdm, new object[] { id, null });
                        SetConfigHypnoses(cfg, false);   // 恢复共享 config；卡槽里的 Duplicate 副本已复制 true
                        return true;
                    }
                }
                _addPacket.Invoke(tdm, new object[] { id, null });
                return true;
            }
            catch { return false; }
        }

        /// <summary>刷出卡牌掉落：SpawnPacket(config, 格子世界坐标, aliveTime, isFall)。
        /// charm=true 时掉落的卡带魅惑：SpawnPacket 里掉落卡牌持有传入的 config 引用（不 Duplicate），
        /// 所以用一份 Duplicate 副本（overrideHypnoses=true）传给 SpawnPacket，不影响共享 config。</summary>
        public static bool SpawnPacketToScene(string id, Vector2I gridPos, bool charm = false)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) { Bootstrap.Log("刷卡牌失败: 无 TDM 实例"); return false; }
                var cfg = GetConfig(id);
                if (cfg == null) { Bootstrap.Log("刷卡牌失败: GetConfig(" + id + ") 为空"); return false; }
                // 每次生成用独立 config 副本（共享 config 内部状态可能导致"刷全场只刷一个"）
                try
                {
                    var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                    if (dm != null) { var dup = dm.Invoke(cfg, new object[] { true }); if (dup != null) cfg = dup; }
                }
                catch { }
                if (charm)
                {
                    var dup = DuplicateConfig(cfg);
                    if (dup != null)
                    {
                        SetConfigHypnoses(dup, true);
                        cfg = dup;
                    }
                }
                if (_getMapCellPlantPos == null)
                    _getMapCellPlantPos = _tdmType.GetMethod("GetMapCellPlantPos", new Type[] { typeof(Vector2I) });
                if (_getMapCellPlantPos == null) { Bootstrap.Log("刷卡牌失败: 找不到 GetMapCellPlantPos"); return false; }
                var pos = (Vector2)_getMapCellPlantPos.Invoke(null, new object[] { gridPos });
                if (_spawnPacket == null)
                    _spawnPacket = FindMethodByPrefix(tdm.GetType(), "SpawnPacket", new Type[] { cfg.GetType(), typeof(Vector2), typeof(double), typeof(bool) });
                if (_spawnPacket == null) { Bootstrap.Log("刷卡牌失败: 找不到 SpawnPacket 方法"); return false; }
                _spawnPacket.Invoke(tdm, FillArgs(_spawnPacket, new object[] { cfg, pos, 30.0, false }));
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("刷卡牌异常: " + ex.Message); return false; }
        }

        /// <summary>设置 config.overrideHypnoses（魅惑标记，GetHypnoses() 返回它）。</summary>
        static void SetConfigHypnoses(object cfg, bool val)
        {
            try
            {
                var f = cfg.GetType().GetField("overrideHypnoses", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(bool)) f.SetValue(cfg, val);
            }
            catch { }
        }

        /// <summary>Duplicate 一份 config（Resource.Duplicate(true)），失败返回 null。</summary>
        static object DuplicateConfig(object cfg)
        {
            try
            {
                var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                if (dm != null) return dm.Invoke(cfg, new object[] { true });
            }
            catch { }
            return null;
        }

        /// <summary>刷出实物：TowerDefensePacketConfig.Plant(gridPos, playAudio, noLimit, ...)。
        /// charm=true 时刷出魅惑阵营：设置 config.overrideHypnoses=true 后 Plant——Plant 的 deferred 初始化
        /// （&lt;Plant&gt;b__0）里 if(GetHypnoses()) 会自动调用 character.Hypnoses() 做完整的魅惑处理
        /// （反转 camp + 转向 + 清 target），比手动改 camp 可靠（手动改在角色未初始化时拿不到正确 camp）。</summary>
        public static bool SpawnCharacter(string id, Vector2I gridPos, bool charm = false)
        {
            try
            {
                var cfg = GetConfig(id);
                if (cfg == null) { Bootstrap.Log("刷实物失败: GetConfig(" + id + ") 为空"); return false; }
                // 每次种植用独立 config 副本（共享 config 内部状态会导致"刷全场自选只刷一个"——Plant 可能记录已种位置/状态）
                try
                {
                    var dm = cfg.GetType().GetMethod("Duplicate", new Type[] { typeof(bool) });
                    if (dm != null) { var dup = dm.Invoke(cfg, new object[] { true }); if (dup != null) cfg = dup; }
                }
                catch { }
                if (_plant == null)
                    _plant = FindMethodByPrefix(cfg.GetType(), "Plant", new Type[] { typeof(Vector2I), typeof(bool), typeof(bool) });
                if (_plant == null) { Bootstrap.Log("刷实物失败: 找不到 Plant 方法"); return false; }
                // 魅惑：临时设 overrideHypnoses=true，让原生 Plant 流程自动 Hypnoses（正确处理初始化时序）
                object origHypnoses = null;
                bool hypnosesSet = false;
                if (charm)
                {
                    try
                    {
                        var hf = cfg.GetType().GetField("overrideHypnoses", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (hf != null && hf.FieldType == typeof(bool))
                        {
                            origHypnoses = hf.GetValue(cfg);
                            hf.SetValue(cfg, true);
                            hypnosesSet = true;
                        }
                    }
                    catch { }
                }
                object ret = null;
                try { ret = _plant.Invoke(cfg, FillArgs(_plant, new object[] { gridPos, true, true })); } catch { }
                if (hypnosesSet)
                {
                    try
                    {
                        var hf = cfg.GetType().GetField("overrideHypnoses", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (hf != null) hf.SetValue(cfg, origHypnoses ?? false);
                    }
                    catch { }
                }
                if (charm)
                {
                    Bootstrap.Log("刷实物魅惑: " + id + " overrideHypnoses=" + hypnosesSet + " ret=" + (ret != null ? ret.GetType().Name : "null"));
                    // 兜底：若角色已就绪（非 deferred 场景），直接调 Hypnoses 补一次
                    if (ret is Node2D n2 && GodotObject.IsInstanceValid(n2))
                    {
                        try
                        {
                            var hm = n2.GetType().GetMethod("Hypnoses", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (hm != null)
                            {
                                var ps = hm.GetParameters();
                                hm.Invoke(n2, FillArgs(hm, new object[] { -1.0, true }));
                            }
                        }
                        catch { }
                    }
                }
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("刷实物异常: " + ex.Message); return false; }
        }

        /// <summary>铲子铲植物 hook：由 patcher 注入到 TowerDefenseCellInstance.Shovel 开头。
        /// ShovelCherry 开时，在随机一个僵尸脚下生成樱桃炸弹（每次铲植物触发）。</summary>
        public static void OnCellShovel(object cell)
        {
            try
            {
                if (!ModSettings.ShovelCherry) return;
                var cid = FindCherryId();
                if (cid.Length == 0) { if (!_cherryIdLogged) { _cherryIdLogged = true; Bootstrap.Log("铲子樱桃: 未找到樱桃卡ID"); } return; }
                Node2D zombie = PickRandomZombie();
                if (zombie == null)
                {
                    // 兜底：临时从场景树里找一个僵尸
                    try
                    {
                        if (cell is Node co)
                        {
                            var tree = co.GetTree();
                            if (tree != null && tree.Root != null) zombie = FindAnyZombie(tree.Root);
                        }
                    }
                    catch { }
                }
                if (zombie == null || !GodotObject.IsInstanceValid(zombie)) return;
                var grid = FindPropOrFieldVal(zombie, "gridPos");
                if (grid is Vector2I gp && gp.X >= 1)
                {
                    SpawnCharacter(cid, gp);
                    Bootstrap.Log("铲子樱桃: 在僵尸脚下 " + gp + " 生成 " + cid);
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("铲子樱桃异常: " + ex.Message); }
        }

        static string _cherryId;
        static bool _cherryIdLogged;
        /// <summary>从植物列表里找樱桃炸弹卡 ID（CherryBomb / 各种樱桃）。</summary>
        static string FindCherryId()
        {
            if (_cherryId != null) return _cherryId;
            _cherryId = "CherryBomb";   // 默认樱桃炸弹（游戏目录 res://Asset/.../CherryBomb/）
            try
            {
                var ids = GetPacketIds(true);
                foreach (var id in ids)
                {
                    if (id.IndexOf("Cherry", System.StringComparison.OrdinalIgnoreCase) >= 0) { _cherryId = id; return id; }
                }
            }
            catch { }
            return _cherryId;
        }

        /// <summary>从共享僵尸缓存随机挑一个活着的僵尸（缓存 60 帧刷新）。</summary>
        static Node2D PickRandomZombie()
        {
            try
            {
                if (_zombieCache.Count == 0) return null;
                for (int i = 0; i < 30; i++)
                {
                    var z = _zombieCache[_bulletRand.Next(_zombieCache.Count)];
                    if (z != null && GodotObject.IsInstanceValid(z)) return z;
                }
            }
            catch { }
            return null;
        }

        /// <summary>遍历树找一个僵尸（兜底）。</summary>
        static Node2D FindAnyZombie(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && GodotObject.IsInstanceValid(n2) && ESP.GetCamp(n2) == "Zombie")
                        return n2;
                }
                catch { }
                var r = FindAnyZombie(child);
                if (r != null) return r;
            }
            return null;
        }

        static object GetConfig(string id)
        {
            try
            {
                if (_tdmType == null) { GetTdmInstance(); if (_tdmType == null) return null; }
                if (_getPacketConfig == null)
                    _getPacketConfig = _tdmType.GetMethod("GetPacketConfig", new Type[] { typeof(string) });
                if (_getPacketConfig == null) return null;
                return _getPacketConfig.Invoke(null, new object[] { id });
            }
            catch { return null; }
        }

        /// <summary>按方法名 + 参数前缀匹配查找方法（处理可选参数导致 GetMethod 精确匹配失败）。</summary>
        static System.Reflection.MethodInfo FindMethodByPrefix(Type t, string name, Type[] prefixTypes)
        {
            try
            {
                foreach (var mi in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
                {
                    if (mi.Name != name) continue;
                    var ps = mi.GetParameters();
                    if (ps.Length < prefixTypes.Length) continue;
                    bool ok = true;
                    for (int i = 0; i < prefixTypes.Length; i++)
                        if (ps[i].ParameterType != prefixTypes[i]) { ok = false; break; }
                    if (ok) return mi;
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("FindMethodByPrefix(" + name + ") 异常: " + ex.Message); }
            return null;
        }

        /// <summary>精确匹配方法（参数个数+类型完全一致，跳过泛型重载，含非 public）。
        /// 用途：GetMethod(name, types) 在泛型+非泛型重载并存时抛 AmbiguousMatchException（如 GetFeature(StringName) vs GetFeature&lt;T&gt;(StringName)），
        /// 且默认 BindingFlags 找不到 internal 方法（如 BulletField.TryChangeBulletDataInPlace）。</summary>
        static System.Reflection.MethodInfo FindMethodExact(Type t, string name, Type[] paramTypes)
        {
            try
            {
                foreach (var mi in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
                {
                    if (mi.Name != name) continue;
                    if (mi.IsGenericMethodDefinition) continue;   // 跳过泛型重载（GetFeature<T> 等）
                    var ps = mi.GetParameters();
                    if (ps.Length != paramTypes.Length) continue;
                    bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                        if (ps[i].ParameterType != paramTypes[i]) { ok = false; break; }
                    if (ok) return mi;
                }
            }
            catch { }
            return null;
        }

        /// <summary>用头部实参填充完整参数列表（可选参数用默认值）。</summary>
        static object[] FillArgs(System.Reflection.MethodInfo mi, object[] head)
        {
            var ps = mi.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < head.Length) args[i] = head[i];
                else args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
            }
            return args;
        }

        // ===== 生成货币 / 一键启动小推车 =====

        static MethodInfo _coinCreate;
        static MethodInfo _ybCreate;
        static MethodInfo _goldCreate;
        static MethodInfo _getMower;
        static MethodInfo _mowerRun;
        static Type _objectEnumType;
        static MethodInfo _foic;
        static MethodInfo _getCharNode;

        /// <summary>无限水晶：把 KeyValue["CrystalNum"] 设为 10 亿（在线关卡兑换货币）。
        /// 水晶 = 通关 OnlineLevel 数，由 GameSaveManager.RefreshCrystalNum 在 Load/SetUserCurrent 时重算，
        /// 所以这里定时检查，被重算回真实值就再设回 10 亿并保存。</summary>
        static void ApplyCrystalInfinite()
        {
            try
            {
                var gsmType = FindType("GameSaveManager");
                if (gsmType == null) return;
                object inst = null;
                var instF = gsmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instF != null) inst = instF.GetValue(null);
                if (inst == null) return;
                var getKv = gsmType.GetMethod("GetKeyValue", new Type[] { typeof(string) });
                if (getKv == null) return;
                long cur = 0;
                try
                {
                    var v = getKv.Invoke(inst, new object[] { "CrystalNum" });
                    if (v is Godot.Variant gv && gv.VariantType == Variant.Type.Int) cur = gv.AsInt64();
                    else if (v is long lv) cur = lv;
                    else if (v is int iv) cur = iv;
                }
                catch { }
                if (cur >= 1000000000L) return;   // 已够，不重复写档
                var setKv = gsmType.GetMethod("SetKeyValue", new Type[] { typeof(string), typeof(Godot.Variant) });
                if (setKv == null) return;
                setKv.Invoke(inst, new object[] { "CrystalNum", Godot.Variant.From(1000000000L) });
                try
                {
                    var save = gsmType.GetMethod("SaveGameConfig", Type.EmptyTypes);
                    if (save == null) save = gsmType.GetMethod("Save", Type.EmptyTypes);
                    save?.Invoke(inst, null);
                }
                catch { }
                Bootstrap.Log("无限水晶: CrystalNum 已设为 1000000000 (原=" + cur + ")");
            }
            catch (System.Exception ex) { Bootstrap.Log("无限水晶异常: " + ex.Message); }
        }

        /// <summary>生成指定货币到关卡中部：type=Silver银币 / Gold金币 / Diamond钻石，num 数量。
        /// 用 FallingObjectItemCreate 直接指定对象类型（CoinCreate 的 num 是面值，会按比例拆分成不同币种）。</summary>
        public static void CreateCurrency(string type, int num)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) { Bootstrap.Log("货币失败: 无 TowerDefenseManager 实例（需在关卡内）"); return; }
                // 必须在真实战斗场景内才能刷——主菜单/选关界面点按钮会触发游戏资源加载主线程死锁 → 卡死
                var curCtrlF = _tdmType.GetField("currentControl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var curCtrl = curCtrlF != null ? curCtrlF.GetValue(tdm) : null;
                if (curCtrl == null)
                {
                    try
                    {
                        var cp = _tdmType.GetProperty("CurrentControl", BindingFlags.Public | BindingFlags.Static);
                        curCtrl = cp != null ? cp.GetValue(null) : null;
                    }
                    catch { }
                }
                if (curCtrl == null) { Bootstrap.Log("货币失败: 当前不在关卡内（请进入关卡再刷钱袋/奖杯/货币）"); return; }
                if (num <= 0) num = 1;
                if (num > 200) num = 200;
                // ObjectManagerConfig.OBJECT 枚举类型
                if (_objectEnumType == null)
                {
                    var objCfg = FindType("ObjectManagerConfig");
                    if (objCfg != null) _objectEnumType = objCfg.GetNestedType("OBJECT");
                }
                if (_objectEnumType == null) { Bootstrap.Log("货币失败: 找不到 OBJECT 枚举"); return; }
                string enumName = type == "Silver" ? "COIN_SILVER" : (type == "Gold" ? "COIN_GOLD" : (type == "LuckyBag" ? "COIN_LUCKY_BAG" : "COIN_DIAMOND"));
                object enumVal;
                try { enumVal = System.Enum.Parse(_objectEnumType, enumName); }
                catch { Bootstrap.Log("货币失败: 枚举 " + enumName + " 不存在"); return; }
                // FallingObjectItemCreate(ObjectManagerConfig.OBJECT, Vector2, double, Vector2, double)
                if (_foic == null)
                    _foic = tdm.GetType().GetMethod("FallingObjectItemCreate", new Type[] { _objectEnumType, typeof(Vector2), typeof(double), typeof(Vector2), typeof(double) });
                if (_foic == null) { Bootstrap.Log("货币失败: 找不到 FallingObjectItemCreate"); return; }
                if (_getMapCellPlantPos == null)
                    _getMapCellPlantPos = _tdmType.GetMethod("GetMapCellPlantPos", new Type[] { typeof(Vector2I) });
                if (_getMapCellPlantPos == null) { Bootstrap.Log("货币失败: 找不到 GetMapCellPlantPos"); return; }
                var pos = (Vector2)_getMapCellPlantPos.Invoke(null, new object[] { new Vector2I(4, 2) });
                if (_getCharNode == null) _getCharNode = _tdmType.GetMethod("GetCharacterNode", Type.EmptyTypes);
                var cn = _getCharNode != null ? _getCharNode.Invoke(tdm, null) : null;
                int ok = 0;
                for (int i = 0; i < num; i++)
                {
                    var item = _foic.Invoke(tdm, new object[] { enumVal, pos, 0.0, default(Vector2), 0.0 });
                    if (item is GodotObject g && GodotObject.IsInstanceValid(g))
                    {
                        if (cn != null)
                        {
                            var reparent = g.GetType().GetMethod("Reparent", new Type[] { typeof(Node), typeof(bool) });
                            if (reparent != null) reparent.Invoke(g, new object[] { cn, false });
                        }
                        ok++;
                    }
                }
                Bootstrap.Log("生成货币: " + enumName + " x" + ok + " @ " + pos);
            }
            catch (System.Exception ex) { Bootstrap.Log("货币异常: " + ex.Message); }
        }

        /// <summary>生成游戏原生奖杯/钱袋本体（Award 系统场景）：sceneGetter = get_TOWER_DEFENSE_AWARD_TROPHY(奖杯) / ..._PURSE(钱袋)。
        /// 直接 Instantiate + 挂场景树根（任何场景都显示，不再依赖 GetCharacterNode/currentControl）。</summary>
        public static void SpawnAward(string sceneGetter, string tag)
        {
            try
            {
                var tree = Godot.Engine.GetMainLoop() as SceneTree;
                var root = tree != null ? tree.Root : null;
                if (root == null) { Bootstrap.Log("奖杯失败: 无场景树"); return; }
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) { Bootstrap.Log("奖杯失败: 无 TowerDefenseManager 实例"); return; }
                var getter = _tdmType.GetMethod(sceneGetter, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (getter == null) { Bootstrap.Log("奖杯失败: 找不到 " + sceneGetter); return; }
                var scene = getter.Invoke(null, null) as Godot.PackedScene;
                if (scene == null) { Bootstrap.Log("奖杯失败: 场景为空 " + sceneGetter); return; }
                var node = scene.Instantiate();
                if (node == null) { Bootstrap.Log("奖杯失败: Instantiate 失败"); return; }
                // 父节点：优先当前激活场景（局内=战斗场景，Award 局内一定显示），兜底 GetCharacterNode/场景树根
                Node parent = null;
                var st = Godot.Engine.GetMainLoop() as SceneTree;
                if (st != null && st.CurrentScene != null && GodotObject.IsInstanceValid(st.CurrentScene))
                    parent = st.CurrentScene;
                if (parent == null)
                {
                    if (_getCharNode == null) _getCharNode = _tdmType.GetMethod("GetCharacterNode", Type.EmptyTypes);
                    if (_getCharNode != null)
                    {
                        var cnObj = _getCharNode.Invoke(tdm, null);
                        if (cnObj is Godot.Node cn && GodotObject.IsInstanceValid(cn)) parent = cn;
                    }
                }
                if (parent == null) parent = root;
                parent.CallDeferred("add_child", node);
                if (node is Godot.Node2D n2)
                {
                    if (_getMapCellPlantPos == null)
                        _getMapCellPlantPos = _tdmType.GetMethod("GetMapCellPlantPos", new Type[] { typeof(Vector2I) });
                    var pos = _getMapCellPlantPos != null ? (Vector2)_getMapCellPlantPos.Invoke(null, new object[] { new Vector2I(4, 2) }) : new Vector2(400f, 300f);
                    n2.GlobalPosition = pos;
                    Bootstrap.Log("奖杯: 已生成 " + sceneGetter + " @ " + pos + " 节点=" + node.GetType().Name);
                }
                else Bootstrap.Log("奖杯: 节点非 Node2D: " + node.GetType().Name);
                var init = node.GetType().GetMethod("Init", new Type[] { typeof(string) });
                try { init?.Invoke(node, new object[] { tag ?? "" }); } catch { }
            }
            catch (System.Exception ex) { Bootstrap.Log("奖杯/钱袋异常: " + ex.Message); }
        }

        /// <summary>一键启动所有小推车（割草机）：遍历 GetMower() 调用 Run()。</summary>
        public static int LaunchAllMowers()
        {
            int count = 0;
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return 0;
                if (_getMower == null) _getMower = _tdmType.GetMethod("GetMower", Type.EmptyTypes);
                if (_getMower == null) return 0;
                var arr = _getMower.Invoke(tdm, null) as Godot.Collections.Array;
                if (arr == null) return 0;
                for (int i = 0; i < arr.Count; i++)
                {
                    var m = arr[i].AsGodotObject();
                    if (m == null || !GodotObject.IsInstanceValid(m)) continue;
                    if (_mowerRun == null) _mowerRun = m.GetType().GetMethod("Run", Type.EmptyTypes);
                    if (_mowerRun != null) { _mowerRun.Invoke(m, null); count++; }
                }
                Bootstrap.Log("启动小推车: " + count + " 辆");
            }
            catch { }
            return count;
        }

        /// <summary>彻底移除全部小推车：从场景树遍历删（游戏自带 RemoveAllMowers 只删 mowerLine 里引用的小推车，
        /// 启动后的小推车已从 mowerLine 移除、删不到），再清空 mowerFeature.mowerLine 引用避免 RestoreAllMowers 跳过重建。</summary>
        public static void RemoveAllMowers(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                int n = 0;
                RemoveMowerWalk(start, ref n);
                // 清空 mowerFeature.mowerLine 引用
                try
                {
                    var tdm = GetTdmInstance();
                    if (tdm != null && _tdmType != null)
                    {
                        var cc = FindPropOrFieldVal(tdm, "currentControl");
                        if (cc != null)
                        {
                            var getFeat = FindMethodExact(cc.GetType(), "GetFeature", new Type[] { typeof(Godot.StringName) });
                            var mf = getFeat != null ? getFeat.Invoke(cc, new object[] { new Godot.StringName("Mower") }) : null;
                            if (mf != null)
                            {
                                var ml = FindPropOrFieldVal(mf, "mowerLine");
                                if (ml is System.Collections.IList il)
                                    for (int i = 0; i < il.Count; i++) il[i] = null;
                            }
                        }
                    }
                }
                catch { }
                Bootstrap.Log("清理小推车: 移除 " + n + " 辆");
            }
            catch { }
        }

        static void RemoveMowerWalk(Node node, ref int n)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child.GetType().Name.StartsWith("TowerDefenseMower") && GodotObject.IsInstanceValid(child))
                    {
                        child.QueueFree();
                        n++;
                    }
                }
                catch { }
                RemoveMowerWalk(child, ref n);
            }
        }

        /// <summary>恢复全部小推车（自研，确定重建）：删掉场上小推车 + 清空 mowerLine + 按使用行 CreateMower 重建。
        /// 官方 RestoreAllMowers 只在 mowerLine[row] 无效时重建——小推车启动/用掉后引用仍有效 → 不重建 → 用户"恢复没用"。</summary>
        public static void RestoreAllMowers()
        {
            int done = 0;
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) { Bootstrap.Log("恢复小推车: 无 TDM"); return; }
                object cc = FindFieldVal(tdm, "currentControl");
                if (cc == null) { Bootstrap.Log("恢复小推车: 无当前关卡"); return; }
                // GetFeature 有泛型 GetFeature<T> 重载，GetMethod 会 AmbiguousMatchException —— 必须用 FindMethodExact
                var gf = FindMethodExact(cc.GetType(), "GetFeature", new Type[] { typeof(Godot.StringName) });
                var mf = gf != null ? gf.Invoke(cc, new object[] { new Godot.StringName("Mower") }) : null;
                if (mf == null) { Bootstrap.Log("恢复小推车: 无 Mower feature"); return; }
                // 1) 删掉场上已有小推车（防重复）
                var root = _lastRoot;
                if (root != null && GodotObject.IsInstanceValid(root))
                {
                    int n = 0;
                    RemoveMowerWalk(root, ref n);
                }
                // 2) 清空 mowerLine 引用（强制重建——Godot Array<T> 可能不实现非泛型 IList，需通用清空）
                var ml = FindPropOrFieldVal(mf, "mowerLine");
                ClearMowerLine(ml);
                // 3) 按使用行重建
                var createM = FindMethodByPrefix(mf.GetType(), "CreateMower", new Type[] { typeof(int), typeof(int) });
                if (createM == null) { Bootstrap.Log("恢复小推车: 找不到 CreateMower"); return; }
                int gridNum = 5;
                var mapF = _tdmType.GetMethod("GetMapFeature", Type.EmptyTypes);
                object mapFeat = mapF != null ? mapF.Invoke(null, null) : null;
                if (mapFeat != null)
                {
                    var cfg = FindPropOrFieldVal(mapFeat, "config");
                    var gn = FindPropOrFieldVal(cfg, "gridNum");
                    if (gn is Godot.Vector2I gv && gv.Y > 0 && gv.Y <= 20) gridNum = gv.Y;
                }
                var getLineUse = _tdmType.GetMethod("GetMapLineUse", new Type[] { typeof(int) });
                Bootstrap.Log("恢复小推车: gridNum=" + gridNum + " mowerLine.Count=" + GetCollectionCount(ml) + " GetFeature=" + (mf != null));
                for (int j = 1; j <= gridNum; j++)
                {
                    bool lineUse = false;
                    try { if (getLineUse != null) lineUse = (bool)getLineUse.Invoke(tdm, new object[] { j }); } catch { }
                    if (!lineUse) continue;
                    try
                    {
                        var ret = createM.Invoke(mf, new object[] { j, -1 });
                        if (ret != null) done++;
                        else Bootstrap.Log("恢复小推车: 行" + j + " 创建失败(CreateMower返回null)");
                    }
                    catch (System.Exception ex) { Bootstrap.Log("恢复小推车: 行" + j + " 异常 " + ex.Message); }
                }
                Bootstrap.Log("恢复小推车: 成功 " + done + " 辆");
            }
            catch (System.Exception ex) { Bootstrap.Log("恢复小推车异常: " + ex.Message); }
        }

        /// <summary>通用清空集合元素为 null（Godot Array&lt;T&gt; 可能不实现非泛型 IList）。</summary>
        static void ClearMowerLine(object ml)
        {
            try
            {
                if (ml == null) return;
                if (ml is System.Collections.IList il)
                {
                    for (int i = 0; i < il.Count; i++) il[i] = null;
                    return;
                }
                var t = ml.GetType();
                var countP = t.GetProperty("Count", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var idxP = t.GetProperty("Item", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (countP == null || idxP == null) return;
                int count = (int)countP.GetValue(ml);
                for (int i = 0; i < count; i++)
                    idxP.SetValue(ml, null, new object[] { i });
            }
            catch { }
        }

        static int GetCollectionCount(object ml)
        {
            try
            {
                if (ml == null) return -1;
                if (ml is System.Collections.ICollection c) return c.Count;
                var countP = ml.GetType().GetProperty("Count", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (countP != null) return (int)countP.GetValue(ml);
            }
            catch { }
            return -1;
        }

        /// <summary>诊断小推车/僵尸状态：打印每辆小推车的 run/hitbox/canStart/camp 和僵尸 camp/die，
        /// 用于定位"僵尸无视小推车"（小推车不杀僵尸）是哪一环断了。</summary>
        public static void DiagnoseMowers(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                var lines = new System.Collections.Generic.List<string>();
                CollectMowerStates(start, lines);
                Bootstrap.Log("小推车诊断: 共 " + lines.Count + " 辆");
                for (int i = 0; i < lines.Count; i++) Bootstrap.Log("  小推车: " + lines[i]);
                var zs = new System.Collections.Generic.List<string>();
                CollectZombieStates(start, zs);
                Bootstrap.Log("僵尸诊断: 共 " + zs.Count + " 只");
                for (int i = 0; i < zs.Count; i++) Bootstrap.Log("  僵尸: " + zs[i]);
                var tdm = GetTdmInstance();
                if (tdm != null && _tdmType != null)
                {
                    var m = _tdmType.GetMethod("IsGameRunning", Type.EmptyTypes);
                    if (m != null) { try { Bootstrap.Log("IsGameRunning=" + m.Invoke(tdm, null)); } catch { } }
                }
            }
            catch (System.Exception ex) { Bootstrap.Log("小推车诊断异常: " + ex.Message); }
        }

        static void CollectMowerStates(Node node, System.Collections.Generic.List<string> lines)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && n2.GetType().Name.StartsWith("TowerDefenseMower"))
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append("run=" + FindPropOrFieldVal(n2, "run"));
                        sb.Append(" hitbox=" + FindPropOrFieldVal(n2, "IsHitBoxEnabled"));
                        var cs = n2.GetType().GetMethod("CanStartRun", Type.EmptyTypes);
                        if (cs != null) { try { sb.Append(" canStart=" + cs.Invoke(n2, null)); } catch { } }
                        var mhc = FindPropOrFieldVal(n2, "mowerHitComponent");
                        sb.Append(" hitComp=" + (mhc != null && GodotObject.IsInstanceValid((GodotObject)mhc)));
                        sb.Append(" camp=" + ESP.GetCamp(n2));
                        sb.Append(" X=" + System.Math.Round(n2.GlobalPosition.X, 1));
                        lines.Add(sb.ToString());
                    }
                }
                catch { }
                CollectMowerStates(child, lines);
            }
        }

        static void CollectZombieStates(Node node, System.Collections.Generic.List<string> lines)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && ESP.GetCamp(n2) == "Zombie")
                    {
                        var die = FindPropOrFieldVal(n2, "die");
                        var inst = FindPropOrFieldVal(n2, "instance");
                        var idie = inst != null ? FindPropOrFieldVal(inst, "die") : null;
                        lines.Add("camp=" + ESP.GetCamp(n2) + " die=" + die + "/" + idie + " X=" + System.Math.Round(n2.GlobalPosition.X, 1));
                    }
                }
                catch { }
                CollectZombieStates(child, lines);
            }
        }

        // ===== IL 补丁回调：由 patcher 注入到游戏方法里调用（不依赖每帧反射遍历） =====

        static bool _ilAttackLogged;
        static bool _ilScaleLogged;
        static bool _ilBlockLogged;
        static int _ilAttackLogTimer;
        static int _ilScaleLogTimer;
        static int _ilBlockLogTimer;
        static int _attackTick;
        static int _charmTick;
        static int _charmLogTimer;
        static int _fogTimer;
        static int _saveTimer;
        static bool _probeDone;

        static void Probe()
        {
            if (_probeDone) return;
            _probeDone = true;
            // 手机版无 D:\ 路径——写用户目录便于诊断注入是否触发（写失败静默忽略）
            try { System.IO.File.WriteAllText(@"mod\il_probe.txt", "IL callbacks executed\r\n"); } catch { }
        }

        /// <summary>攻速：被注入到 AttackComponent.Refresh，把 attackInterval 按阵营倍率缩放。</summary>
        public static double ScaleAttackInterval(object comp, double interval)
        {
            Probe();
            try
            {
                var parent = FindFieldVal(comp, "parent");
                if (parent is Node2D n2)
                {
                    var camp = ESP.GetCamp(n2);
                    if (!_ilAttackLogged)
                    {
                        _ilAttackLogged = true;
                        Bootstrap.Log("IL攻速触发: parent=" + parent.GetType().Name + " camp=" + (camp ?? "null") + " 植物倍率=" + ModSettings.PlantAttackSpeed + " 僵尸倍率=" + ModSettings.ZombieAttackSpeed);
                    }
                    if (camp == "Plant" && ModSettings.PlantAttackSpeed > 0.01f && ModSettings.PlantAttackSpeed != 1.0f)
                        return interval / ModSettings.PlantAttackSpeed;
                    if (camp == "Zombie" && ModSettings.ZombieAttackSpeed > 0.01f && ModSettings.ZombieAttackSpeed != 1.0f)
                        return interval / ModSettings.ZombieAttackSpeed;
                }
                else if (!_ilAttackLogged)
                {
                    _ilAttackLogged = true;
                    Bootstrap.Log("IL攻速: 找不到 parent " + (comp != null ? comp.GetType().Name : "null"));
                }
            }
            catch (System.Exception ex) { if (!_ilAttackLogged) { _ilAttackLogged = true; Bootstrap.Log("IL攻速异常: " + ex.Message); } }
            return interval;
        }

        /// <summary>伤害缩放：被注入到 HurtComponent 的 float 伤害方法，按阵营做无敌/血量倍率。</summary>
        public static float ScaleDamageFloat(object hurtComp, float num)
        {
            Probe();
            try
            {
                var owner = FindFieldVal(hurtComp, "_damageOwner");
                if (owner is Node2D n2)
                {
                    var camp = ESP.GetCamp(n2);
                    if (!_ilScaleLogged)
                    {
                        _ilScaleLogged = true;
                        Bootstrap.Log("IL伤害缩放触发: owner=" + owner.GetType().Name + " camp=" + (camp ?? "null") + " 植物无敌=" + ModSettings.PlantInvincible + " 植物血量=" + ModSettings.PlantHP);
                    }
                    if (camp == "Plant")
                    {
                        if (ModSettings.PlantInvincible) return 0f;
                        // 血量倍率由 hitpointScale 实现（hitpoints 真正翻倍），伤害保持原值——不再除以倍率（否则伤害显示只剩 1~2 点）
                    }
                    if (camp == "Zombie")
                    {
                        if (ModSettings.ZombieInvincible) return 0f;
                    }
                }
                else if (!_ilScaleLogged)
                {
                    _ilScaleLogged = true;
                    Bootstrap.Log("IL伤害缩放: 找不到 owner " + (hurtComp != null ? hurtComp.GetType().Name : "null"));
                }
            }
            catch (System.Exception ex) { if (!_ilScaleLogged) { _ilScaleLogged = true; Bootstrap.Log("IL伤害缩放异常: " + ex.Message); } }
            return num;
        }

        /// <summary>无敌屏蔽：被注入到 HurtComponent 的攻击配置类伤害方法（如僵尸咬），无敌时返回 true 直接 0 伤害。</summary>
        public static bool IsDamageBlocked(object hurtComp)
        {
            Probe();
            try
            {
                var owner = FindFieldVal(hurtComp, "_damageOwner");
                if (owner is Node2D n2)
                {
                    var camp = ESP.GetCamp(n2);
                    if (!_ilBlockLogged)
                    {
                        _ilBlockLogged = true;
                        Bootstrap.Log("IL无敌屏蔽触发: owner=" + owner.GetType().Name + " camp=" + (camp ?? "null"));
                    }
                    if (camp == "Plant" && ModSettings.PlantInvincible) return true;
                    if (camp == "Zombie" && ModSettings.ZombieInvincible) return true;
                }
            }
            catch { }
            return false;
        }

        // ===== 可靠版 IL 回调（简单 call 模式注入，patcher 用这些） =====

        /// <summary>无冷却判断：由 patcher 注入到 TowerDefenseInGamePacketShow.set_coldDownOpen 开头。
        /// value=true（开启冷却）且 NoCooldown 开关开时返回 true（跳过设置，卡牌不进入冷却）。</summary>
        public static bool ShouldSkipCooldownSet(bool value)
        {
            return value && ModSettings.NoCooldown;
        }

        /// <summary>无冷却强制清除：由 patcher 注入到卡牌 _PhysicsProcess / ApplyCachedRuntimeAvailability 开头。
        /// NoCooldown 开时立即关闭已开启的冷却（解决"开局已冷却、开开关后仍在冷却"）。</summary>
        public static void ForcePacketUsable(object packet)
        {
            try
            {
                if (!ModSettings.NoCooldown && !ModSettings.IgnorePurple) return;
                if (packet == null) return;
                var t = packet.GetType();
                if (ModSettings.NoCooldown)
                {
                    var po = t.GetProperty("coldDownOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (po != null && po.PropertyType == typeof(bool) && (bool)po.GetValue(packet))
                    {
                        // .NET 9 下 PropertyInfo.SetValue 会 MissingMethodException，必须用 setter Invoke
                        var setter = po.GetSetMethod(true);
                        if (setter != null) setter.Invoke(packet, new object[] { false });
                    }
                    var tf = t.GetField("coldDownTimer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (tf != null && tf.FieldType == typeof(double) && (double)tf.GetValue(packet) > 0.0)
                        tf.SetValue(packet, 0.0);
                    var cf = t.GetField("coldDown", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (cf != null && cf.FieldType == typeof(double) && (double)cf.GetValue(packet) > 0.0)
                        cf.SetValue(packet, 0.0);
                }
                if (ModSettings.IgnorePurple)
                {
                    // 紫卡解锁：只 _lock=false（卡牌不锁定）。
                    // 注意：不能强制 _cachedRuntimeAvailability=true——那是游戏每帧按阳光+前置植物算的，
                    // 强制会与游戏状态打架导致"卡牌显示亮但点击拿不起来/种植失败"。
                    // 前置植物由 HasRequiredPlantCover 注入恒 true 解决，阳光靠无限阳光/正常攒。
                    var lf = t.GetField("_lock", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (lf != null && lf.FieldType == typeof(bool) && (bool)lf.GetValue(packet))
                        lf.SetValue(packet, false);
                }
            }
            catch { }
        }

        /// <summary>魅惑：由 patcher 注入到 TowerDefenseCharacter.BatchProcessUpdate 开头。
        /// 攻击敌我判断用 camp！所以魅惑 = 反转 camp（植物→ZOMBIE，僵尸→PLANT）；
        /// 用 instanceId 集合记录已反转的角色，关闭开关时自动还原。</summary>
        static readonly System.Collections.Generic.HashSet<ulong> _charmedSet = new System.Collections.Generic.HashSet<ulong>();
        static readonly System.Collections.Generic.Dictionary<ulong, float> _charmFacing = new System.Collections.Generic.Dictionary<ulong, float>();   // 魅惑目标朝向符号（1=朝右 -1=朝左），持续保持防游戏改回
        static bool _charmDiagLogged;
        static bool _charmCallLogged;
        static int _charmDiagTimer;
        static int _accelDiagTimer;
        static int _asDiagTimer;
        static int _accelCallCount;
        static int _charmStateDiagTimer;
        static string _lastCharmState;
        static System.Type _fireCompType;
        static readonly System.Collections.Generic.Dictionary<ulong, float> _charmLastScaleX = new System.Collections.Generic.Dictionary<ulong, float>();
        static readonly System.Collections.Generic.Dictionary<ulong, Godot.Vector2> _charmLastPos = new System.Collections.Generic.Dictionary<ulong, Godot.Vector2>();
        static int _charmFlipCount;
        static int _charmMoveCount;
        static int _charmJitterCount;
        static int _flipReportTimer;
        static int _conveyorTimer;
        static bool _conveyorLogged;
        static object _convCfgRef;
        static object _convOrigVal;
        static bool _convOrigSet;
        static int _rainTimer;
        static int _forceLvTimer;
        static bool _forceLevelApplied;
        static object _forceLvControlRef;
        static string _forceSceneName;   // 场景变化检测（control 可能复用，引用重置不可靠）
        static int _forceRainTimer;
        static int _redLineTimer;
        static bool _redLineLogged;
        static bool _rainLogged;
        static object _rainCfgRef;
        static object _rainOrigVal;
        static bool _rainOrigSet;
        static int _vaseTimer;
        static bool _vaseDone;
        static int _lastVaseCount;
        static int _sbTimer;
        static bool _sbDone;
        static int _lastSbCount;
        static int _sbRetryTimer;   // 成功后定时重新随机（保持卡槽一直随机状态）
        static int _rndDiagTimer;

        public static void ApplyCharmPatch(object character)
        {
            Probe();
            try
            {
                // 诊断：入口绝对日志（确认 ApplyCharmPatch 是否被调用 + character 类型）
                if (!_charmCallLogged)
                {
                    _charmCallLogged = true;
                    var instT = character != null ? FindFieldVal(character, "instance") : null;
                    Bootstrap.Log("魅惑函数入口: argType=" + (character != null ? character.GetType().Name : "null") + " isNode2D=" + (character is Node2D) + " inst=" + (instT != null ? "ok" : "null") + " 植开关=" + ModSettings.CharmPlant + " 僵开关=" + ModSettings.CharmZombie);
                }
                if (++_charmTick % 10 != 0) return;
                if (!(character is Node2D n2)) return;
                var inst = FindFieldVal(character, "instance");
                if (inst == null || !GodotObject.IsInstanceValid((GodotObject)inst)) return;
                ulong id = ((GodotObject)inst).GetInstanceId();
                bool isPlant = IsTypeOf(n2, "TowerDefensePlant");
                bool isZombie = IsTypeOf(n2, "TowerDefenseZombie");
                // 诊断：每 300 帧打印类型识别结果（排查魅惑不生效：isPlant/isZombie 可能都 false）
                if (!_charmDiagLogged || ++_charmDiagTimer >= 300)
                {
                    if (_charmDiagTimer >= 300) _charmDiagTimer = 0;
                    _charmDiagLogged = true;
                    Bootstrap.Log("魅惑诊断: type=" + n2.GetType().Name + " isPlant=" + isPlant + " isZombie=" + isZombie + " 植开关=" + ModSettings.CharmPlant + " 僵开关=" + ModSettings.CharmZombie);
                }
                if (!isPlant && !isZombie) return;
                bool wantCharm = (isPlant && ModSettings.CharmPlant) || (isZombie && ModSettings.CharmZombie);
                bool already = _charmedSet.Contains(id);
                if (wantCharm && !already)
                {
                    // 用游戏原生 Hypnoses()（buff 全权处理转向/移动/攻击方向）——手动 SetCampEnum 只改属性，
                    // 游戏移动组件不认 camp → "魅惑僵尸还是往家走"（2026-08-21 用户反馈，改回 Hypnoses 方案）
                    try
                    {
                        ClearHypnosisImmune(n2);   // 清魅惑免疫位（海妖/僵王等），否则 Hypnoses 不生效
                        if (!BuffHas(n2, "Hypnoses")) InvokeHypnoses(n2);
                        // 僵王/Boss 可能免疫 Hypnoses：额外强制 instance.hypnoses + 清攻击目标，保证魅惑生效
                        SetCharmFlagAndClearTarget(n2);
                        // 所有僵尸（含僵王/车类Zamboni/钻石等）：Hypnoses 免疫时强制改阵营为植物方，确保魅惑生效
                        if (IsTypeOf(n2, "TowerDefenseZombie")) SetCampEnum(n2, "PLANT");
                        _charmedSet.Add(id);
                        if (!_charmLogged) { _charmLogged = true; Bootstrap.Log("魅惑已生效(Hypnoses): " + n2.GetType().Name); }
                    }
                    catch { }
                }
                else if (wantCharm && already)
                {
                    // 持续保持 buff（游戏可能自动移除魅惑状态）；僵王额外强制魅惑标志
                    try { ClearHypnosisImmune(n2); if (!BuffHas(n2, "Hypnoses")) InvokeHypnoses(n2); SetCharmFlagAndClearTarget(n2); if (IsTypeOf(n2, "TowerDefenseZombie")) SetCampEnum(n2, "PLANT"); } catch { }
                }
                else if (!wantCharm && already)
                {
                    try
                    {
                        if (BuffHas(n2, "Hypnoses")) InvokeHypnoses(n2);   // Hypnoses() 有 buff 删、无 buff 加
                        _charmedSet.Remove(id);
                        _charmFacing.Remove(id);
                        Bootstrap.Log("魅惑已还原: " + n2.GetType().Name);
                    }
                    catch { }
                }
            }
            catch (System.Exception ex) { if (!_charmLogged) { _charmLogged = true; Bootstrap.Log("魅惑异常: " + ex.Message); } }
        }

        /// <summary>反射判断节点是否继承自指定类型名。</summary>
        static bool IsTypeOf(Node2D n, string typeName)
        {
            try
            {
                var target = FindType(typeName);
                if (target == null) return false;
                return target.IsAssignableFrom(n.GetType());
            }
            catch { return false; }
        }

        /// <summary>设置 camp 属性为指定枚举名（PLANT/ZOMBIE）。</summary>
        static bool SetCampEnum(Node2D n, string enumName)
        {
            try
            {
                var t = n.GetType();
                var campProp = t.GetProperty("camp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (campProp == null) return false;
                var cur = campProp.GetValue(n);
                object target;
                try { target = Enum.Parse(campProp.PropertyType, enumName); }
                catch { return false; }
                if (cur.Equals(target)) return true;
                var setter = campProp.GetSetMethod(true);
                if (setter == null) return false;
                setter.Invoke(n, new object[] { target });
                return true;
            }
            catch { return false; }
        }

        /// <summary>沿基类链反射取字段 FieldInfo（可写值）。</summary>
        static System.Reflection.FieldInfo FindFieldInfo(object obj, string name)
        {
            try
            {
                var t = obj.GetType();
                while (t != null)
                {
                    var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) return f;
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        /// <summary>清除魅惑免疫位：instance.unUseBuffFlags 的 bit 8。海妖/僵王等免疫魅惑的僵尸设置了该位，
        /// HypnosesComponent.Hypnoses 检查到 (unUseBuffFlags & 8)!=0 直接 return 不添加 buff → 必须清掉才能魅惑。</summary>
        static void ClearHypnosisImmune(Node2D n)
        {
            try
            {
                var inst = FindFieldVal(n, "instance");
                if (inst == null) return;
                var uf = FindFieldInfo(inst, "unUseBuffFlags");
                if (uf == null || uf.FieldType != typeof(int)) return;
                int flags = (int)uf.GetValue(inst);
                if ((flags & 8) != 0)
                {
                    uf.SetValue(inst, flags & ~8);
                    if (!_charmImmuneLogged) { _charmImmuneLogged = true; Bootstrap.Log("魅惑: 清除免疫位 unUseBuffFlags " + flags + "->" + (flags & ~8) + " " + n.GetType().Name); }
                }
            }
            catch { }
        }

        /// <summary>魅惑修复：设 instance.hypnoses=true（真正魅惑标志）+ 清 attackComponent.target（重新寻敌）。</summary>
        static void SetCharmFlagAndClearTarget(Node2D n)
        {
            try
            {
                var inst = FindFieldVal(n, "instance");
                if (inst != null && GodotObject.IsInstanceValid((GodotObject)inst))
                {
                    var t = inst.GetType();
                    var hf = t.GetField("hypnoses", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (hf != null && hf.FieldType == typeof(bool)) hf.SetValue(inst, true);
                }
                var comp = FindFieldVal(n, "attackComponent");
                if (comp == null) comp = FindFieldVal(n, "_attackComponent");
                if (comp != null)
                {
                    var ct = comp.GetType();
                    var tf = ct.GetField("target", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (tf != null) tf.SetValue(comp, null);
                }
            }
            catch { }
        }

        /// <summary>炮类无冷却：玉米加农炮/南瓜炮等 CannonComponent 装填清零（restTime/剩余计时/_canFire）。</summary>
        static void ApplyCannonNoCooldown(Node root)
        {
            int count = 0;
            var tree = root.GetTree();
            Node start = tree != null && tree.Root != null ? tree.Root : root;
            CannonWalk(start, ref count);
            if (++_cannonLogTimer >= 120) { _cannonLogTimer = 0; Bootstrap.Log("炮类无冷却: 炮=" + count); }
        }

        static void CannonWalk(Node node, ref int count)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (TrySetCannonCooldown(child)) count++;
                }
                catch { }
                CannonWalk(child, ref count);
            }
        }

        static bool TrySetCannonCooldown(object obj)
        {
            try
            {
                var t = obj.GetType();
                // 炮类植物的 restTime/_restTime 在植物节点本身上（TowerDefensePlant*CobCannon）——
                // 之前只找 _cannonComponent 字段（不存在）→ 永远匹配不上 → 炮类无冷却失效
                var rp = t.GetProperty("restTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rp != null && rp.PropertyType == typeof(double))
                {
                    var setter = rp.GetSetMethod(true);
                    if (setter != null) { setter.Invoke(obj, new object[] { 0.0 }); return true; }
                }
                var rf = t.GetField("_restTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rf != null && rf.FieldType == typeof(double)) { rf.SetValue(obj, 0.0); return true; }
                // 兜底：旧逻辑找 _cannonComponent 组件
                var cc = FindFieldVal(obj, "_cannonComponent");
                if (cc == null) return false;
                var ct = cc.GetType();
                var cpr = ct.GetProperty("restTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cpr != null && cpr.PropertyType == typeof(double))
                {
                    var cset = cpr.GetSetMethod(true);
                    if (cset != null) cset.Invoke(cc, new object[] { 0.0 });
                }
                var crf = ct.GetField("restTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (crf != null && crf.FieldType == typeof(double)) crf.SetValue(cc, 0.0);
                var rr = ct.GetField("_restTimerRemaining", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rr != null && rr.FieldType == typeof(double)) rr.SetValue(cc, 0.0);
                // 关键：调用游戏原生 StopRestTimer()（= _restTimerRunning=false + _restTimerRemaining=0），
                // 正确结束冷却状态机（只改字段不清计时器会导致炮卡在 Rest 状态点不动/无法开炮）
                var stopM = ct.GetMethod("StopRestTimer", Type.EmptyTypes);
                if (stopM != null) { try { stopM.Invoke(cc, null); } catch { } }
                var rr2 = ct.GetField("_restTimerRemaining", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rr2 != null && rr2.FieldType == typeof(double)) rr2.SetValue(cc, 0.0);
                var run = ct.GetField("_restTimerRunning", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (run != null && run.FieldType == typeof(bool)) run.SetValue(cc, false);
                var paused = ct.GetField("_restTimerPaused", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (paused != null && paused.FieldType == typeof(bool)) paused.SetValue(cc, false);
                var pend = ct.GetField("_pendingRestTimeout", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pend != null && pend.FieldType == typeof(bool)) pend.SetValue(cc, false);
                var cf = ct.GetField("_canFire", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cf != null && cf.FieldType == typeof(bool)) cf.SetValue(cc, true);
                return true;
            }
            catch { }
            return false;
        }

        /// <summary>无视僵尸进家：持续重置失败流程（TowerDefenseZombieWon.LevelFail 的表现层），僵尸进家不判失败。</summary>
        static void ApplyIgnoreHouse(Node root)
        {
            try
            {
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                HouseWalk(start);
            }
            catch { }
        }

        static void HouseWalk(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child.GetType().Name.Contains("ZombieWon"))
                        TryResetFailPresentation(child);
                }
                catch { }
                HouseWalk(child);
            }
        }

        static void TryResetFailPresentation(object won)
        {
            try
            {
                var t = won.GetType();
                var f = t.GetField("_failurePresentationStarted", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null || f.FieldType != typeof(bool)) return;
                if (!(bool)f.GetValue(won)) return;
                f.SetValue(won, false);
                var sp = t.GetField("_sprite", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (sp != null && sp.GetValue(won) is GodotObject s) s.Set("visible", false);
                var cr = t.GetField("_colorRect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cr != null && cr.GetValue(won) is GodotObject c)
                {
                    var m = c.Get("modulate");
                    if (m.VariantType == Variant.Type.Color) c.Set("modulate", new Color(m.AsColor().R, m.AsColor().G, m.AsColor().B, 0f));
                }
                try { if (won is Node wonNode) CloseFailDialogs(wonNode.GetTree()); } catch { }
                if (!_houseLogged) { _houseLogged = true; Bootstrap.Log("无视进家: 已拦截失败流程"); }
            }
            catch { }
        }

        static void CloseFailDialogs(SceneTree tree)
        {
            if (tree == null || tree.Root == null) return;
            foreach (var n in tree.Root.GetChildren(true))
            {
                try
                {
                    if (n is Node nd && nd.GetType().Name.Contains("DialogBoxBattleFail") && GodotObject.IsInstanceValid(nd))
                    {
                        nd.Set("visible", false);
                        nd.QueueFree();
                    }
                }
                catch { }
            }
        }

        /// <summary>僵尸碰到警戒线不失败：把 TowerDefenseBattleFeatureWarningLine._triggered 持续设为 true。
        /// 原理：OnWarningLineTriggered 开头 if(_triggered) return——预置 true 后触发直接短路，不调 GameFail；
        /// 且 Process 也 if(_triggered) return，不再检测僵尸。
        /// 注意：feature 是 Resource（不在场景树里）——必须通过树里的 WarningLine 节点（它有 public feature 字段）拿到实例。</summary>
        static void ApplyIgnoreWarningLine(Node root)
        {
            try
            {
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                WarningLineFeatureWalk(start);
            }
            catch { }
        }

        static void WarningLineFeatureWalk(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    // 树里的节点是 WarningLine（检测线）；feature 是 Resource 不在树里，从 WarningLine.feature 字段拿
                    if (child.GetType().Name == "WarningLine")
                    {
                        var t = child.GetType();
                        var ff = t.GetField("feature", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var feat = ff?.GetValue(child);
                        if (feat != null)
                        {
                            var ft = feat.GetType();
                            var f = ft.GetField("_triggered", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (f != null && f.FieldType == typeof(bool) && !(bool)f.GetValue(feat))
                            {
                                f.SetValue(feat, true);
                                if (!_warningLineLogged) { _warningLineLogged = true; Bootstrap.Log("无视警戒线: 已预置 _triggered (feature=" + ft.Name + ")"); }
                            }
                        }
                        // 兜底：也把 WarningLine 节点自身 ProcessMode 置 Disabled（彻底停止检测/触发）
                        try
                        {
                            if (ModSettings.IgnoreWarningLine) child.Set("process_mode", 3);   // Disabled
                        }
                        catch { }
                    }
                }
                catch { }
                WarningLineFeatureWalk(child);
            }
        }

        /// <summary>强制种子雨（运行时模拟）：每 3 秒在地图随机格子刷一张随机植物卡掉落——不依赖关卡初始化时序，必定生效。</summary>
        static void ApplyForceRainRuntime(Node root)
        {
            try
            {
                var ids = GetPacketIds(true);
                if (ids.Count == 0) { Bootstrap.Log("强制种子雨: 无植物卡池"); return; }
                var id = ids[_bulletRand.Next(ids.Count)];
                var grid = new Vector2I(GD.RandRange(1, 9), GD.RandRange(1, 5));
                bool ok = SpawnPacketToScene(id, grid);
                Bootstrap.Log("强制种子雨: " + (ok ? "掉落 " : "失败 ") + id + " @" + grid);
            }
            catch (System.Exception ex) { Bootstrap.Log("强制种子雨异常: " + ex.Message); }
        }

        /// <summary>强制关卡功能（每关一次）：在关卡 feature 创建前（_battleGraphReady=false 窗口）向 levelConfig 注入：
        /// - ForceFog：featureData["Fog"] + fogManager(open=true) → 关卡加载时生成迷雾
        /// - CanChooseAll：NewConfig 直接改 featureData（OldConfig 走 getter 注入，此处也顺手兜底）
        /// 注意：老格式 OldLevelInit 会先 ExportToFeatureProcess() 重建 featureData，所以必须同时设字段（fogManager/_packetBankMethod）。</summary>
        static void ApplyForceLevelFeatures(Node root)
        {
            try
            {
                if (_forceLevelApplied) return;
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                object control = null;
                try
                {
                    var cp = _tdmType.GetProperty("CurrentControl", BindingFlags.Public | BindingFlags.Static);
                    if (cp != null) control = cp.GetValue(null);
                }
                catch { }
                if (control == null) return;   // 战斗还没开始
                // 关卡切换检测：control 变化（旧 control 销毁/新关卡）→ 重置注入标志
                if (!ReferenceEquals(_forceLvControlRef, control)) { _forceLvControlRef = control; _forceLevelApplied = false; }
                // 场景变化检测（control 可能复用 → 引用重置不可靠）：CurrentScene 类型变化时重置
                try
                {
                    var cs = root != null && root.GetTree() != null ? root.GetTree().CurrentScene : null;
                    string csn = cs != null && GodotObject.IsInstanceValid(cs) ? cs.GetType().Name : "";
                    if (csn.Length > 0 && csn != _forceSceneName)
                    {
                        _forceSceneName = csn;
                        _forceLevelApplied = false;
                    }
                }
                catch { }
                var ct = control.GetType();
                var readyF = ct.GetField("_battleGraphReady", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (readyF == null) return;
                bool ready = (bool)readyF.GetValue(control);
                if (ready) return;   // 已过 feature 窗口：本帧不注入，不放弃（等新场景/control 重置后再试）
                var lc = FindPropOrFieldVal(control, "levelConfig");
                if (lc == null) return;
                var fd = FindPropOrFieldVal(lc, "featureData");
                if (!(fd is System.Collections.IDictionary fdDict)) return;
                bool isOld = lc.GetType().Name == "TowerDefenseLevelConfig";

                // ---- 强制迷雾 ----
                if (ModSettings.ForceFog && !fdDict.Contains("Fog"))
                {
                    object fogMgr = null;
                    try
                    {
                        var ft = FindType("TowerDefenseLevelFogManagerConfig");
                        if (ft != null) fogMgr = Activator.CreateInstance(ft);
                    }
                    catch { }
                    var fogData = new Godot.Collections.Dictionary();
                    fogData["Open"] = true; fogData["BeginColumn"] = 5; fogData["ExtraColumns"] = 10;
                    fogData["BlowReturnDelay"] = 25.0; fogData["BlowDistance"] = 1400.0;
                    fogData["BlowDuration"] = 1.5; fogData["ReturnDuration"] = 3.0;
                    fogData["EntryDuration"] = 3.0; fogData["EntryStartX"] = 1350.0;
                    if (fogMgr != null)
                    {
                        TrySetField(fogMgr, "open", true);
                        TrySetField(fogMgr, "beginColumn", 5);
                        TrySetField(fogMgr, "extraColumns", 10);
                        // 老格式：设 fogManager 字段，ExportToFeatureProcess 会自动生成 featureData["Fog"]
                        if (isOld) TrySetField(lc, "fogManager", fogMgr);
                        // 直接写 featureData（新格式用）
                        try
                        {
                            var ex = fogMgr.GetType().GetMethod("Export", Type.EmptyTypes);
                            if (ex != null) fdDict["Fog"] = ex.Invoke(fogMgr, null);
                            else fdDict["Fog"] = fogData;
                        }
                        catch { fdDict["Fog"] = fogData; }
                    }
                    else
                    {
                        fdDict["Fog"] = fogData;
                    }
                    Bootstrap.Log("强制迷雾: 已注入 Fog featureData");
                }

                // ---- 强制选卡（CanChooseAll）----
                if (ModSettings.CanChooseAll)
                {
                    // 移除传送带/种子雨模式，改选卡
                    fdDict.Remove("ConveyorBelt");
                    fdDict.Remove("RainMode");
                    if (isOld)
                    {
                        var pm = FindFieldRec(lc.GetType(), "_packetBankMethod");
                        if (pm != null)
                        {
                            try { pm.SetValue(lc, Enum.Parse(pm.FieldType, "CHOOSE")); } catch { }
                        }
                    }
                    if (!fdDict.Contains("SeedBank"))
                    {
                        var sbData = new Godot.Collections.Dictionary();
                        sbData["Method"] = "CHOOSE";
                        sbData["PlantColumn"] = false;
                        sbData["ColdDownStart"] = true;
                        sbData["ColdDownUse"] = true;
                        var pkt = new Godot.Collections.Array();
                        foreach (var id in GetPacketIds(true))
                        {
                            var d = new Godot.Collections.Dictionary();
                            d["Name"] = id;
                            pkt.Add(d);
                        }
                        sbData["Packet"] = pkt;
                        fdDict["SeedBank"] = sbData;
                        var pbData = new Godot.Collections.Dictionary();
                        pbData["PacketBankName"] = "";
                        fdDict["PacketBank"] = pbData;
                        Bootstrap.Log("强制选卡: 已注入 SeedBank/PacketBank featureData（" + pkt.Count + " 张）");
                    }
                }

                // ---- 强制手套（GloveMode：注入 featureData["Glove"]，游戏正常初始化加载原生 GloveManager 场景）----
                // 原理：featureData["Glove"] 存在 → 游戏 NewLevelInit 正常 AddFeature + GameInit + GameStart（全程游戏驱动，async 正确 await）
                // → Init 加载 GLOVE_MANAGER 场景 + InitializeManager 初始化按钮 + 创建 GlovePickTool。
                // 注意：不要手动 AddFeature/GameInit/GameStart（与游戏自身流程冲突会重复创建/初始化失败）
                if (ModSettings.GloveMode)
                {
                    if (!fdDict.Contains("Glove"))
                    {
                        fdDict["Glove"] = new Godot.Collections.Dictionary();
                        Bootstrap.Log("强制手套: 已注入 Glove featureData（原生手套）");
                    }
                    // 老格式兜底：写存档标记（游戏读存档路径也能识别手套已开启）
                    try
                    {
                        var gsmType = FindType("GameSaveManager");
                        var gsm = gsmType?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                        if (gsm != null && gsmType != null)
                        {
                            var setFeat = gsmType.GetMethod("SetFeatureValue", new Type[] { typeof(string), typeof(Godot.Variant) });
                            if (setFeat != null)
                            {
                                setFeat.Invoke(gsm, new object[] { "Glove", Godot.Variant.From(1) });
                                Bootstrap.Log("强制手套: 已写存档 SetFeatureValue(Glove)=1");
                            }
                        }
                    }
                    catch { }
                }

                _forceLevelApplied = true;
            }
            catch (System.Exception ex) { Bootstrap.Log("强制关卡功能异常: " + ex.Message); }
        }

        /// <summary>无视红线：红线（警戒线）区域也能种植物/僵尸——把树里的 WarningLine 检测节点 ProcessMode 置 Disabled
        /// （红线区域不再限制/触发），并把 mapFeature.stripeRow 设为 -1（ProcessPacketPick 里 stripeRow==-1 时跳过红线种植限制，
        /// 植物/僵尸在红线两边都能种；红线视觉由 WarningLine 节点负责，不受影响）。</summary>
        static void ApplyIgnoreRedLine(Node root)
        {
            try
            {
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                RedLineWalk(start);
                // 关键：mapFeature.stripeRow = -1 → 种植时红线判断（stripeRow != -1 才生效）整体跳过
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;
                try
                {
                    var mf = _tdmType.GetMethod("GetMapFeature", Type.EmptyTypes);
                    object mapFeat = mf != null ? mf.Invoke(tdm, null) : null;
                    if (mapFeat != null)
                    {
                        var sf = FindFieldRec(mapFeat.GetType(), "stripeRow");
                        if (sf != null && sf.FieldType == typeof(int))
                        {
                            int cur = (int)sf.GetValue(mapFeat);
                            if (cur != -1)
                            {
                                sf.SetValue(mapFeat, -1);
                                if (!_redLineLogged) { _redLineLogged = true; Bootstrap.Log("无视红线: stripeRow " + cur + " → -1（两边可种）"); }
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        static void RedLineWalk(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child.GetType().Name == "WarningLine")
                    {
                        try { child.Set("process_mode", 3); } catch { }   // Disabled：红线检测区域完全失效
                    }
                }
                catch { }
                RedLineWalk(child);
            }
        }

        /// <summary>沿基类链找字段。</summary>
        static System.Reflection.FieldInfo FindFieldRec(System.Type t, string name)
        {
            while (t != null && t != typeof(object))
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        /// <summary>沿基类链设置字段值。</summary>
        static void TrySetField(object obj, string name, object val)
        {
            try
            {
                var f = FindFieldRec(obj.GetType(), name);
                if (f != null) f.SetValue(obj, val);
            }
            catch { }
        }

        /// <summary>趣味 Q弹模式：植物+僵尸有节奏地向下压扁 / 向上拉伸（只 Y 轴，左右不变）。果冻模式：+X 左右摇摆抽搐。
        /// 用 Q弹专用缓存（每 30 帧刷新一次），每帧直接设置 Scale——避免每帧全树遍历导致的卡顿/瞬间暂停。</summary>
        static bool _squashDiag;
        static void ApplyPlantSquash(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                // 每 30 帧或缓存空时刷新角色列表（一次遍历，避免每帧遍历卡顿）
                if (_squashCache.Count == 0 || ++_squashRefreshTimer >= 30)
                {
                    _squashRefreshTimer = 0;
                    _squashCache.Clear();
                    var tree = root.GetTree();
                    Node start = tree != null && tree.Root != null ? tree.Root : root;
                    CollectSquashRoles(start);
                    if (!_squashDiag)
                    {
                        _squashDiag = true;
                        int p = 0, z = 0;
                        foreach (var n in _squashCache)
                        {
                            try { if (ESP.GetCamp(n) == "Zombie") z++; else p++; } catch { }
                        }
                        Bootstrap.Log("Q弹/果冻: 角色缓存 植物=" + p + " 僵尸=" + z + " 果冻=" + ModSettings.JellyMode + " 缓存总数=" + _squashCache.Count);
                    }
                }
                ulong frame = Engine.GetProcessFrames();
                bool jelly = ModSettings.JellyMode;
                // Q弹：Y 压缩（X 保持原值）；果冻：Y 压缩 + X 左右摇摆（更快频率，抽搐感）
                float period = jelly ? 12 : 30;
                float ph = (float)((frame % period) / (double)period) * Mathf.Pi * 2f;
                float sy = 1f - 0.5f * Mathf.Sin(ph);
                for (int i = 0; i < _squashCache.Count; i++)
                {
                    var n2 = _squashCache[i];
                    if (n2 == null || !GodotObject.IsInstanceValid(n2))
                    {
                        _squashCache.RemoveAt(i); i--; continue;
                    }
                    if (jelly)
                    {
                        // 果冻：X 也摇摆（左右扭动）+ Y 压缩（更抽搐）
                        float sx = 1f + 0.35f * Mathf.Sin(ph * 2f);
                        n2.Scale = new Vector2(sx, sy);
                    }
                    else
                    {
                        n2.Scale = new Vector2(n2.Scale.X, sy);   // X 保持，只改 Y
                    }
                }
            }
            catch { }
        }

        static void CollectSquashRoles(Node node)
        {
            foreach (var child in node.GetChildren(true))
            {
                try
                {
                    if (child is Node2D n2 && GodotObject.IsInstanceValid(n2))
                    {
                        var camp = ESP.GetCamp(n2);
                        if (camp == "Plant" || camp == "Zombie")
                            _squashCache.Add(n2);
                    }
                }
                catch { }
                CollectSquashRoles(child);
            }
        }

        /// <summary>攻速（独立逻辑）：由 patcher 注入到 AttackComponent.BatchUpdateValidated 开头，每帧直接控制 timer。
        /// timer 到 0 即触发攻击——倍率>1 每帧额外减 timer（加速），倍率<1 额外加（减速），纯倍率生效。</summary>
        public static void AccelerateTimer(object comp, double delta)
        {
            Probe();
            try
            {
                // 诊断：每 120 帧确认 AccelerateTimer 持续被调用
                if (++_accelCallCount >= 120)
                {
                    _accelCallCount = 0;
                    Bootstrap.Log("加速调用: comp=" + (comp != null ? comp.GetType().Name : "null") + " 植物=" + ModSettings.PlantAttackSpeed + " 僵尸=" + ModSettings.ZombieAttackSpeed);
                }
                var parent = FindFieldVal(comp, "parent");
                if (parent is Node2D n2)
                {
                    var camp = ESP.GetCamp(n2);
                    // 植物/僵尸攻速都由 AccelerateTimer 每帧直接加速 timer（patcher 已注入 BatchUpdateValidated 开头，每帧触发可靠）。
                    // 僵尸不再跳过：改 attackInterval 的 ApplyZombieAttackSpeed 实测未生效（patcher 注释确认），且会双重加速。
                    double mult;
                    if (camp == "Plant") mult = ModSettings.PlantAttackSpeed;
                    else if (camp == "Zombie") mult = ModSettings.ZombieAttackSpeed;
                    else return;
                    if (mult > 0.01 && Math.Abs(mult - 1.0) > 0.001)
                    {
                        double t = GetFieldVal(comp, "timer");
                        if (t > 0.0)
                        {
                            double nt = t - delta * (mult - 1.0);
                            SetFieldVal(comp, "timer", nt);
                            if (++_accelDiagTimer >= 120)
                            {
                                _accelDiagTimer = 0;
                                Bootstrap.Log("加速执行: " + parent.GetType().Name + " camp=" + (camp ?? "null") + " timer=" + t.ToString("0.00") + "->" + nt.ToString("0.00") + " mult=" + mult);
                            }
                        }
                        else if (++_accelDiagTimer >= 120)
                        {
                            _accelDiagTimer = 0;
                            string finfo = "notfound";
                            try { var fi = comp.GetType().GetField("timer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance); if (fi != null) finfo = "found type=" + fi.FieldType.Name + " val=" + fi.GetValue(comp); } catch (System.Exception ex) { finfo = "err:" + ex.Message; }
                            Bootstrap.Log("加速失败: timer=" + t + " field=" + finfo + " comp=" + comp.GetType().Name);
                        }
                    }
                    if (!_ilAttackLogged || ++_ilAttackLogTimer >= 120)
                    {
                        if (_ilAttackLogTimer >= 120) _ilAttackLogTimer = 0;
                        _ilAttackLogged = true;
                        Bootstrap.Log("IL攻速触发: parent=" + parent.GetType().Name + " camp=" + (camp ?? "null") + " 植物=" + ModSettings.PlantAttackSpeed + " 僵尸=" + ModSettings.ZombieAttackSpeed);
                    }
                }
            }
            catch { }
        }

        /// <summary>攻速：由 patcher 以简单 call 模式注入到 AttackComponent.Refresh / BatchUpdateValidated 开头。</summary>
        public static void ApplyAttackSpeedPatch(object comp)
        {
            Probe();
            try
            {
                if (++_attackTick % 3 != 0) return;   // 每 3 帧处理一次（更快生效，仍节流性能）
                var parent = FindFieldVal(comp, "parent");
                if (parent is Node2D n2)
                {
                    var camp = ESP.GetCamp(n2);
                    if (camp == "Plant" && ModSettings.PlantAttackSpeed > 0.01f && ModSettings.PlantAttackSpeed != 1.0f)
                    {
                        double baseV = GetFieldVal(comp, "attackIntervalBase");
                        if (baseV <= 0.0) baseV = 1.5;
                        SetFieldVal(comp, "attackInterval", baseV / ModSettings.PlantAttackSpeed);
                    }
                    if (camp == "Zombie" && ModSettings.ZombieAttackSpeed > 0.01f && ModSettings.ZombieAttackSpeed != 1.0f)
                    {
                        double baseV = GetFieldVal(comp, "attackIntervalBase");
                        if (baseV <= 0.0) baseV = 1.5;
                        SetFieldVal(comp, "attackInterval", baseV / ModSettings.ZombieAttackSpeed);
                    }
                    if (!_ilAttackLogged || ++_ilAttackLogTimer >= 120)
                    {
                        if (_ilAttackLogTimer >= 120) _ilAttackLogTimer = 0;
                        _ilAttackLogged = true;
                        Bootstrap.Log("IL攻速触发: parent=" + parent.GetType().Name + " camp=" + (camp ?? "null") + " 植物=" + ModSettings.PlantAttackSpeed + " 僵尸=" + ModSettings.ZombieAttackSpeed);
                    }
                }
                else if (!_ilAttackLogged)
                {
                    _ilAttackLogged = true;
                    Bootstrap.Log("IL攻速: 找不到 parent " + (comp != null ? comp.GetType().Name : "null"));
                }
            }
            catch (System.Exception ex) { if (!_ilAttackLogged) { _ilAttackLogged = true; Bootstrap.Log("IL攻速异常: " + ex.Message); } }
        }

        /// <summary>伤害缩放（实例层）：由 patcher 注入到 TowerDefenseCharacterInstance 的 double 伤害方法开头。</summary>
        public static double ScaleDamageInstance(object inst, double num)
        {
            Probe();
            double result = num;
            try
            {
                var character = FindFieldVal(inst, "character");
                if (character is Node2D n2)
                {
                    var camp = ESP.GetCamp(n2);
                    // 血量倍率由 hitpointScale 实现（hitpoints 真正翻倍），伤害保持原值——不再除以倍率（否则伤害显示只剩 1~2 点）
                    if (camp == "Plant" && ModSettings.PlantInvincible) result = 0.0;
                    if (camp == "Zombie" && ModSettings.ZombieInvincible) result = 0.0;
                    if (!_ilScaleLogged || ++_ilScaleLogTimer >= 120)
                    {
                        if (_ilScaleLogTimer >= 120) _ilScaleLogTimer = 0;
                        _ilScaleLogged = true;
                        Bootstrap.Log("IL伤害缩放: camp=" + (camp ?? "null") + " 伤=" + num + "->" + result + " 植无敌=" + ModSettings.PlantInvincible + " 植血=" + ModSettings.PlantHP + " 僵无敌=" + ModSettings.ZombieInvincible + " 僵血=" + ModSettings.ZombieHP);
                    }
                }
            }
            catch (System.Exception ex) { if (!_ilScaleLogged) { _ilScaleLogged = true; Bootstrap.Log("IL伤害缩放异常: " + ex.Message); } }
            return result;
        }

        /// <summary>无敌屏蔽（实例层）：由 patcher 注入到 TowerDefenseCharacterInstance 的攻击配置类伤害方法开头。</summary>
        public static bool IsDamageBlockedInstance(object inst)
        {
            Probe();
            try
            {
                var character = FindFieldVal(inst, "character");
                if (character is Node2D n2)
                {
                    var camp = ESP.GetCamp(n2);
                    if (camp == "Plant" && ModSettings.PlantInvincible) return true;
                    if (camp == "Zombie" && ModSettings.ZombieInvincible) return true;
                    if (!_ilBlockLogged || ++_ilBlockLogTimer >= 120)
                    {
                        if (_ilBlockLogTimer >= 120) _ilBlockLogTimer = 0;
                        _ilBlockLogged = true;
                        Bootstrap.Log("IL无敌屏蔽: camp=" + (camp ?? "null") + " 植无敌=" + ModSettings.PlantInvincible + " 僵无敌=" + ModSettings.ZombieInvincible + " -> " + ((camp == "Plant" && ModSettings.PlantInvincible) || (camp == "Zombie" && ModSettings.ZombieInvincible)));
                    }
                }
            }
            catch { }
            return false;
        }

        static double GetFieldVal(object o, string name)
        {
            try
            {
                var t = o.GetType();
                while (t != null)
                {
                    var fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fi != null) return (double)fi.GetValue(o);
                    t = t.BaseType;
                }
            }
            catch { }
            return 0.0;
        }

        static void SetFieldVal(object o, string name, double val)
        {
            try
            {
                var t = o.GetType();
                while (t != null)
                {
                    var fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fi != null) { fi.SetValue(o, val); return; }
                    t = t.BaseType;
                }
            }
            catch { }
        }


        // ================= 以下为 2026-09-14 从电脑版同步过来的对战相关成员 =================

        /// <summary>给 NetPvp 用的类型查找（FindType 是私有）。</summary></summary></summary>
        public static Type NetFindType(string name) { return FindType(name); }

        /// <summary>场景树根（给 NetPvp 的扫描用）。</summary>
        public static Node NetTreeRoot() { try { return GetTreeRoot(); } catch { return null; } }

        /// <summary>角色节点容器（TowerDefenseManager.GetCharacterNode，静态）。
        /// 源码依据：AddWarningColumn 就是把新建的 WarningLine 挂到这下面并用它的坐标系定位。</summary>
        public static Node NetCharacterNode()
        {
            try
            {
                if (_tdmType == null) return null;
                var m = _tdmType.GetMethod("GetCharacterNode", Type.EmptyTypes);
                return m != null ? m.Invoke(null, null) as Node : null;
            }
            catch { return null; }
        }

        /// <summary>当前战斗 control 节点（找不到返回 null）。
        /// 自绘分界线用它当容器 —— 它就在草坪的坐标系里。
        /// 上一版找不到草坪节点就回退成 root，而 root 是屏幕坐标系，
        /// 拿草坪坐标去画就在屏幕外，所以日志里那条“容器=root（可能看不见）”就是看不见的原因。</summary>
        public static Node NetBattleControlNode()
        {
            try
            {
                // ★ 用本项目里**已经在用**的写法：
                //     var root = GetTreeRoot(); FindNodeByTypeName(root, "TowerDefenseControlNew")
                //   （ForceWinAll / 诊断那几处都是这么拿 control 的，一直好用。）
                //   我之前试的 CurrentControl 属性返回 null、GetBattleControl 也不对，白折腾。
                var root = GetTreeRoot();
                if (root == null) return null;
                return FindNodeByTypeName(root, "TowerDefenseControlNew");
            }
            catch { return null; }
        }

        /// <summary>本关地图列数（TowerDefenseManager.GetMapGridNum().X）。拿不到返回 0。</summary>
        public static int NetMapGridNumX()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return 0;
                var m = tdm.GetType().GetMethod("GetMapGridNum", Type.EmptyTypes);
                if (m != null && m.Invoke(tdm, null) is Vector2I n) return n.X;
            }
            catch { }
            return 0;
        }

        /// <summary>按节点类型名删掉场景里的所有小推车（兑底方案）。
        /// CommandManager.RemoveAllMowers() 在某些时候不生效，这里直接把
        /// TowerDefenseMowerDefault / TowerDefenseMowerSun 等节点 QueueFree。</summary>
        public static int NetKillMowerNodes()
        {
            int n = 0;
            try
            {
                var root = GetTreeRoot();
                if (root == null) return 0;
                n = KillMowerWalk(root, 0);
            }
            catch { }
            return n;
        }

        static int KillMowerWalk(Node node, int depth)
        {
            int n = 0;
            if (node == null || depth > 14) return 0;
            try
            {
                var kids = node.GetChildren(true);
                for (int i = 0; i < kids.Count; i++)
                {
                    var c = kids[i];
                    if (c == null) continue;
                    string tn = c.GetType().Name;
                    if (tn.StartsWith("TowerDefenseMower", StringComparison.Ordinal))
                    {
                        try { c.QueueFree(); n++; } catch { }
                        continue;   // 整个子树一起掉
                    }
                    n += KillMowerWalk(c, depth + 1);
                }
            }
            catch { }
            return n;
        }

        /// <summary>取一个 battle feature（先拿卡槽 feature 当入口，再 GetFeature(name)）。</summary>
        public static object NetGetFeature(string name)
        {
            try
            {
                var ctrl = NetBattleControlNode();
                if (ctrl == null) return null;

                // 主路径：control.featureDictionary（本项目取 feature 一直用这个）
                var fd = FindFieldInfo(ctrl, "featureDictionary");
                var v = fd != null ? fd.GetValue(ctrl) : null;
                if (v is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry e in dict)
                    {
                        object val = e.Value;
                        if (val == null) continue;
                        string k = e.Key as string ?? (e.Key != null ? e.Key.ToString() : "");
                        if (k.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return val;
                        if (val.GetType().Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return val;
                    }
                }

                // 兜底：GetFeature —— 它是接口 IBattleEventNetworkContext 上的，
                //   若类里是**显式接口实现**，方法名会是 "命名空间.IBattleEventNetworkContext.GetFeature"，
                //   直接 GetMethod("GetFeature") 是找不到的（上一版就死在道，日志“找不到 GetFeature”）。
                var ms = ctrl.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < ms.Length; i++)
                {
                    string nm = ms[i].Name;
                    if (nm != "GetFeature" && !nm.EndsWith(".GetFeature", StringComparison.Ordinal)) continue;
                    var ps = ms[i].GetParameters();
                    if (ps.Length != 1) continue;
                    object arg = ps[0].ParameterType == typeof(string) ? (object)name : new Godot.StringName(name);
                    try { var r = ms[i].Invoke(ctrl, new object[] { arg }); if (r != null) return r; } catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>用**游戏自己的接口**在指定列加一条警戒线（分界线）。
        ///
        /// ★ 反编译 TowerDefenseBattleFeatureWarningLine.AddWarningColumn 得到的事实：
        ///   警戒线是**按需创建**的（WarningLineScene.Instantiate → AddChild 到
        ///   TowerDefenseManager.GetCharacterNode() → 用 GetMapCellPos(column+1,1) 定位，
        ///   并把 Sprite 纵向缩放拉到整条草坪）。
        ///   关卡默认场景里**根本没有这个节点** —— 这就是我 FindWarningLine 一直找不到、
        ///   红线一直不显示的原因：不该去“找”，而应该让游戏去“建”。
        ///
        /// 加完后把 _triggered 置 true：Process 与 OnWarningLineTriggered 开头都有
        /// if(_triggered) return，置 true 后这条线只剩展示作用，僵尸越线不会判负。</summary>
        public static bool PvpAddWarningColumn(int column, out string err)
        {
            err = "";
            try
            {
                object warn = NetGetFeature("WarningLine");
                if (warn == null) { err = "拿不到 WarningLine feature"; return false; }
                var m = warn.GetType().GetMethod("AddWarningColumn", new Type[] { typeof(int) });
                if (m == null) { err = "找不到 AddWarningColumn"; return false; }
                m.Invoke(warn, new object[] { column });
                PvpWriteField(warn, "_triggered", true);
                _pvpWarnFeature = warn;
                return true;
            }
            catch (Exception ex) { err = ex.GetType().Name + " " + ex.Message; return false; }
        }

        /// <summary>每帧把警戒线置为不触发（游戏只在自己加载时重置它）。</summary>
        public static void PvpKeepWarningLineInert()
        {
            try { if (_pvpWarnFeature != null) PvpWriteField(_pvpWarnFeature, "_triggered", true); }
            catch { }
        }

        /// <summary>删掉本关所有小推车（游戏自带接口：CommandManager.RemoveAllMowers）。</summary>
        public static bool PvpRemoveAllMowers(out string err)
        {
            err = "";
            try
            {
                var t = FindType("CommandManager");
                if (t == null) { err = "找不到 CommandManager"; return false; }
                object inst = null;
                var p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null) inst = p.GetValue(null);
                if (inst == null)
                {
                    var f = t.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null) inst = f.GetValue(null);
                }
                if (inst == null) { err = "CommandManager.Instance 为空"; return false; }
                var m = t.GetMethod("RemoveAllMowers", Type.EmptyTypes);
                if (m == null) { err = "找不到 RemoveAllMowers"; return false; }
                m.Invoke(inst, null);
                return true;
            }
            catch (Exception ex) { err = ex.GetType().Name + " " + ex.Message; return false; }
        }

        /// <summary>对战模式的僵尸方：让游戏自己认为处于 IZM（我是僵尸）模式。
        ///
        /// ★ 依据（反编译 TowerDefenseBattleFeaturePacketBank.SetPacketBankData 原文）：
        ///     packetBankData = _data; RebuildPacketAvailabilityIndex(); PacketClear();
        ///     if (TowerDefenseManager.Instance.IsIZMMode() || IsIZM2Mode()) CategoryChoose("Zombie");
        ///     else CategoryChoose("White", reFresh: true);
        ///   游戏**自己会选分类**，判据就是这个 IsIZMMode()。
        ///   我们之前在外面反复调 SetPacketBankData/CategoryChoose，但这个开关没打开，
        ///   所以每次都落到 else 分支选 "White"（植物分类）—— 僵尸分类自然是空的。
        ///   “僵尸方”在游戏里的表达方式就是 IZM 模式，按它来而不是绕它。
        ///
        /// ★ 注意：IZM 会在多处被判定，可能附带其它行为。先用日志观察，
        ///   若出现关卡流程异常，换成“只对卡槽 feature 生效”的局部方案。</summary>
        public static bool ShouldForceIzMMode()
        {
            try
            {
                if (NetPvp.Active && NetSession.MyFaction == NetFaction.Zombie)
                {
                    if (!_izmForcedLogged)
                    {
                        _izmForcedLogged = true;
                        Bootstrap.Log("对战：已让僵尸方处于 IZM 模式（卡槽会走 CategoryChoose(\"Zombie\")）");
                        Bootstrap.FlushLog();
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static void PvpFillZombieTray()
        {
            try
            {
                if (!PvpCardExperiment) return;
                PvpFillZombieTrayV3();
                return;
                if (!NetPvp.Active || NetSession.MyFaction != NetFaction.Zombie) return;
                if (_pvpTrayDone) return;
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;

                var gm = _tdmType.GetMethod("GetPacketBankFeature", Type.EmptyTypes);
                if (gm == null) { _pvpTrayDone = true; Bootstrap.Log("卡槽: 找不到 GetPacketBankFeature"); Bootstrap.FlushLog(); return; }
                object bank = gm.Invoke(tdm, null);
                if (bank == null) return;   // 卡槽 feature 还没建好，下次再试

                var bt = bank.GetType();
                var ids = NetPvp.ZombieTrayIds();
                var arr = new Godot.Collections.Array();
                for (int i = 0; i < ids.Count; i++)
                {
                    var cc = GetConfig(ids[i]);
                    // Godot 的 Array.Add 只收 Variant，配置是 GodotObject 派生（隐式转换到 Variant）
                    if (cc is Godot.GodotObject go) arr.Add(go);
                }
                if (arr.Count == 0) { _pvpTrayDone = true; Bootstrap.Log("卡槽: 僵尸卡配置全为空"); Bootstrap.FlushLog(); return; }

                // PacketBankInit 会按关卡数据重建卡槽，所以必须先建再灌
                try { bt.GetMethod("PacketBankInit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(bank, null); } catch { }

                var plc = bt.GetMethod("PacketListChoose", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (plc != null)
                {
                    plc.Invoke(bank, new object[] { arr });
                    _pvpTrayDone = true;
                    Bootstrap.Log("对战：已用 PacketListChoose 把 " + arr.Count + " 张僵尸卡交给卡槽（" + bt.Name + "）");
                    Bootstrap.FlushLog();
                    return;
                }

                // 退化路径：老的 SetVirtualizedPackets（实测能调用成功但界面不跟随）
                var svp = bt.GetMethod("SetVirtualizedPackets", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (svp == null) { _pvpTrayDone = true; Bootstrap.Log("卡槽: " + bt.Name + " 上既没有 PacketListChoose 也没有 SetVirtualizedPackets"); Bootstrap.FlushLog(); return; }
                svp.Invoke(bank, new object[] { arr });
                _pvpTrayDone = true;
                Bootstrap.Log("对战：已把 " + arr.Count + " 张僵尸卡灌入卡槽（退化路径 SetVirtualizedPackets）");
                Bootstrap.FlushLog();
            }
            catch (Exception ex) { _pvpTrayDone = true; Bootstrap.Log("卡槽异常: " + ex.GetType().Name + " " + ex.Message); Bootstrap.FlushLog(); }
        }

        public static void PvpFillZombieTrayV3()
        {
            try
            {
                if (NetPvp.Active == false || NetSession.MyFaction != NetFaction.Zombie) return;
                if (_pvpTrayV3Done) return;
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return;

                var gm = _tdmType.GetMethod("GetPacketBankFeature", Type.EmptyTypes);
                if (gm == null) { _pvpTrayV3Done = true; Bootstrap.Log("卡槽V3: 找不到 GetPacketBankFeature"); Bootstrap.FlushLog(); return; }
                object bank = gm.Invoke(tdm, null);
                if (bank == null) return;   // feature 还没建好，下次再试
                var bt = bank.GetType();

                // ① 拿 SeedBank feature（卡槽模式与卡包名都在它 config 上）
                object seed = null;
                try
                {
                    // ★ 改用 NetGetFeature（control.featureDictionary，项目里已经在用的取法）。
                    //   原来走 bank.GetType().GetMethod("GetFeature") —— 日志就是死在“找不到 GetFeature”。
                    seed = NetGetFeature("SeedBank");
                    if (seed == null)
                    {
                        var gfm = bt.GetMethod("GetFeature");
                        if (gfm != null)
                        {
                            var ps2 = gfm.GetParameters();
                            if (ps2.Length == 1)
                            {
                                object a2 = ps2[0].ParameterType == typeof(string) ? (object)"SeedBank" : new Godot.StringName("SeedBank");
                                seed = gfm.Invoke(bank, new object[] { a2 });
                            }
                        }
                    }
                }
                catch (Exception ex) { _pvpTrayV3Done = true; Bootstrap.Log("卡槽V3: 取 SeedBank 异常 " + ex.Message); Bootstrap.FlushLog(); return; }
                if (seed == null) { _pvpTrayV3Done = true; Bootstrap.Log("卡槽V3: 拿不到 SeedBank feature"); Bootstrap.FlushLog(); return; }

                object scfg = PvpReadField(seed, "config");
                if (scfg == null) { _pvpTrayV3Done = true; Bootstrap.Log("卡槽V3: SeedBank.config 为空"); Bootstrap.FlushLog(); return; }

                // ② 选一个真的含僵尸卡的卡包
                string bankName = PvpBestZombieBankName();
                if (string.IsNullOrEmpty(bankName)) { _pvpTrayV3Done = true; Bootstrap.Log("卡槽V3: 找不到含僵尸卡的卡包"); Bootstrap.FlushLog(); return; }

                // ★★ packetBankType 在 **PacketBank feature 自己的 config** 上，
                //    不是在 SeedBank.config 上（PacketBankInit 里写的是 config.packetBankType，
                //    那个 config 是 feature 自己的成员）。上一版写到 seed.config 上 —— 对象错了，
                //    所以字段写了也没用，僵尸分类仍是空的。
                object fcfg = PvpReadField(bank, "config");
                if (fcfg == null) { _pvpTrayV3Done = true; Bootstrap.Log("卡槽V3: 拿不到 PacketBank.config"); Bootstrap.FlushLog(); return; }
                string oldName = PvpReadField(fcfg, "packetBankType") as string;
                if (!PvpWriteField(fcfg, "packetBankType", bankName))
                {
                    _pvpTrayV3Done = true;
                    Bootstrap.Log("卡槽V3: packetBankType 写入失败（类型=" + fcfg.GetType().Name + "）");
                    Bootstrap.FlushLog();
                    return;
                }

                // ③ 直接调 PacketBankInit —— 它会自己读 packetBankType 并完成
                //    SetPacketBankData + CategoryChoose（IZM 时选 Zombie）。
                var pbi = bt.GetMethod("PacketBankInit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pbi == null) { _pvpTrayV3Done = true; Bootstrap.Log("卡槽V3: 找不到 PacketBankInit"); Bootstrap.FlushLog(); return; }
                pbi.Invoke(bank, null);

                // ④ 兜底：再显式设一次数据并切分类（顺序与源码一致）
                try
                {
                    var gbd = _tdmType.GetMethod("GetPacketBankData", new Type[] { typeof(string) });
                    var data = gbd != null ? gbd.Invoke(null, new object[] { bankName }) : null;
                    if (data != null)
                    {
                        var spd = bt.GetMethod("SetPacketBankData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (spd != null) spd.Invoke(bank, new object[] { data });
                    }
                    var cc = bt.GetMethod("CategoryChoose", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (cc != null)
                    {
                        var p0 = cc.GetParameters()[0].ParameterType;
                        object cat = p0 == typeof(string) ? (object)"Zombie" : new Godot.StringName("Zombie");
                        cc.Invoke(bank, new object[] { cat, true });
                    }
                }
                catch { }

                _pvpTrayV3Done = true;
                Bootstrap.Log("对战V3：packetBankType " + oldName + " → " + bankName + "，已 PacketBankInit + SetPacketBankData + CategoryChoose(Zombie)");
                Bootstrap.FlushLog();
            }
            catch (Exception ex) { _pvpTrayV3Done = true; Bootstrap.Log("卡槽V3异常: " + ex.GetType().Name + " " + ex.Message); Bootstrap.FlushLog(); }
        }

        /// <summary>把关卡配置里「某对象下的条目数组」填成指定名字列表。
        ///
        /// 条目形状取自 docs/level-format-analysis.md：
        ///   PreSpawn.Packet   → { "Name": "ZombieTarget", ... }
        ///   PacketBank.Packet → { "PacketName": "ZombieNormal", ... }
        /// 两个名字都试，哪个能写进去用哪个。
        /// ★ 这是在用**游戏自己的**生成/卡槽机制，而不是我反射调 Plant 硬生成 ——
        ///   后者实测返回 true 但实体会掉地（场景扫描恒为僵尸数=0）。</summary>
        static bool PvpFillEntryArray(object owner, string objField, string objField2, string arrField, string[] names, out string err)
        {
            err = "";
            try
            {
                object target = owner;
                // 先找到「装着数组的那个对象」：字段本身是数组就直接用，否则取它的值再找数组字段
                var of = FindFieldInfo(owner, objField) ?? FindFieldInfo(owner, objField2);
                if (of == null) { err = "找不到字段 " + objField + "/" + objField2; return false; }
                if (!of.FieldType.IsArray)
                {
                    target = of.GetValue(owner);
                    if (target == null) { err = objField + " 为空"; return false; }
                    var af = FindFieldInfo(target, arrField) ?? FindFieldInfo(target, arrField.Substring(0, 1).ToUpperInvariant() + arrField.Substring(1));
                    if (af == null) { err = "找不到 " + of.FieldType.Name + "." + arrField; return false; }
                    of = af;
                }
                if (!of.FieldType.IsArray) { err = of.Name + " 不是数组（" + of.FieldType.Name + "）"; return false; }

                var elem = of.FieldType.GetElementType();
                var arr = System.Array.CreateInstance(elem, names.Length);
                int n = 0;
                for (int i = 0; i < names.Length; i++)
                {
                    object item = null;
                    try { item = Activator.CreateInstance(elem); } catch { }
                    if (item == null) continue;
                    bool set = PvpWriteField(item, "Name", names[i]);
                    if (!set) set = PvpWriteField(item, "PacketName", names[i]);
                    if (!set) continue;
                    arr.SetValue(item, n++);
                }
                if (n == 0) { err = elem.Name + " 上没有 Name/PacketName 字段"; return false; }
                if (n != arr.Length) { var t2 = System.Array.CreateInstance(elem, n); System.Array.Copy(arr, t2, n); arr = t2; }
                of.SetValue(target, arr);
                return true;
            }
            catch (Exception ex) { err = ex.GetType().Name + " " + ex.Message; return false; }
        }

        /// <summary>在所有卡包里挑一个僵尸卡最多的（返回包名）。
        /// 和 GetPacketIds(false) 用的是同一条已验证路径（TOWERDEFENSE_PACKETBANKS + GetZombieList）。</summary>
        public static string PvpBestZombieBankName()
        {
            if (_pvpBestBankTried) return _pvpBestBank;
            try
            {
                if (_tdmType == null) return null;
                var gbd = _tdmType.GetMethod("GetPacketBankData", new Type[] { typeof(string) });
                if (gbd == null) return null;
                var rmType = FindType("ResourceManager");
                if (rmType == null) return null;
                var fld = rmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                object rm = fld != null ? fld.GetValue(null) : null;
                if (rm == null) { var p = rmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static); if (p != null) rm = p.GetValue(null); }
                if (rm == null) return null;
                var bp = rm.GetType().GetProperty("TOWERDEFENSE_PACKETBANKS", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (bp == null) return null;
                if (!(bp.GetValue(rm) is System.Collections.IDictionary banks)) return null;

                int best = 0;
                foreach (var k in banks.Keys)
                {
                    try
                    {
                        string name = k as string ?? k.ToString();
                        var d = gbd.Invoke(null, new object[] { name });
                        if (d == null) continue;
                        var zl = d.GetType().GetMethod("GetZombieList", Type.EmptyTypes);
                        if (zl == null) continue;
                        var r = zl.Invoke(d, null);
                        if (!(r is System.Collections.ICollection c)) continue;
                        if (c.Count > best) { best = c.Count; _pvpBestBank = name; }
                    }
                    catch { }
                }
                _pvpBestBankTried = true;
                Bootstrap.Log("卡槽V3: 僵尸卡最多的卡包 = " + _pvpBestBank + "（" + best + " 张）");
                Bootstrap.FlushLog();
            }
            catch { _pvpBestBankTried = true; }
            return _pvpBestBank;
        }

        /// <summary>这张卡是不是僵尸卡。对战模式「植物方只能选植物、僵尸方只能选僵尸」的判据。
        ///
        /// 为什么要硬拦而不是只在选卡界面隐藏：隐藏挡不住「已经带在卡槽里的卡」
        /// 和中途换阵营的情况，真正公平的做法是让这张卡无论怎么来的都用不了。</summary>
        public static bool IsZombieCardId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                var set = _pvpZombieIdSet;
                if (set == null)
                {
                    set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    var ids = GetPacketIds(false);   // false = 僵尸卡包
                    for (int i = 0; i < ids.Count; i++)
                    {
                        string s = ids[i];
                        if (string.IsNullOrEmpty(s)) continue;
                        set.Add(s);
                        set.Add(NetPvp.StripPrefix(s));
                    }
                    _pvpZombieIdSet = set;
                }
                if (set.Contains(id)) return true;
                if (set.Contains(NetPvp.StripPrefix(id))) return true;
                return false;
            }
            catch { }
            // 兜底：卡包表读不到时按前缀判（本作僵尸卡 id 都是 Zombie 开头）
            return id.StartsWith("Zombie", StringComparison.Ordinal);
        }

        /// <summary>联机/对战：格子 → 世界坐标（正向映射）。
        /// 用游戏自己的 <c>TowerDefenseManager.GetMapCellPlantPos(Vector2I)</c>，与刷卡牌/货币同一条路径。
        /// 拿不到时返回 (-1e7,-1e7)。
        ///
        /// ★ 有了它就别再用"扫世界坐标反推格子"那套：反推要扫一大片、谓词还不单调
        ///   （草坪外返回非法值，和"在右半场"同为 false），脆得很。</summary>
        public static Vector2 NetGridToWorld(Vector2I grid)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null || _tdmType == null) return new Vector2(-1e7f, -1e7f);
                if (_getMapCellPlantPos == null)
                    _getMapCellPlantPos = _tdmType.GetMethod("GetMapCellPlantPos", new Type[] { typeof(Vector2I) });
                if (_getMapCellPlantPos == null) return new Vector2(-1e7f, -1e7f);
                if (_getMapCellPlantPos.Invoke(null, new object[] { grid }) is Vector2 v) return v;
            }
            catch { }
            return new Vector2(-1e7f, -1e7f);
        }

        /// <summary>格子 → 世界坐标。★ 用 TowerDefenseManager.GetMapCellPos ——
        /// 源码里 AddWarningColumn 就是这么定位的：
        ///   wl.GlobalPosition = instance.GetMapCellPos(new Vector2I(column + 1, 1)) + new Vector2(-10f, 0f);
        /// （之前试的 GetMapCellPlantPos 恒返回 0，是另一个方法。）</summary>
        public static Vector2 NetMapCellPos(Vector2I grid)
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return new Vector2(-1e7f, -1e7f);
                var m = tdm.GetType().GetMethod("GetMapCellPos", new Type[] { typeof(Vector2I) });
                if (m == null) return new Vector2(-1e7f, -1e7f);
                if (m.Invoke(tdm, new object[] { grid }) is Vector2 v) return v;
            }
            catch { }
            return new Vector2(-1e7f, -1e7f);
        }

        /// <summary>草坪尺寸：格子尺寸 × 列数（源码里警戒线 sprite 的长度就是这么算的：
        /// GetMapGridSize().X * GetMapGridNum().X - 20f）。</summary>
        public static float NetLawnLength()
        {
            try
            {
                var tdm = GetTdmInstance();
                if (tdm == null) return 0f;
                var t = tdm.GetType();
                float cell = 0f;
                var ms = t.GetMethod("GetMapGridSize", Type.EmptyTypes);
                if (ms != null && ms.Invoke(tdm, null) is Vector2 sz) cell = sz.X;
                int cols = 0;
                var mn = t.GetMethod("GetMapGridNum", Type.EmptyTypes);
                if (mn != null && mn.Invoke(tdm, null) is Vector2I n) cols = n.X;
                if (cell > 0f && cols > 0) return cell * cols - 20f;
            }
            catch { }
            return 0f;
        }
        const bool PvpCardExperiment = true;
        static bool _pvpBankLogged;
        static object _pvpBankLcRef;
        static bool _pvpReachLogged;
        static bool _pvpMowerLogged;
        static int _pvpMowerTimer;
        static bool _pvpPreSpawnDone, _pvpBankDone;
        static bool _pvpTrayDone;
        static bool _pvpTrayV3Done;
        static string _pvpBestBank;
        static bool _pvpBestBankTried;
        static object _pvpWarnFeature;
        static bool _izmForcedLogged;
        static System.Collections.Generic.HashSet<string> _pvpZombieIdSet;

        static Godot.Node FindNodeByTypeName(Godot.Node n, string tn)
        {
            if (n == null) return null;
            try { if (n.GetType().Name == tn) return n; } catch { }
            foreach (var child in n.GetChildren(false))
            {
                if (child is Godot.Node c)
                {
                    var r = FindNodeByTypeName(c, tn);
                    if (r != null) return r;
                }
            }
            return null;
        }
    }
}

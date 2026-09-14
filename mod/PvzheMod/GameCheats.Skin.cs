using System;
using System.Reflection;
using Godot;

namespace PvzheMod
{
    /// <summary>皮肤/装扮解锁：枚举带皮肤的卡、列出皮肤、启用指定皮肤、皮肤全解（partial 拆分自 GameCheats）。</summary>
    public static partial class GameCheats
    {
        static readonly System.Collections.Generic.Dictionary<string, string> _unlockedSkins = new();

        /// <summary>返回所有带皮肤的卡 ID（customData.customDictionary 非空）。植物 + 僵尸。</summary>
        /// <summary>取皮肤数据源：customData（调 Init 确保 customDictionary 索引可用）。
        /// 诊断：customData 为 null 说明角色皮肤数据未从 pck 加载（GetCharConfig 走属性 getter 后一般可拿到）。</summary>
        static int _skinCdNullCount;
        static object GetCustomData(object cc)
        {
            try
            {
                var cd = FindPropOrFieldVal(cc, "customData");
                if (cd == null) { _skinCdNullCount++; return null; }
                try
                {
                    var init = cd.GetType().GetMethod("Init", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    init?.Invoke(cd, null);
                }
                catch { }
                return cd;
            }
            catch { return null; }
        }

        public static string[] GetAllPacketIdsWithSkins()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                var ids = GetPacketIds(true);
                try { ids.AddRange(GetPacketIds(false)); } catch { }
                foreach (var id in ids)
                {
                    try
                    {
                        var cfg = GetConfig(id);
                        if (cfg == null) continue;
                        var cc = GetCharConfig(cfg);
                        if (cc == null) continue;
                        var cd = GetCustomData(cc);
                        if (cd == null) continue;
                        // 皮肤数据源是 customList（Array<CharacterCustomConfig>），customDictionary 需 Init 才填充，可能为空
                        var list2 = FindPropOrFieldVal(cd, "customList");
                        bool hasSkin = false;
                        if (list2 is Godot.Collections.Array carr && carr.Count > 0) hasSkin = true;
                        else if (list2 is System.Collections.IEnumerable ienum)
                        {
                            foreach (var _ in ienum) { hasSkin = true; break; }
                        }
                        // 兜底：customDictionary 有内容也算
                        if (!hasSkin)
                        {
                            var dict = FindPropOrFieldVal(cd, "customDictionary");
                            hasSkin = dict is System.Collections.IDictionary idict && idict.Count > 0;
                        }
                        if (hasSkin)
                            list.Add(id);
                    }
                    catch { }
                }
                Bootstrap.Log("皮肤扫描: 带皮肤卡=" + list.Count + " customData空=" + _skinCdNullCount);
            }
            catch (System.Exception ex) { Bootstrap.Log("皮肤扫描异常: " + ex.Message); }
            return list.ToArray();
        }

        // ===== 分帧扫描（防假死）=====
        // GetAllPacketIdsWithSkins 同步遍历所有卡会触发主线程 LoadCharacterBindingOnMainThread + Task.Wait，
        // 配合资源节流导致主线程无限等待 → 打开 Mod 面板即假死。改为每帧只处理几个卡（主线程分帧，不阻塞）。
        static string[] _skinScanIds;
        static int _skinScanIdx;
        static System.Collections.Generic.List<string> _skinScanFound;
        const int _skinScanPerFrame = 5;
        /// <summary>扫描完成回调（主线程帧回调中触发，ModUI 用它更新 UI）。</summary>
        public static System.Action<string[]> OnSkinScanDone;

        /// <summary>开始分帧扫描带皮肤卡（ModUI 点"重新扫描"时调用）。</summary>
        public static void StartSkinScanFrameByFrame()
        {
            try
            {
                var ids = GetPacketIds(true);
                try { ids.AddRange(GetPacketIds(false)); } catch { }
                _skinScanIds = ids.ToArray();
                _skinScanIdx = 0;
                _skinScanFound = new System.Collections.Generic.List<string>();
                Bootstrap.Log("皮肤扫描: 开始分帧扫描 " + _skinScanIds.Length + " 张卡（每帧 " + _skinScanPerFrame + " 张）");
            }
            catch (System.Exception ex) { Bootstrap.Log("皮肤扫描启动异常: " + ex.Message); }
        }

        /// <summary>每帧调用（GameCheats.OnFrame）：处理几个卡，不阻塞主线程。返回 true = 已空闲/完成。</summary>
        public static bool TickSkinScan()
        {
            if (_skinScanIds == null) return true;
            try
            {
                int done = 0;
                while (_skinScanIdx < _skinScanIds.Length && done < _skinScanPerFrame)
                {
                    string id = _skinScanIds[_skinScanIdx++];
                    done++;
                    try
                    {
                        var cfg = GetConfig(id);
                        if (cfg == null) continue;
                        var cc = GetCharConfig(cfg);
                        if (cc == null) continue;
                        var cd = GetCustomData(cc);
                        if (cd == null) continue;
                        var list2 = FindPropOrFieldVal(cd, "customList");
                        bool hasSkin = false;
                        if (list2 is Godot.Collections.Array carr && carr.Count > 0) hasSkin = true;
                        else if (list2 is System.Collections.IEnumerable ienum) { foreach (var _ in ienum) { hasSkin = true; break; } }
                        if (!hasSkin)
                        {
                            var dict = FindPropOrFieldVal(cd, "customDictionary");
                            hasSkin = dict is System.Collections.IDictionary idict && idict.Count > 0;
                        }
                        if (hasSkin) _skinScanFound.Add(id);
                    }
                    catch { }
                }
                if (_skinScanIdx >= _skinScanIds.Length)
                {
                    var res = _skinScanFound.ToArray();
                    _skinScanIds = null;
                    _skinScanFound = null;
                    Bootstrap.Log("皮肤扫描: 分帧完成，带皮肤卡=" + res.Length + " customData空=" + _skinCdNullCount);
                    try { OnSkinScanDone?.Invoke(res); } catch { }
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>返回某卡的所有皮肤 key（customData.customList 的 customName，customDictionary 兜底）。</summary>
        public static string[] GetPacketSkins(string packetId)
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                var cfg = GetConfig(packetId);
                if (cfg == null) return list.ToArray();
                var cc = GetCharConfig(cfg);
                if (cc == null) return list.ToArray();
                var cd = GetCustomData(cc);
                if (cd == null) return list.ToArray();
                // 主源：customList 的 customName（customList 是泛型 Array<CharacterCustomConfig>，
                // `is Godot.Collections.Array` 不匹配 → 必须用 IEnumerable 遍历；元素是 CharacterCustomConfig Resource）
                var list2 = FindPropOrFieldVal(cd, "customList");
                if (list2 is System.Collections.IEnumerable ienum2)
                {
                    foreach (var item in ienum2)
                    {
                        try
                        {
                            if (item == null) continue;
                            object itemObj = item;
                            if (item is Godot.Variant gv)
                            {
                                if (gv.VariantType == Variant.Type.Nil) continue;
                                itemObj = gv.As<GodotObject>() ?? (object)gv;
                            }
                            var cn = FindPropOrFieldVal(itemObj, "customName");
                            if (cn is string s && s.Length > 0) list.Add(s);
                        }
                        catch { }
                    }
                }
                // 兜底：customDictionary 的键
                if (list.Count == 0)
                {
                    var dict = FindPropOrFieldVal(cd, "customDictionary");
                    if (dict is System.Collections.IDictionary idict)
                    {
                        foreach (var k in idict.Keys) list.Add(k as string ?? k.ToString());
                    }
                }
            }
            catch { }
            return list.ToArray();
        }

        /// <summary>启用某卡某皮肤：① 写游戏存档（卡片字典 Key.Custom=skinKey，重启保留 + 新生成植物生效）
        /// ② 触发 BattleEventBus.EmitCharacterSkinSwitched(packetId, skinKey) 实时换皮。
        /// 参考游戏自身装备按钮逻辑（InformationPanel::EquipmentButtonPressed）。</summary>
        /// <summary>开启 CommandManager.debugOpenAllCustom：图鉴里所有皮肤跳过 openKey 解锁检查可直接装备
        /// （皮肤有解锁条件时装备按钮禁用"未获得" → 开启不了）。</summary>
        static void SetDebugOpenAllCustom()
        {
            try
            {
                var cm = FindType("CommandManager");
                if (cm == null) return;
                object inst = null;
                var instF = cm.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instF != null) inst = instF.GetValue(null);
                if (inst == null) return;
                var f = cm.GetField("debugOpenAllCustom", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(bool) && !(bool)f.GetValue(inst))
                {
                    f.SetValue(inst, true);
                    Bootstrap.Log("皮肤: debugOpenAllCustom=true（全部皮肤可装备）");
                }
            }
            catch { }
        }

        public static bool UnlockSkin(string packetId, string skinKey)
        {
            try
            {
                // 0.5 开启 debugOpenAllCustom（图鉴所有皮肤可装备，解决 openKey 解锁条件导致的"开启不了"）
                SetDebugOpenAllCustom();
                // 0. 取存档键 saveKey（= config.saveKey，如 PlantXXXUnlockLoveKeyCustom）
                string saveKey = null;
                try
                {
                    var cfg = GetConfig(packetId);
                    if (cfg != null)
                    {
                        var sf = cfg.GetType().GetField("saveKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (sf != null) saveKey = sf.GetValue(cfg) as string;
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(saveKey)) saveKey = packetId;   // 兜底
                // 1. 写存档（关键：只发事件不写档 → 重启丢 + 新生成植物读不到 → "没用"）
                WriteSkinToSave(saveKey, skinKey);
                var busType = FindType("BattleEventBus");
                if (busType == null) { Bootstrap.Log("皮肤: 找不到 BattleEventBus"); return false; }
                object inst = null;
                try
                {
                    var p = busType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (p != null) inst = p.GetValue(null);
                }
                catch { }
                if (inst == null)
                {
                    try
                    {
                        var f = busType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                        if (f != null) inst = f.GetValue(null);
                    }
                    catch { }
                }
                if (inst == null) { Bootstrap.Log("皮肤: BattleEventBus.Instance 为空（已写档，实时换皮跳过）"); }
                else
                {
                    var emit = busType.GetMethod("EmitCharacterSkinSwitched", new Type[] { typeof(string), typeof(string) });
                    if (emit == null) { Bootstrap.Log("皮肤: 找不到 EmitCharacterSkinSwitched（已写档）"); }
                    else
                    {
                        // 游戏自身用 saveKey 作为事件第一参数（订阅方按 saveKey 匹配），不能传 packetId
                        emit.Invoke(inst, new object[] { saveKey, skinKey });
                        Bootstrap.Log("皮肤: 已写档+实时换皮 " + packetId + "/" + skinKey + " saveKey=" + saveKey);
                    }
                }
                _unlockedSkins[packetId] = skinKey;
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("启用皮肤异常: " + ex.Message); return false; }
        }

        /// <summary>把皮肤 key 写入游戏存档（卡片字典 Key.Custom），并触发保存。
        /// 参考 InformationPanel::EquipmentButtonPressed 的 IL：GetTowerDefensePacketValue → Key.Custom → SetTowerDefensePacketValue → Save。</summary>
        static void WriteSkinToSave(string saveKey, string custom)
        {
            try
            {
                var gsmType = FindType("GameSaveManager");
                if (gsmType == null) { Bootstrap.Log("皮肤写档: 找不到 GameSaveManager"); return; }
                object inst = null;
                var instF = gsmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instF != null) inst = instF.GetValue(null);
                if (inst == null) { Bootstrap.Log("皮肤写档: GameSaveManager.Instance 为空"); return; }
                var getVal = gsmType.GetMethod("GetTowerDefensePacketValue", new Type[] { typeof(string) });
                var setVal = gsmType.GetMethod("SetTowerDefensePacketValue", new Type[] { typeof(string), typeof(Godot.Collections.Dictionary) });
                if (getVal == null || setVal == null) { Bootstrap.Log("皮肤写档: 找不到 Get/SetTowerDefensePacketValue"); return; }
                Godot.Collections.Dictionary dict = null;
                try { dict = getVal.Invoke(inst, new object[] { saveKey }) as Godot.Collections.Dictionary; } catch { }
                if (dict == null) dict = new Godot.Collections.Dictionary();
                Godot.Collections.Dictionary keyDict;
                try
                {
                    if (dict.ContainsKey("Key") && dict["Key"].VariantType != Variant.Type.Nil)
                        keyDict = dict["Key"].AsGodotDictionary();
                    else keyDict = new Godot.Collections.Dictionary();
                }
                catch { keyDict = new Godot.Collections.Dictionary(); }
                keyDict["Custom"] = custom;
                dict["Key"] = keyDict;
                setVal.Invoke(inst, new object[] { saveKey, dict });
                // 触发存档落盘（防重启丢失）
                try
                {
                    var save = gsmType.GetMethod("ScheduleSave", Type.EmptyTypes);
                    if (save == null) save = gsmType.GetMethod("SaveGameConfig", Type.EmptyTypes);
                    save?.Invoke(inst, null);
                }
                catch { }
                Bootstrap.Log("皮肤写档: " + saveKey + " Custom=" + custom);
            }
            catch (System.Exception ex) { Bootstrap.Log("皮肤写档异常: " + ex.Message); }
        }

        /// <summary>皮肤全解：所有带皮肤的卡全部解锁（不随机，每卡启用第一个皮肤），
        /// 并开启 debugOpenAllCustom（图鉴里所有皮肤都可装备）——随机解锁改全部解锁。</summary>
        public static int UnlockAllSkins()
        {
            int n = 0;
            try
            {
                SetDebugOpenAllCustom();
                var cards = GetAllPacketIdsWithSkins();
                foreach (var id in cards)
                {
                    var skins = GetPacketSkins(id);
                    if (skins.Length == 0) continue;
                    if (UnlockSkin(id, skins[0])) n++;
                }
            }
            catch { }
            return n;
        }
    }
}

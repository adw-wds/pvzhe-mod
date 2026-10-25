using System;
using System.Reflection;
using Godot;

namespace PvzheMod
{
    /// <summary>
    /// 布阵器在**手机版**用到的 4 个辅助方法。
    ///
    /// 【为什么要这个文件】布阵器的核心逻辑（BoardSnapshot.cs + GameCheats.Board.cs）两端共用同一份源码，
    /// 但电脑版 GameCheats.cs 里有 4 个方法手机版没有 —— 编译器实测只缺这 4 个（CS0103 x11）：
    ///     IsPlantEntity / ReadEntityReal / WriteEntityReal / SpawnPacketAt
    /// 这里把电脑版实现**原样搬过来**，不重写、不"简化"，否则两端行为会漂移。
    ///
    /// 【依赖已在手机版存在（逐条确认过）】
    ///   FindFieldVal、FindFieldInfo、FindPropOrFieldVal、FindPlantFireComponent、
    ///   ApplyPlantHpDirect、ApplyPlantFireIntervalDirect、ApplyPlantCostDirect、
    ///   ApplyPlantCooldownDirect、FindMethodByPrefix、FillArgs、GetConfig、Bootstrap.Log
    /// 所以这里**不需要**再补别的；若以后手机版删了上面任何一个，编译会立刻报出来。
    /// </summary>
    public static partial class GameCheats
    {
        // ===== 实体属性「真实落点」读写（与电脑版 GameCheats.cs 同源）=====
        // 背景：实体节点的裸字段多为配置缓存/初始化值；真实玩法数值在
        //   instance（血量 hitpoints/hitpointsBase + hitpointScale 属性）、
        //   FireComponent（攻速 fireInterval）、transformPoint（大小 Scale）、
        //   config（阳光 overrideCost / 冷却 overridePacketCooldown）。
        internal static bool IsPlantEntity(Node2D n)
        {
            try { return n.GetType().Name.StartsWith("TowerDefensePlant", StringComparison.Ordinal); }
            catch { return false; }
        }

        /// <summary>读取实体语义字段当前真实值（hp/hitpointScale/scale/attack/cost/cooldown）。命中返回 true 并 out 数值。</summary>
        internal static bool ReadEntityReal(Node2D n, string prop, out double v)
        {
            v = 0;
            try
            {
                switch (prop)
                {
                    case "hp":
                    {
                        var inst = FindFieldVal(n, "instance");
                        var f = inst == null ? null : FindFieldInfo(inst, "hitpoints");
                        if (f == null) return false;
                        v = Convert.ToDouble(f.GetValue(inst), System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                    case "hitpointScale":
                    {
                        var inst = FindFieldVal(n, "instance");
                        if (inst == null) return false;
                        var sp = inst.GetType().GetProperty("hitpointScale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (sp != null && sp.PropertyType == typeof(double)) { v = (double)sp.GetValue(inst); return true; }
                        var f = FindFieldInfo(inst, "hitpointScale");
                        if (f != null) { v = Convert.ToDouble(f.GetValue(inst), System.Globalization.CultureInfo.InvariantCulture); return true; }
                        return false;
                    }
                    case "scale":
                    {
                        var tp = FindFieldVal(n, "transformPoint");
                        if (tp is Node2D nd && GodotObject.IsInstanceValid(nd)) { v = nd.Scale.X; return true; }
                        v = n.Scale.X; return true;
                    }
                    case "attack":
                    {
                        if (!IsPlantEntity(n)) return false;
                        var comp = FindPlantFireComponent(n);
                        if (comp == null) return false;
                        var t = comp.GetType();
                        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                        var f = t.GetField("fireInterval", bf);
                        if (f != null && f.FieldType == typeof(float)) { v = (double)(float)f.GetValue(comp); return true; }
                        var nfp = n.GetType().GetProperty("fireInterval", bf);
                        if (nfp != null && nfp.PropertyType == typeof(double)) { v = (double)nfp.GetValue(n); return true; }
                        return false;
                    }
                    case "cost":
                    {
                        if (!IsPlantEntity(n)) return false;
                        var cfg = FindPropOrFieldVal(n, "config");
                        var f = cfg == null ? null : FindFieldInfo(cfg, "overrideCost");
                        if (f == null) return false;
                        v = Convert.ToDouble(f.GetValue(cfg), System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                    case "cooldown":
                    {
                        if (!IsPlantEntity(n)) return false;
                        var cfg = FindPropOrFieldVal(n, "config");
                        var f = cfg == null ? null : FindFieldInfo(cfg, "overridePacketCooldown");
                        if (f == null) return false;
                        v = Convert.ToDouble(f.GetValue(cfg), System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>写入实体语义字段到真实对象。返回 "ok" 或 "err:原因"。</summary>
        internal static string WriteEntityReal(Node2D n, string prop, double v)
        {
            try
            {
                switch (prop)
                {
                    case "hp": ApplyPlantHpDirect(n, v); return "ok";
                    case "hitpointScale":
                    {
                        var inst = FindFieldVal(n, "instance");
                        if (inst == null) return "err:无 instance";
                        var t = inst.GetType();
                        var sp = t.GetProperty("hitpointScale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (sp != null && sp.PropertyType == typeof(double))
                        {
                            // ★ AOT 裁剪：绝不能用 PropertyInfo.SetValue，必须走 setter.Invoke
                            var setter = sp.GetSetMethod(true);
                            if (setter != null) { setter.Invoke(inst, new object[] { v }); return "ok"; }
                            return "err:hitpointScale 无 setter";
                        }
                        var f = FindFieldInfo(inst, "hitpointScale");
                        if (f != null && (f.FieldType == typeof(double) || f.FieldType == typeof(float)))
                        {
                            if (f.FieldType == typeof(double)) f.SetValue(inst, (double)v);
                            else f.SetValue(inst, (float)v);
                            return "ok";
                        }
                        return "err:hitpointScale 无控制点";
                    }
                    case "scale":
                    {
                        var s = (float)v;
                        var tp = FindFieldVal(n, "transformPoint");
                        if (tp is Node2D nd && GodotObject.IsInstanceValid(nd)) { nd.Scale = new Vector2(s, s); return "ok"; }
                        n.Scale = new Vector2(s, s); return "ok";
                    }
                    case "attack":
                        if (!IsPlantEntity(n)) return "err:仅植物有攻速";
                        ApplyPlantFireIntervalDirect(n, v); return "ok";
                    case "cost":
                        if (!IsPlantEntity(n)) return "err:仅植物有价格";
                        ApplyPlantCostDirect(n, v); return "ok";
                    case "cooldown":
                        if (!IsPlantEntity(n)) return "err:仅植物有冷却";
                        ApplyPlantCooldownDirect(n, v); return "ok";
                }
            }
            catch (Exception ex) { return "err:" + ex.Message; }
            return "err:未知字段 " + prop;
        }

        /// <summary>在指定格子生成指定卡牌（植物/僵尸，复用原生 TowerDefensePacketConfig.Plant 流程）。
        /// 布阵器还原时用它把实体「生出来」，之后再按快照写回状态。</summary>
        public static bool SpawnPacketAt(string id, int row, int col)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return false;
                var config = GetConfig(id);
                if (config == null) return false;
                var m = FindMethodByPrefix(config.GetType(), "Plant", new Type[] { typeof(Vector2I), typeof(bool), typeof(bool) });
                if (m == null) return false;
                m.Invoke(config, FillArgs(m, new object[] { new Vector2I(col, row), true, true }));
                return true;
            }
            catch (System.Exception ex) { Bootstrap.Log("外置刷物: " + id + " 异常 " + ex.Message); return false; }
        }

        // ==================== 手机版 ModUI 门面 ====================
        // 【为什么放在本文件】拿场景根节点的方式两端不同：
        //   电脑版从 RemoteServer.MainRoot() 拿；手机版在进程内，从 GameCheats._lastRoot / GetTreeRoot() 拿。
        // 所以门面只存在于手机版，而共用的 GameCheats.Board.cs 保持与电脑版逐字一致（不漂移）。

        static Node BoardUiRoot()
        {
            try { if (_lastRoot != null && GodotObject.IsInstanceValid(_lastRoot)) return _lastRoot; } catch { }
            try { return GetTreeRoot(); } catch { return null; }
        }

        /// <summary>ModUI 用：采集当前战场并写入关卡库。返回 MOD 端原文（"ok …" / "err:…"）。</summary>
        public static string BoardSaveUi(string name, string partsCsv)
        {
            var r = BoardUiRoot();
            if (r == null) return "err:拿不到场景根节点（先进游戏）";
            return BoardSave(r, name, partsCsv);
        }

        /// <summary>ModUI 用：关卡内直接铺开。不在关卡里时返回 err:（不替你进关）。</summary>
        public static string BoardLoadUi(string fileOrName, string partsCsv)
        {
            var r = BoardUiRoot();
            if (r == null) return "err:拿不到场景根节点（先进游戏）";
            return BoardLoad(r, fileOrName, partsCsv);
        }
    }
}

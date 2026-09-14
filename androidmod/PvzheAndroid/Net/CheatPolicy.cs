using System;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// 房主作弊策略（客户端强制层）。
/// 客机收到房主设置后：总闸关闭 = 强制关掉全部修改器；否则逐项按 CheatMask 关闭被禁功能。
/// 通过反射按字段名强制置零，因此两端（电脑/手机）共用同一份代码，字段缺失自动跳过。
/// 注意：这是“防君子”层，改过 MOD 的玩家可绕过；真正可靠需房主权威校验（M3 之后）。
/// </summary>
public static class CheatPolicy
{
    /// <summary>当前生效的房间设置（未联机时为默认：全允许）。</summary>
    public static RoomSettings Current = new RoomSettings();
    /// <summary>总闸是否被房主关闭（客机视角）。</summary>
    public static bool CheatsLocked { get; private set; }
    /// <summary>被禁用的位掩码（客机视角；房主恒为 0）。</summary>
    public static uint BlockedMask { get; private set; }

    /// <summary>字段名 → 作弊位。字段名与 PvzheMod.ModSettings 一致；缺失的字段会被安全跳过。</summary>
    static readonly Dictionary<string, uint> Map = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        // 资源无限
        { "InfiniteSun", CheatBits.Resource },
        { "InfiniteCoin", CheatBits.Resource },
        { "CrystalInfinite", CheatBits.Resource },
        // 卡牌便利
        { "NoCooldown", CheatBits.FreeCard },
        { "CannonNoCooldown", CheatBits.FreeCard },
        { "ZeroCost", CheatBits.FreeCard },
        { "NoCostRise", CheatBits.FreeCard },
        // 植物强化
        { "PlantFullRange", CheatBits.PlantPower },
        { "PlantTriple", CheatBits.PlantPower },
        { "PlantColumn", CheatBits.PlantPower },
        // 僵尸控制
        { "PlantNoAttack", CheatBits.ZombieCtl },
        { "ZombieNoAttack", CheatBits.ZombieCtl },
        { "ZombieNoMove", CheatBits.ZombieCtl },
        { "NoZombieSpawn", CheatBits.ZombieCtl },
        { "BossNoBow", CheatBits.ZombieCtl },
        { "NoThrowImp", CheatBits.ZombieCtl },
        // 无敌与免失败
        { "PlantInvincible", CheatBits.Invincible },
        { "ZombieInvincible", CheatBits.Invincible },
        { "IgnoreHouse", CheatBits.Invincible },
        { "IgnoreWarningLine", CheatBits.Invincible },
        { "IgnoreRedLine", CheatBits.Invincible },
        // 自动开火
        { "PlantAutoFire", CheatBits.AutoFire },
        // 篡改
        { "TrickEnabled", CheatBits.Trick },
        { "TrickBox", CheatBits.Trick },
        { "TrickRain", CheatBits.Trick },
        { "TrickConveyor", CheatBits.Trick },
        { "TrickFixedOnly", CheatBits.Trick },
        // 随机环境
        { "VaseRandom", CheatBits.RandomEnv },
        { "SeedBankRandom", CheatBits.RandomEnv },
        { "SeedBankRandomZombie", CheatBits.RandomEnv },
        { "SeedBankEveryFrame", CheatBits.RandomEnv },
        { "ForceRain", CheatBits.RandomEnv },
        { "ForceFog", CheatBits.RandomEnv },
        { "ConveyorFast", CheatBits.RandomEnv },
        { "RainFast", CheatBits.RandomEnv },
        // 趣味
        { "ZombieColor", CheatBits.Fun },
        { "ZombieDance", CheatBits.Fun },
        { "PlantSquash", CheatBits.Fun },
        { "JellyMode", CheatBits.Fun },
        { "BungiFastGrab", CheatBits.Fun },
        { "BungiIgnoreUmbrella", CheatBits.Fun },
        { "JackboxFastBomb", CheatBits.Fun },
        { "ZombiesFollowMouse", CheatBits.Fun },
        { "BungiSpawnJackbox", CheatBits.Fun },
        { "ShovelCherry", CheatBits.Fun },
        { "BloverClearAll", CheatBits.Fun },
        { "CharmPlant", CheatBits.Fun },
        { "CharmZombie", CheatBits.Fun },
        // 子弹
        { "BulletTrack", CheatBits.BulletMods },
        { "BulletFollowMouse", CheatBits.BulletMods },
        { "BulletRandom", CheatBits.BulletMods },
        // 清理与透视
        { "ClearCrater", CheatBits.Clear },
        { "FogESP", CheatBits.Clear },
        { "VaseESP", CheatBits.Clear },
        { "ESPEnabled", CheatBits.Clear },
        { "ESPZombie", CheatBits.Clear },
        { "ESPPlant", CheatBits.Clear },
        // 情报与解锁
        { "AlmanacAll", CheatBits.Info },
        { "IgnorePurple", CheatBits.Info },
        { "UnlockPackets", CheatBits.Info },
        { "CanChooseAll", CheatBits.Info },
        { "NoSleep", CheatBits.Info },
        { "ChomperFastSwallow", CheatBits.Info },
        { "InstantGrow", CheatBits.Info },
        // 其他便捷
        { "GloveMode", CheatBits.Misc },
        { "WavePaused", CheatBits.Misc },
        { "EngineLowRes", CheatBits.Misc },
        // 控制台
        { "ConsoleEnabled", CheatBits.Console },
        // 高级
        { "GlobalOverridesEnabled", CheatBits.Advanced },
        { "CustomLevelActive", CheatBits.Advanced },
        { "PerfMode", CheatBits.Advanced },
        // 默认开启的“便利型作弊”（房主禁用作弊时也要关，否则叠加种植/无视地形依旧生效）
        { "PlantOverlap", CheatBits.PlantPower },
        { "IgnoreTerrain", CheatBits.PlantPower },
    };

    /// <summary>
    /// 应用房间设置。房主与客机一致生效（房间禁用作弊 = 房里所有人都禁）。
    /// 仅在未联机（单人/离线）时不做任何限制。
    /// </summary>
    public static void Apply(RoomSettings s)
    {
        Current = s ?? new RoomSettings();
        if (NetSession.State == NetState.Offline)
        {
            CheatsLocked = false;
            BlockedMask = 0;
            return;
        }
        // ★★ 联机时**一律强制关闭全部修改器**（不再提供「允许作弊」总闸）。
        //   用户实测后明确要求：只要放开，玩家开的某些功能就会导致两边表现不一致
        //   （不同步）。所以只要在房间里，不管房间设置写什么，全部禁用。
        CheatsLocked = true;
        BlockedMask = CheatBits.All;
        // 开始限制前先暂存：这样玩家原来自己开的开关退房后能恢复
        TakeSnapshot();
        NetLog.Info("作弊策略更新：" + (NetSession.IsHost ? "房主" : "客机") +
                    "联机中强制关闭全部修改器");
    }

    /// <summary>重置为“无限制”（离开房间时调用）：先恢复进房前的开关，再清空策略。</summary>
    public static void Reset()
    {
        RestoreSnapshot();
        Current = new RoomSettings();
        CheatsLocked = false;
        BlockedMask = 0;
    }

    public static bool IsBlocked(uint bit) => (BlockedMask & bit) != 0;

    /// <summary>是否允许某个作弊位（供 UI 显示用）。</summary>
    public static bool Allowed(uint bit) => !IsBlocked(bit);

    // 数值类默认值表（禁用作弊时复位；比 Map 更全：血倍率/大小/游戏速度/炮多发等）
    static readonly Dictionary<string, object> NumDefaults = new Dictionary<string, object>(StringComparer.Ordinal)
    {
        { "PlantAttackSpeed", 1f }, { "ZombieAttackSpeed", 1f }, { "SpawnMultiplier", 1 },
        { "SunMultiplier", 1f }, { "PlantHP", 1f }, { "ZombieHP", 1f },
        { "PlantScale", 1f }, { "ZombieScale", 1f }, { "GameSpeed", 1f },
        { "CannonMultiShot", 1 },
    };

    /// <summary>字符串型作弊字段：禁用作弊时清空（自定义子弹 / 目标卡池）。</summary>
    static readonly string[] StrClear = { "BulletType", "TrickPool" };

    static readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
    static Type _modSettingsType;
    static ulong _lastEnforceMs;
    static bool _warned;

    // ---- 进房被禁前的状态快照（离开房间时原样恢复）----
    static readonly Dictionary<string, object> _snapBool = new Dictionary<string, object>(StringComparer.Ordinal);
    static readonly Dictionary<string, object> _snapNum = new Dictionary<string, object>(StringComparer.Ordinal);
    static readonly Dictionary<string, string> _snapStr = new Dictionary<string, string>(StringComparer.Ordinal);
    static bool _snapshotTaken;

    /// <summary>UI 用：本房间是否正在禁用作弊（选项应显示为已锁定）。</summary>
    public static bool IsLockedForUi => CheatsLocked;
    /// <summary>UI 用：已被临时锁住的开关数。</summary>
    public static int LockedCount => _snapBool.Count;

    static void ResolveModSettings()
    {
        if (_modSettingsType != null) return;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("PvzheMod.ModSettings", false);
            if (t != null) { _modSettingsType = t; return; }
        }
    }

    static FieldInfo Field(string name)
    {
        ResolveModSettings();
        if (_modSettingsType == null) return null;
        if (!_fields.TryGetValue(name, out var f))
        {
            f = _modSettingsType.GetField(name, BindingFlags.Public | BindingFlags.Static);
            _fields[name] = f;
        }
        return f;
    }

    /// <summary>
    /// 把即将被强制关闭的字段当前值全部记下来（只做一次，直到离开房间）。
    /// 这样玩家自己开的开关不会被永久抹掉，退房后能原样恢复。
    /// </summary>
    public static void TakeSnapshot()
    {
        if (_snapshotTaken) return;
        ResolveModSettings();
        if (_modSettingsType == null) return;
        _snapshotTaken = true;
        _snapBool.Clear(); _snapNum.Clear(); _snapStr.Clear();

        int n = 0;
        foreach (var kv in Map)
        {
            var f = Field(kv.Key);
            if (f == null || f.FieldType != typeof(bool)) continue;
            try { _snapBool[kv.Key] = f.GetValue(null); n++; } catch { }
        }
        foreach (var kv in NumDefaults)
        {
            var f = Field(kv.Key);
            if (f == null) continue;
            try { _snapNum[kv.Key] = f.GetValue(null); n++; } catch { }
        }
        for (int i = 0; i < StrClear.Length; i++)
        {
            var f = Field(StrClear[i]);
            if (f == null || f.FieldType != typeof(string)) continue;
            try { _snapStr[StrClear[i]] = (string)f.GetValue(null); n++; } catch { }
        }
        NetLog.Info("已暂存进房前的修改器状态（" + n + " 项），退出房间后原样恢复");
    }

    /// <summary>离开房间：把暂存的开关原样恢复（玩家自己开的照旧打开）。</summary>
    public static void RestoreSnapshot()
    {
        if (!_snapshotTaken) return;
        _snapshotTaken = false;
        int n = 0;
        foreach (var kv in _snapBool) { var f = Field(kv.Key); if (f != null) { try { f.SetValue(null, kv.Value); n++; } catch { } } }
        foreach (var kv in _snapNum) { var f = Field(kv.Key); if (f != null) { try { f.SetValue(null, kv.Value); n++; } catch { } } }
        foreach (var kv in _snapStr) { var f = Field(kv.Key); if (f != null) { try { f.SetValue(null, kv.Value); n++; } catch { } } }
        _snapBool.Clear(); _snapNum.Clear(); _snapStr.Clear();
        if (n > 0) NetLog.Info("已恢复进房前的修改器状态（" + n + " 项）");
    }

    /// <summary>
    /// 由 OnFrame 每秒调用一次：把被禁功能的开关强制关掉（含数值复位）。
    /// 采用“强制关字段”而不是逐个功能加判断，避免侵入几十处功能代码。
    /// </summary>
    public static void Tick(ulong nowMs)
    {
        if (BlockedMask == 0) return;
        // 250ms 节流：原来 1000ms 会留出“点开作弊爽一秒”的窗口；30 帧内必须被纠正。
        if (nowMs - _lastEnforceMs < 250) return;
        _lastEnforceMs = nowMs;

        if (_modSettingsType == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("PvzheMod.ModSettings", false);
                if (t != null) { _modSettingsType = t; break; }
            }
            if (_modSettingsType == null)
            {
                if (!_warned) { NetLog.Warn("找不到 ModSettings，作弊策略无法强制执行"); _warned = true; }
                return;
            }
        }

        int turnedOff = 0;
        foreach (var kv in Map)
        {
            if ((BlockedMask & kv.Value) == 0) continue;
            if (!_fields.TryGetValue(kv.Key, out var f))
            {
                f = _modSettingsType.GetField(kv.Key, BindingFlags.Public | BindingFlags.Static);
                _fields[kv.Key] = f;
            }
            if (f == null || f.FieldType != typeof(bool)) continue;
            try
            {
                if ((bool)f.GetValue(null))
                {
                    f.SetValue(null, false);
                    turnedOff++;
                }
            }
            catch { }
        }

        // 数值类：倍率/大小/速度全部复位（含血倍率、大小、游戏速度、炮多发——之前只复位了 4 项，
        // 导致“进房间前开的血倍率/大小”等禁不掉）
        foreach (var kv in NumDefaults) turnedOff += ResetNumeric(kv.Key, kv.Value);
        // 字符串类：自定义子弹 / 目标卡池清空
        for (int i = 0; i < StrClear.Length; i++) turnedOff += ClearString(StrClear[i]);

        if (turnedOff > 0) NetLog.Info("房主已禁用部分修改器：本次强制关闭 " + turnedOff + " 项");
    }

    /// <summary>把字符串型作弊字段清空（自定义子弹 / 目标卡池），返回是否真的清了一项。</summary>
    static int ClearString(string name)
    {
        try
        {
            if (_modSettingsType == null) return 0;
            if (!_fields.TryGetValue(name, out FieldInfo f))
            {
                f = _modSettingsType.GetField(name, BindingFlags.Public | BindingFlags.Static);
                _fields[name] = f;
            }
            if (f == null || f.FieldType != typeof(string)) return 0;
            var cur = f.GetValue(null) as string;
            if (!string.IsNullOrEmpty(cur)) { f.SetValue(null, ""); return 1; }
        }
        catch { }
        return 0;
    }

    static int ResetNumeric(string name, object def)
    {
        try
        {
            if (_modSettingsType == null) return 0;
            var f = _modSettingsType.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (f == null) return 0;
            object cur = f.GetValue(null);
            if (cur == null) return 0;
            double d = Convert.ToDouble(cur);
            double target = Convert.ToDouble(def);
            if (Math.Abs(d - target) < 0.0001) return 0;
            f.SetValue(null, Convert.ChangeType(def, f.FieldType));
            return 1;
        }
        catch { return 0; }
    }
}

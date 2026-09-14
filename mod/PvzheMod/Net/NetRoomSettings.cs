/// <summary>房主可调的房间设置（中继存储 + 广播；两端共用编解码）。</summary>
public class RoomSettings
{
    /// <summary>房间存活时间的硬上限（分钟）。UI 只允许填到这个值，服务端还会再夹一次，
    /// 防止旧客户端或手改包把房间放到更久。</summary>
    public const byte MaxTtlMinutes = 120;

    public byte MaxPlayers = 4;
    /// <summary>作弊总闸：false = 全员强制关闭修改器。</summary>
    public bool AllowCheats = true;
    /// <summary>逐项作弊白名单位掩码（1=允许）。
    /// ★ 低 30 位是作弊位，**高两位（bit30/bit31）被 RoomSettingsCodec 用来携带对战开关**，
    ///   所以这里不能是 0xFFFFFFFF，否则会被误读成“开了对战模式”。</summary>
    public uint CheatMask = RoomSettingsCodec.MaskBits;
    /// <summary>房间绝对存活时间（分钟；0 = 不限制）。</summary>
    public byte TtlMinutes = 120;
    public bool AllowLateJoin = true;
    /// <summary>权威同步（M2）：房主每秒下发阳光/波次，客机服从房主数值。</summary>
    public bool AuthoritySync = true;
    /// <summary>对战模式（取消时双方各自独立的阳光经济，僵尸方靠阳光墓碑产阳光）。
    ///
    /// ★ 存储位置说明（重要）：它实际编码在 <see cref="CheatMask"/> 的 bit30（平衡是 bit31），
    ///   而不是单独占一个字节。原因：线上中继跑的可能是不含本字段的旧代码，
    ///   旧编解码器读到 IpWhitelist 就停、会**忽略**追加的字节，并在广播时把它丢掉 ——
    ///   对战模式就被静默吞掉了（表现为“勾了还是合作模式”）。
    ///   而 CheatMask 是旧中继认识且会原样透传的字段，借它的空闲高位就能跨旧中继生效。</summary>
    public bool BattleMode;
    /// <summary>人数平衡（仅对战模式有意义）：阻止两边人数差超过 1，并给少人方少量补偿。</summary>
    public bool BalanceTeams = true;
    public string Password = "";
    public string IpWhitelist = "";

    public RoomSettings Clone()
    {
        return new RoomSettings
        {
            MaxPlayers = MaxPlayers,
            AllowCheats = AllowCheats,
            CheatMask = CheatMask,
            TtlMinutes = TtlMinutes,
            AllowLateJoin = AllowLateJoin,
            AuthoritySync = AuthoritySync,
            BattleMode = BattleMode,
            BalanceTeams = BalanceTeams,
            Password = Password ?? "",
            IpWhitelist = IpWhitelist ?? ""
        };
    }
}

/// <summary>作弊项位（与 MOD 功能分组对应，供 CheatMask 使用；房主可逐项允许/禁止）。</summary>
public static class CheatBits
{
    public const uint Resource = 1u << 0;    // 资源：无限阳光/金币/水晶
    public const uint FreeCard = 1u << 1;    // 卡牌便利：无冷却/零消费/不涨价/炮无冷却
    public const uint PlantPower = 1u << 2;  // 植物强化：攻速/全图射程/三倍种植/种一列
    public const uint ZombieCtl = 1u << 3;   // 僵尸控制：攻速/禁攻/禁移/禁出怪/僵王/小鬼
    public const uint Invincible = 1u << 4;  // 无敌与免失败：植物/僵尸无敌、无视进家、无视警戒线/红线
    public const uint AutoFire = 1u << 5;    // 自动开火
    public const uint Trick = 1u << 6;       // 篡改：盲盒/种子雨/传送带/抽卡植物
    public const uint RandomEnv = 1u << 7;   // 随机环境：罐子/卡槽/种子雨/传送带加速/强制雨雾
    public const uint Fun = 1u << 8;         // 趣味：变色/跳舞/果冻/飞贼/小丑/吸附/魅惑/铲子樱桃/三叶草
    public const uint BulletMods = 1u << 9;  // 子弹：追踪/跟随鼠标/随机
    public const uint Clear = 1u << 10;      // 清理与透视：清弹坑/迷雾透视/透视框/罐子透视
    public const uint Info = 1u << 11;       // 情报与解锁：图鉴全解/无视紫卡/清锁定卡/所有关选卡/不睡/秒熟
    public const uint Misc = 1u << 12;       // 其他便捷：手套/大嘴花/波次暂停/低配渲染
    public const uint Console = 1u << 13;    // 游戏原生控制台
    public const uint Advanced = 1u << 14;   // 全局属性覆盖 / 自制关卡 / 性能模式
    /// <summary>全部作弊位。★ 只到 bit29：bit30/bit31 留给 RoomSettingsCodec 传对战开关。</summary>
    public const uint All = 0x3FFFFFFFu;
}

/// <summary>作弊位的中文名（面板显示顺序）。</summary>
public static class CheatNames
{
    public static readonly uint[] Order = {
        CheatBits.Resource, CheatBits.FreeCard, CheatBits.PlantPower, CheatBits.ZombieCtl,
        CheatBits.Invincible, CheatBits.AutoFire, CheatBits.Trick, CheatBits.RandomEnv,
        CheatBits.Fun, CheatBits.BulletMods, CheatBits.Clear, CheatBits.Info,
        CheatBits.Misc, CheatBits.Console, CheatBits.Advanced
    };

    public static string Of(uint bit)
    {
        if (bit == CheatBits.Resource) return "资源无限";
        if (bit == CheatBits.FreeCard) return "卡牌便利";
        if (bit == CheatBits.PlantPower) return "植物强化";
        if (bit == CheatBits.ZombieCtl) return "僵尸控制";
        if (bit == CheatBits.Invincible) return "无敌免失败";
        if (bit == CheatBits.AutoFire) return "自动开火";
        if (bit == CheatBits.Trick) return "刷出物篡改";
        if (bit == CheatBits.RandomEnv) return "随机环境";
        if (bit == CheatBits.Fun) return "趣味作弊";
        if (bit == CheatBits.BulletMods) return "子弹强化";
        if (bit == CheatBits.Clear) return "清理与透视";
        if (bit == CheatBits.Info) return "情报与解锁";
        if (bit == CheatBits.Misc) return "其他便捷";
        if (bit == CheatBits.Console) return "原生控制台";
        if (bit == CheatBits.Advanced) return "高级覆盖";
        return "未知";
    }
}

/// <summary>RoomSettings 的二进制编解码（两端必须一致）。
///
/// ★ 对战开关不单独占字节，而是编码在 CheatMask 的 bit30/bit31：
///   旧中继不认新字段会把它丢掉，但 CheatMask 是它认识且会原样透传的，
///   借空闲高位传输就能让新客户端经过旧中继时依然拿到正确的模式。</summary>
public static class RoomSettingsCodec
{
    /// <summary>对战模式位（CheatMask bit30）。</summary>
    public const uint ModeBattle = 1u << 30;
    /// <summary>人数平衡位（CheatMask bit31）。</summary>
    public const uint ModeBalance = 1u << 31;
    /// <summary>剔除模式位后的纯作弊掩码（低 30 位）。</summary>
    public const uint MaskBits = 0x3FFFFFFFu;

    public static byte[] Write(RoomSettings s, byte flags = 0)
    {
        s = s ?? new RoomSettings();
        var w = new BitWriter();
        w.WriteByte(flags);
        w.WriteByte(s.MaxPlayers);
        w.WriteByte((byte)(s.AllowCheats ? 1 : 0));
        // CheatMask 低 30 位是真正的作弊白名单，高两位承载对战开关
        uint mask = (s.CheatMask & MaskBits);
        if (s.BattleMode) mask |= ModeBattle;
        if (s.BalanceTeams) mask |= ModeBalance;
        w.WriteU32(mask);
        w.WriteByte(s.TtlMinutes);
        w.WriteByte((byte)(s.AllowLateJoin ? 1 : 0));
        w.WriteByte((byte)(s.AuthoritySync ? 1 : 0));
        w.WriteStr(s.Password ?? "");
        w.WriteStr(s.IpWhitelist ?? "");
        return w.ToArray();
    }

    public static RoomSettings Read(byte[] payload)
    {
        var s = new RoomSettings();
        var r = new BitReader(payload);
        r.ReadByte();                                  // flags（由调用方决定语义）
        s.MaxPlayers = r.ReadByte();
        s.AllowCheats = r.ReadByte() != 0;
        uint mask = r.ReadU32();
        s.BattleMode = (mask & ModeBattle) != 0;
        s.BalanceTeams = (mask & ModeBalance) != 0;
        s.CheatMask = mask & MaskBits;
        s.TtlMinutes = r.ReadByte();
        s.AllowLateJoin = r.ReadByte() != 0;
        s.AuthoritySync = r.ReadByte() != 0;
        s.Password = r.ReadStr();
        s.IpWhitelist = r.ReadStr();
        return s;
    }
}

/// <summary>RoomSettings 消息的负载标志。</summary>
public static class RoomSettingsFlags
{
    public const byte Full = 0;        // 完整设置（房主下发 / 加入时同步）
    public const byte Patch = 1;       // 增量（预留）
}

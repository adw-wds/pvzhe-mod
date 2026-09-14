using System.Collections.Generic;

/// <summary>房间内一名玩家（中继下发；昵称/颜色槽用于名单显示与联机光标）。</summary>
public class PeerInfo
{
    public ushort Id;
    public string Nick = "";
    /// <summary>颜色槽 0..7（中继分配，房间内唯一，离开即释放）。</summary>
    public byte Color;
    public bool IsHost;
    /// <summary>该玩家的 X25519 公钥（32 字节；v3 房间密钥分发用；旧数据可能为 null）。</summary>
    public byte[] PublicKey;
    /// <summary>对战模式阵营（v4；NetFaction.None/Plant/Zombie）。合作模式下恒为 None。</summary>
    public byte Faction;

    public PeerInfo Clone()
    {
        return new PeerInfo { Id = Id, Nick = Nick, Color = Color, IsHost = IsHost, PublicKey = PublicKey, Faction = Faction };
    }
}

/// <summary>对战模式的阵营（v4）。</summary>
public static class NetFaction
{
    public const byte None = 0;
    public const byte Plant = 1;
    public const byte Zombie = 2;

    public static string Name(byte f)
    {
        if (f == Plant) return "植物方";
        if (f == Zombie) return "僵尸方";
        return "未选";
    }
}

/// <summary>玩家名单消息（MsgPlayerList）编解码：count + [id:u16, flags:u8, color:u8, pubLen:u8, pub, nick:str]×N。</summary>
public static class PeerListCodec
{
    public const byte FlagHost = 1;
    /// <summary>v4：阵营存在 flags 的 bit1..2（0=未选 1=植物方 2=僵尸方）。
    /// 复用已有字节而不是新增字段，名单帧长与 v3 保持一致。</summary>
    public const byte FactionMask = 0x06;
    public const int FactionShift = 1;

    public static byte[] Write(List<PeerInfo> players)
    {
        var w = new BitWriter();
        int n = players?.Count ?? 0;
        if (n > NetConstants.MaxPlayers) n = NetConstants.MaxPlayers;
        w.WriteByte((byte)n);
        for (int i = 0; i < n; i++)
        {
            var p = players[i] ?? new PeerInfo();
            w.WriteU16(p.Id);
            byte fl = (byte)(p.IsHost ? FlagHost : 0);
            fl |= (byte)((p.Faction & 0x03) << FactionShift);
            w.WriteByte(fl);
            w.WriteByte(p.Color);
            int kl = (p.PublicKey != null && p.PublicKey.Length == 32) ? 32 : 0;
            w.WriteByte((byte)kl);
            if (kl > 0) w.WriteBytes(p.PublicKey);
            w.WriteStr(Shorten(p.Nick));
        }
        return w.ToArray();
    }

    public static List<PeerInfo> Read(byte[] payload)
    {
        var list = new List<PeerInfo>();
        var r = new BitReader(payload);
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            var p = new PeerInfo();
            p.Id = r.ReadU16();
            byte flags = r.ReadByte();
            p.Color = r.ReadByte();
            int kl = r.ReadByte();
            if (kl > 0) p.PublicKey = r.ReadBytes(kl);
            p.Nick = r.ReadStr();
            p.IsHost = (flags & FlagHost) != 0;
            p.Faction = (byte)((flags & FactionMask) >> FactionShift);
            list.Add(p);
        }
        return list;
    }

    /// <summary>昵称限长（UTF-8 字节数与字符数双限，避免超长帧）。</summary>
    public static string Shorten(string nick)
    {
        if (string.IsNullOrEmpty(nick)) return "玩家";
        string s = nick.Trim();
        if (s.Length > NetConstants.MaxNickChars) s = s.Substring(0, NetConstants.MaxNickChars);
        return s;
    }
}

/// <summary>玩家颜色板（8 槽）：名单与联机光标共用。</summary>
public static class PeerColors
{
    /// <summary>ARGB 颜色（Godot Color 由 0xRRGGBB + Alpha=1 构造）。</summary>
    public static readonly uint[] Palette = {
        0xFF6B6B, // 红
        0x4DACFF, // 蓝
        0x06D6A0, // 青绿
        0xFFD166, // 金
        0xC77DFF, // 紫
        0xFF9F1C, // 橙
        0x2EC4B6, // 蓝绿
        0xF72585  // 品红
    };

    public const int Count = 8;

    /// <summary>取颜色（RGB float 分量均归一到 0..1）。</summary>
    public static void GetRgb(byte slot, out float r, out float g, out float b)
    {
        uint c = Palette[slot % Count];
        r = ((c >> 16) & 0xFF) / 255f;
        g = ((c >> 8) & 0xFF) / 255f;
        b = (c & 0xFF) / 255f;
    }
}

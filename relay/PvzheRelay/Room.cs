using System;
using System.Collections.Generic;

namespace PvzheRelay
{
    /// <summary>中继侧的玩家句柄；Send 由 RelayServer 注入（把帧写到该玩家的连接）。</summary>
    public class Player
    {
        public ushort Id;
        public string Nick = "";
        public string Ip = "";
        /// <summary>颜色槽 0..7（房间内唯一，用于名单显示与联机光标）。</summary>
        public byte Color;
        public Action<byte, byte[]> Send;
        /// <summary>盲转发用：直接写出已加密的 E2E 帧体（中继不碰内容）。</summary>
        public Action<byte, byte[]> SendRaw;
        /// <summary>中继侧 X25519 公钥（用于房主包裹房间密钥时定位成员）。</summary>
        public byte[] PublicKey;
        /// <summary>对战模式阵营（v4；NetFaction.None/Plant/Zombie）。合作模式下恒为 None。</summary>
        public byte Faction;
    }

    /// <summary>房间：成员、房间码、房主设置、TTL、广播。</summary>
    public class Room
    {
        public uint Id;
        public string Code = "";
        public int MaxPlayers = 4;
        public ushort HostId;
        public readonly List<Player> Players = new List<Player>();
        /// <summary>保护 Players / Started / MaxPlayers 等可变状态的锁（广播线程遍历时可能被其它线程修改）。</summary>
        public readonly object Sync = new object();
        /// <summary>房主可调设置（作弊总闸/掩码/口令/TTL/白名单…）。</summary>
        public RoomSettings Settings = new RoomSettings();
        /// <summary>绝对到期时刻（Environment.TickCount64；0 = 不限制）。</summary>
        public long ExpiresAtMs;
        /// <summary>是否已发过“即将到期”提醒。</summary>
        public bool ClosingSoonSent;
        /// <summary>创建时刻（日志用）。</summary>
        public long CreatedAtMs;
        /// <summary>对局是否已开始（已开始则不能重复开，直到 MsgBattleEnd 或房间销毁）。</summary>
        public bool Started;
        /// <summary>对局关卡标识（房主上报的 saveKey；未开战为空）。</summary>
        public string LevelKey = "";
        /// <summary>对局序号（每次开始自增，用于忽略过期的结束消息）。</summary>
        public uint BattleId;

        public Player Find(ushort id)
        {
            lock (Sync)
            {
                foreach (var p in Players) if (p.Id == id) return p;
            }
            return null;
        }

        /// <summary>当前人数（线程安全）。</summary>
        public int PlayerCount { get { lock (Sync) return Players.Count; } }

        /// <summary>指定阵营的人数（线程安全；对战模式分边仲裁用）。</summary>
        public int CountFaction(byte faction)
        {
            lock (Sync)
            {
                int n = 0;
                foreach (var p in Players) if (p != null && p.Faction == faction) n++;
                return n;
            }
        }

        /// <summary>玩家名单消息负载（MsgPlayerList）：id/房主标志/颜色槽/昵称。</summary>
        public byte[] PlayerList()
        {
            var list = new System.Collections.Generic.List<PeerInfo>();
            lock (Sync)
            {
                list.Capacity = Players.Count;
                foreach (var p in Players)
                {
                    if (p == null) continue;
                    // ★ 必须带上公钥：房主要用它给每个成员包裹房间密钥，客机要用房主那条解包。
                    //   漏了的话 → 分发时 mask==0 直接返回 → E2E 永远启用不了 →
                    //   对局状态/僵尸快照/光标/聊天全部发不出去（表现为“能开战但没同步”）。
                    list.Add(new PeerInfo
                    {
                        Id = p.Id,
                        Nick = p.Nick ?? "",
                        Color = p.Color,
                        IsHost = p.Id == HostId,
                        PublicKey = p.PublicKey,
                        Faction = p.Faction,
                    });
                }
            }
            return PeerListCodec.Write(list);
        }

        /// <summary>向房间内所有人（含房主）广播玩家名单。</summary>
        public void BroadcastPlayerList()
        {
            BroadcastExcept(0, NetProto.MsgPlayerList, PlayerList());
        }

        /// <summary>向除 fromId 以外的所有成员广播（fromId 传 0 表示不排除任何人）。
        /// 先快照再发送，避免广播过程中成员列表被其它线程修改。</summary>
        public void BroadcastExcept(ushort fromId, byte type, byte[] payload)
        {
            Player[] snapshot;
            lock (Sync) snapshot = Players.ToArray();
            foreach (var p in snapshot)
            {
                if (p == null || p.Id == fromId || p.Send == null) continue;
                try { p.Send(type, payload); } catch { }
            }
        }

        /// <summary>
        /// 端到端加密帧的盲转发：**不改动任何字节**（含 senderId/seq/密文/tag）。
        /// 中继无法解密，也不需要解密——它只负责按 type 投递。
        /// </summary>
        public void BroadcastRawExcept(ushort fromId, byte type, byte[] rawBody)
        {
            Player[] snapshot;
            lock (Sync) snapshot = Players.ToArray();
            foreach (var p in snapshot)
            {
                if (p == null || p.Id == fromId || p.SendRaw == null) continue;
                try { p.SendRaw(type, rawBody); } catch { }
            }
        }
    }
}

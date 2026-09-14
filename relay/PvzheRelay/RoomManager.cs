using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace PvzheRelay
{
    /// <summary>带错误码的中继异常（错误码与 NetProto.Err* 一致）。</summary>
    public class RelayException : Exception
    {
        public byte Code { get; }
        public RelayException(byte code, string msg) : base(msg) { Code = code; }
    }

    /// <summary>房间管理（纯逻辑，无线程/网络依赖，便于单测）。所有公开方法线程安全。</summary>
    public class RoomManager
    {
        readonly object _lock = new object();
        readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>();
        readonly Dictionary<ushort, Room> _playerRoom = new Dictionary<ushort, Room>();
        uint _nextRoomId = 1;
        ushort _nextPlayerId = 1;

        /// <summary>建房；host 由调用方（连接）提供，中继只分配 Id —— 保证房间持有的是连接自身的 Player 对象。</summary>
        public Room Create(int maxPlayers, Player host)
        {
            lock (_lock)
            {
                if (maxPlayers < 2) maxPlayers = 2;
                if (maxPlayers > 8) maxPlayers = 8;
                var room = new Room { Id = _nextRoomId++, MaxPlayers = maxPlayers, Code = NewCode() };
                room.CreatedAtMs = Environment.TickCount64;
                room.Settings = new RoomSettings { MaxPlayers = (byte)maxPlayers, TtlMinutes = (byte)NetConstants.DefaultRoomTtlMinutes };
                ApplyTtlLocked(room);
                host.Id = NewPlayerId();
                host.Color = 0;
                room.HostId = host.Id;
                lock (room.Sync) room.Players.Add(host);
                _rooms[room.Code] = room;
                _playerRoom[host.Id] = room;
                return room;
            }
        }

        /// <summary>加入房间（不带口令，兼容旧调用）。</summary>
        public Room Join(string code, Player guest) => Join(code, guest, "");

        /// <summary>加入房间：校验口令、是否允许中途加入、IP 白名单。</summary>
        public Room Join(string code, Player guest, string password)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(code) || !_rooms.TryGetValue(code, out var room))
                    throw new RelayException(NetProto.ErrNoRoom, "房间不存在");
                var st = room.Settings ?? new RoomSettings();
                lock (room.Sync)
                {
                    if (room.Players.Count >= room.MaxPlayers)
                        throw new RelayException(NetProto.ErrFull, "房间已满");
                    if (!string.IsNullOrEmpty(st.Password))
                    {
                        if (!string.Equals(st.Password, password ?? "", StringComparison.Ordinal))
                            throw new RelayException(NetProto.ErrBadPass, "房间口令错误");
                    }
                    if (room.Players.Count > 0 && !st.AllowLateJoin)
                        throw new RelayException(NetProto.ErrFull, "房主已禁止中途加入");
                    if (!IpAllowed(st.IpWhitelist, guest.Ip))
                        throw new RelayException(NetProto.ErrNoRoom, "来源 IP 不在白名单内");
                    guest.Id = NewPlayerId();
                    guest.Color = AllocColorLocked(room);
                    room.Players.Add(guest);
                }
                _playerRoom[guest.Id] = room;
                return room;
            }
        }

        /// <summary>分配房间内最小空闲颜色槽（0..7）；离开即自动释放。</summary>
        static byte AllocColorLocked(Room room)
        {
            for (byte c = 0; c < PeerColors.Count; c++)
            {
                bool used = false;
                foreach (var p in room.Players) if (p != null && p.Color == c) { used = true; break; }
                if (!used) return c;
            }
            return 0;
        }

        /// <summary>房主开始对局：仅房主可发起，且不能重复开（已开始抛 ErrBattleStarted）。</summary>
        public Room StartBattle(Player from, string levelKey)
        {
            lock (_lock)
            {
                var room = RoomOf(from);
                if (room == null) throw new RelayException(NetProto.ErrNotInRoom, "未加入房间");
                if (from.Id != room.HostId) throw new RelayException(NetProto.ErrNotHost, "只有房主可以开始对局");
                if (room.Started) throw new RelayException(NetProto.ErrBattleStarted, "对局已开始");
                room.Started = true;
                room.LevelKey = levelKey ?? "";
                room.BattleId++;
                return room;
            }
        }

        /// <summary>房主结束对局（Started 复位，之后可再次开始）。</summary>
        public Room EndBattle(Player from)
        {
            lock (_lock)
            {
                var room = RoomOf(from);
                if (room == null) throw new RelayException(NetProto.ErrNotInRoom, "未加入房间");
                if (from.Id != room.HostId) throw new RelayException(NetProto.ErrNotHost, "只有房主可以结束对局");
                room.Started = false;
                room.LevelKey = "";
                return room;
            }
        }

        /// <summary>IP 白名单校验：空 = 不限制；支持逗号分隔的完整 IP 或前缀（如 "119.181.60."）。</summary>
        static bool IpAllowed(string whitelist, string ip)
        {
            if (string.IsNullOrWhiteSpace(whitelist)) return true;
            if (string.IsNullOrEmpty(ip)) return false;
            foreach (var raw in whitelist.Split(','))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                if (ip.Equals(p, StringComparison.Ordinal)) return true;
                if (ip.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>房主修改房间设置（仅房主可调用）。</summary>
        public Room SetSettings(Player from, RoomSettings s)
        {
            lock (_lock)
            {
                var room = RoomOf(from);
                if (room == null) throw new RelayException(NetProto.ErrNotInRoom, "未加入房间");
                if (from.Id != room.HostId) throw new RelayException(NetProto.ErrNotInRoom, "只有房主可以修改房间设置");
                s = s ?? new RoomSettings();
                if (s.MaxPlayers < 2) s.MaxPlayers = 2;
                if (s.MaxPlayers > 8) s.MaxPlayers = 8;
                room.Settings = s;
                room.MaxPlayers = s.MaxPlayers;
                room.ClosingSoonSent = false;
                ApplyTtlLocked(room);
                return room;
            }
        }

        void ApplyTtlLocked(Room room)
        {
            int ttl = room.Settings?.TtlMinutes ?? 0;
            // ★ 硬上限 120 分钟：UI 只让填到这个数，但旧客户端 / 手改包可能塞更大的值，
            //   这里再夹一次才是真正的保证（0 = 不限时仍然允许）。
            if (ttl > RoomSettings.MaxTtlMinutes) ttl = RoomSettings.MaxTtlMinutes;
            room.ExpiresAtMs = ttl <= 0 ? 0 : Environment.TickCount64 + ttl * 60_000L;
        }

        /// <summary>内部：取房间快照（巡检用）。</summary>
        public List<Room> AllRooms()
        {
            lock (_lock) return new List<Room>(_rooms.Values);
        }

        /// <summary>解散房间（原因见 NetProto.Close*）。房间被移出管理表，但保留 Players 供调用方广播与断开。</summary>
        public Room Close(string code)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(code) || !_rooms.TryGetValue(code, out var room)) return null;
                _rooms.Remove(code);
                lock (room.Sync)
                {
                    foreach (var p in room.Players) _playerRoom.Remove(p.Id);
                }
                return room;
            }
        }

        /// <summary>玩家离开；返回其房间与“房间是否已空”。</summary>
        public (Room room, bool empty) Leave(Player p)
        {
            lock (_lock)
            {
                if (p == null || !_playerRoom.TryGetValue(p.Id, out var room)) return (null, false);
                _playerRoom.Remove(p.Id);
                lock (room.Sync)
                {
                    room.Players.RemoveAll(x => x.Id == p.Id);
                    if (room.Players.Count == 0)
                    {
                        _rooms.Remove(room.Code);
                        return (room, true);
                    }
                    // M1 只记录新 hostId，不做房主迁移的状态同步
                    if (room.HostId == p.Id) room.HostId = room.Players[0].Id;
                }
                return (room, false);
            }
        }

        public Room RoomOf(Player p)
        {
            lock (_lock)
            {
                if (p == null) return null;
                return _playerRoom.TryGetValue(p.Id, out var r) ? r : null;
            }
        }

        /// <summary>把 payload 交给目标玩家（用于交换候选地址）。</summary>
        public void Signal(Player from, ushort target, byte[] payload)
        {
            Player to;
            Room room;
            lock (_lock)
            {
                room = RoomOf(from);
                if (room == null) throw new RelayException(NetProto.ErrNotInRoom, "未加入房间");
                to = room.Find(target);
            }
            if (to?.Send == null) throw new RelayException(NetProto.ErrNotInRoom, "目标不可达");
            byte[] body = new BitWriter().WriteU16(from.Id).WriteBytes(payload ?? new byte[0]).ToArray();
            to.Send(NetProto.MsgSignalFrom, body);
        }

        /// <summary>转发业务数据；target=0 表示广播给房间内其他成员。</summary>
        public void Relay(Player from, ushort target, byte[] payload)
        {
            Room room;
            lock (_lock)
            {
                room = RoomOf(from);
                if (room == null) throw new RelayException(NetProto.ErrNotInRoom, "未加入房间");
            }
            byte[] body = new BitWriter().WriteU16(from.Id).WriteBytes(payload ?? new byte[0]).ToArray();
            if (target == 0)
            {
                room.BroadcastExcept(from.Id, NetProto.MsgRelayDataTo, body);
                return;
            }
            var to = room.Find(target);
            if (to?.Send == null) throw new RelayException(NetProto.ErrNotInRoom, "目标不可达");
            to.Send(NetProto.MsgRelayDataTo, body);
        }

        ushort NewPlayerId()
        {
            for (int i = 0; i < 64; i++)
            {
                if (_nextPlayerId == 0) _nextPlayerId = 1;
                ushort id = _nextPlayerId++;
                if (!_playerRoom.ContainsKey(id)) return id;
            }
            throw new RelayException(NetProto.ErrFull, "玩家 ID 耗尽");
        }

        string NewCode()
        {
            for (int attempt = 0; attempt < 500; attempt++)
            {
                var buf = new byte[2];
                RandomNumberGenerator.Fill(buf);
                string code = ((buf[0] | (buf[1] << 8)) % 10000).ToString("D4");
                if (!_rooms.ContainsKey(code)) return code;
            }
            throw new RelayException(NetProto.ErrProto, "房间码耗尽");
        }
    }
}

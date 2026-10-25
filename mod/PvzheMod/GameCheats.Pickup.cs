using System;
using Godot;

namespace PvzheMod
{
    /// <summary>中键 / 长按提取植物：对着场上种下的植物按【鼠标中键】（手机【长按 2 秒】）
    /// → 植物被"抓"到手上跟随鼠标，松手就落到当前格子（等于把它提起来换个地方种）。
    /// 复用 GameCheats.Glove 里已经验证过的拾取 / 落格 / 逻辑位置同步逻辑，不新造一套反射。</summary>
    public static partial class GameCheats
    {
        static bool _pkDown;          // 上一帧抓取键是否按下
        static bool _pkHeld;          // 保留（旧拖拽逻辑已不再使用）
        static Node2D _pkPlant;       // 保留
        static Vector2 _pkOffset;     // 保留
        static string _pkWantId;      // 待“拿在手上”的卡牌 id
        static int _pkTries;          // 重试帧数（新卡可能要下一帧才进卡槽）
        static float _pkHoldMs;       // 左键长按计时（手机触摸会被引擎模拟成左键）
        static Vector2 _pkHoldPos;
        const float PK_HOLD_MS = 2000f;    // 手机：长按 2 秒
        const float PK_MOVE_TOL = 30f;     // 长按期间允许的抖动（屏幕像素）

        public static void ApplyMiddlePickup(Node root)
        {
            try
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;

                // 有待选中的卡 → 每帧重试（新加的卡可能要下一帧才出现在卡槽里）
                if (_pkWantId != null)
                {
                    if (TryHoldPacket(root, _pkWantId)) { Bootstrap.Log("[提取] 已拿在手上: " + _pkWantId); _pkWantId = null; _pkTries = 0; }
                    else if (++_pkTries > 90) { Bootstrap.Log("[提取] 自动拿卡超时: " + _pkWantId); _pkWantId = null; _pkTries = 0; }
                }

                bool mid = Input.IsMouseButtonPressed(MouseButton.Middle);
                bool left = Input.IsMouseButtonPressed(MouseButton.Left);

                // 手机端：左键（=触摸）按住 ≥2 秒且几乎没移动 → 当作长按
                bool holdFire = false;
                if (!mid && left)
                {
                    Vector2 sp = MouseScreen(root);
                    if (_pkHoldMs <= 0f) { _pkHoldPos = sp; _pkHoldMs = 0.001f; }
                    else
                    {
                        _pkHoldMs += (float)root.GetProcessDeltaTime() * 1000f;
                        if (sp.DistanceTo(_pkHoldPos) > PK_MOVE_TOL) { _pkHoldMs = 0.001f; _pkHoldPos = sp; }
                        holdFire = _pkHoldMs >= PK_HOLD_MS;
                    }
                }
                else _pkHoldMs = 0f;

                bool down = mid || holdFire;
                if (down && !_pkDown)
                {
                    var p = FindPlantAtMouse(root);
                    if (p != null && GodotObject.IsInstanceValid(p))
                    {
                        string id = ReadPacketSaveKeyOf(p);
                        if (!string.IsNullOrEmpty(id))
                        {
                            bool ok = AddPacketToInventory(id);      // 复制一张同样的卡进卡槽
                            _pkWantId = null;                        // 不再自动拿在手上（用户要求：只复制到卡槽）
                            _pkTries = 0;
                            Bootstrap.Log("[提取] " + (ok ? "已复制卡牌 " : "加卡失败 ") + id + "（来源 " + p.GetType().Name + "）");
                        }
                        else Bootstrap.Log("[提取] 取不到卡牌 id: " + p.GetType().Name);
                    }
                }
                _pkDown = down;
            }
            catch { }
        }

        /// <summary>把卡槽里指定 id 的卡设为“拿在手上”。返回 true = 已成功。</summary>
        static bool TryHoldPacket(Node root, string id)
        {
            try
            {
                var tree = root.GetTree();
                Node start = tree != null && tree.Root != null ? tree.Root : root;
                return WalkHold(start, id);
            }
            catch { return false; }
        }

        static bool WalkHold(Node node, string id)
        {
            try
            {
                foreach (var child in node.GetChildren(true))
                {
                    try
                    {
                        if (child.GetType().Name == "TowerDefenseInGamePacketShow")
                        {
                            string cid = PacketShowId(child);
                            if (!string.IsNullOrEmpty(cid) && cid == id && SelectPacketShow(child)) return true;
                        }
                    }
                    catch { }
                    if (WalkHold(child, id)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>触发卡牌的“选中”状态：优先调游戏自己的方法，没有就直写 select 字段。</summary>
        static bool SelectPacketShow(Node ps)
        {
            try
            {
                var t = ps.GetType();
                foreach (var mn in new[] { "SetSelect", "Select", "SetSelected", "OnSelect" })
                {
                    var m = t.GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (m == null) continue;
                    var pars = m.GetParameters();
                    if (pars.Length == 0) { m.Invoke(ps, null); return true; }
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(bool)) { m.Invoke(ps, new object[] { true }); return true; }
                }
                var f = t.GetField("select", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(bool)) { f.SetValue(ps, true); return true; }
                var pr = t.GetProperty("select", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (pr != null && pr.CanWrite && pr.PropertyType == typeof(bool)) { pr.SetValue(ps, true, null); return true; }
                // 兵底：真实点一下这张卡（游戏自己的点卡逻辑，一定能进入待种状态）
                return ClickPacketShow(ps);
            }
            catch { }
            return false;
        }

        /// <summary>把鼠标移到卡牌中心并模拟一次左键点击（之后还原鼠标位置）。</summary>
        static bool ClickPacketShow(Node ps)
        {
            try
            {
                if (!(ps is Control c)) return false;
                var vp = c.GetViewport();
                if (vp == null) return false;
                Vector2 center = c.GetGlobalRect().GetCenter();
                Vector2 old = vp.GetMousePosition();
                try { Input.WarpMouse(center); } catch { }
                Input.ParseInputEvent(new InputEventMouseMotion { Position = center, GlobalPosition = center });
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = center, GlobalPosition = center });
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = center, GlobalPosition = center });
                try { Input.WarpMouse(old); } catch { }
                return true;
            }
            catch { return false; }
        }

        static Vector2 MouseScreen(Node root)
        {
            try
            {
                var vp = root.GetViewport();
                if (vp != null) return vp.GetMousePosition();
            }
            catch { }
            return Vector2.Zero;
        }

        /// <summary>抓在手上：贴着鼠标走，同时同步游戏的逻辑位置缓存（否则会被物理帧拉回原位）。</summary>
        static void FollowMouse(Node2D plant, Vector2 offset)
        {
            try
            {
                Vector2 np = plant.GetGlobalMousePosition() + offset;
                plant.GlobalPosition = np;
                var setM = plant.GetType().GetMethod("SetLogicalGlobalPosition", new Type[] { typeof(Vector2) });
                if (setM != null) setM.Invoke(plant, new object[] { np });
            }
            catch { }
        }

        /// <summary>松手落格：复用 Glove 的 GetMapGridPos / GetMapCellPlantPos / TrySetPlantGrid。</summary>
        static void DropPickedPlant()
        {
            var plant = _pkPlant;
            _pkHeld = false;
            _pkPlant = null;
            try
            {
                if (plant == null || !GodotObject.IsInstanceValid(plant)) return;
                Vector2 pos = plant.GetGlobalMousePosition();
                var tdm = GetTdmInstance();
                if (tdm != null)
                {
                    if (_gloveMapGridPos == null)
                        _gloveMapGridPos = tdm.GetType().GetMethod("GetMapGridPos", new Type[] { typeof(Vector2) });
                    if (_gloveMapGridPos != null && _gloveMapGridPos.Invoke(tdm, new object[] { pos }) is Vector2I gp)
                    {
                        if (_getMapCellPlantPos == null)
                            _getMapCellPlantPos = tdm.GetType().GetMethod("GetMapCellPlantPos", new Type[] { typeof(Vector2I) });
                        if (_getMapCellPlantPos != null)
                            pos = (Vector2)_getMapCellPlantPos.Invoke(null, new object[] { gp });
                        TrySetPlantGrid(plant, gp);
                    }
                }
                plant.GlobalPosition = pos;
                var setM = plant.GetType().GetMethod("SetLogicalGlobalPosition", new Type[] { typeof(Vector2) });
                if (setM != null) setM.Invoke(plant, new object[] { pos });
                Bootstrap.Log("[提取植物] 放下 " + plant.GetType().Name);
            }
            catch { }
        }
    }
}

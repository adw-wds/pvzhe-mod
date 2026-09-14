using System;
using Godot;

namespace PvzheMod
{
    /// <summary>手套挪动：局中点击植物拿起、点空地放置（partial 拆分自 GameCheats）。</summary>
    public static partial class GameCheats
    {
        // ================= 手套挪动（局中移动植物） =================
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
                {
                    Vector2 np = _glovePlant.GetGlobalMousePosition() + _gloveOffset;
                    _glovePlant.GlobalPosition = np;
                    // 移动过程中也同步逻辑位置缓存（游戏用 physicsFrame 缓存位置，不同步会被拉回原位）
                    try
                    {
                        var setM = _glovePlant.GetType().GetMethod("SetLogicalGlobalPosition", new Type[] { typeof(Vector2) });
                        if (setM != null) setM.Invoke(_glovePlant, new object[] { np });
                    }
                    catch { }
                }
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
    }
}

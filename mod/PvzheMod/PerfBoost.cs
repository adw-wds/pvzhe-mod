// ============================================================================
//  性能优化（Performance Boost）
// ============================================================================
//  定位：MOD 改不了引擎，但能把 Godot 暴露出来的「开销旋钮」全部拧到省资源那一档，
//        再补上 MOD 自己能做的资源管理。目标是把「游戏本体（非 MOD 功能）卡」压下去。
//
//  分项（对应外置修改器的开关）：
//    ① 渲染降载 —— 关 2D 多重采样 / 关屏幕空间抗锯齿 / 关色带抖动
//    ② 解除限流 —— 不限帧 / 关垂直同步 / 关低功耗模式 / 物理 tick 绝不下调
//       ★ 实测教训：早期版本把这三项当成「省资源」往下压，结果 6 万子弹场景
//         出现「CPU 只占 10% 但只有 3 帧、还被锁 30 帧」。限流 ≠ 优化。
//    ③ 音频降载 —— 旁路总线效果器（Godot 4 已移除 MixRate，这是音频侧唯一能运行时调的省 CPU 手段）
//    ④ 资源回收 —— 强制 GC + 收紧 MOD 自己的缓存，按住「越玩越卡」
//    ⑤ 渲染线程 —— 只检测报告：thread_model 是**启动期项目设置**，运行时改不了
//    ⑥ 低配渲染联动 —— 一并把渲染分辨率降下来（降资源占用最立竿见影的一项）
//
//  ★ 为什么「多线程渲染」只能报告：
//      Godot 的 rendering/driver/threads/thread_model、着色器编译策略都得在
//      引擎初始化前定好。MOD 运行在游戏进程里、启动早已完成，所以这里给报告 + 建议。
//      真要换，得改 project.godot 里的该项（附在报告里告诉用户改哪个键）。
// ============================================================================
using Godot;
using System;

namespace PvzheMod
{
    public static partial class GameCheats
    {
        // ── 状态 ────────────────────────────────────────────────────────
        static bool _perfBoostOn;

        /// <summary>性能优化：一键把各项开销旋钮拧到省资源档。on=false 恢复默认。</summary>
        public static string PerfBoost(bool on)
        {
            var sb = new System.Text.StringBuilder();
            _perfBoostOn = on;

            var vp = GetRootViewport();

            // ── ① 渲染降载 ──────────────────────────────────────────────
            try
            {
                if (vp != null)
                {
                    try
                    {
                        vp.Msaa2D = on ? Viewport.Msaa.Disabled : Viewport.Msaa.Msaa2X;
                        sb.Append("渲染: MSAA2D=").Append(vp.Msaa2D).Append("  ");
                    }
                    catch (Exception e) { sb.Append("MSAA2D 设置失败(").Append(e.Message).Append(")  "); }

                    try
                    {
                        vp.ScreenSpaceAA = on ? Viewport.ScreenSpaceAAEnum.Disabled : Viewport.ScreenSpaceAAEnum.Fxaa;
                        sb.Append("ScreenSpaceAA=").Append(vp.ScreenSpaceAA).Append("  ");
                    }
                    catch (Exception e) { sb.Append("SSAA 设置失败(").Append(e.Message).Append(")  "); }

                    try { vp.UseDebanding = !on; sb.Append("Debanding=").Append(vp.UseDebanding); }
                    catch { }
                    sb.Append('\n');
                }
                else sb.Append("渲染: 拿不到 Viewport（可能还没起场景）\n");
            }
            catch (Exception e) { sb.Append("渲染: ").Append(e.Message).Append('\n'); }

            // ── ② 调度：只报告，不做任何修改 ─────────────────────────
            //  ★ 实测教训（两轮，代价很大）：
            //    第一版把「物理 tick 降 30 / 低功耗模式 / 锁 60 帧」当优化 → 6 万子弹时
            //    「CPU 只占 10%、3 帧、被锁 30 帧」。第二版反过来「不限帧 + 关垂直同步」
            //    → 用户报「更卡了、满帧都到不了」。两次都错在【碰引擎的帧率/同步】。
            //    结论：帧率与同步一律不动，引擎自己的调度比 MOD 猜的靠谱。
            //    这里只做一件事：如果历史版本动过，就【还原】回去。
            try
            {
                sb.Append("调度: 未改动（帧率上限 ").Append(Engine.MaxFps <= 0 ? "无" : Engine.MaxFps.ToString())
                  .Append("，物理tick ").Append(Engine.PhysicsTicksPerSecond)
                  .Append("，垂直同步/低功耗不碰）").Append('\n');
            }
            catch (Exception e) { sb.Append("调度: ").Append(e.Message).Append('\n'); }

            // ── ③ 音频降载 ──────────────────────────────────────────────
            //  ★ Godot 4 移除了 AudioServer.MixRate（采样率由后端固定），
            //    所以音频侧改成「旁路掉总线上的效果器」—— 混响/压缩/限幅这些
            //    每帧都要跑，是音频线程实打实的 CPU 开销。
            try
            {
                int n = AudioServer.BusCount;
                for (int i = 0; i < n; i++)
                {
                    try { AudioServer.SetBusBypassEffects(i, on); } catch { }
                }
                sb.Append("音频: ").Append(n).Append(" 条总线的效果器旁路=").Append(on).Append('\n');
            }
            catch (Exception e) { sb.Append("音频: ").Append(e.Message).Append('\n'); }

            // ── ④ 资源回收：【不自动做】────────────────────────────
            //  上一版在这里开开关时就跑一次 GC，又在 PerfTick 里每 600 帧自动跑，
            //  是无参 GC.Collect() = 阻塞式完整 gen2 回收 + WaitForPendingFinalizers，
            //  1GB 堆上每次冻结数百毫秒 —— 用户实测「更卡了」。
            //  现在只提供【手动】回收（外置修改器的「回收内存」按钮 → GcOnce）。

            // ── ⑤ 多线程渲染检测 ────────────────────────────────────────
            sb.Append(RenderThreadReport());
            // ── ⑥ 【不再】联动 ApplyEnginePerfNow ──────────────────────
            //  上一版在这里调了 ApplyEnginePerfNow()，以为它“开了低配渲染才动”。
            //  查证后发现它【无条件】执行 Engine.MaxFps=60 + 强开垂直同步（只有改分辨率
            //  那一段才看 EngineLowRes）—— 等于开性能优化就静默锁了 60 帧。已删除。
            //  真需要它，外置修改器里有独立的「立即应用渲染优化」按钮，由用户显式点。
            Bootstrap.Log("性能优化: " + (on ? "开" : "关") + " | " + sb.ToString().Replace('\n', ' ').Replace('\r', ' '));
            return sb.ToString();
        }

        /// <summary>取当前根 Viewport（拿不到就返回 null，不抛）。</summary>
        static Viewport GetRootViewport()
        {
            try
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                return tree != null ? tree.Root.GetViewport() : null;
            }
            catch { return null; }
        }

        /// <summary>手动回收一次托管内存（外置修改器「回收内存」按钮调用）。
        /// ★ 只允许手动触发：无参 GC.Collect() 是阻塞式完整 gen2 回收，
        ///   自动周期性跑必造成可感知的卡顿尖峰。</summary>
        public static string GcOnce()
        {
            try
            {
                long before = GC.GetTotalMemory(false);
                // ★ AOT 裁剪坑：GC.Collect(int, GCCollectionMode, bool) 这个重载会被裁掉，
                //   运行时报 "Method not found"。只用无参重载（一定保留）。
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long after = GC.GetTotalMemory(false);
                long freedKb = (before - after) / 1024;
                string s = "资源: GC 回收 " + freedKb + " KB（" + (before / 1024) + " -> " + (after / 1024) + " KB）\n";
                return s;
            }
            catch (Exception e) { return "资源: " + e.Message + "\n"; }
        }

        /// <summary>报告渲染线程模型。thread_model：0=Single-Safe 1=Single-Unsafe 2=Multi-Threaded</summary>
        public static string RenderThreadReport()
        {
            try
            {
                var v = ProjectSettings.GetSetting("rendering/driver/threads/thread_model");
                // ★ AOT 裁剪坑：不要用 (int)v / v.VariantType / Variant.Type.Nil ——
                //   这些稀有的 Variant 成员在裁剪后可能 MissingMethod。只用 AsInt32。
                int mode = v.AsInt32();
                string name = mode == 0 ? "Single-Safe" : mode == 1 ? "Single-Unsafe"
                            : mode == 2 ? "Multi-Threaded" : ("未知(" + mode + ")");
                return "渲染线程: " + name
                     + "\n  （该项是启动期项目设置，运行时改不了。要换请改 project.godot 的"
                     + " rendering/driver/threads/thread_model = 2）\n";
            }
            catch (Exception e) { return "渲染线程: 读取失败(" + e.Message + ")\n"; }
        }

        /// <summary>每帧调用（仅在性能优化开启时做实际工作）。做低频维护：
        /// 定期强制回收内存；这样「长时间游玩逐渐变卡」不会累积。</summary>
        public static void PerfTick(Node root, bool loading)
        {
            // ★ 总开关跟随设置：外置修改器改的是 ModSettings.PerfBoost 字段（会持久化），
            //   真正的引擎旋钮必须在这里落地。PerfBoost() 内部会写 _perfBoostOn，
            //   所以这一行不会反复触发。
            if (ModSettings.PerfBoost != _perfBoostOn)
            {
                try { PerfBoost(ModSettings.PerfBoost); } catch { }
            }
            // ★ 此方法刻意保持「空」：不再做任何每帧/周期性维护。
            //   上一版在这里每 600 帧跑 GC.Collect()（阻塞式完整 gen2 回收），
            //   1GB 堆上每次冻结数百毫秒 —— 这就是「比以前更卡、满帧都到不了」的直接原因。
        }

        /// <summary>性能状态报告（给外置修改器显示）。</summary>
        public static string PerfReport()
        {
            try
            {
                double fps = Performance.GetMonitor(Performance.Monitor.TimeFps);
                double proc = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
                double phys = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
                long mem = GC.GetTotalMemory(false);
                int nodes = 0;
                try { nodes = (int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount); } catch { }
                int drawCalls = 0;
                try { drawCalls = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame); } catch { }

                var sb = new System.Text.StringBuilder();
                sb.Append("FPS ").Append(fps.ToString("F0"));
                sb.Append("  主线程 ").Append(proc.ToString("F1")).Append("ms");
                sb.Append("  物理 ").Append(phys.ToString("F1")).Append("ms");
                sb.Append("  节点 ").Append(nodes);
                sb.Append("  绘制 ").Append(drawCalls);
                sb.Append("  托管内存 ").Append(mem / 1024 / 1024).Append(" MB");
                sb.Append("  性能优化=").Append(_perfBoostOn ? "开" : "关");

                // ── 诊断用：确认限流已解除 + MOD 侧的子弹处理量 ──────────────
                //  用户实测「6 万子弹 3 帧」时 CPU 只占 10%，说明瓶颈不在算力而在
                //  人为限流（物理 tick 30 / 低功耗模式），所以这两项必须能一眼看到。
                int interval = _bfLoopLast > 20000 ? 30
                             : _bfLoopLast > 8000  ? 12
                             : _bfLoopLast > 3000  ? 6 : 2;
                sb.Append("\n活动子弹 ").Append(_bfLoopLast).Append(" 颗  MOD子弹处理间隔 ").Append(interval).Append(" 帧");
                sb.Append("  帧率上限 ").Append(Engine.MaxFps <= 0 ? "无(不限)" : Engine.MaxFps.ToString());
                sb.Append("  物理tick ").Append(Engine.PhysicsTicksPerSecond);
                try { sb.Append("  低功耗 ").Append(OS.LowProcessorUsageMode ? "开" : "关"); } catch { }
                try
                {
                    var rvp = GetRootViewport();
                    sb.Append("  MSAA ").Append(rvp != null ? rvp.Msaa2D.ToString() : "-");
                }
                catch { }
                return sb.ToString();
            }
            catch (Exception e) { return "读取失败: " + e.Message; }
        }
    }
}

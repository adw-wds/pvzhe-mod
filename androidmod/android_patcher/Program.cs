using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// patcher：把 PvzheMod 的类型合并进主程序集，并注入调用点。
/// 用法：patcher
///  1) 从 .bak 恢复干净主程序集
///  2) 合并 PvzheMod 类型（Bootstrap / GlobalColor）
///  3) 注入 MainMenu._PhysicsProcess 开头: ldarg.0; call PvzheMod.GlobalColor::OnFrame(Godot.Node)
///  4) 保存
/// </summary>
class Program
{
    // 手机版：APK 解压目录的 arm64 程序集目录（0.28）
    static string gameDir = @"D:\杂交版028手机\assets\.godot\mono\publish\arm64";
    static string mainPath = Path.Combine(gameDir, "PlantsVsZombies.dll");
    static string backupPath = mainPath + ".clean";   // 手机版干净原版（首次自动从当前文件备份）
    static string modPath = @"androidmod\PvzheAndroid\bin\Release\net8.0\PvzheMod.dll";

    static int Main(string[] args)
    {
        // check 模式：打印合并后类型方法体的所有引用 Scope
        if (args.Length > 0 && args[0] == "check")
        {
            var cr = new DefaultAssemblyResolver();
            cr.AddSearchDirectory(gameDir);
            using (var m = ModuleDefinition.ReadModule(mainPath, new ReaderParameters { AssemblyResolver = cr }))
            {
                Console.WriteLine("== 所有 PvzheMod 类型中的 PvzheMod 残留引用（全量） ==");
                foreach (var t in m.Types)
                {
                    if (t.Namespace != "PvzheMod") continue;
                    ScanTypeRefs(t);
                }
                Console.WriteLine("== 非 PvzheMod 类型中的 PvzheMod 残留引用（合并的注入代码/属性等） ==");
                foreach (var t in m.Types)
                {
                    if (t.Namespace == "PvzheMod") continue;
                    ScanTypeRefs(t);
                }
            }
            return 0;
        }

        static void ScanTypeRefs(TypeDefinition t)
        {
            try
            {
                foreach (var f in t.Fields) CheckRef(f.FieldType, t.FullName + ".field:" + f.Name);
                foreach (var p in t.Properties) CheckRef(p.PropertyType, t.FullName + ".property:" + p.Name);
                foreach (var e in t.Events) CheckRef(e.EventType, t.FullName + ".event:" + e.Name);
                foreach (var meth in t.Methods)
                {
                    CheckRef(meth.ReturnType, t.FullName + "." + meth.Name + ".ret");
                    foreach (var p in meth.Parameters) CheckRef(p.ParameterType, t.FullName + "." + meth.Name + ".param:" + p.Name);
                    foreach (var gp in meth.GenericParameters)
                        foreach (var c in gp.Constraints) CheckRef(c.ConstraintType, t.FullName + "." + meth.Name + ".gpc:" + gp.Name);
                    if (meth.HasBody)
                    {
                        foreach (var v in meth.Body.Variables) CheckRef(v.VariableType, t.FullName + "." + meth.Name + ".var:" + v.Index);
                        foreach (var ins in meth.Body.Instructions)
                        {
                            if (ins.Operand is MethodReference mr) CheckRef(mr.DeclaringType, t.FullName + "." + meth.Name + ".ins:mr");
                            else if (ins.Operand is FieldReference fr) CheckRef(fr.DeclaringType, t.FullName + "." + meth.Name + ".ins:fr");
                            else if (ins.Operand is TypeReference tr) CheckRef(tr, t.FullName + "." + meth.Name + ".ins:tr");
                        }
                        foreach (var eh in meth.Body.ExceptionHandlers)
                            if (eh.CatchType != null) CheckRef(eh.CatchType, t.FullName + "." + meth.Name + ".catch");
                    }
                }
                foreach (var ca in t.CustomAttributes) CheckRef(ca.AttributeType, t.FullName + ".attr");
                foreach (var nt in t.NestedTypes) ScanTypeRefs(nt);
            }
            catch { }
        }

        static void CheckRef(TypeReference tr, string where)
        {
            while (tr != null)
            {
                if (tr.Scope is AssemblyNameReference anr && anr.Name == "PvzheMod")
                    Console.WriteLine("  残留 " + where + " : " + tr.FullName);
                if (tr is GenericInstanceType git)
                {
                    foreach (var ga in git.GenericArguments) CheckRef(ga, where + "<arg>");
                }
                else if (tr is ArrayType at) { tr = at.ElementType; continue; }
                else if (tr is ByReferenceType brt) { tr = brt.ElementType; continue; }
                else if (tr is PointerType pt) { tr = pt.ElementType; continue; }
                tr = tr.DeclaringType;
            }
        }

        // 1) 恢复干净主程序集（手机版无 .bak；首次运行时把当前原版备份为 .clean，之后每轮从 .clean 恢复）
        if (!File.Exists(backupPath) && File.Exists(mainPath))
        {
            File.Copy(mainPath, backupPath, true);
            Console.WriteLine("首次运行：已备份干净原版 -> " + backupPath);
        }
        if (File.Exists(backupPath))
        {
            File.Copy(backupPath, mainPath, true);
            Console.WriteLine("已从 .clean 恢复干净主程序集");
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(gameDir);
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };

        using (var main = ModuleDefinition.ReadModule(mainPath, rp))
        using (var mod = ModuleDefinition.ReadModule(modPath, rp))
        {
            // 2) [独立DLL方案] 不合并 PvzheMod 类型进主 DLL！
            // 根因（手机版闪退）：Mono.Cecil 把 mod 类型合并进主 DLL 后，手机版 mono 加载主 DLL 时
            // 静默崩溃/退出（无任何日志，连 Godot banner 都不打印；电脑版 x64 不受影响）。
            // 独立 DLL 方案：主 DLL 只注入调用点（call PvzheMod.GlobalColor::OnFrame），
            // 类型在独立 PvzheMod.dll 里，mono 运行时按需加载（publish 目录已含 PvzheMod.dll）。
            // MergeTypes(main, mod);
            // RedirectModRefs(main);
            // 注：不移除 PvzheMod AssemblyRef、不删除独立 PvzheMod.dll——
            // 实测移除 AssemblyRef 后 Godot Mono 报 "Failed to open assembly image" 启动闪退。

            // 3) 注入调用点（任意界面都会触发）：SceneManager + MainMenu 的 _PhysicsProcess
            InjectFrame(main, mod, "SceneManager", "_PhysicsProcess");
            InjectFrame(main, mod, "MainMenu", "_PhysicsProcess");

            // 3.5) 种植限制：CanPacketPlant 开关感知（PlantOverlap/IgnoreTerrain 开才 true；关=原始逻辑，解决关不掉）
            InjectReturnTrueIf(main, mod, "TowerDefenseCellInstance", "CanPacketPlant", "ShouldIgnorePlantLimit");

            // 3.6) 罐子透视：LightDetectionComponent.CheckShow 开关感知（VaseESP 开才 true）
            InjectReturnTrueIf(main, mod, "LightDetectionComponent", "CheckShow", "ShouldVaseESP");

            // 3.6.5) 无视紫卡限制：Unlock/IsUnlocked/HasRequiredPlantCover 开关感知（IgnorePurple 开才 true，关=原始逻辑）
            InjectReturnTrueIf(main, mod, "TowerDefensePacketConfig", "Unlock", "ShouldUnlockAll");
            InjectReturnTrueIf(main, mod, "GlobalFeatureManager", "IsUnlocked", "ShouldUnlockAll");
            // 紫卡前置植物：IgnorePurple 开时无视（alive 由阳光正常决定，不闪烁）
            InjectReturnTrueIf(main, mod, "TowerDefenseInGamePacketShow", "HasRequiredPlantCover", "ShouldUnlockAll");

            // 3.6.7) 取消植物数量限制：IsLimitGridNum 恒 false（基础种植修复，种太多达到上限后所有卡牌种不了）
            InjectReturnFalse(main, mod, "TowerDefensePacketConfig", "IsLimitGridNum");

            // 3.6.8) 卡价不涨价（金卡/重复购买 costRise=0）+ 零消费（GetCost=0，所有卡免费）
            // 金卡/波点卡价格走 GetCostBeforeModifiers / GetWavePointCost（GetCost 内部调，但金卡可能直接用）——一并注入
            InjectReturnZeroIf(main, mod, "TowerDefensePacketConfig", "GetCostRise", "ShouldNoCostRise");
            InjectReturnZeroIf(main, mod, "TowerDefensePacketConfig", "GetCost", "ShouldZeroCost");
            InjectReturnZeroIf(main, mod, "TowerDefensePacketConfig", "GetCostBeforeModifiers", "ShouldZeroCost");
            InjectReturnZeroIf(main, mod, "TowerDefensePacketConfig", "GetWavePointCost", "ShouldZeroCost");

            // 3.6.9) 所有关卡可选卡：TowerDefenseLevelConfig.get_packetBankMethod 开头，开则返回 CHOOSE(1)
            InjectReturnValueIf(main, mod, "TowerDefenseLevelConfig", "get_packetBankMethod", "ShouldForceChoose", 1);

            // 3.6.9b) 【已撤销】僵尸方强制 IZM 模式 —— 与电脑版一致。
            //   IZM 是整个玩法模式的开关，关卡进入/校验多处会读；全局强开会导致
            //   “进战斗别人进不去、在线类关卡进不去”。改用局部 CategoryChoose("Zombie")。

            // 3.6.6) 种一列出列：TowerDefensePacketConfig.Plant(Vector2I,...) 开头批量种植同列
            InjectPlantColumn(main, mod);

            // 3.6.6b) 对战模式禁卡/分区拦截 + 标靶僵尸免疫。
            //   ★ 必须排在 InjectPlantColumn 之后：它们把拦截插到方法最前，
            //     这样判定先执行，能一并跳过 OnPlantPlaced/OnPacketAboutToPlant 的副作用。
            //   与电脑版 mod/patcher/Program.cs 保持逐条对应，别只改一边。
            InjectPvpPlantBan(main, mod);
            InjectPvpImmunity(main, mod);

            // 3.7) 攻速：注入 AccelerateTimer 到 AttackComponent.BatchUpdateValidated 开头（v2，2026-08-21 恢复）。
            // 旧判断"BatchUpdateValidated 不常触发"是错的——0.26 里 PhysicsProcess 每帧调 BatchUpdateValidated
            // （get_WantsPhysicsProcess = timer>0，攻击计时期间每帧触发），AccelerateTimer 每帧直接加速 timer，
            // 不依赖 attackInterval/组件查找（ApplyAttackSpeed 每 5 帧设 attackInterval 实际未生效且会双重加速）。
            InjectAttackSpeedPatch(main, mod);

            // 3.8) 伤害补丁：TowerDefenseCharacterInstance（最终扣血点）注入伤害缩放/无敌屏蔽
            InjectInstanceDamageScaling(main, mod);

            // 3.9) 无冷却补丁：set_coldDownOpen 拦截冷却开启 + _PhysicsProcess/ApplyCachedRuntimeAvailability 强制清除
            InjectNoCooldown(main, mod);
            InjectForceUsable(main, mod);

            // 3.9.5) 铲子铲植物 hook：TowerDefenseCellInstance.Shovel 开头（铲植物瞬间触发，OnCellShovel 生成樱桃）
            InjectShovel(main, mod);

            // 3.10) 魅惑补丁：已移除 InjectCharm（BatchProcessUpdate 里 ApplyCharmPatch 强制 camp 反转会与游戏 Hypnoses buff 冲突，
            // 导致魅惑僵尸转头抽搐）。魅惑改由 GameCheats.CombatWalk 每帧调用游戏 Hypnoses()（buff 全权处理）实现。
            // InjectCharm(main, mod);

            // 3.10.5) 去除魅惑免疫：禁用 Hypnoses 免疫检查（unUseBuffFlags & 8 → & 0），海妖/僵王/带头盔也能魅惑
            InjectCharmImmuneBypass(main);

            // 3.11) 子弹追踪/随机/自定义：BulletField.Spawn 注入已移除（最终决定，勿恢复）！
            // 原因：Spawn 参数是 [In][IsReadOnly] ref BulletData，注入 stfld 写字段有 IL 验证错误，
            // 实测开启子弹功能时攻击卡死/随机不生效（readonly ref 写入被丢弃/异常）。
            // 改为 GameCheats.ApplyBulletModsRuntime 每帧运行时设置活动子弹（Spawn 完全原版，不卡）。
            // InjectBulletTrack(main, mod);

            // 3.12) 蘑菇白天不睡觉：SleepComponent.CanSleep 开头——开关开时返回 false
            InjectNoSleep(main, mod);

            // 3.13) 迷雾透视：TowerDefenseFog.SetCanVisible 拦截 visible=true（GameEntry 会重设迷雾）
            InjectFogHide(main, mod);

            // 3.14) 罐子随机：VaseContentComponent.DestroySet 开头注入 OnVaseAboutToBreak——每次砸罐子前随机内容
            InjectVaseBreakRandom(main, mod);

            // 3.15) 卡牌随机：TowerDefenseInGamePacketShow.Plant 开头注入 OnPacketAboutToPlant（在种一列之前）——
            //       每次种卡前把卡内容随机成随机植物（卡牌内东西随机，一直刷）
            InjectPacketPlantRandom(main, mod);

            // 3.16) 无法攻击/无法移动：注入攻击/移动状态机开头——开关开时直接 return（不破坏状态机，关=原始逻辑）
            InjectNoAttackAndNoMove(main, mod);

            // 3.17) Boss 不鞠躬 + 禁止投掷小鬼
            var gcB = mod.GetType("PvzheMod.GameCheats");
            if (gcB != null)
            {
                InjectVoidReturnIf(main, mod, gcB, "TowerDefenseZombieBoss", "HeadExitedEntered", "ShouldBossNoBow");
                InjectVoidReturnIf(main, mod, gcB, "ImpThrowerComponent", "SpawnImp", "ShouldNoThrowImp");
                // ★★ 客机不自己结算（不弹通关奖杯）—— 同电脑版，见 mod/patcher 里的详细注释
                foreach (var awardType in new[] { "TowerDefenseControlNew", "TowerDefenseLevelControl", "TowerDefenseControl" })
                    InjectVoidReturnIf(main, mod, gcB, awardType, "AwardCreate", "ShouldBlockClientAward");
                // 3.17b) 成长植物秒成熟：GrowUpComponent.PhysicsProcess 计时器拉满
                InjectInstantGrow(main, mod, gcB);
                // 3.17c) 礼盒盲盒篡改：PresentBox/Green 的 Explode 开头——开盒出目标卡
                InjectPresentBoxTrick(main, mod);
                InjectRandomPacketPlantTrick(main, mod);
            }

            // 3.18) 游戏本体卡顿修复：ResourceManager.RunResourceLoadOnMainThread 加载节流（同电脑版，实测稳定）
            InjectResourceLoadThrottle(main);
            // 3.18a) 0.27 加载界面卡顿优化：LoadBuiltInRoot（主线程资源加载统一入口）注入 ForceDrawThrottled（每 8 资源强制绘制）
            InjectForceDrawThrottle(main, mod);
            // ★ 3.18c) 已废弃：曾往 LoadUniqueResourceRoots 注入线程化预热 → 实测【启动闪退】，已移除。
            //   详见电脑版 Program.cs 同一处注释。【不要再加回来】

            // 3.18b) 角色后台加载节流（v2）：RunCharacterBackgroundWorkerLane worker 循环每任务 sleep 3ms——
            // 降 worker CPU 满载（渲染线程被挤掉 → 启动界面卡），只影响 worker 线程，
            // 不碰 LoadCharacterTaskContinuously（v1 开头 sleep 导致主界面/进游戏卡死，已改注入点）
            InjectCharacterLoadThrottle(main);

            // ★ 3.19) 角色场景懒加载：启动不加载 563 个角色场景，改为 GetCharacterScene 用到时才加载
            //   ① LoadUniquePackedSceneRoots 开头 → 返回占位字典替代真实加载
            //   ② GetCharacterScene 开头  → 确保该角色真身已加载并写回字典
            InjectLazyCharacterScene(main, mod);

            // 4) 保存
            // [二分完成] 大方法体（136数组/93字典）合并后 mono 崩溃——已通过拆分成小方法修复，清空代码移除
            string tmp = mainPath + ".tmp";
            main.Write(tmp);
            Console.WriteLine("patched -> " + tmp);
        }

        if (File.Exists(mainPath + ".tmp"))
        {
            File.Delete(mainPath);
            File.Move(mainPath + ".tmp", mainPath);
            Console.WriteLine("已替换主程序集");
        }
        return 0;
    }

    /// <summary>把 source 的所有类型（含嵌套）复制进 target（两阶段：先建壳，再填方法体）。</summary>
    static void MergeTypes(ModuleDefinition target, ModuleDefinition source)
    {
        var typeMap = new System.Collections.Generic.Dictionary<string, TypeDefinition>();
        // 阶段1：创建所有类型（含嵌套，如编译器生成的 <>O 委托缓存类）
        foreach (var type in source.Types.ToList())
            CreateTypeRec(target, type, null, typeMap);
        // 阶段2：填充方法体（此时所有类型已在 target，跨类型引用可正确重定向）
        foreach (var type in source.Types.ToList())
            FillBodiesRec(target, type, typeMap);
    }

    /// <summary>把合并后残留的 PvzheMod 程序集类型引用全部重定向到 target 内对应类型（根除运行时加载 PvzheMod 失败）。</summary>
    static void RedirectModRefs(ModuleDefinition target)
    {
        int fixedCount = 0;
        foreach (var t in target.Types.ToList())
            RedirectType(t, target, ref fixedCount);
        Console.WriteLine("重定向残留引用: " + fixedCount + " 处");
    }

    static void RedirectType(TypeDefinition t, ModuleDefinition target, ref int fixedCount)
    {
        try
        {
            foreach (var f in t.Fields)
            {
                var nr = RedirectRef(f.FieldType, target, ref fixedCount);
                if (nr != null) f.FieldType = nr;
            }
            foreach (var p in t.Properties)
            {
                var nr = RedirectRef(p.PropertyType, target, ref fixedCount);
                if (nr != null) p.PropertyType = nr;
            }
            foreach (var e in t.Events)
            {
                var nr = RedirectRef(e.EventType, target, ref fixedCount);
                if (nr != null) e.EventType = nr;
            }
            foreach (var m in t.Methods)
            {
                var nr = RedirectRef(m.ReturnType, target, ref fixedCount);
                if (nr != null) m.ReturnType = nr;
                foreach (var p in m.Parameters)
                {
                    var pnr = RedirectRef(p.ParameterType, target, ref fixedCount);
                    if (pnr != null) p.ParameterType = pnr;
                }
                foreach (var gp in m.GenericParameters)
                {
                    if (gp.Constraints.Count == 0) continue;
                    var cons = gp.Constraints.Select(c => c.ConstraintType).ToList();
                    gp.Constraints.Clear();
                    foreach (var c in cons)
                    {
                        var cnr = RedirectRef(c, target, ref fixedCount);
                        gp.Constraints.Add(new GenericParameterConstraint(cnr ?? c));
                    }
                }
                if (m.HasBody)
                {
                    for (int i = 0; i < m.Body.Variables.Count; i++)
                    {
                        var vnr = RedirectRef(m.Body.Variables[i].VariableType, target, ref fixedCount);
                        if (vnr != null) m.Body.Variables[i] = new VariableDefinition(vnr);
                    }
                    foreach (var eh in m.Body.ExceptionHandlers)
                    {
                        if (eh.CatchType == null) continue;
                        var cnr = RedirectRef(eh.CatchType, target, ref fixedCount);
                        if (cnr != null) eh.CatchType = cnr;
                    }
                }
            }
        }
        catch { }
        foreach (var nt in t.NestedTypes)
            RedirectType(nt, target, ref fixedCount);
    }

    /// <summary>重定向单个类型引用：若 Scope 是 PvzheMod 且 target 有对应类型则返回 target 内类型，否则返回 null。</summary>
    static TypeReference RedirectRef(TypeReference tr, ModuleDefinition target, ref int fixedCount)
    {
        try
        {
            if (tr is GenericInstanceType git)
            {
                var el = RedirectRef(git.ElementType, target, ref fixedCount);
                var n = new GenericInstanceType(el ?? git.ElementType);
                foreach (var a in git.GenericArguments)
                {
                    var ar = RedirectRef(a, target, ref fixedCount);
                    n.GenericArguments.Add(ar ?? a);
                }
                return n;
            }
            if (tr is ArrayType at)
            {
                var el = RedirectRef(at.ElementType, target, ref fixedCount);
                return el != null ? new ArrayType(el, at.Rank) : null;
            }
            if (tr is ByReferenceType brt)
            {
                var el = RedirectRef(brt.ElementType, target, ref fixedCount);
                return el != null ? new ByReferenceType(el) : null;
            }
            if (tr is PointerType pt)
            {
                var el = RedirectRef(pt.ElementType, target, ref fixedCount);
                return el != null ? new PointerType(el) : null;
            }
            if (tr.Scope is AssemblyNameReference anr && anr.Name == "PvzheMod")
            {
                var lt = target.GetType(tr.FullName);
                if (lt != null)
                {
                    fixedCount++;
                    return lt;
                }
            }
        }
        catch { }
        return null;
    }

    static void CreateTypeRec(ModuleDefinition target, TypeDefinition src, TypeDefinition parent,
        System.Collections.Generic.Dictionary<string, TypeDefinition> typeMap)
    {
        if (src.FullName == "<Module>") return;
        if (target.GetType(src.FullName) != null) { Console.WriteLine("类型已存在，跳过: " + src.FullName); return; }

        TypeReference baseRef = src.BaseType != null ? target.ImportReference(src.BaseType) : null;
        var nt = new TypeDefinition(src.Namespace, src.Name, src.Attributes, baseRef);
        if (parent != null) nt.DeclaringType = parent;
        nt.IsAbstract = src.IsAbstract;
        nt.IsSealed = src.IsSealed;

        foreach (var f in src.Fields)
        {
            var nf = new FieldDefinition(f.Name, f.Attributes, target.ImportReference(f.FieldType));
            if (f.HasConstant) nf.Constant = f.Constant;
            if (f.InitialValue != null) nf.InitialValue = f.InitialValue;
            nt.Fields.Add(nf);
        }

        foreach (var m in src.Methods)
        {
            var nm = new MethodDefinition(m.Name, m.Attributes, target.ImportReference(m.ReturnType));
            foreach (var p in m.Parameters)
                nm.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, target.ImportReference(p.ParameterType)));
            foreach (var gp in m.GenericParameters)
                nm.GenericParameters.Add(new GenericParameter(gp.Name, nm));
            if (m.HasBody) nm.Body.InitLocals = m.Body.InitLocals;
            nt.Methods.Add(nm);
        }

        if (parent != null) parent.NestedTypes.Add(nt);
        else target.Types.Add(nt);
        typeMap[src.FullName] = nt;
        Console.WriteLine("合并类型: " + src.FullName);

        foreach (var nested in src.NestedTypes)
            CreateTypeRec(target, nested, nt, typeMap);
    }

    static void FillBodiesRec(ModuleDefinition target, TypeDefinition src,
        System.Collections.Generic.Dictionary<string, TypeDefinition> typeMap)
    {
        if (typeMap.TryGetValue(src.FullName, out var nt))
        {
            foreach (var m in src.Methods)
            {
                if (!m.HasBody) continue;
                var nm = nt.Methods.FirstOrDefault(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count);
                if (nm == null || nm.Body == null) continue;
                CopyBody(nm.Body, m.Body, target);
            }
        }
        foreach (var nested in src.NestedTypes)
            FillBodiesRec(target, nested, typeMap);
    }

    static void CopyBody(MethodBody dest, MethodBody src, ModuleDefinition target)
    {
        // 局部变量
        foreach (var v in src.Variables)
            dest.Variables.Add(new VariableDefinition(target.ImportReference(v.VariableType)));

        // 第一遍：创建所有指令（占位 operand），并建立 旧->新 映射。
        // Instruction 的构造函数是 internal 的，用反射创建以支持任意 opcode。
        var ctor = typeof(Instruction).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(c => c.GetParameters().Length == 2 && c.GetParameters()[0].ParameterType == typeof(OpCode));
        var map = new System.Collections.Generic.Dictionary<Instruction, Instruction>();
        foreach (var ins in src.Instructions)
        {
            var newIns = (Instruction)ctor.Invoke(new object[] { ins.OpCode, null });
            map[ins] = newIns;
            dest.Instructions.Add(newIns);
        }

        // 第二遍：设置 operand（分支目标映射到新指令；引用类型 import 重定向）
        for (int i = 0; i < src.Instructions.Count; i++)
        {
            var srcIns = src.Instructions[i];
            var newIns = dest.Instructions[i];
            var op = srcIns.Operand;
            if (op is Instruction targetIns)
                newIns.Operand = map[targetIns];
            else if (op is Instruction[] targets)
                newIns.Operand = targets.Select(t => map[t]).ToArray();
            else
                newIns.Operand = ImportOperand(op, target);
        }

        // 异常处理器（简单复制，mod 里一般没有）
        foreach (var eh in src.ExceptionHandlers)
        {
            var neh = new ExceptionHandler(eh.HandlerType)
            {
                TryStart = map[eh.TryStart],
                TryEnd = map[eh.TryEnd],
                HandlerStart = map[eh.HandlerStart],
                HandlerEnd = map[eh.HandlerEnd],
            };
            if (eh.CatchType != null) neh.CatchType = target.ImportReference(eh.CatchType);
            dest.ExceptionHandlers.Add(neh);
        }
    }

    static object ImportOperand(object operand, ModuleDefinition target)
    {
        if (operand == null) return null;
        if (operand is MethodReference mr)
        {
            if (IsModScope(mr.DeclaringType))
            {
                var lt = target.GetType(mr.DeclaringType.FullName);
                if (lt != null)
                {
                    var lm = lt.Methods.FirstOrDefault(x => x.FullName == mr.FullName);
                    if (lm != null) return lm;
                }
            }
            return target.ImportReference(mr);
        }
        if (operand is TypeReference tr)
        {
            if (IsModScope(tr))
            {
                var lt = target.GetType(tr.FullName);
                if (lt != null) return lt;
            }
            return target.ImportReference(tr);
        }
        if (operand is FieldReference fr)
        {
            if (IsModScope(fr.DeclaringType))
            {
                var lt = target.GetType(fr.DeclaringType.FullName);
                if (lt != null)
                {
                    var lf = lt.Fields.FirstOrDefault(x => x.Name == fr.Name);
                    if (lf != null) return lf;
                }
            }
            return target.ImportReference(fr);
        }
        if (operand is string || operand is int || operand is long || operand is float ||
            operand is double || operand is byte || operand is short || operand is bool)
            return operand;
        return operand;
    }

    /// <summary>是否为待合并的 mod 程序集作用域。
    /// mod 自己类型的 Scope 是 ModuleDefinition（PvzheMod.dll），外部引用的 Scope 是 AssemblyNameReference。</summary>
    static bool IsModScope(TypeReference tr)
    {
        if (tr == null) return false;
        if (tr.Scope is AssemblyNameReference anr) return anr.Name == "PvzheMod";
        if (tr.Scope is ModuleDefinition md) return md.Name == "PvzheMod.dll";
        return false;
    }

    static string ScopeName(TypeReference tr)
    {
        if (tr == null) return "null";
        if (tr.Scope is AssemblyNameReference anr) return "asm:" + anr.Name;
        if (tr.Scope is ModuleDefinition md) return "mod:" + md.Name;
        return "other:" + tr.Scope;
    }

    /// <summary>注入指定类型 _PhysicsProcess 开头: ldarg.0; call PvzheMod.GlobalColor::OnFrame(Godot.Node)</summary>
    static void InjectFrame(ModuleDefinition main, ModuleDefinition mod, string typeName, string methodName)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == methodName);
        if (method == null || method.Body == null) { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName); return; }

        var first = method.Body.Instructions[0];
        if (first.OpCode == OpCodes.Call && first.Operand is MethodReference mr0 && mr0.Name == "OnFrame")
        {
            Console.WriteLine(typeName + "." + methodName + " 已注入过，跳过");
            return;
        }

        var il = method.Body.GetILProcessor();
        // [独立DLL] 从 mod 程序集引用 GlobalColor.OnFrame（类型不合并进 main，运行时装 PvzheMod.dll 解析）
        var modGc = mod.GetType("PvzheMod.GlobalColor");
        if (modGc == null) { Console.WriteLine("警告: mod 里找不到 PvzheMod.GlobalColor"); return; }
        var onFrame = modGc.Methods.FirstOrDefault(x => x.Name == "OnFrame");
        if (onFrame == null) { Console.WriteLine("警告: 找不到 GlobalColor.OnFrame"); return; }
        var onFrameRef = main.ImportReference(onFrame);

        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(onFrameRef)));
        Console.WriteLine("已注入 " + typeName + "." + methodName + " 开头 -> [PvzheMod]GlobalColor.OnFrame");
    }

    /// <summary>0.27 加载界面卡顿优化：ResourceManager.LoadBuiltInRoot 开头注入 ForceDrawThrottled（每 8 资源强制绘制一帧）。
    /// 独立 DLL 方案：从 mod（PvzheMod.dll）引用 GameCheats.ForceDrawThrottled。</summary>
    static void InjectForceDrawThrottle(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("ResourceManager");
        if (t == null) { Console.WriteLine("警告: 找不到 ResourceManager"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "LoadBuiltInRoot" && x.HasBody);
        if (method == null) { Console.WriteLine("警告: 找不到 ResourceManager.LoadBuiltInRoot"); return; }
        if (HasInjectedCall(method, "ForceDrawThrottled")) { Console.WriteLine("LoadBuiltInRoot 强制绘制已注入，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var fd = gc.Methods.FirstOrDefault(x => x.Name == "ForceDrawThrottled" && x.IsStatic);
        if (fd == null) { Console.WriteLine("警告: 找不到 ForceDrawThrottled"); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(fd)));
        Console.WriteLine("已注入 ResourceManager.LoadBuiltInRoot -> 强制绘制节流(每8资源)");
    }

    /// <summary>角色场景懒加载（手机版，独立程序集 → 需 ImportReference）。
    /// ① LoadUniquePackedSceneRoots(paths, category)：注入
    ///      ldarg.1; ldarg.2; call TryLazyCharacterSceneRoots
    ///      dup; brfalse.s POP_AND_ORIG; ret; POP_AND_ORIG: pop
    ///    → 返回非 null（占位字典）则直接 return 它，否则 pop 掉 null 继续原逻辑。
    /// ② GetCharacterScene(string)：注入 ldarg.1; call EnsureCharacterSceneLoaded —— 只插入调用、
    ///    不改控制流，原方法的字典查找随后就能查到刚写回的真身。</summary>
    static void InjectLazyCharacterScene(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("ResourceManager");
        if (t == null) { Console.WriteLine("警告: 找不到 ResourceManager"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }

        // ---- ① LoadUniquePackedSceneRoots ----
        var m1 = t.Methods.FirstOrDefault(x => x.Name == "LoadUniquePackedSceneRoots" && x.HasBody);
        var f1 = gc.Methods.FirstOrDefault(x => x.Name == "TryLazyCharacterSceneRoots" && x.IsStatic);
        if (m1 == null || f1 == null)
        {
            Console.WriteLine(m1 == null ? "跳过懒加载①（无 LoadUniquePackedSceneRoots）" : "警告: 找不到 TryLazyCharacterSceneRoots");
        }
        else if (HasInjectedCall(m1, "TryLazyCharacterSceneRoots")) Console.WriteLine("懒加载①已注入，跳过");
        else if (f1.Parameters.Count != 2)
        {
            Console.WriteLine("警告: TryLazyCharacterSceneRoots 签名不符（应为 2 参），跳过①");
        }
        else
        {
            var f1Ref = main.ImportReference(f1);
            var il1 = m1.Body.GetILProcessor();
            var first1 = m1.Body.Instructions[0];
            var popAndOrig = il1.Create(OpCodes.Pop);
            il1.InsertBefore(first1, il1.Create(OpCodes.Ldarg_1));   // paths
            il1.InsertBefore(first1, il1.Create(OpCodes.Ldarg_2));   // category
            il1.InsertBefore(first1, il1.Create(OpCodes.Call, f1Ref));
            il1.InsertBefore(first1, il1.Create(OpCodes.Dup));
            il1.InsertBefore(first1, il1.Create(OpCodes.Brfalse_S, popAndOrig));
            il1.InsertBefore(first1, il1.Create(OpCodes.Ret));
            il1.InsertBefore(first1, popAndOrig);
            Console.WriteLine("已注入 LoadUniquePackedSceneRoots -> 角色场景懒加载(占位替代)");
        }

        // ---- ② GetCharacterScene ----
        var m2 = t.Methods.FirstOrDefault(x => x.Name == "GetCharacterScene" && x.HasBody);
        var f2 = gc.Methods.FirstOrDefault(x => x.Name == "EnsureCharacterSceneLoaded" && x.IsStatic);
        if (m2 == null || f2 == null)
        {
            Console.WriteLine(m2 == null ? "跳过懒加载②（无 GetCharacterScene）" : "警告: 找不到 EnsureCharacterSceneLoaded");
        }
        else if (HasInjectedCall(m2, "EnsureCharacterSceneLoaded")) Console.WriteLine("懒加载②已注入，跳过");
        else if (f2.Parameters.Count != 1)
        {
            Console.WriteLine("警告: EnsureCharacterSceneLoaded 签名不符（应为 1 参），跳过②");
        }
        else
        {
            var f2Ref = main.ImportReference(f2);
            var il2 = m2.Body.GetILProcessor();
            var first2 = m2.Body.Instructions[0];
            il2.InsertBefore(first2, il2.Create(OpCodes.Ldarg_1));   // characterName
            il2.InsertBefore(first2, il2.Create(OpCodes.Call, f2Ref));
            Console.WriteLine("已注入 GetCharacterScene -> 用到时才加载角色资源");
        }
    }

    /// <summary>0.28 加载加速（手机版）：ResourceManager.LoadUniqueResourceRoots 开头注入
    /// PvzheMod.GameCheats.PrewarmResourceRoots(paths)。独立程序集方案 → 需 ImportReference。
    /// 原理见电脑版同名函数：LoadThreadedRequest 并行预处理，让原循环的 GD.Load 命中缓存。
    /// ⚠️ 0.27 及更早无 LoadUniqueResourceRoots → 打印「跳过」而不报警告。</summary>
    static void InjectPrewarmResourceRoots(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("ResourceManager");
        if (t == null) { Console.WriteLine("警告: 找不到 ResourceManager"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "LoadUniqueResourceRoots" && x.HasBody);
        if (method == null)
        {
            Console.WriteLine(t.Methods.Any(x => x.Name == "LoadCoreResourcesOnMainThread")
                ? "警告: 0.28 架构但找不到 LoadUniqueResourceRoots（注入点变了，需重新核对）"
                : "跳过 LoadUniqueResourceRoots 预热（0.27 及更早无此方法）");
            return;
        }
        if (HasInjectedCall(method, "PrewarmResourceRoots")) { Console.WriteLine("LoadUniqueResourceRoots 预热已注入，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var pw = gc.Methods.FirstOrDefault(x => x.Name == "PrewarmResourceRoots" && x.IsStatic);
        if (pw == null) { Console.WriteLine("警告: 找不到 PrewarmResourceRoots（mod 里被删了？）"); return; }
        // 签名守卫：必须是 (IReadOnlyList<string>) → void，否则注入后运行时会 MissingMethod 崩溃
        if (pw.Parameters.Count != 1 || pw.Parameters[0].ParameterType.Name != "IReadOnlyList`1")
        {
            Console.WriteLine("警告: PrewarmResourceRoots 签名不符（应为 1 个 IReadOnlyList<string> 参数），跳过注入");
            return;
        }
        var pwRef = main.ImportReference(pw);
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));   // paths（实例方法：arg0=this, arg1=paths）
        il.InsertBefore(first, il.Create(OpCodes.Call, pwRef));
        Console.WriteLine("已注入 ResourceManager.LoadUniqueResourceRoots -> 线程化预热");
    }

    /// <summary>游戏本体卡顿修复：ResourceManager.RunResourceLoadOnMainThread 加载节流（同电脑版，2026-08-20 实测稳定）。
    /// 后台线程每请求一个资源 sleep 2ms，限制主线程单帧处理的 CallDeferred 加载数量，
    /// 避免主线程被资源加载占满导致加载界面卡。保持主线程加载（后台并行实测崩溃）。</summary>
    static void InjectResourceLoadThrottle(ModuleDefinition main)
    {
        var t = main.GetType("ResourceManager");
        if (t == null) { Console.WriteLine("警告: 找不到 ResourceManager"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "RunResourceLoadOnMainThread" && x.HasBody);
        if (method == null)
        {
            // 0.28+ 重写了加载架构：废弃「后台线程请求主线程加载」通道，改为
            // LoadCoreResourcesOnMainThread / LoadFullGameplayRootsOnMainThreadAsync 全主线程
            // + YieldAfterStageAsync 主动让出，官方自己已消除卡顿，此节流不再需要。
            Console.WriteLine(t.Methods.Any(x => x.Name == "LoadCoreResourcesOnMainThread")
                ? "跳过 RunResourceLoadOnMainThread 节流（0.28+ 新加载架构已废弃此后台通道）"
                : "警告: 找不到 RunResourceLoadOnMainThread");
            return;
        }
        if (HasInjectedCall(method, "Sleep")) { Console.WriteLine("节流已注入过，跳过"); return; }
        Instruction branchTarget = null;
        var insns = method.Body.Instructions;
        for (int i = 0; i < insns.Count; i++)
        {
            if (insns[i].OpCode == OpCodes.Call && insns[i].Operand is MethodReference mr && mr.Name == "get_CurrentManagedThreadId")
            {
                for (int j = i + 1; j < insns.Count; j++)
                {
                    if (insns[j].OpCode == OpCodes.Bne_Un || insns[j].OpCode == OpCodes.Bne_Un_S)
                    {
                        branchTarget = insns[j].Operand as Instruction;
                        break;
                    }
                }
                break;
            }
        }
        if (branchTarget == null) { Console.WriteLine("警告: 未找到非主线程分支起点"); return; }
        var threadSleep = main.ImportReference(typeof(System.Threading.Thread).GetMethod("Sleep", new[] { typeof(int) }));
        if (threadSleep == null) { Console.WriteLine("警告: 找不到 Thread.Sleep"); return; }
        var il = method.Body.GetILProcessor();
        // 3ms：限制后台请求速率轻微；10ms 会让进战斗主线程 Task.Wait 无限等待假死
        il.InsertBefore(branchTarget, il.Create(OpCodes.Ldc_I4, 3));
        il.InsertBefore(branchTarget, il.Create(OpCodes.Call, threadSleep));
        Console.WriteLine("已注入 RunResourceLoadOnMainThread 节流(3ms)");
    }

    /// <summary>角色后台加载节流：LoadCharacterTaskContinuously（每个后台 worker 的角色任务）开头 sleep。
    /// 角色加载走主线程误判分支在 worker 线程直接加载（5 worker 并行 94 秒 CPU 满载），
    /// 把渲染线程挤掉 → 界面无响应。每个任务前 sleep 3ms 降低平均 CPU 占用 → 渲染流畅。
    /// v2（2026-08-21）：注入点从 LoadCharacterTaskContinuously 开头改为 RunCharacterBackgroundWorkerLane
    /// 的 TryLoadCharacterTaskContinuously 调用前——只影响后台 worker 线程。
    /// v1 教训：LoadCharacterTaskContinuously 是角色加载核心路径（主界面卡牌预览/进游戏角色），
    /// 开头 sleep 拖慢所有角色加载 → 主线程 Task.Wait 等 → 主界面/进游戏卡死。</summary>
    static void InjectCharacterLoadThrottle(ModuleDefinition main)
    {
        var t = main.GetType("ResourceManager");
        if (t == null) { Console.WriteLine("警告: 找不到 ResourceManager"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "RunCharacterBackgroundWorkerLane" && x.HasBody);
        if (method == null)
        {
            // 0.28+ 已取消角色资源后台 worker 通道：角色场景/精灵改由 LoadFullGameplayRootsOnMainThreadAsync
            // 下的 LoadUniquePackedSceneRoots（主线程）统一加载，不再有 worker 抢渲染线程的问题。
            Console.WriteLine(t.Methods.Any(x => x.Name == "LoadUniquePackedSceneRoots")
                ? "跳过 RunCharacterBackgroundWorkerLane 节流（0.28+ 角色资源已改主线程加载，无后台 worker）"
                : "警告: 找不到 RunCharacterBackgroundWorkerLane");
            return;
        }
        // 找 TryLoadCharacterTaskContinuously 调用（worker 每个角色任务前插入 sleep）
        Instruction target = null;
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "TryLoadCharacterTaskContinuously")
            {
                target = ins;
                break;
            }
        }
        if (target == null) { Console.WriteLine("警告: 未找到 TryLoadCharacterTaskContinuously 调用"); return; }
        var threadSleep = main.ImportReference(typeof(System.Threading.Thread).GetMethod("Sleep", new[] { typeof(int) }));
        if (threadSleep == null) { Console.WriteLine("警告: 找不到 Thread.Sleep"); return; }
        var il = method.Body.GetILProcessor();
        // 10ms：worker 慢一点给渲染线程喘息（3ms 实测仍满载主界面卡，10ms 记忆验证过不假死且流畅），主线程 Task.Wait 等角色不假死
        il.InsertBefore(target, il.Create(OpCodes.Ldc_I4, 10));
        il.InsertBefore(target, il.Create(OpCodes.Call, threadSleep));
        Console.WriteLine("已注入 RunCharacterBackgroundWorkerLane 角色节流(10ms/任务, worker线程)");
    }

    /// <summary>魅惑：注入 TowerDefenseCharacter.BatchProcessUpdate 开头——开关开启时强制 hypnoses=true（反转攻击）。</summary>
    static void InjectCharm(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseCharacter");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseCharacter"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "BatchProcessUpdate" && !x.IsStatic && x.HasBody);
        if (method == null) { Console.WriteLine("警告: 找不到 TowerDefenseCharacter.BatchProcessUpdate"); return; }
        if (HasInjectedCall(method, "ApplyCharmPatch")) { Console.WriteLine("BatchProcessUpdate 魅惑已注入，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var charm = gc.Methods.FirstOrDefault(x => x.Name == "ApplyCharmPatch");
        if (charm == null) { Console.WriteLine("警告: 找不到 ApplyCharmPatch"); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(charm)));
        Console.WriteLine("已注入 BatchProcessUpdate 魅惑");
    }

    /// <summary>去除魅惑免疫：把 HypnosesComponent.Hypnoses 与 TowerDefenseCharacterBuffHypnoses.Enter 中
    /// `unUseBuffFlags & 8` 的免疫判断改为 `& 0`（恒不触发）→ 海妖/僵王/带头盔等免疫魅惑的也能被魅惑。
    /// 运行时清 unUseBuffFlags 会被僵王状态（HeadExitedEntered/AnimeCompleted 设 -1/-4）持续重设，必须注入禁用检查。</summary>
    static void InjectCharmImmuneBypass(ModuleDefinition main)
    {
        int total = 0;
        var targets = new (string type, string method)[]
        {
            ("HypnosesComponent", "Hypnoses"),
            ("TowerDefenseCharacterBuffHypnoses", "Enter"),
            ("TowerDefenseZombie", "Hypnoses"),
        };
        foreach (var (tn, mn) in targets)
        {
            var t = main.GetType(tn);
            if (t == null) { Console.WriteLine("警告: 找不到 " + tn); continue; }
            var m = t.Methods.FirstOrDefault(x => x.HasBody && x.Name == mn);
            if (m == null) { Console.WriteLine("警告: 找不到 " + tn + "." + mn); continue; }
            var il = m.Body.Instructions.ToList();
            bool hit = false;
            for (int i = 0; i < il.Count; i++)
            {
                // ldc.i4.8 是短指令 Ldc_I4_8（操作数编码在 opcode），也可能是 Ldc_I4 + 操作数 8
                bool is8 = il[i].OpCode == OpCodes.Ldc_I4_8
                        || (il[i].OpCode == OpCodes.Ldc_I4 && il[i].Operand is int vv && vv == 8);
                if (is8 && i + 1 < il.Count && il[i + 1].OpCode == OpCodes.And)
                {
                    // 往前确认最近是 ldfld unUseBuffFlags（魅惑免疫位），避免误改其他 &8
                    bool ok = false;
                    for (int j = i - 1; j >= 0 && j >= i - 6; j--)
                    {
                        if (il[j].OpCode == OpCodes.Ldfld && il[j].Operand is Mono.Cecil.FieldReference fr && fr.Name == "unUseBuffFlags") { ok = true; break; }
                        if (il[j].OpCode == OpCodes.Ldloc || il[j].OpCode == OpCodes.Stloc || il[j].OpCode == OpCodes.Stfld) break;
                    }
                    if (ok) { il[i].OpCode = OpCodes.Ldc_I4_0; hit = true; total++; }
                }
            }
            Console.WriteLine(hit ? ("已去除 " + tn + "." + mn + " 魅惑免疫检查") : (tn + "." + mn + " 未找到免疫位"));
        }
        Console.WriteLine("魅惑免疫检查处理 " + total + " 处");
    }

    /// <summary>铲子铲植物 hook：注入 TowerDefenseCellInstance.Shovel 开头——铲植物瞬间调用
    /// GameCheats.OnCellShovel(cell)（ShovelCherry 开时随机僵尸脚下生成樱桃）。独立DLL方案。</summary>
    static void InjectShovel(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseCellInstance");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseCellInstance"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "Shovel" && !x.IsStatic && x.HasBody);
        if (method == null) { Console.WriteLine("警告: 找不到 TowerDefenseCellInstance.Shovel"); return; }
        if (HasInjectedCall(method, "OnCellShovel")) { Console.WriteLine("Shovel 铲子已注入，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var hook = gc.Methods.FirstOrDefault(x => x.Name == "OnCellShovel");
        if (hook == null) { Console.WriteLine("警告: 找不到 OnCellShovel"); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(hook)));
        Console.WriteLine("已注入 Shovel -> OnCellShovel");
    }

    /// <summary>子弹追踪/随机：在 BulletField.Spawn(ref BulletData data) 开头注入
    /// data.trackOpen = GameCheats.ShouldTrackBullets()（BulletTrack 全追踪 / BulletRandom 随机追踪）。
    /// trackOpen=true 后 BulletField.Update 走 ProcessTrackData，每帧朝目标追踪。</summary>
    static void InjectBulletTrack(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("BulletField");
        if (t == null) { Console.WriteLine("警告: 找不到 BulletField"); return; }
        var spawn = t.Methods.FirstOrDefault(x => x.Name == "Spawn" && !x.IsStatic && x.HasBody && x.Parameters.Count == 1);
        if (spawn == null) { Console.WriteLine("警告: 找不到 BulletField.Spawn"); return; }
        if (HasInjectedCall(spawn, "ShouldTrackBullets")) { Console.WriteLine("BulletField.Spawn 子弹追踪/随机已注入，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var should = gc.Methods.FirstOrDefault(x => x.Name == "ShouldTrackBullets");
        if (should == null) { Console.WriteLine("警告: 找不到 ShouldTrackBullets"); return; }
        var dataType = main.GetType("BulletData");
        if (dataType == null) { Console.WriteLine("警告: 找不到 BulletData"); return; }
        var trackOpen = dataType.Fields.FirstOrDefault(x => x.Name == "trackOpen");
        if (trackOpen == null) { Console.WriteLine("警告: 找不到 BulletData.trackOpen"); return; }
        var first = spawn.Body.Instructions[0];
        var il = spawn.Body.GetILProcessor();
        // 全关完全跳过（零干扰）：if (GameCheats.BulletModsActive()) { <下面所有注入> }
        // 关键（Mono.Cecil InsertBefore(first,X) 是后插的更靠近 first = 执行顺序更靠后）：
        //   - call bma + brfalse skip 必须【最先插入】→ 执行顺序最前
        //   - 注入体在中间插入
        //   - skip 标签必须【最后插入】→ 位于注入体之后、first 之前
        // 若顺序搞反（skip 在注入体前），BulletModsActive=false 时 BulletField.Spawn 无限循环 → 植物一攻击就冻结（电脑版 2026-08-18 实测）
        var skip = il.Create(OpCodes.Nop);
        var bma = gc.Methods.FirstOrDefault(x => x.Name == "BulletModsActive");
        if (bma == null) { Console.WriteLine("警告: 找不到 BulletModsActive"); return; }
        // 全关跳过：if (!BulletModsActive()) goto skip（最先插入 = 执行顺序最前）
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(bma)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, skip));
        // data.trackOpen = ShouldTrackBullets(data.trackOpen)：ldarg.1(BulletData&) → ldfld 原值 → call → ldarg.1 → stfld 存回
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Ldfld, trackOpen));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(should)));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Stfld, trackOpen));
        Console.WriteLine("已注入 BulletField.Spawn 子弹追踪");
        // data.trackSearchInterval = TrackIntervalOverride(data.trackSearchInterval)（追踪开启时每帧重搜全图目标；全关保留原版值）
        var tsiField = dataType.Fields.FirstOrDefault(x => x.Name == "trackSearchInterval");
        var tsi = gc.Methods.FirstOrDefault(x => x.Name == "TrackIntervalOverride");
        if (tsiField != null && tsi != null)
        {
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, tsiField));
            il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(tsi)));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, tsiField));
            Console.WriteLine("已注入 BulletField.Spawn 追踪间隔(每帧重搜)");
        }
        // data.speed = RandomizeSpeed(data.speed)（随机子弹速度 0.7~1.3 倍，快慢不一，随机感明显）
        var speedField = dataType.Fields.FirstOrDefault(x => x.Name == "speed" && x.FieldType.FullName == "System.Single");
        var rs = gc.Methods.FirstOrDefault(x => x.Name == "RandomizeSpeed");
        if (speedField != null && rs != null)
        {
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, speedField));
            il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(rs)));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, speedField));
            Console.WriteLine("已注入 BulletField.Spawn 子弹速度随机");
        }
        // data.config = (TowerDefenseProjectileConfig)RandomProjectileConfig(data.config)
        // 随机/自定义子弹 = 换一种子弹配置（从 ResourceManager.PROJECTILE_CONFIG）
        var configField = dataType.Fields.FirstOrDefault(x => x.Name == "config");
        var rpc = gc.Methods.FirstOrDefault(x => x.Name == "RandomProjectileConfig");
        if (configField != null && rpc != null)
        {
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, configField));
            il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(rpc)));
            il.InsertBefore(first, il.Create(OpCodes.Castclass, configField.FieldType));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, configField));
            Console.WriteLine("已注入 BulletField.Spawn 随机子弹种类");
        }
        // skip 标签最后插入（位于所有注入体之后、first 之前）——brfalse 跳到这里跳过注入体。
        // 注意：call/brfalse 已在方法开头最先插入，此处只插 skip。
        il.InsertBefore(first, skip);
    }

    /// <summary>对战模式禁卡/分区拦截（与电脑版 mod/patcher 逐条对应）：
    ///   ① TowerDefenseInGamePacketShow.Plant → ShouldBlockPlantAt(this, gridPos)：禁卡 + 分区一次判定。
    ///      分区规则只有这一层能拿到真实目标格。
    ///   ② TowerDefensePacketConfig.Plant → ShouldBlockThisPlant(this)：只判禁卡，
    ///      覆盖「种一列」与其它绕过路径。分层原因：底层重载里 gridPos 参数位不一致，
    ///      按固定下标取会取错；而且标靶僵尸生成/僵尸方放置也走这里，坐标天生在右半场。
    /// 返回 null 是安全的：两个方法自身就把 null 当失败结果（!alive / config.Plant 失败）。
    /// 不要改成拦 PlantColumnBatch —— 它失败时返回的是空列表，return null 会空引用。</summary>
    static void InjectPvpPlantBan(ModuleDefinition main, ModuleDefinition mod)
    {
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        int n = 0;
        var chkAt = gc.Methods.FirstOrDefault(x => x.Name == "ShouldBlockPlantAt");
        if (chkAt != null) n += InjectNullIfBlockedAt(main, mod, chkAt);
        else Console.WriteLine("警告: 找不到 ShouldBlockPlantAt");
        var chk = gc.Methods.FirstOrDefault(x => x.Name == "ShouldBlockThisPlant");
        if (chk != null) n += InjectNullIfBlockedPlain(main, "TowerDefensePacketConfig", "Plant", chk);
        else Console.WriteLine("警告: 找不到 ShouldBlockThisPlant");
        Console.WriteLine("已注入 对战禁卡/分区拦截 " + n + " 处");
    }

    /// <summary>对战模式标靶僵尸免疫：
    ///   ① TowerDefenseCharacter.Hypnoses → ShouldBlockCharm(this) → 直接 return；
    ///   ② TowerDefenseCharacter.get_IsHardControlImmune → ShouldImmuneHardControl(this) → return true。
    /// 仅在对战模式且实体为 ZombieTarget 时生效，其它情况一律保持原逻辑。</summary>
    static void InjectPvpImmunity(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseCharacter");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseCharacter"); return; }
        var np = mod.GetType("PvzheMod.NetPvp");
        if (np == null) { Console.WriteLine("警告: 找不到 PvzheMod.NetPvp"); return; }
        int n = 0;

        var charm = np.Methods.FirstOrDefault(x => x.Name == "ShouldBlockCharm");
        if (charm != null)
        {
            foreach (var m in t.Methods.Where(x => x.Name == "Hypnoses" && !x.IsStatic && x.HasBody && x.Parameters.Count >= 1).ToList())
            {
                if (HasInjectedCall(m, "ShouldBlockCharm")) continue;
                var il = m.Body.GetILProcessor();
                var first = m.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(charm)));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                n++;
            }
        }
        else Console.WriteLine("警告: 找不到 NetPvp.ShouldBlockCharm");

        var hard = np.Methods.FirstOrDefault(x => x.Name == "ShouldImmuneHardControl");
        if (hard != null)
        {
            foreach (var m in t.Methods.Where(x => x.Name == "get_IsHardControlImmune" && !x.IsStatic && x.HasBody && x.Parameters.Count == 0).ToList())
            {
                if (HasInjectedCall(m, "ShouldImmuneHardControl")) continue;
                var il = m.Body.GetILProcessor();
                var first = m.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(hard)));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                n++;
            }
        }
        else Console.WriteLine("警告: 找不到 NetPvp.ShouldImmuneHardControl");

        Console.WriteLine("已注入 对战免疫（魅惑/硬控）" + n + " 处");
    }

    /// <summary>方法头注入（带参数）：ldarg.0; ldarg.1; call; brfalse 原首条; ldnull; ret
    /// 只作用于首参为 Vector2I 的 Plant 重载（能拿到目标格）。</summary>
    static int InjectNullIfBlockedAt(ModuleDefinition main, ModuleDefinition mod, MethodReference chk)
    {
        var t = main.GetType("TowerDefenseInGamePacketShow");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseInGamePacketShow"); return 0; }
        int n = 0;
        foreach (var m in t.Methods.Where(x => x.Name == "Plant" && !x.IsStatic && x.HasBody &&
                 x.Parameters.Count >= 1 && x.Parameters[0].ParameterType.FullName == "Godot.Vector2I").ToList())
        {
            if (HasInjectedCall(m, "ShouldBlockPlantAt")) continue;
            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(chk)));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldnull));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            n++;
        }
        return n;
    }

    /// <summary>把指定类型下所有同名实例方法（含重载）头部注入
    ///   ldarg.0; call; brfalse 原首条; ldnull; ret</summary>
    static int InjectNullIfBlockedPlain(ModuleDefinition main, string typeName, string methodName, MethodReference chk)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return 0; }
        int n = 0;
        foreach (var m in t.Methods.Where(x => x.Name == methodName && !x.IsStatic && x.HasBody).ToList())
        {
            if (HasInjectedCall(m, "ShouldBlockThisPlant")) continue;
            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(chk)));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldnull));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            n++;
        }
        return n;
    }

    /// <summary>种一列出列：注入到 TowerDefenseInGamePacketShow.Plant(Vector2I, ...) 开头
    /// ldarg.0; ldarg.1; call void GameCheats::OnPlantPlaced(object, Vector2I)。
    /// 只对玩家种植生效——罐子内容/僵尸生成走 TowerDefensePacketConfig.Plant，不经过这里，
    /// 避免整列复制罐子/僵尸。OnPlantPlaced 内部从 packetShow.config 拿配置批量种同列。</summary>
    static void InjectPlantColumn(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseInGamePacketShow");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseInGamePacketShow"); return; }
        var plant = t.Methods.FirstOrDefault(x => x.Name == "Plant" && !x.IsStatic && x.HasBody &&
            x.Parameters.Count >= 1 && x.Parameters[0].ParameterType.FullName == "Godot.Vector2I");
        if (plant == null) { Console.WriteLine("警告: 找不到 TowerDefenseInGamePacketShow.Plant(Vector2I,...)"); return; }
        if (HasInjectedCall(plant, "OnPlantPlaced")) { Console.WriteLine("TowerDefenseInGamePacketShow.Plant 种列已注入，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var op = gc.Methods.FirstOrDefault(x => x.Name == "OnPlantPlaced");
        if (op == null) { Console.WriteLine("警告: 找不到 OnPlantPlaced"); return; }
        var first = plant.Body.Instructions[0];
        var il = plant.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(op)));
        Console.WriteLine("已注入 TowerDefenseInGamePacketShow.Plant 种列");
    }

    /// <summary>卡牌随机：TowerDefenseInGamePacketShow.Plant(Vector2I,...) 开头注入
    /// ldarg.0; call void GameCheats::OnPacketAboutToPlant(object)。
    /// 在 InjectPlantColumn 之后调用 → 插到最前 → 先随机卡内容，再种一列。</summary>
    static void InjectPacketPlantRandom(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseInGamePacketShow");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseInGamePacketShow"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var cb = gc.Methods.FirstOrDefault(x => x.Name == "OnPacketAboutToPlant");
        if (cb == null) { Console.WriteLine("警告: 找不到 OnPacketAboutToPlant"); return; }
        var plant = t.Methods.FirstOrDefault(x => x.Name == "Plant" && !x.IsStatic && x.HasBody &&
            x.Parameters.Count >= 1 && x.Parameters[0].ParameterType.FullName == "Godot.Vector2I");
        if (plant == null) { Console.WriteLine("警告: 找不到 TowerDefenseInGamePacketShow.Plant"); return; }
        if (HasInjectedCall(plant, "OnPacketAboutToPlant")) { Console.WriteLine("TowerDefenseInGamePacketShow.Plant 卡牌随机已注入，跳过"); return; }
        var first = plant.Body.Instructions[0];
        var il = plant.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(cb)));
        Console.WriteLine("已注入 TowerDefenseInGamePacketShow.Plant 卡牌砸前随机");
    }

    /// <summary>礼盒盲盒篡改 + 抽卡类植物篡改：注入指定类型 Explode 开头——
    /// if (GameCheats.<hookName>(this)) return; （mod 接管产出目标卡，独立 DLL 引用）。</summary>
    static void InjectExplodeTrick(ModuleDefinition main, ModuleDefinition mod, string hookName, string label, string[] typeNames)
    {
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 GameCheats"); return; }
        var hook = gc.Methods.FirstOrDefault(x => x.Name == hookName);
        if (hook == null) { Console.WriteLine("警告: 找不到 " + hookName); return; }
        foreach (var tn in typeNames)
        {
            var t = main.GetType(tn);
            if (t == null) { Console.WriteLine("警告: 找不到 " + tn); continue; }
            var ex = t.Methods.FirstOrDefault(x => x.Name == "Explode" && !x.IsStatic && x.HasBody && x.Parameters.Count == 0);
            if (ex == null) { Console.WriteLine("警告: 找不到 " + tn + ".Explode"); continue; }
            if (HasInjectedCall(ex, hookName)) { Console.WriteLine(tn + ".Explode 已注入，跳过"); continue; }
            var first = ex.Body.Instructions[0];
            var il = ex.Body.GetILProcessor();
            // 注：不能用 Brtrue_S 跳到方法末尾（超出 ±127 字节→非法分支目标，Explode IL 损坏）。
            // 正确：hook 真→走新增 ret 返回；hook 假→短跳到紧跟的跳过点继续原逻辑。
            var skip = il.Create(OpCodes.Nop);
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(hook)));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, skip));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            il.InsertBefore(first, skip);
            Console.WriteLine("已注入 " + tn + ".Explode " + label);
        }
    }

    /// <summary>礼盒盲盒篡改：TowerDefensePlantPresentBox / Green（种下爆炸随机开出卡）。</summary>
    static void InjectPresentBoxTrick(ModuleDefinition main, ModuleDefinition mod)
    {
        InjectExplodeTrick(main, mod, "OnPresentBoxExplode", "礼盒篡改",
            new string[] { "TowerDefensePlantPresentBox", "TowerDefensePlantPresentBoxGreen" });
    }

    /// <summary>抽卡类植物篡改：种下随机给一张卡的植物——
    /// BYWZ(备用物质) / LuckyBlover(幸运三叶草) / GardenSet(花园套装) / Upgradebean(升级豆) /
    /// LampShroom(路灯菇) / MagicBean(魔法豆)。</summary>
    static void InjectRandomPacketPlantTrick(ModuleDefinition main, ModuleDefinition mod)
    {
        InjectExplodeTrick(main, mod, "OnRandomPacketPlantExplode", "抽卡植物篡改",
            new string[] { "TowerDefensePlantBYWZ", "TowerDefensePlantLuckyBlover", "TowerDefensePlantGardenSet",
                           "TowerDefensePlantUpgradebean", "TowerDefensePlantLampShroom", "TowerDefensePlantMagicBean" });
    }

    /// <summary>罐子随机：VaseContentComponent.DestroySet 开头注入
    /// ldarg.0; call void GameCheats::OnVaseAboutToBreak(object)。
    /// 每次砸罐子前随机该罐子内容（无名版随机罐子：每次砸都随机）。</summary>
    static void InjectVaseBreakRandom(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("VaseContentComponent");
        if (t == null) { Console.WriteLine("警告: 找不到 VaseContentComponent"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var cb = gc.Methods.FirstOrDefault(x => x.Name == "OnVaseAboutToBreak");
        if (cb == null) { Console.WriteLine("警告: 找不到 OnVaseAboutToBreak"); return; }
        var ds = t.Methods.FirstOrDefault(x => x.Name == "DestroySet" && !x.IsStatic && x.HasBody);
        if (ds == null) { Console.WriteLine("警告: 找不到 VaseContentComponent.DestroySet"); return; }
        if (HasInjectedCall(ds, "OnVaseAboutToBreak")) { Console.WriteLine("VaseContentComponent.DestroySet 罐子随机已注入，跳过"); return; }
        var first = ds.Body.Instructions[0];
        var il = ds.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(cb)));
        Console.WriteLine("已注入 VaseContentComponent.DestroySet 罐子砸前随机");
    }

    /// <summary>无法攻击/无法移动：注入 void 方法开头
    /// if (GameCheats.ShouldXxx()) return;（开关开时直接退出，不破坏组件状态机，关=原始逻辑）
    /// 植物：FireComponent.IdleProcessing；僵尸攻击：AttackComponent.IdleProcessing；僵尸移动：TowerDefenseZombie.WalkSystem。</summary>
    static void InjectNoAttackAndNoMove(ModuleDefinition main, ModuleDefinition mod)
    {
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }

        // 植物无法攻击：FireComponent.IdleProcessing（空闲检测+发射入口）。
        // 注意：不能注入 AttackProcessing——会把攻击动画卡死，状态机永久停在攻击状态，开关关了也不恢复（"关了也生效"bug）
        InjectVoidReturnIf(main, mod, gc, "FireComponent", "IdleProcessing", "ShouldPlantNoAttack");

        // 僵尸无法攻击：AttackComponent.CanAttack() 无参 + CanAttack(bool) 带参（攻击最终裁决，僵尸/植物都走这里；带 this 判断父是僵尸才拦）
        // TryCommitContactTarget 只是设目标，不保证触发攻击；CanAttack 是所有攻击路径的最终判断
        var ac = main.GetType("AttackComponent");
        if (ac != null)
        {
            var canAttackNoArg = ac.Methods.FirstOrDefault(x => x.Name == "CanAttack" && !x.IsStatic && x.HasBody && x.Parameters.Count == 0);
            if (canAttackNoArg == null || canAttackNoArg.ReturnType.MetadataType != Mono.Cecil.MetadataType.Boolean)
                Console.WriteLine("警告: 找不到 AttackComponent.CanAttack() 无参");
            else
                InjectReturnFalseIfInstance2(main, gc, ac, canAttackNoArg, "ShouldZombieNoAttack!this");
            // 带参重载也注入（内部可能直接调用带参版本）
            var canAttackArg = ac.Methods.FirstOrDefault(x => x.Name == "CanAttack" && !x.IsStatic && x.HasBody && x.Parameters.Count == 1 && x.Parameters[0].ParameterType.MetadataType == Mono.Cecil.MetadataType.Boolean);
            if (canAttackArg == null || canAttackArg.ReturnType.MetadataType != Mono.Cecil.MetadataType.Boolean)
                Console.WriteLine("警告: 找不到 AttackComponent.CanAttack(bool) 带参");
            else
                InjectReturnFalseIfInstance2(main, gc, ac, canAttackArg, "ShouldZombieNoAttack!this");
        }
        else Console.WriteLine("警告: 找不到 AttackComponent");

        // 僵尸无法移动：
        // 1) TowerDefenseZombie.BatchUpdate + BatchUpdateValidated——僵尸自己的移动入口（BatchUpdateValidated 激活时直接调 BatchUpdateCore 绕过 BatchUpdate，必须拦）
        // 2) GroundMoveComponent.BatchUpdate + BatchUpdateValidated（通用兜底）
        // 3) 气球僵尸飞行：TowerDefenseZombieBalloon.FlyProcessing
        InjectVoidReturnIf(main, mod, gc, "TowerDefenseZombie", "BatchUpdate", "ShouldZombieNoMove!this");
        InjectVoidReturnIf(main, mod, gc, "TowerDefenseZombie", "BatchUpdateValidated", "ShouldZombieNoMove!this");
        InjectVoidReturnIf(main, mod, gc, "GroundMoveComponent", "BatchUpdate", "ShouldZombieNoMove!this");
        InjectVoidReturnIf(main, mod, gc, "GroundMoveComponent", "BatchUpdateValidated", "ShouldZombieNoMove!this");
        InjectVoidReturnIf(main, mod, gc, "TowerDefenseZombieBalloon", "FlyProcessing", "ShouldZombieNoMove!this");
    }

    /// <summary>bool 方法开头注入（指定 MethodDefinition）：if (GameCheats.check(this)) return false;。
    /// checkMethodName 以 "!this" 结尾时，把 this 作为参数传入。</summary>
    static void InjectReturnFalseIfInstance2(ModuleDefinition main, TypeDefinition gc, TypeDefinition t, MethodDefinition method, string checkMethodName)
    {
        if (method == null || method.ReturnType.MetadataType != Mono.Cecil.MetadataType.Boolean) return;
        bool passThis = checkMethodName.EndsWith("!this");
        string checkName = passThis ? checkMethodName.Substring(0, checkMethodName.Length - 5) : checkMethodName;
        if (HasInjectedCall(method, checkName)) { Console.WriteLine(t.Name + "." + method.Name + " 已注入，跳过"); return; }
        var check = gc.Methods.FirstOrDefault(x => x.Name == checkName);
        if (check == null) { Console.WriteLine("警告: 找不到 " + checkName); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        var skip = il.Create(OpCodes.Nop);
        if (passThis) il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(check)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, skip));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        il.InsertBefore(first, skip);
        Console.WriteLine("已注入 " + t.Name + "." + method.Name + " (" + checkName + ")");
    }

    /// <summary>bool 方法开头注入：if (GameCheats.check(this)) return false;（开关开时阻止）。
    /// checkMethodName 以 "!this" 结尾时，把 this 作为参数传入。</summary>
    static void InjectReturnFalseIfInstance(ModuleDefinition main, ModuleDefinition mod, TypeDefinition gc, string typeName, string methodName, string checkMethodName)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == methodName && !x.IsStatic && x.HasBody);
        if (method == null || method.ReturnType.MetadataType != Mono.Cecil.MetadataType.Boolean)
        { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName + " (bool)"); return; }
        bool passThis = checkMethodName.EndsWith("!this");
        string checkName = passThis ? checkMethodName.Substring(0, checkMethodName.Length - 5) : checkMethodName;
        if (HasInjectedCall(method, checkName)) { Console.WriteLine(typeName + "." + methodName + " 已注入，跳过"); return; }
        var check = gc.Methods.FirstOrDefault(x => x.Name == checkName);
        if (check == null) { Console.WriteLine("警告: 找不到 " + checkName); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        var skip = il.Create(OpCodes.Nop);
        if (passThis) il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(check)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, skip));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        il.InsertBefore(first, skip);
        Console.WriteLine("已注入 " + typeName + "." + methodName + " (" + checkName + ")");
    }

    /// <summary>void 方法开头注入：if (GameCheats.check()) return;（开关开时直接退出）。
    /// checkMethodName 以 "!this" 结尾时，把 this 作为参数传入（如 ShouldZombieNoMove!this）。</summary>
    static void InjectVoidReturnIf(ModuleDefinition main, ModuleDefinition mod, TypeDefinition gc, string typeName, string methodName, string checkMethodName)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == methodName && !x.IsStatic && x.HasBody);
        if (method == null || method.ReturnType.MetadataType != Mono.Cecil.MetadataType.Void)
        { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName + " (void)"); return; }
        bool passThis = checkMethodName.EndsWith("!this");
        string checkName = passThis ? checkMethodName.Substring(0, checkMethodName.Length - 5) : checkMethodName;
        if (HasInjectedCall(method, checkName)) { Console.WriteLine(typeName + "." + methodName + " 已注入，跳过"); return; }
        var check = gc.Methods.FirstOrDefault(x => x.Name == checkName);
        if (check == null) { Console.WriteLine("警告: 找不到 " + checkName); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        var skip = il.Create(OpCodes.Nop);
        if (passThis)
        {
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0)); // this
        }
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(check)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, skip));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        il.InsertBefore(first, skip);
        Console.WriteLine("已注入 " + typeName + "." + methodName + " (" + checkName + ")");
    }

    /// <summary>成长植物秒成熟：GrowUpComponent.PhysicsProcess 开头注入
    /// if (GameCheats.ShouldInstantGrow()) timer = float.MaxValue;
    /// （开关开时成长计时器瞬间满→while 循环一次推到最大形态；对在场与新种的都生效）</summary>
    static void InjectInstantGrow(ModuleDefinition main, ModuleDefinition mod, TypeDefinition gc)
    {
        var check = gc.Methods.FirstOrDefault(x => x.Name == "ShouldInstantGrow");
        if (check == null) { Console.WriteLine("警告: 找不到 ShouldInstantGrow"); return; }
        var t = main.GetType("GrowUpComponent");
        if (t == null) { Console.WriteLine("警告: 找不到 GrowUpComponent"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "PhysicsProcess" && !x.IsStatic && x.HasBody);
        if (method == null) { Console.WriteLine("警告: 找不到 GrowUpComponent.PhysicsProcess"); return; }
        if (HasInjectedCall(method, "ShouldInstantGrow")) { Console.WriteLine("GrowUpComponent.PhysicsProcess 秒成长已注入，跳过"); return; }
        var timerField = t.Fields.FirstOrDefault(f => f.Name == "timer");
        if (timerField == null) { Console.WriteLine("警告: 找不到 GrowUpComponent.timer 字段"); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        var skip = il.Create(OpCodes.Nop);
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(check)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, skip));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, float.MaxValue));
        il.InsertBefore(first, il.Create(OpCodes.Stfld, timerField));
        il.InsertBefore(first, skip);
        Console.WriteLine("已注入 GrowUpComponent.PhysicsProcess 秒成长");
    }

    /// <summary>蘑菇白天不睡觉：SleepComponent.CanSleep 开头注入
    /// if (GameCheats.ShouldPreventSleep(this)) return false;（开关开时禁止一切睡眠）</summary>
    static void InjectNoSleep(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("SleepComponent");
        if (t == null) { Console.WriteLine("警告: 找不到 SleepComponent"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "CanSleep" && !x.IsStatic && x.HasBody);
        if (method == null) { Console.WriteLine("警告: 找不到 SleepComponent.CanSleep"); return; }
        if (HasInjectedCall(method, "ShouldPreventSleep")) { Console.WriteLine("SleepComponent.CanSleep 已注入，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var sp = gc.Methods.FirstOrDefault(x => x.Name == "ShouldPreventSleep");
        if (sp == null) { Console.WriteLine("警告: 找不到 ShouldPreventSleep"); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        var skip = il.Create(OpCodes.Nop);
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(sp)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, skip));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        il.InsertBefore(first, skip);
        Console.WriteLine("已注入 SleepComponent.CanSleep 蘑菇不睡");
    }

    /// <summary>迷雾透视：TowerDefenseFog.SetCanVisible(bool) 开头注入
    /// if (GameCheats.ShouldBlockFogVisible(visible)) return;（FogESP 开时拦截 visible=true，雾保持隐藏）</summary>
    static void InjectFogHide(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseFog");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseFog"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "SetCanVisible" && !x.IsStatic && x.HasBody && x.Parameters.Count == 1);
        if (method == null) { Console.WriteLine("警告: 找不到 TowerDefenseFog.SetCanVisible"); return; }
        if (HasInjectedCall(method, "ShouldBlockFogVisible")) { Console.WriteLine("TowerDefenseFog.SetCanVisible 迷雾拦截已注入，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var sbf = gc.Methods.FirstOrDefault(x => x.Name == "ShouldBlockFogVisible");
        if (sbf == null) { Console.WriteLine("警告: 找不到 ShouldBlockFogVisible"); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        var skip = il.Create(OpCodes.Nop);
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(sbf)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, skip));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        il.InsertBefore(first, skip);
        Console.WriteLine("已注入 TowerDefenseFog.SetCanVisible 迷雾拦截(强制隐藏)");
    }

    /// <summary>无冷却强制清除：注入 TowerDefenseInGamePacketShow 的 _PhysicsProcess / ApplyCachedRuntimeAvailability 开头。
    /// NoCooldown 开时立即关闭已开启的冷却并隐藏进度条（灰卡恢复正常）。</summary>
    static void InjectForceUsable(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseInGamePacketShow");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseInGamePacketShow"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var force = gc.Methods.FirstOrDefault(x => x.Name == "ForcePacketUsable");
        if (force == null) { Console.WriteLine("警告: 找不到 ForcePacketUsable"); return; }
        foreach (var mn in new string[] { "_PhysicsProcess", "ApplyCachedRuntimeAvailability" })
        {
            var method = t.Methods.FirstOrDefault(x => x.Name == mn && !x.IsStatic && x.HasBody);
            if (method == null) { Console.WriteLine("警告: 找不到 " + mn); continue; }
            if (HasInjectedCall(method, "ForcePacketUsable")) { Console.WriteLine(mn + " 已注入，跳过"); continue; }
            var first = method.Body.Instructions[0];
            var il = method.Body.GetILProcessor();
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(force)));
            Console.WriteLine("已注入 " + mn + " 无冷却强制清除");
        }
    }

    /// <summary>无冷却：注入 TowerDefenseInGamePacketShow.set_coldDownOpen——NoCooldown 开启时跳过"开启冷却"的设置。</summary>
    static void InjectNoCooldown(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseInGamePacketShow");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseInGamePacketShow"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "set_coldDownOpen" && !x.IsStatic && x.HasBody &&
            x.Parameters.Count == 1 && x.Parameters[0].ParameterType.FullName == "System.Boolean");
        if (method == null) { Console.WriteLine("警告: 找不到 set_coldDownOpen"); return; }
        if (method.Body.Instructions[0].OpCode == OpCodes.Ldarg_1)
        {
            Console.WriteLine("set_coldDownOpen 已注入，跳过");
            return;
        }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var skip = gc.Methods.FirstOrDefault(x => x.Name == "ShouldSkipCooldownSet");
        if (skip == null) { Console.WriteLine("警告: 找不到 ShouldSkipCooldownSet"); return; }
        var first = method.Body.Instructions[0];
        var last = method.Body.Instructions[method.Body.Instructions.Count - 1];
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(skip)));
        il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, last));
        Console.WriteLine("已注入 set_coldDownOpen 无冷却");
    }

    /// <summary>注入方法开头：if (GameCheats.check()) return 0;（开关开返回 0，关闭走原始逻辑）。用于 GetCost/GetCostRise。</summary>
    static void InjectReturnZeroIf(ModuleDefinition main, ModuleDefinition mod, string typeName, string methodName, string checkMethodName)
    {
        InjectReturnValueIf(main, mod, typeName, methodName, checkMethodName, 0);
    }

    /// <summary>注入方法开头：if (GameCheats.check()) return value;（开关开才恒 value，关闭走原始逻辑）。
    /// 用于选卡（get_packetBankMethod 返回 CHOOSE=1）等返回 int/枚举的方法。</summary>
    static void InjectReturnValueIf(ModuleDefinition main, ModuleDefinition mod, string typeName, string methodName, string checkMethodName, int value)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == methodName);
        if (method == null || method.Body == null) { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName); return; }
        if (HasInjectedCall(method, checkMethodName)) { Console.WriteLine(typeName + "." + methodName + " 已注入开关，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var check = gc.Methods.FirstOrDefault(x => x.Name == checkMethodName);
        if (check == null) { Console.WriteLine("警告: 找不到 " + checkMethodName); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        // if (check()) return value; else 原始逻辑
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(check)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, value));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        Console.WriteLine("已注入 " + typeName + "." + methodName + " 开关(开则返回" + value + ")");
    }

    /// <summary>注入方法开头：if (GameCheats.check()) return true;（开关开才恒 true，关闭走原始逻辑）。
    /// 用于"功能打开后关掉还生效"的修复——IL 注入必须受 mod 开关控制。</summary>
    static void InjectReturnTrueIf(ModuleDefinition main, ModuleDefinition mod, string typeName, string methodName, string checkMethodName)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == methodName);
        if (method == null || method.Body == null) { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName); return; }
        if (HasInjectedCall(method, checkMethodName)) { Console.WriteLine(typeName + "." + methodName + " 已注入开关，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var check = gc.Methods.FirstOrDefault(x => x.Name == checkMethodName);
        if (check == null) { Console.WriteLine("警告: 找不到 " + checkMethodName); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        // if (check()) return true; else 原始逻辑
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(check)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        Console.WriteLine("已注入 " + typeName + "." + methodName + " 开关(开则 true)");
    }

    /// <summary>注入方法开头：if (GameCheats.check()) return false;（开关开才恒 false，关闭走原始逻辑）。</summary>
    static void InjectReturnFalseIf(ModuleDefinition main, ModuleDefinition mod, string typeName, string methodName, string checkMethodName)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == methodName);
        if (method == null || method.Body == null) { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName); return; }
        if (HasInjectedCall(method, checkMethodName)) { Console.WriteLine(typeName + "." + methodName + " 已注入开关，跳过"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var check = gc.Methods.FirstOrDefault(x => x.Name == checkMethodName);
        if (check == null) { Console.WriteLine("警告: 找不到 " + checkMethodName); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        // if (check()) return false; else 原始逻辑
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(check)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        Console.WriteLine("已注入 " + typeName + "." + methodName + " 开关(开则 false)");
    }

    /// <summary>给方法开头注入 ldc.i4.1; ret（恒返回 true）——用于 CanPacketPlant 无视种植限制（重叠/地形）。</summary>
    static void InjectReturnTrue(ModuleDefinition main, ModuleDefinition mod, string typeName, string methodName)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == methodName);
        if (method == null || method.Body == null) { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName); return; }
        var first = method.Body.Instructions[0];
        if (first.OpCode == OpCodes.Ldc_I4_1 && first.Next != null && first.Next.OpCode == OpCodes.Ret)
        {
            Console.WriteLine(typeName + "." + methodName + " 已打补丁，跳过");
            return;
        }
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        Console.WriteLine("已补丁 " + typeName + "." + methodName + " 恒返回 true");
    }

    /// <summary>注入方法开头恒返回 false（用于取消数量限制等检查）。</summary>
    static void InjectReturnFalse(ModuleDefinition main, ModuleDefinition mod, string typeName, string methodName)
    {
        var t = main.GetType(typeName);
        if (t == null) { Console.WriteLine("警告: 找不到 " + typeName); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == methodName);
        if (method == null || method.Body == null) { Console.WriteLine("警告: 找不到 " + typeName + "." + methodName); return; }
        var first = method.Body.Instructions[0];
        if (first.OpCode == OpCodes.Ldc_I4_0 && first.Next != null && first.Next.OpCode == OpCodes.Ret)
        {
            Console.WriteLine(typeName + "." + methodName + " 已打补丁，跳过");
            return;
        }
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        Console.WriteLine("已补丁 " + typeName + "." + methodName + " 恒返回 false");
    }

    /// <summary>检查方法前 8 条指令里是否已调用指定名字的方法（用于判断是否已注入）。</summary>
    static bool HasInjectedCall(MethodDefinition method, string methodName)
    {
        int n = 0;
        foreach (var ins in method.Body.Instructions)
        {
            if (n++ > 8) break;
            if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == methodName)
                return true;
        }
        return false;
    }

    /// <summary>攻速（独立逻辑）：只注入 AccelerateTimer 到 BatchUpdateValidated 开头（每帧被调用，timer 递减处）。
    /// mod 直接控制 timer（每帧额外增减），不依赖 attackInterval 机制，纯倍率生效、更可靠。</summary>
    static void InjectAttackSpeedPatch(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("AttackComponent");
        if (t == null) { Console.WriteLine("警告: 找不到 AttackComponent"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var accel = gc.Methods.FirstOrDefault(x => x.Name == "AccelerateTimer");
        if (accel == null) { Console.WriteLine("警告: 找不到 AccelerateTimer"); return; }
        var method = t.Methods.FirstOrDefault(x => x.Name == "BatchUpdateValidated" && !x.IsStatic && x.HasBody && x.Parameters.Count >= 1);
        if (method == null) { Console.WriteLine("警告: 找不到 BatchUpdateValidated"); return; }
        if (HasInjectedCall(method, "AccelerateTimer")) { Console.WriteLine("BatchUpdateValidated 攻速加速已注入，跳过"); return; }
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(accel)));
        Console.WriteLine("已注入 BatchUpdateValidated 攻速加速(独立timer)");
    }

    /// <summary>伤害：注入到 TowerDefenseCharacterInstance（所有伤害最终扣血点）。
    /// double 伤害方法：num = ScaleDamageInstance(this, num)；攻击配置类方法：if (IsDamageBlockedInstance(this)) return 0。</summary>
    static void InjectInstanceDamageScaling(ModuleDefinition main, ModuleDefinition mod)
    {
        var t = main.GetType("TowerDefenseCharacterInstance");
        if (t == null) { Console.WriteLine("警告: 找不到 TowerDefenseCharacterInstance"); return; }
        var gc = mod.GetType("PvzheMod.GameCheats");
        if (gc == null) { Console.WriteLine("警告: 找不到 PvzheMod.GameCheats"); return; }
        var scaleD = gc.Methods.FirstOrDefault(x => x.Name == "ScaleDamageInstance");
        var blocked = gc.Methods.FirstOrDefault(x => x.Name == "IsDamageBlockedInstance");
        if (scaleD == null || blocked == null) { Console.WriteLine("警告: 找不到 ScaleDamageInstance/IsDamageBlockedInstance"); return; }

        foreach (var method in t.Methods)
        {
            if (!method.HasBody || method.IsStatic || method.Parameters.Count < 1) continue;
            if (method.Name == "Health") continue;   // 治疗不能缩放
            if (method.Name.StartsWith("set_") || method.Name.StartsWith("get_")) continue;  // 属性访问器不是伤害
            var p0 = method.Parameters[0];
            var pt = p0.ParameterType.FullName;
            if (pt == "System.Double")
            {
                // double 伤害：只注入返回 double 的方法（伤害方法），避免破坏其他
                if (method.ReturnType.MetadataType != Mono.Cecil.MetadataType.Double) continue;
                // num = ScaleDamageInstance(this, num)
                if (HasInjectedCall(method, "ScaleDamageInstance")) { Console.WriteLine(method.Name + " 已注入缩放，跳过"); continue; }
                var first = method.Body.Instructions[0];
                var il = method.Body.GetILProcessor();
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
                il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(scaleD)));
                il.InsertBefore(first, il.Create(OpCodes.Starg_S, p0));
                Console.WriteLine("已注入 " + method.Name + " 伤害缩放");
            }
            else if (pt != null && (pt.Contains("AttackConfig") || pt.Contains("ProjectileHitInfo") || pt.Contains("TowerDefenseProjectile")))
            {
                // 攻击配置类：只注入返回 double 的伤害方法（避免把 bool/void 的命中检测也注入成 return 0.0——曾致子弹打不中）
                if (method.ReturnType.MetadataType != Mono.Cecil.MetadataType.Double) continue;
                // if (IsDamageBlockedInstance(this)) return 0.0
                if (HasInjectedCall(method, "IsDamageBlockedInstance")) { Console.WriteLine(method.Name + " 已注入屏蔽，跳过"); continue; }
                var first = method.Body.Instructions[0];
                var il = method.Body.GetILProcessor();
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, main.ImportReference(blocked)));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_R8, 0.0));
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                Console.WriteLine("已注入 " + method.Name + " 无敌屏蔽");
            }
        }
    }
}

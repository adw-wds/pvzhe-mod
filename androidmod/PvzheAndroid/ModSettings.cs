namespace PvzheMod
{
    /// <summary>mod 全局设置（由主界面面板调节）。</summary>
    public static class ModSettings
    {
        public static bool Enabled = true;    // 是否启用彩色
        public static float Speed = 0.4f;     // 渐变速度（慢，清晰）
        public static int Style = 0;          // 0=彩虹 1=红金 2=蓝紫 3=霓虹
        public static bool FunEnabled = true;  // 趣味弹幕
        public static bool BGEnabled = true;   // 背景彩虹氛围
        public static float GameSpeed = 1.0f;  // 游戏速度（0.5~2.0）
        public static string CustomMessages = "";  // 自定义弹幕（逗号/换行分隔）
        public static bool InfiniteSun = false; // 无限阳光
        public static bool InfiniteCoin = false; // 无限金币
        public static bool ESPEnabled = false;   // 透视框总开关
        public static bool ESPZombie = true;     // 僵尸透视
        public static bool ESPPlant = false;     // 植物透视
        public static float ZombieScale = 1.0f;  // 僵尸大小
        public static float PlantScale = 1.0f;   // 植物大小
        public static bool VaseESP = true;       // 透视罐子
        public static float DanmakuSpeed = 1.0f; // 弹幕速度
        public static bool NoCooldown = false;    // 卡片无冷却
        public static bool AlmanacAll = false;     // 图鉴全解（持续设置 plantShowAll）
        public static bool PlantOverlap = true;    // 植物重叠（patcher 已补丁 CanPacketPlant）
        public static bool IgnoreTerrain = true;   // 无视地形（同 CanPacketPlant 补丁）
        public static bool IgnorePurple = false;   // 无视紫卡限制（持续 GlobalFeatureManager.Unlock）
        public static float PlantAttackSpeed = 1.0f;   // 植物攻速倍率（1=正常，2=两倍）
        public static float ZombieAttackSpeed = 1.0f;  // 僵尸攻速倍率
        public static float PlantHP = 1.0f;            // 植物血量倍率
        public static float ZombieHP = 1.0f;           // 僵尸血量倍率
        public static bool PlantInvincible = false;    // 植物无敌
        public static bool ZombieInvincible = false;   // 僵尸无敌
        public static bool CharmPlant = false;         // 魅惑植物（反转攻击）
        public static bool CharmZombie = false;        // 魅惑僵尸
        public static bool BulletTrack = false;         // 子弹追踪（patcher 注入 BulletField.Spawn 强制 trackOpen）
        public static bool BulletFollowMouse = false;    // 子弹跟随鼠标
        public static bool BulletRandom = false;         // 随机子弹（随机追踪）
        public static string BulletType = "";           // 自定义子弹类型（PROJECTILE_CONFIG key，空=不生效）
        public static bool PlantAutoFire = false;        // 自动开火（全图锁敌，独立于子弹追踪）
        public static bool FogESP = false;              // 迷雾透视（隐藏 TowerDefenseFog.canVisible）
        // 性能诊断：每 10 秒把 OnFrame 各段平均耗时写进日志（排查掉帧用）。
        // 开着的开销只有每帧几次 GetTicksUsec，可忽略；正式发布前改回 false。
        public static bool PerfDiag = true;
        public static bool PlantColumn = false;         // 种一个出一列（patcher 注入 Plant 开头批量种植）
        public static bool NoSleep = false;             // 蘑菇白天不睡觉（patcher 注入 SleepComponent.CanSleep）
        public static bool ChomperFastSwallow = false;   // 大嘴花秒吞咽（咬住立即吞掉，不咀嚼）
        public static bool InstantGrow = false;          // 成长植物秒成熟（阳光菇家族计时器拉满直接到最大形态）
        public static bool ClearCrater = false;         // 清除弹坑（持续删除 Crater 组节点）
        public static bool ConveyorFast = false;          // 传送带加速送卡（缩短 spawn interval）
        public static bool RainFast = false;               // 种子雨加速掉落（缩短 rain interval）
        public static bool VaseRandom = false;             // 罐子内物品随机（自动，进罐子关卡随机并显示内容）
        public static bool SeedBankRandom = false;         // 卡槽卡牌随机（自动，进关卡把卡槽里的卡随机成随机植物）
        public static bool ZombieColor = false;         // 僵尸变色（趣味）
        public static bool ZombieDance = false;         // 僵尸跳舞（趣味）
        public static bool PlantNoAttack = false;        // 植物无法攻击
        public static bool ZombieNoAttack = false;       // 僵尸无法攻击
        public static bool ZombieNoMove = false;         // 僵尸无法移动
        public static bool GloveMode = false;            // 手套挪动（局中点击植物拿起、点空地放置）
        public static bool NoFlicker = true;             // 禁止闪屏（渲染安全模式：动态渲染降到最保守）
        public static bool BossNoBow = false;            // Boss 不鞠躬
        public static bool CanChooseAll = false;         // 所有关卡可选卡
        public static bool UnlockPackets = false;          // 清除锁定卡牌（战斗中卡牌 _lock=false，独立开关）
        public static bool NoCostRise = false;           // 卡价不涨价（金卡/重复购买）
        public static bool NoThrowImp = false;           // 禁止投掷小鬼
        public static bool NoZombieSpawn = false;        // 禁止出僵尸（每帧删除场上僵尸）
        public static bool CustomLevelActive = false;    // 自制关卡模式（阳光豆赌博：卡槽只剩阳光豆、种下随机生成、3波僵尸）
        public static bool PerfMode = false;             // 性能优化模式（降低渲染/遍历开销）
        public static bool PlantTriple = false;          // 三倍种植（种一个，同列相邻两行也种）
        public static int TextColorMode = 0;             // 文字颜色：0=彩虹律动 1=纯白 2=纯黑
        public static bool ZeroCost = false;             // 零消费（GetCost=0，所有卡免费）
        public static bool CannonNoCooldown = false;     // 炮类无冷却（玉米加农炮等）
        public static bool IgnoreHouse = false;          // 无视僵尸进家（进家不失败）
        public static bool IgnoreWarningLine = false;    // 僵尸碰到警戒线不失败
        public static bool PlantSquash = false;          // 趣味 Q弹模式（植物压扁/拉扁）
        public static bool JellyMode = false;            // 果冻模式：Q弹基础上加左右摇摆抽搐
        public static bool ForceRain = false;            // 强制种子雨（任何关卡都有种子雨掉落植物卡）
        public static bool ForceFog = false;             // 强制迷雾（任何关卡都开启迷雾）
        public static bool IgnoreRedLine = false;        // 无视红线：警戒线（红线）区域也能种植物/僵尸
        public static bool SeedBankRandomZombie = false; // 卡槽卡牌随机成僵尸（false=随机成植物）
        public static bool ShovelCherry = false;         // 铲子铲植物：随机在僵尸脚下生成樱桃炸弹
        public static bool BloverClearAll = false;       // 三叶草吹飞所有僵尸（种 Blover 时清空场上僵尸）
        public static int CannonMultiShot = 1;           // 炮类/投手一次发射数量（1=原版，2+ = 一次发射多枚）
        public static bool PlantFullRange = false;       // 全植物全图射程
        public static bool CrystalInfinite = false;       // 无限水晶：CrystalNum=10亿（在线关卡兑换货币）
        public static bool WavePaused = false;            // 波次暂停（官方 CommandManager.debugWavePaused，波次不推进）
        public static int SpawnMultiplier = 1;             // 刷怪倍数（1=原版，2+ = 每波僵尸数量翻倍）
        public static bool BungiFastGrab = false;           // 飞贼秒偷：飞贼一出现立即偷走最近植物并飞走
        public static bool BungiIgnoreUmbrella = false;     // 飞贼无视保护伞
        public static bool JackboxFastBomb = false;         // 小丑秒炸：瞬移到目标面前立即爆炸
        public static bool ZombiesFollowMouse = false;      // 全场僵尸吸附到鼠标
        public static bool BungiSpawnJackbox = false;       // 飞贼偷取后原地生成快爆炸的小丑
        public static bool GlobalOverridesEnabled = false;  // 全局植物属性覆盖（plant_overrides.txt 所有关卡生效）
        public static bool ConsoleEnabled = true;           // 游戏原生作弊控制台（CommandManager，官方 debug=false 隐藏，MOD 强制开启）
        // ===== 刷出物篡改（锁定老虎机盲盒 / 种子雨 / 传送带 自动刷出的植物/僵尸内容） =====
        public static bool TrickEnabled = false;       // 总开关：按下方目标卡池锁定三类自动刷出内容
        public static bool TrickBox = false;           // 篡改老虎机(盲盒)转盘：转盘图案 + 中奖掉卡 = 目标池
        public static bool TrickRain = false;          // 篡改种子雨：天上掉的卡 = 目标池（仅掉卡型雨）
        public static bool TrickConveyor = false;      // 篡改传送带：传送带送的卡 = 目标池
        public static bool TrickFixedOnly = false;     // true=只固定第一张目标卡；false=在目标池内随机
        public static string TrickPool = "";          // 目标卡池（逗号/空格分隔的植物或僵尸 ID；留空=不篡改）
        public static bool SeedBankEveryFrame = true;  // 卡槽卡牌随机：true=1帧1刷；false=约每秒6次

        // 手机版：用 user://（应用私有目录）保存，Godot.FileAccess 直接支持该协议
        // （之前写死电脑路径导致手机上保存/加载全失败）
        private static readonly string SavePath = "user://modsettings.txt";
        public static readonly string SaveDir = "user://";   // 其他存档文件目录（plantmods.json 等）

        /// <summary>保存设置到文件（攻速/血量/魅惑等持久化，重进游戏不丢）。</summary>
        public static void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("PlantAttackSpeed=" + PlantAttackSpeed.ToString("0.###"));
                sb.AppendLine("ZombieAttackSpeed=" + ZombieAttackSpeed.ToString("0.###"));
                sb.AppendLine("PlantHP=" + PlantHP.ToString("0.###"));
                sb.AppendLine("ZombieHP=" + ZombieHP.ToString("0.###"));
                sb.AppendLine("PlantInvincible=" + PlantInvincible);
                sb.AppendLine("ZombieInvincible=" + ZombieInvincible);
                sb.AppendLine("CharmPlant=" + CharmPlant);
                sb.AppendLine("CharmZombie=" + CharmZombie);
                sb.AppendLine("BulletTrack=" + BulletTrack);
                sb.AppendLine("BulletFollowMouse=" + BulletFollowMouse);
                sb.AppendLine("BulletRandom=" + BulletRandom);
                sb.AppendLine("BulletType=" + BulletType);
                sb.AppendLine("PlantAutoFire=" + PlantAutoFire);
                sb.AppendLine("PlantColumn=" + PlantColumn);
                sb.AppendLine("NoSleep=" + NoSleep);
                sb.AppendLine("ClearCrater=" + ClearCrater);
                sb.AppendLine("ConveyorFast=" + ConveyorFast);
                sb.AppendLine("RainFast=" + RainFast);
                sb.AppendLine("VaseRandom=" + VaseRandom);
                sb.AppendLine("SeedBankRandom=" + SeedBankRandom);
                sb.AppendLine("PlantOverlap=" + PlantOverlap);
                sb.AppendLine("IgnoreTerrain=" + IgnoreTerrain);
                sb.AppendLine("IgnorePurple=" + IgnorePurple);
                sb.AppendLine("FogESP=" + FogESP);
                sb.AppendLine("ZombieColor=" + ZombieColor);
                sb.AppendLine("ZombieDance=" + ZombieDance);
                sb.AppendLine("PlantNoAttack=" + PlantNoAttack);
                sb.AppendLine("ZombieNoAttack=" + ZombieNoAttack);
                sb.AppendLine("ZombieNoMove=" + ZombieNoMove);
                sb.AppendLine("GloveMode=" + GloveMode);
                sb.AppendLine("NoFlicker=" + NoFlicker);
                sb.AppendLine("BossNoBow=" + BossNoBow);
                sb.AppendLine("CanChooseAll=" + CanChooseAll);
                sb.AppendLine("UnlockPackets=" + UnlockPackets);
                sb.AppendLine("NoCostRise=" + NoCostRise);
                sb.AppendLine("NoThrowImp=" + NoThrowImp);
                sb.AppendLine("NoZombieSpawn=" + NoZombieSpawn);
                sb.AppendLine("PerfMode=" + PerfMode);
                sb.AppendLine("PlantTriple=" + PlantTriple);
                sb.AppendLine("TextColorMode=" + TextColorMode);
                sb.AppendLine("ZeroCost=" + ZeroCost);
                sb.AppendLine("GameSpeed=" + GameSpeed.ToString("0.###"));
                sb.AppendLine("NoCooldown=" + NoCooldown);
                sb.AppendLine("AlmanacAll=" + AlmanacAll);
                sb.AppendLine("CannonNoCooldown=" + CannonNoCooldown);
                sb.AppendLine("IgnoreHouse=" + IgnoreHouse);
                sb.AppendLine("IgnoreWarningLine=" + IgnoreWarningLine);
                sb.AppendLine("PlantSquash=" + PlantSquash);
                sb.AppendLine("JellyMode=" + JellyMode);
                sb.AppendLine("ForceRain=" + ForceRain);
                sb.AppendLine("ForceFog=" + ForceFog);
                sb.AppendLine("IgnoreRedLine=" + IgnoreRedLine);
                sb.AppendLine("SeedBankRandomZombie=" + SeedBankRandomZombie);
                sb.AppendLine("ShovelCherry=" + ShovelCherry);
                sb.AppendLine("BloverClearAll=" + BloverClearAll);
                sb.AppendLine("CannonMultiShot=" + CannonMultiShot);
                sb.AppendLine("PlantFullRange=" + PlantFullRange);
                sb.AppendLine("CrystalInfinite=" + CrystalInfinite);
                sb.AppendLine("WavePaused=" + WavePaused);
                sb.AppendLine("SpawnMultiplier=" + SpawnMultiplier);
                sb.AppendLine("BungiFastGrab=" + BungiFastGrab);
                sb.AppendLine("BungiIgnoreUmbrella=" + BungiIgnoreUmbrella);
                sb.AppendLine("JackboxFastBomb=" + JackboxFastBomb);
                sb.AppendLine("ZombiesFollowMouse=" + ZombiesFollowMouse);
                sb.AppendLine("BungiSpawnJackbox=" + BungiSpawnJackbox);
                sb.AppendLine("GlobalOverridesEnabled=" + GlobalOverridesEnabled);
                sb.AppendLine("ConsoleEnabled=" + ConsoleEnabled);
                sb.AppendLine("TrickEnabled=" + TrickEnabled);
                sb.AppendLine("TrickBox=" + TrickBox);
                sb.AppendLine("TrickRain=" + TrickRain);
                sb.AppendLine("TrickConveyor=" + TrickConveyor);
                sb.AppendLine("TrickFixedOnly=" + TrickFixedOnly);
                sb.AppendLine("TrickPool=" + TrickPool);
                sb.AppendLine("SeedBankEveryFrame=" + SeedBankEveryFrame);
                // ===== 补全：主开关/无限资源/ESP/趣味 等持久化（重进游戏保持开启状态） =====
                sb.AppendLine("Enabled=" + Enabled);
                sb.AppendLine("FunEnabled=" + FunEnabled);
                sb.AppendLine("BGEnabled=" + BGEnabled);
                sb.AppendLine("InfiniteSun=" + InfiniteSun);
                sb.AppendLine("InfiniteCoin=" + InfiniteCoin);
                sb.AppendLine("ESPEnabled=" + ESPEnabled);
                sb.AppendLine("ESPZombie=" + ESPZombie);
                sb.AppendLine("ESPPlant=" + ESPPlant);
                sb.AppendLine("VaseESP=" + VaseESP);
                sb.AppendLine("ChomperFastSwallow=" + ChomperFastSwallow);
                sb.AppendLine("Speed=" + Speed.ToString("0.###"));
                sb.AppendLine("Style=" + Style);
                sb.AppendLine("ZombieScale=" + ZombieScale.ToString("0.###"));
                sb.AppendLine("PlantScale=" + PlantScale.ToString("0.###"));
                sb.AppendLine("DanmakuSpeed=" + DanmakuSpeed.ToString("0.###"));
                sb.AppendLine("CustomMessages=" + CustomMessages);
                // 注意：Godot 导出裁剪了 System.IO.File 部分方法（ReadAllLines MissingMethod），改用 Godot.FileAccess
                var fa = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
                if (fa != null)
                {
                    fa.StoreString(sb.ToString());
                    fa.Close();
                }
            }
            catch { }
        }

        /// <summary>从文件加载设置（Bootstrap.Init 时调用）。</summary>
        public static void Load()
        {
            try
            {
                if (!Godot.FileAccess.FileExists(SavePath)) return;
                var fa = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Read);
                if (fa == null) return;
                string all = fa.GetAsText();
                fa.Close();
                foreach (var line in all.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                {
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    string k = line.Substring(0, idx).Trim();
                    string v = line.Substring(idx + 1).Trim();
                    bool b; float f;
                    switch (k)
                    {
                        case "PlantAttackSpeed": if (float.TryParse(v, out f)) PlantAttackSpeed = f; break;
                        case "ZombieAttackSpeed": if (float.TryParse(v, out f)) ZombieAttackSpeed = f; break;
                        case "PlantHP": if (float.TryParse(v, out f)) PlantHP = f; break;
                        case "ZombieHP": if (float.TryParse(v, out f)) ZombieHP = f; break;
                        case "PlantInvincible": if (bool.TryParse(v, out b)) PlantInvincible = b; break;
                        case "ZombieInvincible": if (bool.TryParse(v, out b)) ZombieInvincible = b; break;
                        case "CharmPlant": if (bool.TryParse(v, out b)) CharmPlant = b; break;
                        case "CharmZombie": if (bool.TryParse(v, out b)) CharmZombie = b; break;
                        case "BulletTrack": if (bool.TryParse(v, out b)) BulletTrack = b; break;
                        case "BulletFollowMouse": if (bool.TryParse(v, out b)) BulletFollowMouse = b; break;
                        case "BulletRandom": if (bool.TryParse(v, out b)) BulletRandom = b; break;
                        case "BulletType": BulletType = v; break;
                        case "PlantAutoFire": if (bool.TryParse(v, out b)) PlantAutoFire = b; break;
                        case "PlantColumn": if (bool.TryParse(v, out b)) PlantColumn = b; break;
                        case "NoSleep": if (bool.TryParse(v, out b)) NoSleep = b; break;
                        case "ClearCrater": if (bool.TryParse(v, out b)) ClearCrater = b; break;
                        case "ConveyorFast": if (bool.TryParse(v, out b)) ConveyorFast = b; break;
                        case "RainFast": if (bool.TryParse(v, out b)) RainFast = b; break;
                        case "VaseRandom": if (bool.TryParse(v, out b)) VaseRandom = b; break;
                        case "SeedBankRandom": if (bool.TryParse(v, out b)) SeedBankRandom = b; break;
                        case "PlantOverlap": if (bool.TryParse(v, out b)) PlantOverlap = b; break;
                        case "IgnoreTerrain": if (bool.TryParse(v, out b)) IgnoreTerrain = b; break;
                        case "IgnorePurple": if (bool.TryParse(v, out b)) IgnorePurple = b; break;
                        case "FogESP": if (bool.TryParse(v, out b)) FogESP = b; break;
                        case "ZombieColor": if (bool.TryParse(v, out b)) ZombieColor = b; break;
                        case "ZombieDance": if (bool.TryParse(v, out b)) ZombieDance = b; break;
                        case "PlantNoAttack": if (bool.TryParse(v, out b)) PlantNoAttack = b; break;
                        case "ZombieNoAttack": if (bool.TryParse(v, out b)) ZombieNoAttack = b; break;
                        case "ZombieNoMove": if (bool.TryParse(v, out b)) ZombieNoMove = b; break;
                        case "GloveMode": if (bool.TryParse(v, out b)) GloveMode = b; break;
                        case "NoFlicker": if (bool.TryParse(v, out b)) NoFlicker = b; break;
                        case "BossNoBow": if (bool.TryParse(v, out b)) BossNoBow = b; break;
                        case "CanChooseAll": if (bool.TryParse(v, out b)) CanChooseAll = b; break;
                        case "UnlockPackets": if (bool.TryParse(v, out b)) UnlockPackets = b; break;
                        case "NoCostRise": if (bool.TryParse(v, out b)) NoCostRise = b; break;
                        case "NoThrowImp": if (bool.TryParse(v, out b)) NoThrowImp = b; break;
                        case "NoZombieSpawn": if (bool.TryParse(v, out b)) NoZombieSpawn = b; break;
                        case "PerfMode": if (bool.TryParse(v, out b)) PerfMode = b; break;
                        case "PlantTriple": if (bool.TryParse(v, out b)) PlantTriple = b; break;
                        case "TextColorMode": if (int.TryParse(v, out var iv)) TextColorMode = iv; break;
                        case "ZeroCost": if (bool.TryParse(v, out b)) ZeroCost = b; break;
                        case "GameSpeed": if (float.TryParse(v, out f)) GameSpeed = f; break;
                        case "NoCooldown": if (bool.TryParse(v, out b)) NoCooldown = b; break;
                        case "AlmanacAll": if (bool.TryParse(v, out b)) AlmanacAll = b; break;
                        case "CannonNoCooldown": if (bool.TryParse(v, out b)) CannonNoCooldown = b; break;
                        case "IgnoreHouse": if (bool.TryParse(v, out b)) IgnoreHouse = b; break;
                        case "IgnoreWarningLine": if (bool.TryParse(v, out b)) IgnoreWarningLine = b; break;
                        case "PlantSquash": if (bool.TryParse(v, out b)) PlantSquash = b; break;
                        case "JellyMode": if (bool.TryParse(v, out b)) JellyMode = b; break;
                        case "ForceRain": if (bool.TryParse(v, out b)) ForceRain = b; break;
                        case "ForceFog": if (bool.TryParse(v, out b)) ForceFog = b; break;
                        case "IgnoreRedLine": if (bool.TryParse(v, out b)) IgnoreRedLine = b; break;
                        case "SeedBankRandomZombie": if (bool.TryParse(v, out b)) SeedBankRandomZombie = b; break;
                        case "ShovelCherry": if (bool.TryParse(v, out b)) ShovelCherry = b; break;
                        case "BloverClearAll": if (bool.TryParse(v, out b)) BloverClearAll = b; break;
                        case "CannonMultiShot": if (int.TryParse(v, out var cm)) CannonMultiShot = cm; break;
                        case "PlantFullRange": if (bool.TryParse(v, out b)) PlantFullRange = b; break;
                        case "CrystalInfinite": if (bool.TryParse(v, out b)) CrystalInfinite = b; break;
                        case "WavePaused": if (bool.TryParse(v, out b)) WavePaused = b; break;
                        case "SpawnMultiplier": if (int.TryParse(v, out var sm)) SpawnMultiplier = sm; break;
                        case "BungiFastGrab": if (bool.TryParse(v, out b)) BungiFastGrab = b; break;
                        case "BungiIgnoreUmbrella": if (bool.TryParse(v, out b)) BungiIgnoreUmbrella = b; break;
                        case "JackboxFastBomb": if (bool.TryParse(v, out b)) JackboxFastBomb = b; break;
                        case "ZombiesFollowMouse": if (bool.TryParse(v, out b)) ZombiesFollowMouse = b; break;
                        case "BungiSpawnJackbox": if (bool.TryParse(v, out b)) BungiSpawnJackbox = b; break;
                        case "GlobalOverridesEnabled": if (bool.TryParse(v, out b)) GlobalOverridesEnabled = b; break;
                        case "ConsoleEnabled": if (bool.TryParse(v, out b)) ConsoleEnabled = b; break;
                        case "TrickEnabled": if (bool.TryParse(v, out b)) TrickEnabled = b; break;
                        case "TrickBox": if (bool.TryParse(v, out b)) TrickBox = b; break;
                        case "TrickRain": if (bool.TryParse(v, out b)) TrickRain = b; break;
                        case "TrickConveyor": if (bool.TryParse(v, out b)) TrickConveyor = b; break;
                        case "TrickFixedOnly": if (bool.TryParse(v, out b)) TrickFixedOnly = b; break;
                        case "TrickPool": TrickPool = v; break;
                        case "SeedBankEveryFrame": if (bool.TryParse(v, out b)) SeedBankEveryFrame = b; break;
                        case "Enabled": if (bool.TryParse(v, out b)) Enabled = b; break;
                        case "FunEnabled": if (bool.TryParse(v, out b)) FunEnabled = b; break;
                        case "BGEnabled": if (bool.TryParse(v, out b)) BGEnabled = b; break;
                        case "InfiniteSun": if (bool.TryParse(v, out b)) InfiniteSun = b; break;
                        case "InfiniteCoin": if (bool.TryParse(v, out b)) InfiniteCoin = b; break;
                        case "ESPEnabled": if (bool.TryParse(v, out b)) ESPEnabled = b; break;
                        case "ESPZombie": if (bool.TryParse(v, out b)) ESPZombie = b; break;
                        case "ESPPlant": if (bool.TryParse(v, out b)) ESPPlant = b; break;
                        case "VaseESP": if (bool.TryParse(v, out b)) VaseESP = b; break;
                        case "ChomperFastSwallow": if (bool.TryParse(v, out b)) ChomperFastSwallow = b; break;
                        case "Speed": if (float.TryParse(v, out f)) Speed = f; break;
                        case "Style": if (int.TryParse(v, out var st)) Style = st; break;
                        case "ZombieScale": if (float.TryParse(v, out f)) ZombieScale = f; break;
                        case "PlantScale": if (float.TryParse(v, out f)) PlantScale = f; break;
                        case "DanmakuSpeed": if (float.TryParse(v, out f)) DanmakuSpeed = f; break;
                        case "CustomMessages": CustomMessages = v; break;
                    }
                }
            }
            catch { }
        }

        /// <summary>是否有任何战斗/功能开关开启（GameCheats.OnFrame 总开关快速路径用）。
        /// 全关时每帧零遍历零反射直接退出——战斗场景最受益。
        /// 不含 Enabled(律动)/PerfMode/TextColorMode/GameSpeed/ESP/Fun/BG——那些由 GlobalColor 层处理。</summary>
        public static bool AnyModActive()
        {
            return InfiniteSun || InfiniteCoin || NoCooldown || AlmanacAll || IgnorePurple ||
                   FogESP || ClearCrater || ConveyorFast || RainFast || VaseRandom || SeedBankRandom ||
                   ZombieColor || ZombieDance || BulletFollowMouse || PlantAutoFire ||
                   BulletTrack || BulletRandom ||
                   PlantAttackSpeed != 1f || ZombieAttackSpeed != 1f || PlantHP != 1f || ZombieHP != 1f ||
                   PlantInvincible || ZombieInvincible || CharmPlant || CharmZombie ||
                   PlantNoAttack || ZombieNoAttack || ZombieNoMove || GloveMode || PlantTriple ||
                   NoCostRise || ZeroCost || BulletType.Length > 0 || CanChooseAll || UnlockPackets ||
                   NoZombieSpawn || BossNoBow || NoThrowImp || CannonNoCooldown || IgnoreHouse ||
                   IgnoreWarningLine || PlantSquash || JellyMode || ForceRain || ForceFog || IgnoreRedLine ||
                   SeedBankRandomZombie || ShovelCherry || BloverClearAll || CannonMultiShot > 1 ||
                   PlantFullRange || CrystalInfinite || WavePaused || SpawnMultiplier > 1 ||
                   BungiFastGrab || BungiIgnoreUmbrella || JackboxFastBomb || ZombiesFollowMouse || BungiSpawnJackbox ||
                   GlobalOverridesEnabled || ConsoleEnabled || TrickEnabled || TrickBox || TrickRain || TrickConveyor;
        }

        public const string Version = "0.9.3 修复版";    // mod 版本号
    }
}

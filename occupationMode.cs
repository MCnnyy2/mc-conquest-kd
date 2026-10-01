//MCCScript 1.0
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Net.Http;
//using System.Text.RegularExpressions;

MCC.LoadBot(new OccupationModeBot());

//MCCScript Extensions

// =====================================================================
// 占领模式计分脚本（/script occupationMode.cs）
//
// 规则：
//   1) 每个“正在被占领”的据点（该方人数占优且进度未到边界），每 0.5 秒 +1 分
//   2) 完全占领一个据点（进度推到 100% 或被打回 0%），占领方 +100 分
//   3) 击杀一名敌方玩家 +25 分
//   4) 任意一方先达到 gf_info.txt 中的“胜利分数”即获胜（默认 300）
//
// 数据流：
//   gf_info.txt   -> 读取“胜利分数”与据点列表
//   gf_player.csv -> 读取攻/守方名单（与 assaultDefense1.cs 共用）
//   gf_score.txt  -> 写出比分与据点进度
// =====================================================================
public class OccupationModeBot : ChatBot
{
    // ==================== 可调参数 ====================
    const double TickInterval = 0.5;          // 结算周期（秒）
    const int PassiveScore = 1;               // 每个正在占领的据点每周期 +1 分
    const int CaptureScore = 100;             // 完全占领据点 +100 分
    const int KillScore = 25;                 // 击杀敌方玩家 +25 分
    const int DefaultTargetScore = 300;       // gf_info.txt 未配置“胜利分数”时使用

    const double CaptureBaseRate = 0.5;       // 单人推进速度（每周期）
    const double CapturePerPlayerRate = 1.5;  // 每多一人额外推进速度（每周期）

    // 据点坐标范围（按实际地图修改，需与 assaultDefense1.cs 保持一致）
    static readonly Dictionary<string, (int minX, int maxX, int minZ, int maxZ)> Strongholds = new()
    {
        ["A"]  = (15193, 15211, 40183, 40192),
        ["B1"] = (15238, 15251, 40158, 40170),
        ["B2"] = (15236, 15243, 40222, 40231),
        ["C1"] = (15276, 15287, 40139, 40157),
        ["C2"] = (15290, 15306, 40194, 40212),
    };

    // 默认据点顺序；gf_info.txt 中若存在据点行则以文件内容为准
    static readonly string[] DefaultOrder = { "A", "B1", "B2", "C1", "C2" };

    // ==================== 文件与接口 ====================
    const string InfoPath = "gf_info.txt";     // 读取：胜利分数、据点列表
    const string CsvPath = "gf_player.csv";    // 阵营名单
    const string ScorePath = "gf_score.txt";   // 输出：比分与据点进度
    const string BlueMapUrl = "http://map.mangocraft.cn:2087/maps/world/live/players.json";

    // ==================== 运行状态 ====================
    readonly Dictionary<string, double> progress = new();   // 0 = 守方控制，100 = 攻方控制
    readonly Dictionary<string, int> direction = new();     // 1 攻方推进，-1 守方推进，0 静止
    readonly HashSet<string> attackerNames = new();
    readonly HashSet<string> defenderNames = new();
    readonly Dictionary<string, (double x, double z)> playerPositions = new();
    List<string> points = new(DefaultOrder);

    int targetScore = DefaultTargetScore;
    int attackerScore, defenderScore;
    bool ended;
    bool positionsOk = true;

    DateTime lastTick = DateTime.MinValue;
    DateTime lastCsvRead = DateTime.MinValue;
    DateTime lastScoreMsg = DateTime.MinValue;

    static readonly Regex[] DeathPatterns = BuildDeathPatterns();

    // ==================== 生命周期 ====================
    public override void Initialize()
    {
        LogToConsole("占领模式脚本加载，正在自检...");

        if (!File.Exists(CsvPath))
        {
            LogToConsole($"错误：缺少 {CsvPath}，脚本退出。");
            UnloadBot(); return;
        }
        if (!File.Exists(InfoPath))
        {
            LogToConsole($"错误：缺少 {InfoPath}，脚本退出。");
            UnloadBot(); return;
        }

        ReadInfoFile();
        ResetState();
        LogToConsole($"自检通过：目标分数 {targetScore}，据点 {string.Join("/", points)}，等待加入服务器...");
    }

    public override void AfterGameJoined()
    {
        ReadInfoFile();
        ResetState();
        ReadPlayerCsv();
        FetchPlayerPositions();

        var now = DateTime.UtcNow;
        lastTick = now;
        lastCsvRead = now;
        lastScoreMsg = DateTime.MinValue;

        SendText($"[占领模式] 开始！每 0.5 秒每个正在占领的据点 +{PassiveScore} 分，完全占领据点 +{CaptureScore} 分，击杀敌方玩家 +{KillScore} 分。");
        SendText($"[占领模式] 任意一方率先达到 {targetScore} 分即获胜。");
        SendScoreLine();
        WriteScoreFile();
    }

    public override void Update()
    {
        var now = DateTime.UtcNow;
        if ((now - lastTick).TotalSeconds < TickInterval) return;
        lastTick = now;

        FetchPlayerPositions();

        if ((now - lastCsvRead).TotalSeconds >= 1)
        {
            ReadPlayerCsv();
            lastCsvRead = now;
        }

        if (ended) return;

        Tick();

        if ((now - lastScoreMsg).TotalSeconds >= 5)
        {
            SendScoreLine();
            lastScoreMsg = now;
        }
    }

    // ==================== 击杀监听 ====================
    public override void GetText(string text)
    {
        if (ended) return;

        string raw = GetVerbatim(text);

        string victim = null, killer = null;
        foreach (var regex in DeathPatterns)
        {
            var match = regex.Match(raw);
            if (!match.Success) continue;
            victim = match.Groups["victim"].Value;
            if (match.Groups["killer"].Success) killer = match.Groups["killer"].Value;
            break;
        }
        if (string.IsNullOrEmpty(victim) || string.IsNullOrEmpty(killer) || killer == victim) return;

        ReadPlayerCsv();

        bool killerAtk = attackerNames.Contains(killer);
        bool killerDef = defenderNames.Contains(killer);
        bool victimAtk = attackerNames.Contains(victim);
        bool victimDef = defenderNames.Contains(victim);

        if (killerAtk && victimDef) AwardKill(true, killer, victim);
        else if (killerDef && victimAtk) AwardKill(false, killer, victim);
    }

    void AwardKill(bool attackerKiller, string killer, string victim)
    {
        if (attackerKiller) attackerScore += KillScore;
        else defenderScore += KillScore;

        SendText($"[占领] {(attackerKiller ? "攻方" : "守方")} {killer} 击杀 {victim}，+{KillScore} 分（攻 {attackerScore} : {defenderScore} 守）");
        WriteScoreFile();
        CheckWin();
    }

    // ==================== 每周期结算 ====================
    void Tick()
    {
        foreach (var pt in points)
        {
            int atk = CountInStronghold(pt, attackerNames);
            int def = CountInStronghold(pt, defenderNames);

            double prev = progress[pt];
            double delta;
            bool attackerCapturing, defenderCapturing;

            if (atk > def)
            {
                direction[pt] = 1;
                attackerCapturing = true;
                defenderCapturing = false;
                delta = CaptureBaseRate + (atk - def) * CapturePerPlayerRate;
            }
            else if (def > atk)
            {
                direction[pt] = -1;
                attackerCapturing = false;
                defenderCapturing = true;
                delta = -(CaptureBaseRate + (def - atk) * CapturePerPlayerRate);
            }
            else
            {
                direction[pt] = 0;
                attackerCapturing = false;
                defenderCapturing = false;
                delta = 0;
            }

            double next = Math.Clamp(prev + delta, 0, 100);
            progress[pt] = next;

            // 1) 正在占领：每周期为该方计分（进度已到该方边界的据点视为占领完成，不再计分）
            if (attackerCapturing && prev < 100) attackerScore += PassiveScore;
            if (defenderCapturing && prev > 0) defenderScore += PassiveScore;

            // 2) 完全占领奖励
            if (prev < 100 && next >= 100)
            {
                attackerScore += CaptureScore;
                SendText($"★ 攻方完全占领 {pt} 据点！+{CaptureScore} 分（攻 {attackerScore} : {defenderScore} 守）");
            }
            else if (prev > 0 && next <= 0)
            {
                defenderScore += CaptureScore;
                SendText($"★ 守方完全占领 {pt} 据点！+{CaptureScore} 分（攻 {attackerScore} : {defenderScore} 守）");
            }
        }

        WriteScoreFile();
        CheckWin();
    }

    void CheckWin()
    {
        if (attackerScore >= targetScore) EndGame("攻方");
        else if (defenderScore >= targetScore) EndGame("守方");
    }

    void EndGame(string winner)
    {
        if (ended) return;
        ended = true;
        WriteScoreFile();
        SendText($"🎉 {winner}率先达到 {targetScore} 分，获得占领模式胜利！（攻 {attackerScore} : {defenderScore} 守）");
        LogToConsole($"占领模式结束：{winner}获胜，攻 {attackerScore} : {defenderScore} 守");
    }

    // ==================== 状态初始化 ====================
    void ResetState()
    {
        progress.Clear();
        direction.Clear();
        foreach (var pt in points)
        {
            progress[pt] = 0;
            direction[pt] = 0;
        }
        playerPositions.Clear();
        attackerScore = 0;
        defenderScore = 0;
        ended = false;
        positionsOk = true;
    }

    // ==================== 数据读写 ====================
    void ReadInfoFile()
    {
        try
        {
            if (!File.Exists(InfoPath)) return;

            int? parsedTarget = null;
            var parsedPoints = new List<string>();

            foreach (var raw in File.ReadAllLines(InfoPath))
            {
                string line = raw.Trim().Replace('，', ',');
                if (line.Length == 0) continue;

                int comma = line.IndexOf(',');
                if (comma <= 0) continue;

                string key = line.Substring(0, comma).Trim();
                string val = line.Substring(comma + 1).Trim();

                if (key == "胜利分数")
                {
                    if (int.TryParse(val, out int t) && t > 0) parsedTarget = t;
                }
                else if (val.Contains('/'))
                {
                    if (Strongholds.ContainsKey(key))
                    {
                        if (!parsedPoints.Contains(key)) parsedPoints.Add(key);
                    }
                    else LogToConsole($"警告：据点 {key} 未在 Strongholds 中配置坐标，已忽略。");
                }
            }

            if (parsedTarget.HasValue) targetScore = parsedTarget.Value;
            points = parsedPoints.Count > 0 ? parsedPoints : new List<string>(DefaultOrder);
        }
        catch (Exception ex) { LogToConsole($"读取 {InfoPath} 失败: {ex.Message}"); }
    }

    void WriteScoreFile()
    {
        try
        {
            using (var sw = new StreamWriter(ScorePath, false))
            {
                sw.WriteLine("占领模式");
                sw.WriteLine($"胜利分数,{targetScore}");
                sw.WriteLine($"攻方分数,{attackerScore}");
                sw.WriteLine($"守方分数,{defenderScore}");
                foreach (var pt in points)
                    sw.WriteLine($"{pt},{(int)Math.Floor(progress[pt])}/100");
            }
        }
        catch (Exception ex) { LogToConsole($"写入 {ScorePath} 失败: {ex.Message}"); }
    }

    void ReadPlayerCsv()
    {
        try
        {
            if (!File.Exists(CsvPath)) return;

            bool inAtt = false, inDef = false;
            attackerNames.Clear();
            defenderNames.Clear();

            foreach (var line in File.ReadAllLines(CsvPath))
            {
                string t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith("攻方")) { inAtt = true; inDef = false; continue; }
                if (t.StartsWith("守方")) { inAtt = false; inDef = true; continue; }
                if (t.StartsWith("玩家")) continue;

                string playerName = t.Split(',')[0].Trim();
                if (string.IsNullOrEmpty(playerName)) continue;
                if (inAtt) attackerNames.Add(playerName);
                else if (inDef) defenderNames.Add(playerName);
            }
        }
        catch (Exception ex) { LogToConsole($"读取 CSV 失败: {ex.Message}"); }
    }

    void FetchPlayerPositions()
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                string json = client.GetStringAsync(BlueMapUrl).Result;

                playerPositions.Clear();
                int idx = 0;
                while ((idx = json.IndexOf("{\"uuid\":", idx, StringComparison.Ordinal)) != -1)
                {
                    int endIdx = json.IndexOf('}', idx);
                    if (endIdx == -1) break;

                    string playerObj = json.Substring(idx, endIdx - idx + 1);
                    double x = ExtractDouble(playerObj, "\"x\":");
                    double z = ExtractDouble(playerObj, "\"z\":");
                    string name = ExtractString(playerObj, "\"name\":\"", "\"");

                    if (name != null && !double.IsNaN(x) && !double.IsNaN(z))
                        playerPositions[name] = (x, z);

                    idx = endIdx + 1;
                }

                if (!positionsOk)
                {
                    positionsOk = true;
                    LogToConsole("玩家坐标已恢复，继续结算。");
                }
            }
        }
        catch (Exception ex)
        {
            playerPositions.Clear();
            if (positionsOk)
            {
                positionsOk = false;
                LogToConsole($"位置获取失败: {ex.Message}");
                SendText("[占领] 警告：无法获取玩家坐标，进度暂停更新");
            }
        }
    }

    double ExtractDouble(string source, string prefix)
    {
        int start = source.IndexOf(prefix, StringComparison.Ordinal);
        if (start == -1) return double.NaN;
        start += prefix.Length;

        int end = start;
        while (end < source.Length && (char.IsDigit(source[end]) || source[end] == '.' || source[end] == '-' || source[end] == 'e' || source[end] == 'E'))
            end++;

        if (double.TryParse(source.Substring(start, end - start), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double result))
            return result;
        return double.NaN;
    }

    string ExtractString(string source, string prefix, string suffix)
    {
        int start = source.IndexOf(prefix, StringComparison.Ordinal);
        if (start == -1) return null;
        start += prefix.Length;

        int end = source.IndexOf(suffix, start, StringComparison.Ordinal);
        if (end == -1) return null;
        return source.Substring(start, end - start);
    }

    // ==================== 显示 ====================
    void SendScoreLine()
    {
        var parts = points.Select(pt =>
        {
            string mark = direction[pt] > 0 ? "↑" : direction[pt] < 0 ? "↓" : "－";
            return $"{pt}:{(int)Math.Floor(progress[pt])}%{mark}";
        });

        SendText($"[占领] 攻 {attackerScore} : {defenderScore} 守 | {string.Join("  ", parts)} | 目标 {targetScore} 分");
    }

    int CountInStronghold(string point, HashSet<string> team)
    {
        if (!Strongholds.TryGetValue(point, out var box)) return 0;

        int cnt = 0;
        foreach (var name in team)
            if (playerPositions.TryGetValue(name, out var pos) &&
                pos.x >= box.minX && pos.x <= box.maxX &&
                pos.z >= box.minZ && pos.z <= box.maxZ)
                cnt++;
        return cnt;
    }

    // ==================== 死亡消息解析 ====================
    static Regex[] BuildDeathPatterns()
    {
        return new[]
        {
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)用(?<item>.+)杀死了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)用(?<item>.+)射杀$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)用(?<item>.+)刺穿了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)用(?<item>.+)给砸死了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)用(?<item>.+)一锤毙命$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)用(?<item>.+)发射的火球烧死了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)用(?<item>.+)发射的头颅射杀$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)用(?<item>.+)炸死了$"),
            new Regex(@"^(?<victim>\S+)在试图伤害(?<killer>\S+)时被(?<item>.+)杀死$"),
            new Regex(@"^(?<victim>\S+)在试图伤害(?<killer>\S+)时被杀$"),
            new Regex(@"^(?<victim>\S+)在试图逃离持有(?<item>.+)的(?<killer>\S+)时被一道音波尖啸抹除了$"),
            new Regex(@"^(?<victim>\S+)在试图逃离(?<killer>\S+)时被一道音波尖啸抹除了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)的龙息烤熟了$"),
            new Regex(@"^(?<victim>\S+)随着(?<killer>\S+)用(?<item>.+)发射的烟花发出的巨响消失了$"),
            new Regex(@"^(?<victim>\S+)在与(?<killer>\S+)战斗时随着一声巨响消失了$"),
            new Regex(@"^(?<victim>\S+)在与持有(?<item>.+)的(?<killer>\S+)战斗时被烤得酥脆$"),
            new Regex(@"^(?<victim>\S+)在与(?<killer>\S+)战斗时被烤得酥脆$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)使用的魔法杀死了$"),
            new Regex(@"^(?<victim>\S+)在试图逃离(?<killer>\S+)时被魔法杀死了$"),
            new Regex(@"^(?<victim>\S+)在与(?<killer>\S+)战斗时凋零了$"),
            new Regex(@"^(?<victim>\S+)在与(?<killer>\S+)战斗时饿死了$"),
            new Regex(@"^(?<victim>\S+)因为(?<killer>\S+)使用了(?<item>.+)注定要摔死$"),
            new Regex(@"^(?<victim>\S+)因为(?<killer>\S+)注定要摔死$"),
            new Regex(@"^(?<victim>\S+)摔伤得太重并被(?<killer>\S+)用(?<item>.+)完结了生命$"),
            new Regex(@"^(?<victim>\S+)摔伤得太重并被(?<killer>\S+)完结了生命$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)杀死了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)射杀$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)刺穿了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)给砸死了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)一锤毙命$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)炸死了$"),
            new Regex(@"^(?<victim>\S+)被(?<killer>\S+)发射的头颅射杀$"),
            new Regex(@"^(?<victim>\S+)在与(?<killer>\S+)战斗时被杀死了$"),
            new Regex(@"^(?<victim>\S+)死于(?<killer>\S+)$"),
            new Regex(@"^(?<victim>\S+)被一道音波尖啸抹除了$"),
            new Regex(@"^(?<victim>\S+)随着一声巨响消失了$"),
            new Regex(@"^(?<victim>\S+)被龙息烤熟了$"),
            new Regex(@"^(?<victim>\S+)被烧死了$"),
            new Regex(@"^(?<victim>\S+)被魔法杀死了$"),
            new Regex(@"^(?<victim>\S+)凋零了$"),
            new Regex(@"^(?<victim>\S+)饿死了$"),
            new Regex(@"^(?<victim>\S+)被戳死了$"),
            new Regex(@"^(?<victim>\S+)浴火焚身$"),
            new Regex(@"^(?<victim>\S+)试图在熔岩里游泳$"),
            new Regex(@"^(?<victim>\S+)发现了地板是熔岩做的$"),
            new Regex(@"^(?<victim>\S+)发现了不只有地板是熔岩做的$"),
            new Regex(@"^(?<victim>\S+)被甜浆果丛刺死了$"),
            new Regex(@"^(?<victim>\S+)在墙里窒息而亡$"),
            new Regex(@"^(?<victim>\S+)淹死了$"),
            new Regex(@"^(?<victim>\S+)因脱水而死$"),
            new Regex(@"^(?<victim>\S+)被冻死了$"),
            new Regex(@"^(?<victim>\S+)因被过度挤压而死$"),
            new Regex(@"^(?<victim>\S+)被闪电击中$"),
            new Regex(@"^(?<victim>\S+)脱离了这个世界$"),
            new Regex(@"^(?<victim>\S+)掉出了这个世界$"),
            new Regex(@"^(?<victim>\S+)落地过猛$"),
            new Regex(@"^(?<victim>\S+)被石笋刺穿了$"),
            new Regex(@"^(?<victim>\S+)被下落的铁砧压扁了$"),
            new Regex(@"^(?<victim>\S+)被下落的钟乳石刺穿了$"),
            new Regex(@"^(?<victim>\S+)被下落的方块压扁了$"),
            new Regex(@"^(?<victim>\S+)感受到了动能$"),
            new Regex(@"^(?<victim>\S+)从高处摔了下来$"),
            new Regex(@"^(?<victim>\S+)从梯子上摔了下来$"),
            new Regex(@"^(?<victim>\S+)从脚手架上摔了下来$"),
            new Regex(@"^(?<victim>\S+)从藤蔓上摔了下来$"),
            new Regex(@"^(?<victim>\S+)从垂泪藤上摔了下来$"),
            new Regex(@"^(?<victim>\S+)从缠怨藤上摔了下来$"),
            new Regex(@"^(?<victim>\S+)在攀爬时摔了下来$"),
            new Regex(@"^(?<victim>\S+)注定要摔死$"),
            new Regex(@"^(?<victim>\S+)爆炸了$"),
            new Regex(@"^(?<victim>\S+)被\[刻意的游戏设计\]杀死了$"),
            new Regex(@"^(?<victim>\S+)死了$"),
            new Regex(@"^(?<victim>\S+)被杀死了$"),
            new Regex(@"^(?<victim>\S+)被不为人知的魔法杀死了$"),
            new Regex(@"^(?<victim>\S+)摔伤得太重并被(?<killer>\S+)完结了生命$"),
        };
    }
}

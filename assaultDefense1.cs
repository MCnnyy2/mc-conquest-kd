//MCCScript 1.0
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Net.Http;
//using System.Text.RegularExpressions;

MCC.LoadBot(new AttackDefenseBot());

//MCCScript Extensions

public class AttackDefenseBot : ChatBot
{
    // ---------- 配置 ----------
    const int AreaMinX = 15162, AreaMaxX = 15346;
    const int AreaMinZ = 40137, AreaMaxZ = 40246;

    static readonly Dictionary<string, (int minX, int maxX, int minZ, int maxZ)> Strongholds = new()
    {
        ["A"]  = (15193, 15211, 40183, 40192),
        ["B1"] = (15238, 15251, 40158, 40170),
        ["B2"] = (15236, 15243, 40222, 40231),
        ["C1"] = (15276, 15287, 40139, 40157),
        ["C2"] = (15290, 15306, 40194, 40212),
    };

    static readonly string[] Order = { "A", "B1", "B2", "C1", "C2" };

    enum GamePhase
    {
        WaitingA, CapturingA, CooldownAfterA,
        CapturingB, CooldownAfterB, CapturingC,
        AttackerWin, DefenderWin
    }

    // ---------- 运行时状态 ----------
    GamePhase phase = GamePhase.WaitingA;
    DateTime countdownStart;
    DateTime lastCooldownMsgTime = DateTime.MinValue;
    DateTime lastProgressMsgTime = DateTime.MinValue;
    DateTime lastLogicTime = DateTime.MinValue;
    DateTime lastOutOfBoundsCheck = DateTime.MinValue;
    DateTime lastUnlimitedProgressMsgTime = DateTime.MinValue;  // 无限兵力播报间隔

    Dictionary<string, double> progress = new();
    Dictionary<string, int> progressDirection = new();
    HashSet<string> attackerNames = new(), defenderNames = new();
    Dictionary<string, (double x, double z)> playerPositions = new();

    // 兵力（直接加减）
    int baseForces = 0;
    int attackerForces = 0;

    // 超界管理
    Dictionary<string, DateTime> outOfBoundsStart = new();
    HashSet<string> publicWarnedPlayers = new();

    // 背水 / 破釜
    bool lastChanceUsed = false, finalChanceUsed = false;
    bool unlimitedForces = false;
    DateTime? unlimitedEndTime = null;

    // C1 制导
    bool c1Destroyed = false;
    bool c1DestroyScheduled = false;
    DateTime c1DestroyStart;
    bool c1DestroyAnimating = false;
    int c1DestroyXCount = 10;
    DateTime c1LastAnimStep;

    readonly string csvPath = "gf_player.csv";
    readonly string infoPath = "gf_info.txt";

    static readonly Regex[] DeathPatterns = BuildDeathPatterns();

    // ========== 生命周期 ==========
    public override void Initialize()
    {
        LogToConsole("攻防脚本加载，正在自检...");
        if (!File.Exists(csvPath))
        {
            LogToConsole("错误：缺少 gf_player.csv，脚本退出。");
            UnloadBot(); return;
        }
        if (!File.Exists(infoPath))
        {
            LogToConsole("错误：缺少 gf_info.txt，脚本退出。");
            UnloadBot(); return;
        }

        ReadInfoFile();
        if (baseForces <= 0)
        {
            LogToConsole("错误：攻方兵力为 0 或负数，脚本退出。");
            UnloadBot(); return;
        }
        attackerForces = baseForces;

        foreach (var pt in Order)
        {
            progress[pt] = 0;
            progressDirection[pt] = 1;
        }
        LogToConsole("自检通过，等待加入服务器...");
    }

    public override void AfterGameJoined()
    {
        SendText("[攻防] 脚本已启动，30秒后开放 A 点！");
        phase = GamePhase.WaitingA;
        countdownStart = DateTime.UtcNow;
        lastCooldownMsgTime = DateTime.MinValue;
        lastProgressMsgTime = DateTime.MinValue;
        lastLogicTime = DateTime.UtcNow;
        lastOutOfBoundsCheck = DateTime.MinValue;
        publicWarnedPlayers.Clear();
        ReadPlayerCsv();
        attackerForces = baseForces;
        lastChanceUsed = false;
        finalChanceUsed = false;
        unlimitedForces = false;
        unlimitedEndTime = null;
        FetchPlayerPositions();

        if (playerPositions.Count == 0)
        {
            LogToConsole("⚠ 警告：未能获取任何玩家坐标，请检查网络或 API 地址！");
            SendText("[攻防] 警告：无法获取玩家坐标，进度将暂停更新");
        }
    }

    public override void Update()
    {
        var now = DateTime.UtcNow;
        if ((now - lastLogicTime).TotalSeconds < 0.5) return;
        lastLogicTime = now;

        ReadPlayerCsv();
        FetchPlayerPositions();
        ProcessGuidanceEvent(now);

        if (phase == GamePhase.AttackerWin || phase == GamePhase.DefenderWin) return;

        if (unlimitedForces && unlimitedEndTime.HasValue && now >= unlimitedEndTime.Value)
{
    // 无限兵力时间到，恢复正常兵力（此时 attackerForces 仍为触发前的值，通常为 0）
    unlimitedForces = false;
    unlimitedEndTime = null;
    // 不调用 EndGame，让下一个 Update 循环检查：
    // 如果兵力为 0 且满足进度条件，将自动触发破釜沉舟；
    // 如果不满足条件，则正常判负。
    LogToConsole("无限兵力时间结束，重新评估兵力...");
}

        if (!unlimitedForces && attackerForces <= 0)
        {
            if (!TryTriggerLastStand(now))
            {
                EndGame(GamePhase.DefenderWin);
                return;
            }
        }

        // ===== 无限兵力定期播报 =====
        if (unlimitedForces && unlimitedEndTime.HasValue)
        {
            double remaining = (unlimitedEndTime.Value - now).TotalSeconds;
            if (remaining > 10)
            {
                if ((now - lastUnlimitedProgressMsgTime).TotalSeconds >= 4)
                {
                    SendProgressMessage();
                    lastUnlimitedProgressMsgTime = now;
                }
            }
            else if (remaining > 0)
            {
                if ((now - lastUnlimitedProgressMsgTime).TotalSeconds >= 1)
                {
                    SendProgressMessage();
                    lastUnlimitedProgressMsgTime = now;
                }
            }
        }

        switch (phase)
        {
            case GamePhase.WaitingA:        UpdateCountdown(now, 30, GamePhase.CapturingA); break;
            case GamePhase.CapturingA:      UpdateCapture(now, new[] { "A" }); break;
            case GamePhase.CooldownAfterA:  UpdateCooldown(now, 20, GamePhase.CapturingB); break;
            case GamePhase.CapturingB:      UpdateCapture(now, new[] { "B1", "B2" }); break;
            case GamePhase.CooldownAfterB:  UpdateCooldown(now, 20, GamePhase.CapturingC); break;
            case GamePhase.CapturingC:      UpdateCaptureC(now); break;
        }

        CheckOutOfBounds(now);
        WriteInfoFile();
    }

    // ========== 死亡监听 ==========
    public override void GetText(string text)
    {
        string raw = GetVerbatim(text);
        // 制导命令
        string noSpace = raw.Replace(" ", "");
        if (noSpace.Contains("山映水樱") && noSpace.Contains("制导"))
        {
            if (!c1DestroyScheduled && !c1Destroyed && !c1DestroyAnimating)
            {
                c1DestroyScheduled = true;
                c1DestroyStart = DateTime.UtcNow;
                SendText("C1花地正在摧毁，请撤离！");
                return;
            }
        }

        // 死亡解析
        string victim = null;
        foreach (var regex in DeathPatterns)
        {
            var match = regex.Match(raw);
            if (!match.Success) continue;
            victim = match.Groups["victim"].Value;
            break;
        }
        if (victim == null) return;

        // 即时刷新名单
        ReadPlayerCsv();
        if (!attackerNames.Contains(victim)) return;

        // 冷却期间不扣
        if (phase == GamePhase.CooldownAfterA || phase == GamePhase.CooldownAfterB) return;

        // 扣兵力
        attackerForces = Math.Max(0, attackerForces - 1);
        LogToConsole($"攻方 {victim} 死亡，剩余兵力 {attackerForces}");
    }

    // ========== 制导事件 ==========
    void ProcessGuidanceEvent(DateTime now)
    {
        if (c1DestroyScheduled && !c1DestroyAnimating)
        {
            double elapsed = (now - c1DestroyStart).TotalSeconds;
            if (elapsed >= 10)
            {
                c1DestroyScheduled = false;
                c1DestroyAnimating = true;
                c1DestroyXCount = 10;
                c1LastAnimStep = now;
                SendText("C1 开始崩毁！");
            }
        }
    }

    // ========== 阶段逻辑 ==========
    void UpdateCountdown(DateTime now, int totalSec, GamePhase nextPhase)
    {
        double elapsed = (now - countdownStart).TotalSeconds;
        int remaining = Math.Max(0, totalSec - (int)elapsed);

        if (remaining <= 0)
        {
            phase = nextPhase;
            if (nextPhase == GamePhase.CapturingA) progress["A"] = 0;
            else if (nextPhase == GamePhase.CapturingB) { progress["B1"] = 0; progress["B2"] = 0; }
            else if (nextPhase == GamePhase.CapturingC) { if (!c1Destroyed) progress["C1"] = 0; progress["C2"] = 0; }
            SendText($"据点开放！{GetPhaseName(nextPhase)}");
            lastProgressMsgTime = DateTime.MinValue;
            return;
        }

        bool shouldSend = (remaining > 5 && (now - lastCooldownMsgTime).TotalSeconds >= 5) ||
                          (remaining <= 5 && (now - lastCooldownMsgTime).TotalSeconds >= 1);
        if (shouldSend)
        {
            lastCooldownMsgTime = now;
            if (remaining > 5)
                SendText(FormatCooldownDisplay(remaining));
            else
            {
                string action = phase == GamePhase.WaitingA ? "抢夺" : "占领";
                SendText($"准备 {action} {remaining}秒");
            }
        }
    }

    void UpdateCooldown(DateTime now, int totalSec, GamePhase nextPhase)
    {
        double elapsed = (now - countdownStart).TotalSeconds;
        int remaining = Math.Max(0, totalSec - (int)elapsed);

        if (remaining <= 0)
        {
            phase = nextPhase;
            if (nextPhase == GamePhase.CapturingB) { progress["B1"] = 0; progress["B2"] = 0; }
            else if (nextPhase == GamePhase.CapturingC) { if (!c1Destroyed) progress["C1"] = 0; progress["C2"] = 0; }
            SendText($"据点开放！{GetPhaseName(nextPhase)}");
            lastProgressMsgTime = DateTime.MinValue;
            return;
        }

        bool shouldSend = (remaining > 5 && (now - lastCooldownMsgTime).TotalSeconds >= 5) ||
                          (remaining <= 5 && (now - lastCooldownMsgTime).TotalSeconds >= 1);
        if (shouldSend)
        {
            lastCooldownMsgTime = now;
            SendText(FormatCooldownDisplay(remaining));
        }
    }

    void UpdateCapture(DateTime now, string[] points)
    {
        bool changed = false;
        foreach (var pt in points)
        {
            if (pt == "C1" && c1Destroyed) continue;
            int atk = CountInStronghold(pt, attackerNames);
            int def = CountInStronghold(pt, defenderNames);
            double delta = 0;

            if (atk > def)
            {
                delta = 0.5 + (atk - def) * 1.5;
                progressDirection[pt] = 1;
            }
            else if (def > atk)
            {
                delta = -0.5 - (def - atk) * 1.5;
                progressDirection[pt] = -1;
            }
            else
            {
                delta = 0;
                progressDirection[pt] = 0;
            }

            double old = progress[pt];
            progress[pt] = Math.Clamp(progress[pt] + delta, 0, 100);
            if (Math.Abs(progress[pt] - old) > 0.01) changed = true;
        }

        if (changed && (now - lastProgressMsgTime).TotalSeconds >= 3)
        {
            SendProgressMessage(points);
            lastProgressMsgTime = now;
        }

        if (points.All(p => progress[p] >= 100))
            OnGroupCaptured(points);
    }

    void UpdateCaptureC(DateTime now)
    {
        if (c1DestroyAnimating)
        {
            if ((now - c1LastAnimStep).TotalSeconds >= 3)
            {
                c1LastAnimStep = now;
                c1DestroyXCount = Math.Max(1, c1DestroyXCount - 1);
                if (c1DestroyXCount <= 1)
                {
                    c1Destroyed = true;
                    c1DestroyAnimating = false;
                    progress["C1"] = -1;
                    SendText("C1 花地已被摧毁，只需占领 C2 即可！");
                }
                SendProgressMessage(new[] { "C1", "C2" });
                lastProgressMsgTime = now;
            }
            UpdateCapture(now, new[] { "C2" });
            return;
        }
        if (c1Destroyed)
        {
            UpdateCapture(now, new[] { "C2" });
            if (progress["C2"] >= 100) EndGame(GamePhase.AttackerWin);
            return;
        }
        UpdateCapture(now, new[] { "C1", "C2" });
    }

    void OnGroupCaptured(string[] points)
    {
        if (unlimitedForces)
        {
            unlimitedForces = false;
            unlimitedEndTime = null;
            SendText("成功占领据点，无限兵力结束。");
        }

if (points.Contains("A"))
{
    attackerForces = Math.Min(attackerForces + 60, baseForces);
    SendText($"A 组被攻方占领，兵力+60（当前 {attackerForces}）");
    phase = GamePhase.CooldownAfterA;
}
else if (points.Contains("B1"))
{
    attackerForces = Math.Min(attackerForces + 80, baseForces);
    SendText($"B 组被攻方占领，兵力+80（当前 {attackerForces}）");
    phase = GamePhase.CooldownAfterB;
}
        else
        {
            EndGame(GamePhase.AttackerWin);
            return;
        }

        countdownStart = DateTime.UtcNow;
        lastCooldownMsgTime = DateTime.MinValue;
    }

    bool TryTriggerLastStand(DateTime now)
    {
        if (phase != GamePhase.CapturingA && phase != GamePhase.CapturingB && phase != GamePhase.CapturingC)
            return false;

        bool condition = false;
        if (phase == GamePhase.CapturingA)
            condition = progress["A"] > 0;
        else if (phase == GamePhase.CapturingB)
            condition = (progress["B1"] >= 50 || progress["B2"] >= 50) && (progress["B1"] > 0 && progress["B2"] > 0);
        else if (phase == GamePhase.CapturingC)
        {
            if (c1Destroyed) condition = progress["C2"] > 0;
            else condition = (progress["C1"] >= 50 || progress["C2"] >= 50) && (progress["C1"] > 0 && progress["C2"] > 0);
        }

        if (!condition) return false;

        if (!lastChanceUsed)
        {
            lastChanceUsed = true;
            unlimitedForces = true;
            unlimitedEndTime = now.AddSeconds(70);
            lastUnlimitedProgressMsgTime = DateTime.MinValue; // 立即触发一次播报
            SendText("⚔ 攻方兵力耗尽！触发【背水一战】！无限兵力持续 70 秒！");
            return true;
        }
        else if (!finalChanceUsed)
        {
            finalChanceUsed = true;
            unlimitedForces = true;
            unlimitedEndTime = now.AddSeconds(40);
            lastUnlimitedProgressMsgTime = DateTime.MinValue;
            SendText("🔥 再次陷入绝境！触发【破釜沉舟】！无限兵力持续 40 秒！");
            return true;
        }
        return false;
    }

    void EndGame(GamePhase result)
    {
        phase = result;
        if (result == GamePhase.AttackerWin) SendText("🎉 攻方成功占领所有据点，攻方胜利！");
        else SendText("🛡 守方保卫了防线，守方胜利！");
        WriteInfoFile();
    }

    // ========== 超界检测 ==========
    void CheckOutOfBounds(DateTime now)
    {
        if ((now - lastOutOfBoundsCheck).TotalSeconds < 1) return;
        lastOutOfBoundsCheck = now;

        var participants = new HashSet<string>(attackerNames);
        participants.UnionWith(defenderNames);

        foreach (var name in participants)
        {
            if (!playerPositions.TryGetValue(name, out var pos)) continue;
            bool inArea = pos.x >= AreaMinX && pos.x <= AreaMaxX &&
                          pos.z >= AreaMinZ && pos.z <= AreaMaxZ;

            if (!inArea)
            {
                if (!outOfBoundsStart.ContainsKey(name))
                    outOfBoundsStart[name] = now;

                double dur = (now - outOfBoundsStart[name]).TotalSeconds;
                if (dur < 10)
                {
                    int remain = 10 - (int)dur;
                    SendPrivateMessage(name, $"超出活动区域！请返回战场（{remain}秒）");
                }
                else if (!publicWarnedPlayers.Contains(name))
                {
                    SendText($"{name} 超出活动区域！请返回战场！");
                    publicWarnedPlayers.Add(name);
                }
            }
            else
            {
                outOfBoundsStart.Remove(name);
                publicWarnedPlayers.Remove(name);
            }
        }

        var offline = outOfBoundsStart.Keys.Where(n => !playerPositions.ContainsKey(n)).ToList();
        foreach (var n in offline)
        {
            outOfBoundsStart.Remove(n);
            publicWarnedPlayers.Remove(n);
        }
    }

    // ========== 数据读写 ==========
    void FetchPlayerPositions()
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                string json = client.GetStringAsync("http://map.mangocraft.cn:2087/maps/world/live/players.json").Result;
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
            }
        }
        catch (Exception ex) { LogToConsole($"位置获取失败: {ex.Message}"); }
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

    void ReadPlayerCsv()
    {
        try
        {
            if (!File.Exists(csvPath)) return;
            var lines = File.ReadAllLines(csvPath);
            bool inAtt = false, inDef = false;
            attackerNames.Clear(); defenderNames.Clear();

            foreach (var line in lines)
            {
                string t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith("攻方") || t == "攻方") { inAtt = true; inDef = false; continue; }
                if (t.StartsWith("守方") || t == "守方") { inAtt = false; inDef = true; continue; }
                if (t.StartsWith("玩家")) continue;
                string playerName = t.Split(',')[0].Trim();
                if (string.IsNullOrEmpty(playerName)) continue;
                if (inAtt) attackerNames.Add(playerName);
                else if (inDef) defenderNames.Add(playerName);
            }
        }
        catch (Exception ex) { LogToConsole($"读取 CSV 失败: {ex.Message}"); }
    }

    void ReadInfoFile()
    {
        try
        {
            if (!File.Exists(infoPath)) return;
            foreach (var line in File.ReadAllLines(infoPath))
            {
                if (line.StartsWith("攻方兵力"))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int val))
                        baseForces = val;
                }
            }
        }
        catch { }
    }

    void WriteInfoFile()
    {
        try
        {
            using (var sw = new StreamWriter(infoPath, false))
            {
                sw.WriteLine("攻防数据");
                foreach (var pt in Order)
                {
                    string val = pt == "C1" && c1Destroyed ? "已摧毁" :
                                 $"{Math.Floor(progress[pt])}/100";
                    sw.WriteLine($"{pt},{val}");
                }
                string forcesDisplay = unlimitedForces ? "∞" : attackerForces.ToString();
                sw.WriteLine($"攻方兵力, {forcesDisplay}");
            }
        }
        catch (Exception ex) { LogToConsole($"写入进度失败: {ex.Message}"); }
    }

    // ========== 消息显示 ==========
    string FormatStrongholdDisplay(string name, double prog, int direction)
    {
        if (name == "C1" && (c1Destroyed || c1DestroyAnimating))
        {
            if (c1Destroyed) return "——已摧毁——";
            int xCount = c1DestroyXCount;
            string left = new string('×', xCount);
            string right = new string('×', xCount);
            return $"{left} C1 {right}";
        }

        int filled = (int)Math.Floor(prog / 10);
        filled = Math.Clamp(filled, 0, 10);
        int empty = 10 - filled;
        int percent = (int)Math.Floor(prog);

        string bar = direction switch
        {
            > 0 => new string('=', filled) + name + ">" + new string('=', empty),
            < 0 => new string('=', filled) + "<" + name + new string('=', empty),
            _   => new string('=', filled) + name + new string('=', empty)
        };
        return $"{bar} {percent}%";
    }

    string GetForcesDisplay()
    {
        if (unlimitedForces && unlimitedEndTime.HasValue)
        {
            int sec = Math.Max(0, (int)(unlimitedEndTime.Value - DateTime.UtcNow).TotalSeconds);
            return $"∞ ({sec}s)";
        }
        return unlimitedForces ? "∞" : attackerForces.ToString();
    }

    string FormatCooldownDisplay(int seconds)
    {
        string GetStatus(string pt)
        {
            if (pt == "C1" && c1Destroyed) return "×";
            if (progress[pt] >= 100) return "●";
            if (phase == GamePhase.WaitingA && pt == "A") return $"{seconds}s";
            if (phase == GamePhase.CooldownAfterA && (pt == "B1" || pt == "B2")) return $"{seconds}s";
            if (phase == GamePhase.CooldownAfterB && (pt == "C1" || pt == "C2")) return $"{seconds}s";
            return "○";
        }

        var statuses = Order.Select(pt => GetStatus(pt));
        string forcesStr = GetForcesDisplay();
        return $"{forcesStr} 攻➹    {string.Join("—", statuses)}    ♜防 ∞";
    }

    void SendProgressMessage(string[] activePoints = null)
    {
        if (activePoints == null)
        {
            activePoints = phase switch
            {
                GamePhase.CapturingA => new[] { "A" },
                GamePhase.CapturingB => new[] { "B1", "B2" },
                GamePhase.CapturingC => c1Destroyed ? new[] { "C2" } : new[] { "C1", "C2" },
                _ => Order
            };
        }

        string forcesStr = GetForcesDisplay();
        string line = $"{forcesStr} 攻➹    ";
        foreach (var pt in activePoints)
        {
            int dir = progressDirection.TryGetValue(pt, out var d) ? d : 0;
            line += FormatStrongholdDisplay(pt, progress[pt], dir) + "    ";
        }
        line += $"♜防 ∞";
        SendText(line);
    }

    string GetPhaseName(GamePhase p) => p switch
    {
        GamePhase.CapturingA => "A",
        GamePhase.CapturingB => "B1、B2",
        GamePhase.CapturingC => "C1、C2",
        _ => ""
    };

    int CountInStronghold(string point, HashSet<string> team)
    {
        var (minX, maxX, minZ, maxZ) = Strongholds[point];
        int cnt = 0;
        foreach (var name in team)
            if (playerPositions.TryGetValue(name, out var pos) &&
                pos.x >= minX && pos.x <= maxX && pos.z >= minZ && pos.z <= maxZ)
                cnt++;
        return cnt;
    }

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
//MCCScript 1.0
//using System.IO;
//using System.Collections.Generic;
//using System.Text.RegularExpressions;
//using System.Linq;

MCC.LoadBot(new EventKDStatBot());

//MCCScript Extensions

public class EventKDStatBot : ChatBot
{
    private const string CsvPath = "gf_player.csv";
    private string csvFullPath;

    private Dictionary<string, PlayerData> players = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> lastDeathTime = new(StringComparer.OrdinalIgnoreCase);
    private List<PendingNotification> pendingNotifications = new();

    // 自动重载
    private DateTime lastFileWriteTime = DateTime.MinValue;
    private DateTime nextFileCheck = DateTime.UtcNow.AddSeconds(5);

    // 启动清空间答
    private bool needAskClear = false;
    private bool waitingForClearResponse = false;
    private DateTime askTime = DateTime.MinValue;
    private const double AskTimeoutSeconds = 60;

    private bool firstLoad = true;
    private bool gameJoined = false;

    private static readonly HashSet<string> AuthorizedSenders = new(StringComparer.OrdinalIgnoreCase)
    {
        //此处填写管理员ID
    };

    private static readonly Regex[] DeathPatterns = BuildPatterns();

    private class PlayerData
    {
        public string OriginalName;
        public string Side;   // "攻" 或 "守"
        public string Team;   // 小队字母，空表示已退出
        public int Kills;
        public int Deaths;
        public bool IsActive;
    }

    private struct PendingNotification
    {
        public string Victim;
        public DateTime DeathTime;
    }

    // ========== 生命周期 ==========
    public override void Initialize()
    {
        csvFullPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, CsvPath);
        LoadPlayersFromCsv();
        LogToConsole($"Loaded {players.Count(p => p.Value.IsActive)} active players.");
        needAskClear = players.Values.Any(p => p.Kills > 0 || p.Deaths > 0);
    }

    public override void AfterGameJoined()
    {
        gameJoined = true;
        if (needAskClear && !waitingForClearResponse)
        {
            waitingForClearResponse = true;
            askTime = DateTime.UtcNow;
            SendText("[KD Bot] 检测到已有KD数据，是否清空？(是/否) 仅限授权者回复。");
        }
    }

    public override void Update()
    {
        var now = DateTime.UtcNow;
        if (now >= nextFileCheck)
        {
            nextFileCheck = now.AddSeconds(5);
            CheckAndReloadFile();
        }

        if (waitingForClearResponse && (now - askTime).TotalSeconds > AskTimeoutSeconds)
        {
            waitingForClearResponse = false;
            LogToConsole("Clear query timed out. Keeping current KD.");
            SendText("[KD Bot] 未收到回复，保留现有KD数据。");
        }

        for (int i = pendingNotifications.Count - 1; i >= 0; i--)
        {
            var pn = pendingNotifications[i];
            if (now < pn.DeathTime.AddSeconds(4)) continue;
            pendingNotifications.RemoveAt(i);
            ProcessNotification(pn.Victim);
        }
    }

    // ========== 消息处理 ==========
    public override void GetText(string text)
    {
        text = GetVerbatim(text).Trim();
        if (string.IsNullOrEmpty(text)) return;

        string message = "";
        string sender = "";

        // ---- 公屏消息 ----
        if (IsChatMessage(text, ref message, ref sender))
        {
            string cleanMsg = message.Replace(" ", ""); // 去掉空格后的内容

            // 管理员列队
            if (cleanMsg == "ls" && IsAuthorized(sender))
            {
                PrintTeamList();
                SendAllParticipantsInfo();
                return;
            }

            // 个人查询
            if (cleanMsg == "查")
            {
                SendSelfTeamInfo(sender);
                return;
            }

            // 清空确认（原有逻辑）
            if (waitingForClearResponse && IsAuthorized(sender))
            {
                var answer = message.Trim().Replace(" ", "").ToLowerInvariant();
                if (answer == "是" || answer == "y" || answer == "yes" || answer == "true" || answer == "1")
                {
                    ClearAllKD();
                    SendText("[KD Bot] 已清空所有KD数据。");
                    waitingForClearResponse = false;
                    return;
                }
                if (answer == "否" || answer == "n" || answer == "no" || answer == "false" || answer == "0")
                {
                    SendText("[KD Bot] 保留现有KD数据，继续累计。");
                    waitingForClearResponse = false;
                    return;
                }
            }
        }

        // ---- 私聊 reload（BotOwner） ----
        string cmd = "";
        string pmSender = "";
        if (IsPrivateMessage(text, ref cmd, ref pmSender)
            && Settings.Config.Main.Advanced.BotOwners.Contains(pmSender.ToLowerInvariant()))
        {
            if (cmd.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                LoadPlayersFromCsv();
                SendPrivateMessage(pmSender, $"Reloaded. Active: {players.Count(p => p.Value.IsActive)}");
                return;
            }
        }

        // ---- 死亡消息统计 ----
        string victim = null, killer = null;
        foreach (var regex in DeathPatterns)
        {
            var match = regex.Match(text);
            if (!match.Success) continue;
            victim = match.Groups["victim"].Value;
            killer = match.Groups["killer"].Success ? match.Groups["killer"].Value : null;
            break;
        }
        if (victim == null) return;

        if (!players.TryGetValue(victim, out var victimData) || !victimData.IsActive) return;

        victimData.Deaths++;
        lastDeathTime[victim] = DateTime.UtcNow;
        UpdateCsvPlayerKD(victimData.OriginalName, victimData.Kills, victimData.Deaths);

        if (killer != null && players.TryGetValue(killer, out var killerData) && killerData.IsActive)
        {
            killerData.Kills++;
            UpdateCsvPlayerKD(killerData.OriginalName, killerData.Kills, killerData.Deaths);
        }

        pendingNotifications.Add(new PendingNotification { Victim = victim, DeathTime = DateTime.UtcNow });
    }

    // ========== 队伍查询 ==========
    private bool IsAuthorized(string sender)
    {
        var cleanSender = sender.Replace(" ", "");
        foreach (var auth in AuthorizedSenders)
            if (string.Equals(cleanSender, auth.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void SendSelfTeamInfo(string playerName)
    {
        if (!players.TryGetValue(playerName, out var data) || !data.IsActive)
        {
            SendPrivateMessage(playerName, "您不在当前活动名单中。");
            return;
        }

        var side = data.Side == "攻" ? "攻方" : "守方";
        var teammates = GetActiveTeammates(data);
        var names = teammates.Select(t => t.OriginalName).ToList();
        string teamInfo = $"您所在的队伍：{side} {data.Team} 小队。队友：{(names.Count > 0 ? string.Join("、", names) : "暂无队友")}";
        SendPrivateMessage(data.OriginalName, teamInfo);
    }

    private void SendAllParticipantsInfo()
    {
        foreach (var p in players.Values.Where(p => p.IsActive))
            SendSelfTeamInfo(p.OriginalName);
    }

    private void PrintTeamList()
    {
        var attackers = players.Values.Where(p => p.IsActive && p.Side == "攻").OrderBy(p => p.Team).ThenBy(p => p.OriginalName).ToList();
        var defenders = players.Values.Where(p => p.IsActive && p.Side == "守").OrderBy(p => p.Team).ThenBy(p => p.OriginalName).ToList();

        // 公屏发送攻方列表
        SendText("----- 攻方 -----");
        foreach (var team in attackers.GroupBy(p => p.Team))
        {
            var line = string.Join("  ", team.Select(p => $"[{p.Team}]{p.OriginalName}"));
            SendText(line);
        }

        // 公屏发送守方列表
        SendText("----- 守方 -----");
        foreach (var team in defenders.GroupBy(p => p.Team))
        {
            var line = string.Join("  ", team.Select(p => $"[{p.Team}]{p.OriginalName}"));
            SendText(line);
        }

        SendText("----------------");
    }

    // ========== CSV 读写与变动检测 ==========
    private void LoadPlayersFromCsv()
    {
        if (!File.Exists(csvFullPath))
        {
            LogToConsole("CSV file not found: " + csvFullPath);
            return;
        }

        var oldPlayers = new Dictionary<string, PlayerData>(players);

        var lines = File.ReadAllLines(csvFullPath);
        var newPlayers = new Dictionary<string, PlayerData>(StringComparer.OrdinalIgnoreCase);

        string currentSide = "";
        bool inSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "攻方") { inSection = true; currentSide = "攻"; continue; }
            if (trimmed == "守方") { inSection = true; currentSide = "守"; continue; }
            if (!inSection || string.IsNullOrEmpty(trimmed) || trimmed.Contains("玩家id"))
                continue;

            var parts = trimmed.Split(',');
            if (parts.Length < 4) continue;

            var name = parts[0].Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var team = parts[3].Trim().ToUpperInvariant();
            bool isActive = !string.IsNullOrEmpty(team);

            int kills = 0, deaths = 0;
            int.TryParse(parts[1], out kills);
            int.TryParse(parts[2], out deaths);

            if (oldPlayers.TryGetValue(name, out var oldData))
            {
                kills = oldData.Kills;
                deaths = oldData.Deaths;
            }

            newPlayers[name.ToLowerInvariant()] = new PlayerData
            {
                OriginalName = name,
                Side = currentSide,
                Team = team,
                Kills = kills,
                Deaths = deaths,
                IsActive = isActive
            };
        }

        players = newPlayers;

        if (!firstLoad && gameJoined)
            DetectAndNotifyChanges(oldPlayers, newPlayers);

        firstLoad = false;
        LogToConsole($"CSV loaded. Active: {players.Count(p => p.Value.IsActive)} / Total: {players.Count}");
    }

    private void DetectAndNotifyChanges(Dictionary<string, PlayerData> oldPlayers, Dictionary<string, PlayerData> newPlayers)
    {
        foreach (var kvp in newPlayers)
        {
            var name = kvp.Key;
            var newData = kvp.Value;
            if (!oldPlayers.TryGetValue(name, out var oldData))
            {
                if (newData.IsActive) NotifyJoin(newData);
            }
            else
            {
                if (oldData.IsActive && newData.IsActive && (oldData.Side != newData.Side || oldData.Team != newData.Team))
                    NotifyTeamChange(oldData, newData);
                else if (oldData.IsActive && !newData.IsActive)
                    NotifyLeave(oldData);
            }
        }

        foreach (var kvp in oldPlayers)
        {
            var name = kvp.Key;
            var oldData = kvp.Value;
            if (!newPlayers.ContainsKey(name) && oldData.IsActive)
                NotifyLeave(oldData);
        }
    }

    private string SideText(string side) => side == "攻" ? "攻方" : "守方";

    private void NotifyJoin(PlayerData data)
    {
        var side = SideText(data.Side);
        SendText($"[KD] {data.OriginalName} 加入了 {side} {data.Team} 小队");

        var teammates = GetActiveTeammates(data);
        var teammateNames = string.Join("、", teammates.Select(p => p.OriginalName));
        SendPrivateMessage(data.OriginalName, $"您已加入 {side} {data.Team} 小队。队友：{(teammateNames.Length > 0 ? teammateNames : "暂无")}");

        foreach (var t in teammates)
            SendPrivateMessage(t.OriginalName, $"{data.OriginalName} 加入了您的队伍 [{data.Team}]");
    }

    private void NotifyLeave(PlayerData data)
    {
        var side = SideText(data.Side);
        SendText($"[KD] {data.OriginalName} 退出了 {side} {data.Team} 小队");
        SendPrivateMessage(data.OriginalName, $"您已退出 {side} {data.Team} 小队");

        var teammates = GetActiveTeammates(data);
        foreach (var t in teammates)
            SendPrivateMessage(t.OriginalName, $"{data.OriginalName} 离开了您的队伍 [{data.Team}]");
    }

    private void NotifyTeamChange(PlayerData oldData, PlayerData newData)
    {
        var oldSide = SideText(oldData.Side);
        var newSide = SideText(newData.Side);
        SendText($"[KD] {newData.OriginalName} 从 {oldSide} {oldData.Team} 小队 转至 {newSide} {newData.Team} 小队");

        var teammates = GetActiveTeammates(newData);
        var teammateNames = string.Join("、", teammates.Select(p => p.OriginalName));
        SendPrivateMessage(newData.OriginalName, $"您已转移至 {newSide} {newData.Team} 小队。队友：{(teammateNames.Length > 0 ? teammateNames : "暂无")}");

        foreach (var t in teammates)
            SendPrivateMessage(t.OriginalName, $"{newData.OriginalName} 加入了您的队伍 [{newData.Team}]");
    }

    private List<PlayerData> GetActiveTeammates(PlayerData data)
    {
        return players.Values
            .Where(p => p.IsActive && p.Side == data.Side && p.Team == data.Team && !p.OriginalName.Equals(data.OriginalName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ========== KD 更新 ==========
    private void UpdateCsvPlayerKD(string playerName, int kills, int deaths)
    {
        try
        {
            if (!File.Exists(csvFullPath)) return;
            var lines = File.ReadAllLines(csvFullPath);
            bool modified = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 4) continue;
                var name = parts[0].Trim();
                if (!name.Equals(playerName, StringComparison.OrdinalIgnoreCase)) continue;
                parts[1] = kills.ToString();
                parts[2] = deaths.ToString();
                lines[i] = string.Join(",", parts);
                modified = true;
                break;
            }
            if (modified) File.WriteAllLines(csvFullPath, lines);
        }
        catch (Exception ex) { LogToConsole($"Failed to update CSV: {ex.Message}"); }
    }

    private void ClearAllKD()
    {
        foreach (var p in players.Values)
        {
            p.Kills = 0;
            p.Deaths = 0;
            UpdateCsvPlayerKD(p.OriginalName, 0, 0);
        }
    }

    private void CheckAndReloadFile()
    {
        try
        {
            var fi = new FileInfo(csvFullPath);
            if (!fi.Exists) return;
            if (fi.LastWriteTimeUtc > lastFileWriteTime)
            {
                lastFileWriteTime = fi.LastWriteTimeUtc;
                LogToConsole("CSV changed, auto-reloading...");
                LoadPlayersFromCsv();
            }
        }
        catch { }
    }

    // ========== 传送通知 ==========
    private void ProcessNotification(string victimLower)
    {
        if (!players.TryGetValue(victimLower, out var victimData) || !victimData.IsActive) return;
        var team = victimData.Team;
        var now = DateTime.UtcNow;

        var activeTeammates = players.Values
            .Where(p => p.IsActive && p.Team == team && !p.OriginalName.Equals(victimData.OriginalName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (activeTeammates.Count == 0) return;

        var aliveTeammates = activeTeammates
            .Where(t => !lastDeathTime.TryGetValue(t.OriginalName, out var dt) || (now - dt).TotalSeconds >= 4)
            .Select(t => t.OriginalName)
            .ToList();
        if (aliveTeammates.Count == 0) return;

        SendPrivateMessage(victimData.OriginalName, $"[{team}]您可以传送队友：/tpa {string.Join(" ", aliveTeammates)}");
        foreach (var alive in aliveTeammates)
            SendPrivateMessage(alive, $"[{team}] {victimData.OriginalName}可能刚复活。请留意[{team}] {victimData.OriginalName}传送提示");
    }

    // ========== 死亡正则 ==========
    private static Regex[] BuildPatterns()
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

    public override void OnUnload()
    {
        LogToConsole("=== Event KD Summary ===");
        foreach (var p in players.Values)
            LogToConsole($"{p.OriginalName} [{(p.IsActive ? p.Side + " " + p.Team : "OUT")}] K:{p.Kills} D:{p.Deaths}");
    }
}
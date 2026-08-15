//MCCScript 1.0
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;

MCC.LoadBot(new ListTeamsBot());

//MCCScript Extensions

public class ListTeamsBot : ChatBot
{
    private string csvPath = "gf_player.csv";

    private class PlayerData
    {
        public string OriginalName;
        public string Side;   // "攻" or "守"
        public string Team;   // 小队字母
    }

    public override void Initialize()
    {
        if (!File.Exists(csvPath))
        {
            LogToConsole("gf_player.csv not found.");
            UnloadBot();
        }
    }

    public override void AfterGameJoined()
    {
        var players = ReadPlayers();
        if (players.Count == 0)
        {
            SendText("[名单] 当前无参赛玩家。");
            UnloadBot();
            return;
        }

        // 公屏打印队伍列表
        PrintTeamList(players);
        // 私信每位玩家
        SendPrivateInfo(players);

        // 执行完毕后卸载自身
        UnloadBot();
    }

    private List<PlayerData> ReadPlayers()
    {
        var result = new List<PlayerData>();
        string[] lines;
        try
        {
            lines = File.ReadAllLines(csvPath);
        }
        catch
        {
            return result;
        }

        string currentSide = "";
        foreach (var line in lines)
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t == "攻方") { currentSide = "攻"; continue; }
            if (t == "守方") { currentSide = "守"; continue; }
            if (t.StartsWith("玩家")) continue;

            var parts = t.Split(',');
            if (parts.Length < 4) continue;
            string name = parts[0].Trim();
            string team = parts[3].Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(team)) continue;

            result.Add(new PlayerData
            {
                OriginalName = name,
                Side = currentSide,
                Team = team
            });
        }
        return result;
    }

    private void PrintTeamList(List<PlayerData> players)
    {
        var attackers = players.Where(p => p.Side == "攻")
                               .OrderBy(p => p.Team)
                               .ThenBy(p => p.OriginalName)
                               .ToList();
        var defenders = players.Where(p => p.Side == "守")
                               .OrderBy(p => p.Team)
                               .ThenBy(p => p.OriginalName)
                               .ToList();

        SendText("----- 攻方 -----");
        foreach (var group in attackers.GroupBy(p => p.Team))
        {
            string line = string.Join("  ", group.Select(p => $"[{p.Team}]{p.OriginalName}"));
            SendText(line);
        }

        SendText("----- 守方 -----");
        foreach (var group in defenders.GroupBy(p => p.Team))
        {
            string line = string.Join("  ", group.Select(p => $"[{p.Team}]{p.OriginalName}"));
            SendText(line);
        }

        SendText("----------------");
    }

    private void SendPrivateInfo(List<PlayerData> players)
    {
        foreach (var p in players)
        {
            var side = p.Side == "攻" ? "攻方" : "守方";
            var teammates = players
                .Where(o => o.Side == p.Side && o.Team == p.Team && !o.OriginalName.Equals(p.OriginalName, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.OriginalName)
                .ToList();

            string info = $"您所在的队伍：{side} {p.Team} 小队。队友：{(teammates.Count > 0 ? string.Join("、", teammates) : "暂无队友")}";
            SendPrivateMessage(p.OriginalName, info);
        }
    }
}
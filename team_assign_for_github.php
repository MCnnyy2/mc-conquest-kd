<?php
session_start();
header('Content-Type: text/html; charset=utf-8');

$password = ''; //场务密码
$csvFile = '/gf_player.csv';
$infoFile = '/gf_info.txt';
$proxyUrl = ''; //代理bluemap的网址

// ---------- 登录 ----------
if ($_SERVER['REQUEST_METHOD'] === 'POST' && isset($_POST['password'])) {
    if ($_POST['password'] === $password) {
        $_SESSION['admin'] = true;
    } else {
        $error = '密码错误';
    }
}
if (isset($_GET['logout'])) {
    session_destroy();
    header('Location: team_assign.php');
    exit;
}
if (empty($_SESSION['admin'])) {
    ?>
    <!DOCTYPE html><html><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"><title>场务登录</title>
    <style>body{font-family:sans-serif;display:flex;justify-content:center;align-items:center;height:100vh;background:#1a1a2e;color:#eee;margin:0;} form{background:#16213e;padding:30px;border-radius:12px;width:90%;max-width:350px;} input{width:100%;box-sizing:border-box;padding:10px;margin:10px 0;border-radius:6px;border:none;background:#0d1425;color:#eee;} button{width:100%;padding:10px;background:#3a6bd5;color:white;border:none;border-radius:6px;font-size:1em;}</style>
    </head><body><form method="post"><h2>攻防战场务管理</h2>
    <?php if(isset($error)) echo "<p style='color:red'>$error</p>"; ?>
    <input type="password" name="password" placeholder="密码"><button type="submit">登录</button></form></body></html>
    <?php
    exit;
}

// ---------- 辅助函数 ----------
function canonName($name) {
    return strtolower(str_replace(['.', '_'], '', $name));
}

function readPlayers() {
    global $csvFile;
    if (!file_exists($csvFile)) return [];
    $lines = file($csvFile, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
    $currentSide = '';
    $players = [];
    foreach ($lines as $line) {
        $line = trim($line);
        if ($line === '攻方') { $currentSide = '攻'; continue; }
        if ($line === '守方') { $currentSide = '守'; continue; }
        $cols = str_getcsv($line);
        if (count($cols) >= 4) {
            $players[] = [
                'name'   => $cols[0],
                'side'   => $currentSide,
                'kills'  => (int)$cols[1],
                'deaths' => (int)$cols[2],
                'team'   => $cols[3]   // 小队字母，空表示已退出
            ];
        }
    }
    return $players;
}

function writePlayers($players) {
    global $csvFile;
    $attackers = array_filter($players, fn($p) => $p['side'] === '攻');
    $defenders = array_filter($players, fn($p) => $p['side'] === '守');
    $lines = ["攻方"];
    foreach ($attackers as $p) $lines[] = implode(',', [$p['name'], $p['kills'], $p['deaths'], $p['team']]);
    $lines[] = "守方";
    foreach ($defenders as $p) $lines[] = implode(',', [$p['name'], $p['kills'], $p['deaths'], $p['team']]);
    file_put_contents($csvFile, implode("\n", $lines) . "\n");
}

/**
 * 生成分配建议，返回数组 ['side' => '攻'|'守', 'team' => 'A'...]
 * 阵营选择：①人数少 ②基岩版人数少 ③攻方优先
 * 小队选择：
 *   1. 对方未用该字母且本阵营该队 1~2 人（已有未满），按本阵营人数升序
 *   2. 无满足1的，选对方未用且本阵营 0 人的完全空闲字母
 *   3. 兜底：本阵营人数<3 的字母（即使对方也用），按人数升序
 */
function makeAssignmentSuggestion($attackCount, $defendCount, $bedrockAttack, $bedrockDefend, $teamStats) {
    // 选择阵营
    if ($attackCount < $defendCount) {
        $side = '攻';
    } elseif ($defendCount < $attackCount) {
        $side = '守';
    } else {
        if ($bedrockAttack < $bedrockDefend) {
            $side = '攻';
        } elseif ($bedrockDefend < $bedrockAttack) {
            $side = '守';
        } else {
            $side = '攻'; // 攻方优先
        }
    }

    $ownKey   = ($side === '攻') ? 'attack' : 'defend';
    $otherKey = ($side === '攻') ? 'defend' : 'attack';

    // 第一优先：对方未用，本阵营已有 1~2 人（未满3）
    $candidatesFirst = [];
    foreach (range('A', 'Z') as $L) {
        $own = $teamStats[$L][$ownKey];
        $other = $teamStats[$L][$otherKey];
        if ($other === 0 && $own >= 1 && $own <= 2) {
            $candidatesFirst[] = ['letter' => $L, 'count' => $own];
        }
    }
    if (!empty($candidatesFirst)) {
        usort($candidatesFirst, function($a, $b) {
            if ($a['count'] != $b['count']) return $a['count'] - $b['count'];
            return strcmp($a['letter'], $b['letter']);
        });
        return ['side' => $side, 'team' => $candidatesFirst[0]['letter']];
    }

    // 第二优先：对方未用且本阵营 0 人（完全空闲）
    $candidatesSecond = [];
    foreach (range('A', 'Z') as $L) {
        $own = $teamStats[$L][$ownKey];
        $other = $teamStats[$L][$otherKey];
        if ($other === 0 && $own === 0) {
            $candidatesSecond[] = ['letter' => $L, 'count' => 0];
        }
    }
    if (!empty($candidatesSecond)) {
        usort($candidatesSecond, fn($a, $b) => strcmp($a['letter'], $b['letter']));
        return ['side' => $side, 'team' => $candidatesSecond[0]['letter']];
    }

    // 第三兜底：本阵营人数<3（即使对方已用），按人数升序
    $candidatesThird = [];
    foreach (range('A', 'Z') as $L) {
        $own = $teamStats[$L][$ownKey];
        if ($own < 3) {
            $candidatesThird[] = ['letter' => $L, 'count' => $own];
        }
    }
    if (!empty($candidatesThird)) {
        usort($candidatesThird, function($a, $b) {
            if ($a['count'] != $b['count']) return $a['count'] - $b['count'];
            return strcmp($a['letter'], $b['letter']);
        });
        return ['side' => $side, 'team' => $candidatesThird[0]['letter']];
    }

    // 理论不会到这里，但兜底返回 A
    return ['side' => $side, 'team' => 'A'];
}

// ---------- 操作处理 ----------
$message = '';
$messageType = '';

if (isset($_POST['action']) && $_POST['action'] === 'assign') {
    $name = trim($_POST['player'] ?? '');
    $side = $_POST['side'] ?? '攻';
    $team = strtoupper(trim($_POST['team'] ?? 'A'));

    if (!in_array($side, ['攻', '守'])) $side = '攻';
    if (!preg_match('/^[A-Z]$/', $team)) $team = 'A';

    if ($name === '') {
        $message = '玩家名不能为空';
        $messageType = 'error';
    } else {
        $players = readPlayers();
        $found = false;
        $inputCanon = canonName($name);

        foreach ($players as &$p) {
            if (canonName($p['name']) === $inputCanon) {
                $p['side'] = $side;
                $p['team'] = $team;
                $found = true;
                break;
            }
        }
        unset($p);

        if (!$found) {
            $players[] = ['name' => $name, 'side' => $side, 'kills' => 0, 'deaths' => 0, 'team' => $team];
        }

        writePlayers($players);
        $message = "已添加/更新 {$name}";
        $messageType = 'success';
    }
}

if (isset($_POST['action']) && $_POST['action'] === 'kick') {
    $name = $_POST['player'] ?? '';
    $targetCanon = canonName($name);
    $players = readPlayers();
    $newPlayers = [];
    $kicked = false;

    foreach ($players as $p) {
        if (canonName($p['name']) === $targetCanon) {
            $kicked = true;
            continue;
        }
        $newPlayers[] = $p;
    }

    if ($kicked) {
        writePlayers($newPlayers);
        echo json_encode(['success' => true]);
    } else {
        echo json_encode(['success' => false, 'error' => '玩家不存在']);
    }
    exit;
}

if (isset($_POST['action']) && $_POST['action'] === 'reset') {
    file_put_contents($csvFile, "攻方\n守方\n");
    file_put_contents($infoFile, "攻防数据\nA,0/100\nB1,0/100\nB2,0/100\nC1,0/100\nC2,0/100\n攻方兵力, 0\n");
    $message = '游戏文件已重置';
    $messageType = 'success';
}

$players = readPlayers();
$assignedNames = array_column($players, 'name');

$attackPlayers = array_filter($players, fn($p) => $p['side'] === '攻');
$defendPlayers = array_filter($players, fn($p) => $p['side'] === '守');
usort($attackPlayers, fn($a, $b) => strcmp($a['team'], $b['team']) ?: strcmp($a['name'], $b['name']));
usort($defendPlayers, fn($a, $b) => strcmp($a['team'], $b['team']) ?: strcmp($a['name'], $b['name']));

$totalAttack = count($attackPlayers);
$totalDefend = count($defendPlayers);
$bedrockAttack = count(array_filter($attackPlayers, fn($p) => substr($p['name'], 0, 1) === '.'));
$bedrockDefend = count(array_filter($defendPlayers, fn($p) => substr($p['name'], 0, 1) === '.'));

// 统计各小队攻守人数
$teamStats = [];
foreach (range('A', 'Z') as $L) {
    $teamStats[$L] = ['attack' => 0, 'defend' => 0];
}
foreach ($attackPlayers as $p) {
    $t = strtoupper(trim($p['team'] ?? ''));
    if ($t !== '' && isset($teamStats[$t])) $teamStats[$t]['attack']++;
}
foreach ($defendPlayers as $p) {
    $t = strtoupper(trim($p['team'] ?? ''));
    if ($t !== '' && isset($teamStats[$t])) $teamStats[$t]['defend']++;
}

$suggestion = makeAssignmentSuggestion($totalAttack, $totalDefend, $bedrockAttack, $bedrockDefend, $teamStats);
$suggestionText = "建议分配至：{$suggestion['side']} / {$suggestion['team']}";
?>
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>攻防场务管理</title>
    <style>
        * { box-sizing: border-box; }
        body {
            font-family: 'Segoe UI', 'Microsoft YaHei', sans-serif;
            background: #0a0f1e; color: #e0e6f0; margin: 0; padding: 10px;
        }
        .container { max-width: 1200px; margin: auto; }
        .header { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; margin: 10px 0 20px; }
        .header h1 { font-size: 1.6em; margin: 0; }
        .logout { color: #e74c3c; text-decoration: none; font-weight: bold; }

        .message { padding: 10px; border-radius: 6px; margin: 10px 0; }
        .success { background: #1e3a1e; color: #2ecc71; }
        .error { background: #3a1e1e; color: #e74c3c; }

        /* 分配表单 */
        .assign-form {
            background: #0d1425; border-radius: 12px; padding: 15px;
            margin-bottom: 12px; border: 1px solid #1e2a4a;
            display: flex; flex-wrap: wrap; gap: 10px; align-items: center;
        }
        .assign-form input {
            padding: 8px 10px; border-radius: 6px; border: 1px solid #3a4a6a;
            background: #121a2b; color: #eee; font-size: 0.95em;
        }
        .assign-form button {
            padding: 9px 20px; border-radius: 6px; border: none;
            background: #3a6bd5; color: white; cursor: pointer; font-weight: bold;
        }
        .autocomplete-wrapper { position: relative; flex: 1 1 200px; }
        #playerInput { width: 100%; }
        .suggestions {
            position: absolute; top: 100%; left: 0; right: 0;
            background: #1a2035; border: 1px solid #3a4a6a;
            border-radius: 0 0 8px 8px; max-height: 150px; overflow-y: auto;
            display: none; z-index: 1000;
        }
        .suggestion-item {
            padding: 8px 12px; cursor: pointer; border-bottom: 1px solid #2a3a5a;
        }
        .suggestion-item:hover { background: #2a3a6a; }

        .side-btns { display: flex; gap: 6px; }
        .side-btn {
            padding: 9px 16px; border-radius: 6px; border: 2px solid transparent;
            cursor: pointer; font-weight: bold; background: #121a2b; color: #eee;
        }
        .side-btn.attack { border-color: #2ecc71; color: #2ecc71; }
        .side-btn.defend { border-color: #e74c3c; color: #e74c3c; }
        .side-btn.active {
            outline: 2px solid #fff;
            outline-offset: 2px;
        }
        .side-btn.active.attack { background: #1a3a1e; }
        .side-btn.active.defend { background: #3a1e1e; }

        .team-selector-area { flex-basis: 100%; margin-top: 5px; }
        .team-header { display: flex; align-items: center; gap: 8px; margin-bottom: 5px; }
        .toggle-btn {
            background: #2a3a5a; color: #eee; border: none;
            padding: 4px 10px; border-radius: 4px; cursor: pointer;
        }
        .team-selector { display: flex; flex-wrap: wrap; gap: 6px; }
        .team-btn {
            flex: 0 0 96px; padding: 6px 0; border-radius: 6px;
            border: 1px solid #3a4a6a; background: #121a2b; color: #eee;
            cursor: pointer; font-size: 0.85em; white-space: nowrap; text-align: center;
        }
        .team-btn.selected { outline: 2px solid #fff; }
        .team-btn.attack { background: #1a3a1e; border-color: #2ecc71; color: #2ecc71; }
        .team-btn.defend { background: #3a1e1e; border-color: #e74c3c; color: #e74c3c; }
        .team-btn.both { background: #3a2e1e; border-color: #e67e22; color: #e67e22; }
        .team-btn.empty { background: #121a2b; color: #7f8c8d; border-color: #3a4a6a; }

        .suggestion-box {
            background: #0d1425; border: 1px dashed #3a6bd5;
            border-radius: 12px; padding: 12px 15px; margin-bottom: 15px; color: #f1c40f;
            font-weight: bold;
        }

        /* 安检未分配区域 */
        .checkpoint-area {
            background: #0d1425; border-radius: 12px; padding: 15px;
            margin-bottom: 25px; border: 1px solid #2e4a1e;
        }
        .checkpoint-area h3 { margin: 0 0 10px; color: #2ecc71; }
        .checkpoint-list { display: flex; flex-wrap: wrap; gap: 8px; min-height: 24px; }
        .checkpoint-player {
            background: #1a3a1e; padding: 4px 10px; border-radius: 6px;
            cursor: pointer; transition: background 0.2s;
        }
        .checkpoint-player:hover { background: #2a5a2e; }
        .checkpoint-empty { color: #7f8c8d; font-style: italic; }

        /* 总信息 */
        .summary-panel {
            background: #0d1425; border-radius: 12px; padding: 15px;
            margin-bottom: 20px; border: 1px solid #1e2a4a;
        }
        .summary-cards { display: flex; gap: 12px; flex-wrap: wrap; }
        .summary-card {
            flex: 1; min-width: 160px; padding: 12px; border-radius: 8px; background: #121a2b;
        }
        .summary-card.attack { border-left: 4px solid #2ecc71; }
        .summary-card.defend { border-left: 4px solid #e74c3c; }
        .summary-card b { font-size: 1.3em; }
        .team-summary { margin-top: 10px; display: flex; flex-wrap: wrap; gap: 6px; }
        .team-summary-item { padding: 4px 10px; border-radius: 6px; background: #121a2b; color: #ccc; }
        .team-summary-item.attack { color: #2ecc71; background: #1a3a1e; }
        .team-summary-item.defend { color: #e74c3c; background: #3a1e1e; }
        .team-summary-item.both { color: #e67e22; background: #3a2e1e; }

        /* 双列玩家面板 */
        .panels { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-bottom: 25px; }
        @media (max-width: 700px) {
            .panels { grid-template-columns: 1fr; }
            .assign-form { flex-direction: column; align-items: stretch; }
            .autocomplete-wrapper { width: 100%; }
            .side-btns { width: 100%; }
            .side-btns button { flex: 1; }
        }
        .panel { background: #0d1425; border-radius: 12px; padding: 15px; border: 1px solid #1e2a4a; }
        .attack h3 { color: #5b9cf5; margin-top: 0; }
        .defend h3 { color: #f55b5b; margin-top: 0; }

        .player-row {
            display: flex; flex-wrap: wrap; align-items: center; gap: 8px;
            padding: 8px 0; border-bottom: 1px solid #1e2a4a; position: relative;
        }
        .player-name { flex: 1; min-width: 100px; font-weight: bold; }
        .team-badge { background: #2a3a5a; padding: 2px 8px; border-radius: 4px; font-size: 0.85em; white-space: nowrap; }
        .stats { font-size: 0.9em; white-space: nowrap; }
        .kick-btn {
            background: #c0392b; color: white; border: none; padding: 4px 12px;
            border-radius: 4px; cursor: pointer; font-size: 0.85em;
        }

        .slider-row {
            display: none; padding: 8px 0; gap: 8px; align-items: center;
        }
        .slider-row input[type=range] { flex: 1; }
        .slider-row span { font-size: 0.85em; color: #aaa; }

        .reset-section { margin-top: 30px; }
        .reset-section form { display: inline; }
        .danger-btn {
            background: #c0392b; color: white; border: none; padding: 10px 20px;
            border-radius: 6px; cursor: pointer; font-weight: bold;
        }
    </style>
</head>
<body>
<div class="container">
    <div class="header">
        <h1>⚙️ 攻防场务管理</h1>
        <a class="logout" href="?logout=1">退出登录</a>
    </div>

    <?php if ($message): ?>
    <div class="message <?= $messageType ?>"><?= htmlspecialchars($message) ?></div>
    <?php endif; ?>

    <!-- 分配表单 -->
    <div class="assign-form">
        <div class="autocomplete-wrapper">
            <input type="search" id="playerInput" placeholder="玩家名" autocomplete="off" autocorrect="off" autocapitalize="off" spellcheck="false">
            <div class="suggestions" id="suggestions"></div>
        </div>
        <div class="side-btns">
            <button type="button" id="sideAttackBtn" class="side-btn attack active">🔵 攻方（<?= $totalAttack ?>人）</button>
            <button type="button" id="sideDefendBtn" class="side-btn defend">🔴 守方（<?= $totalDefend ?>人）</button>
        </div>
        <div class="team-selector-area">
            <div class="team-header">
                <label>小队:</label>
                <button type="button" id="teamToggleBtn" class="toggle-btn">展开</button>
            </div>
            <div id="teamSelector" class="team-selector"></div>
        </div>
        <button id="assignBtn">分配/添加</button>
    </div>

    <!-- 分配建议（简洁版） -->
    <div class="suggestion-box"><?= htmlspecialchars($suggestionText) ?></div>

    <!-- 安检未分配玩家 -->
    <div class="checkpoint-area">
        <h3>🛂 安检未分配玩家（实时）</h3>
        <div class="checkpoint-list" id="checkpointList">
            <span class="checkpoint-empty">正在加载...</span>
        </div>
    </div>

    <!-- 总信息 -->
    <div class="summary-panel">
        <div class="summary-cards">
            <div class="summary-card attack">🔵 攻方总人数：<b><?= $totalAttack ?></b> 人 · 基岩版：<b><?= $bedrockAttack ?></b> 位</div>
            <div class="summary-card defend">🔴 守方总人数：<b><?= $totalDefend ?></b> 人 · 基岩版：<b><?= $bedrockDefend ?></b> 位</div>
        </div>
        <div class="team-summary">
            <?php
            $hasTeam = false;
            foreach (range('A', 'Z') as $L) {
                $a = $teamStats[$L]['attack'];
                $d = $teamStats[$L]['defend'];
                if ($a > 0 || $d > 0) {
                    $hasTeam = true;
                    $cls = ($a > 0 && $d > 0) ? 'both' : (($a > 0) ? 'attack' : 'defend');
                    $txt = $L . '(';
                    if ($a > 0) $txt .= "攻{$a}";
                    if ($d > 0) $txt .= "守{$d}";
                    $txt .= '人)';
                    echo '<span class="team-summary-item ' . $cls . '">' . htmlspecialchars($txt) . '</span>';
                }
            }
            if (!$hasTeam) echo '<span style="color:#7f8c8d">暂无小队分配</span>';
            ?>
        </div>
    </div>

    <!-- 双列玩家列表 -->
    <div class="panels">
        <div class="panel attack" id="attackPanel">
            <h3>🔵 攻方</h3>
            <?php foreach ($attackPlayers as $p): ?>
            <?= renderPlayerRow($p) ?>
            <?php endforeach; ?>
        </div>
        <div class="panel defend" id="defendPanel">
            <h3>🔴 守方</h3>
            <?php foreach ($defendPlayers as $p): ?>
            <?= renderPlayerRow($p) ?>
            <?php endforeach; ?>
        </div>
    </div>

    <!-- 重置 -->
    <div class="reset-section panel">
        <h3>⚠️ 重置游戏</h3>
        <p>清空 gf_player.csv 和 gf_info.txt 为初始状态。</p>
        <form method="post" onsubmit="return confirm('确定重置？所有数据将丢失！');">
            <input type="hidden" name="action" value="reset">
            <button type="submit" class="danger-btn">重置所有文件</button>
        </form>
    </div>
</div>

<script>
// ---------- 工具函数 ----------
function normName(s) {
    return String(s).toLowerCase().replace(/[._]/g, '');
}

const assignedNames = <?= json_encode($assignedNames) ?>;
const teamStats = <?= json_encode($teamStats) ?>;
const proxyUrl = <?= json_encode($proxyUrl) ?>;
const assignedNormSet = new Set(assignedNames.map(normName));

let selectedSide = '攻';
let selectedTeam = 'A';
let teamExpanded = false;

// ---------- 阵营按钮 ----------
const sideAttackBtn = document.getElementById('sideAttackBtn');
const sideDefendBtn = document.getElementById('sideDefendBtn');

function updateSideButtons() {
    sideAttackBtn.classList.toggle('active', selectedSide === '攻');
    sideDefendBtn.classList.toggle('active', selectedSide === '守');
}

sideAttackBtn.addEventListener('click', () => {
    selectedSide = '攻';
    updateSideButtons();
});

sideDefendBtn.addEventListener('click', () => {
    selectedSide = '守';
    updateSideButtons();
});

updateSideButtons();

// ---------- 小队按钮 ----------
const teamSelector = document.getElementById('teamSelector');
const teamToggleBtn = document.getElementById('teamToggleBtn');

function getTeamStats(letter) {
    return teamStats[letter] || { attack: 0, defend: 0 };
}

function getTeamText(letter) {
    const s = getTeamStats(letter);
    if (s.attack > 0 && s.defend > 0) return `${letter}(攻${s.attack}守${s.defend}人)`;
    if (s.attack > 0) return `${letter}(攻${s.attack}人)`;
    if (s.defend > 0) return `${letter}(守${s.defend}人)`;
    return letter;
}

function getTeamClass(letter) {
    const s = getTeamStats(letter);
    if (s.attack > 0 && s.defend > 0) return 'both';
    if (s.attack > 0) return 'attack';
    if (s.defend > 0) return 'defend';
    return 'empty';
}

function renderTeamButtons() {
    const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');
    let visibleCount;

    if (teamExpanded) {
        visibleCount = letters.length;
    } else {
        const width = teamSelector.clientWidth ||
                      (teamSelector.parentElement ? teamSelector.parentElement.clientWidth : 0) ||
                      (window.innerWidth - 40);
        visibleCount = Math.max(1, Math.min(letters.length, Math.floor(width / 102)));
    }

    teamSelector.innerHTML = '';
    const visibleLetters = letters.slice(0, visibleCount);

    visibleLetters.forEach(letter => {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'team-btn ' + getTeamClass(letter) + (letter === selectedTeam ? ' selected' : '');
        btn.textContent = getTeamText(letter);
        btn.addEventListener('click', () => {
            selectedTeam = letter;
            renderTeamButtons();
        });
        teamSelector.appendChild(btn);
    });

    teamToggleBtn.textContent = teamExpanded ? '收纳' : '展开';
}

teamToggleBtn.addEventListener('click', () => {
    teamExpanded = !teamExpanded;
    renderTeamButtons();
});

window.addEventListener('resize', () => {
    if (!teamExpanded) renderTeamButtons();
});

renderTeamButtons();

// ---------- 在线补全 ----------
const playerInput = document.getElementById('playerInput');
const suggestionsDiv = document.getElementById('suggestions');

function renderSuggestions(matches) {
    suggestionsDiv.innerHTML = '';
    if (!matches || matches.length === 0) {
        suggestionsDiv.style.display = 'none';
        return;
    }
    matches.forEach(name => {
        const div = document.createElement('div');
        div.className = 'suggestion-item';
        div.textContent = name;
        div.addEventListener('click', () => {
            playerInput.value = name;
            suggestionsDiv.style.display = 'none';
        });
        suggestionsDiv.appendChild(div);
    });
    suggestionsDiv.style.display = 'block';
}

playerInput.addEventListener('focus', () => {
    if (window.onlineNames && window.onlineNames.length > 0) {
        renderSuggestions(window.onlineNames.filter(n => !assignedNormSet.has(normName(n))));
    }
});

playerInput.addEventListener('input', () => {
    const valNorm = normName(playerInput.value.trim());
    const online = window.onlineNames || [];
    const filtered = valNorm
        ? online.filter(n => normName(n).includes(valNorm) && !assignedNormSet.has(normName(n)))
        : [];
    renderSuggestions(filtered);
});

document.addEventListener('click', (e) => {
    if (!e.target.closest('.autocomplete-wrapper')) suggestionsDiv.style.display = 'none';
});

// ---------- 分配按钮 ----------
document.getElementById('assignBtn').addEventListener('click', () => {
    const name = playerInput.value.trim();
    if (!name) { alert('请输入玩家名'); return; }
    fetch('', {
        method: 'POST',
        headers: {'Content-Type': 'application/x-www-form-urlencoded'},
        body: `action=assign&player=${encodeURIComponent(name)}&side=${encodeURIComponent(selectedSide)}&team=${encodeURIComponent(selectedTeam)}`
    }).then(r => r.text()).then(() => location.reload());
});

// ---------- 安检实时列表 ----------
const checkpointList = document.getElementById('checkpointList');
const CHECK_X_MIN = 15313, CHECK_X_MAX = 15324;
const CHECK_Z_MIN = 40231, CHECK_Z_MAX = 40246;

function fetchCheckpointPlayers() {
    fetch(proxyUrl)
        .then(response => response.json())
        .then(data => {
            const players = data.players || [];
            window.onlineNames = players.map(p => p.name);

            const unassigned = players.filter(p => {
                if (assignedNormSet.has(normName(p.name))) return false;
                const x = p.position.x, z = p.position.z;
                return x >= CHECK_X_MIN && x <= CHECK_X_MAX && z >= CHECK_Z_MIN && z <= CHECK_Z_MAX;
            });

            updateCheckpointList(unassigned);
        })
        .catch(err => {
            checkpointList.innerHTML = '<span class="checkpoint-empty">加载失败</span>';
        });
}

function updateCheckpointList(players) {
    checkpointList.innerHTML = '';
    if (players.length === 0) {
        checkpointList.innerHTML = '<span class="checkpoint-empty">暂无未分配玩家</span>';
        return;
    }
    players.forEach(p => {
        const div = document.createElement('div');
        div.className = 'checkpoint-player';
        div.textContent = p.name;
        div.addEventListener('click', () => {
            playerInput.value = p.name;
            selectedSide = '攻';
            updateSideButtons();
            selectedTeam = 'A';
            renderTeamButtons();
        });
        checkpointList.appendChild(div);
    });
}

fetchCheckpointPlayers();
setInterval(fetchCheckpointPlayers, 1000);

// ---------- 踢出行内滑块 ----------
function toggleKickSlider(btn) {
    const row = btn.closest('.player-row');
    const sliderRow = row.nextElementSibling;
    if (!sliderRow || !sliderRow.classList.contains('slider-row')) return;
    sliderRow.style.display = 'flex';
    const range = sliderRow.querySelector('input[type=range]');
    range.value = 0;
    range.oninput = function() {
        if (this.value >= 100) {
            const playerName = row.querySelector('.player-name').textContent;
            fetch('', {
                method: 'POST',
                headers: {'Content-Type': 'application/x-www-form-urlencoded'},
                body: `action=kick&player=${encodeURIComponent(playerName)}`
            }).then(r => r.json()).then(data => {
                if (data.success) {
                    // 踢出成功后刷新页面，确保小队人数、总信息等统计更新
                    location.reload();
                } else {
                    alert('删除失败: ' + (data.error || '未知错误'));
                    sliderRow.style.display = 'none';
                }
            });
        }
    };
}

document.addEventListener('click', (e) => {
    if (!e.target.closest('.slider-row') && !e.target.closest('.kick-btn')) {
        document.querySelectorAll('.slider-row').forEach(s => s.style.display = 'none');
    }
});
</script>
</body>
</html>

<?php
function renderPlayerRow($p) {
    $name = htmlspecialchars($p['name']);
    $team = htmlspecialchars($p['team'] !== '' ? strtoupper($p['team']) : '无');
    $k = $p['kills']; $d = $p['deaths'];
    $kd = $d > 0 ? round($k/$d, 2) : ($k > 0 ? '∞' : '0');
    return <<<HTML
    <div class="player-row">
        <span class="player-name">$name</span>
        <span class="team-badge">小队 $team</span>
        <span class="stats">K{$k} D{$d} KD{$kd}</span>
        <button class="kick-btn" onclick="toggleKickSlider(this)">踢出</button>
    </div>
    <div class="slider-row">
        <span>滑动确认 →</span>
        <input type="range" min="0" max="100" value="0">
        <button onclick="this.parentElement.style.display='none'">取消</button>
    </div>
HTML;
}
?>
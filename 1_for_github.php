<?php
header('Content-Type: application/json; charset=utf-8');
header('Access-Control-Allow-Origin: *');

/**
 * 统一 JSON 输出。
 * HEX 系列选项把 < > & ' " 转成 \uXXXX，即使响应被嵌进 <script> 也无法提前闭合标签。
 */
function jsonOut($data, int $status = 200): void
{
    http_response_code($status);
    echo json_encode(
        $data,
        JSON_UNESCAPED_UNICODE | JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT
    );
    exit;
}

// 白名单：只允许读取以下文件，从源头杜绝路径穿越
$allowedFiles = ['gf_player.csv', 'gf_info.txt', 'gf_score.txt'];

$file = $_GET['file'] ?? '';
if (!is_string($file) || !in_array($file, $allowedFiles, true)) {
    jsonOut(['error' => 'Forbidden'], 403);
}

$baseDir = 'example/mcc';   // 你的 MCC 目录绝对路径
$baseReal = realpath($baseDir);
if ($baseReal === false) {
    jsonOut(['error' => 'Base directory not found'], 500);
}

// 双保险：文件名不得带目录部分（白名单已保证，防止日后放宽时被绕过）
if ($file !== basename($file)) {
    jsonOut(['error' => 'Forbidden'], 403);
}

$fullPath = $baseReal . DIRECTORY_SEPARATOR . $file;
$fullReal = realpath($fullPath);

// 前缀比较带上目录分隔符，避免 /mcc_backup 这类同前缀目录绕过检查
$basePrefix = rtrim($baseReal, DIRECTORY_SEPARATOR) . DIRECTORY_SEPARATOR;
if ($fullReal === false || strncmp($fullReal, $basePrefix, strlen($basePrefix)) !== 0) {
    jsonOut(['error' => 'Access denied'], 403);
}

if (!is_file($fullReal)) {
    jsonOut(['error' => 'File not found'], 404);
}

if ($file === 'gf_info.txt') {
    $lines = file($fullReal, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
    $data = ['attacker_forces' => 0, 'points' => []];
    foreach ($lines as $line) {
        $line = trim($line);
        if ($line === '' || $line === '攻防数据') continue;
        if (strpos($line, ',') !== false) {
            list($key, $val) = explode(',', $line, 2);
            $key = trim($key);
            $val = trim($val);
            if ($key === '攻方兵力') {
                $data['attacker_forces'] = is_numeric($val) ? intval($val) : $val;
            } elseif (strpos($val, '/') !== false) {
                // 只把进度形式的行当作据点（如 A,0/100），忽略“胜利分数”等配置行
                $parts = explode('/', $val);
                $data['points'][$key] = intval($parts[0]);
            }
        }
    }
    jsonOut($data);
} elseif ($file === 'gf_score.txt') {
    // 占领模式输出（由 occupationMode.cs 写入），格式：
    //   占领模式
    //   胜利分数,300
    //   攻方分数,120
    //   守方分数,80
    //   A,45/100
    $lines = file($fullReal, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
    $data = ['target_score' => 0, 'attacker_score' => 0, 'defender_score' => 0, 'points' => []];
    foreach ($lines as $line) {
        $line = trim($line);
        if ($line === '' || strpos($line, ',') === false) continue;
        list($key, $val) = explode(',', $line, 2);
        $key = trim($key);
        $val = trim($val);
        if ($key === '胜利分数') {
            $data['target_score'] = intval($val);
        } elseif ($key === '攻方分数') {
            $data['attacker_score'] = intval($val);
        } elseif ($key === '守方分数') {
            $data['defender_score'] = intval($val);
        } elseif (strpos($val, '/') !== false) {
            $parts = explode('/', $val);
            $data['points'][$key] = intval($parts[0]);
        }
    }
    jsonOut($data);
} else {
    // gf_player.csv 格式：姓名,击杀,死亡,小队
    $lines = file($fullReal, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
    $currentSide = '';
    $players = [];
    foreach ($lines as $line) {
        $line = trim($line);
        if ($line === '攻方') { $currentSide = '攻'; continue; }
        if ($line === '守方') { $currentSide = '守'; continue; }
        $cols = str_getcsv($line);
        if (count($cols) >= 4) {
            $kills = intval($cols[1]);
            $deaths = intval($cols[2]);
            $kd = $deaths > 0 ? $kills / $deaths : ($kills > 0 ? $kills : 0);
            $players[] = [
                'name'   => $cols[0],
                'side'   => $currentSide,
                'team'   => $cols[3],
                'kills'  => $kills,
                'deaths' => $deaths,
                'kd'     => round($kd, 2)
            ];
        }
    }
    jsonOut($players);
}

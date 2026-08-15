<?php
header('Content-Type: application/json; charset=utf-8');
header('Access-Control-Allow-Origin: *');

$allowedFiles = ['gf_player.csv', 'gf_info.txt'];
$file = $_GET['file'] ?? '';

if (!in_array($file, $allowedFiles, true)) {
    http_response_code(403);
    echo json_encode(['error' => 'Forbidden']);
    exit;
}

$baseDir = 'example/mcc';   // 你的 MCC 目录绝对路径
$fullPath = $baseDir . '/' . $file;
if (realpath($fullPath) === false || strpos(realpath($fullPath), realpath($baseDir)) !== 0) {
    http_response_code(403);
    echo json_encode(['error' => 'Access denied']);
    exit;
}
if (!file_exists($fullPath)) {
    http_response_code(404);
    echo json_encode(['error' => 'File not found']);
    exit;
}

if ($file === 'gf_info.txt') {
    $lines = file($fullPath, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
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
            } else {
                $parts = explode('/', $val);
                $data['points'][$key] = intval($parts[0]);
            }
        }
    }
    echo json_encode($data);
} else {
    // gf_player.csv 格式：姓名,击杀,死亡,小队
    $lines = file($fullPath, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
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
    echo json_encode($players);
}
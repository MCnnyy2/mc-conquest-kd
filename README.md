# MC-Conquest-KD

适用于 Minecraft 攻防 / 占领 / PVP 活动的自动据点判断与 KD 统计工具。  
支持通过 Bluemap 自动判断玩家是否位于据点内，统计击杀 / 死亡 / KD，并通过 Web 页面进行阵营分配与排行榜展示。

## 功能特性

- 自动判断玩家是否在据点内（依赖 Bluemap）
- 实时统计玩家击杀、死亡、KD
- 管理员通过 Web 页面添加玩家、分配阵营，游戏内公屏与私信通知
- 公屏列出玩家阵营并私信小队分配
- 攻防主脚本统计攻方死亡人数与据点占领情况
- 据点变动或大事件公屏播报（制导导弹、背水一战、破釜沉舟除外）
- 游戏内发送“制导导弹”可炸毁 C1 据点
- 玩家重生后自动告知在场队友，提醒可能传送支援
- 根据兵力与据点情况自动判定胜负
- 简化模式：仅统计 KD 与重生提醒

## 前置要求

- Minecraft 服务器
- [BlueMap](https://github.com/BlueMap-Minecraft/BlueMap) 插件（已正确配置，可获取玩家位置）
- [Minecraft Console Client (MCC)](https://github.com/MCCTeam/Minecraft-Console-Client)（支持 `/script` 命令）
- Web 服务器 + PHP 7.4+（用于 `team_assign.php`）
- 浏览器（查看排行榜）

## 安装与配置

1. 将本仓库克隆到 MCC 的脚本目录（或任意位置，运行时指定路径）。
2. 配置 Bluemap：
   - 安装并配置 [BlueMap](https://github.com/BlueMap-Minecraft/BlueMap) 插件，确保地图可正常访问。
   - 在脚本中配置 Bluemap 的 API 地址、地图 ID、据点坐标范围等。
3. 部署 Web 端：
   - 将 `web/` 目录部署到 Web 服务器。
   - 修改 `team_assign.php` 中的管理员密码。
   - 修改 `index.html` 中的数据接口地址（指向同一服务器上的数据文件或 API）。
   - 确保 `team_assign.php` 对数据文件 / 数据库有写权限。
4. 编辑脚本配置：
   - 打开各 `.cs` 脚本，按注释修改据点坐标、兵力值、Web URL、队伍名称等。
   - 确认 MCC 使用的账号拥有管理员 / 必要权限。
5. 启动 [Minecraft Console Client (MCC)](https://github.com/MCCTeam/Minecraft-Console-Client) 并连接到服务器。

## 使用方法

### 完整攻防模式

1. 启动 MCC，在控制台输入：
```

/script ./count_kd.cs

```
   该脚本开始统计 KD 和重生提醒。

2. 管理员打开 `http://你的域名/team_assign.php`，输入密码进入管理页面，添加玩家并分配阵营。  
   脚本会向公屏和私信发送分配情况。

3. 分配完成后，管理员在 MCC 中运行：

```

/script ./list_of_players.cs

```

   在公屏列出玩家阵营并私信小队分配。

4. 管理员运行攻防主脚本：

```

/script ./assaultDefense1.cs

```
   **注意：请提前设置好兵力，兵力为 0 时脚本会自动退出。**  
   脚本将开始统计攻方死亡人数和据点占领情况。只有据点变动或大事件（制导导弹、背水一战、破釜沉舟除外）才会公屏播报据点进度。

5. 玩家可以打开 `http://你的域名/index.html` 查看据点占领进度和 KD 排行榜。

6. 游戏内特殊事件：
   - 管理员在公屏发送“制导导弹”可炸毁 C1 据点。
   - 其他事件按脚本内注释触发。

7. 玩家重生后，脚本会告知在场队友，并提醒可能传送过去支援。

8. 当兵力或据点条件满足时，脚本自动判定胜负并结束。

### 简化模式（仅统计 KD 和重生提醒）

如果不需要据点占领和自动判负，只需要运行：
```

/script ./count_kd.cs

```

即可。

## 配置说明
* 需要在`.php`、`.cs`更改目标文件地址，在`index.html`更改`1.php`的访问地址
* 需要在`assaultDefense1.cs`更改据点坐标范围、添加Bluemap获取玩家坐标的api端口
* 需要在`team_assign.php`添加Bluemap获取玩家坐标的api端口、场务密码
* 需要在`gf_info.txt`更改初始兵力（也是最大兵力值）
* ...
  
## 相关项目

- [BlueMap](https://github.com/BlueMap-Minecraft/BlueMap) - Minecraft 网页地图插件，用于获取玩家位置和据点判断
- [Minecraft Console Client (MCC)](https://github.com/MCCTeam/Minecraft-Console-Client) - 轻量级 Minecraft 控制台客户端，用于运行自动化脚本

## 注意事项

- 使用前请确认 Bluemap 已正确安装且 API 可用，否则据点判断无效。
- team_assign.php无法保证多人共用正常工作
- 请勿在未经服务器允许的情况下使用自动化脚本。
- 脚本中的坐标和兵力值需根据实际地图调整。
- 若只需 KD 统计，请勿运行 `assaultDefense1.cs`，避免误触发攻防逻辑。

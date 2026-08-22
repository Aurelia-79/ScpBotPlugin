# ScpBotPlugin — SCP:SL 服务端机器人插件

[![C#](https://img.shields.io/badge/C%23-12.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Framework](https://img.shields.io/badge/.NET-Framework%204.8-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![LabAPI](https://img.shields.io/badge/API-LabAPI-ff6a00)](https://github.com/northwood-studios/LabAPI)
[![Python](https://img.shields.io/badge/Python-3.8%2B-3776AB?logo=python&logoColor=white)](https://www.python.org/)
[![License](https://img.shields.io/badge/License-MIT-4B0082)](LICENSE)

基于官方 **LabAPI** 框架开发的 SCP: Secret Laboratory 服务端机器人插件。使用游戏内置的 **Dummy** 机制生成机器人，实现自动索敌、寻路、战斗、投掷、自疗等接近真人玩家的行为；可选接入独立的 **Python AI 服务器**做多核并行决策，并内置 **DQN 神经网络**持续在线学习战斗策略。

> ⚠️ 本插件仅供个人服务器娱乐/测试使用。请遵守游戏服务条款，勿用于作弊或影响他人正常游戏体验。

---

## ✨ 功能特性

### 机器人行为
- **拟人索敌**：只追击「看得见」的敌人（本地视线检测），不可见时靠记忆搜索并超时遗忘，消除隔墙透视
- **多级寻路**：地表 NavMesh（运行时烘焙）→ 房间图 BFS → 房间内航点 → 大房间地标点 → 直线追击，逐级兜底
- **真人式战斗**：追击/绕圈状态机、猛冲模式、横移晃动、近距后撤、瞄准散布（模拟枪法误差）
- **完整武器逻辑**：弹匣换弹（按键两阶段序列）、备用弹药自动补给、无限弹药开关
- **投掷物**：手榴弹/闪光弹自动投掷（敌人聚集判定 + 冷却），与游戏 `ThrowableItem` 服务端状态机精确对齐
- **自动自疗**：血量告急时背包用药 → 房间内拾取医疗品 → 放弃冷却
- **智能换路**：多路线寻路按 bot 编号分散、跨队夹击反向分配、路线阵亡超阈值自动切换备选路线
- **卡死脱离**：跳跃 → 光线扫描 → 瞬移兜底三级脱离；卡房无交战超时自动重生全阵营 + 神经网络惩罚
- **位置漂移防护**：电梯/传送带等移动 Waypoint 绕过、异常跳变检测与瞬移修正

### 管理与扩展
- **10 组管理命令**（RA 与服务器控制台双端可用，详见[命令](#-命令)）
- **示教学习**：管理员带领 bot 走正确路线，轨迹自动提交给神经网络模仿学习
- **外部 AI 服务器**：独立的 Python 多线程决策服务，失联自动降级为本地 AI
- **配置热重载**：`labapi reload configs` 即可生效；航点文件 `waypoints.yml` 支持运行中自动热重载

---

## 🏗️ 架构

```
┌─────────────────────────── SL 服务器（C# / LabAPI 插件）──────────────────────────┐
│                                                                                   │
│  BotPlugin ── 生命周期 / 事件订阅 / 配置加载                                        │
│    └─ BotManager ── 集中管理（生成/销毁/统计）+ MEC 主循环（10Hz）                  │
│          ├─ Bot ── 单个机器人 AI（索敌/寻路/战斗/投掷/自疗）                        │
│          │     └─ RoomNavigator / RoomWaypoints / SurfaceNavMeshService ── 寻路    │
│          └─ ExternalAIBridge ── TCP 桥（后台线程收发 JSON 行）                      │
│                                                                                   │
└─────────────────────────────────┬─────────────────────────────────────────────────┘
                                  │ TCP（每行一个 JSON）
┌─────────────────────────────────▼─────────────────────────────────────────────────┐
│  ai_server.py（Python 3.8+，可选）                                                │
│    ├─ ThreadPoolExecutor 多线程并行决策（索敌/走位/开火/巡逻/压制战术）             │
│    └─ brain_route.py ── numpy 手写 DQN（17 维状态 × 16 维动作，持续在线学习）      │
│         └─ brain_route.npz（权重持久化，运行时生成，不入库）                       │
└───────────────────────────────────────────────────────────────────────────────────┘
```

**职责划分**：感知（视线检测）与执行（移动/开火）永远在插件主线程完成；纯计算决策（走位方向、开火时机）可在外部 AI 多核并行。

---

## 📦 依赖

| 依赖 | 用途 | 来源 |
|---|---|---|
| **LabAPI**（`LabApi.dll`） | 插件框架 | SL 服务端 `SCPSL_Data/Managed` 自带 |
| **.NET Framework 4.8** | 运行/编译目标框架 | Windows 自带 / .NET SDK |
| **Python 3.8+**（可选） | 外部 AI 决策服务器 | 独立安装 |
| **numpy**（可选） | DQN 神经网络学习 | `pip install numpy` |

> 本插件**不依赖** HintServiceMeow / AudioApi 等第三方库，仅使用游戏服务端自带的程序集。

---

## 🔧 编译

### 前置条件

1. 安装 [.NET SDK](https://dotnet.microsoft.com/download)（支持 .NET Framework 4.8 目标）
2. 拥有 **SCP:SL 专用服务器本体**（本地路径需包含 `SCPSL_Data\Managed` 程序集目录）
   - Steam 默认位置：
     `C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed`

### 编译命令

```bash
dotnet build ScpBotPlugin.csproj -c Release
```

如果游戏服务器不在默认路径，用 `-p:SL_REFERENCES=` 覆盖程序集目录：

```bash
dotnet build ScpBotPlugin.csproj -c Release -p:SL_REFERENCES="D:\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed"
```

> 项目文件（`ScpBotPlugin.csproj`）中已通过 `SL_REFERENCES` 属性集中管理引用路径，无需手动修改 HintPath。

### 引用的游戏程序集（9 个，均来自 `SCPSL_Data\Managed`）

| 程序集 | 用途 |
|---|---|
| `Assembly-CSharp.dll` | 游戏主程序集（角色/物品/门/房间等） |
| `Assembly-CSharp-firstpass.dll` | 游戏基础程序集 |
| `CommandSystem.Core.dll` | 命令系统 |
| `Mirror.dll` | 网络同步（Dummy 生成/销毁） |
| `LabApi.dll` | LabAPI 框架 |
| `NorthwoodLib.dll` | 北木工具库 |
| `Pooling.dll` | 对象池 |
| `UnityEngine.CoreModule.dll` | Unity 核心 |
| `UnityEngine.PhysicsModule.dll` | 物理（视线检测/射线） |
| `UnityEngine.AIModule.dll` | NavMesh 烘焙与寻路 |

---

## 🚀 部署

1. **编译**插件得到 `ScpBotPlugin.dll`
2. 放入服务端 **`Plugins/`** 目录（LabAPI 会自动加载；`LabApi.dll` 由服务端提供，无需复制）
3. 启动服务端，插件首次运行会自动生成配置文件 `scpbot.yml`
4. （可选）启动外部 AI 服务器：
   ```bash
   pip install numpy        # 神经网络学习需要；不装也能跑（自动禁用学习）
   python ai_server/ai_server.py 9000
   ```
5. 在服务器内执行 `bot spawn 10` 生成机器人，或配置 `AutoSpawnOnRoundStart` 自动生成

---

## ⚙️ 配置（`scpbot.yml`）

> 修改后执行 `labapi reload configs` 热重载（航点文件除外，见下）。

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `BotNamePrefix` | `Bot` | 机器人昵称前缀（实际为 `"{前缀} {id}"`） |
| `BotRole` | `NtfCaptain` | 默认生成角色 |
| `PrimaryWeapon` | `GunE11SR` | 默认主武器 |
| `GiveArmor` / `GiveMedkit` | `true` | 是否配发护甲/医疗包 |
| `ReserveAmmo` | `200` | 备用弹药数 |
| `InfiniteAmmo` | `false` | 无限弹药（跳过换弹） |
| `TickInterval` | `0.1` | AI 决策间隔（秒） |
| `MoveSpeed` | `14` | 移动速度（米/秒） |
| `AttackRange` | `40` | 开火距离（米） |
| `PreferredEngageDistance` | `10` | 理想交战距离（米） |
| `AggressiveCharge` | `true` | 猛冲模式（忽略绕圈直接冲锋） |
| `MaxVisionDistance` | `60` | 索敌视野距离（米） |
| `AimHeight` / `AimSpread` | `0.35` / `0.5` | 瞄准高度 / 散布半径 |
| `HealThreshold` / `HealCooldown` | `0.35` / `8` | 自疗触发血比 / 失败冷却 |
| `ThrowMinEnemies` / `ThrowCooldown` | `2` / `12` | 投掷聚集人数 / 冷却 |
| `StuckJumpAfter` / `StuckRaycastAfter` | `0.8` / `2` | 卡死脱离阶段阈值 |
| `IdleStuckTimeout` | `90` | 卡房无交战超时（0 或负值禁用） |
| `RoomGraph` | `{}` | 自定义房间图（房间名 → 邻居列表，自动补全反向边） |
| `SpawnPosition` / `NtfSpawnPosition` / `CiSpawnPosition` | 空 | 阵营出生点（`"x y z"`） |
| `BakeQuality` | `High` | NavMesh 烘焙质量（`High` / `Ultra`） |
| `AutoSpawnOnRoundStart` / `AutoSpawnCount` | `false` / `0` | 回合开始自动生成 |
| `ExternalAI.Enabled` | `false` | 启用外部 AI 服务器 |
| `ExternalAI.Host` / `Port` | `127.0.0.1` / `9000` | AI 服务器地址 |
| `ExternalAI.SendInterval` | `0.1` | 快照发送间隔（秒） |
| `ExternalAI.TimeoutSeconds` | `2` | 失联判定超时（秒） |
| `ExternalAI.IdleWhenNoOrders` | `true` | 无指令时保持待命 |

### 航点文件（`waypoints.yml`）

独立于主配置，用于配置房间内路线与大地图地标点，支持**运行中直接编辑热重载**（约 1 秒内自动生效）：

```yaml
RoomWaypoints:
  Lcz173:
    - ["x y z", "x y z", "x y z"]   # 一条路线（房间内绕障路径点）
    - ["x y z", "x y z"]            # 第二条路线（进房随机选一条）
RoomTargets:
  Outside:
    - "x y z"                       # 大房间推荐地标
```

---

## 🎮 命令

所有命令在 **RA（远程管理）** 与 **服务器控制台** 均可使用，需要 `Facility Management` 权限。父命令：`bot`（别名 `scpbot`）。

| 命令 | 说明 |
|---|---|
| `bot spawn [数量] [角色]` | 生成机器人（默认 1，上限 64，可指定角色如 `NtfCaptain` / `ChaosRifleman` / `Scp173`） |
| `bot spawnpos ntf\|ci <x y z>` | 设置 NTF/CI 阵营出生点（设施内任意位置） |
| `bot spawnpos show` / `clear <ntf\|ci>` | 查看 / 清除出生点 |
| `bot follow <玩家> [all\|id]` | 示教：让机器人跟随玩家并记录房间轨迹 |
| `bot follow stop [all\|id]` | 停止跟随并提交轨迹给神经网络学习 |
| `bot follow list` | 查看当前跟随中的机器人 |
| `bot kill all` / `bot kill <id>` | 销毁全部 / 单个机器人 |
| `bot list` | 列出所有存活机器人 |
| `bot status <id>` | 查看机器人详细实时状态（诊断用） |
| `bot room [id]` | 查看机器人所在房间 |
| `bot path <id>` | 查看机器人寻路路径 |
| `bot respawn on\|off\|status` | 切换死亡自动复活 |
| `bot wp new [房间]` | 开始录入新路线 |
| `bot wp add [房间]` | 把当前位置录入为航点 |
| `bot wp list [房间]` | 列出已配置航点 |
| `bot wp clear <房间> [编号]` | 删除某条路线（不填编号=清空全部） |
| `bot wp export` | 把内存航点写入 `waypoints.yml` 并生效 |

---

## 🧠 外部 AI 服务器（`ai_server/`）

独立于游戏的 Python 决策服务，为每个机器人做完整的索敌/走位/开火/巡逻决策，多线程并行，并把指令回传给插件执行。

### 启动

```bash
python ai_server.py [端口]        # 默认 9000，默认只监听 127.0.0.1
```

### 环境变量

| 变量 | 说明 |
|---|---|
| `SCPBOT_HOST` | 监听地址（默认 `127.0.0.1`；跨机器部署时显式设置并自行承担暴露风险） |
| `SCPBOT_NO_BRAIN=1` | 关闭神经网络学习（纯规则决策） |
| `SCPBOT_VERBOSE=1` | 打印每个 bot 的决策明细 |

### 神经网络学习（`brain_route.py`）

- numpy 手写 **DQN**：17 维状态（血量/距离/敌人数/掩体等）× 16 维动作（8 走位 × 2 开火）
- **持续在线学习**：击杀 +1、阵亡 -3、血量变化、靠近目标等奖励信号来自插件统计
- 权重持久化到 `brain_route.npz`（运行时生成，**不入库**）；`SCPBOT_NO_BRAIN=1` 可关闭
- 经验回放（容量 1000）+ 目标网络 + ε-贪心（0.3 指数衰减至 0.1）

> 没有 numpy 时自动禁用学习并降级为规则决策，不影响插件运行。

---

## ❓ 常见问题

**Q: 编译报「未能找到类型或命名空间」？**
A: 游戏程序集引用路径不对。用 `-p:SL_REFERENCES="你的服务端路径\SCPSL_Data\Managed"` 覆盖。

**Q: 机器人站着不动 / 不攻击？**
A: 依次检查：`scpbot.yml` 配置（`TickInterval`、`AttackRange`）、是否有敌对目标可见（拟人索敌只追可见敌人）、`bot status <id>` 诊断当前状态。

**Q: 外部 AI 没有生效？**
A: 确认 `ExternalAI.Enabled = true`、Python 进程已启动、端口一致；插件日志会打印连接状态，失联自动降级本地 AI。

**Q: 机器人传送乱飞 / 位置异常？**
A: 这是位置漂移防护在修正移动 Waypoint（电梯/传送带）引用问题，日志会记录；若频繁出现请检查出生点是否设在非法位置。

**Q: 航点改了不生效？**
A: `waypoints.yml` 支持热重载（约 1 秒）；`bot wp export` 会立即写入并生效。

---

## 📄 许可证

本项目使用 **MIT License**（详见 [LICENSE](LICENSE)）。

简单来说：你可以自由使用、修改、分发、商用本代码，但需保留版权声明；作者不对代码提供任何担保。使用本插件请同时遵守游戏官方服务条款。

---

*由 [LabAPI](https://github.com/northwood-studios/LabAPI) 驱动 · 仅限个人服务器娱乐使用*

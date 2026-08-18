# ScpBotPlugin Bug 总清单

> 来源：三轮对抗性代码审查（16 个审查/验证代理 + 33 项 Python 实测 + 游戏 DLL 反编译取证）
> 最终定级：**critical 4 / major 31 / minor 44 / nit 16 = 95 条**
> 状态：`待修复`（未开始）| `修复中` | `已修复`（commit hash）| `拒绝/降级`（裁决理由）
> 完整审查报告见 `C:\Users\Aurelia\source\repos\AI\review_tmp\`（r1_*/r2_*/r3_final.md）

---

## ⚠️ Critical（4 条）—— 崩溃 / 挂起 / 数据损坏

| id | 文件 | 问题 | 状态 |
|---|---|---|---|
| FF-01 | ai_server/brain_route.py L153/L199-209 | train_step 每 300 步自锁死锁（非可重入 Lock + save 二次加锁），AI 服务器整体挂死 | 待修复 |
| FF-02 | ai_server/brain_route.py L136-203/230-237 | NaN/Inf 零防护：一次 NaN 奖励永久损坏权重并写盘，重载后 predict 全 NaN | 待修复 |
| FF-03 | ExternalAI/ExternalAIBridge.cs L125-135 | 锁内无超时 Write ↔ 主线程同锁 Enqueue 互锁，整服冻结 | 待修复 |
| FF-04 | ai_server/ai_server.py L1248 | readline 默认 64KB 行上限，大快照必杀连接，AI 决策系统性失效 | 待修复 |

## 🔥 Major（31 条）—— 明显逻辑错误导致功能失效

| id | 文件 | 问题 | 状态 |
|---|---|---|---|
| FF-05 | BotManager.cs L319/324/574 | Bots 字典键（自增计数器）与 PlayerId（槽位池）两套编号错配，击杀/阵亡统计与神经网络奖励恒 0 | 待修复 |
| FF-06 | BotManager.cs L244-281 | 卡房超时重生用 config.BotRole 替换整队，CI/SCP 阵营反转同队互打 | 待修复 |
| FF-07 | Bot.cs L2757-2796 | 换弹机制与游戏输入语义不符（releaseAction 恒 null、无弹匣状态验证），无弹匣类武器永久哑火 | 待修复 |
| FF-08 | Bot.cs L3018-3049 | 瞬移兜底落点无地面/几何校验，反复瞬移循环/虚空摔死 | 待修复 |
| FF-09 | 多处（Bot.cs/RoomWaypoints.cs/JsonMini.cs/ai_server.py/brain_route.py） | NaN/±Infinity 坐标从解析到执行全链路无校验 | 待修复 |
| FF-10 | Bot.cs L2290-2384 | 投掷确认 0.7s 固定等待可能早于 ReadyToThrow 门槛 + `??` 优先级冷却绕过 + 无条件置 pending | 待修复 |
| FF-11 | BotManager.cs L413 + Bot.cs L1094-1101 | IdleStuckTimeout=0/负值（文档称禁用）反而每 tick 全阵营重生风暴 | 待修复 |
| FF-12 | BotPlugin.cs L80-94 / BotManager.cs L361-365 | 外部 AI 未连接时 Disable/重载主线程 Join 阻塞最多 1.5s + 僵尸线程残留 | 待修复 |
| FF-13 | Bot.cs L2018 | Move 步长用 Time.deltaTime 而非 TickInterval，bot 实际速度 ≈ 配置值 1/6 | 待修复 |
| FF-14 | SurfaceNavMesh.cs L196-241 | 「回主线程」依赖未安装的 SynchronizationContext（潜在，needs_code_change） | 待修复 |
| FF-15 | SurfaceNavMesh.cs L70-74/163-164/200-209 | 烘焙在途回合重置 → 整回合无 NavMesh（作废后无人重试） | 待修复 |
| FF-16 | SurfaceNavMesh.cs L272/292 | TryFindPath 渐进采样 sampleDistance<=0 时主线程死循环（当前不可达） | 待修复 |
| FF-17 | WaypointStore.cs L118-150 | waypoints.yml 热重载「先记时间后读取」+ null config 毒化，永久停更 | 待修复 |
| FF-18 | ExternalAIBridge.cs L171-181 | 断线后 _lineAccumulator/_sendBuffer/_incoming 未清空，旧半行拼接错误指令 | 待修复 |
| FF-19 | ai_server.py L1188-1190 + ExternalAIBridge.cs L298-304 + JsonMini.cs L44-51 | 投掷指令 tx/ty/tz 协议不匹配（Python 标量 vs C# 数组解析），投掷瞄准恒失效 | 待修复 |
| FF-20 | Commands/BotWaypointCommand.cs L229-245 | `bot wp clear <房间> <非数字>` 静默清空该房间全部路线（数据丢失） | 待修复 |
| FF-21 | Bot.cs L401-405 + BotManager.cs L391/399-407 | TryInitLoadout 10 连败 Dispose 后不移除 Bots，外部 AI 模式每 tick 快照失败 | 待修复 |
| FF-22 | ai_server/ai_server.py L558/L341-345/L411-412 | 神经网络仅 1/5 tick 真正接管战斗（样本节流字段被误用作接管开关） | 待修复 |
| FF-23 | ai_server/ai_server.py L1002-1016/918/1082-1104 | 手雷指令队列永不消费，投掷轰炸功能整体死链 | 待修复 |
| FF-24 | ai_server/ai_server.py L1281-1284/905/217-222/322-326 | 多线程无锁读写共享 tactics/states/nn_stats（违反线程安全硬约束） | 待修复 |
| FF-25 | ai_server/ai_server.py L1068-1079 | 压制战术无条件返回 "suppress"，全图 bot 被局部掩体记忆绑架 | 待修复 |
| FF-26 | ai_server/ai_server.py L383-385 | episode 判定用完整敌人列表，玩家死亡/重生即重置，DQN 退化为单步 TD | 待修复 |
| FF-27 | ai_server/ai_server.py L839-852 | waypoint_step 单点路线 4 tick 内 IndexError 杀连接 | 待修复 |
| FF-28 | ai_server/ai_server.py L90-91/1281-1284/1305-1306 | 单个 bot 快照脏数据（缺字段）杀死整个连接 | 待修复 |
| FF-29 | ai_server/ai_server.py L240-305/457-495 | 示教学习（taught_routes/build_nav_candidates）死代码 + 无界增长 | 待修复 |
| FF-30 | ai_server/brain_route.py L250/254-257/262-275 | _load shape guard 只覆盖 w1，其余矩阵/回放列数不校验，加载后运行期崩溃 | 待修复 |
| FF-31 | ai_server/ai_server.py L1320/1267-1269/504 | 无鉴权 TCP 客户端可对共享神经网络 NaN 投毒（越权数据损坏入口） | 待修复 |

## 🟡 Minor（44 条）—— 边界情况下的错误

| id | 文件 | 问题 | 状态 |
|---|---|---|---|
| FF-32 | Bot.cs L577-613/2448-2463 | Tick 内访问已销毁 Unity 对象无局部保护，异常作废整 tick | 待修复 |
| FF-33 | Bot.cs L2989 | 卡死阶段 2 角度选择基准错误（与全局 +Z 比而非当前朝向） | 待修复 |
| FF-34 | Bot.cs L439-468 | 死亡复活后投掷/开门/配装状态残留 | 待修复 |
| FF-35 | Bot.cs L2853-2897 | 电梯/传送带高速位移误判位置漂移（plausible） | 待修复 |
| FF-36 | Bot.cs L2907-2928 | 开门等门板 0.8s 触发卡死跳跃（StuckJumpAfter 恰好 0.8s） | 待修复 |
| FF-37 | ExternalAIBridge.cs | 外部 AI Stop→Start 快速切换旧线程残留（僵尸线程最长 ~20s） | 待修复 |
| FF-38 | ExternalAIBridge.cs L146-161 | TCP 行缓冲无上限 + UTF-8 按块解码 | 待修复 |
| FF-39 | Bot.cs L1245-1258 | 外部 AI 模式无指令 bot 永不初始化配装 | 待修复 |
| FF-40 | BotManager.cs L410-433 | HandleIdleStuckTimeout 销毁 bot 后同迭代继续访问已销毁对象 | 待修复 |
| FF-41 | BotManager.cs L315-334 | Dying 事件未检查 ev.IsAllowed | 待修复 |
| FF-42 | BotManager.cs | Disable 不清空 Pending 队列，残留命令下次 Enable 执行 | 待修复 |
| FF-43 | BotManager.cs L67-112 | RouteCasualties 静态字典跨重载/回合残留 + timeSinceLevelLoad 基准 | 待修复 |
| FF-44 | BotManager.cs L543 | spawn 数量 Mathf.Clamp 使 0/负数也生成 1 个 | 待修复 |
| FF-45 | BotPlugin.cs L50-77 | Enable() 整体无异常保护 → 订阅泄漏 + 二次 Enable 重复订阅 | 待修复 |
| FF-46 | BotConfig.cs + BotManager.cs L461/496 | 配置零校验：TickInterval=0/NaN 忙循环、SendInterval=NaN 刷爆 TCP | 待修复 |
| FF-47 | Bot.cs L941-945 | ExecuteOrders 缺 authManager.UserId 就绪检查（与 Tick 不一致） | 待修复 |
| FF-48 | BotManager.cs L424 | Respawn 返回值被忽略 → 复活失败 bot 每 tick 无限重试 + 日志风暴 | 待修复 |
| FF-49 | SurfaceNavMesh.cs L383-407 | IsBakedZone 只控制锚点，实际烘焙覆盖由 120m/90m 扫描决定（注释失实） | 待修复 |
| FF-50 | SurfaceNavMesh.cs L122-138 | 忽略整个 Door 根节点：门框/墙段若属该根则门侧墙体被挖洞（plausible） | 待修复 |
| FF-51 | SurfaceNavMesh.cs L205-209 | generation check-then-act 窗口：过期 NavMesh 可能被注册（plausible） | 待修复 |
| FF-52 | WaypointStore.cs L62-65 | 旧配置迁移只在 waypoints.yml 不存在时执行 | 待修复 |
| FF-53 | WaypointStore.cs L67/101-103 | Load 回写破坏注释；Save→Apply 清空正在录入的路线状态 | 待修复 |
| FF-54 | RoomWaypoints.cs L167-175 | AddPoint 跨房间自动新建路线 → 碎路线爆炸 | 待修复 |
| FF-55 | RoomNavigator.cs L142-184 | FindPaths 注释「按长度升序」不实；maxPaths 无上限主线程多轮 BFS | 待修复 |
| FF-56 | RoomNavigator.cs L36-61 | 自定义房间图不对称：X→Y 可达而 Y→X 不可达 | 待修复 |
| FF-57 | Commands/BotWaypointCommand.cs L115-125 | wp add 在无活动路线时报告的编号与实际不符 | 待修复 |
| FF-58 | Commands/BotFollowCommand.cs L55-61 | follow stop 非数字停止全部 bot / id 不存在谎报成功 | 待修复 |
| FF-59 | Commands/BotSpawnCommand.cs L36-48 | spawn 角色参数接受数字/负数/越界值，非法角色静默销毁 | 待修复 |
| FF-60 | Commands/BotSpawnPositionCommand.cs L83-89 | spawnpos 坐标解析依赖进程区域设置（de-DE 下 3.5→35） | 待修复 |
| FF-61 | Commands/BotSpawnPositionCommand.cs | bot spawnpos 保存结果未检查，SaveConfig 失败仍报成功 | 待修复 |
| FF-62 | SurfaceNavMesh.cs L392-452 | 主线程烘焙准备成本：全场景 Collider 遍历 + 逐 collider 玩家扫描 | 待修复 |
| FF-63 | Commands/BotFollowCommand.cs L110-118 | follow FindPlayer 子串匹配歧义，命中多个玩家取首个 | 待修复 |
| FF-64 | WaypointStore.cs（ConfigurationLoader L43） | Save 非原子覆盖写：崩溃/断电留截断文件 | 待修复 |
| FF-65 | ExternalAI/ExternalAiProtocol.cs L29-43 | BuildConfigJson 无中心房间输出 c:[0,0,0] → bot 派往世界原点 | 待修复 |
| FF-66 | ai_server/ai_server.py L377-394 | learn_settle_reward UnboundLocalError + 缺 d 默认 0.0 恒判靠近 | 待修复 |
| FF-67 | ai_server/ai_server.py L318 | 探索统计错标（needs_code_change） | 待修复 |
| FF-68 | ai_server/ai_server.py L1260 | 顶层 JSON 合法但非对象时 AttributeError 杀连接 | 待修复 |
| FF-69 | ai_server/ai_server.py L165-171 | world.states/patrol_warned/taught_routes 无界增长（内存泄漏） | 待修复 |
| FF-70 | ai_server/ai_server.py L1131-1138/1088-1095 | 敢死侦查与总攻「到达即站桩」，侦查结果从不回传 | 待修复 |
| FF-71 | ai_server/ai_server.py L1164-1175 | decide_suppress 常数不一致（10m 后退 vs 12m 圈、2D/3D 距离混用） | 待修复 |
| FF-72 | ai_server/ai_server.py L516-520 | handle_penalty 原地改写已训练样本 + total_reward 双算 + 时序错位 | 待修复 |
| FF-73 | ai_server/ai_server.py L1234-1236 | 慢客户端使 send/drain 挂起无超时 | 待修复 |
| FF-74 | ai_server/ai_server.py L1006 | queue_grenades(world, t["cover_pos"]) 无 None 防护 | 待修复 |
| FF-75 | ai_server/ai_server.py L340-346 | 样本节流导致奖励延迟归因（因果错位 3~4 tick） | 待修复 |

## ⚪ Nit（16 条）—— 可维护性

| id | 文件 | 问题 | 状态 |
|---|---|---|---|
| FF-76 | Bot.cs L79/144 | 死字段：_routeAssignTick 只写不读、_routeCasualties 从未使用 | 待修复 |
| FF-77 | BotManager.cs L513 | 每 tick 主线程 waypoints.yml 文件 stat（10Hz × 2 次） | 待修复 |
| FF-78 | BotManager.cs 多处 | 直接调用 Unity API（Mathf/Time/NetworkServer.Destroy）违反自定规范 | 待修复 |
| FF-79 | RoomNavigator.cs L69-91/238-247 | GetNeighbors 返回内部 HashSet 活引用；GetAllKnownRooms 不全 | 待修复 |
| FF-80 | RoomNavigator.cs L76-88 | NativeNeighborCache 跨回合/跨地图不失效 | 待修复 |
| FF-81 | WaypointStore.cs L118-150 | 每 tick 多次文件系统元数据调用 | 待修复 |
| FF-82 | WaypointStore.cs L38-63 | yml 重复房间键 YamlDotNet 静默 last-wins | 待修复 |
| FF-83 | ExternalAIBridge.cs | IsActive 用 Environment.TickCount 32 位回绕 | 待修复 |
| FF-84 | WaypointStore.cs | CheckForReload 与 Save 并发窗口：tick 读到半写文件 | 待修复 |
| FF-85 | ai_server/ai_server.py（n1） | pick_scouts fallback 条件冗余 | 待修复 |
| FF-86 | ai_server/ai_server.py（n2） | handle_trace 对重复路线仍累加 stats | 待修复 |
| FF-87 | ai_server/ai_server.py（n3） | executor 硬编码 16、bot_positions/cover_since/peers 死字段 | 待修复 |
| FF-88 | ai_server/ai_server.py（n4） | 决策统计口径混算 | 待修复 |
| FF-89 | ai_server/brain_route.py（F12） | 文档多处与代码不符（线性/指数、4000/1000、16/17 维） | 待修复 |
| FF-90 | ai_server/brain_route.py（F13） | 样本不足时 train_step 提前返回，ε 不衰减 | 待修复 |
| FF-91 | ai_server/brain_route.py（F14） | 特征冗余与语义歧义：has_ammo 重叠、friend_count 含自身 | 待修复 |

---

## 🛠️ 本轮修复范围（priorityOrder 前 22 项，上线前必修）

| 顺序 | id | 修复要点 | 状态 |
|---|---|---|---|
| 1 | FF-03 | 锁内 Write 移出 + SendTimeout + _sendBuffer 容量上限 | 待修复 |
| 2 | FF-01 | train_step 改调 _save_locked / RLock | 待修复 |
| 3 | FF-02 | NaN/Inf 三层防护（store/train/predict）+ 写盘前校验 | 待修复 |
| 4 | FF-04 | readline limit=1MB | 待修复 |
| 5 | FF-31 | penalty 有限性校验 + 监听/鉴权 | 待修复 |
| 6 | FF-05 | PlayerId↔Id 映射或 Hub 定位 | 待修复 |
| 7 | FF-11 | IdleStuck 判定加 Enabled && Timeout>0 | 待修复 |
| 8 | FF-13 | Move 步长改 TickInterval | 待修复 |
| 9 | FF-22 | decide_combat 去掉 learn_last_action 条件 | 待修复 |
| 10 | FF-24 | tactics/states/nn_stats 加锁 | 待修复 |
| 11 | FF-28 | per-bot try/except + 防御式字段访问 | 待修复 |
| 12 | FF-27 | waypoint_step len==1 特判 + 越界前置检查 | 待修复 |
| 13 | FF-21 | Dispose 同步从 Bots 移除 | 待修复 |
| 14 | FF-09 | 非有限数值全链路校验 | 待修复 |
| 15 | FF-17 | waypoints.yml 先读后记 + null 判空 | 待修复 |
| 16 | FF-19 | 投掷 tx/ty/tz 两端协议统一 | 待修复 |
| 17 | FF-23 | rush 阶段消费手雷 | 待修复 |
| 18 | FF-26 | episode 判定改目标身份比较 | 待修复 |
| 19 | FF-25 | get_tactic_role 距离过滤 | 待修复 |
| 20 | FF-30 | _load 全矩阵 + 回放列数校验 | 待修复 |
| 21 | FF-06 | 按原角色重建 bot | 待修复 |
| 22 | FF-07 | 换弹序列化（按下→保持→松开） | 待修复 |
| 23 | FF-10 | 投掷确认轮询 + ?? 优先级 | 待修复 |

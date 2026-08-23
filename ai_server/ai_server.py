#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ScpBot 外部 AI 服务器（多核决策版）
=====================================
独立于 SL 服务器运行：接收插件推送的世界状态（房间图 + 机器人/敌人快照），
用多 worker 并行对每个机器人做完整决策（索敌 + 走位状态机 + 巡逻 + 开火），
把指令回传给插件执行（插件侧仍是唯一能碰游戏对象的地方）。

决策逻辑与插件本地 AI（Bot.Tick / MoveCombat / PatrolTick）保持一致，
只是把「纯计算」部分搬到这里多核并行，感知（视线检测）与执行（移动/开火）仍在插件主线程。

协议（TCP + 每行一个 JSON）：
    插件 -> 本服务:
        {"type":"cfg","rooms":{...},"routes":{...},"targets":{...}}
        {"type":"snap","bots":[{"id","p","r","t","h","role",
                                "kills","deaths",
                                "items":{"he","flash","med"},
                                "routes":[[room,...],...],
                                "enemies":[{"n","p","ap","d","t","vis"}]}],
                        "peers":[...]}
    本服务 -> 插件:
        {"type":"orders","bot":N,"shoot":0/1,"look":[x,y,z],
                         "moveTo":[x,y,z],"chaseTo":[x,y,z],
                         "throw":"he"/"flash","tx":[x,y,z],"heal":0/1}
        {"type":"ping"}

神经网络学习（可选，依赖 numpy）：
    用 brain_route.py 的轻量 DQN 学习「追目标选哪条路线」（3 选 1），
    奖励来自插件统计的 kills/deaths/血量变化，模型存 brain_route.npz。
    环境变量 SCPBOT_NO_BRAIN=1 可关闭。

用法：python ai_server.py [端口]
依赖：Python 3.8+（标准库；神经网络学习需 pip install numpy）
"""
import asyncio
import json
import math
import os
import random
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor

# 神经网络学习（路线选择）：可设置环境变量 SCPBOT_NO_BRAIN=1 关闭。
NO_BRAIN = os.environ.get("SCPBOT_NO_BRAIN", "0") == "1"
if not NO_BRAIN:
    try:
        import numpy as np  # noqa: F401  确保 numpy 可用（brain_route 依赖 + 本文件 np.argmax/mean/max）
        from brain_route import build_state, get_brain, save_brain, status_json
    except ImportError:
        NO_BRAIN = True
        print("[brain] numpy 未安装或加载失败，神经网络学习已禁用（pip install numpy 可启用）。",
              flush=True)

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 9000

# 详细日志开关：环境变量 SCPBOT_VERBOSE=1 时打印每个 bot 的决策明细。
VERBOSE = os.environ.get("SCPBOT_VERBOSE", "0") == "1"


def log(msg):
    """带时间戳的日志（flush 确保双击运行时实时可见）。"""
    ts = time.strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)

# ---- 战斗/走位参数（与插件 BotConfig 默认值一致）----
ATTACK_RANGE = 40.0            # 开火距离（米）
PREFERRED_RANGE = 10.0         # 理想交战距离（米）
RANGE_TOLERANCE = 4.0          # 状态机距离容差
ORBIT_RETREAT_DISTANCE = 2.5   # 贴脸后撤距离（贴近肉搏，避免室内畏缩）
CLOSE_QUARTER_DISTANCE = 12.0  # 室内近距离：改用朝目标推进 + 小横移（低于该距离）
ORBIT_INWARD_BIAS = 0.12       # 绕圈内收强度
AGGRESSIVE_CHARGE = True       # 猛冲模式：战斗时任何距离都朝目标冲锋（与插件 AggressiveCharge 一致）
CHASE_STRAFE_BIAS = 0.6        # 追击横移强度
PATROL_SPREAD_RADIUS = 8.0     # 巡逻扩散半径
VISION_RANGE = 60.0            # 索敌距离（米）
MOVE_STEP = 3.0                # 走位目标点距当前位置的步长（米）

# 横移方向随机翻转间隔（秒）
STRAFE_FLIP_MIN = 0.12
STRAFE_FLIP_MAX = 0.32
# 巡逻扩散偏移刷新间隔（秒）
PATROL_SPREAD_MIN = 0.8
PATROL_SPREAD_MAX = 2.0

PING_INTERVAL = 1.0


def vec3(p):
    """把快照坐标转 (x, y, z)。FF-28/FF-09 防御：缺字段/类型错误/非数字/NaN/Inf
    返回 None，由调用方兜底（脏快照与畸形数值都不应让整个连接崩溃或污染 orders）。"""
    if not isinstance(p, (list, tuple)) or len(p) < 3:
        return None
    try:
        x, y, z = float(p[0]), float(p[1]), float(p[2])
    except (TypeError, ValueError):
        return None
    if not (math.isfinite(x) and math.isfinite(y) and math.isfinite(z)):
        return None
    return (x, y, z)


def dist2(a, b):
    return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2


def dist(a, b):
    return math.sqrt(dist2(a, b))


def horiz(a):
    """取水平方向单位向量（y=0）；零向量返回 None。"""
    v = (a[0], 0.0, a[2])
    m = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if m < 1e-6:
        return None
    return (v[0] / m, v[1] / m, v[2] / m)


def add(a, b, scale=1.0):
    return (a[0] + b[0] * scale, a[1] + b[1] * scale, a[2] + b[2] * scale)


def normalized(v):
    m = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if m < 1e-6:
        return (0.0, 0.0, 0.0)
    return (v[0] / m, v[1] / m, v[2] / m)


class BotState:
    """单个机器人的决策状态（跨快照保留，模拟本地 AI 的字段状态）。"""

    def __init__(self):
        self.combat_state = "chase"   # chase / orbit
        self.strafe_direction = 1     # -1 左 / 1 右
        self.orbit_direction = 1      # 绕圈方向 -1/1
        self.next_strafe_flip = 0.0
        self.patrol_target = None     # 锁定地标
        self.last_patrol_target = None
        self.patrol_spread = (0.0, 0.0, 0.0)
        self.next_patrol_spread = 0.0
        # 航点巡逻状态（复刻本地 TryGetNextWaypoint）
        self.waypoint_room = None     # 当前航点所属房间
        self.waypoint_route = None    # 当前选中的路线（点列表）
        self.waypoint_index = 0
        self.waypoint_forward = True

        # 神经网络学习状态（路线选择 / 寻路学习）
        self.learn_last_state = None   # 上一 tick 的状态特征
        self.learn_last_action = None  # 上一 tick 选择的路线索引
        # FF-75：样本记录时刻的血量/击杀/阵亡快照 —— 样本节流（每 SAMPLE_EVERY_TICKS 记一个）
        # 使「结算时用上次结算后的基线」滞后 3~4 tick，击杀/伤害被错误归因到旧样本。
        # 改为用样本时刻的快照算增量（样本→结算的因果窗口正好对应所选动作的后果）。
        self.learn_sample_health = None
        self.learn_sample_kills = 0
        self.learn_sample_deaths = 0
        self.learn_last_goal = None    # 上一 tick 的目标房间（路线变化时重置 episode）
        self.learn_prev_target_dist = None  # 上一 tick 目标距离（算靠近/远离奖励）
        self.learn_sample_tick = 0     # 样本节流计数（每 SAMPLE_EVERY_TICKS 记一个样本）

        # 拟人记忆：最后可见的敌人（位置 + 时间），不可见时朝记忆位置搜索，超时遗忘。
        # 解决「bot 看不见玩家却直接追过来」的透视问题。
        self.last_seen_target = None    # (x, y, z)
        self.last_seen_time = 0.0


class World:
    """一个连接内的世界状态：静态 cfg + 最新快照 + 每个 bot 的决策状态。"""

    def __init__(self):
        self.rooms = {}
        self.routes = {}
        self.targets = {}
        self.bots = []
        self.peers = []
        self.states = {}   # bot id -> BotState
        self.patrol_warned = set()   # (bot_id, room) 已警告过无导航数据的组合，避免刷屏
        # FF-87：worker 数按 CPU 核数自适应（此前硬编码 16，低核机器过度并发）。
        self.executor = ThreadPoolExecutor(max_workers=max(4, min(32, os.cpu_count() or 16)))

        # FF-24：保护共享可变状态（tactics / nn_stats / states）的并发读写。
        # decide_bot 在 ThreadPoolExecutor 的多 worker 线程中并发执行，
        # update_tactics（scout_wave += 1 等 RMW）、nn_stats 计数、get_state 的
        # check-then-set 都是跨线程共享操作，必须串行化。
        self.lock = threading.Lock()

        # 示教路线库（模仿学习）：(起点房间, 目标房间) -> [路线1, 路线2, ...]
        # 由插件 bot follow 带领期间记录的房间轨迹填充，供神经网络冷启动与导航参考。
        self.taught_routes = {}

        # 神经网络运行统计（控制台输出用）。
        self.nn_stats = {
            "decisions": 0,          # 神经网络参与决策的次数
            "explore": 0,            # ε-贪心探索次数
            "exploit": 0,            # 利用次数
            "rewards": 0.0,          # 累计奖励
            "penalties": 0,          # 惩罚次数
            "penalty_total": 0.0,    # 惩罚总额
            "traces": 0,             # 示教轨迹数
            "trace_rooms": 0,        # 示教轨迹房间总数
            "q_samples": 0,          # Q 值采样数
            "q_sum": 0.0,            # Q 值总和（算均值）
            "q_max_sum": 0.0,        # Q 最大值总和
        }

        # predict 抛异常累计次数（诊断用）：只在前几次打印异常详情，避免刷屏。
        self.predict_faults = 0

        # 火力压制战术协调状态（bot 之间「商量」的共享载体，跨 tick 保留）：
        # 敌人躲进掩体（多个 bot 记忆同一位置）时，指派敢死 bot 靠近侦查，
        # 其余 bot 朝掩体方向开火压制 + 分散站位；敢死队全灭则手雷轰炸掩体。
        self.tactics = {
            "active": False,         # 压制战术是否激活
            "phase": "suppress",     # 战术阶段：suppress（压制+侦查）/ rush（扔完手雷总攻）
            "cover_pos": None,       # 被压制的掩体位置 (x,y,z)
            "cover_since": 0.0,      # 压制开始时间
            "suppress_until": 0.0,   # 压制截止（持续时长）
            "scout_ids": [],         # 本轮敢死 bot id 列表
            "scout_wave": 0,         # 敢死波次（每次派新敢死队递增）
            "scout_started": 0.0,    # 本轮敢死开始时间
            "grenade_wave": 0,       # 已扔手雷的波次（避免重复扔）
            "rush_started": 0.0,     # 总攻开始时间
            "pending_grenades": [],  # 待消费的手雷指令（bot id 列表）
            "last_grenade_throw": None,  # 待消费的投掷指令 {bot, target}
            "bot_positions": {},     # bot id -> 上 tick 位置（检测敢死队推进/死亡）
        }

    def load_config(self, cfg):
        self.rooms = cfg.get("rooms", {})
        self.routes = cfg.get("routes", {})
        self.targets = cfg.get("targets", {})
        # 诊断：打印地标/航点详情（房间名 + 点数），方便核对房间名是否与 bot 快照的 r 匹配。
        t_detail = ", ".join(f"{k}:{len(v)}" for k, v in self.targets.items()) or "(无)"
        r_detail = ", ".join(f"{k}:{len(v)}条" for k, v in self.routes.items()) or "(无)"
        log(f"[cfg] 目标点房间 {len(self.targets)} 个：[{t_detail}]")
        log(f"[cfg] 航点房间 {len(self.routes)} 个：[{r_detail}]")

    def get_state(self, bot_id):
        # FF-24：用 dict.setdefault（GIL 下原子）替代 check-then-set，
        # 避免并发线程同时发现 key 缺失、各自 new BotState() 互相覆盖。
        return self.states.setdefault(bot_id, BotState())


# ---- 神经网络学习（内战战斗决策）----

# 训练/日志节流：样本每 tick 产生（环形缓冲封顶），但训练降频、日志节流，
# 防止 Python 端过载导致 cmd 崩溃（5000+ 样本时曾崩）。
BRAIN_TRAIN_INTERVAL = 20   # 每 N 个快照训练一批（每 ~2 秒一次，原 2 过快）
TRAIN_LOG_EVERY = 25        # 每 N 训练步打一次 [brain] 日志（原每次训练都打）
NN_SUMMARY_EVERY = 200      # 每 N 决策打一次 [nn] 综合摘要（原 50 过快）
SAMPLE_EVERY_TICKS = 5      # 每 N 个 tick 记一个学习样本（原每 tick 一个，过密）
_brain_snap_counter = 0


def _brain_enabled():
    return not NO_BRAIN


def build_nav_candidates(world, bot, target):
    """构造寻路目标候选（动作空间）：
    候选 0 = 直线目标（目标当前位置）；
    候选 1..N = 各候选路线的终点房间中心（最多 3 条，含示教路线优先）；
    无路线时生成「目标方向偏移点」候选（保证网络总有 ≥2 个动作可学，避免样本为 0）。
    返回 (candidates, valid_indices)。"""
    pos = vec3(bot["p"])
    tpos = vec3(target["p"])
    candidates = [tpos]
    valid = [0]

    # 先看示教路线库：当前房间 → 目标房间 是否学过（模仿学习优先）。
    bot_room = bot.get("r") or ""
    taught = None
    if bot_room:
        # 尝试精确匹配当前房间到目标房间；再尝试从任意起点到目标房间。
        goal_room = bot.get("routes")[0][-1] if bot.get("routes") else None
        if goal_room:
            taught = world.taught_routes.get((bot_room, goal_room))
            if not taught:
                # 泛化：任意起点 → 该目标房间的第一条路线。
                for (s, g), routes in world.taught_routes.items():
                    if g == goal_room and routes:
                        taught = routes
                        break

    if taught:
        # 示教路线（每条的终点房间中心作为候选，最多 3 条）。
        for i, route in enumerate(taught[:3]):
            if route:
                end_room = route[-1]
                room_info = world.rooms.get(end_room)
                if room_info and room_info.get("c"):
                    candidates.append(tuple(room_info["c"]))
                    valid.append(i + 1)

    # 常规路线候选（快照里的 routes）。
    routes = bot.get("routes")
    if routes:
        for i, route in enumerate(routes[:3]):
            if route:
                end_room = route[-1]
                room_info = world.rooms.get(end_room)
                if room_info and room_info.get("c"):
                    # 避免与示教候选重复。
                    candidate = tuple(room_info["c"])
                    if candidate not in candidates:
                        candidates.append(candidate)
                        valid.append(len(candidates) - 1)

    # 候选仍不足 2 个（无路线/无房间中心）：生成目标方向偏移点，
    # 保证网络有可学动作（朝目标 / 偏左 / 偏右 30° 的走位备选）。
    if len(valid) <= 1:
        to_target = horiz((tpos[0] - pos[0], tpos[1] - pos[1], tpos[2] - pos[2]))
        if to_target is not None:
            step = 6.0  # 偏移距离（米）
            # 目标方向 ±30° 的偏移点（绕行备选）。
            for angle in (30.0, -30.0, 60.0, -60.0):
                rad = math.radians(angle)
                cos_a, sin_a = math.cos(rad), math.sin(rad)
                dx = to_target[0] * cos_a - to_target[2] * sin_a
                dz = to_target[0] * sin_a + to_target[2] * cos_a
                candidates.append((pos[0] + dx * step, pos[1], pos[2] + dz * step))
                valid.append(len(candidates) - 1)

    return candidates, valid


def learn_combat_action(world, bot, st, target, target_dist, visible_count):
    """用神经网络选内战战斗走位动作（ε-贪心，8 向走位）。
    返回 (action, None 或 动作索引)；无目标/网络禁用时返回 (None, None)。"""
    if not _brain_enabled():
        return None, None

    state = build_state(bot, target, target_dist, visible_count, world,
                        prev_dist=st.learn_prev_target_dist)
    brain = get_brain()

    action = brain.choose_action(state)

    # 神经网络运行统计（控制台输出用）。
    # FF-24：nn_stats 由 worker 线程（此处）与事件循环线程（brain_tick 读取）并发
    # 访问，+= 是「读-加-写」三步，必须加锁防止丢失更新。
    # FF-68：此前用独立 random.random() < epsilon 预判「探索」，与 choose_action 内部
    # 的随机分支是两次独立随机，统计经常错标（判定探索实际利用、反之亦然）。
    # 改为与 choose_action 的实际行为对齐：选中的动作 == Q 值最大 → 利用，否则 → 探索。
    # 同时顺带完成 Q 值统计（避免重复 predict）。
    try:
        q = brain.predict(state)
        is_exploit = action == int(np.argmax(q))
        with world.lock:
            world.nn_stats["decisions"] += 1
            if is_exploit:
                world.nn_stats["exploit"] += 1
            else:
                world.nn_stats["explore"] += 1
            world.nn_stats["q_samples"] += 1
            world.nn_stats["q_sum"] += float(np.mean(q))
            world.nn_stats["q_max_sum"] += float(np.max(q))
    except Exception as ex:
        # predict 失败：只记决策数（无法判定探索/利用时按探索计，保守）。
        # 诊断：predict 一旦持续抛异常，[nn] 日志会呈现「探索=100% Q均值=0 Qmax=0」，
        # 但异常被这里吞掉后根因不可见。前 3 次打印异常类型/消息与 state 维度，
        # 帮助定位（常见：代码与 brain_route.npz 的 STATE_DIM 不一致导致矩阵维度不匹配）。
        with world.lock:
            world.nn_stats["decisions"] += 1
            world.nn_stats["explore"] += 1
            world.predict_faults += 1
            fault_n = world.predict_faults
        if fault_n <= 3:
            state_dim = len(state) if isinstance(state, (list, tuple)) else "?"
            log(f"[brain] predict 异常 #{fault_n}：{type(ex).__name__}: {ex}（state 维度={state_dim}，期望 17）")

    # 记录本次选择，供下一 tick 结算奖励。
    # 样本节流：每 SAMPLE_EVERY_TICKS 个 tick 才记录一个学习样本（网络决策本身每 tick 执行，
    # 节流的只是样本记录），避免 20 bot × 10 tick/s = 200 样本/s 把 Python 端压垮。
    st.learn_sample_tick += 1
    if st.learn_sample_tick >= SAMPLE_EVERY_TICKS:
        st.learn_sample_tick = 0
        st.learn_last_state = state
        st.learn_last_action = action
        st.learn_prev_target_dist = target_dist
        # FF-75：记录样本时快照血量/击杀/阵亡基线（供 learn_settle_reward 精确归因）。
        st.learn_sample_health = bot.get("h", 0.0)
        st.learn_sample_kills = bot.get("kills", 0)
        st.learn_sample_deaths = bot.get("deaths", 0)
    return action, action


def learn_settle_reward(world, bot, st):
    """每 tick 结算上一动作的奖励并存入经验回放（DQN 持续在线学习）。
    内战奖励：击杀 +1、阵亡 -1、存活每 tick +0.01、血量上升 +0.05*Δ（治疗/回血）、
    受伤害 -0.05*Δ、靠近目标 +0.05、远离目标 -0.05。
    目标变化/死亡时视为 episode 结束（done=True）。"""
    if not _brain_enabled() or st.learn_last_state is None or st.learn_last_action is None:
        return

    h = bot.get("h", 0.0)
    kills = bot.get("kills", 0)
    deaths = bot.get("deaths", 0)

    reward = 0.0

    # 击杀 / 阵亡增量（内战核心，惩罚严厉：送死重罚）。
    # FF-75：用样本记录时刻的快照（learn_sample_*）而非上次结算后的基线（learn_prev_*）——
    # 后者在样本节流下滞后 3~4 tick，把中间 tick 的击杀/伤害错误归因到旧样本。
    reward += (kills - st.learn_sample_kills) * 1.0
    reward += (deaths - st.learn_sample_deaths) * -3.0

    # 血量变化奖励（治疗正、受伤害惩罚重：被打很疼，-0.15*Δ）。
    if st.learn_sample_health is not None:
        dh = h - st.learn_sample_health
        reward += dh * 0.15

    # 存活奖励（每 tick 小额正奖励，鼓励不送死）。
    reward += 0.01

    # 靠近目标奖励：本 tick 与目标的距离变化（选对走位 → 更接近 → 正奖励；
    # 远离目标惩罚重，-0.1，防止乱跑/逃跑）。
    target = choose_target(bot.get("enemies", []))
    # FF-67：cur_d 必须在 if 之外初始化 —— 当 target 存在但 learn_prev_target_dist 为 None
    # 时，不进入下面 if 分支，cur_d 未赋值；后续 not done 分支中
    # prev_dist=cur_d if target else None 会因 cur_d 未定义抛 UnboundLocalError。
    cur_d = 0.0
    if target is not None and st.learn_prev_target_dist is not None:
        cur_d = target.get("d", 0.0)
        reward += 0.05 if cur_d < st.learn_prev_target_dist else -0.1

    # episode 结束判定。
    # FF-26：不能用完整敌人列表比较 —— 插件快照的敌人列表含全部敌方玩家且无序，
    # 任何敌方玩家死亡/重生/进出都会让列表变化 → 每个 bot 每几秒就被重置 episode，
    # done=True 时多步回报（GAMMA=0.9）归零，DQN 退化为 1 步 TD(0)。
    # 改为只比较「当前追击目标」的身份：目标换人（或消失）才结束本段 episode。
    target_id = target.get("n") if target else None
    goal_changed = st.learn_last_goal is not None and target_id != st.learn_last_goal
    done = goal_changed or deaths > st.learn_sample_deaths or h <= 0.0

    # 下一状态（用于 DQN 的 next_state；done 时用零向量）。
    next_state = st.learn_last_state
    if not done:
        tpos = vec3(target["p"]) if target else None
        tdist = dist(vec3(bot["p"]), tpos) if tpos else 0.0
        vis_count = sum(1 for e in bot.get("enemies", []) if e.get("vis"))
        next_state = build_state(bot, target, tdist, vis_count, world,
                                 prev_dist=cur_d if target else None)

    brain = get_brain()
    brain.store(st.learn_last_state, st.learn_last_action, reward, next_state, done)
    brain.total_reward += reward
    # FF-24：nn_stats 跨线程（worker 线程写 decisions 等），锁内累加防丢失更新。
    with world.lock:
        world.nn_stats["rewards"] += reward

    # 更新基线（learn_sample_* 由下次样本记录重新快照，无需在此更新）。
    st.learn_last_goal = target_id
    if target is not None:
        st.learn_prev_target_dist = target.get("d", 0.0)

    # 清空待结算状态：样本节流期间（每 5 tick 一个新样本），
    # 中间 tick 不再重复结算同一动作（否则样本爆炸 + 奖励错乱）。
    st.learn_last_state = None
    st.learn_last_action = None


def brain_tick(world):
    """训练调度：每 BRAIN_TRAIN_INTERVAL 个快照，对所有 bot 结算奖励并做一步训练。
    同时定期输出神经网络运行状态（决策统计 / Q 值 / 示教学习 / 惩罚），便于观察学习进程。"""
    global _brain_snap_counter
    if not _brain_enabled():
        return

    # FF-69：清理已不存在 bot 的决策状态 —— bot id 是 C# 端自增计数器（无上限），
    # 长时间运行会积累大量死 bot 的 BotState（内存泄漏）。当前存活 id 集合由事件循环
    # 线程的 world.bots 提供；del 与 worker 的 setdefault 在 GIL 下原子，竞争无害。
    alive_ids = {b["id"] for b in world.bots}
    for sid in list(world.states.keys()):
        if sid not in alive_ids:
            del world.states[sid]

    for bot in world.bots:
        st = world.get_state(bot["id"])
        learn_settle_reward(world, bot, st)

    _brain_snap_counter += 1
    if _brain_snap_counter >= BRAIN_TRAIN_INTERVAL:
        _brain_snap_counter = 0
        brain = get_brain()
        loss = brain.train_step()
        # 日志节流：每 25 训练步打一次（避免每 2 秒刷一行导致 cmd 缓冲区堆满崩溃）。
        if loss is not None and brain.step % TRAIN_LOG_EVERY == 0:
            log(f"[brain] 训练步={brain.step} loss={loss:.4f} ε={brain.epsilon:.3f} "
                f"样本={brain.samples} 累计奖励={brain.total_reward:.2f}")

    # 每 NN_SUMMARY_EVERY 个快照输出一次神经网络综合状态（节流，避免刷屏）。
    if _brain_snap_counter == 0 and world.nn_stats["decisions"] > 0 \
            and (world.nn_stats["decisions"] % NN_SUMMARY_EVERY) < BRAIN_TRAIN_INTERVAL:
        s = world.nn_stats
        explore_pct = 100.0 * s["explore"] / max(1, s["decisions"])
        q_avg = s["q_sum"] / max(1, s["q_samples"])
        q_max_avg = s["q_max_sum"] / max(1, s["q_samples"])
        log(f"[nn] 决策={s['decisions']} 探索={explore_pct:.0f}% 奖励累计={s['rewards']:.2f} "
            f"Q均值={q_avg:.3f} Qmax={q_max_avg:.3f}")
        log(f"[nn] 示教路线组={len(world.taught_routes)} 轨迹={s['traces']} 房间={s['trace_rooms']} "
            f"惩罚={s['penalties']}次/{s['penalty_total']:.1f}")


def brain_save():
    if _brain_enabled():
        try:
            save_brain()
        except Exception as ex:
            log(f"[brain] 保存失败: {ex}")


def handle_trace(world, msg):
    """处理插件发来的示教轨迹（bot follow 带领期间记录的房间序列）：
    存入示教路线库（起点房间, 目标房间）→ 路线，供模仿学习参考。"""
    if not _brain_enabled():
        return

    rooms = msg.get("rooms")
    if not rooms or len(rooms) < 2:
        log("[trace] 示教轨迹房间数不足 2，忽略。")
        return

    # 去重连续重复房间（同一房间停留不重复）。
    cleaned = []
    for r in rooms:
        if not cleaned or cleaned[-1] != r:
            cleaned.append(r)

    if len(cleaned) < 2:
        return

    start = cleaned[0]
    goal = cleaned[-1]
    key = (start, goal)

    if key not in world.taught_routes:
        world.taught_routes[key] = []

    route = cleaned
    # 避免完全重复的路线。
    # FF-86：重复路线（已存在）时不再累加 traces/trace_rooms 统计 ——
    # 此前每次都 +1，统计虚高且误导「学到了多少新轨迹」。
    is_new_route = route not in world.taught_routes[key]
    if is_new_route:
        world.taught_routes[key].append(route)
        # FF-24：nn_stats 跨线程（worker 线程写 decisions 等），锁内累加防丢失更新。
        with world.lock:
            world.nn_stats["traces"] += 1
            world.nn_stats["trace_rooms"] += len(cleaned)

    log(f"[trace] 示教轨迹已学习：{start} → {goal}（{len(cleaned)} 个房间），"
        f"该路线组现有 {len(world.taught_routes[key])} 条路线。")
    if VERBOSE:
        log(f"[trace]   路线: {' -> '.join(cleaned)}")


def handle_penalty(world, msg):
    """处理插件发来的严厉惩罚（卡房超时等）：
    给神经网络所有样本统一记大额负奖励（通过 total_reward 与经验回放内奖励追加实现）。"""
    if not _brain_enabled():
        return

    # FF-31：amount 有限性校验 + 限幅 —— 服务器无鉴权，任何能连上 TCP 的客户端都能发
    # penalty；NaN/Inf/超大值会污染经验回放并最终写坏权重文件（越权数据损坏入口）。
    try:
        amount = float(msg.get("amount", -5.0))
    except (TypeError, ValueError):
        log("[惩罚] 忽略非法 amount（非数字）")
        return
    if not math.isfinite(amount):
        log(f"[惩罚] 忽略非有限 amount（{amount}），拒绝 NaN/Inf 投毒")
        return
    amount = max(-50.0, min(50.0, amount))
    reason = msg.get("reason", "unknown")
    team = msg.get("team", "?")

    # FF-24：nn_stats 跨线程，锁内累加。
    with world.lock:
        world.nn_stats["penalties"] += 1
        world.nn_stats["penalty_total"] += amount

    # 给经验回放中最近的样本追加惩罚（严厉惩罚，让网络学到「卡房=坏行为」）。
    # FF-72：只改经验回放（训练信号），不再直接累加 brain.total_reward —— total_reward 由
    # learn_settle_reward 逐 tick 累加真实奖励；penalty 若两边都加会造成日志与训练信号双重计数
    # （惩罚统计已由 nn_stats["penalty_total"] 记录）。
    brain = get_brain()
    if brain.replay:
        # 对所有 bot 最近 20 条经验追加惩罚（简化：全局追加，强化惩罚信号）。
        n = min(20, len(brain.replay))
        for i in range(len(brain.replay) - n, len(brain.replay)):
            state, action, reward, next_state, done = brain.replay[i]
            brain.replay[i] = (state, action, reward + amount, next_state, done)

    log(f"[惩罚] 阵营 {team} 卡房超时（{reason}），神经网络惩罚 {amount:.1f}，"
        f"累计惩罚 {world.nn_stats['penalty_total']:.1f}")


# ---- 决策逻辑 ----

# 拟人记忆时长（秒）：敌人从视野消失后，bot 还会朝最后看见的位置搜索多久。
# 超时遗忘转巡逻，避免「看不见还追到掩体后」的透视行为。
LAST_SEEN_MEMORY = 6.0


def choose_target(enemies):
    """选最近的可见敌人；无可见敌人返回 None（不再把看不见的敌人当目标——消除透视）。
    不可见敌人由拟人记忆处理（decide_bot）。"""
    if not enemies:
        return None
    visible = [e for e in enemies if e.get("vis")]
    if not visible:
        return None
    return min(visible, key=lambda e: e.get("d", math.inf))


def decide_combat(world, bot, st, target, combat_action=None):
    """战斗决策：神经网络全面接管（走位 + 开火时机，16 维动作）。
    规则（猛冲 + 可见射程内开火）仅在网络禁用或冷启动无样本时兜底。
    返回 orders 字典。"""
    pos = vec3(bot["p"])
    tpos = vec3(target["p"])
    aim = vec3(target.get("ap", target["p"]))
    d = dist(pos, tpos)
    visible = bool(target.get("vis"))
    now = time.monotonic()

    orders = {"type": "orders", "bot": bot["id"]}

    # 神经网络启用且本次决策返回了动作（combat_action 由 learn_combat_action 每 tick 计算）
    # → 网络接管走位 + 开火。
    # FF-22 修复：此前误用 st.learn_last_action（样本节流字段，每 SAMPLE_EVERY_TICKS 才写入一次、
    # 结算后被清空）作为接管开关，导致网络只在 1/5 的 tick 真正生效，其余 tick 全走规则兜底。
    if combat_action is not None and _brain_enabled():
        move_action = combat_action % 8      # 走位部分
        want_shoot = combat_action >= 8      # 开火部分
        orders["shoot"] = 1 if want_shoot else 0
        orders["look"] = list(aim)

        # 走位：网络 8 向（0冲 1左冲 2右冲 3左移 4右移 5后退 6保持 7贴身）。
        move_dir = combat_action_to_dir(pos, tpos, combat_action)
        if move_dir is None:
            return orders  # 网络选「原地保持」：不开火或开火，但不移动
        orders["moveTo"] = list(add(pos, move_dir, MOVE_STEP))
        return orders

    # ---- 兜底（网络禁用 / 冷启动无样本 / 无网络动作）：规则接管 ----

    # 开火：可见且射程内。
    shoot = visible and d <= ATTACK_RANGE
    orders["shoot"] = 1 if shoot else 0
    orders["look"] = list(aim)

    # 横移方向周期性翻转（模拟真人反复横跳）。
    if now >= st.next_strafe_flip:
        st.strafe_direction = random.choice((-1, 1))
        st.next_strafe_flip = now + random.uniform(STRAFE_FLIP_MIN, STRAFE_FLIP_MAX)

    move_dir = None
    if AGGRESSIVE_CHARGE:
        # 猛冲：任何距离都朝目标冲锋。贴脸（<3m）纯直线压上；较远叠加小幅横移晃动。
        to_target = horiz((tpos[0] - pos[0], tpos[1] - pos[1], tpos[2] - pos[2]))
        if to_target is None:
            return orders
        if d < 3.0:
            move_dir = to_target
        else:
            right = (to_target[2], 0.0, -to_target[0])
            desired = (
                to_target[0] * 0.9 + right[0] * st.strafe_direction * 0.1,
                0.0,
                to_target[2] * 0.9 + right[2] * st.strafe_direction * 0.1,
            )
            move_dir = normalized(desired)
    else:
        # 状态机：距离 > 理想+容差 -> chase，否则 orbit。
        next_state = "chase" if d > PREFERRED_RANGE + RANGE_TOLERANCE else "orbit"
        if next_state != st.combat_state:
            st.combat_state = next_state
            if next_state == "orbit":
                st.orbit_direction = random.choice((-1, 1))

        if st.combat_state == "orbit":
            if d < ORBIT_RETREAT_DISTANCE:
                # 贴脸：不再后撤（避免双方僵持），朝目标压上。
                move_dir = horiz((tpos[0] - pos[0], tpos[1] - pos[1], tpos[2] - pos[2]))
            elif d < CLOSE_QUARTER_DISTANCE:
                # 室内近距离：朝目标推进 + 小幅横移（避免切向绕圈撞墙倒退导致畏缩不前）。
                move_dir = build_close_quarter_direction(pos, tpos, st)
            else:
                move_dir = build_orbit_direction(pos, tpos, d, st)
        else:
            move_dir = build_chase_direction(pos, tpos, st)

    if move_dir is None:
        return orders  # 距离过近无法定方向，原地待命

    # moveTo = 当前位置 + 走位方向 * 步长（本地 Move 会朝该点走并做障碍绕行）。
    orders["moveTo"] = list(add(pos, move_dir, MOVE_STEP))
    return orders


def combat_action_to_dir(pos, tpos, action):
    """神经网络 16 维动作 → 移动方向向量（走位部分 action%8）：
    0 朝目标猛冲 / 1 偏左30°冲 / 2 偏右30°冲 / 3 左横移 / 4 右横移 /
    5 后退 / 6 原地保持（返回 None 表示不动）/ 7 贴身压上。
    开火部分（action>=8）由调用方处理，这里只算走位。"""
    move_action = action % 8
    to_target = horiz((tpos[0] - pos[0], tpos[1] - pos[1], tpos[2] - pos[2]))
    if to_target is None:
        return None

    right = (to_target[2], 0.0, -to_target[0])

    def rotate(vec, angle_deg):
        rad = math.radians(angle_deg)
        cos_a, sin_a = math.cos(rad), math.sin(rad)
        return (vec[0] * cos_a - vec[2] * sin_a, 0.0, vec[0] * sin_a + vec[2] * cos_a)

    if move_action == 0:
        return to_target
    if move_action == 1:
        return rotate(to_target, -30)
    if move_action == 2:
        return rotate(to_target, 30)
    if move_action == 3:
        return right
    if move_action == 4:
        return (-right[0], 0.0, -right[2])
    if move_action == 5:
        return (-to_target[0], 0.0, -to_target[2])
    if move_action == 6:
        return None  # 原地保持
    if move_action == 7:
        # 贴身压上：朝目标 + 小横移（比 0 更激进的贴脸）。
        desired = (to_target[0] * 0.95 + right[0] * 0.05, 0.0, to_target[2] * 0.95 + right[2] * 0.05)
        return normalized(desired)
    return to_target


def build_close_quarter_direction(pos, tpos, st):
    """室内近距离走位：朝目标 75% + 横移 25%（复刻本地 BuildCloseQuarterDirection）。"""
    to_goal = horiz((tpos[0] - pos[0], tpos[1] - pos[1], tpos[2] - pos[2]))
    if to_goal is None:
        return None
    right = (to_goal[2], 0.0, -to_goal[0])
    desired = (
        to_goal[0] * 0.75 + right[0] * st.strafe_direction * 0.25,
        0.0,
        to_goal[2] * 0.75 + right[2] * st.strafe_direction * 0.25,
    )
    return normalized(desired)


def build_chase_direction(pos, tpos, st):
    """追击方向：朝目标 + 右侧横移（复刻本地 BuildChaseDirection）。"""
    to_goal = horiz((tpos[0] - pos[0], tpos[1] - pos[1], tpos[2] - pos[2]))
    if to_goal is None:
        return None
    # cross(up, to_goal) 的右向量：(to_goal.z, 0, -to_goal.x)
    right = (to_goal[2], 0.0, -to_goal[0])
    desired = (
        to_goal[0] + right[0] * st.strafe_direction * CHASE_STRAFE_BIAS,
        0.0,
        to_goal[2] + right[2] * st.strafe_direction * CHASE_STRAFE_BIAS,
    )
    return normalized(desired)


def build_orbit_direction(pos, tpos, d, st):
    """绕圈方向：切线 + 距离修正（太远内收）+ 随机横移（复刻本地 BuildOrbitDirection）。"""
    radial = horiz((tpos[0] - pos[0], tpos[1] - pos[1], tpos[2] - pos[2]))
    if radial is None:
        return None
    # cross(up, radial) 切线
    tangent = (radial[2], 0.0, -radial[0])
    tangent = (tangent[0] * st.orbit_direction, 0.0, tangent[2] * st.orbit_direction)

    min_orbit = max(2.0, PREFERRED_RANGE * 0.7)
    dist_correction = (0.0, 0.0, 0.0)
    if d >= min_orbit:
        dist_correction = (radial[0] * ORBIT_INWARD_BIAS, 0.0, radial[2] * ORBIT_INWARD_BIAS)

    radial_strafe = (radial[0] * st.strafe_direction * 0.22, 0.0, radial[2] * st.strafe_direction * 0.22)

    desired = (
        tangent[0] + dist_correction[0] + radial_strafe[0],
        0.0,
        tangent[2] + dist_correction[2] + radial_strafe[2],
    )
    return normalized(desired)


def decide_patrol(world, bot, st):
    """巡逻决策：与本地 PatrolTick 对齐的四级兜底（航点 → 地标 → 相邻房间 → 房间中心）。"""
    room = bot.get("r") or ""
    pos = vec3(bot["p"])
    now = time.monotonic()

    def no_move(reason):
        """无路可走时返回待命指令（并诊断一次，避免刷屏）。"""
        key = (bot["id"], room)
        if key not in world.patrol_warned:
            world.patrol_warned.add(key)
            log(f"[巡逻诊断] bot={bot['id']} 房间 '{room}' 无可用导航数据（{reason}），原地待命")
        return {"type": "orders", "bot": bot["id"], "shoot": 0}

    # 1) 航点巡逻：该房间配置了路线时沿路线走（进房随机选一条，就近端起步，到头翻向）。
    routes = world.routes.get(room)
    if routes:
        point = waypoint_step(st, room, routes, pos)
        if point is not None:
            return {
                "type": "orders",
                "bot": bot["id"],
                "shoot": 0,
                "look": list(point),
                "moveTo": list(point),
            }

    # 2) 地标巡逻：锁定地标 + 扩散偏移，到达后换下一个（形成来回）。
    pts = world.targets.get(room)
    if pts:
        reach_sq = 1.5 * 1.5
        reached = st.patrol_target is not None and dist2(pos, st.patrol_target) <= reach_sq
        if st.patrol_target is None or reached:
            nxt = select_patrol_target(pts, st)
            if nxt is not None:
                st.patrol_target = nxt
                refresh_patrol_spread(st, now)

        if now >= st.next_patrol_spread:
            refresh_patrol_spread(st, now)

        if st.patrol_target is not None:
            dest = add(st.patrol_target, st.patrol_spread)
            return {
                "type": "orders",
                "bot": bot["id"],
                "shoot": 0,
                "look": list(dest),
                "moveTo": list(dest),
            }

    # 离开有地标的房间后，清除巡逻目标。
    st.patrol_target = None

    # 3) 相邻房间：随机挑一个邻居，走向其中心（复刻本地 PatrolTick 第 3 级）。
    room_info = world.rooms.get(room)
    if room_info:
        neighbors = [n for n in room_info.get("a", []) if n != room]
        if neighbors:
            nxt_name = random.choice(neighbors)
            nxt_info = world.rooms.get(nxt_name)
            if nxt_info and nxt_info.get("c"):
                dest = tuple(nxt_info["c"])
                return {
                    "type": "orders",
                    "bot": bot["id"],
                    "shoot": 0,
                    "look": list(dest),
                    "moveTo": list(dest),
                }

    # 4) 兜底：当前房间中心附近随机点（避免所有 bot 挤到同一个点）。
    if room_info and room_info.get("c"):
        c = tuple(room_info["c"])
        dest = (
            c[0] + random.uniform(-3.0, 3.0),
            c[1],
            c[2] + random.uniform(-3.0, 3.0),
        )
        return {
            "type": "orders",
            "bot": bot["id"],
            "shoot": 0,
            "look": list(dest),
            "moveTo": list(dest),
        }

    return no_move("无航点/地标/邻居/中心")


def waypoint_step(st, room, routes, pos):
    """航点巡逻单步：返回当前应前往的航点；进新房间时随机选路线、就近端起步，到头翻向。
    复刻本地 TryGetNextWaypoint 的逻辑。无可用路线返回 None。"""
    if not routes:
        return None

    # 换房间 / 路线失效 → 重新随机选一条并就近端起步（正走/倒走）。
    if st.waypoint_room != room or st.waypoint_route is None:
        st.waypoint_room = room
        st.waypoint_route = random.choice(routes)
        route = st.waypoint_route

        if len(route) > 1:
            start_d = dist2(tuple(route[0]), pos)
            end_d = dist2(tuple(route[-1]), pos)
            st.waypoint_forward = start_d <= end_d
            st.waypoint_index = 0 if st.waypoint_forward else len(route) - 1
        else:
            st.waypoint_forward = True
            st.waypoint_index = 0

        if not route:
            return None

        return tuple(route[st.waypoint_index])

    route = st.waypoint_route
    if not route:
        return None

    # FF-27：单点路线特判 —— 永远返回该点（已到达也保持原位），
    # 避免「到达 → index 推进 → 翻转 → 负索引访问 → index=1 → 越界」的 4 tick 崩溃链
    # （此前单点路线会在第 4 tick 抛 IndexError 杀死整个连接）。
    if len(route) == 1:
        st.waypoint_index = 0
        st.waypoint_forward = True
        return tuple(route[0])

    # 到达当前点则推进（到达判定 1.5m，与本地 WaypointReachDistance 一致）。
    # FF-27：访问前防御性钳制 index（防止历史遗留状态/脏数据直接越界）。
    if st.waypoint_index < 0 or st.waypoint_index >= len(route):
        st.waypoint_index = 0 if st.waypoint_forward else len(route) - 1
    point = tuple(route[st.waypoint_index])
    if dist(pos, point) <= 1.5:
        st.waypoint_index += 1 if st.waypoint_forward else -1

    # 正序到头 → 翻向，从倒数第二点往回走；反序到头 → 翻向，从第 1 点往正序走。
    if st.waypoint_forward and st.waypoint_index >= len(route):
        st.waypoint_forward = False
        st.waypoint_index = len(route) - 2
    elif not st.waypoint_forward and st.waypoint_index < 0:
        st.waypoint_forward = True
        st.waypoint_index = 1

    if st.waypoint_index < 0 or st.waypoint_index >= len(route):
        return None

    return tuple(route[st.waypoint_index])


def select_patrol_target(pts, st):
    """选一个地标，排除上一个（避免原地踏步）。"""
    if len(pts) == 1:
        st.last_patrol_target = tuple(pts[0])
        return tuple(pts[0])
    candidates = [tuple(p) for p in pts if st.last_patrol_target is None or dist(tuple(p), st.last_patrol_target) > 0.1]
    if not candidates:
        candidates = [tuple(p) for p in pts]
    chosen = random.choice(candidates)
    st.last_patrol_target = chosen
    return chosen


def refresh_patrol_spread(st, now):
    st.patrol_spread = (
        random.uniform(-PATROL_SPREAD_RADIUS, PATROL_SPREAD_RADIUS),
        0.0,
        random.uniform(-PATROL_SPREAD_RADIUS, PATROL_SPREAD_RADIUS),
    )
    st.next_patrol_spread = now + random.uniform(PATROL_SPREAD_MIN, PATROL_SPREAD_MAX)


def decide_bot(world, bot):
    """对单个机器人做一次完整决策（拟人索敌：只追可见敌人，不可见靠记忆搜索 + 压制战术）。"""
    st = world.get_state(bot["id"])
    enemies = bot.get("enemies", [])
    target = choose_target(enemies)   # 只选可见敌人
    now = time.monotonic()

    if target is not None:
        # 看见敌人：更新记忆，进入战斗/追击。
        st.last_seen_target = tuple(vec3(target["p"]))
        st.last_seen_time = now

        visible = bool(target.get("vis"))
        d = target.get("d", math.inf)
        vis_count = sum(1 for e in enemies if e.get("vis"))

        if visible and d <= ATTACK_RANGE:
            # 战斗（内战）：神经网络学战斗走位（8 向），开火照常。
            combat_action, _ = learn_combat_action(world, bot, st, target, d, vis_count)
            return decide_combat(world, bot, st, target, combat_action)

        # 追击：可见但超射程 -> 规则寻路（NavMesh 拐点/房间路径）。
        return decide_chase(world, bot, target)

    # 当前看不见敌人：火力压制战术（多个 bot 对同一掩体位置压制 + 敢死侦查 + 手雷）。
    # 先更新战术协调（让全体 bot 共享掩体位置——即使自己没亲眼见过，也参与压制/总攻）。
    update_tactics(world, now)

    # FF-24：tactics 由多个 worker 线程并发读写（update_tactics 与这里），
    # 在锁内读取快照，避免读到中间状态（active=True 但 cover_pos 未就绪等）。
    with world.lock:
        tactic_active = world.tactics["active"]
        tactic_cover = world.tactics["cover_pos"]
    if tactic_active and tactic_cover is not None:
        my_pos = vec3(bot["p"])
        mem_pos = tactic_cover

        # 战术参与者：执行压制/侦查/总攻角色（不要求自己有个人记忆）。
        # FF-23：手雷消费不再只挂在 suppress 分支 —— 敢死失败后 phase 立即切 rush，
        # 全部 bot 走 decide_rush 而 consume_grenade 唯一调用点在 suppress 分支，
        # 导致 pending_grenades 永久滞留、手雷轰炸从未生效。rush 也消费。
        role = get_tactic_role(world, bot)
        orders = None
        if role == "scout":
            return decide_scout(world, bot, st, mem_pos)
        if role == "rush":
            orders = decide_rush(world, bot, st, mem_pos)
        elif role == "suppress":
            orders = decide_suppress(world, bot, st, mem_pos)
        if orders is not None:
            grenade = consume_grenade(world, bot, mem_pos)
            if grenade:
                orders.update(grenade)   # 附加 throw + tx（rush/suppress 都生效）
            return orders

    # 战术未激活或自己未参与：退回个人记忆搜索。
    if st.last_seen_target is not None and (now - st.last_seen_time) <= LAST_SEEN_MEMORY:
        my_pos = vec3(bot["p"])
        mem_pos = st.last_seen_target
        if dist(my_pos, mem_pos) <= 2.5:
            st.last_seen_target = None
            return decide_patrol(world, bot, st)

        return {
            "type": "orders",
            "bot": bot["id"],
            "shoot": 0,
            "look": list(mem_pos),
            "chaseTo": list(mem_pos),
        }

    # 无记忆或已过期：遗忘并巡逻。
    st.last_seen_target = None
    return decide_patrol(world, bot, st)


# ---- 火力压制战术（多 bot 协作）----

SUPPRESS_RADIUS = 12.0      # 压制圈半径：压制 bot 站掩体周围这个距离外
SUPPRESS_DURATION = 15.0    # 压制持续秒数（之后重新评估）
SCOUT_COUNT = 2             # 每轮敢死 bot 数量
SCOUT_TIMEOUT = 10.0        # 敢死队侦查超时（秒），超时视为失败
GRENADE_DISTANCE = 40.0     # 手雷能扔到掩体的最大距离（bot 追击中会接近）
# FF-25：压制参与者距离上限 —— 只有距掩体在此射程内的 bot 才被指派 suppress，
# 更远的 bot 继续自己的巡逻（避免全图 bot 被一个局部掩体记忆绑架）。
SUPPRESS_PARTICIPATION_RANGE = 40.0


def update_tactics(world, now):
    """FF-24 线程安全包装：update_tactics 由多个 worker 线程并发调用
    （每个「看不见敌人」的 bot 都会调一次），内部对 world.tactics 做
    read-modify-write（scout_wave += 1、pending_grenades 替换、states 清理等），
    必须整体串行化，否则波次计数丢失/双激活窗口/共享状态撕裂。"""
    with world.lock:
        _update_tactics(world, now)


def _update_tactics(world, now):
    """每 tick 更新压制战术状态：
    1) 统计所有 bot 的记忆掩体位置，找出「多个 bot 记忆同一位置」→ 激活压制；
    2) 敢死队死亡/超时 → 派新手雷波（若有手雷）或换一批敢死；
    3) 压制超时 → 解散（各自回巡逻）。"""
    t = world.tactics

    # 收集各 bot 当前记忆位置（最近 6s 内消失的敌人）。
    mem_counts = {}
    mem_positions = {}
    alive_ids = set()
    for b in world.bots:
        if b.get("h", 0) <= 0:
            continue
        alive_ids.add(b["id"])
        bs = world.get_state(b["id"])
        if bs.last_seen_target is not None and (now - bs.last_seen_time) <= LAST_SEEN_MEMORY:
            key = (round(bs.last_seen_target[0] / 5), round(bs.last_seen_target[2] / 5))  # 5m 网格聚类
            mem_counts[key] = mem_counts.get(key, 0) + 1
            mem_positions.setdefault(key, bs.last_seen_target)

    # 找被 ≥2 个 bot 记忆的位置（多人看见敌人躲进同一掩体）→ 激活压制。
    if not t["active"]:
        best_key = None
        best_count = 0
        for key, count in mem_counts.items():
            if count >= 2 and count > best_count:
                best_key = key
                best_count = count
        if best_key is not None:
            t["active"] = True
            t["cover_pos"] = mem_positions[best_key]
            t["cover_since"] = now
            t["suppress_until"] = now + SUPPRESS_DURATION
            t["scout_wave"] += 1
            t["scout_ids"] = pick_scouts(world, t["cover_pos"], now)
            t["scout_started"] = now
            t["grenade_wave"] = 0
            log(f"[战术] 激活火力压制：掩体位置 {[round(x,1) for x in t['cover_pos']]}，"
                f"敢死队 #{t['scout_ids']}（第 {t['scout_wave']} 波）")

    if not t["active"]:
        return

    # 敢死队完成或失败判定：
    # - 敢死 bot 全部死亡（不在存活列表）
    # - 或敢死超时（SCOUT_TIMEOUT 没看到敌人/没回来）
    scouts_alive = [sid for sid in t["scout_ids"] if sid in alive_ids]
    scouts_dead = len(scouts_alive) == 0
    scout_timeout = (now - t["scout_started"]) > SCOUT_TIMEOUT

    if (scouts_dead or scout_timeout) and t["phase"] == "suppress":
        # 敢死失败：派新手雷波（有手雷的 bot 扔手雷向掩体）。
        if t["grenade_wave"] < t["scout_wave"]:
            t["grenade_wave"] = t["scout_wave"]
            grenade_orders = queue_grenades(world, t["cover_pos"])
            if grenade_orders:
                # 把手雷指令注入订单队列（decide_bot 消费）。
                t["pending_grenades"] = grenade_orders
                log(f"[战术] 敢死队全灭/超时，{len(grenade_orders)} 个 bot 向掩体扔手雷！")

        # 扔完手雷 → 进入总攻阶段：所有 bot 压上掩体（不再派新敢死）。
        t["phase"] = "rush"
        t["rush_started"] = now
        t["scout_ids"] = []
        log("[战术] 手雷已投，全部 bot 总攻压上掩体！")

    # 总攻阶段：全体冲向掩体，直到到达或超时解散。
    if t["phase"] == "rush":
        if now - t["rush_started"] > SUPPRESS_DURATION * 0.6:
            log("[战术] 总攻结束，解散。")
            t["active"] = False
            t["phase"] = "suppress"
            t["cover_pos"] = None
            t["scout_ids"] = []
            for bs in world.states.values():
                bs.last_seen_target = None
        return

    # 压制超时（无手雷可用或一直没触发）：解散。
    if now > t["suppress_until"]:
        log("[战术] 压制结束，解散。")
        t["active"] = False
        t["phase"] = "suppress"
        t["cover_pos"] = None
        t["scout_ids"] = []
        for bs in world.states.values():
            bs.last_seen_target = None


def pick_scouts(world, cover_pos, now):
    """从在场 bot 中挑 SCOUT_COUNT 个当敢死侦查：
    优先离掩体近的，但**有手雷的 bot 不当敢死**（留作轰炸手），
    避免敢死全灭后无人扔手雷。"""
    candidates = []
    for b in world.bots:
        if b.get("h", 0) <= 0:
            continue
        items = b.get("items", {})
        if items.get("he", 0) > 0:
            continue   # 有手雷的不当敢死（留作轰炸手）
        d = dist(vec3(b["p"]), cover_pos)
        candidates.append((d, b["id"]))
    candidates.sort()
    picked = [c[1] for c in candidates[:SCOUT_COUNT]]
    # 若没几个无手雷 bot，宁可少派敢死也不用手雷 bot 冒险。
    # FF-85：原条件 `len(picked) < min(SCOUT_COUNT, 2) and len(picked) == 0` 中
    # 前半段是恒真冗余（len==0 时必然 < min），简化为 `not picked`。
    if not picked:
        # 完全没合适的：退回最近的 bot（总得有敢死）。
        fallback = []
        for b in world.bots:
            if b.get("h", 0) > 0:
                fallback.append((dist(vec3(b["p"]), cover_pos), b["id"]))
        fallback.sort()
        picked = [f[1] for f in fallback[:SCOUT_COUNT]]
    return picked


def get_tactic_role(world, bot):
    """返回 bot 在压制战术中的角色：scout（敢死侦查）/ rush（总攻）/ suppress（压制）/ None。
    FF-25：suppress 角色必须按距离过滤 —— 此前无条件返回 "suppress"，
    500m 外的 bot 也会被局部掩体记忆绑架（停止巡逻、朝掩体空放 24s）。
    只有距掩体在压制射程内的 bot 才参与压制；更远的继续自己的巡逻/记忆搜索。
    rush（总攻）是刻意全员命令，不受距离限制。"""
    # FF-24：tactics 跨线程读写，锁内读取。
    with world.lock:
        t = world.tactics
        if not t["active"]:
            return None
        # 总攻阶段：所有 bot 都压上掩体。
        if t["phase"] == "rush":
            return "rush"
        if bot["id"] in t["scout_ids"]:
            return "scout"
        cover = t["cover_pos"]
    # 压制参与者：距掩体在 SUPPRESS_PARTICIPATION_RANGE 内（射程内压制才有意义）。
    if cover is None:
        return None
    my_pos = vec3(bot.get("p"))
    if my_pos is None:
        return None
    if dist(my_pos, cover) > SUPPRESS_PARTICIPATION_RANGE:
        return None
    return "suppress"


def decide_rush(world, bot, st, mem_pos):
    """总攻：手雷已扔，全体 bot 朝掩体位置冲锋（到附近后由索敌接管战斗）。"""
    my_pos = vec3(bot["p"])
    d = dist(my_pos, mem_pos)

    # 到掩体附近（5m 内）→ 交给索敌（看到敌人就战斗，看不到则近身搜查）。
    # FF-70：总攻 bot 到 5m 内不再 moveTo 掩体本身（=站桩），改为环绕掩体小幅移动
    # 寻找能看到敌人的角度（与敢死侦查一致）。
    if d <= 5.0:
        to_cover = (mem_pos[0] - my_pos[0], 0.0, mem_pos[2] - my_pos[2])
        m = math.sqrt(to_cover[0] * to_cover[0] + to_cover[2] * to_cover[2])
        if m > 1e-6:
            tangent = (to_cover[2] / m, 0.0, -to_cover[0] / m)
            side = 1.0 if bot["id"] % 2 == 0 else -1.0
            orbit_point = (my_pos[0] + tangent[0] * side * 2.0,
                           my_pos[1],
                           my_pos[2] + tangent[2] * side * 2.0)
            return {
                "type": "orders",
                "bot": bot["id"],
                "shoot": 0,
                "look": list(mem_pos),
                "moveTo": list(orbit_point),
            }

        return {
            "type": "orders",
            "bot": bot["id"],
            "shoot": 0,
            "look": list(mem_pos),
            "moveTo": list(mem_pos),
        }

    # 冲锋：chaseTo 掩体（本地走 NavMesh/直线），开火朝掩体方向（边冲边压制）。
    return {
        "type": "orders",
        "bot": bot["id"],
        "shoot": 1,
        "look": list(mem_pos),
        "chaseTo": list(mem_pos),
    }


def consume_grenade(world, bot, mem_pos):
    """若本 bot 在待扔手雷列表里，原子消费并返回投掷指令字段（throw + tx 数组），
    否则返回 None。FF-23/FF-24：在锁内完成「检查-消费-返回」整个序列——
    不再经 last_grenade_throw 共享暂存字段（它会被并发 worker 互相覆盖，
    且 rush 阶段从不消费导致手雷死链）。"""
    with world.lock:
        t = world.tactics
        if not t["pending_grenades"]:
            return None
        if bot["id"] not in t["pending_grenades"]:
            return None

        # 从队列移除（每个 bot 只扔一次）。
        t["pending_grenades"] = [gid for gid in t["pending_grenades"] if gid != bot["id"]]
        log(f"[战术] bot #{bot['id']} 向掩体扔手雷 {[round(x,1) for x in mem_pos]}")

    # FF-19：tx 发 [x,y,z] 数组（C# TryParseVector 只接受 "[..." 形式）。
    return {"throw": "he", "tx": list(mem_pos)}


def decide_scout(world, bot, st, mem_pos):
    """敢死 bot：靠近掩体侦查。到达后若看到敌人交给战斗；没看到就环绕掩体寻找视线角度。"""
    my_pos = vec3(bot["p"])
    d = dist(my_pos, mem_pos)

    # 到掩体附近（5m 内）→ 环绕掩体小幅移动寻找能看到敌人的角度（不站桩）。
    # FF-71：此前 moveTo 掩体位置本身（bot 已在 5m 内、近乎到达），Dummy 到点即停，
    # 敢死 bot 站桩等超时；敌人躲在掩体后不出来时永远看不到。改为绕掩体走切线。
    if d <= 5.0:
        to_cover = (mem_pos[0] - my_pos[0], 0.0, mem_pos[2] - my_pos[2])
        m = math.sqrt(to_cover[0] * to_cover[0] + to_cover[2] * to_cover[2])
        if m > 1e-6:
            # 切线方向（垂直半径），按 bot id 奇偶选左右，偏移 2m 环绕。
            tangent = (to_cover[2] / m, 0.0, -to_cover[0] / m)
            side = 1.0 if bot["id"] % 2 == 0 else -1.0
            orbit_point = (my_pos[0] + tangent[0] * side * 2.0,
                           my_pos[1],
                           my_pos[2] + tangent[2] * side * 2.0)
            return {
                "type": "orders",
                "bot": bot["id"],
                "shoot": 0,
                "look": list(mem_pos),
                "moveTo": list(orbit_point),
            }

        return {
            "type": "orders",
            "bot": bot["id"],
            "shoot": 0,
            "look": list(mem_pos),
            "moveTo": list(mem_pos),
        }

    return {
        "type": "orders",
        "bot": bot["id"],
        "shoot": 0,
        "look": list(mem_pos),
        "chaseTo": list(mem_pos),
    }


def decide_suppress(world, bot, st, mem_pos):
    """压制 bot：**站远处**朝掩体方向开火压制（打不中人也压制），
    绝不前进（靠近侦查是敢死队的活）；只做横向分散站位 + 太近时后退。
    等手雷扔完进入总攻（rush）阶段才全体冲锋。"""
    my_pos = vec3(bot["p"])
    to_cover = (mem_pos[0] - my_pos[0], 0.0, mem_pos[2] - my_pos[2])
    # FF-71：统一用 2D 水平距离（与 to_cover 一致），避免 3D/2D 混用导致距离判断不一致；
    # 「太近后退」阈值改用 SUPPRESS_RADIUS（12m），与压制圈语义一致（此前硬编码 10.0）。
    d2 = math.sqrt(to_cover[0] * to_cover[0] + to_cover[2] * to_cover[2])
    right = (to_cover[2], 0.0, -to_cover[0]) if d2 > 0.01 else (1.0, 0.0, 0.0)
    rn = dist(right, (0.0, 0.0, 0.0)) or 1.0
    right = (right[0] / rn, 0.0, right[2] / rn)

    # 分散站位：按 bot id 奇偶左右分散（不聚堆，防被一锅端）。
    side = 1.0 if bot["id"] % 2 == 0 else -1.0
    spread = (right[0] * side * 3.0, 0.0, right[2] * side * 3.0)

    # 压制距离带：保持在掩体外 SUPPRESS_RADIUS(12m)~SUPPRESS_RADIUS*2(24m) 之间。
    # - 太近（<SUPPRESS_RADIUS）：后退拉开到压制圈外（不靠近）
    # - 正常：只横向分散，不前进
    # - 太远（>SUPPRESS_RADIUS*2）：也不前进（压制靠射程，走太近危险），仅横向分散
    if d2 < SUPPRESS_RADIUS:
        back = (-to_cover[0] / (d2 or 1.0), 0.0, -to_cover[2] / (d2 or 1.0))
        target = (my_pos[0] + back[0] * 4 + spread[0],
                  my_pos[1],
                  my_pos[2] + back[2] * 4 + spread[2])
    else:
        # 不前进：只横向小幅移动保持分散（压制位置固定）。
        target = (my_pos[0] + spread[0], my_pos[1], my_pos[2] + spread[2])

    orders = {
        "type": "orders",
        "bot": bot["id"],
        "shoot": 1,                       # 朝掩体方向开火压制
        "look": list(mem_pos),            # 瞄准掩体方向（打不中人也压制）
        "moveTo": list(target),           # 横向分散移动（绝不前进）
    }

    # 手雷指令：由 decide_bot 统一调用 consume_grenade 并合并进 orders
    # （FF-23：rush 阶段同样消费；这里不再读 last_grenade_throw 共享字段）。
    return orders


def queue_grenades(world, cover_pos):
    """找出在场有手雷（GrenadeHE）的 bot，返回要发手雷指令的 bot id 列表。
    手雷指令通过 World.tactics.pending_grenades 由 decide_bot 消费。"""
    result = []
    for b in world.bots:
        if b.get("h", 0) <= 0:
            continue
        # FF-75：vec3(b["p"]) 可能返回 None（脏快照），dist(None, ...) 崩溃。
        p = vec3(b.get("p"))
        if p is None:
            continue
        items = b.get("items", {})
        if items.get("he", 0) > 0:
            d = dist(p, cover_pos)
            if d <= GRENADE_DISTANCE:
                result.append(b["id"])
        if len(result) >= 3:   # 最多 3 个扔手雷，避免浪费
            break
    return result


def decide_chase(world, bot, target):
    """追击指令：规则寻路——发 chaseTo 让本地走 NavMesh 拐点/房间路径，否则直线追击。
    神经网络不参与寻路（专注内战打法）。"""
    aim = vec3(target.get("ap", target["p"]))
    tpos = vec3(target["p"])

    return {
        "type": "orders",
        "bot": bot["id"],
        "shoot": 0,
        "look": list(aim),
        "chaseTo": list(tpos),
    }


# ---- 网络层 ----

async def handle_client(reader, writer):
    world = World()
    peer = writer.get_extra_info("peername")
    log(f"[连接] 来自 {peer}")

    async def send(line):
        writer.write((line + "\n").encode("utf-8"))
        # FF-73：慢客户端使 drain 永久挂起（TCP 缓冲满且对端不读）→ 连接变活死。
        # 10s 超时后抛 TimeoutError → 外层 except 捕获 → 连接关闭重连（与 C# 端超时语义对齐）。
        await asyncio.wait_for(writer.drain(), timeout=10.0)

    async def pinger():
        try:
            while True:
                await send('{"type":"ping"}')
                await asyncio.sleep(PING_INTERVAL)
        except Exception:
            # 发送失败（对端断开/超时）：静默退出，主循环会处理连接关闭。
            pass

    ping_task = asyncio.create_task(pinger())
    snap_count = 0

    try:
        while True:
            # FF-74：readline 无超时 —— C# 端因 bot 数量为 0 长时间不推快照时，Python 端
            # 永久挂起等待，导致连接「活死」（无法被外部判死重连）。加 60s 超时：超时后
            # 退出循环走 finally 清理（与 C# 端 TimeoutSeconds 语义对齐）。
            try:
                raw = await asyncio.wait_for(reader.readline(), timeout=60.0)
            except asyncio.TimeoutError:
                log("[警告] 客户端 60s 未发送数据，断开连接")
                break
            if not raw:
                break
            line = raw.decode("utf-8", errors="ignore").strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                log("[警告] 收到非法 JSON 行，已忽略")
                continue

            # FF-69：json.loads 可能返回 list / 标量（如 "[1,2]" 或 "42"），
            # 此时 .get("type") 抛 AttributeError。只有 dict 才是合法协议消息。
            if not isinstance(msg, dict):
                log("[警告] 收到非对象 JSON 行，已忽略")
                continue

            mtype = msg.get("type")
            if mtype == "cfg":
                world.load_config(msg)
                log(f"[cfg] 已加载配置：房间 {len(world.rooms)}，路线房间 {len(world.routes)}，目标点房间 {len(world.targets)}")
            elif mtype == "trace":
                # 示教轨迹（bot follow 带领）：学习路线。
                handle_trace(world, msg)
            elif mtype == "penalty":
                # 严厉惩罚（卡房超时等）：惩罚神经网络。
                handle_penalty(world, msg)
            elif mtype == "snap":
                snap_count += 1
                world.bots = msg.get("bots", [])
                world.peers = msg.get("peers", [])
                loop = asyncio.get_running_loop()

                # 神经网络：先结算上一 tick 的奖励/经验（用最新快照的 kills/deaths/h），
                # 再让 decide_bot 用网络选路线。
                brain_tick(world)

                t0 = time.perf_counter()
                # FF-28：per-bot try/except —— 单个 bot 快照脏数据（缺 "p"/"vis"/"d" 字段、
                # 类型错误、坐标非数字）不能让整个 executor future 抛异常、进而杀掉整个连接
                # （此前任一 bot 的 KeyError/TypeError 都会让本 snap 全部 orders 丢失）。
                def _decide_one(world, bot):
                    try:
                        return decide_bot(world, bot)
                    except Exception as ex:
                        log(f"[警告] bot #{bot.get('id', '?')} 决策异常（脏快照）：{ex}，下发待命指令")
                        return {"type": "orders", "bot": bot.get("id", -1), "shoot": 0}

                results = await loop.run_in_executor(
                    world.executor,
                    lambda: [_decide_one(world, b) for b in world.bots],
                )
                elapsed_ms = (time.perf_counter() - t0) * 1000.0

                combat = sum(1 for o in results if o.get("shoot"))
                chase = sum(1 for o in results if "chaseTo" in o)
                patrol = len(results) - combat - chase

                # 每 10 个快照打印一次摘要，避免刷屏。
                if snap_count % 10 == 1:
                    log(f"[决策] #{snap_count} bots={len(results)} 战斗={combat} 追击={chase} 巡逻={patrol} 耗时={elapsed_ms:.1f}ms")

                # FF-70：patrol_warned 随 (bot_id, room) 组合无限增长（每次诊断新增一个），
                # 长时间运行后浪费内存。每 100 个快照清理一次（已警告过的组合无需保留）。
                if snap_count % 100 == 0:
                    world.patrol_warned.clear()

                if VERBOSE:
                    for o in results:
                        log(f"    bot={o.get('bot')} shoot={o.get('shoot')} moveTo={o.get('moveTo')} chaseTo={o.get('chaseTo')} look={o.get('look')}")

                for orders in results:
                    await send(json.dumps(orders, separators=(",", ":")))
            else:
                log(f"[警告] 未知消息类型：{mtype}")
    except asyncio.CancelledError:
        raise
    except ValueError as ex:
        # FF-04：readline 默认 64KB 行上限，大快照（20 bot x 25 敌人）极易超限，
        # 此时 StreamReader 抛 ValueError 并关闭连接。给出根因日志并继续等待重连。
        log(f"[错误] 收到超长行（>1MB）或行解析失败，连接已关闭：{ex}")
    except Exception as ex:
        log(f"[错误] 连接处理异常：{ex}")
    finally:
        ping_task.cancel()
        brain_save()
        world.executor.shutdown(wait=False)
        writer.close()
        try:
            await writer.wait_closed()
        except Exception:
            pass
        log(f"[断开] {peer}，共处理 {snap_count} 个快照")


async def main():
    # FF-31：默认只监听本机回环 —— 服务器无鉴权，任何能连上端口的主机都可投毒共享
    # 神经网络；AI 服务器与游戏同机部署（本机回环足够）。确需跨机器部署时，
    # 用环境变量 SCPBOT_HOST 显式覆盖（如 0.0.0.0），并自行承担暴露风险。
    host = os.environ.get("SCPBOT_HOST", "127.0.0.1")
    # FF-04：显式放宽行上限到 1MB（默认 64KB 对含完整敌人列表的大快照不够用，
    # 超限会让 readline 抛 ValueError 关闭连接，导致 AI 决策系统性失效）。
    server = await asyncio.start_server(handle_client, host, PORT, limit=1 << 20)
    log(f"[ScpBot AI 服务器] 已启动，监听 {host}:{PORT}（worker 上限按 CPU 核数自适应）")
    if _brain_enabled():
        st = status_json()
        log(f"[brain] 神经网络学习已启用：训练步 {st['step']}，ε={st['epsilon']}，样本 {st['samples']}")
        log("[brain] 提示：设置环境变量 SCPBOT_NO_BRAIN=1 可关闭学习")
    else:
        log("[brain] 神经网络学习未启用（SCPBOT_NO_BRAIN=1 或 numpy 缺失）")
    log(f"[提示] 详细日志：设置环境变量 SCPBOT_VERBOSE=1 后重启")
    log(f"[提示] Ctrl+C 退出")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n已退出（Ctrl+C）")
    except Exception as ex:
        import traceback
        print(f"\n[错误] {ex}")
        traceback.print_exc()
    finally:
        try:
            input("\n按回车键关闭窗口...")
        except (EOFError, KeyboardInterrupt):
            pass

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
                         "throw":"he"/"flash","tx","ty","tz","heal":0/1}
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
import time
from concurrent.futures import ThreadPoolExecutor

# 神经网络学习（路线选择）：可设置环境变量 SCPBOT_NO_BRAIN=1 关闭。
NO_BRAIN = os.environ.get("SCPBOT_NO_BRAIN", "0") == "1"
if not NO_BRAIN:
    try:
        import numpy  # noqa: F401  确保 numpy 可用（brain_route 依赖）
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
    return (float(p[0]), float(p[1]), float(p[2]))


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
        self.learn_prev_health = None  # 上一 tick 血量（算血量奖励）
        self.learn_prev_kills = 0
        self.learn_prev_deaths = 0
        self.learn_last_goal = None    # 上一 tick 的目标房间（路线变化时重置 episode）
        self.learn_prev_target_dist = None  # 上一 tick 目标距离（算靠近/远离奖励）


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
        self.executor = ThreadPoolExecutor(max_workers=max(4, min(32, 16)))

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
        st = self.states.get(bot_id)
        if st is None:
            st = BotState()
            self.states[bot_id] = st
        return st


# ---- 神经网络学习（持续学习寻路）----

BRAIN_TRAIN_INTERVAL = 2   # 每 N 个快照训练一批（更频繁的在线学习）
_brain_snap_counter = 0


def _brain_enabled():
    return not NO_BRAIN


def build_nav_candidates(world, bot, target):
    """构造寻路目标候选（动作空间）：
    候选 0 = 直线目标（目标当前位置）；
    候选 1..N = 各候选路线的终点房间中心（最多 3 条，含示教路线优先）。
    返回 (candidates, valid_indices)；无路线时只有直线候选。"""
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
        if len(valid) > 1:
            return candidates, valid

    # 常规路线候选（快照里的 routes）。
    routes = bot.get("routes")
    if routes:
        for i, route in enumerate(routes[:3]):
            if route:
                end_room = route[-1]
                room_info = world.rooms.get(end_room)
                if room_info and room_info.get("c"):
                    candidates.append(tuple(room_info["c"]))
                    valid.append(i + 1)

    return candidates, valid


def learn_pick_nav_target(world, bot, st, target, target_dist, visible_count):
    """用神经网络选寻路目标（ε-贪心）。返回 (target_pos, None 或 动作索引)。
    target_pos 为选中的寻路目标世界坐标；动作 None 表示不用网络（走规则直线追击）。"""
    if not _brain_enabled():
        return None, None

    candidates, valid = build_nav_candidates(world, bot, target)
    if len(valid) <= 1:
        # 没有路线可学（只有直线目标）：交给规则。
        return None, None

    state = build_state(bot, target_dist, visible_count, prev_dist=st.learn_prev_target_dist)
    brain = get_brain()

    # 判断是探索还是利用（用于统计）。
    exploring = random.random() < brain.epsilon
    action = brain.choose_action(state, valid)
    chosen = candidates[action]

    # 神经网络运行统计（控制台输出用）。
    world.nn_stats["decisions"] += 1
    if exploring:
        world.nn_stats["explore"] += 1
    else:
        world.nn_stats["exploit"] += 1

    # Q 值统计（采样当前状态下的 Q 分布）。
    try:
        q = brain.predict(state)
        world.nn_stats["q_samples"] += 1
        world.nn_stats["q_sum"] += float(np.mean(q))
        world.nn_stats["q_max_sum"] += float(np.max(q))
    except Exception:
        pass

    # 记录本次选择，供下一 tick 结算奖励。
    st.learn_last_state = state
    st.learn_last_action = action
    st.learn_prev_target_dist = target_dist
    return chosen, action


def learn_settle_reward(world, bot, st):
    """每 tick 结算上一动作的奖励并存入经验回放（DQN 持续在线学习）。
    奖励：击杀 +1、阵亡 -1、存活每 tick +0.01、血量上升 +0.02*Δ、受伤害 -0.02*Δ、
    靠近目标 +0.05、远离目标 -0.05（寻路学习的核心信号）。
    路线目标变化（routes 变化）或死亡时视为 episode 结束（done=True）。"""
    if not _brain_enabled() or st.learn_last_state is None or st.learn_last_action is None:
        return

    h = bot.get("h", 0.0)
    kills = bot.get("kills", 0)
    deaths = bot.get("deaths", 0)

    reward = 0.0

    # 击杀 / 阵亡增量。
    reward += (kills - st.learn_prev_kills) * 1.0
    reward += (deaths - st.learn_prev_deaths) * -1.0

    # 血量变化奖励（治疗正、受伤害负）。
    if st.learn_prev_health is not None:
        dh = h - st.learn_prev_health
        reward += dh * 0.02

    # 存活奖励（每 tick 小额正奖励，鼓励不送死）。
    reward += 0.01

    # 靠近目标奖励：本 tick 与目标的距离变化（选对寻路点 → 更接近 → 正奖励）。
    target = choose_target(bot.get("enemies", []))
    if target is not None and st.learn_prev_target_dist is not None:
        cur_d = target.get("d", 0.0)
        reward += 0.05 if cur_d < st.learn_prev_target_dist else -0.05

    # episode 结束判定：路线集合变化（目标换了）或死亡。
    routes = bot.get("routes") or []
    goal_changed = st.learn_last_goal is not None and routes != st.learn_last_goal
    done = goal_changed or deaths > st.learn_prev_deaths or h <= 0.0

    # 下一状态（用于 DQN 的 next_state；done 时用零向量）。
    next_state = st.learn_last_state
    if not done:
        tpos = vec3(target["p"]) if target else None
        tdist = dist(vec3(bot["p"]), tpos) if tpos else 0.0
        vis_count = sum(1 for e in bot.get("enemies", []) if e.get("vis"))
        next_state = build_state(bot, tdist, vis_count, prev_dist=cur_d if target else None)

    brain = get_brain()
    brain.store(st.learn_last_state, st.learn_last_action, reward, next_state, done)
    brain.total_reward += reward
    world.nn_stats["rewards"] += reward

    # 更新基线。
    st.learn_prev_health = h
    st.learn_prev_kills = kills
    st.learn_prev_deaths = deaths
    st.learn_last_goal = routes
    if target is not None:
        st.learn_prev_target_dist = target.get("d", 0.0)


def brain_tick(world):
    """训练调度：每 BRAIN_TRAIN_INTERVAL 个快照，对所有 bot 结算奖励并做一步训练。
    同时定期输出神经网络运行状态（决策统计 / Q 值 / 示教学习 / 惩罚），便于观察学习进程。"""
    global _brain_snap_counter
    if not _brain_enabled():
        return

    for bot in world.bots:
        st = world.get_state(bot["id"])
        learn_settle_reward(world, bot, st)

    _brain_snap_counter += 1
    if _brain_snap_counter >= BRAIN_TRAIN_INTERVAL:
        _brain_snap_counter = 0
        brain = get_brain()
        loss = brain.train_step()
        if loss is not None:
            # 每次训练输出：步数 / loss / ε / 样本 / 累计奖励。
            log(f"[brain] 训练步={brain.step} loss={loss:.4f} ε={brain.epsilon:.3f} "
                f"样本={brain.samples} 累计奖励={brain.total_reward:.2f}")

    # 每 50 个快照输出一次神经网络综合状态（含决策/探索/利用/Q 值/示教/惩罚统计）。
    if _brain_snap_counter == 0 and world.nn_stats["decisions"] > 0:
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
    if route not in world.taught_routes[key]:
        world.taught_routes[key].append(route)

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

    amount = float(msg.get("amount", -5.0))
    reason = msg.get("reason", "unknown")
    team = msg.get("team", "?")

    world.nn_stats["penalties"] += 1
    world.nn_stats["penalty_total"] += amount

    # 给经验回放中最近的样本追加惩罚（严厉惩罚，让网络学到「卡房=坏行为」）。
    brain = get_brain()
    if brain.replay:
        # 对所有 bot 最近 20 条经验追加惩罚（简化：全局追加，强化惩罚信号）。
        n = min(20, len(brain.replay))
        for i in range(len(brain.replay) - n, len(brain.replay)):
            state, action, reward, next_state, done = brain.replay[i]
            brain.replay[i] = (state, action, reward + amount, next_state, done)

    brain.total_reward += amount

    log(f"[惩罚] 阵营 {team} 卡房超时（{reason}），神经网络惩罚 {amount:.1f}，"
        f"累计惩罚 {world.nn_stats['penalty_total']:.1f}")


# ---- 决策逻辑 ----

def choose_target(enemies):
    """选最近敌人（无论是否可见，可见优先）；无敌人返回 None。"""
    if not enemies:
        return None
    visible = [e for e in enemies if e.get("vis")]
    pool = visible if visible else enemies
    return min(pool, key=lambda e: e.get("d", math.inf))


def decide_combat(world, bot, st, target):
    """战斗决策：走位状态机（chase/orbit/retreat）+ 开火。返回 orders 字典。"""
    pos = vec3(bot["p"])
    tpos = vec3(target["p"])
    aim = vec3(target.get("ap", target["p"]))
    d = dist(pos, tpos)
    visible = bool(target.get("vis"))
    now = time.monotonic()

    orders = {"type": "orders", "bot": bot["id"]}

    # 开火：可见且射程内。
    shoot = visible and d <= ATTACK_RANGE
    orders["shoot"] = 1 if shoot else 0
    orders["look"] = list(aim)

    # 状态机：距离 > 理想+容差 -> chase，否则 orbit。
    next_state = "chase" if d > PREFERRED_RANGE + RANGE_TOLERANCE else "orbit"
    if next_state != st.combat_state:
        st.combat_state = next_state
        if next_state == "orbit":
            st.orbit_direction = random.choice((-1, 1))

    # 横移方向周期性翻转（模拟真人反复横跳）。
    if now >= st.next_strafe_flip:
        st.strafe_direction = random.choice((-1, 1))
        st.next_strafe_flip = now + random.uniform(STRAFE_FLIP_MIN, STRAFE_FLIP_MAX)

    move_dir = None
    if st.combat_state == "orbit":
        if d < ORBIT_RETREAT_DISTANCE:
            # 贴脸：不再后撤（避免双方僵持），朝目标压上。
            move_dir = horiz((tpos[0] - pos[0], tpos[1] - pos[1], tpos[2] - tpos[2]))
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

    # 到达当前点则推进（到达判定 1.5m，与本地 WaypointReachDistance 一致）。
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
    """对单个机器人做一次完整决策。"""
    st = world.get_state(bot["id"])
    enemies = bot.get("enemies", [])
    target = choose_target(enemies)

    if target is None:
        return decide_patrol(world, bot, st)

    visible = bool(target.get("vis"))
    d = target.get("d", math.inf)

    if visible and d <= ATTACK_RANGE:
        # 战斗：可见且射程内 -> 开火 + 走位（moveTo）。
        return decide_combat(world, bot, st, target)

    # 追击：不可见或超范围 -> 神经网络选寻路目标（直线/示教路线/常规路线终点），再发 chaseTo。
    nav_target, action = learn_pick_nav_target(
        world, bot, st, target, d, sum(1 for e in enemies if e.get("vis")))
    return decide_chase(world, bot, target, nav_target)


def decide_chase(world, bot, target, nav_target=None):
    """追击指令：发 chaseTo 让本地走 NavMesh 拐点绕山/绕楼，否则直线追击。
    若神经网络选定了寻路目标点（nav_target），则直接以该点作为追击目标
    （可能是直线目标、示教路线终点或常规路线终点）。"""
    aim = vec3(target.get("ap", target["p"]))
    tpos = vec3(nav_target) if nav_target is not None else vec3(target["p"])

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
        await writer.drain()

    async def pinger():
        while True:
            await send('{"type":"ping"}')
            await asyncio.sleep(PING_INTERVAL)

    ping_task = asyncio.create_task(pinger())
    snap_count = 0

    try:
        while True:
            raw = await reader.readline()
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
                results = await loop.run_in_executor(
                    world.executor,
                    lambda: [decide_bot(world, b) for b in world.bots],
                )
                elapsed_ms = (time.perf_counter() - t0) * 1000.0

                combat = sum(1 for o in results if o.get("shoot"))
                chase = sum(1 for o in results if "chaseTo" in o)
                patrol = len(results) - combat - chase

                # 每 10 个快照打印一次摘要，避免刷屏。
                if snap_count % 10 == 1:
                    log(f"[决策] #{snap_count} bots={len(results)} 战斗={combat} 追击={chase} 巡逻={patrol} 耗时={elapsed_ms:.1f}ms")

                if VERBOSE:
                    for o in results:
                        log(f"    bot={o.get('bot')} shoot={o.get('shoot')} moveTo={o.get('moveTo')} chaseTo={o.get('chaseTo')} look={o.get('look')}")

                for orders in results:
                    await send(json.dumps(orders, separators=(",", ":")))
            else:
                log(f"[警告] 未知消息类型：{mtype}")
    except asyncio.CancelledError:
        raise
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
    server = await asyncio.start_server(handle_client, "0.0.0.0", PORT)
    log(f"[ScpBot AI 服务器] 已启动，监听 0.0.0.0:{PORT}（worker 上限按 CPU 核数自适应）")
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

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
                                "enemies":[{"n","p","ap","d","t","vis"}]}],
                        "peers":[...]}
    本服务 -> 插件:
        {"type":"orders","bot":N,"shoot":0/1,"look":[x,y,z],"moveTo":[x,y,z]}
        {"type":"ping"}

用法：python ai_server.py [端口]
依赖：Python 3.8+（仅标准库）
"""
import asyncio
import json
import math
import os
import random
import sys
import time
from concurrent.futures import ThreadPoolExecutor

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
ORBIT_RETREAT_DISTANCE = 4.0   # 贴脸后撤距离
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
            # 贴脸后撤：远离目标。
            move_dir = horiz((pos[0] - tpos[0], pos[1] - tpos[1], pos[2] - tpos[2]))
        else:
            move_dir = build_orbit_direction(pos, tpos, d, st)
    else:
        move_dir = build_chase_direction(pos, tpos, st)

    if move_dir is None:
        return orders  # 距离过近无法定方向，原地待命

    # moveTo = 当前位置 + 走位方向 * 步长（本地 Move 会朝该点走并做障碍绕行）。
    orders["moveTo"] = list(add(pos, move_dir, MOVE_STEP))
    return orders


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

    # 追击：不可见或超范围 -> chaseTo（本地算地表 NavMesh 拐点 / 直线）。
    return decide_chase(world, bot, target)


def decide_chase(world, bot, target):
    """追击指令：发 chaseTo 让本地走 NavMesh 拐点绕山/绕楼，否则直线追击。"""
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
            elif mtype == "snap":
                snap_count += 1
                world.bots = msg.get("bots", [])
                world.peers = msg.get("peers", [])
                loop = asyncio.get_running_loop()
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

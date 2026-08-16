#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
brain_route.py —— 寻路学习神经网络（轻量 DQN，持续在线学习）
================================================================
目标：教 AI 持续学习寻路——追击目标时选择「下一个寻路目标点」（4 选 1 离散动作）：
    动作 0: 直线目标（目标当前位置）
    动作 1: 路线 0 的终点房间中心
    动作 2: 路线 1 的终点房间中心
    动作 3: 路线 2 的终点房间中心
（候选不足时 valid_actions 收缩，网络只在合法动作里选。）

设计（与 ai_server.py 协作）：
- 状态特征（每 bot 每 tick 构造一次，14 维）：
     0  血量比例 (0~1)
     1  目标距离 / 100
     2  当前路线长度 / 20
     3  最短路线长度 / 20
     4  路线数 / 3
     5  击杀 / 10
     6  阵亡 / 10
     7  可见敌人数 / 10
     8  手榴弹数 / 3
     9  闪光弹数 / 3
    10  目标距离变化率（>0 靠近，<0 远离）——寻路学习的核心信号
    11  最近门距离 / 20（越小门越近）
    12  是否隔墙不可见（0/1）
    13  是否与目标同房间（0/1）
- 学习：在线 DQN —— 经验回放（容量 4000）+ 目标网络（每 200 步同步）+ ε-贪心
  （ε 从 0.3 线性衰减到 0.1）
- 奖励（ai_server 每 tick 计算）：
    击杀 +1、阵亡 -1、存活每 tick +0.01、血量上升 +0.02*Δ、受伤害 -0.02*Δ、
    靠近目标 +0.05、远离目标 -0.05  ← 寻路学习的核心奖励
- 持久化：brain_route.npz（numpy），启动加载，训练中每 300 步自动保存 + 断开时保存。

依赖：numpy（pip install numpy）。首次运行自动创建随机权重文件。
"""
import os
import random
import threading

import numpy as np

MODEL_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "brain_route.npz")

# 网络结构
STATE_DIM = 14
HIDDEN_DIM = 24
# 动作数 = 最大候选数（直线目标 + 最多 3 条路线终点 + 4 个方向偏移点 = 8 上限，取 8）。
# choose_action 用候选索引直接取 Q 值，valid 里的值必须 < ACTION_DIM。
ACTION_DIM = 8

# 学习参数
LEARNING_RATE = 0.005
GAMMA = 0.9
EPSILON_START = 0.3
EPSILON_END = 0.1
EPSILON_DECAY = 0.9995
REPLAY_CAPACITY = 4000
BATCH_SIZE = 64
TARGET_SYNC_STEPS = 200
SAVE_EVERY_STEPS = 300


def _rand_layer(fan_in, fan_out):
    """Xavier 初始化权重 + 零偏置。"""
    limit = np.sqrt(6.0 / (fan_in + fan_out))
    return (np.random.uniform(-limit, limit, (fan_in, fan_out)),
            np.zeros(fan_out))


class RouteBrain:
    """寻路学习神经网络（numpy 手写 MLP，双网络：在线 + 目标）。"""

    def __init__(self, state_dim=STATE_DIM, hidden_dim=HIDDEN_DIM, action_dim=ACTION_DIM):
        self.state_dim = state_dim
        self.hidden_dim = hidden_dim
        self.action_dim = action_dim
        self._lock = threading.Lock()

        self.w1, self.b1 = _rand_layer(state_dim, hidden_dim)
        self.w2, self.b2 = _rand_layer(hidden_dim, action_dim)
        self.tw1, self.tb1 = self.w1.copy(), self.b1.copy()
        self.tw2, self.tb2 = self.w2.copy(), self.b2.copy()

        self.step = 0
        self.epsilon = EPSILON_START
        self.replay = []
        self.replay_pos = 0
        self.total_reward = 0.0
        self.samples = 0

        self._load()

    # ---- 前向 ----

    def _forward(self, x, w1, b1, w2, b2):
        h = np.maximum(0.0, x @ w1 + b1)
        return h @ w2 + b2

    def predict(self, state, use_target=False):
        """返回各动作的 Q 值（state 为长度为 state_dim 的 list/tuple）。"""
        with self._lock:
            x = np.asarray(state, dtype=np.float32).reshape(1, -1)
            if use_target:
                return self._forward(x, self.tw1, self.tb1, self.tw2, self.tb2)[0]
            return self._forward(x, self.w1, self.b1, self.w2, self.b2)[0]

    def choose_action(self, state, valid_actions):
        """ε-贪心选动作。valid_actions：实际可选的寻路目标索引列表（如 [0,1,3]）。"""
        if not valid_actions:
            return 0

        if random.random() < self.epsilon:
            return random.choice(valid_actions)

        q = self.predict(state)
        best = None
        best_q = float("-inf")
        for a in valid_actions:
            if q[a] > best_q:
                best_q = q[a]
                best = a
        return best if best is not None else random.choice(valid_actions)

    # ---- 训练 ----

    def store(self, state, action, reward, next_state, done):
        """存入经验回放（环形缓冲）。"""
        with self._lock:
            if len(self.replay) < REPLAY_CAPACITY:
                self.replay.append((state, action, reward, next_state, done))
            else:
                self.replay[self.replay_pos] = (state, action, reward, next_state, done)
                self.replay_pos = (self.replay_pos + 1) % REPLAY_CAPACITY
            self.samples += 1

    def train_step(self):
        """从回放池采样一个小批量，做一步 DQN 更新。返回 loss 或 None（样本不足）。"""
        with self._lock:
            if len(self.replay) < BATCH_SIZE:
                return None

            batch = random.sample(self.replay, BATCH_SIZE)
            states = np.asarray([s for s, _, _, _, _ in batch], dtype=np.float32)
            actions = np.asarray([a for _, a, _, _, _ in batch], dtype=np.int64)
            rewards = np.asarray([r for _, _, r, _, _ in batch], dtype=np.float32)
            next_states = np.asarray([ns for _, _, _, ns, _ in batch], dtype=np.float32)
            dones = np.asarray([1.0 if d else 0.0 for _, _, _, _, d in batch], dtype=np.float32)

            w1, b1, w2, b2 = self.w1, self.b1, self.w2, self.b2

            h = np.maximum(0.0, states @ w1 + b1)
            q_all = h @ w2 + b2
            q = q_all[np.arange(BATCH_SIZE), actions]

            th = np.maximum(0.0, next_states @ self.tw1 + self.tb1)
            tq_all = th @ self.tw2 + self.tb2
            tq = np.max(tq_all, axis=1)
            target = rewards + GAMMA * tq * (1.0 - dones)

            dq = 2.0 * (q - target) / BATCH_SIZE
            one_hot = np.zeros((BATCH_SIZE, ACTION_DIM), dtype=np.float32)
            one_hot[np.arange(BATCH_SIZE), actions] = 1.0
            dq_out = dq[:, None] * one_hot

            dh = dq_out @ w2.T
            dh[h <= 0.0] = 0.0

            gw2 = h.T @ dq_out
            gb2 = dq_out.sum(axis=0)
            gw1 = states.T @ dh
            gb1 = dh.sum(axis=0)

            w1 -= LEARNING_RATE * gw1
            b1 -= LEARNING_RATE * gb1
            w2 -= LEARNING_RATE * gw2
            b2 -= LEARNING_RATE * gb2

            self.step += 1
            self.epsilon = max(EPSILON_END, self.epsilon * EPSILON_DECAY)

            if self.step % TARGET_SYNC_STEPS == 0:
                self.tw1, self.tb1 = w1.copy(), b1.copy()
                self.tw2, self.tb2 = w2.copy(), b2.copy()
            if self.step % SAVE_EVERY_STEPS == 0:
                self.save()

            loss = float(np.mean((q - target) ** 2))
            return loss

    # ---- 持久化 ----

    def save(self):
        try:
            with self._lock:
                np.savez(
                    MODEL_FILE,
                    w1=self.w1, b1=self.b1, w2=self.w2, b2=self.b2,
                    tw1=self.tw1, tb1=self.tb1, tw2=self.tw2, tb2=self.tb2,
                    step=self.step, epsilon=self.epsilon,
                )
        except Exception as ex:  # pragma: no cover
            print(f"[brain] 保存模型失败: {ex}")

    def _load(self):
        if not os.path.exists(MODEL_FILE):
            print(f"[brain] 未找到模型 {MODEL_FILE}，使用随机初始化（首次运行）。")
            return
        try:
            with np.load(MODEL_FILE) as f:
                w1 = f["w1"]
                # 维度校验：网络结构变化（如状态从 10 维升到 14 维）时旧模型不兼容，
                # 自动丢弃并重新随机初始化，避免 matmul 维度不匹配崩溃。
                if w1.shape != (self.state_dim, self.hidden_dim):
                    print(f"[brain] 模型维度不匹配（w1={w1.shape}，期望 "
                          f"({self.state_dim}, {self.hidden_dim})），已丢弃旧模型，重新随机初始化。")
                    return
                self.w1, self.b1 = w1, f["b1"]
                self.w2, self.b2 = f["w2"], f["b2"]
                self.tw1, self.tb1 = f["tw1"], f["tb1"]
                self.tw2, self.tb2 = f["tw2"], f["tb2"]
                self.step = int(f["step"])
                self.epsilon = float(f["epsilon"])
            print(f"[brain] 已加载模型（步数 {self.step}，ε={self.epsilon:.3f}）。")
        except Exception as ex:
            print(f"[brain] 模型加载失败（{ex}），使用随机初始化。")


# ---- 状态特征构造（14 维）----

def build_state(bot, target_dist, visible_count, prev_dist=None, nearest_door_dist=None):
    """从 bot 快照构造 14 维状态特征（全部分量归一化到 0~1 附近）。
    prev_dist：上一 tick 目标距离（算靠近/远离信号）；nearest_door_dist：最近门距离。"""
    h = bot.get("h", 0) / 100.0
    items = bot.get("items", {})
    he = items.get("he", 0)
    flash = items.get("flash", 0)
    kills = bot.get("kills", 0)
    deaths = bot.get("deaths", 0)

    routes = bot.get("routes") or []
    route_count = len(routes)
    cur_len = 0
    min_len = 0
    if route_count > 0:
        lengths = [len(r) for r in routes]
        min_len = min(lengths)
        cur_len = lengths[0]

    # 目标距离变化率：>0 靠近，<0 远离（clamp 到 [-1,1]）。
    dist_delta = 0.0
    if prev_dist is not None and target_dist is not None:
        dist_delta = max(-1.0, min(1.0, (prev_dist - target_dist) / 5.0))

    # 最近门距离（无数据时给 1.0 = 很远）。
    door_feat = 1.0
    if nearest_door_dist is not None:
        door_feat = min(1.0, nearest_door_dist / 20.0)

    # 隔墙不可见：目标存在但 enemies 里 vis=0（或目标不可见）。
    hidden = 1.0
    enemies = bot.get("enemies", [])
    target = next((e for e in enemies if e.get("vis")), None)
    if target is not None:
        hidden = 0.0

    # 与目标同房间：bot 的 r 与目标房间相同（快照没有目标房间，用敌人列表推断不可靠，
    # 简化：路由存在且目标距离近时认为同房可能性高；这里用路线数>0 表示有明确目标房间）。
    same_room = 1.0 if route_count > 0 and target_dist is not None and target_dist < 30.0 else 0.0

    return [
        min(1.0, max(0.0, h)),
        min(1.0, (target_dist or 0.0) / 100.0),
        min(1.0, cur_len / 20.0),
        min(1.0, min_len / 20.0),
        min(1.0, route_count / 3.0),
        min(1.0, kills / 10.0),
        min(1.0, deaths / 10.0),
        min(1.0, visible_count / 10.0),
        min(1.0, he / 3.0),
        min(1.0, flash / 3.0),
        dist_delta,
        door_feat,
        hidden,
        same_room,
    ]


# ---- 便捷接口（供 ai_server 调用）----

_brain = None
_brain_lock = threading.Lock()


def get_brain():
    global _brain
    with _brain_lock:
        if _brain is None:
            _brain = RouteBrain()
        return _brain


def save_brain():
    b = get_brain()
    b.save()


def status_json():
    """返回训练状态摘要（供 ai_server 打印/调试）。"""
    b = get_brain()
    return {
        "step": b.step,
        "epsilon": round(b.epsilon, 4),
        "samples": b.samples,
        "total_reward": round(b.total_reward, 2),
        "state_dim": b.state_dim,
        "action_dim": b.action_dim,
    }

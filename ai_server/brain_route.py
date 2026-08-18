#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
brain_route.py —— 内战战斗决策学习神经网络（轻量 DQN，持续在线学习）
=====================================================================
目标：教 AI 学习「bot vs bot 内战打法」——因地制宜找出最优战斗策略。
动作 = 16 维（8 走位 × 2 开火），网络同时决定「怎么走 + 何时开枪」：
    走位部分（action % 8）：
        0: 朝目标猛冲        1: 偏左 30° 冲    2: 偏右 30° 冲
        3: 左横移            4: 右横移          5: 后退（拉开）
        6: 原地保持          7: 贴身压上
    开火部分（action >= 8）：8..15 表示开火，0..7 表示不开火。
    例：15 = 贴身压上 + 开火；6 = 原地不开火；14 = 原地开火（站桩射击）。

设计（与 ai_server.py 协作）：
- 状态特征（每 bot 每 tick 构造一次，16 维）：
     0  血量比例 (0~1)
     1  目标距离 / 100
     2  可见敌人数 / 10
     3  是否室内（0=地表 1=室内；因地制宜的关键）
     4  我方存活 bot 数 / 20
     5  敌方存活 bot 数 / 20
     6  击杀 / 10
     7  阵亡 / 10
     8  手榴弹数 / 3
     9  闪光弹数 / 3
    10  目标距离变化率（>0 靠近，<0 远离）
    11  是否隔墙不可见（0/1）
    12  是否有可抛投掷物（0/1）
    13  弹药是否充足（0/1）
    14  与目标同房间（0/1）
    15  最近门距离 / 20（越小门越近）
- 学习：在线 DQN —— 经验回放（容量 4000）+ 目标网络（每 200 步同步）+ ε-贪心
  （ε 从 0.3 线性衰减到 0.1；探索是网络自身机制，不是规则接管）
- 奖励（ai_server 每 tick 计算，内战核心信号，惩罚严厉）：
    击杀 +1、阵亡 -3（送死重罚）、存活每 tick +0.01、
    血量上升 +0.15*Δ（治疗/回血）、受伤害 -0.15*Δ（被打很疼）、
    靠近目标 +0.05、远离目标 -0.1（乱跑/逃跑重罚）
- 持久化：brain_route.npz（numpy），启动加载，训练中每 300 步自动保存 + 断开时保存。

依赖：numpy（pip install numpy）。首次运行自动创建随机权重文件。
"""
import os
import random
import threading

import numpy as np

MODEL_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "brain_route.npz")

# 网络结构
STATE_DIM = 17
HIDDEN_DIM = 24
ACTION_DIM = 16   # 8 走位 × 2 开火（网络全面接管战斗：怎么走 + 何时开枪）

# 学习参数
LEARNING_RATE = 0.005
GAMMA = 0.9
EPSILON_START = 0.3
EPSILON_END = 0.1
EPSILON_DECAY = 0.9995
# 回放容量/批大小调小：样本虽多（每 tick 一个），环形缓冲封顶 + 小批量训练，
# 避免 Python 端内存/CPU 过载（5000+ 样本时 cmd 崩溃）。
REPLAY_CAPACITY = 1000
BATCH_SIZE = 32
TARGET_SYNC_STEPS = 200
SAVE_EVERY_STEPS = 300
SAVE_EVERY_SAMPLES = 100  # 每 N 个新样本自动保存一次（权重 + 样本）


def _rand_layer(fan_in, fan_out):
    """Xavier 初始化权重 + 零偏置。"""
    limit = np.sqrt(6.0 / (fan_in + fan_out))
    return (np.random.uniform(-limit, limit, (fan_in, fan_out)),
            np.zeros(fan_out))


class RouteBrain:
    """内战战斗决策学习神经网络（numpy 手写 MLP，双网络：在线 + 目标）。"""

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

    def choose_action(self, state, valid_actions=None):
        """ε-贪心选动作。valid_actions 为 None 时用全部动作（0..action_dim-1）。"""
        if valid_actions is None:
            valid_actions = list(range(self.action_dim))
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
        """存入经验回放（环形缓冲）。每 SAVE_EVERY_SAMPLES 个样本自动保存一次
        （权重 + 样本），防止长时间运行后崩溃丢失全部学习进度。"""
        with self._lock:
            if len(self.replay) < REPLAY_CAPACITY:
                self.replay.append((state, action, reward, next_state, done))
            else:
                self.replay[self.replay_pos] = (state, action, reward, next_state, done)
                self.replay_pos = (self.replay_pos + 1) % REPLAY_CAPACITY
            self.samples += 1

            # 样本节流自动保存：每 SAVE_EVERY_SAMPLES 个样本存一次。
            if self.samples % SAVE_EVERY_SAMPLES == 0:
                self._save_locked()

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
                # 注意：此处已持有 self._lock（train_step 全程持锁），必须调用 _save_locked()
                # 直接保存；调用 self.save() 会二次获取非可重入锁导致死锁（FF-01）。
                self._save_locked()

            loss = float(np.mean((q - target) ** 2))
            return loss

    # ---- 持久化 ----

    def save(self):
        """保存网络权重 + 训练进度 + 经验回放（样本）到 npz（外部调用，带锁）。"""
        with self._lock:
            self._save_locked()

    def _save_locked(self):
        """保存实现（调用方需持有 _lock）。"""
        try:
            replay = self.replay
            n = len(replay)
            if n > 0:
                states = np.asarray([s for s, _, _, _, _ in replay], dtype=np.float32)
                actions = np.asarray([a for _, a, _, _, _ in replay], dtype=np.int64)
                rewards = np.asarray([r for _, _, r, _, _ in replay], dtype=np.float32)
                next_states = np.asarray([ns for _, _, _, ns, _ in replay], dtype=np.float32)
                dones = np.asarray([1.0 if d else 0.0 for _, _, _, _, d in replay], dtype=np.float32)
            else:
                states = np.zeros((0, self.state_dim), dtype=np.float32)
                actions = np.zeros((0,), dtype=np.int64)
                rewards = np.zeros((0,), dtype=np.float32)
                next_states = np.zeros((0, self.state_dim), dtype=np.float32)
                dones = np.zeros((0,), dtype=np.float32)

            np.savez(
                MODEL_FILE,
                w1=self.w1, b1=self.b1, w2=self.w2, b2=self.b2,
                tw1=self.tw1, tb1=self.tb1, tw2=self.tw2, tb2=self.tb2,
                step=self.step, epsilon=self.epsilon,
                replay_states=states, replay_actions=actions, replay_rewards=rewards,
                replay_next_states=next_states, replay_dones=dones,
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
                # 维度校验：网络结构变化（如状态从 14 维升到 16 维）时旧模型不兼容，
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

                # 恢复经验回放（样本）。
                rs = f["replay_states"]
                if rs.shape[0] > 0:
                    ra = f["replay_actions"]
                    rr = f["replay_rewards"]
                    rn = f["replay_next_states"]
                    rd = f["replay_dones"]
                    self.replay = []
                    for i in range(rs.shape[0]):
                        self.replay.append((
                            rs[i].tolist(), int(ra[i]), float(rr[i]),
                            rn[i].tolist(), bool(rd[i]),
                        ))
                    self.samples = len(self.replay)
                    self.replay_pos = 0
            print(f"[brain] 已加载模型（步数 {self.step}，ε={self.epsilon:.3f}，"
                  f"恢复样本 {len(self.replay)}）。")
        except Exception as ex:
            print(f"[brain] 模型加载失败（{ex}），使用随机初始化。")


# ---- 状态特征构造（16 维，内战战斗决策）----

def build_state(bot, target, target_dist, visible_count, world, prev_dist=None, nearest_door_dist=None):
    """从 bot 快照构造 16 维内战战斗状态特征（全部分量归一化）。
    world：World 实例（算双方存活 bot 数）。prev_dist：上一 tick 目标距离（靠近/远离信号）。"""
    h = bot.get("h", 0) / 100.0
    items = bot.get("items", {})
    he = items.get("he", 0)
    flash = items.get("flash", 0)
    kills = bot.get("kills", 0)
    deaths = bot.get("deaths", 0)

    # 我方/敌方存活 bot 数（内战关键：人数比）。
    my_team = bot.get("t", "")
    friend_count = 0
    enemy_count = 0
    for b in world.bots:
        if b.get("h", 0) <= 0:
            continue
        if b.get("t") == my_team:
            friend_count += 1
        else:
            enemy_count += 1

    # 是否室内：房间名前缀（Lcz/Hcz/Ez 为设施内，Outside 为地表）。
    room = bot.get("r") or ""
    indoor = 1.0 if room.upper().startswith(("LCZ", "HCZ", "EZ")) else 0.0

    # 目标距离变化率：>0 靠近，<0 远离。
    dist_delta = 0.0
    if prev_dist is not None and target_dist is not None:
        dist_delta = max(-1.0, min(1.0, (prev_dist - target_dist) / 5.0))

    # 弹药是否充足（启发式：有 he/flash 或血量高时视作可战）。
    has_ammo = 1.0 if (he + flash > 0 or h > 0.3) else 0.0

    # 最近门距离（无数据给 1.0 = 很远）。
    door_feat = 1.0
    if nearest_door_dist is not None:
        door_feat = min(1.0, nearest_door_dist / 20.0)

    # 隔墙不可见：目标存在但不可见。
    hidden = 0.0
    if target is not None and not bool(target.get("vis")):
        hidden = 1.0

    # 与目标同房间：目标房间无从获取，用「目标距离近且不可见」近似。
    same_room = 1.0 if hidden and target_dist is not None and target_dist < 30.0 else 0.0

    # 掩体状态（插件快照 cover）：与目标视线被遮挡（岩石/建筑/箱子后）→ 1。
    cover = 1.0 if bot.get("cover") else 0.0

    return [
        min(1.0, max(0.0, h)),
        min(1.0, (target_dist or 0.0) / 100.0),
        min(1.0, visible_count / 10.0),
        indoor,
        min(1.0, friend_count / 20.0),
        min(1.0, enemy_count / 20.0),
        min(1.0, kills / 10.0),
        min(1.0, deaths / 10.0),
        min(1.0, he / 3.0),
        min(1.0, flash / 3.0),
        dist_delta,
        hidden,
        1.0 if (he + flash) > 0 else 0.0,
        has_ammo,
        same_room,
        door_feat,
        cover,
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

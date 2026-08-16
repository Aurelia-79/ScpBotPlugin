using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using Logger = LabApi.Features.Console.Logger;
using MEC;
using Mirror;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using ScpBotPlugin.ExternalAI;
using UnityEngine;

namespace ScpBotPlugin;

/// <summary>机器人操作类型。</summary>
internal enum BotOpKind
{
    Spawn,
    KillAll,
    KillOne
}

/// <summary>待处理机器人操作。</summary>
internal readonly struct BotOp
{
    public BotOpKind Kind { get; }

    public int Arg { get; }

    /// <summary>生成时指定的角色；null 表示用配置默认角色。</summary>
    public RoleTypeId? Role { get; }

    public BotOp(BotOpKind kind, int arg = 0, RoleTypeId? role = null)
    {
        Kind = kind;
        Arg = arg;
        Role = role;
    }
}

/// <summary>
/// 机器人的集中管理器：负责生成、销毁、AI 主循环与线程安全的命令转发。
/// </summary>
public static class BotManager
{
    private static readonly ConcurrentDictionary<int, Bot> Bots = new();
    private static readonly ConcurrentQueue<BotOp> Pending = new();

    private static int _nextId;
    private static CoroutineHandle _tick;
    private static Dictionary<string, List<string>>? _seenRoomGraph;

    // 外部 AI 桥。
    private static ExternalAIBridge? _bridge;
    private static bool _wasConnected;
    private static bool _cfgSent;
    private static float _nextSnapshotTime;

    // 死亡自动复活开关（默认开启；关闭后 bot 打完即销毁，不再复活）。
    private static bool _respawnEnabled = true;

    /// <summary>当前存活的机器人数量。</summary>
    public static int Count => Bots.Count;

    /// <summary>死亡自动复活开关（命令 bot respawn on/off 控制）。</summary>
    public static bool RespawnEnabled
    {
        get => _respawnEnabled;
        set => _respawnEnabled = value;
    }

    /// <summary>获取当前机器人的快照。</summary>
    public static Bot[] Snapshot() => Bots.Values.ToArray();

    /// <summary>请求生成若干机器人（线程安全，命令里调用）。role 为 null 时用配置默认角色。</summary>
    public static void RequestSpawn(int count, RoleTypeId? role = null) => Pending.Enqueue(new BotOp(BotOpKind.Spawn, count, role));

    /// <summary>请求销毁全部机器人（线程安全，命令里调用）。</summary>
    public static void RequestKillAll() => Pending.Enqueue(new BotOp(BotOpKind.KillAll));

    /// <summary>请求按编号销毁单个机器人（线程安全，命令里调用）。</summary>
    public static void RequestKill(int id) => Pending.Enqueue(new BotOp(BotOpKind.KillOne, id));

    /// <summary>启动 AI 主循环。配置始终从插件单例的最新实例读取，支持热重载。</summary>
    public static void StartTickLoop()
    {
        StopTickLoop();
        _tick = Timing.RunCoroutine(TickLoop(), Segment.Update);
    }

    /// <summary>停止 AI 主循环。</summary>
    public static void StopTickLoop()
    {
        if (_tick.IsValid)
        {
            Timing.KillCoroutines(_tick);
        }
    }

    /// <summary>立即销毁全部机器人（仅主线程调用，如插件禁用时）。</summary>
    public static void KillAll()
    {
        foreach (Bot bot in Bots.Values.ToArray())
        {
            bot.Dispose();
        }

        Bots.Clear();
    }

    /// <summary>启动外部 AI 桥（插件启用时调用）。</summary>
    public static void StartExternalAI(ExternalAiConfig config)
    {
        StopExternalAI();
        _wasConnected = false;
        _cfgSent = false;
        _nextSnapshotTime = 0f;

        _bridge = new ExternalAIBridge(config);
        _bridge.Start();
        Logger.Info($"[ScpBot] 外部 AI 已启用：{config.Host}:{config.Port}");
    }

    /// <summary>停止外部 AI 桥（插件禁用时调用）。</summary>
    public static void StopExternalAI()
    {
        _bridge?.Stop();
        _bridge = null;
    }

    private static IEnumerator<float> TickLoop()
    {
        while (true)
        {
            // 每次 tick 都取插件最新的配置实例，方便 labapi reload configs 热更新。
            BotConfig? config = BotPlugin.Instance?.Config;
            if (config == null)
            {
                yield return Timing.WaitForSeconds(0.5f);
                continue;
            }

            try
            {
                SyncConfig(config);
                SyncExternalAiState(config);
                ProcessPending(config);

                // 外部 AI：连接可用时采集快照、取指令；失联/未启用则走本地 AI。
                bool externalActive = _bridge != null && _bridge.IsActive;
                Dictionary<int, BotOrders>? ordersMap = null;

                if (externalActive)
                {
                    TrySendSnapshots(config);
                    ordersMap = new Dictionary<int, BotOrders>();
                    foreach (BotOrders order in _bridge!.DrainOrders())
                    {
                        ordersMap[order.Bot] = order;   // 同 bot 多条指令保留最新
                    }
                }

                foreach (Bot bot in Bots.Values.ToArray())
                {
                    // hub 已销毁：无法复活，直接清理。
                    if (!bot.IsValid)
                    {
                        bot.Dispose();
                        Bots.TryRemove(bot.Id, out _);
                        continue;
                    }

                    // 死亡（未初始化等待配装的除外）。
                    if (!bot.IsPendingLoadout && !bot.IsAlive)
                    {
                        if (_respawnEnabled)
                        {
                            bot.Respawn(config);   // 自动复活（保持生前角色）
                        }
                        else
                        {
                            bot.Dispose();          // 复活关闭：打完即销毁
                            Bots.TryRemove(bot.Id, out _);
                        }

                        continue;
                    }

                    if (externalActive && ordersMap != null)
                    {
                        if (ordersMap.TryGetValue(bot.Id, out BotOrders? orders))
                        {
                            bot.ExecuteOrders(orders, config);
                        }
                        else if (config.ExternalAI.IdleWhenNoOrders)
                        {
                            bot.Idle(config);
                        }
                        else
                        {
                            bot.Tick(config);
                        }
                    }
                    else
                    {
                        bot.Tick(config);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[ScpBot] AI 主循环异常: {ex}");
            }

            yield return Timing.WaitForSeconds(Mathf.Max(0.02f, config.TickInterval));
        }
    }

    /// <summary>
    /// 连接建立后发送一次静态配置；之后按 SendInterval 节流发送动态快照。
    /// 连接断开重连后会自动重发配置。
    /// </summary>
    private static void TrySendSnapshots(BotConfig config)
    {
        if (_bridge == null)
        {
            return;
        }

        bool connected = _bridge.Connected;
        if (connected && !_wasConnected)
        {
            _cfgSent = false;   // 新连接（含重连）：下一条即静态配置
        }

        _wasConnected = connected;

        if (!_cfgSent)
        {
            _bridge.Enqueue(ExternalAiProtocol.BuildConfigJson());
            _cfgSent = true;
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextSnapshotTime)
        {
            return;
        }

        _nextSnapshotTime = now + Math.Max(0.05f, config.ExternalAI.SendInterval);
        _bridge.Enqueue(ExternalAiProtocol.BuildSnapshotJson(Bots.Values.ToArray(), config));
    }

    /// <summary>
    /// 配置重载后（RoomGraph 引用变化）自动重新加载寻路数据；航点/目标点来自独立文件
    /// waypoints.yml，由 WaypointStore 负责（LoadConfigs 全量加载 + 本方法每 tick 检测文件改动热重载）。
    /// </summary>
    private static void SyncConfig(BotConfig config)
    {
        if (!ReferenceEquals(config.RoomGraph, _seenRoomGraph))
        {
            _seenRoomGraph = config.RoomGraph;
            RoomNavigator.LoadGraph(config.RoomGraph);
        }

        // 航点/目标点热重载：waypoints.yml 被外部修改时自动重新加载。
        WaypointStore.CheckForReload();
    }

    /// <summary>
    /// 外部 AI 热启停：配置里 ExternalAI.Enabled 变化时自动启动/停止桥，
    /// 无需重启插件（labapi reload configs 即可生效）。
    /// </summary>
    private static void SyncExternalAiState(BotConfig config)
    {
        bool wantExternal = config.ExternalAI.Enabled;
        bool haveBridge = _bridge != null;

        if (wantExternal && !haveBridge)
        {
            StartExternalAI(config.ExternalAI);
        }
        else if (!wantExternal && haveBridge)
        {
            StopExternalAI();
        }
    }

    private static void ProcessPending(BotConfig config)
    {
        while (Pending.TryDequeue(out BotOp op))
        {
            switch (op.Kind)
            {
                case BotOpKind.Spawn:
                {
                    int count = Mathf.Clamp(op.Arg, 1, 64);
                    RoleTypeId role = op.Role ?? config.BotRole;
                    for (int i = 0; i < count; i++)
                    {
                        SpawnOne(config, role);
                    }

                    break;
                }

                case BotOpKind.KillAll:
                    KillAll();
                    break;

                case BotOpKind.KillOne:
                {
                    if (Bots.TryRemove(op.Arg, out Bot? bot))
                    {
                        bot.Dispose();
                    }

                    break;
                }
            }
        }
    }

    private static void SpawnOne(BotConfig config, RoleTypeId role)
    {
        try
        {
            int id = System.Threading.Interlocked.Increment(ref _nextId);
            ReferenceHub? hub = DummyUtils.SpawnDummy($"{config.BotNamePrefix} {id}");

            if (hub == null)
            {
                Logger.Error("[ScpBot] DummyUtils.SpawnDummy 返回 null，生成失败。");
                return;
            }

            Player? player = Player.Get(hub);
            if (player == null)
            {
                NetworkServer.Destroy(hub.gameObject);
                Logger.Error("[ScpBot] 无法获取 Player 包装器，生成失败。");
                return;
            }

            Bot bot = new(id, hub, player, role);
            // 不在此处立即配装：Dummy 刚生成时 authManager.UserId 尚未设为 "ID_Dummy"，
            // 立即 SetRole 会因钥匙卡序列号 null key 崩溃，改为由 AI tick 在下一帧初始化。
            Bots[id] = bot;

            Logger.Info($"[ScpBot] 已生成机器人 #{id}（角色 {role}，武器 {config.PrimaryWeapon}）。");
        }
        catch (Exception ex)
        {
            Logger.Error($"[ScpBot] 生成机器人失败: {ex}");
        }
    }
}

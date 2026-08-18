using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using Logger = LabApi.Features.Console.Logger;
using MapGeneration;
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

    // 路线阵亡统计：路线指纹（房间序列）→ (时间, bot netId)。供「打不过换路」判定。
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<(float Time, uint NetId)>> RouteCasualties = new();

    /// <summary>记录某个 bot 的阵亡（含其当前路线指纹），供多路线换路判定。</summary>
    internal static void RecordCasualty(Bot bot, string? routeFingerprint, float now)
    {
        if (string.IsNullOrEmpty(routeFingerprint))
        {
            return;
        }

        ConcurrentQueue<(float, uint)> queue = RouteCasualties.GetOrAdd(routeFingerprint!, _ => new ConcurrentQueue<(float, uint)>());
        queue.Enqueue((now, (uint)bot.Id));

        // 清理过期记录（防止队列无限增长）。
        while (queue.TryPeek(out (float Time, uint NetId) oldest) && now - oldest.Time > 60f)
        {
            queue.TryDequeue(out _);
        }
    }

    /// <summary>统计指定路线（指纹）在最近 window 秒内的阵亡数。</summary>
    internal static int GetRouteCasualtyCount(List<RoomName> route, float window)
    {
        if (route == null || route.Count == 0 || window <= 0f)
        {
            return 0;
        }

        string fingerprint = BuildRouteFingerprint(route);
        if (!RouteCasualties.TryGetValue(fingerprint, out ConcurrentQueue<(float, uint)>? queue))
        {
            return 0;
        }

        float now = Time.timeSinceLevelLoad;
        int count = 0;
        foreach ((float time, uint _) in queue)
        {
            if (now - time <= window)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>路线指纹：房间名序列用 ">" 连接（用于阵亡统计去重匹配）。</summary>
    private static string BuildRouteFingerprint(List<RoomName> route)
    {
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('>');
            }

            sb.Append(route[i]);
        }

        return sb.ToString();
    }

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

    /// <summary>
    /// 示教学习：让指定（或全部）机器人跟随玩家并记录房间轨迹。
    /// targetId 为 null 时所有存活的 bot 都跟随。
    /// </summary>
    public static int StartFollow(Player leader, int? targetId)
    {
        int count = 0;
        foreach (Bot bot in Bots.Values.ToArray())
        {
            if (!bot.IsAlive || (targetId.HasValue && bot.Id != targetId.Value))
            {
                continue;
            }

            bot.StartFollow(leader);
            count++;
        }

        return count;
    }

    /// <summary>示教学习：停止跟随（指定或全部）并提交轨迹。</summary>
    public static int StopFollow(int? targetId)
    {
        int count = 0;
        foreach (Bot bot in Bots.Values.ToArray())
        {
            if (!bot.IsFollowing || (targetId.HasValue && bot.Id != targetId.Value))
            {
                continue;
            }

            bot.StopFollow();
            count++;
        }

        return count;
    }

    /// <summary>跟随状态摘要（命令 bot follow list 用）。</summary>
    public static string FollowStatus()
    {
        var following = Bots.Values.Where(b => b.IsFollowing).ToArray();
        if (following.Length == 0)
        {
            return "当前没有机器人处于跟随（示教）模式。";
        }

        System.Text.StringBuilder sb = new();
        sb.Append("跟随中的机器人：");
        foreach (Bot bot in following)
        {
            sb.Append($"\n  #{bot.Id}（{bot.Name}，队伍 {bot.Team}）");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 提交示教轨迹给外部 AI（trace 消息：bot id + 房间序列）。
    /// 外部 AI 未连接时丢弃（轨迹只对神经网络学习有意义）。
    /// </summary>
    public static void SubmitTrace(int botId, List<string> rooms)
    {
        if (_bridge == null || !_bridge.IsActive || rooms.Count < 2)
        {
            return;
        }

        System.Text.StringBuilder sb = new();
        sb.Append("{\"type\":\"trace\",\"bot\":").Append(botId.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"rooms\":[");
        for (int i = 0; i < rooms.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('"').Append(rooms[i]).Append('"');
        }

        sb.Append("]}");
        _bridge.Enqueue(sb.ToString());
        Logger.Info($"[ScpBot] 示教轨迹已发送给外部 AI：bot #{botId}，{rooms.Count} 个房间。");
    }

    /// <summary>
    /// 卡房超时处理：bot 卡在同一房间且无交战超时 → 重生该阵营的全部 bot，
    /// 并给神经网络发送严厉惩罚（penalty 消息，Python 端对所有相关 bot 记账）。
    /// </summary>
    private static void HandleIdleStuckTimeout(BotConfig config, Bot stuckBot)
    {
        // 重置计时，避免连续触发（重生本身也是一种“进展”）。
        stuckBot.ResetIdleStuck();

        Team team = stuckBot.Team;
        int count = 0;

        // 重生该阵营的全部存活 bot（先销毁再按原角色重建）。
        foreach (Bot bot in Bots.Values.ToArray())
        {
            if (!bot.IsAlive || bot.IsFollowing || bot.Team != team)
            {
                continue;
            }

            bot.ResetIdleStuck();
            bot.Dispose();
            Bots.TryRemove(bot.Id, out _);
            count++;
        }

        // 按原数量重生（每个被销毁的 bot 用其队伍默认角色重建——用 config 默认角色，
        // 阵营由生成时的角色决定；这里简单用 config.BotRole 保持同队）。
        for (int i = 0; i < count; i++)
        {
            RequestSpawn(1, config.BotRole);
        }

        Logger.Warn($"[ScpBot] 机器人 #{stuckBot.Id} 卡房无交战超时（{config.IdleStuckTimeout:F0}s），已重生 {team} 阵营全部 {count} 个机器人。");

        // 给神经网络严厉惩罚（外部 AI 在线时发送）。
        if (_bridge != null && _bridge.IsActive)
        {
            _bridge.Enqueue($"{{\"type\":\"penalty\",\"team\":\"{team}\",\"amount\":-5.0,\"reason\":\"idle_stuck_timeout\"}}");
            Logger.Info("[ScpBot] 已向神经网络发送卡房超时惩罚（-5.0）。");
        }
    }

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

    /// <summary>订阅击杀/阵亡事件（插件启用时调用），供神经网络学习奖励统计。</summary>
    public static void InitStats()
    {
        PlayerEvents.Dying += OnPlayerDying;
    }

    /// <summary>退订击杀/阵亡事件（插件禁用时调用）。</summary>
    public static void TerminateStats()
    {
        PlayerEvents.Dying -= OnPlayerDying;
    }

    /// <summary>
    /// 死亡事件：凶手是本 bot → 该 bot 击杀 +1；死者是本 bot → 该 bot 阵亡 +1。
    /// 由 TickLoop 统一在死亡后自动复活/清理，这里只记账。
    /// </summary>
    private static void OnPlayerDying(PlayerDyingEventArgs ev)
    {
        try
        {
            // FF-05：不能用 PlayerId 查 Bots 字典 —— Bots 的键是内部自增计数器（Id），
            // 而 Player.PlayerId 是 RecyclablePlayerId（最小可用槽位池，0 起、断线回收复用），
            // 两套编号相互独立，TryGetValue 几乎必然落空（击杀/阵亡统计与神经网络奖励恒 0）。
            // 这里改为按 Hub 引用定位，唯一且不受槽位回收影响。
            if (ev.Attacker != null && ev.Attacker.IsDummy)
            {
                Bot? killer = Bots.Values.FirstOrDefault(b => b.Hub == ev.Attacker.ReferenceHub);
                if (killer != null)
                {
                    killer.Kills++;
                }
            }

            if (ev.Player != null && ev.Player.IsDummy)
            {
                Bot? victim = Bots.Values.FirstOrDefault(b => b.Hub == ev.Player.ReferenceHub);
                if (victim != null)
                {
                    victim.Deaths++;
                    RecordCasualty(victim, victim.RouteFingerprint, Time.timeSinceLevelLoad);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ScpBot] 击杀统计异常: {ex.GetBaseException().Message}");
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

                    // 卡房超时检测：卡在同一房间且无交战超过阈值 → 重生整个阵营 + 惩罚网络。
                    // FF-11：必须同时校验 Enabled 与 Timeout > 0 —— 文档约定「0 或负值禁用」，
                    // 但 UpdateIdleStuck 把计时清零后 0 >= 0 恒真，不加守卫会在每 tick 触发
                    // 全阵营销毁-重生风暴（无限循环）。
                    if (bot.IsAlive && !bot.IsFollowing
                        && config.IdleStuckTimeoutEnabled && config.IdleStuckTimeout > 0f)
                    {
                        bot.UpdateIdleStuck(config);
                        if (bot.IdleStuckTime >= config.IdleStuckTimeout)
                        {
                            HandleIdleStuckTimeout(config, bot);
                        }
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

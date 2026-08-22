using System;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;

namespace ScpBotPlugin;

/// <summary>
/// 基于游戏内置 Dummy 的自动寻路 + 自动战斗机器人插件。
/// </summary>
public class BotPlugin : Plugin<BotConfig>
{
    /// <summary>插件单例，方便命令访问配置。</summary>
    public static BotPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "ScpBot";

    /// <inheritdoc />
    public override string Description => "依赖游戏内置 Dummy 的机器人：自动索敌、自动寻路、自动开枪，行为接近普通玩家。";

    /// <inheritdoc />
    public override string Author => "ScpBot";

    /// <inheritdoc />
    public override Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);

    /// <inheritdoc />
    public override string ConfigFileName => "scpbot.yml";

    /// <inheritdoc />
    /// <remarks>
    /// 主配置加载后，顺带从独立航点文件（waypoints.yml）加载航点/目标点。
    /// labapi reload configs 会走这里，因此航点文件同样支持热重载。
    /// 注意：LoadConfigs 在 Enable() 之前被调用（PluginLoader.EnablePlugin），
    /// 此时 Instance 尚未赋值，必须用 this 而非 Instance。
    /// </remarks>
    public override void LoadConfigs()
    {
        base.LoadConfigs();
        WaypointStore.Load(this);
    }

    /// <inheritdoc />
    public override void Enable()
    {
        Instance = this;

        ServerEvents.RoundStarted += OnRoundStarted;
        ServerEvents.RoundEnded += OnRoundEnded;
        ServerEvents.WaitingForPlayers += OnWaitingForPlayers;

        // 房间图 / 航点 / 目标点的加载统一交给 BotManager.TickLoop 内的 SyncConfig
        // 负责（首次 tick 立即同步，之后每次热重载也走同一通道），此处不重复加载。

        // 地表 NavMesh（运行时烘焙，只为 Outside 提供连续地形自动寻路）。
        SurfaceNavMeshService.Init();

        // 外部 AI（可选）：独立 Python/Node 进程多核决策，失联自动降级本地 AI。
        if (Config.ExternalAI.Enabled)
        {
            BotManager.StartExternalAI(Config.ExternalAI);
        }

        // 击杀/阵亡统计（神经网络学习奖励信号）。
        BotManager.InitStats();

        // 启动 AI 主循环（MEC 协程，在主线程上运行）。
        BotManager.StartTickLoop();

        Logger.Info("[ScpBot] 已启用。使用 RA / 服务器控制台命令 'bot spawn [数量]' 生成机器人。");
    }

    /// <inheritdoc />
    public override void Disable()
    {
        ServerEvents.RoundStarted -= OnRoundStarted;
        ServerEvents.RoundEnded -= OnRoundEnded;
        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;

        BotManager.StopTickLoop();
        BotManager.StopExternalAI();
        BotManager.TerminateStats();
        BotManager.ClearPending();   // FF-44：丢弃禁用前残留的待处理操作，防止重新启用后执行过期命令
        BotManager.KillAll();
        SurfaceNavMeshService.Terminate();

        Instance = null;
        Logger.Info("[ScpBot] 已禁用，全部机器人已销毁。");
    }

    private void OnRoundStarted()
    {
        if (Config.AutoSpawnOnRoundStart && Config.AutoSpawnCount > 0)
        {
            BotManager.RequestSpawn(Config.AutoSpawnCount);
        }
    }

    private void OnRoundEnded(RoundEndedEventArgs ev)
    {
        BotManager.RequestKillAll();
    }

    private void OnWaitingForPlayers()
    {
        BotManager.RequestKillAll();
    }
}

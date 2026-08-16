using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using MapGeneration;
using UnityEngine;
using UnityEngine.AI;
using Logger = LabApi.Features.Console.Logger;
using Object = UnityEngine.Object;

namespace ScpBotPlugin;

/// <summary>
/// 运行时 NavMesh：烘焙 Surface（地表）与 Entrance（入口区）两大区域，为 bot 提供连续地形自动寻路。
/// 烘焙用 PhysicsColliders 收集可走面，路径查询用 NavMesh.SamplePosition + NavMesh.CalculatePath 静态 API。
/// 刻意不引入 Harmony、不使用 NavMeshAgent 组件：移动仍走 Bot 的 Move()（ReceivedPosition + 障碍绕行）。
/// 烘焙质量支持 High（默认）/ Ultra（最高质量）两档，可在 scpbot.yml 中通过 BakeQuality 切换。
/// </summary>
public static class SurfaceNavMeshService
{
    private static NavMeshDataInstance _instance;
    private static bool _hasNavMesh;
    private static bool _baking; // 后台烘焙进行中标记，防止并发重复烘焙
    private static int _bakeGeneration; // 烘焙代际：回合重置时 +1，使过期后台结果作废

    /// <summary>是否已有可用的 NavMesh。</summary>
    public static bool HasNavMesh => _hasNavMesh;

    /// <summary>订阅地图生成事件（插件启用时调用）。</summary>
    public static void Init()
    {
        ServerEvents.MapGenerated += OnMapGenerated;
        ServerEvents.RoundRestarted += OnRoundRestarted;
    }

    /// <summary>退订事件并移除 NavMesh（插件禁用时调用）。</summary>
    public static void Terminate()
    {
        ServerEvents.MapGenerated -= OnMapGenerated;
        ServerEvents.RoundRestarted -= OnRoundRestarted;
        RemoveNavMesh();
    }

    private static void OnMapGenerated(MapGeneratedEventArgs ev) => TryBake(BotPlugin.Instance?.Config);

    private static void OnRoundRestarted() => RemoveNavMesh();

    /// <summary>移除当前 NavMesh，并作废正在进行的后台烘焙。</summary>
    public static void RemoveNavMesh()
    {
        _bakeGeneration++; // 作废所有在途后台烘焙结果
        if (_hasNavMesh)
        {
            NavMesh.RemoveNavMeshData(_instance);
            _instance = default;
            _hasNavMesh = false;
        }
    }

    /// <summary>
    /// 烘焙 NavMesh（Surface + Entrance 区域）。成功返回 true。
    /// CollectSources 在主线程执行（访问场景碰撞体），BuildNavMeshData 放到后台线程
    /// （输入全为值类型，跨线程安全），完成后回到主线程 AddNavMeshData，避免卡服务器主线程。
    /// </summary>
    public static bool TryBake(BotConfig? config = null)
    {
        if (_baking)
        {
            Logger.Warn("[ScpBot] NavMesh 烘焙正在进行中，忽略本次请求。");
            return false;
        }

        RemoveNavMesh();

        // 1) 收集烘焙区域锚点：Surface + Entrance（入口区）全区域（房间中心 + 门位置）。
        List<Vector3> anchors = new();
        foreach (Room room in Room.List)
        {
            if (room != null && !room.IsDestroyed && IsBakedZone(room.Zone))
            {
                anchors.Add(room.Position);
            }
        }

        foreach (Door door in Door.List)
        {
            if (door != null && !door.IsDestroyed && IsBakedZone(door.Zone))
            {
                anchors.Add(door.Position);
            }
        }

        if (anchors.Count == 0)
        {
            Logger.Warn("[ScpBot] 未找到烘焙区域锚点，跳过 NavMesh 烘焙。");
            return false;
        }

        // 2) 由锚点 + 附近碰撞体计算烘焙范围。
        Bounds bounds = BuildSurfaceBounds(anchors);

        // 3) 忽略玩家角色（避免把玩家/机器人模型烘焙进 NavMesh）。
        List<NavMeshBuildMarkup> markups = new();
        foreach (Player player in Player.List)
        {
            if (player?.ReferenceHub?.transform != null)
            {
                markups.Add(new NavMeshBuildMarkup
                {
                    root = player.ReferenceHub.transform,
                    ignoreFromBuild = true,
                });
            }
        }

        // 4) 收集碰撞体源（主线程：访问场景对象）。
        List<NavMeshBuildSource> sources = new();
        NavMeshBuilder.CollectSources(
            bounds,
            ~0,
            NavMeshCollectGeometry.PhysicsColliders,
            0,
            markups,
            sources);

        if (sources.Count == 0)
        {
            Logger.Warn("[ScpBot] NavMesh 烘焙：未收集到碰撞体，回退到直线追击。");
            return false;
        }

        // 5) 烘焙设置：按质量档（High / Ultra）配置 agent 参数与体素精度。
        NavMeshBuildSettings settings = GetBuildSettings(config);
        int sourceCount = sources.Count;
        int anchorCount = anchors.Count;
        string zoneLabel = DescribeBakedZones();

        // 6) 后台烘焙（输入均为值类型：settings / sources / bounds，跨线程安全）。
        _baking = true;
        int generation = ++_bakeGeneration;
        DateTime started = DateTime.UtcNow;
        Logger.Info($"[ScpBot] NavMesh 烘焙开始（区域 {zoneLabel}，质量 {(config != null && string.Equals(config.BakeQuality?.Trim(), "Ultra", StringComparison.OrdinalIgnoreCase) ? "Ultra" : "High")}，voxel={settings.voxelSize:F2}，{sources.Count} 个碰撞体源，后台执行）。");

        Task<NavMeshData?> bakeTask = Task.Run(() =>
        {
            try
            {
                // 静默内部日志，避免 NavMesh 日志刷屏（后台线程内同样生效）。
                bool previousLogEnabled = Debug.unityLogger.logEnabled;
                Debug.unityLogger.logEnabled = false;
                NavMeshData data;
                try
                {
                    data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
                }
                finally
                {
                    Debug.unityLogger.logEnabled = previousLogEnabled;
                }

                return data;
            }
            catch (Exception ex)
            {
                Logger.Error($"[ScpBot] NavMesh 后台烘焙异常: {ex.GetBaseException().Message}");
                return null;
            }
        });

        // 主线程同步上下文存在时回主线程注册 NavMesh（Unity 对象操作必须在主线程）；
        // 若当前无同步上下文（异常情况），直接执行（Unity 内部会校验线程）。
        TaskScheduler scheduler = SynchronizationContext.Current != null
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Default;

        bakeTask.ContinueWith(t =>
        {
            _baking = false;

            // 烘焙期间回合已重置（或已手动移除）：作废本次结果，避免注册过期 NavMesh。
            if (generation != _bakeGeneration)
            {
                Logger.Info("[ScpBot] NavMesh 烘焙完成但已被回合重置作废，丢弃结果。");
                return;
            }

            NavMeshData? data = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (data == null)
            {
                Logger.Warn("[ScpBot] NavMesh 后台烘焙失败或返回空数据。");
                return;
            }

            // 回到主线程注册 NavMesh（Unity 对象操作必须在主线程）。
            try
            {
                _instance = NavMesh.AddNavMeshData(data);
                _hasNavMesh = _instance.valid;

                if (_hasNavMesh)
                {
                    NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                    int triangles = tri.indices != null ? tri.indices.Length / 3 : 0;
                    double seconds = (DateTime.UtcNow - started).TotalSeconds;
                    Logger.Info($"[ScpBot] NavMesh 已烘焙完成：{sourceCount} 个源、{triangles} 个三角形，耗时 {seconds:F1}s。");
                }
                else
                {
                    Logger.Warn("[ScpBot] NavMesh 烘焙完成但实例无效。");
                }
            }
            catch (Exception ex)
            {
                _hasNavMesh = false;
                Logger.Error($"[ScpBot] NavMesh 注册失败: {ex.GetBaseException().Message}");
            }
        }, scheduler);

        return true;
    }

    /// <summary>判断指定区域是否纳入烘焙（Surface + Entrance）。</summary>
    private static bool IsBakedZone(FacilityZone zone) =>
        zone == FacilityZone.Surface || zone == FacilityZone.Entrance;

    private static string DescribeBakedZones() => "地表 + 入口区";

    /// <summary>
    /// 查询地表 NavMesh 路径，返回拐点列表（含终点、不含起点）。
    /// 起点/终点无法采样或路径无效时返回 false，调用方应回退到直线追击。
    /// </summary>
    public static bool TryFindPath(
        Vector3 start,
        Vector3 target,
        out List<Vector3> corners,
        float sampleDistance = 2.5f)
    {
        corners = new List<Vector3>();
        if (!_hasNavMesh)
        {
            return false;
        }

        if (!NavMesh.SamplePosition(start, out NavMeshHit startHit, sampleDistance, NavMesh.AllAreas))
        {
            return false;
        }

        if (!NavMesh.SamplePosition(target, out NavMeshHit targetHit, sampleDistance, NavMesh.AllAreas))
        {
            return false;
        }

        NavMeshPath path = new();
        if (!NavMesh.CalculatePath(startHit.position, targetHit.position, NavMesh.AllAreas, path)
            || path.status == NavMeshPathStatus.PathInvalid)
        {
            return false;
        }

        Vector3[] pathCorners = path.corners ?? Array.Empty<Vector3>();
        for (int i = 1; i < pathCorners.Length; i++)
        {
            corners.Add(pathCorners[i]);
        }

        return corners.Count > 0;
    }

    /// <summary>
    /// 按质量档构建烘焙设置：
    /// High（默认）：voxel 0.15、agentRadius 0.35（与原行为一致）。
    /// Ultra（最高质量）：voxel 0.1、agentRadius 0.25、更高坡度/攀爬、buildHeightMesh、全核烘焙。
    /// </summary>
    private static NavMeshBuildSettings GetBuildSettings(BotConfig? config)
    {
        NavMeshBuildSettings settings;
        if (NavMesh.GetSettingsCount() > 0)
        {
            try
            {
                settings = NavMesh.GetSettingsByIndex(0);
            }
            catch
            {
                settings = NavMesh.CreateSettings();
                settings.agentTypeID = 0;
            }
        }
        else
        {
            settings = NavMesh.CreateSettings();
            settings.agentTypeID = 0;
        }

        bool ultra = config != null
            && string.Equals(config.BakeQuality?.Trim(), "Ultra", StringComparison.OrdinalIgnoreCase);

        if (ultra)
        {
            // Ultra 最高质量：更小体素捕捉细节（门缝/窄道/楼梯边缘）、更小 agent 半径贴近墙面走、
            // 更高坡度/攀爬适配设施内坡道与台阶、保留全部小区域、构建高度网格、全核并行。
            settings.overrideVoxelSize = true;
            settings.voxelSize = 0.1f;
            settings.agentRadius = 0.25f;
            settings.agentHeight = 2.0f;
            settings.agentSlope = 55f;
            settings.agentClimb = 0.8f;
            settings.minRegionArea = 0f;
            settings.buildHeightMesh = true;
            settings.maxJobWorkers = 0; // 0 = 使用全部可用 worker
        }
        else
        {
            // High 默认：与原地表烘焙一致 + 适度细化体素。
            settings.overrideVoxelSize = true;
            settings.voxelSize = 0.15f;
            settings.agentRadius = 0.35f;
            settings.agentHeight = 2.0f;
            settings.agentSlope = 50f;
            settings.agentClimb = 0.6f;
            settings.minRegionArea = 0f;
            settings.buildHeightMesh = false;
            settings.maxJobWorkers = 0;
        }

        return settings;
    }

    private static Bounds BuildSurfaceBounds(List<Vector3> anchors)
    {
        Bounds bounds = new(anchors[0], Vector3.zero);
        foreach (Vector3 anchor in anchors)
        {
            bounds.Encapsulate(new Bounds(anchor, new Vector3(30f, 24f, 30f)));
        }

        // 遍历场景碰撞体，扩展 bounds 以覆盖山体/楼群等连续地形。
        foreach (Collider collider in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (collider == null || !collider.enabled || ShouldIgnore(collider.transform))
            {
                continue;
            }

            if (IsNearAnyAnchor(collider.bounds, anchors, 120f, 90f))
            {
                bounds.Encapsulate(collider.bounds);
            }
        }

        bounds.Expand(new Vector3(12f, 12f, 12f));
        return bounds;
    }

    private static bool IsNearAnyAnchor(Bounds bounds, List<Vector3> anchors, float horizontal, float vertical)
    {
        foreach (Vector3 anchor in anchors)
        {
            float dx = OutOfRange(anchor.x, bounds.min.x, bounds.max.x);
            float dz = OutOfRange(anchor.z, bounds.min.z, bounds.max.z);
            float dy = OutOfRange(anchor.y, bounds.min.y, bounds.max.y);
            if (dy <= vertical && Mathf.Sqrt((dx * dx) + (dz * dz)) <= horizontal)
            {
                return true;
            }
        }

        return false;
    }

    private static float OutOfRange(float value, float min, float max)
    {
        if (value < min)
        {
            return min - value;
        }

        return value > max ? value - max : 0f;
    }

    private static bool ShouldIgnore(Transform transform)
    {
        if (transform == null)
        {
            return true;
        }

        foreach (Player player in Player.List)
        {
            Transform? root = player?.ReferenceHub?.transform;
            if (root != null && (transform == root || transform.IsChildOf(root)))
            {
                return true;
            }
        }

        return false;
    }
}

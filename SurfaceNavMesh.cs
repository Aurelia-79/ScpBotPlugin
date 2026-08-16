using System;
using System.Collections.Generic;
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
/// 地表（Outside）运行时 NavMesh：只烘焙 Surface 区，为 bot 提供连续地形（山体/楼群/斜坡）的自动寻路。
/// 烘焙用 PhysicsColliders 收集可走面，路径查询用 NavMesh.SamplePosition + NavMesh.CalculatePath 静态 API。
/// 刻意不引入 Harmony、不使用 NavMeshAgent 组件：移动仍走 Bot 的 Move()（ReceivedPosition + 障碍绕行）。
/// </summary>
public static class SurfaceNavMeshService
{
    private static NavMeshDataInstance _instance;
    private static bool _hasNavMesh;

    /// <summary>是否已有可用的地表 NavMesh。</summary>
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

    private static void OnMapGenerated(MapGeneratedEventArgs ev) => TryBakeSurface();

    private static void OnRoundRestarted() => RemoveNavMesh();

    /// <summary>移除当前地表 NavMesh。</summary>
    public static void RemoveNavMesh()
    {
        if (_hasNavMesh)
        {
            NavMesh.RemoveNavMeshData(_instance);
            _instance = default;
            _hasNavMesh = false;
        }
    }

    /// <summary>烘焙地表 NavMesh（只覆盖 Surface 区）。成功返回 true。</summary>
    public static bool TryBakeSurface()
    {
        RemoveNavMesh();

        // 1) 收集 Surface 区锚点（房间中心 + 门位置），作为烘焙范围基准。
        List<Vector3> anchors = new();
        foreach (Room room in Room.List)
        {
            if (room != null && !room.IsDestroyed && room.Zone == FacilityZone.Surface)
            {
                anchors.Add(room.Position);
            }
        }

        foreach (Door door in Door.List)
        {
            if (door != null && !door.IsDestroyed && door.Zone == FacilityZone.Surface)
            {
                anchors.Add(door.Position);
            }
        }

        if (anchors.Count == 0)
        {
            Logger.Warn("[ScpBot] 未找到地表锚点，跳过地表 NavMesh 烘焙。");
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

        // 4) 收集碰撞体源。
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
            Logger.Warn("[ScpBot] 地表 NavMesh 烘焙：未收集到碰撞体，回退到直线追击。");
            return false;
        }

        // 5) 烘焙设置（贴近人类行走体型的 agent 参数）。
        NavMeshBuildSettings settings = GetBuildSettings();
        settings.agentRadius = 0.35f;
        settings.agentHeight = 2.0f;
        settings.agentSlope = 50f;
        settings.agentClimb = 0.6f;
        settings.minRegionArea = 0f;

        // 6) 烘焙（静默，避免 NavMesh 内部日志刷屏）。
        NavMeshData data;
        try
        {
            bool previousLogEnabled = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            try
            {
                data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            }
            finally
            {
                Debug.unityLogger.logEnabled = previousLogEnabled;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ScpBot] 地表 NavMesh 烘焙失败: {ex.GetBaseException().Message}");
            return false;
        }

        if (data == null)
        {
            Logger.Warn("[ScpBot] 地表 NavMesh 烘焙返回空数据。");
            return false;
        }

        _instance = NavMesh.AddNavMeshData(data);
        _hasNavMesh = _instance.valid;

        if (_hasNavMesh)
        {
            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            int triangles = tri.indices != null ? tri.indices.Length / 3 : 0;
            Logger.Info($"[ScpBot] 地表 NavMesh 已烘焙：{sources.Count} 个源、{triangles} 个三角形。");
        }
        else
        {
            Logger.Warn("[ScpBot] 地表 NavMesh 烘焙完成但实例无效。");
        }

        return _hasNavMesh;
    }

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

    private static NavMeshBuildSettings GetBuildSettings()
    {
        if (NavMesh.GetSettingsCount() > 0)
        {
            try
            {
                return NavMesh.GetSettingsByIndex(0);
            }
            catch
            {
                // 回退到新建设置。
            }
        }

        NavMeshBuildSettings settings = NavMesh.CreateSettings();
        settings.agentTypeID = 0;
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

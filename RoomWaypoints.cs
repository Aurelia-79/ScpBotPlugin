using System;
using System.Collections.Generic;
using System.Globalization;
using LabApi.Features.Wrappers;
using Logger = LabApi.Features.Console.Logger;
using MapGeneration;
using UnityEngine;

namespace ScpBotPlugin;

/// <summary>
/// 房间内航点管理：一个房间可配置多条独立路线，机器人每次进入该房间时随机选择其中一条，
/// 依次经过路线上的点，用于绕开房间内障碍物或走出快捷路线。
/// </summary>
public static class RoomWaypoints
{
    // 房间 → 多条路线；每条路线是一串世界坐标点。
    private static readonly Dictionary<RoomName, List<List<Vector3>>> Routes = new();

    // 「当前正在录入」的路线（配合 bot wp new / add 命令）。
    private static RoomName? _activeRoom;
    private static List<Vector3>? _activeRoute;

    /// <summary>
    /// 从配置加载航点（房间名 → 多条路线，每条路线是 "x y z" 坐标字符串列表）。
    /// </summary>
    public static void LoadConfig(Dictionary<string, List<List<string>>>? config)
    {
        Routes.Clear();
        _activeRoom = null;
        _activeRoute = null;

        if (config == null || config.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, List<List<string>>> kv in config)
        {
            if (!Enum.TryParse(kv.Key, true, out RoomName name))
            {
                Logger.Warn($"[ScpBot] 航点配置中的房间名无效，已忽略：'{kv.Key}'");
                continue;
            }

            List<List<Vector3>> roomRoutes = new();
            if (kv.Value != null)
            {
                foreach (List<string> rawRoute in kv.Value)
                {
                    List<Vector3> route = new();
                    if (rawRoute != null)
                    {
                        foreach (string raw in rawRoute)
                        {
                            if (TryParsePosition(raw, out Vector3 pos))
                            {
                                route.Add(pos);
                            }
                            else
                            {
                                Logger.Warn($"[ScpBot] 房间 '{kv.Key}' 的航点坐标无效，已忽略：'{raw}'（格式应为 \"x y z\"）");
                            }
                        }
                    }

                    if (route.Count > 0)
                    {
                        roomRoutes.Add(route);
                    }
                }
            }

            if (roomRoutes.Count > 0)
            {
                Routes[name] = roomRoutes;
            }
        }

        Logger.Info($"[ScpBot] 房间内航点已加载，共 {Routes.Count} 个房间、{GetTotalRouteCount()} 条路线。");
    }

    /// <summary>获取指定房间的全部路线；未配置则返回 false。</summary>
    public static bool TryGetRoutes(RoomName room, out List<List<Vector3>>? routes)
    {
        routes = null;
        return Routes.TryGetValue(room, out routes);
    }

    /// <summary>随机取该房间的一条路线；无配置返回 null。</summary>
    public static List<Vector3>? GetRandomRoute(RoomName room)
    {
        if (!Routes.TryGetValue(room, out List<List<Vector3>>? routes) || routes.Count == 0)
        {
            return null;
        }

        return routes[UnityEngine.Random.Range(0, routes.Count)];
    }

    /// <summary>取指定房间指定编号的路线（0 起）。</summary>
    public static bool GetRoute(RoomName room, int routeIndex, out List<Vector3>? route)
    {
        route = null;
        if (!Routes.TryGetValue(room, out List<List<Vector3>>? routes) || routeIndex < 0 || routeIndex >= routes.Count)
        {
            return false;
        }

        route = routes[routeIndex];
        return true;
    }

    /// <summary>该房间已配置的路线数量。</summary>
    public static int GetRouteCount(RoomName room)
    {
        return Routes.TryGetValue(room, out List<List<Vector3>>? routes) ? routes.Count : 0;
    }

    /// <summary>该房间指定路线的点数。</summary>
    public static int GetPointCount(RoomName room, int routeIndex)
    {
        if (!Routes.TryGetValue(room, out List<List<Vector3>>? routes) || routeIndex < 0 || routeIndex >= routes.Count)
        {
            return 0;
        }

        return routes[routeIndex].Count;
    }

    /// <summary>全部路线总数。</summary>
    public static int GetTotalRouteCount()
    {
        int total = 0;
        foreach (KeyValuePair<RoomName, List<List<Vector3>>> kv in Routes)
        {
            total += kv.Value.Count;
        }

        return total;
    }

    /// <summary>已配置航点的房间名集合。</summary>
    public static IEnumerable<RoomName> GetAllRooms()
    {
        return Routes.Keys;
    }

    // ---- 运行时录入（bot wp 命令） ----

    /// <summary>为指定房间开始一条新路线（之后 add 的点都加进这条）。</summary>
    public static void StartNewRoute(RoomName room)
    {
        List<Vector3> route = new();
        if (!Routes.TryGetValue(room, out List<List<Vector3>>? routes))
        {
            routes = new List<List<Vector3>>();
            Routes[room] = routes;
        }

        routes.Add(route);
        _activeRoom = room;
        _activeRoute = route;
    }

    /// <summary>把位置追加到指定房间的「当前路线」；无活动路线则自动新建一条。</summary>
    public static void AddPoint(RoomName room, Vector3 position)
    {
        // FF-57：此前判断 `_activeRoom != room` 就会新建路线，导致「管理员在房间 A 开了路线、
        // 走到房间 B 再 wp add」时静默新建一条 B 房间的路线，A 房间的路线被遗留（碎路线）。
        // 正确行为：已有活动路线时直接把点加进去（路线归属以其创建时所在房间为准），
        // 只有完全没有活动路线时才新建。
        if (_activeRoute == null)
        {
            StartNewRoute(room);
        }

        _activeRoute!.Add(position);
    }

    /// <summary>删除指定房间的某条路线（0 起）；清空该房间后移除整个房间条目。</summary>
    public static bool ClearRoute(RoomName room, int routeIndex)
    {
        if (!Routes.TryGetValue(room, out List<List<Vector3>>? routes) || routeIndex < 0 || routeIndex >= routes.Count)
        {
            return false;
        }

        routes.RemoveAt(routeIndex);

        if (_activeRoom == room)
        {
            _activeRoom = null;
            _activeRoute = null;
        }

        if (routes.Count == 0)
        {
            Routes.Remove(room);
        }

        return true;
    }

    /// <summary>清空指定房间的全部路线。</summary>
    public static void Clear(RoomName room)
    {
        Routes.Remove(room);

        if (_activeRoom == room)
        {
            _activeRoom = null;
            _activeRoute = null;
        }
    }

    /// <summary>清空全部路线。</summary>
    public static void ClearAll()
    {
        Routes.Clear();
        _activeRoom = null;
        _activeRoute = null;
    }

    // ---- 工具 ----

    /// <summary>解析 "x y z" 坐标字符串。</summary>
    public static bool TryParsePosition(string raw, out Vector3 position)
    {
        position = default;

        string[] parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            return false;
        }

        // FF-09：显式拒绝 NaN/Infinity —— .NET 的 float.TryParse("NaN"/"Infinity", NumberStyles.Float)
        // 实测返回 true，NaN 坐标会污染移动/目标选择（bot 冲向世界原点等）。
        if (float.IsNaN(x) || float.IsInfinity(x)
            || float.IsNaN(y) || float.IsInfinity(y)
            || float.IsNaN(z) || float.IsInfinity(z))
        {
            return false;
        }

        position = new Vector3(x, y, z);
        return true;
    }

    /// <summary>把位置格式化为 "x y z"。</summary>
    public static string Format(Vector3 position)
    {
        return $"{position.x.ToString("F2", CultureInfo.InvariantCulture)} {position.y.ToString("F2", CultureInfo.InvariantCulture)} {position.z.ToString("F2", CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// 大房间推荐目标点管理：给面积大/地形复杂的房间（如地表 Outside）配置若干关键地标，
/// 机器人进入该房间后奔向离目标最近的推荐点分段接近，避免笔直冲目标被地形卡住。
/// </summary>
public static class RoomTargets
{
    private static readonly Dictionary<RoomName, List<Vector3>> Targets = new();

    /// <summary>从配置加载推荐目标点（房间名 → "x y z" 坐标字符串列表）。</summary>
    public static void LoadConfig(Dictionary<string, List<string>>? config)
    {
        Targets.Clear();

        if (config == null || config.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, List<string>> kv in config)
        {
            if (!Enum.TryParse(kv.Key, true, out RoomName name))
            {
                Logger.Warn($"[ScpBot] 目标点配置中的房间名无效，已忽略：'{kv.Key}'");
                continue;
            }

            List<Vector3> points = new();
            if (kv.Value != null)
            {
                foreach (string raw in kv.Value)
                {
                    if (RoomWaypoints.TryParsePosition(raw, out Vector3 pos))
                    {
                        points.Add(pos);
                    }
                    else
                    {
                        Logger.Warn($"[ScpBot] 房间 '{kv.Key}' 的目标点坐标无效，已忽略：'{raw}'（格式应为 \"x y z\"）");
                    }
                }
            }

            if (points.Count > 0)
            {
                Targets[name] = points;
            }
        }

        Logger.Info($"[ScpBot] 大房间推荐目标点已加载，共 {Targets.Count} 个房间配置了目标点。");
    }

    /// <summary>取指定房间中离 <paramref name="position"/> 最近的目标点；未配置返回 false。</summary>
    public static bool TryGetClosest(RoomName room, Vector3 position, out Vector3 closest)
    {
        closest = default;

        if (!Targets.TryGetValue(room, out List<Vector3>? points) || points.Count == 0)
        {
            return false;
        }

        float best = float.MaxValue;
        foreach (Vector3 point in points)
        {
            float d = (point - position).sqrMagnitude;
            if (d < best)
            {
                best = d;
                closest = point;
            }
        }

        return true;
    }

    /// <summary>指定房间已配置的目标点数量。</summary>
    public static int GetCount(RoomName room)
    {
        return Targets.TryGetValue(room, out List<Vector3>? points) ? points.Count : 0;
    }

    /// <summary>已配置目标点的房间名集合。</summary>
    public static IEnumerable<RoomName> GetAllRooms()
    {
        return Targets.Keys;
    }

    /// <summary>取指定房间的全部目标点。</summary>
    public static bool TryGetAll(RoomName room, out List<Vector3>? points)
    {
        points = null;
        return Targets.TryGetValue(room, out points);
    }
}
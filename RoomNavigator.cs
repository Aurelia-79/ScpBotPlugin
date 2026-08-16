using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using Logger = LabApi.Features.Console.Logger;
using MapGeneration;

namespace ScpBotPlugin;

/// <summary>
/// 房间图寻路器。
/// 邻居关系优先使用用户配置的房间图（BotConfig.RoomGraph）；
/// 未在配置中出现的房间回退到游戏原生相邻房间（AdjacentRooms，含电梯跨区）。
/// </summary>
public static class RoomNavigator
{
    private static readonly Dictionary<RoomName, HashSet<RoomName>> CustomGraph = new();
    private static readonly Dictionary<RoomName, HashSet<RoomName>> NativeNeighborCache = new();
    private static bool _graphLoaded;

    /// <summary>
    /// 从配置加载房间图（插件启用/重载时在主线程调用）。
    /// </summary>
    /// <param name="graph">配置的房间图（房间名 → 邻居房间名列表），可为 null。</param>
    public static void LoadGraph(Dictionary<string, List<string>>? graph)
    {
        CustomGraph.Clear();
        NativeNeighborCache.Clear();
        _graphLoaded = true;

        if (graph == null || graph.Count == 0)
        {
            Logger.Info("[ScpBot] 未配置自定义房间图，将全部使用游戏原生相邻房间寻路。");
            return;
        }

        foreach (KeyValuePair<string, List<string>> kv in graph)
        {
            if (!Enum.TryParse(kv.Key, true, out RoomName name))
            {
                Logger.Warn($"[ScpBot] 房间图配置中的房间名无效，已忽略：'{kv.Key}'");
                continue;
            }

            HashSet<RoomName> neighbors = new();
            if (kv.Value != null)
            {
                foreach (string neighbor in kv.Value)
                {
                    if (Enum.TryParse(neighbor, true, out RoomName neighborName))
                    {
                        neighbors.Add(neighborName);
                    }
                    else
                    {
                        Logger.Warn($"[ScpBot] 房间 '{kv.Key}' 的邻居房间名无效，已忽略：'{neighbor}'");
                    }
                }
            }

            CustomGraph[name] = neighbors;
        }

        Logger.Info($"[ScpBot] 自定义房间图已加载，共 {CustomGraph.Count} 个房间节点。");
    }

    /// <summary>
    /// 获取某房间的邻居房间名集合（用户配置优先，否则游戏原生 AdjacentRooms）。
    /// </summary>
    public static IEnumerable<RoomName> GetNeighbors(RoomName room)
    {
        if (_graphLoaded && CustomGraph.TryGetValue(room, out HashSet<RoomName>? custom))
        {
            return custom;
        }

        if (!NativeNeighborCache.TryGetValue(room, out HashSet<RoomName>? native))
        {
            native = new HashSet<RoomName>();
            foreach (Room wrapper in Room.Get(room))
            {
                foreach (Room adjacent in wrapper.AdjacentRooms)
                {
                    native.Add(adjacent.Name);
                }
            }

            NativeNeighborCache[room] = native;
        }

        return native;
    }

    /// <summary>
    /// BFS 求房间名路径。路径不含起点、含终点；找不到返回 null；同房间返回空列表。
    /// </summary>
    public static List<RoomName>? FindPath(RoomName start, RoomName goal)
    {
        if (start == goal)
        {
            return new List<RoomName>();
        }

        Queue<RoomName> queue = new();
        Dictionary<RoomName, RoomName> previous = new();
        HashSet<RoomName> visited = new() { start };
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            RoomName current = queue.Dequeue();

            foreach (RoomName next in GetNeighbors(current))
            {
                if (!visited.Add(next))
                {
                    continue;
                }

                previous[next] = current;

                if (next == goal)
                {
                    List<RoomName> path = new();
                    RoomName step = goal;
                    while (!step.Equals(start))
                    {
                        path.Add(step);
                        step = previous[step];
                    }

                    path.Reverse();
                    return path;
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    /// <summary>返回导航器已知的全部房间名（自定义图节点 + 原生相邻缓存键）。</summary>
    public static IEnumerable<RoomName> GetAllKnownRooms()
    {
        HashSet<RoomName> names = new(CustomGraph.Keys);
        foreach (RoomName name in NativeNeighborCache.Keys)
        {
            names.Add(name);
        }

        return names;
    }
}
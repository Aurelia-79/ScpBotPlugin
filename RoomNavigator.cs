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

            // FF-59：自动补全反向边 —— 用户配置 A→B 但漏写 B→A 时，BFS 从 B 出发无法到达 A，
            // 导致该 bot 永远无法沿房间路径返回 A（单向寻路死胡同）。自动补全使房间图变为无向图。
            if (neighbors.Count > 0)
            {
                foreach (RoomName n in neighbors)
                {
                    if (!CustomGraph.TryGetValue(n, out HashSet<RoomName>? nNeighbors))
                    {
                        nNeighbors = new HashSet<RoomName>();
                        CustomGraph[n] = nNeighbors;
                    }

                    if (!nNeighbors.Contains(name))
                    {
                        nNeighbors.Add(name);
                    }
                }
            }

            CustomGraph[name] = neighbors;
        }

        Logger.Info($"[ScpBot] 自定义房间图已加载，共 {CustomGraph.Count} 个房间节点。");
    }

    /// <summary>
    /// 获取某房间的邻居房间名集合（用户配置优先，否则游戏原生 AdjacentRooms）。
    /// FF-79：返回快照（数组）而非内部 HashSet 活引用 —— 调用方若意外修改返回集合
    /// 会污染内部房间图，且跨 tick 持有的活引用随 LoadGraph 重建而失效。
    /// </summary>
    public static IEnumerable<RoomName> GetNeighbors(RoomName room)
    {
        if (_graphLoaded && CustomGraph.TryGetValue(room, out HashSet<RoomName>? custom))
        {
            // 用 List 构造而非 ToArray()：项目内无 System.Linq using，ToArray 会解析到
            // 冲突的 CollectionExtensions（编译期类型推断失败）。
            return new List<RoomName>(custom);
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

        return new List<RoomName>(native);
    }

    /// <summary>清空原生邻居缓存（回合重置/地图变更时调用，FF-80：旧地图邻居关系失效）。</summary>
    public static void ClearNativeCache() => NativeNeighborCache.Clear();

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

    /// <summary>
    /// 求最多 maxPaths 条首边互不相同的房间路径（按长度升序：最短在前）。
    /// 算法：反复运行 BFS，每次把已找到路径的「首条边」（起点 → 第一个节点）从图中临时移除，
    /// 迫使下一次 BFS 走不同的出口方向。注意：不同路径在首条边之后可能仍共享中间节点，
    /// 保证的是「不同方向出发」而非「全程节点互斥」。
    /// 路径不含起点、含终点；同房间返回单条空路径；不足 maxPaths 条时返回实际找到的条数。
    /// 供多路线寻路 / 跨队夹击使用：多个 bot 追同一目标时分配不同路线。
    /// </summary>
    public static List<List<RoomName>> FindPaths(RoomName start, RoomName goal, int maxPaths)
    {
        List<List<RoomName>> result = new();
        // FF-55：maxPaths 来自配置（MaxRouteOptions），若管理员配置了超大值，
        // 主线程会跑 maxPaths 轮 BFS 卡死服务器。钳制到 16 条上限（多路线战术用不了更多）。
        if (maxPaths <= 0)
        {
            return result;
        }

        maxPaths = Math.Min(maxPaths, 16);

        if (start == goal)
        {
            result.Add(new List<RoomName>());
            return result;
        }

        // 被禁止的首条边集合：{(start, firstNode)}，避免多条路径共用同一个出口方向。
        HashSet<(RoomName From, RoomName To)> bannedFirstEdges = new();

        for (int attempt = 0; attempt < maxPaths; attempt++)
        {
            List<RoomName>? path = FindPathAvoidingFirstEdges(start, goal, bannedFirstEdges);
            if (path == null)
            {
                break; // 没有更多可行路径
            }

            result.Add(path);

            // 把这条路径的「第一条边」加入禁用集合，下一条路径必须从不同方向出发。
            if (path.Count > 0)
            {
                bannedFirstEdges.Add((start, path[0]));
            }
        }

        return result;
    }

    /// <summary>带首边禁用集的 BFS 求路径（供 <see cref="FindPaths"/> 使用）。</summary>
    private static List<RoomName>? FindPathAvoidingFirstEdges(
        RoomName start,
        RoomName goal,
        HashSet<(RoomName From, RoomName To)> bannedFirstEdges)
    {
        Queue<RoomName> queue = new();
        Dictionary<RoomName, RoomName> previous = new();
        HashSet<RoomName> visited = new() { start };
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            RoomName current = queue.Dequeue();

            foreach (RoomName next in GetNeighbors(current))
            {
                // 起点出发的首条边被禁用则跳过（迫使走不同方向）。
                if (current.Equals(start) && bannedFirstEdges.Contains((start, next)))
                {
                    continue;
                }

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
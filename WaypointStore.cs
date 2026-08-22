using System;
using System.Collections.Generic;
using System.IO;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using Logger = LabApi.Features.Console.Logger;
using MapGeneration;
using UnityEngine;

namespace ScpBotPlugin;

/// <summary>
/// 航点独立配置模型：与主配置（scpbot.yml）分离，单独存于 waypoints.yml。
/// 字段命名与旧主配置中的同名键保持一致（room_waypoints / room_targets），便于迁移。
/// </summary>
public class WaypointConfig
{
    /// <summary>
    /// 房间内航点：房间名 → 多条独立路线；每条路线是该房间内部的路径点坐标列表（"x y z" 字符串）。
    /// 与旧 BotConfig.RoomWaypoints 结构一致。
    /// </summary>
    public Dictionary<string, List<List<string>>> RoomWaypoints { get; set; } = [];

    /// <summary>
    /// 大房间推荐目标点：房间名 → 该房间内的关键地标坐标列表（"x y z" 字符串）。
    /// 与旧 BotConfig.RoomTargets 结构一致。
    /// </summary>
    public Dictionary<string, List<string>> RoomTargets { get; set; } = [];
}

/// <summary>
/// 航点/目标点的独立文件存储：负责 waypoints.yml 的加载、保存、热重载与旧配置一次性迁移。
///
/// 文件位置与 scpbot.yml 相同（LabAPI 插件配置目录），文件名 waypoints.yml。
/// 热加载两条通道：
///  1. labapi reload configs → 插件 LoadConfigs() → <see cref="Load"/>（本类被 BotPlugin 覆盖的 LoadConfigs 调用）；
///  2. 运行中直接编辑 waypoints.yml → BotManager 每 tick 调 <see cref="CheckForReload"/> 检测文件修改时间自动重载。
/// 自动写入：bot wp export 调 <see cref="Save"/>；文件不存在时 <see cref="Load"/> 会自动创建空文件。
/// </summary>
public static class WaypointStore
{
    /// <summary>航点配置文件名称（与主配置同目录）。</summary>
    public const string FileName = "waypoints.yml";

    /// <summary>上次加载时的文件修改时间（UTC），用于热重载检测。</summary>
    private static DateTime _lastWriteUtc = DateTime.MinValue;

    /// <summary>上次文件 stat 检查的时间（realtimeSinceStartup），用于节流（FF-77/81）。</summary>
    private static float _lastReloadCheckTime;

    /// <summary>
    /// 从 waypoints.yml 加载航点并应用到运行时（RoomWaypoints / RoomTargets）。
    /// 文件不存在时：若旧主配置 scpbot.yml 中还有遗留的 RoomWaypoints/RoomTargets 数据，
    /// 一次性迁移到 waypoints.yml；否则自动创建空文件。
    /// 插件 LoadConfigs（含 labapi reload configs）与首次启动都会调用。
    /// </summary>
    /// <param name="plugin">插件实例（LoadConfigs 阶段 Instance 尚未赋值，必须显式传入）。</param>
    public static void Load(BotPlugin plugin)
    {
        try
        {
            string path = plugin.GetConfigPath(FileName);

            // 文件不存在：尝试从旧 scpbot.yml 迁移，迁移不了则交给 TryLoadConfig 自动创建空文件。
            if (!File.Exists(path))
            {
                TryMigrateLegacy(plugin);
            }

            if (plugin.TryLoadConfig(FileName, out WaypointConfig? config))
            {
                // FF-17：空文件/纯注释时 YamlDotNet 返回 null 且 TryLoadConfig 返回 true，
                // 必须显式判空，否则 config.RoomWaypoints.Count 抛 NRE 被外层 catch 吞掉、
                // 航点静默不加载且文件被 TryLoadConfig 毒化为 "---\n"。
                if (config == null)
                {
                    Logger.Warn($"[ScpBot] {FileName} 内容为空或无法解析，已按空航点处理（如需恢复请重新导出）。");
                    config = new WaypointConfig();
                }

                Apply(config);
                _lastWriteUtc = GetWriteTimeUtc(plugin);
                Logger.Info($"[ScpBot] 航点已加载（{FileName}）：{config.RoomWaypoints.Count} 个房间航点、{config.RoomTargets.Count} 个房间目标点。");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ScpBot] 加载航点文件失败（{FileName}）：{ex}");
        }
    }

    /// <summary>
    /// 把当前运行时内存中的全部航点/目标点写回 waypoints.yml 并立即生效（bot wp export 调用）。
    /// </summary>
    /// <param name="plugin">插件实例。</param>
    /// <returns>是否保存成功。</returns>
    public static bool Save(BotPlugin plugin)
    {
        try
        {
            WaypointConfig config = new()
            {
                RoomWaypoints = BuildWaypointDict(),
                RoomTargets = BuildTargetsDict(),
            };

            // FF-56：BuildWaypointDict / BuildTargetsDict 在内存中没有航点/目标点时返回空字典，
            // 直接 Apply 会走 RoomWaypoints.LoadConfig({}) → Routes.Clear() → 清空全部航点（含已配好的），
            // 造成「bot wp export 后所有航点消失」。只有确有内容时才 Apply + 落盘。
            if (config.RoomWaypoints.Count == 0 && config.RoomTargets.Count == 0)
            {
                Logger.Warn("[ScpBot] 保存航点：当前无航点/目标点数据，跳过写入 waypoints.yml。");
                return false;
            }

            if (!plugin.TrySaveConfig(config, FileName))
            {
                Logger.Error($"[ScpBot] 保存航点文件失败（{FileName}）。");
                return false;
            }

            // 立即生效（不依赖热重载检测），并同步记录文件修改时间。
            Apply(config);
            _lastWriteUtc = GetWriteTimeUtc(plugin);
            Logger.Info($"[ScpBot] 航点已保存至 {FileName}（{config.RoomWaypoints.Count} 个房间航点、{config.RoomTargets.Count} 个房间目标点）。");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ScpBot] 保存航点文件失败（{FileName}）：{ex}");
            return false;
        }
    }

    /// <summary>
    /// 热重载检测：waypoints.yml 在运行中被外部修改时自动重新加载（由 BotManager 每 tick 调用）。
    /// 文件被 bot wp export 自己写回时不会重复加载（已记录最新修改时间）。
    /// </summary>
    public static void CheckForReload()
    {
        if (BotPlugin.Instance == null)
        {
            return;
        }

        // FF-77/81：File.Exists + GetLastWriteTimeUtc 是文件系统 I/O，每 tick（10Hz）调用浪费
        // 且无必要（人工编辑文件不会那么频繁）。节流到 1 秒检查一次。
        float now = Time.realtimeSinceStartup;
        if (now - _lastReloadCheckTime < 1f)
        {
            return;
        }

        _lastReloadCheckTime = now;

        try
        {
            string path = BotPlugin.Instance.GetConfigPath(FileName);
            if (!File.Exists(path))
            {
                return;
            }

            DateTime current = File.GetLastWriteTimeUtc(path);
            if (current == _lastWriteUtc)
            {
                return;
            }

            // FF-17：必须先读成功再更新 _lastWriteUtc —— 此前先记时间后读取，
            // 解析失败（畸形/半写/空文件）时时间戳已推进、mtime 不再变化 → 永不重试，
            // bot 永久沿用旧航点。现在读取失败会保留旧时间戳，文件修正后 mtime 变化即自动重试。
            if (BotPlugin.Instance.TryReadConfig(FileName, out WaypointConfig? config))
            {
                // FF-17：空文件/纯注释 → YamlDotNet 返回 null 且 TryReadConfig 返回 true；
                // 且「RoomWaypoints:」空 section 的字段为 null，若直接 Apply 会触发
                // RoomWaypoints.LoadConfig(null) → Routes.Clear() 清空全部航点。显式判空。
                if (config == null)
                {
                    Logger.Warn($"[ScpBot] {FileName} 内容为空或无法解析，已按空航点处理（如需恢复请重新导出）。");
                    config = new WaypointConfig();
                }

                Apply(config);
                _lastWriteUtc = current;
                Logger.Info($"[ScpBot] 检测到 {FileName} 被修改，已热重载（{config.RoomWaypoints.Count} 个房间航点、{config.RoomTargets.Count} 个房间目标点）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ScpBot] 热重载航点文件失败（{FileName}）：{ex}");
        }
    }

    /// <summary>把配置模型应用到运行时静态数据（RoomWaypoints / RoomTargets）。</summary>
    private static void Apply(WaypointConfig config)
    {
        RoomWaypoints.LoadConfig(config.RoomWaypoints);
        RoomTargets.LoadConfig(config.RoomTargets);
    }

    /// <summary>
    /// 一次性迁移：waypoints.yml 尚不存在时，若旧主配置 scpbot.yml 中仍有 RoomWaypoints/RoomTargets
    /// 数据（旧版本 bot wp export 写入的），自动搬到独立文件。
    /// </summary>
    private static void TryMigrateLegacy(BotPlugin plugin)
    {
        try
        {
            // 用 LabAPI 配置解析器反序列化主配置，但只取航点字段：
            // LegacyWaypointDto 没有其它属性，IgnoreUnmatchedProperties 会忽略主配置的其余字段。
            if (!plugin.TryReadConfig(plugin.ConfigFileName, out LegacyWaypointDto? legacy)
                || legacy == null)
            {
                return;
            }

            if ((legacy.RoomWaypoints == null || legacy.RoomWaypoints.Count == 0)
                && (legacy.RoomTargets == null || legacy.RoomTargets.Count == 0))
            {
                return;   // 旧配置里没有航点数据，无需迁移
            }

            WaypointConfig migrated = new()
            {
                RoomWaypoints = legacy.RoomWaypoints ?? [],
                RoomTargets = legacy.RoomTargets ?? [],
            };

            if (plugin.TrySaveConfig(migrated, FileName))
            {
                Logger.Info($"[ScpBot] 已把旧 scpbot.yml 中的航点/目标点迁移到 {FileName}（原配置键可手动删除）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ScpBot] 尝试从旧配置迁移航点失败（跳过，将创建空航点文件）：{ex.Message}");
        }
    }

    /// <summary>从运行时内存重建航点配置字典（Vector3 → "x y z" 字符串）。</summary>
    private static Dictionary<string, List<List<string>>> BuildWaypointDict()
    {
        Dictionary<string, List<List<string>>> dict = new();
        foreach (RoomName room in RoomWaypoints.GetAllRooms())
        {
            if (!RoomWaypoints.TryGetRoutes(room, out List<List<Vector3>>? routes)
                || routes == null || routes.Count == 0)
            {
                continue;
            }

            List<List<string>> roomRoutes = new();
            foreach (List<Vector3> route in routes)
            {
                if (route == null || route.Count == 0)
                {
                    continue;
                }

                List<string> routeStrings = new();
                foreach (Vector3 point in route)
                {
                    routeStrings.Add(RoomWaypoints.Format(point));
                }

                roomRoutes.Add(routeStrings);
            }

            if (roomRoutes.Count > 0)
            {
                dict[room.ToString()] = roomRoutes;
            }
        }

        return dict;
    }

    /// <summary>从运行时内存重建目标点配置字典（Vector3 → "x y z" 字符串）。</summary>
    private static Dictionary<string, List<string>> BuildTargetsDict()
    {
        Dictionary<string, List<string>> dict = new();
        foreach (RoomName room in RoomTargets.GetAllRooms())
        {
            if (!RoomTargets.TryGetAll(room, out List<Vector3>? points)
                || points == null || points.Count == 0)
            {
                continue;
            }

            List<string> targetStrings = new();
            foreach (Vector3 point in points)
            {
                targetStrings.Add(RoomWaypoints.Format(point));
            }

            dict[room.ToString()] = targetStrings;
        }

        return dict;
    }

    private static DateTime GetWriteTimeUtc(BotPlugin plugin)
    {
        try
        {
            return File.GetLastWriteTimeUtc(plugin.GetConfigPath(FileName));
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>旧主配置中航点字段的只读模型（迁移用，忽略其余配置字段）。</summary>
    private sealed class LegacyWaypointDto
    {
        public Dictionary<string, List<List<string>>>? RoomWaypoints { get; set; }

        public Dictionary<string, List<string>>? RoomTargets { get; set; }
    }
}

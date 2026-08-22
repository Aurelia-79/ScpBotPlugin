using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using UnityEngine;

namespace ScpBotPlugin.ExternalAI;

/// <summary>
/// 外部 AI 协议的 JSON 行生成器（cfg 静态数据 / snap 动态快照）。
/// 结构为本插件与 ai_server.py 共同约定的固定格式，字段变化需两端同步修改。
/// </summary>
public static class ExternalAiProtocol
{
    /// <summary>连接后由主线程发送一次的静态数据：房间图（邻居+中心）、航点路线、推荐目标点。</summary>
    public static string BuildConfigJson()
    {
        StringBuilder sb = new();
        sb.Append("{\"type\":\"cfg\"");

        // 房间（含邻居与中心）：机器人与外部共享同一套邻居表（自定义图优先）。
        sb.Append(",\"rooms\":{");
        bool firstRoom = true;
        foreach (RoomName name in GetAllRoomNames())
        {
            List<RoomName> neighbors = new(RoomNavigator.GetNeighbors(name));
            Vector3? center = GetRoomCenter(name);
            if (neighbors.Count == 0 && !center.HasValue)
            {
                continue;
            }

            if (!firstRoom)
            {
                sb.Append(',');
            }

            firstRoom = false;
            sb.Append('"').Append(name).Append("\":{\"c\":");
            // FF-65：无中心坐标的房间输出 "c":null（而非 [0,0,0]）—— Python 端
            // room_info.get("c") 对 null 返回 None（falsy），寻路/候选会跳过该房间；
            // 若输出 [0,0,0]，外部 AI 会当真把 bot 派往世界原点。
            if (center.HasValue)
            {
                AppendVector(sb, center.Value);
            }
            else
            {
                sb.Append("null");
            }

            sb.Append(",\"a\":[");
            for (int i = 0; i < neighbors.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append('"').Append(neighbors[i]).Append('"');
            }

            sb.Append("]}");
        }

        sb.Append('}');

        // 航点路线。
        sb.Append(",\"routes\":{");
        bool firstRouteRoom = true;
        foreach (RoomName room in RoomWaypoints.GetAllRooms())
        {
            if (!RoomWaypoints.TryGetRoutes(room, out List<List<Vector3>>? routes) || routes == null || routes.Count == 0)
            {
                continue;
            }

            if (!firstRouteRoom)
            {
                sb.Append(',');
            }

            firstRouteRoom = false;
            sb.Append('"').Append(room).Append("\":[");

            for (int r = 0; r < routes.Count; r++)
            {
                if (r > 0)
                {
                    sb.Append(',');
                }

                sb.Append('[');
                List<Vector3> route = routes[r];
                for (int p = 0; p < route.Count; p++)
                {
                    if (p > 0)
                    {
                        sb.Append(',');
                    }

                    AppendVector(sb, route[p]);
                }

                sb.Append(']');
            }

            sb.Append(']');
        }

        sb.Append('}');

        // 大房间推荐目标点。
        sb.Append(",\"targets\":{");
        bool firstTargetRoom = true;
        foreach (RoomName room in RoomTargets.GetAllRooms())
        {
            if (!RoomTargets.TryGetAll(room, out List<Vector3>? points) || points == null || points.Count == 0)
            {
                continue;
            }

            if (!firstTargetRoom)
            {
                sb.Append(',');
            }

            firstTargetRoom = false;
            sb.Append('"').Append(room).Append("\":[");
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendVector(sb, points[i]);
            }

            sb.Append(']');
        }

        sb.Append("}}");
        return sb.ToString();
    }

    /// <summary>
    /// 动态快照：全部机器人 + 全部真实玩家（含房间、队伍、血量）。
    /// 每个 bot 额外带 role（角色）与 enemies（本地算好的可见敌人列表，含视线结果），
    /// 供外部 AI 做索敌/走位/开火决策。
    /// </summary>
    public static string BuildSnapshotJson(IReadOnlyCollection<Bot> bots, BotConfig config)
    {
        StringBuilder sb = new();
        sb.Append("{\"type\":\"snap\"");

        sb.Append(",\"bots\":[");
        bool firstBot = true;
        foreach (Bot bot in bots)
        {
            // FF-66：bot 可能在快照构建期间被销毁（DisposeAndRemove），
            // 访问 Position / Health 等 Unity 对象抛 NRE 会作废整个快照。跳过无效 bot。
            if (!bot.IsValid)
            {
                continue;
            }

            if (!firstBot)
            {
                sb.Append(',');
            }

            firstBot = false;
            sb.Append("{\"id\":").Append(bot.Id.ToString(CultureInfo.InvariantCulture))
              .Append(",\"p\":");
            AppendVector(sb, bot.Position);
            sb.Append(",\"r\":\"");
            if (bot.CurrentRoomName.HasValue)
            {
                sb.Append(bot.CurrentRoomName.Value);
            }

            sb.Append("\",\"t\":\"").Append(bot.Team).Append("\",\"h\":")
              .Append(bot.Health.ToString("F0", CultureInfo.InvariantCulture))
              .Append(",\"role\":\"").Append(bot.Player.Role).Append('"');

            // 击杀/阵亡累计值（神经网络学习奖励：Python 端 diff 得增量）。
            sb.Append(",\"kills\":").Append(bot.Kills.ToString(CultureInfo.InvariantCulture))
              .Append(",\"deaths\":").Append(bot.Deaths.ToString(CultureInfo.InvariantCulture));

            // 背包物品摘要（手榴弹/闪光弹/医疗），供外部 AI 投掷与自疗决策。
            (int he, int flash, int med) = bot.ItemSummary;
            sb.Append(",\"items\":{\"he\":").Append(he.ToString(CultureInfo.InvariantCulture))
              .Append(",\"flash\":").Append(flash.ToString(CultureInfo.InvariantCulture))
              .Append(",\"med\":").Append(med.ToString(CultureInfo.InvariantCulture)).Append('}');

            // 掩体状态：与目标视线被遮挡（地表岩石/建筑/箱子后）→ 1；供神经网络学躲掩体。
            sb.Append(",\"cover\":").Append(bot.InCover ? 1 : 0);

            // 本地算好的可见敌人列表（含视线结果）。
            sb.Append(",\"enemies\":[");
            List<EnemyPerception> enemies = bot.CollectEnemyPerceptions(config);
            for (int i = 0; i < enemies.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                EnemyPerception e = enemies[i];
                sb.Append("{\"n\":").Append(e.NetId.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"p\":");
                AppendVector(sb, e.Position);
                sb.Append(",\"ap\":");
                AppendVector(sb, e.AimPosition);
                sb.Append(",\"d\":").Append(e.Distance.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(",\"t\":\"").Append(e.Team).Append("\",\"vis\":")
                  .Append(e.Visible ? 1 : 0).Append('}');
            }

            sb.Append(']');

            // 候选路线（房间名序列），供神经网络路线选择；无路线时省略。
            IReadOnlyList<List<RoomName>>? routes = bot.CandidateRoutes;
            if (routes != null && routes.Count > 0)
            {
                sb.Append(",\"routes\":[");
                for (int ri = 0; ri < routes.Count; ri++)
                {
                    if (ri > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append('[');
                    List<RoomName> route = routes[ri];
                    for (int pi = 0; pi < route.Count; pi++)
                    {
                        if (pi > 0)
                        {
                            sb.Append(',');
                        }

                        sb.Append('"').Append(route[pi]).Append('"');
                    }

                    sb.Append(']');
                }

                sb.Append(']');
            }

            sb.Append('}');
        }

        sb.Append("],\"peers\":[");

        bool firstPeer = true;
        foreach (ReferenceHub hub in new List<ReferenceHub>(ReferenceHub.AllHubs))
        {
            if (hub == null || hub.isLocalPlayer || hub.IsDummy)
            {
                continue;
            }

            if (!firstPeer)
            {
                sb.Append(',');
            }

            firstPeer = false;

            Vector3 pos = hub.transform.position;
            RoomName? roomName = null;
            Player? p = Player.Get(hub);
            if (p != null)
            {
                roomName = p.Room?.Name ?? p.CachedRoom?.Name;
            }

            sb.Append("{\"n\":").Append(hub.netId.ToString(CultureInfo.InvariantCulture))
              .Append(",\"p\":");
            AppendVector(sb, pos);
            sb.Append(",\"r\":\"");
            if (roomName.HasValue)
            {
                sb.Append(roomName.Value);
            }

            sb.Append("\",\"t\":\"").Append(hub.GetTeam()).Append("\",\"a\":")
              .Append(hub.IsAlive() ? 1 : 0).Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static void AppendVector(StringBuilder sb, Vector3 v)
    {
        sb.Append('[')
          .Append(v.x.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
          .Append(v.y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
          .Append(v.z.ToString("F2", CultureInfo.InvariantCulture)).Append(']');
    }

    private static List<RoomName> GetAllRoomNames()
    {
        HashSet<RoomName> names = new();

        foreach (Room room in Room.List)
        {
            if (room != null && !room.IsDestroyed && room.Name != RoomName.Unnamed)
            {
                names.Add(room.Name);
            }
        }

        foreach (RoomName name in RoomNavigator.GetAllKnownRooms())
        {
            names.Add(name);
        }

        foreach (RoomName name in RoomWaypoints.GetAllRooms())
        {
            names.Add(name);
        }

        foreach (RoomName name in RoomTargets.GetAllRooms())
        {
            names.Add(name);
        }

        return new List<RoomName>(names);
    }

    private static Vector3? GetRoomCenter(RoomName name)
    {
        foreach (Room room in Room.Get(name))
        {
            if (room != null && !room.IsDestroyed)
            {
                return room.Position;
            }
        }

        return null;
    }
}
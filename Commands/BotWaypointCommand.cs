using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommandSystem;
using LabApi.Features.Wrappers;
using Logger = LabApi.Features.Console.Logger;
using MapGeneration;
using UnityEngine;

namespace ScpBotPlugin.Commands;

/// <summary>
/// 房间内航点管理父命令（绕障碍/快捷走法，一个房间可配多条路线随机选用）：
/// bot wp new / add / list / clear / export
/// </summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotWaypointCommand : ParentCommand
{
    /// <inheritdoc />
    public override string Command => "wp";

    /// <inheritdoc />
    public override string[] Aliases => ["waypoint"];

    /// <inheritdoc />
    public override string Description => "房间内航点管理（多条路线随机选用）。子命令：new / add / list / clear / export";

    /// <inheritdoc />
    public override void LoadGeneratedCommands()
    {
        RegisterCommand(new BotWaypointNewCommand());
        RegisterCommand(new BotWaypointAddCommand());
        RegisterCommand(new BotWaypointListCommand());
        RegisterCommand(new BotWaypointClearCommand());
        RegisterCommand(new BotWaypointExportCommand());
    }

    /// <inheritdoc />
    protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "用法: bot wp <new|add|list|clear|export> [房间名] [路线编号]";
        return false;
    }
}

/// <summary>为指定（或当前所在）房间开始一条新路线：bot wp new [房间名]。</summary>
[CommandHandler(typeof(BotWaypointCommand))]
public class BotWaypointNewCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "new";

    /// <inheritdoc />
    public string[] Aliases => ["n"];

    /// <inheritdoc />
    public string Description => "为指定（或当前所在）房间开始一条新路线，之后 add 的点都加进这条路线。";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        if (!BotWaypointRoomHelper.TryResolveRoom(arguments, sender, out RoomName room, out response))
        {
            return false;
        }

        int number = RoomWaypoints.GetRouteCount(room);
        RoomWaypoints.StartNewRoute(room);
        response = $"已为房间 {room} 开始第 {number + 1} 条路线。走到点位上后执行 bot wp add 录入航点。";
        BotWaypointFeedback.Broadcast(sender, $"已为 {room} 开始第 {number + 1} 条路线");
        return true;
    }
}

/// <summary>把执行者当前位置追加为房间（默认当前所在房间）的当前路线航点：bot wp add [房间名]。</summary>
[CommandHandler(typeof(BotWaypointCommand))]
public class BotWaypointAddCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "add";

    /// <inheritdoc />
    public string[] Aliases => ["a"];

    /// <inheritdoc />
    public string Description => "把执行者当前位置追加为指定（或当前所在）房间的当前路线航点（无活动路线则自动新建）。";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        if (!Player.TryGet(sender, out Player? player))
        {
            response = "该命令只能由服务器内的玩家执行（需要在游戏里站到目标位置）。";
            return false;
        }

        if (!BotWaypointRoomHelper.TryResolveRoom(arguments, sender, out RoomName room, out response))
        {
            return false;
        }

        int routeCountBefore = RoomWaypoints.GetRouteCount(room);
        // FF-60：wp add 在无路线时新建一条（编号 0），有路线时追加到最后一条（编号 count-1）。
        // 此前用 `GetRouteCount - 1` 在无路线时得 -1，Math.Max 掩盖后显示「路线 #0」实为新建，误导管理员。
        bool isNewRoute = routeCountBefore == 0;
        int routeIndex = isNewRoute ? 0 : routeCountBefore - 1;
        int pointIndex = isNewRoute ? 0 : RoomWaypoints.GetPointCount(room, routeIndex);

        RoomWaypoints.AddPoint(room, player.Position);

        string routeLabel = isNewRoute ? $"新路线 #{routeIndex}" : $"路线 #{routeIndex}";
        string formatted = RoomWaypoints.Format(player.Position);
        Logger.Info($"[ScpBot] 房间 {room} {routeLabel} 已追加航点 #{(pointIndex + 1)}：{{{formatted}}}");

        response = $"已把当前位置录入为房间 {room} {routeLabel} 的第 {(pointIndex + 1)} 个航点（{formatted}）。\n"
            + "继续走到下一个点位再执行 bot wp add；全部完成后用 bot wp export 复制 YAML 保存到配置文件。";
        BotWaypointFeedback.Broadcast(sender, $"航点已录入 {room} {routeLabel} 点#{(pointIndex + 1)}");
        return true;
    }
}

/// <summary>列出航点：bot wp list [房间名]。</summary>
[CommandHandler(typeof(BotWaypointCommand))]
public class BotWaypointListCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "list";

    /// <inheritdoc />
    public string[] Aliases => ["ls"];

    /// <inheritdoc />
    public string Description => "列出已配置的房间路线与航点。";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        StringBuilder sb = new();

        if (arguments.Count > 0)
        {
            if (!Enum.TryParse(arguments.At(0), true, out RoomName room))
            {
                response = $"房间名无效：'{arguments.At(0)}'";
                return false;
            }

            AppendRoom(sb, room);
        }
        else
        {
            foreach (RoomName room in RoomWaypoints.GetAllRooms())
            {
                AppendRoom(sb, room);
            }
        }

        response = sb.Length == 0 ? "尚未配置任何房间航点。" : sb.ToString();
        return true;
    }

    private static void AppendRoom(StringBuilder sb, RoomName room)
    {
        if (!RoomWaypoints.TryGetRoutes(room, out List<List<Vector3>>? routes) || routes == null || routes.Count == 0)
        {
            return;
        }

        sb.AppendLine($"房间 {room}（{routes.Count} 条路线）：");
        for (int r = 0; r < routes.Count; r++)
        {
            sb.AppendLine($"  路线 #{r}（{routes[r].Count} 个点）：");
            foreach (Vector3 point in routes[r])
            {
                sb.AppendLine($"    {RoomWaypoints.Format(point)}");
            }
        }
    }
}

/// <summary>删除航点：bot wp clear &lt;房间名&gt; [路线编号]（不填编号则清空该房间全部路线）。</summary>
[CommandHandler(typeof(BotWaypointCommand))]
public class BotWaypointClearCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "clear";

    /// <inheritdoc />
    public string[] Aliases => ["c", "remove"];

    /// <inheritdoc />
    public string Description => "删除指定房间的某条路线（或全部路线）。";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        if (arguments.Count < 1)
        {
            response = "用法: bot wp clear <房间名> [路线编号]";
            return false;
        }

        if (!Enum.TryParse(arguments.At(0), true, out RoomName room))
        {
            response = $"房间名无效：'{arguments.At(0)}'";
            return false;
        }

        if (arguments.Count >= 2 && int.TryParse(arguments.At(1), out int routeIndex))
        {
            if (!RoomWaypoints.ClearRoute(room, routeIndex))
            {
                response = $"房间 {room} 没有路线 #{routeIndex}。用 bot wp list {room} 查看。";
                return false;
            }

            response = $"已删除房间 {room} 的路线 #{routeIndex}。剩余路线数：{RoomWaypoints.GetRouteCount(room)}";
            BotWaypointFeedback.Broadcast(sender, $"已删除 {room} 路线#{routeIndex}，剩余 {RoomWaypoints.GetRouteCount(room)} 条");
            return true;
        }

        RoomWaypoints.Clear(room);
        response = $"已清空房间 {room} 的全部路线。";
        BotWaypointFeedback.Broadcast(sender, $"已清空 {room} 的全部路线");
        return true;
    }
}

/// <summary>
/// 把内存中当前所有路线/目标点直接写入独立航点文件 waypoints.yml 并立即生效（bot wp export）。
/// 航点数据与主配置 scpbot.yml 分离，热加载与自动写入保持不变：
/// 写入后立即应用；之后直接编辑 waypoints.yml 也会被自动热重载。
/// </summary>
[CommandHandler(typeof(BotWaypointCommand))]
public class BotWaypointExportCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "export";

    /// <inheritdoc />
    public string[] Aliases => ["e", "dump", "save"];

    /// <inheritdoc />
    public string Description => "把当前内存中的全部航点/目标点写入独立文件 waypoints.yml 并立即生效。";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        if (BotPlugin.Instance == null)
        {
            response = "插件尚未启用，无法保存航点。";
            return false;
        }

        bool hasWaypoints = RoomWaypoints.GetTotalRouteCount() > 0;
        bool hasTargets = RoomTargets.GetAllRooms().Any();
        if (!hasWaypoints && !hasTargets)
        {
            response = "当前没有可保存的路线/目标点。先用 bot wp new / add 录入。";
            return false;
        }

        try
        {
            if (!WaypointStore.Save(BotPlugin.Instance))
            {
                response = $"保存航点文件失败（{WaypointStore.FileName}），详见服务器日志。";
                return false;
            }
        }
        catch (Exception ex)
        {
            response = $"保存航点文件失败：{ex.Message}";
            return false;
        }

        int wpCount = RoomWaypoints.GetAllRooms().Count();
        int totalRoutes = RoomWaypoints.GetTotalRouteCount();
        int targetCount = RoomTargets.GetAllRooms().Count();

        response = $"已保存至 {WaypointStore.FileName}（{wpCount} 个房间 {totalRoutes} 条路线，{targetCount} 个房间目标点），寻路立即生效。";
        BotWaypointFeedback.Broadcast(sender, $"航点已写入 {WaypointStore.FileName} 并生效（{wpCount} 房间 {totalRoutes} 路线）");
        return true;
    }
}

/// <summary>从参数解析房间名；未指定时用执行者当前所在房间。</summary>
internal static class BotWaypointRoomHelper
{
    public static bool TryResolveRoom(ArraySegment<string> arguments, ICommandSender sender, out RoomName room, out string response)
    {
        room = default;
        response = string.Empty;

        if (arguments.Count > 0)
        {
            if (Enum.TryParse(arguments.At(0), true, out RoomName parsed))
            {
                room = parsed;
                return true;
            }

            response = $"房间名无效：'{arguments.At(0)}'。可用 bot wp list 查看已配置房间，或用 bot room 看当前房间名。";
            return false;
        }

        if (Player.TryGet(sender, out Player? player))
        {
            RoomName? current = player.Room?.Name ?? player.CachedRoom?.Name;
            if (current.HasValue)
            {
                room = current.Value;
                return true;
            }
        }

        response = "无法确定你当前所在的房间，请手动指定房间名：bot wp <new|add> <房间名>";
        return false;
    }
}

/// <summary>给命令执行者（若为游戏内玩家）发屏幕中央 Broadcast 提示。</summary>
internal static class BotWaypointFeedback
{
    public static void Broadcast(ICommandSender sender, string message, ushort duration = 4)
    {
        if (Player.TryGet(sender, out Player? player) && player != null)
        {
            player.SendBroadcast(message, duration);
        }
    }
}
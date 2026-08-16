using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using Mirror;
using UnityEngine;

namespace ScpBotPlugin.Commands;

/// <summary>显示机器人详细实时状态（含坐标/移动/视线）：bot status &lt;id&gt;。</summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotStatusCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "status";

    /// <inheritdoc />
    public string[] Aliases => ["st"];

    /// <inheritdoc />
    public string Description => "显示指定机器人的详细实时状态，用于诊断寻路/消失问题。";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        if (arguments.Count < 1 || !int.TryParse(arguments.At(0), out int id))
        {
            response = "用法: bot status <id>";
            return false;
        }

        Bot[] bots = BotManager.Snapshot();
        Bot? bot = Array.Find(bots, b => b.Id == id);
        if (bot == null)
        {
            response = $"未找到机器人 #{id}。";
            return false;
        }

        try
        {
            response = GatherStatus(bot);
        }
        catch (Exception ex)
        {
            response = $"获取状态失败：{ex.Message}";
        }

        return true;
    }

    private static string GatherStatus(Bot bot)
    {
        ReferenceHub hub = bot.Hub;
        Player player = bot.Player;

        string pos = "N/A";
        string rp = "N/A";
        string rpWaypoint = "N/A";
        string rpOutOfRange = "false";
        string isPending = bot.IsPendingLoadout ? "是" : "否";

        if (hub != null && hub.roleManager.CurrentRole is PlayerRoles.FirstPersonControl.IFpcRole fpc)
        {
            Vector3 p = fpc.FpcModule.Position;
            pos = $"{p.x:F2} {p.y:F2} {p.z:F2}";
            rpWaypoint = fpc.FpcModule.Motor.ReceivedPosition.WaypointId.ToString();
            rp = $"{fpc.FpcModule.Motor.ReceivedPosition.Position.x:F2} {fpc.FpcModule.Motor.ReceivedPosition.Position.y:F2} {fpc.FpcModule.Motor.ReceivedPosition.Position.z:F2}";
            rpOutOfRange = fpc.FpcModule.Motor.ReceivedPosition.OutOfRange.ToString().ToLower();
        }

        return $"#{bot.Id} {bot.Name}\n"
            + $"存活={player.IsAlive}  配装中={isPending}  队伍={player.Team}\n"
            + $"当前房间={bot.CurrentRoomName?.ToString() ?? "(未知)"}\n"
            + $"位置={pos}\n"
            + $"ReceivedPosition={rp}  waypointId={rpWaypoint}  OutOfRange={rpOutOfRange}\n"
            + $"目标房间={bot.TargetRoomName?.ToString() ?? "(无)"}  路径={bot.PathSummary}";
    }
}

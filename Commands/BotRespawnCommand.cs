using System;
using CommandSystem;

namespace ScpBotPlugin.Commands;

/// <summary>切换死亡自动复活：bot respawn [on|off|status]。</summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotRespawnCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "respawn";

    /// <inheritdoc />
    public string[] Aliases => ["revive", "r"];

    /// <inheritdoc />
    public string Description => "切换机器人死亡自动复活。用法: bot respawn <on|off|status>（默认开启）";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        if (arguments.Count == 0)
        {
            response = $"当前自动复活：{(BotManager.RespawnEnabled ? "开启" : "关闭")}。用法: bot respawn <on|off|status>";
            return true;
        }

        switch (arguments.At(0).ToLowerInvariant())
        {
            case "on":
            case "true":
            case "1":
            case "enable":
                BotManager.RespawnEnabled = true;
                response = "已开启自动复活：bot 死亡后会以生前角色复活。";
                return true;

            case "off":
            case "false":
            case "0":
            case "disable":
                BotManager.RespawnEnabled = false;
                response = "已关闭自动复活：当前这波 bot 打完即销毁，不再复活。";
                return true;

            case "status":
                response = $"自动复活：{(BotManager.RespawnEnabled ? "开启" : "关闭")}。";
                return true;

            default:
                response = $"参数无效：'{arguments.At(0)}'。用法: bot respawn <on|off|status>";
                return false;
        }
    }
}

using System;
using System.Text;
using CommandSystem;

namespace ScpBotPlugin.Commands;

/// <summary>列出当前机器人：bot list。</summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotListCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "list";

    /// <inheritdoc />
    public string[] Aliases => ["ls"];

    /// <inheritdoc />
    public string Description => "列出所有存活的机器人。";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        Bot[] bots = BotManager.Snapshot();
        if (bots.Length == 0)
        {
            response = "当前没有存活的机器人。";
            return true;
        }

        StringBuilder sb = new();
        sb.AppendLine($"共 {bots.Length} 个机器人：");
        foreach (Bot bot in bots)
        {
            sb.AppendLine($"  #{bot.Id}  {bot.Name}  存活={bot.IsAlive}");
        }

        response = sb.ToString();
        return true;
    }
}

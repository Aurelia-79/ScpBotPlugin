using System;
using CommandSystem;

namespace ScpBotPlugin.Commands;

/// <summary>销毁机器人：bot kill all | bot kill &lt;id&gt;。</summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotKillCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "kill";

    /// <inheritdoc />
    public string[] Aliases => ["remove", "destroy", "k"];

    /// <inheritdoc />
    public string Description => "销毁机器人：bot kill all 或 bot kill <id>";

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
            response = "用法: bot kill all | bot kill <id>";
            return false;
        }

        string arg = arguments.At(0);

        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            BotManager.RequestKillAll();
            response = "已请求销毁全部机器人。";
            return true;
        }

        if (int.TryParse(arg, out int id))
        {
            BotManager.RequestKill(id);
            response = $"已请求销毁机器人 #{id}。";
            return true;
        }

        response = "参数无效：需要 all 或数字 id。";
        return false;
    }
}

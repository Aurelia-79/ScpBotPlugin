using System;
using System.Linq;
using CommandSystem;

namespace ScpBotPlugin.Commands;

/// <summary>显示机器人当前寻路路径：bot path &lt;id&gt;。</summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotPathCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "path";

    /// <inheritdoc />
    public string[] Aliases => ["route"];

    /// <inheritdoc />
    public string Description => "显示指定机器人的寻路路径。";

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
            response = "用法: bot path <id>";
            return false;
        }

        Bot[] bots = BotManager.Snapshot();
        Bot? target = bots.FirstOrDefault(b => b.Id == id);
        if (target == null)
        {
            response = $"未找到机器人 #{id}。";
            return false;
        }

        response = $"#{target.Id} {target.Name}\n"
            + $"当前房间: {target.CurrentRoomName?.ToString() ?? "(未知)"}\n"
            + $"目标房间: {target.TargetRoomName?.ToString() ?? "(无目标)"}\n"
            + $"路径: {target.PathSummary}";
        return true;
    }
}
using System;
using System.Linq;
using System.Text;
using CommandSystem;

namespace ScpBotPlugin.Commands;

/// <summary>显示机器人当前所在房间，方便玩家补全房间图：bot room [id]。</summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotRoomCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "room";

    /// <inheritdoc />
    public string[] Aliases => ["where"];

    /// <inheritdoc />
    public string Description => "显示机器人当前所在房间（不填 id 则显示全部）。";

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

        if (arguments.Count == 0)
        {
            StringBuilder sb = new();
            sb.AppendLine($"共 {bots.Length} 个机器人：");
            foreach (Bot bot in bots)
            {
                string roomName = bot.CurrentRoomName?.ToString() ?? "(未知)";
                sb.AppendLine($"  #{bot.Id}  {bot.Name}  当前房间={roomName}");
            }

            response = sb.ToString();
            return true;
        }

        if (!int.TryParse(arguments.At(0), out int id))
        {
            response = "参数无效：应为机器人 id。";
            return false;
        }

        Bot? target = bots.FirstOrDefault(b => b.Id == id);
        if (target == null)
        {
            response = $"未找到机器人 #{id}。";
            return false;
        }

        response = $"机器人 #{target.Id} 当前房间：{target.CurrentRoomName?.ToString() ?? "(未知)"}；目标房间：{target.TargetRoomName?.ToString() ?? "(无目标)"}";
        return true;
    }
}
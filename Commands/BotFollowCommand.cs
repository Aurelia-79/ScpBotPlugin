using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using PlayerRoles;

namespace ScpBotPlugin.Commands;

/// <summary>
/// 带领机器人走正确路线（示教学习用）：
///   bot follow &lt;玩家&gt; [all|id]   —— 让指定/全部机器人跟随该玩家，沿途记录经过的房间序列
///   bot follow stop [all|id]         —— 停止跟随并提交示教轨迹给神经网络学习
///   bot follow list                  —— 查看当前跟随中的机器人
/// 用法示例：玩家 "Alice" 想带 bot #3 从重收容走到入口区，先执行 bot follow Alice 3，
/// 然后正常走路，bot 会跟着走；到达后执行 bot follow stop 3，轨迹自动发给外部 AI 学习。
/// </summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotFollowCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "follow";

    /// <inheritdoc />
    public string[] Aliases => ["lead", "teach", "f"];

    /// <inheritdoc />
    public string Description => "带领机器人走正确路线（示教学习）。用法: bot follow <玩家> [all|id] | bot follow stop [all|id] | bot follow list";

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
            response = "用法: bot follow <玩家> [all|id] | bot follow stop [all|id] | bot follow list";
            return false;
        }

        string action = arguments.At(0);

        // 查看跟随状态。
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            response = BotManager.FollowStatus();
            return true;
        }

        // 停止跟随。
        if (action.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            int? id = arguments.Count > 1 ? ParseId(arguments.At(1)) : null;
            int stopped = BotManager.StopFollow(id);
            // FF-61：区分「真的停止了跟随」与「没有 bot 在跟随」—— 此前无论 stopped 是 0 还是正数，
            // 都显示「已停止并提交示教轨迹」，当没有 bot 在跟随时是误报。
            if (id.HasValue)
            {
                response = stopped > 0
                    ? $"已停止机器人 #{id} 的跟随并提交示教轨迹。"
                    : $"机器人 #{id} 未在跟随（或已不存在），无轨迹可提交。";
            }
            else
            {
                response = stopped > 0
                    ? $"已停止 {stopped} 个机器人的跟随并提交示教轨迹。"
                    : "当前没有机器人在跟随，无轨迹可提交。";
            }
            return true;
        }

        // 开始跟随：bot follow <玩家> [all|id]
        if (arguments.Count < 1)
        {
            response = "用法: bot follow <玩家名> [all|id]";
            return false;
        }

        string playerArg = arguments.At(0);

        // 找目标玩家（按名称或 ID）。
        Player? leader = FindPlayer(playerArg);
        if (leader == null)
        {
            response = $"找不到玩家 '{playerArg}'（可按昵称或 PlayerId）。";
            return false;
        }

        int? targetId = arguments.Count > 1 ? ParseId(arguments.At(1)) : null;
        int count = BotManager.StartFollow(leader, targetId);

        response = targetId.HasValue
            ? $"机器人 #{targetId} 开始跟随玩家 {leader.DisplayName}（跟随期间记录房间轨迹）。"
            : $"全部 {count} 个机器人开始跟随玩家 {leader.DisplayName}（跟随期间记录房间轨迹）。";
        return true;
    }

    private static int? ParseId(string raw)
    {
        if (int.TryParse(raw, out int id))
        {
            return id;
        }

        return null;
    }

    private static Player? FindPlayer(string query)
    {
        if (int.TryParse(query, out int playerId))
        {
            Player? byId = Player.Get(playerId);
            if (byId != null)
            {
                return byId;
            }
        }

        foreach (Player player in Player.List)
        {
            if (player != null && player.IsAlive
                && (player.DisplayName != null && player.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || player.Nickname != null && player.Nickname.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return player;
            }
        }

        return null;
    }
}

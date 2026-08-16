using System;
using CommandSystem;

namespace ScpBotPlugin.Commands;

/// <summary>
/// 机器人管理父命令，同时注册到 Remote Admin 与服务器控制台。
/// </summary>
[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
public class BotParentCommand : ParentCommand
{
    /// <inheritdoc />
    public override string Command => "bot";

    /// <inheritdoc />
    public override string[] Aliases => ["scpbot"];

    /// <inheritdoc />
    public override string Description => "机器人管理命令。子命令：spawn / spawnpos / follow / kill / list / room / path / wp / respawn";

    /// <inheritdoc />
    public override void LoadGeneratedCommands()
    {
        RegisterCommand(new BotSpawnCommand());
        RegisterCommand(new BotSpawnPositionCommand());
        RegisterCommand(new BotFollowCommand());
        RegisterCommand(new BotKillCommand());
        RegisterCommand(new BotListCommand());
        RegisterCommand(new BotRoomCommand());
        RegisterCommand(new BotPathCommand());
        RegisterCommand(new BotWaypointCommand());
        RegisterCommand(new BotRespawnCommand());
    }

    /// <inheritdoc />
    protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "用法: bot <spawn|spawnpos|follow|kill|list|room|path|wp|respawn> [参数]";
        return false;
    }
}

using System;
using CommandSystem;
using PlayerRoles;

namespace ScpBotPlugin.Commands;

/// <summary>生成机器人：bot spawn [数量] [角色]。</summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotSpawnCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "spawn";

    /// <inheritdoc />
    public string[] Aliases => ["add", "s"];

    /// <inheritdoc />
    public string Description => "生成 N 个机器人（默认 1，上限 64），可选指定角色。用法: bot spawn [数量] [角色名]";

    /// <inheritdoc />
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.FacilityManagement))
        {
            response = "权限不足（需要 Facility Management）。";
            return false;
        }

        int count = 1;
        if (arguments.Count > 0 && (!int.TryParse(arguments.At(0), out count) || count <= 0))
        {
            response = "数量参数无效，应为正整数。";
            return false;
        }

        RoleTypeId? role = null;
        if (arguments.Count > 1)
        {
            if (!Enum.TryParse(arguments.At(1), true, out RoleTypeId parsedRole) || parsedRole == RoleTypeId.None)
            {
                response = $"角色名无效：'{arguments.At(1)}'。可用角色（不区分大小写）：\n"
                    + "人类：NtfCaptain、NtfSpecialist、NtfSergeant、NtfPrivate、ChaosRifleman、ChaosMarauder、ChaosRepressor、ChaosConscript、Scientist、FacilityGuard、ClassD\n"
                    + "SCP：Scp173、Scp106、Scp049、Scp0492、Scp096、Scp939、Scp3114";
                return false;
            }

            role = parsedRole;
        }

        count = Math.Min(count, 64);
        BotManager.RequestSpawn(count, role);

        // FF-62：SCP 角色无法持枪，生成后是无武器的裸 bot。若管理员误选 SCP 角色，
        // 在 response 中明确提示，避免管理员以为 bot 故障。
        bool isScp = role.HasValue && !CanHoldWeapon(role.Value);
        response = isScp
            ? $"已请求生成 {count} 个机器人（角色 {role!.Value}，注意：SCP 角色无法持枪，将裸奔作战）。"
            : (role.HasValue
                ? $"已请求生成 {count} 个机器人（角色 {role!.Value}）。"
                : $"已请求生成 {count} 个机器人（使用配置默认角色）。");
        return true;
    }

    private static bool CanHoldWeapon(RoleTypeId role)
    {
        return role switch
        {
            RoleTypeId.Scp173 or RoleTypeId.Scp106 or RoleTypeId.Scp049 or RoleTypeId.Scp0492
                or RoleTypeId.Scp096 or RoleTypeId.Scp939 or RoleTypeId.Scp3114 => false,
            _ => true,
        };
    }
}

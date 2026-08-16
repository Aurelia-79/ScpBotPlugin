using System;
using CommandSystem;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using PlayerRoles;

namespace ScpBotPlugin.Commands;

/// <summary>
/// 设置/查看 NTF 与 CI 阵营机器人的出生位置（RA 与控制台均可用）。
/// 用法：
///   bot spawnpos ntf &lt;x y z&gt;   设置 NTF 出生点
///   bot spawnpos ci &lt;x y z&gt;    设置 CI 出生点
///   bot spawnpos show              查看当前设置
///   bot spawnpos clear &lt;ntf|ci&gt; 清除指定阵营出生点
/// 出生点允许设置在设施内任意位置（不限于地表）。
/// </summary>
[CommandHandler(typeof(BotParentCommand))]
public class BotSpawnPositionCommand : ICommand
{
    /// <inheritdoc />
    public string Command => "spawnpos";

    /// <inheritdoc />
    public string[] Aliases => ["sp", "birth"];

    /// <inheritdoc />
    public string Description => "设置/查看 NTF 与 CI 机器人的出生位置。用法: bot spawnpos ntf|ci <x y z> | show | clear <ntf|ci>";

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
            response = "用法: bot spawnpos ntf|ci <x y z> | show | clear <ntf|ci>";
            return false;
        }

        string action = arguments.At(0).ToLowerInvariant();

        // 查看当前设置。
        if (action == "show")
        {
            BotConfig current = BotPlugin.Instance?.Config ?? new BotConfig();
            string ntf = string.IsNullOrWhiteSpace(current.NtfSpawnPosition) ? "(未设置)" : current.NtfSpawnPosition!;
            string ci = string.IsNullOrWhiteSpace(current.CiSpawnPosition) ? "(未设置)" : current.CiSpawnPosition!;
            string legacy = string.IsNullOrWhiteSpace(current.SpawnPosition) ? "(未设置)" : current.SpawnPosition!;
            response = $"NTF 出生点: {ntf}\nCI 出生点: {ci}\n兼容旧配置 SpawnPosition: {legacy}";
            return true;
        }

        // 清除指定阵营出生点。
        if (action == "clear")
        {
            if (arguments.Count < 2)
            {
                response = "用法: bot spawnpos clear <ntf|ci>";
                return false;
            }

            return ClearSpawn(arguments.At(1).ToLowerInvariant(), sender, out response);
        }

        // 设置出生点：bot spawnpos <ntf|ci> <x y z>
        if (action != "ntf" && action != "ci")
        {
            response = $"未知参数 '{action}'。用法: bot spawnpos ntf|ci <x y z> | show | clear <ntf|ci>";
            return false;
        }

        if (arguments.Count < 4)
        {
            response = $"用法: bot spawnpos {action} <x y z>（世界坐标，可设设施内任意位置）";
            return false;
        }

        if (!float.TryParse(arguments.At(1), out float x)
            || !float.TryParse(arguments.At(2), out float y)
            || !float.TryParse(arguments.At(3), out float z))
        {
            response = "坐标无效，应为三个数字：bot spawnpos <ntf|ci> <x y z>";
            return false;
        }

        BotPlugin? plugin = BotPlugin.Instance;
        if (plugin == null)
        {
            response = "插件实例尚未就绪，请稍后重试。";
            return false;
        }

        string pos = $"{x} {y} {z}";
        BotConfig config = plugin.Config;
        if (action == "ntf")
        {
            config.NtfSpawnPosition = pos;
        }
        else
        {
            config.CiSpawnPosition = pos;
        }

        plugin.SaveConfig();
        response = $"已设置 {(action == "ntf" ? "NTF" : "CI")} 出生点为 ({pos})，已保存到配置文件，下次生成生效。";
        return true;
    }

    private static bool ClearSpawn(string side, ICommandSender sender, out string response)
    {
        BotPlugin? plugin = BotPlugin.Instance;
        if (plugin == null)
        {
            response = "插件实例尚未就绪，请稍后重试。";
            return false;
        }

        BotConfig config = plugin.Config;
        if (side == "ntf")
        {
            config.NtfSpawnPosition = null;
        }
        else if (side == "ci")
        {
            config.CiSpawnPosition = null;
        }
        else
        {
            response = "用法: bot spawnpos clear <ntf|ci>";
            return false;
        }

        plugin.SaveConfig();
        response = $"已清除 {(side == "ntf" ? "NTF" : "CI")} 出生点，将回退到兼容配置/角色默认出生点。";
        return true;
    }
}

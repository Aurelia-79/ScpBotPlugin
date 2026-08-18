using System;
using System.Globalization;

namespace ScpBotPlugin.ExternalAI;

/// <summary>
/// 极简 JSON 工具，仅解析/生成本插件与外部 AI 服务器之间约定好的固定结构。
/// 刻意不使用 System.Text.Json / Newtonsoft，避免游戏运行时程序集版本兼容问题。
/// </summary>
public static class JsonMini
{
    /// <summary>取字段值：定位 "name" 后的冒号并返回从值开始的子串（不含引号/定界符），找不到返回 null。</summary>
    public static string? FindValue(string json, string field)
    {
        int idx = json.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        int colon = json.IndexOf(':', idx);
        if (colon < 0)
        {
            return null;
        }

        int i = colon + 1;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t'))
        {
            i++;
        }

        if (i >= json.Length)
        {
            return null;
        }

        if (json[i] == '"')
        {
            int end = json.IndexOf('"', i + 1);
            return end < 0 ? null : json.Substring(i + 1, end - i - 1);
        }

        // FF-19：标量字段可能是对象的最后一个字段（后跟 '}' 而非 ','），
        // 若 endChar 固定为 ','，值会扫到行尾并带上 '}'（如 "3.0}"）导致解析失败。
        // 因此 endChar 取 ',' 与 '}' 中先出现者。
        bool includeCloser = false;
        char endChar;
        if (json[i] == '[')
        {
            endChar = ']';
            // 数组值必须包含尾 ']'：TryParseVector 假定值以 ']' 结尾（Substring(1, len-2)），
            // 若只返回 "[a,b,c" 会截掉最后一位数字导致解析失败。
            includeCloser = true;
        }
        else if (json[i] == '{')
        {
            endChar = '}';
            includeCloser = true;
        }
        else
        {
            // 标量：先出现的 ',' 或 '}' 都是合法结尾。
            int comma = json.IndexOf(',', i + 1);
            int brace = json.IndexOf('}', i + 1);
            endChar = comma < 0 ? '}' : (brace >= 0 && brace < comma ? '}' : ',');
        }
        int j = i + 1;
        while (j < json.Length && json[j] != endChar)
        {
            j++;
        }

        // 数组/对象值：把闭合符纳入返回值（标量值不含分隔符）。
        if (includeCloser && j < json.Length && json[j] == endChar)
        {
            j++;
        }

        return json.Substring(i, j - i);
    }

    /// <summary>解析形如 [a, b, c] 的三个浮点数，失败返回 false。</summary>
    public static bool TryParseVector(string? raw, out float x, out float y, out float z)
    {
        x = y = z = 0f;

        string source = raw ?? string.Empty;
        if (source.Length < 2 || source[0] != '[')
        {
            return false;
        }

        string inner = source.Substring(1, source.Length - 2);
        string[] parts = inner.Split(',');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryFloat(parts[0], out x) || !TryFloat(parts[1], out y) || !TryFloat(parts[2], out z))
        {
            return false;
        }

        return true;
    }

    /// <summary>解析整数（含布尔 0/1）。</summary>
    public static bool TryInt(string? raw, out int value)
    {
        value = 0;
        return raw != null && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryFloat(string raw, out float value)
    {
        // FF-09：显式拒绝 NaN/Infinity（float.TryParse 对二者返回 true），
        // 外部 AI 是网络对端，其 NaN 坐标会流入 Face/Move 损坏瞄准/移动。
        if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

/// <summary>
/// 外部 AI 服务器对单个机器人的一条指令。
/// </summary>
public sealed class BotOrders
{
    /// <summary>目标机器人编号。</summary>
    public int Bot;

    /// <summary>是否要移动到指定世界坐标（走位/巡逻目标，本地直接 Move）。</summary>
    public bool HasMoveTo;
    public float MoveX, MoveY, MoveZ;

    /// <summary>是否追击指定世界坐标（本地先算地表 NavMesh 拐点再 Move）。</summary>
    public bool HasChaseTo;
    public float ChaseX, ChaseY, ChaseZ;

    /// <summary>是否要看向指定世界坐标点。</summary>
    public bool HasLook;
    public float LookX, LookY, LookZ;

    /// <summary>开火状态：true 按住扳机，false 松开，null 未指定。</summary>
    public bool? Shoot;

    /// <summary>投掷指令：he（手榴弹）/ flash（闪光弹）；null 不投。</summary>
    public string? Throw;

    /// <summary>投掷目标方向（世界坐标点，本地朝该点投掷）。</summary>
    public bool HasThrowTarget;
    public float ThrowX, ThrowY, ThrowZ;

    /// <summary>治疗指令：true 使用医疗物品（本地执行背包/拾取流程）。</summary>
    public bool Heal;
}
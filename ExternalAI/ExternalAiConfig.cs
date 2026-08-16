namespace ScpBotPlugin;

/// <summary>
/// 外部 AI 服务器配置。
/// </summary>
public class ExternalAiConfig
{
    /// <summary>是否启用外部 AI。关闭时机器人始终使用本地 AI。</summary>
    public bool Enabled { get; set; }

    /// <summary>外部 AI 服务器地址（默认本机）。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>外部 AI 服务器端口。</summary>
    public int Port { get; set; } = 9000;

    /// <summary>快照发送间隔（秒）。默认 0.1（10Hz）。</summary>
    public float SendInterval { get; set; } = 0.1f;

    /// <summary>超过该时间（秒）未收到外部服务器任何数据则判定失联，降级为本地 AI。</summary>
    public float TimeoutSeconds { get; set; } = 2.0f;

    /// <summary>外部服务器失联（或未启用）时，无指令的机器人是否保持待命。</summary>
    public bool IdleWhenNoOrders { get; set; } = true;
}
using System.Collections.Generic;
using PlayerRoles;

namespace ScpBotPlugin;

/// <summary>
/// 机器人插件配置。修改后需重载插件或重启服务器生效。
/// </summary>
public class BotConfig
{
    /// <summary>机器人昵称前缀，实际昵称为 "{前缀} {id}"。</summary>
    public string BotNamePrefix { get; set; } = "Bot";

    /// <summary>机器人生成的角色（默认 NTF 队长，会与 SCP/混沌/ClassD 敌对）。</summary>
    public RoleTypeId BotRole { get; set; } = RoleTypeId.NtfCaptain;

    /// <summary>主武器（默认 E11-SR）。</summary>
    public ItemType PrimaryWeapon { get; set; } = ItemType.GunE11SR;

    /// <summary>是否额外给一件战斗护甲。</summary>
    public bool GiveArmor { get; set; } = true;

    /// <summary>是否额外给一个医疗包。</summary>
    public bool GiveMedkit { get; set; } = true;

    /// <summary>备用弹药数量。</summary>
    public ushort ReserveAmmo { get; set; } = 200;

    /// <summary>无限弹药：弹匣打空后直接补满（跳过换弹动画），备用弹药也锁满。默认开启。</summary>
    public bool InfiniteAmmo { get; set; } = true;

    /// <summary>AI 更新间隔（秒），越小越灵敏但更耗性能。</summary>
    public float TickInterval { get; set; } = 0.1f;

    /// <summary>移动速度（米/秒）。默认 14，接近游戏内 NTF 冲刺速度；值越高 bot 移动越快。</summary>
    public float MoveSpeed { get; set; } = 14f;

    /// <summary>开火距离（米）。</summary>
    public float AttackRange { get; set; } = 40f;

    /// <summary>与目标的理想交战距离（米），过近会停下保持距离。</summary>
    public float PreferredEngageDistance { get; set; } = 10f;

    /// <summary>启用真人式走位：绕圈、追击横移、近距后撤、横移方向随机翻转。默认开启。</summary>
    public bool EnableOrbitMovement { get; set; } = true;

    /// <summary>状态机距离容差（米）：与理想距离偏差超过该值才在追击/绕圈间切换。</summary>
    public float RangeTolerance { get; set; } = 4f;

    /// <summary>绕圈时低于该距离（米）就后撤拉开，模拟真人被贴脸时退。</summary>
    public float OrbitRetreatDistance { get; set; } = 4f;

    /// <summary>绕圈时离理想距离过远的内收强度（值越大越急着拉回射程）。</summary>
    public float OrbitInwardBias { get; set; } = 0.12f;

    /// <summary>追击时的横向走位强度（值越大追击时横移越明显）。</summary>
    public float ChaseStrafeBias { get; set; } = 0.6f;

    /// <summary>队友分离半径（米）：同队机器人低于该距离互相排斥，避免扎堆。</summary>
    public float NearbyBotAvoidanceRadius { get; set; } = 1.5f;

    /// <summary>横移方向随机翻转的最短间隔（毫秒）。</summary>
    public int StrafeFlipMinMs { get; set; } = 120;

    /// <summary>横移方向随机翻转的最长间隔（毫秒）。</summary>
    public int StrafeFlipMaxMs { get; set; } = 320;

    /// <summary>巡逻路径扩散半径（米）：锁定地标后，实际导航点在地标 ±该半径内随机偏移，让巡逻路径不每次都走同一条直线。</summary>
    public float PatrolSpreadRadius { get; set; } = 8f;

    /// <summary>瞄准高度（米），从目标脚下算起。默认 0.35 对应腹部/躯干偏下；值越小瞄准越低。</summary>
    public float AimHeight { get; set; } = 0.35f;

    /// <summary>
    /// 瞄准散布半径（米）：开火时瞄准点会在目标身体点周围随机偏移（水平 ±半径、垂直 ±0.6 半径），
    /// 模拟真人枪法误差，避免机器人命中率过高（否则真人玩家打不过人机）。
    /// 偏移每 tick 重新随机，连发时子弹自然扩散。0 关闭（精确瞄准）。
    /// </summary>
    public float AimSpread { get; set; } = 0.5f;

    /// <summary>索敌/视野最大距离（米）。</summary>
    public float MaxVisionDistance { get; set; } = 60f;

    /// <summary>卡住多少秒后瞬移到目标附近（兜底“寻路”）。</summary>
    public float StuckTeleportAfter { get; set; } = 3f;

    /// <summary>前方障碍检测距离（米）。</summary>
    public float ObstacleLookAhead { get; set; } = 2.5f;

    /// <summary>是否启用房间图寻路（跨房间追击时按房间路径行进）。</summary>
    public bool EnableRoomNavigation { get; set; } = true;

    /// <summary>
    /// 用户自定义房间图：房间名 → 可达的邻居房间名列表。
    /// 寻路时优先使用这里的邻居关系；未在此配置的房间回退到游戏原生相邻房间（含电梯跨区）。
    /// 房间名见游戏内 MapGeneration.RoomName 枚举，例如 Lcz173、Hcz049、EzGateA、HczCheckpointToEntranceZone。
    /// </summary>
    public Dictionary<string, List<string>> RoomGraph { get; set; } = [];

    /// <summary>
    /// 配装完成后将机器人传送到该世界坐标（格式 "x y z"；留空则用角色默认出生点）。
    /// NTF 默认出生在地表 B 平台；若想让机器人直接在设施内参战，可填设施内某处的坐标
    /// （例如某个房间的中心，可用 bot wp add 站在目标位置看输出的坐标）。
    /// </summary>
    public string? SpawnPosition { get; set; }

    /// <summary>
    /// NTF 阵营机器人的出生坐标（格式 "x y z"；留空则回退到 <see cref="SpawnPosition"/>，再留空用角色默认出生点）。
    /// 允许设置在设施内任意位置（不限于地表）。
    /// </summary>
    public string? NtfSpawnPosition { get; set; }

    /// <summary>
    /// CI 阵营机器人的出生坐标（格式 "x y z"；留空则回退到 <see cref="SpawnPosition"/>，再留空用角色默认出生点）。
    /// 允许设置在设施内任意位置（不限于地表）。
    /// </summary>
    public string? CiSpawnPosition { get; set; }

    /// <summary>是否允许 AI 自己开门（关闭且未锁的门直接服务器端打开）。默认开启。</summary>
    public bool OpenDoors { get; set; } = true;

    /// <summary>血量低于该比例（0~1）时触发自疗。默认 0.35。</summary>
    public float HealThreshold { get; set; } = 0.35f;

    /// <summary>自疗判定「附近无敌人」的距离（米）。默认 15。</summary>
    public float HealNoEnemyRange { get; set; } = 15f;

    /// <summary>自疗尝试失败后的冷却时间（秒），避免每 tick 反复扫描。默认 8。</summary>
    public float HealCooldown { get; set; } = 8f;

    /// <summary>投掷手榴弹/闪光弹所需的最小敌人数（聚集判定）。默认 2。</summary>
    public int ThrowMinEnemies { get; set; } = 2;

    /// <summary>投掷手榴弹/闪光弹的敌人距离区间（米）：[下限, 上限]。默认 [8, 15]。</summary>
    public float ThrowMinDistance { get; set; } = 8f;

    /// <summary>投掷手榴弹/闪光弹的敌人距离区间（米）：[下限, 上限]。默认 [8, 15]。</summary>
    public float ThrowMaxDistance { get; set; } = 15f;

    /// <summary>投掷间隔冷却（秒），防止连丢。默认 12。</summary>
    public float ThrowCooldown { get; set; } = 12f;

    /// <summary>卡住多久（秒）后尝试跳跃脱离。默认 0.8。</summary>
    public float StuckJumpAfter { get; set; } = 0.8f;

    /// <summary>跳跃/改向尝试多久（秒）无效后进入光线检查寻路。默认 2。</summary>
    public float StuckRaycastAfter { get; set; } = 2f;

    /// <summary>卡死时是否最终瞬移到目标附近兜底。默认 true。</summary>
    public bool StuckTeleportEnabled { get; set; } = true;

    /// <summary>NavMesh 烘焙质量：High（默认，voxel 0.15）/ Ultra（最高质量，voxel 0.1、更小 agent）。</summary>
    public string BakeQuality { get; set; } = "High";

    /// <summary>多路线寻路：一个目标最多生成几条候选路线。默认 3。</summary>
    public int MaxRouteOptions { get; set; } = 3;

    /// <summary>「打不过换路」统计窗口（秒）：统计该时间窗内当前路线上的己方阵亡数。默认 20。</summary>
    public float RouteCasualtyWindow { get; set; } = 20f;

    /// <summary>「打不过换路」阈值：窗口内当前路线阵亡数达到该值就切换备选路线。默认 2。</summary>
    public int RouteCasualtyThreshold { get; set; } = 2;

    /// <summary>
    /// 卡房超时（秒）：bot 卡在同一房间且无交战（无目标/未开火）超过该时间，
    /// 判定为「卡死无进展」，重生整个阵营的 bot 并给神经网络严厉惩罚。0 或负值禁用。默认 90。
    /// </summary>
    public float IdleStuckTimeout { get; set; } = 90f;

    /// <summary>卡房超时是否启用。默认 true。</summary>
    public bool IdleStuckTimeoutEnabled { get; set; } = true;

    /// <summary>进入房间后距当前航点多近就算“到达”并走向下一个航点（米）。</summary>
    public float WaypointReachDistance { get; set; } = 1.5f;

    /// <summary>与目标直线距离小于该值（米）时放弃地标/航点，直接冲向目标收尾。</summary>
    public float DirectChaseDistance { get; set; } = 12f;

    /// <summary>
    /// 外部 AI 服务器配置。启用后，机器人决策（索敌/寻路/开火判断）由独立的 Python/Node 进程
    /// 多核并行计算，本插件只负责采集快照并通过 TCP 发送、接收指令在主线程执行。
    /// 外部服务器不可用时自动降级为本地 AI（内置逻辑）。
    /// </summary>
    public ExternalAiConfig ExternalAI { get; set; } = new();

    /// <summary>回合开始时是否自动生成机器人。</summary>
    public bool AutoSpawnOnRoundStart { get; set; }

    /// <summary>回合开始时自动生成的数量。</summary>
    public int AutoSpawnCount { get; set; }
}

using System;
using System.Collections.Generic;
using System.Linq;
using InventorySystem.Items;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Firearms.Modules;
using LabApi.Features.Wrappers;
using Logger = LabApi.Features.Console.Logger;
using MapGeneration;
using Mirror;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.PlayableScps;
using RelativePositioning;
using ScpBotPlugin.ExternalAI;
using UnityEngine;

namespace ScpBotPlugin;

/// <summary>战斗移动状态：追击（远）与绕圈（进入射程）。</summary>
internal enum CombatState
{
    Chase,
    Orbit
}

/// <summary>外部 AI 感知：一个敌人的快照信息（含本地视线检测结果）。</summary>
public readonly struct EnemyPerception
{
    /// <summary>敌人 netId。</summary>
    public uint NetId { get; init; }

    /// <summary>敌人位置（脚底）。</summary>
    public Vector3 Position { get; init; }

    /// <summary>敌人瞄准点（身体位置，本地按 AimHeight 算好）。</summary>
    public Vector3 AimPosition { get; init; }

    /// <summary>到本 bot 的距离（米）。</summary>
    public float Distance { get; init; }

    /// <summary>敌人队伍名。</summary>
    public string Team { get; init; }

    /// <summary>本地视线检测是否可见。</summary>
    public bool Visible { get; init; }
}

/// <summary>
/// 表示一个由游戏内置 Dummy 驱动的机器人，负责单个机器人的索敌、寻路与战斗逻辑。
/// </summary>
public sealed class Bot
{
    private const float EyeHeight = 1.5f;

    private readonly ReferenceHub _hub;
    private readonly Player _player;
    private readonly RoleTypeId _role;

    private ReferenceHub? _target;
    private Vector3 _lastPosition;
    private float _stuckTime;

    // 位置漂移检测：如果服务器端实际位置跳变超过阈值（通常是 RelativePosition 引用了
    // 移动物体的 waypoint，如电梯/传送平台），自动拉回上次已知正常位置。
    private Vector3 _lastActualPosition;
    private int _driftCorrectionCount;
    // 当漂移发生时，改用 ServerOverridePosition 直接移动（绕过 RelativePosition / waypoint），
    // 持续指定帧数后再尝试恢复正常的 RelativePosition 移动。
    private int _serverOverrideFrames;

    // 房间级寻路状态
    private List<RoomName>? _roomPath;
    private RoomName? _roomPathGoal;
    private int _roomPathIndex;
    // 多路线：目标房间对应的全部候选路线（按长度升序），_routeIndex 为当前选中路线。
    private List<List<RoomName>>? _roomRoutes;
    private float _routeAssignTick;   // 路线分配时间点（用于按 Id 分散与跨队夹击的稳定分配）

    // 房间内航点状态（玩家提供的绕障/快捷走法；进房随机选一条路线，可正走或倒走）
    private RoomName? _waypointRoom;
    private int _waypointIndex;
    private bool _waypointForward;
    private List<Vector3>? _waypointRoute;

    // 初始化状态：Dummy 刚生成时 authManager.UserId 还是 null（下一帧 Start() 才设为 "ID_Dummy"），
    // 立即 SetRole 会触发钥匙卡发放并因 null key 崩溃，所以延迟到下一帧再配装，失败自动重试。
    private bool _pendingLoadout = true;
    private int _pendingLoadoutAttempts;

    // 弹药 / 换弹状态：检测弹匣剩余量，空仓时自动换弹再开火。
    private bool _isReloading;
    private float _reloadWaitTime;
    // FF-07：换弹按键两阶段状态 —— Reload 键按下后下一 tick 才能拿到 "Reload->Release"
    // 动作（DummyKeyEmulator 动作列表是状态相关的），必须先 Hold 再 Release 才能触发换弹。
    private bool _reloadKeyHeld;
    private bool _reloadTriggered;

    // 战斗走位状态（真人拉扯/走位）：追击 vs 绕圈状态机 + 横移/绕圈方向 + 翻转计时。
    private CombatState _combatState = CombatState.Chase;
    private int _strafeDirection = 1;
    private int _orbitDirection = 1;
    private int _nextStrafeFlipTick;

    // 地表 NavMesh 路径状态：地表（Outside）长途追击时沿 NavMesh 拐点走，替代直线冲山体/楼群。
    private List<Vector3>? _surfacePath;
    private int _surfacePathIndex;
    private Vector3 _surfacePathGoal;

    // 巡逻目标（锁定直到到达）：地标坐标 + 扩散偏移，到达后随机换下一个，形成来回巡逻。
    private Vector3? _patrolTarget;
    private Vector3 _patrolSpread;
    private Vector3 _lastPatrolTarget;
    // 扩散偏移的刷新计时：周期性重新随机偏移，让巡逻路径蜿蜒而不是走直线。
    private int _patrolSpreadNextTick;

    // 投掷状态：投掷冷却计时 + 投掷动画等待（ServerProcessInitiation 后需等待 ReadyToThrow 阈值）。
    private float _nextThrowTick;
    private bool _throwPending;
    private float _throwPendingStart;
    private float _throwReadyTime;  // FF-10：ServerProcessInitiation 成功后，_throwReadyTime = 当前时间 + 0.8f * ThrowingAnimTime
    private ItemType _throwPendingType;

    // 自疗状态：冷却计时（放弃后隔一段时间再尝试）。
    private float _nextHealTick;

    // 卡死三级脱离状态：跳跃/改向阶段计时。
    private float _stuckJumpTick;   // 开始尝试跳跃的时间点（卡住累计到 StuckJumpAfter）
    private int _stuckJumpCount;    // 已跳跃次数（限制跳跃频率）
    private float _stuckRaycastTick; // 光线检查阶段的开始时间
    private bool _stuckRaycasting;

    // 门等待状态：开门后门板需要时间移开，期间原地等待（避免顶门板）。
    private Door? _waitDoor;
    private float _waitDoorStart;

    // 示教跟随状态：跟随玩家时记录经过的房间序列（供神经网络模仿学习）。
    private ReferenceHub? _followTarget;
    private RoomName? _followLastRoom;
    private readonly List<string> _followTrace = new();

    // 卡房超时检测：同一房间内无交战累计时间。
    private RoomName? _idleStuckRoom;
    private float _idleStuckTime;

    // 路线状态：当前路线索引 + 阵亡统计（跨 tick 保留）。
    private int _routeIndex;
    private readonly Queue<(float Time, uint BotNetId)> _routeCasualties = new();

    /// <summary>机器人内部编号。</summary>
    public int Id { get; }

    /// <summary>机器人的 <see cref="ReferenceHub"/>。</summary>
    public ReferenceHub Hub => _hub;

    /// <summary>机器人显示名。</summary>
    public string Name => _player.DisplayName;

    /// <summary>机器人的 <see cref="Player"/> 包装器。</summary>
    public Player Player => _player;

    /// <summary>机器人是否存活。</summary>
    public bool IsAlive => _player.IsAlive;

    /// <summary>机器人当前队伍（快照用）。</summary>
    public Team Team => _player.Team;

    /// <summary>机器人生成时的角色（FF-06：卡房重生按原角色重建，避免阵营反转）。</summary>
    public RoleTypeId Role => _role;

    /// <summary>机器人当前血量（快照用）。</summary>
    public float Health => _player.Health;

    /// <summary>累计击杀数（PlayerEvents.Dying 中 Attacker 是本 bot 时由 BotManager 统计，供神经网络学习奖励）。</summary>
    public int Kills { get; internal set; }

    /// <summary>累计阵亡数（本 bot 死亡时由 BotManager 统计，供神经网络学习奖励）。</summary>
    public int Deaths { get; internal set; }

    /// <summary>背包物品摘要：手榴弹数 / 闪光弹数 / 医疗物品数（供外部 AI 决策与神经网络状态）。</summary>
    public (int He, int Flash, int Med) ItemSummary
    {
        get
        {
            int he = 0, flash = 0, med = 0;
            foreach (Item item in _player.Items)
            {
                switch (item.Type)
                {
                    case ItemType.GrenadeHE:
                        he++;
                        break;
                    case ItemType.GrenadeFlash:
                        flash++;
                        break;
                    case ItemType.Medkit:
                    case ItemType.Adrenaline:
                    case ItemType.Painkillers:
                        med++;
                        break;
                }
            }

            return (he, flash, med);
        }
    }

    /// <summary>机器人当前位置（快照用）。</summary>
    public Vector3 Position => _player.Position;

    /// <summary>底层对象是否仍然有效。</summary>
    public bool IsValid => _hub != null && _hub.gameObject != null;

    /// <summary>是否尚未完成角色/装备初始化（配装延迟到下一帧执行）。</summary>
    public bool IsPendingLoadout => _pendingLoadout;

    /// <summary>机器人当前所在房间名（用于调试/显示）。</summary>
    public RoomName? CurrentRoomName { get; private set; }

    /// <summary>目标玩家当前所在房间名（用于调试/显示）。</summary>
    public RoomName? TargetRoomName { get; private set; }

    /// <summary>当前寻路路径摘要（用于调试/显示）。</summary>
    public string PathSummary { get; private set; } = string.Empty;

    /// <summary>当前路线指纹（房间名序列），供阵亡统计/换路判定；无路线返回 null。</summary>
    public string? RouteFingerprint
    {
        get
        {
            if (_roomPath == null || _roomPath.Count == 0)
            {
                return null;
            }

            System.Text.StringBuilder sb = new();
            for (int i = 0; i < _roomPath.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('>');
                }

                sb.Append(_roomPath[i]);
            }

            return sb.ToString();
        }
    }

    /// <summary>当前目标的全部候选路线（房间名序列，按长度升序），供外部 AI 神经网络做路线选择。</summary>
    public IReadOnlyList<List<RoomName>>? CandidateRoutes => _roomRoutes;

    /// <summary>
    /// 掩体状态：与最近敌人的视线被遮挡（敌人存在但距离较近却看不见）→ 处于掩体后。
    /// 供神经网络学习「找掩体躲」：地表有大量岩石/建筑/箱子，玩家会躲而 bot 不会，
    /// 把掩体作为状态特征 + 奖励信号，让网络学会主动利用。
    /// </summary>
    public bool InCover
    {
        get
        {
            if (_target == null || _hub == null)
            {
                return false;
            }

            try
            {
                // 距离较近（<40m）但看不见 → 中间有障碍物挡着（掩体/墙）。
                Vector3 bodyPos = GetAimPoint(_target, 0.35f);
                float d = (_target.transform.position - _hub.transform.position).magnitude;
                if (d > 40f)
                {
                    return false;
                }

                return !CanSee(bodyPos, BotPlugin.Instance?.Config ?? new BotConfig());
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 初始化机器人。
    /// </summary>
    /// <param name="id">机器人编号。</param>
    /// <param name="hub">Dummy 的 <see cref="ReferenceHub"/>。</param>
    /// <param name="player">LabAPI 玩家包装器。</param>
    /// <param name="role">生成时指定的角色。</param>
    public Bot(int id, ReferenceHub hub, Player player, RoleTypeId role)
    {
        Id = id;
        _hub = hub;
        _player = player;
        _role = role;
        _lastPosition = hub.transform.position;
    }

    /// <summary>
    /// 设置角色并配发装备。必须先设置角色，再添加物品。
    /// SCP 类角色（无法持枪）跳过武器/护甲/医疗包配发。
    /// </summary>
    public void SetupLoadout(BotConfig config)
    {
        _player.SetRole(_role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);

        // SCP 角色无法持枪，跳过人类装备配发。
        if (!CanHoldWeapon(_role))
        {
            return;
        }

        Item? gun = _player.AddItem(config.PrimaryWeapon);
        if (gun is FirearmItem firearm)
        {
            try
            {
                ArmFirearm(firearm, config);
            }
            catch (Exception ex)
            {
                Logger.Error($"[ScpBot] 机器人 #{Id} 上膛失败: {ex}");
            }

            if (firearm.AmmoType != ItemType.None)
            {
                _player.AddAmmo(firearm.AmmoType, config.ReserveAmmo);
            }

            _hub.inventory.ServerSelectItem(firearm.Serial);
        }
        else if (gun != null)
        {
            _hub.inventory.ServerSelectItem(gun.Serial);
        }

        if (config.GiveArmor)
        {
            _player.AddItem(ItemType.ArmorCombat);
        }

        if (config.GiveMedkit)
        {
            _player.AddItem(ItemType.Medkit);
        }
    }

    /// <summary>判断角色能否持枪（SCP 类角色返回 false）。</summary>
    private static bool CanHoldWeapon(RoleTypeId role)
    {
        return role switch
        {
            RoleTypeId.Scp173 or RoleTypeId.Scp106 or RoleTypeId.Scp049 or RoleTypeId.Scp079
                or RoleTypeId.Scp096 or RoleTypeId.Scp0492 or RoleTypeId.Scp939 or RoleTypeId.Scp3114 => false,
            _ => true,
        };
    }

    /// <summary>
    /// 给枪插入弹匣、填满弹药并上膛。
    /// 不使用 LabAPI 的 MagazineInserted/StoredAmmo：其 CacheModules 的 switch 对同时实现
    /// IPrimaryAmmoContainerModule 与 IMagazineControllerModule 的 MagazineModule 只会缓存前者，
    /// 导致弹匣永远无法插入。这里直接用游戏原生模块 API 操作，与官方行为一致。
    /// </summary>
    private static void ArmFirearm(FirearmItem firearm, BotConfig config)
    {
        Firearm baseFirearm = firearm.Base;

        // 弹匣类武器：插入空弹匣并填满。
        if (baseFirearm.TryGetModule<MagazineModule>(out MagazineModule? magazine))
        {
            magazine.ServerInsertEmptyMagazine();
            magazine.ServerModifyAmmo(magazine.AmmoMax - magazine.AmmoStored);
        }

        // 自动武器（闭锁）：膛内压一发、上膛。
        if (baseFirearm.TryGetModule<AutomaticActionModule>(out AutomaticActionModule? action))
        {
            action.AmmoStored = 1;
            action.Cocked = true;
            action.ServerResync();
        }
    }

    /// <summary>
    /// 初始化角色与装备。Dummy 需在生成后的下一帧调用（等 authManager.UserId 设为 "ID_Dummy"），
    /// 否则 SetRole 发钥匙卡时 SerialNumberDetail.GetNumberForPlayer 会因 null key 崩溃。
    /// 失败自动在下一个 tick 重试，失败过多则销毁该机器人。
    /// </summary>
    private void TryInitLoadout(BotConfig config)
    {
        _pendingLoadout = false;

        try
        {
            SetupLoadout(config);
            ApplySpawnPosition(config);
        }
        catch (Exception ex)
        {
            _pendingLoadout = true;
            _pendingLoadoutAttempts++;

            if (_pendingLoadoutAttempts >= 10)
            {
                Logger.Error($"[ScpBot] 机器人 #{Id} 初始化失败次数过多，已销毁。{ex}");
                // FF-21：必须同步从 Bots 移除 —— 仅 Dispose() 会让已销毁条目残留，
                // 快照构建访问其 Position/Role（Unity 假 null）抛 NRE、整 tick 作废。
                BotManager.DisposeAndRemove(this);
                return;
            }

            Logger.Warn($"[ScpBot] 机器人 #{Id} 第 {_pendingLoadoutAttempts} 次初始化失败，将在下个 tick 重试：{ex.Message}");
        }
    }

    /// <summary>
    /// 死亡后自动复活：重新设置到生成时的角色并配装，重置战斗/寻路状态。
    /// Dummy 死亡后 ReferenceHub 仍然有效，直接 SetRole 即可复活，无需重新 SpawnDummy。
    /// </summary>
    public bool Respawn(BotConfig config)
    {
        if (!IsValid)
        {
            return false;
        }

        try
        {
            SetupLoadout(config);
            ApplySpawnPosition(config);
            ResetCombatState();
            Logger.Info($"[ScpBot] 机器人 #{Id} 已自动复活（角色 {_role}）。");
            return true;
        }
        catch (Exception ex)
        {
            // FF-39：SetupLoadout 中途失败（如配装到一半）会残留 _isReloading / _reloadWaitTime 等状态，
            // 导致下个 tick bot 误以为还在换弹、不响应战斗。显式重置。
            _isReloading = false;
            _reloadWaitTime = 0f;
            _reloadKeyHeld = false;
            _reloadTriggered = false;
            Logger.Warn($"[ScpBot] 机器人 #{Id} 复活失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>重置战斗/寻路状态（复活后调用，让 bot 以干净状态重新投入战斗）。</summary>
    private void ResetCombatState()
    {
        _target = null;
        _lastPosition = _hub.transform.position;
        _stuckTime = 0f;
        _lastActualPosition = Vector3.zero;
        _driftCorrectionCount = 0;
        _serverOverrideFrames = 0;

        _roomPath = null;
        _roomPathGoal = null;
        _roomPathIndex = 0;
        _waypointRoom = null;
        _waypointIndex = 0;
        _waypointForward = true;
        _waypointRoute = null;
        _surfacePath = null;
        _surfacePathIndex = 0;
        _patrolTarget = null;
        _patrolSpread = Vector3.zero;
        _patrolSpreadNextTick = 0;

        _isReloading = false;
        _reloadWaitTime = 0f;
        _reloadKeyHeld = false;
        _reloadTriggered = false;
        // FF-10：重置投掷状态，防止 bot 复活后残留上一局的 _throwPending / _throwReadyTime。
        _throwPending = false;
        _throwPendingStart = 0f;
        _throwReadyTime = 0f;
        _nextThrowTick = 0f;
        _combatState = CombatState.Chase;
        _strafeDirection = 1;
        _orbitDirection = 1;
        _nextStrafeFlipTick = 0;
        PathSummary = string.Empty;
    }

    /// <summary>
    /// 若配置了阵营出生点（NtfSpawnPosition / CiSpawnPosition，兼容旧 SpawnPosition），
    /// 配装完成后把机器人传送到该点（设施内任意位置均可，不限于地表）。
    /// </summary>
    private void ApplySpawnPosition(BotConfig config)
    {
        // 按阵营取出生点：NTF / CI 各自独立配置，未设置时回退到旧的 SpawnPosition。
        string? spawn = null;
        if (_player.Team == Team.FoundationForces && !string.IsNullOrWhiteSpace(config.NtfSpawnPosition))
        {
            spawn = config.NtfSpawnPosition;
        }
        else if (_player.Team == Team.ChaosInsurgency && !string.IsNullOrWhiteSpace(config.CiSpawnPosition))
        {
            spawn = config.CiSpawnPosition;
        }
        else if (!string.IsNullOrWhiteSpace(config.SpawnPosition))
        {
            spawn = config.SpawnPosition;
        }

        if (spawn == null)
        {
            return;
        }

        if (!RoomWaypoints.TryParsePosition(spawn, out Vector3 pos))
        {
            Logger.Warn($"[ScpBot] 机器人 #{Id} 的出生点坐标无效，已忽略：'{spawn}'（格式应为 \"x y z\"）");
            return;
        }

        try
        {
            if (_hub.roleManager.CurrentRole is IFpcRole fpc)
            {
                fpc.FpcModule.ServerOverridePosition(pos);
            }
            else
            {
                _player.Position = pos;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ScpBot] 机器人 #{Id} 传送到出生点失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 销毁机器人（松开射击按键并摧毁 Dummy 对象）。
    /// </summary>
    public void Dispose()
    {
        SetShoot(false);

        try
        {
            if (IsValid)
            {
                NetworkServer.Destroy(_hub.gameObject);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ScpBot] 销毁机器人 #{Id} 失败: {ex}");
        }
    }

    /// <summary>
    /// 执行一次 AI 决策。
    /// </summary>
    public void Tick(BotConfig config)
    {
        // FF-32：_hub 存在但 roleManager 为 null（玩家断开/角色切换中间态）时，
        // 后续 _hub.roleManager.CurrentRole 与 _player.IsAlive 都会抛 NRE，
        // 作废整个 tick（BotManager 外层 try-catch 虽会吞异常，但本 tick 全量 bot 的决策都被跳过）。
        // 此处提前检测并自毁清理，让 BotManager 在下个 tick 移除该条目。
        if (_hub == null || _hub.roleManager == null)
        {
            try
            {
                BotManager.DisposeAndRemove(this);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ScpBot] 机器人 #{Id} hub 已销毁但清理异常: {ex.GetBaseException().Message}");
            }
            return;
        }

        // 尚未初始化（Dummy 刚生成、UserId 未就绪）：这一帧只做角色/装备配装。
        if (_pendingLoadout)
        {
            // 等 PlayerAuthenticationManager.Start() 把 UserId 设为 "ID_Dummy" 后再配装，
            // 否则角色默认钥匙卡发放会因 null key 崩溃（SerialNumberDetail.GetNumberForPlayer）。
            if (_hub.authManager.UserId == null)
            {
                return;
            }

            TryInitLoadout(config);
            return;
        }

        if (!_player.IsAlive)
        {
            SetShoot(false);
            return;
        }

        if (_hub.roleManager.CurrentRole is not IFpcRole fpc)
        {
            SetShoot(false);
            return;
        }

        // 示教跟随模式：优先跟随玩家并记录轨迹，跳过正常 AI 决策。
        if (_followTarget != null)
        {
            FollowTick(fpc, config);
            return;
        }

        AcquireTarget();

        CurrentRoomName = GetCurrentRoom()?.Name;
        TargetRoomName = _target != null ? GetTargetRoom()?.Name : null;

        // 血量告急自疗（无目标或目标太远时执行；背包/房间找医疗物品）。
        TryHeal(fpc, config);

        // 投掷动画推进：投掷中即使目标消失/死亡也要完成投掷（否则物品被吞）。
        if (_throwPending)
        {
            // FF-10：用 _throwReadyTime（根据实际 ThrowingAnimTime 计算的精确就绪时刻）替代硬编码 0.7f。
            if (Time.timeSinceLevelLoad >= _throwReadyTime)
            {
                ConfirmThrow();
            }
        }

        if (_target == null)
        {
            SetShoot(false);
            PatrolTick(fpc, config);
            return;
        }

        Vector3 myPos = fpc.FpcModule.Position;
        // 身体点：视线检测与距离判定用（不受散布影响，避免散布导致误判不可见/距离偏差）。
        Vector3 bodyPos = GetAimPoint(_target, config.AimHeight);
        // 开火瞄准点：在身体点周围叠加随机散布，模拟真人枪法误差，避免命中率过高。
        Vector3 aimPos = ApplyAimSpread(bodyPos, config);
        // 瞄准方向必须从「眼睛/相机」出发，而非脚底：否则俯仰角会偏高约 1.6m 高度差，
        // 近距离（10m）产生约 9° 仰角偏差，子弹从头顶飞过（枪口抬太高）。
        Vector3 eyePos = _hub.PlayerCameraReference.position;
        Vector3 aimDir = aimPos - eyePos;
        float dist = (bodyPos - myPos).magnitude;

        // 始终面向目标（含垂直方向，用于瞄准）。
        Face(fpc, aimDir);

        bool canSee = dist <= config.MaxVisionDistance && CanSee(bodyPos, config);

        // 投掷判断：目标聚集且距离合适时投手榴弹/闪光弹（投掷期间本 tick 不开火）。
        if (canSee && _throwPending == false && dist <= config.AttackRange)
        {
            TryThrow(fpc, config);
            if (_throwPending)
            {
                SetShoot(false);
                UpdateStuck(fpc, config);
                CheckPositionDrift(fpc, config);
                return;
            }
        }

        if (canSee && dist <= config.AttackRange)
        {
            // 备用弹药自动补给（火力压制等长时间射击不会弹尽停火；弹匣机制保留）。
            if (config.AutoRefillReserveAmmo)
            {
                RefillReserveAmmo(config);
            }

            if (config.InfiniteAmmo)
            {
                // 无限弹药：直接补满弹匣/膛内/备用，无换弹停顿。
                RefillAmmo(config);
            }
            else if (NeedsReload(config))
            {
                // 弹匣空了先换弹，换弹期间不动枪。
                SetShoot(false);
                TryStartReload(config);
                _reloadWaitTime += config.TickInterval;

                // 换弹动画约 1.5s 完成，期间保持瞄准但不开火。
                if (_reloadWaitTime < 1.5f)
                {
                    UpdateStuck(fpc, config);
                    CheckPositionDrift(fpc, config);
                    return;
                }

                _isReloading = false;
                _reloadWaitTime = 0f;
                _reloadKeyHeld = false;
                _reloadTriggered = false;
            }

            SetShoot(true);

            if (config.EnableOrbitMovement)
            {
                // 真人式走位：状态机（追击/绕圈）+ 横移 + 近距后撤 + 方向随机翻转。
                MoveCombat(fpc, bodyPos, dist, config);
            }
            else if (dist > config.PreferredEngageDistance)
            {
                Move(fpc, bodyPos, config);
            }
            else
            {
                // 不完全停住：慢速向目标漂移。彻底 StopMove 会把 ReceivedPosition 快照到当前
                // waypoint 上；若该 waypoint 属于移动物体（电梯/传送平台），waypoint 一动
                // 就会拖着 bot 一起走（典型症状：传送到 0, 300, 0 空中位置）。
                Vector3 curPos = fpc.FpcModule.Position;
                Vector3 toTarget = bodyPos - curPos;
                toTarget.y = 0f;

                if (toTarget.sqrMagnitude > 0.01f)
                {
                    Vector3 driftDir = toTarget.normalized;
                    // 0.5 m/s 慢速漂移：既保持连接活跃（避免 Dummy 心跳超时），
                    // 又避免 waypoint 漂移把 bot 拖走。
                    Vector3 driftStep = curPos + driftDir * 0.5f;
                    Move(fpc, driftStep, config);
                }
                else
                {
                    StopMove(fpc);
                }
            }

            UpdateStuck(fpc, config);
            CheckPositionDrift(fpc, config);
        }
        else
        {
            SetShoot(false);

            // 地表 NavMesh 优先：Outside 长途追击时沿 NavMesh 拐点走，自动绕山体/楼群。
            if (TryGetSurfaceNavPoint(fpc, bodyPos, config, out Vector3 surfaceNavPoint))
            {
                Move(fpc, surfaceNavPoint, config);
                UpdateStuck(fpc, config);
                CheckPositionDrift(fpc, config);
                return;
            }

            // 房间内航点优先：进入配置了航点的房间后，先按玩家给的绕障/快捷走法依次走。
            if (config.EnableRoomNavigation && CurrentRoomName.HasValue
                && TryGetNextWaypoint(CurrentRoomName.Value, fpc.FpcModule.Position, out Vector3 waypoint))
            {
                bool reached = (waypoint - fpc.FpcModule.Position).sqrMagnitude
                    <= config.WaypointReachDistance * config.WaypointReachDistance;
                Move(fpc, waypoint, config);

                if (reached)
                {
                    // 按方向推进：正序 +1，倒序 -1。
                    _waypointIndex += _waypointForward ? 1 : -1;
                }

                UpdateStuck(fpc, config);
                CheckPositionDrift(fpc, config);
                return;
            }

            // 大房间推荐目标点：与目标同房且距离较远时，奔向离目标最近的推荐点分段接近
            // （解决地表这类大房间直线冲目标被山体/楼群卡住的问题）。
            if (config.EnableRoomNavigation && CurrentRoomName.HasValue
                && CurrentRoomName == TargetRoomName
                && dist > config.DirectChaseDistance
                && RoomTargets.TryGetClosest(CurrentRoomName.Value, bodyPos, out Vector3 targetPoint))
            {
                Move(fpc, targetPoint, config);
                UpdateStuck(fpc, config);
                CheckPositionDrift(fpc, config);
                return;
            }

            if (config.EnableRoomNavigation)
            {
                // 跨房间：沿房间路径行进；路径不可用则退化为直线追击。
                Vector3? navPoint = ComputeNavPoint(fpc, config);
                if (navPoint.HasValue)
                {
                    Move(fpc, navPoint.Value, config);
                    UpdateStuck(fpc, config);
                    CheckPositionDrift(fpc, config);
                    return;
                }
            }

            // 目标隔墙不可见（canSee=false）时：主动找门绕行，而不是直线顶墙。
            // 覆盖同房间隔墙（ComputeNavPoint 返回 null 的情况）与房间路径不可达两种情况。
            if (!canSee && config.OpenDoors && TryApproachDoor(fpc, config))
            {
                UpdateStuck(fpc, config);
                CheckPositionDrift(fpc, config);
                return;
            }

            Move(fpc, bodyPos, config);
        }

        UpdateStuck(fpc, config);
        CheckPositionDrift(fpc, config);
    }

    /// <summary>
    /// 无目标时的巡逻行为：按优先级依次尝试房间内航点 → 房间推荐目标点 →
    /// 随机相邻房间 → 当前房间中心。找到目标点后执行移动 + 转向，与有目标时的寻路
    /// 流水线完全一致（含障碍绕行、卡住传送）。
    /// </summary>
    private void PatrolTick(IFpcRole fpc, BotConfig config)
    {
        // 1) 有房间航点时，沿航点巡逻（进入新房间随机选一条路线）。
        if (config.EnableRoomNavigation && CurrentRoomName.HasValue
            && TryGetNextWaypoint(CurrentRoomName.Value, fpc.FpcModule.Position, out Vector3 waypoint))
        {
            float reached = (waypoint - fpc.FpcModule.Position).sqrMagnitude;
            Move(fpc, waypoint, config);
            Face(fpc, waypoint - fpc.FpcModule.Position);

            if (reached <= config.WaypointReachDistance * config.WaypointReachDistance)
            {
                _waypointIndex += _waypointForward ? 1 : -1;
            }

            UpdateStuck(fpc, config);
            CheckPositionDrift(fpc, config);
            return;
        }

        // 2) 地标巡逻：锁定一个地标（到达才换），路径加周期性随机扩散偏移，形成蜿蜒的来回巡逻。
        if (config.EnableRoomNavigation && CurrentRoomName.HasValue
            && RoomTargets.TryGetAll(CurrentRoomName.Value, out List<Vector3>? targets)
            && targets != null && targets.Count > 0)
        {
            Vector3 myPos = fpc.FpcModule.Position;
            int nowTick = Environment.TickCount;
            float reachSq = config.WaypointReachDistance * config.WaypointReachDistance;

            // 无目标或已到达当前地标 → 随机选下一个（排除上一个，形成来回）。
            if (_patrolTarget == null || (_patrolTarget.Value - myPos).sqrMagnitude <= reachSq)
            {
                if (TrySelectPatrolTarget(targets, out Vector3 next))
                {
                    _patrolTarget = next;
                    RefreshPatrolSpread(config, nowTick);
                }
            }

            // 周期性重新随机偏移：让巡逻路径蜿蜒扩散，而不是一条直线走到地标。
            if (unchecked(_patrolSpreadNextTick - nowTick) <= 0)
            {
                RefreshPatrolSpread(config, nowTick);
            }

            if (_patrolTarget == null)
            {
                StopMove(fpc);
                return;
            }

            Vector3 dest = _patrolTarget.Value + _patrolSpread;
            Move(fpc, dest, config);
            Face(fpc, dest - myPos);
            UpdateStuck(fpc, config);
            CheckPositionDrift(fpc, config);
            return;
        }

        // 离开有地标的房间后，清除巡逻目标。
        _patrolTarget = null;

        // 3) 跨房间巡逻：找一个相邻房间，走向其中心（BFS 单步）。
        Room? current = GetCurrentRoom();
        if (current != null)
        {
            List<RoomName> neighbors = new List<RoomName>(RoomNavigator.GetNeighbors(current.Name));
            neighbors.RemoveAll(n => n == current.Name);

            if (neighbors.Count > 0)
            {
                RoomName nextName = neighbors[UnityEngine.Random.Range(0, neighbors.Count)];
                Room? nextRoom = Room.Get(nextName)
                    .OrderBy(r => (r.Position - fpc.FpcModule.Position).sqrMagnitude)
                    .FirstOrDefault();

                if (nextRoom != null)
                {
                    Vector3 dest = nextRoom.Position;
                    Move(fpc, dest, config);
                    Face(fpc, dest - fpc.FpcModule.Position);
                    UpdateStuck(fpc, config);
                    CheckPositionDrift(fpc, config);
                    return;
                }
            }
        }

        // 4) 兜底：当前房间中心附近随机点（避免所有 bot 挤到同一个点）。
        if (current != null)
        {
            Vector3 center = current.Position
                + new Vector3(UnityEngine.Random.Range(-3f, 3f), 0f, UnityEngine.Random.Range(-3f, 3f));
            Move(fpc, center, config);
            Face(fpc, center - fpc.FpcModule.Position);
            UpdateStuck(fpc, config);
            CheckPositionDrift(fpc, config);
        }
        else
        {
            StopMove(fpc);
            CheckPositionDrift(fpc, config);
        }
    }

    /// <summary>
    /// 随机选一个巡逻地标，排除上一个目标（避免原地踏步），形成「来回」巡逻。
    /// 只有一个地标或排除后为空时，直接选可用的。
    /// </summary>
    private bool TrySelectPatrolTarget(List<Vector3> targets, out Vector3 target)
    {
        target = default;
        if (targets == null || targets.Count == 0)
        {
            return false;
        }

        if (targets.Count == 1)
        {
            target = targets[0];
            _lastPatrolTarget = target;
            return true;
        }

        // 排除上一个目标，避免连续选同一个。
        List<Vector3> candidates = new();
        foreach (Vector3 t in targets)
        {
            if ((t - _lastPatrolTarget).sqrMagnitude > 0.01f)
            {
                candidates.Add(t);
            }
        }

        if (candidates.Count == 0)
        {
            candidates.AddRange(targets);
        }

        target = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        _lastPatrolTarget = target;
        return true;
    }

    /// <summary>
    /// 随机刷新巡逻扩散偏移，并设定下一次刷新的时间（0.8~2 秒后）。
    /// 周期性刷新让巡逻路径持续蜿蜒，而不是一条直线走到地标。
    /// </summary>
    private void RefreshPatrolSpread(BotConfig config, int nowTick)
    {
        _patrolSpread = new Vector3(
            UnityEngine.Random.Range(-config.PatrolSpreadRadius, config.PatrolSpreadRadius),
            0f,
            UnityEngine.Random.Range(-config.PatrolSpreadRadius, config.PatrolSpreadRadius));
        _patrolSpreadNextTick = nowTick + UnityEngine.Random.Range(800, 2000);
    }

    /// <summary>
    /// 执行外部 AI 服务器下发的指令（MoveTo/Look/Shoot）。
    /// 仅执行主线程内受控的移动/转向/扳机操作，所有游戏对象访问仍在主线程完成。
    /// </summary>
    public void ExecuteOrders(BotOrders orders, BotConfig config)
    {
        if (_pendingLoadout)
        {
            TryInitLoadout(config);
            return;
        }

        if (!_player.IsAlive)
        {
            SetShoot(false);
            return;
        }

        if (_hub.roleManager.CurrentRole is not IFpcRole fpc)
        {
            SetShoot(false);
            return;
        }

        CurrentRoomName = GetCurrentRoom()?.Name;
        TargetRoomName = null;

        // 外部 AI 治疗指令：本地执行背包/拾取自疗流程。
        if (orders.Heal)
        {
            TryHeal(fpc, config);
        }

        // 外部 AI 投掷指令：按指定类型与方向走本地投掷流程（Initiation → 等待 → Confirm）。
        if (!string.IsNullOrEmpty(orders.Throw) && !_throwPending && Time.timeSinceLevelLoad >= _nextThrowTick)
        {
            bool isHe = orders.Throw!.Equals("he", StringComparison.OrdinalIgnoreCase);
            ItemType throwType = isHe ? ItemType.GrenadeHE : ItemType.GrenadeFlash;

            Item? throwItem = _player.Items.FirstOrDefault(i => i.Type == throwType);
            if (throwItem != null)
            {
                // 对准目标方向（外部 AI 给的 tx/ty/tz 世界坐标点）。
                if (orders.HasThrowTarget)
                {
                    Vector3 target = new Vector3(orders.ThrowX, orders.ThrowY, orders.ThrowZ);
                    Face(fpc, target - _hub.PlayerCameraReference.position);
                }

                _hub.inventory.ServerSelectItem(throwItem.Serial);
                if (throwItem is ThrowableItem t)
                {
                    t.Base.ServerProcessInitiation();
                    // FF-10：ServerProcessInitiation 仅在 AllowHolster && !HasBlock 时才启动 ThrowStopwatch，
                    // 否则静默 no-op（如 bot 已被 Handcuff/ItemPrimaryAction 阻断）。必须检查 IsRunning 确认。
                    if (t.Base.ThrowStopwatch.IsRunning)
                    {
                        _throwPending = true;
                        _throwPendingStart = Time.timeSinceLevelLoad;
                        // FF-10：服务端 CurrentTimeTolerance=0.8f，ReadyToThrow 阈值为 0.8f * ThrowingAnimTime。
                        _throwReadyTime = Time.timeSinceLevelLoad + 0.8f * t.Base.ThrowingAnimTime;
                        _throwPendingType = throwType;
                        Logger.Info($"[ScpBot] 机器人 #{Id} 收到外部指令投掷 {throwType}。");
                    }
                    else
                    {
                        Logger.Warn($"[ScpBot] 机器人 #{Id} 投掷启动失败（ServerProcessInitiation no-op，可能持有/被阻断）。");
                    }
                }
            }
        }
        else if (_throwPending)
        {
            // 推进外部/本地投掷动画。
            if (Time.timeSinceLevelLoad >= _throwReadyTime)
            {
                ConfirmThrow();
            }
        }

        if (orders.HasLook)
        {
            // 瞄准方向从眼睛/相机出发（与本地 AI 一致），避免俯仰偏高。
            Vector3 lookPos = new Vector3(orders.LookX, orders.LookY, orders.LookZ);
            Vector3 eyePos = _hub.PlayerCameraReference.position;
            Face(fpc, lookPos - eyePos);
        }

        // 开火前的弹药处理（与本地 Tick 一致）：外部 AI 只下发 shoot 指令，
        // 弹匣补弹/换弹必须在本地完成，否则打空弹匣后 bot 会哑火。
        bool wantShoot = orders.Shoot == true;
        bool canShoot = true;

        if (wantShoot)
        {
            // 备用弹药自动补给（火力压制等长时间射击不会弹尽停火；弹匣机制保留）。
            if (config.AutoRefillReserveAmmo)
            {
                RefillReserveAmmo(config);
            }

            if (config.InfiniteAmmo)
            {
                // 无限弹药：开火前直接补满弹匣/膛内/备用，无换弹停顿。
                RefillAmmo(config);
            }
            else if (NeedsReload(config))
            {
                // 弹匣空了先换弹；换弹动画约 1.5s，期间保持瞄准但不开火。
                SetShoot(false);
                TryStartReload(config);
                _reloadWaitTime += config.TickInterval;

                if (_reloadWaitTime < 1.5f)
                {
                    canShoot = false;   // 换弹中不开火，但下方移动照常执行
                }
                else
                {
                    _isReloading = false;
                    _reloadWaitTime = 0f;
                    _reloadKeyHeld = false;
                    _reloadTriggered = false;
                }
            }
        }

        SetShoot(canShoot && wantShoot);

        if (orders.HasChaseTo)
        {
            // 追击：本地先算地表 NavMesh 拐点（若可用），再移动。
            ChaseTo(new Vector3(orders.ChaseX, orders.ChaseY, orders.ChaseZ), config);
        }
        else if (orders.HasMoveTo)
        {
            Move(fpc, new Vector3(orders.MoveX, orders.MoveY, orders.MoveZ), config);
            UpdateStuck(fpc, config);
            CheckPositionDrift(fpc, config);
        }
        else
        {
            StopMove(fpc);
            CheckPositionDrift(fpc, config);
        }
    }

    /// <summary>
    /// 示教学习：开始跟随指定玩家（管理员带领 bot 走正确路线）。
    /// 跟随期间每 tick 记录经过的房间序列；停止时提交轨迹给外部 AI 学习。
    /// </summary>
    public void StartFollow(Player leader)
    {
        _followTarget = leader.ReferenceHub;
        _followLastRoom = null;
        _followTrace.Clear();
        Logger.Info($"[ScpBot] 机器人 #{Id} 开始跟随 {leader.DisplayName}（示教模式，记录房间轨迹）。");
    }

    /// <summary>是否正在跟随（示教模式）。</summary>
    public bool IsFollowing => _followTarget != null;

    /// <summary>卡在同一房间且无交战的累计时间（秒），供 BotManager 超时检测。</summary>
    public float IdleStuckTime => _idleStuckTime;

    /// <summary>是否处于「卡房无交战」状态（房间已知且计时 &gt; 0）。</summary>
    public bool IsIdleStuck => _idleStuckTime > 0f;

    /// <summary>
    /// 更新卡房超时计时：同一房间内持续无交战（无目标或未开火）则累计；
    /// 有目标/开火/换房间则清零。由 BotManager 每 tick 调用。
    /// </summary>
    public void UpdateIdleStuck(BotConfig config)
    {
        if (!config.IdleStuckTimeoutEnabled || config.IdleStuckTimeout <= 0f)
        {
            _idleStuckRoom = null;
            _idleStuckTime = 0f;
            return;
        }

        // 交战判定：有目标且射程内（开火条件）视为有进展。
        bool inCombat = _target != null && _player.IsAlive;
        if (inCombat && _target != null)
        {
            Vector3 myPos = _player.Position;
            Vector3 targetPos = _target.transform.position;
            inCombat = (targetPos - myPos).sqrMagnitude <= config.AttackRange * config.AttackRange;
        }

        RoomName? room = GetCurrentRoom()?.Name;

        if (!inCombat && room.HasValue && room.Value == _idleStuckRoom)
        {
            // 同房间且无交战：累计。
            _idleStuckTime += config.TickInterval;
        }
        else
        {
            // 有交战 / 换房间 / 房间未知：重置。
            _idleStuckRoom = room;
            _idleStuckTime = 0f;
        }
    }

    /// <summary>重置卡房计时（重生/传送后调用）。</summary>
    public void ResetIdleStuck()
    {
        _idleStuckRoom = null;
        _idleStuckTime = 0f;
    }

    /// <summary>
    /// 停止跟随：把记录的轨迹提交给外部 AI（trace 消息），返回轨迹长度（房间数）。
    /// </summary>
    public int StopFollow()
    {
        _followTarget = null;
        _followLastRoom = null;

        // 轨迹至少 2 个房间才有学习价值。
        if (_followTrace.Count < 2)
        {
            _followTrace.Clear();
            return 0;
        }

        // 去重连续重复房间（同一房间内停留不重复记录）。
        List<string> rooms = new();
        string? last = null;
        foreach (string r in _followTrace)
        {
            if (r != last)
            {
                rooms.Add(r);
                last = r;
            }
        }

        _followTrace.Clear();

        if (rooms.Count < 2)
        {
            return 0;
        }

        BotManager.SubmitTrace(Id, rooms);
        Logger.Info($"[ScpBot] 机器人 #{Id} 示教轨迹已提交（{rooms.Count} 个房间）。");
        return rooms.Count;
    }

    /// <summary>
    /// 跟随 tick：朝玩家位置移动，并记录经过的房间（房间变化时追加到轨迹）。
    /// </summary>
    private void FollowTick(IFpcRole fpc, BotConfig config)
    {
        ReferenceHub? leader = _followTarget;
        if (leader == null || !leader.IsAlive())
        {
            // 跟随目标消失/死亡：停止跟随并提交轨迹。
            StopFollow();
            return;
        }

        // 记录房间变化。
        RoomName? current = GetCurrentRoom()?.Name;
        if (current.HasValue && current.Value != _followLastRoom)
        {
            _followLastRoom = current.Value;
            _followTrace.Add(current.Value.ToString());
        }

        // 朝玩家位置移动（保持 3m 距离，避免贴脸挡路）。
        Vector3 leaderPos = leader.transform.position;
        Vector3 myPos = fpc.FpcModule.Position;
        Vector3 toLeader = leaderPos - myPos;
        toLeader.y = 0f;
        float d = toLeader.magnitude;

        // 面向玩家，保持跟随距离。
        if (d > 3f)
        {
            // 目标点 = 玩家位置前方一点（模拟跟随走位，避免正正好好站在玩家身上）。
            Vector3 dir = toLeader.normalized;
            Vector3 followTarget = leaderPos - (dir * 2f);
            Face(fpc, toLeader);
            Move(fpc, followTarget, config);
        }
        else
        {
            Face(fpc, toLeader);
            StopMove(fpc);
        }

        UpdateStuck(fpc, config);
        CheckPositionDrift(fpc, config);
    }

    /// <summary>
    /// 外部 AI 追击指令：朝目标位置移动，地表（Outside）优先走 NavMesh 拐点绕山体/楼群，
    /// 否则直线追击（Move 内的障碍绕行 + 卡住瞬移兜底）。
    /// </summary>
    public void ChaseTo(Vector3 targetPos, BotConfig config)
    {
        if (_hub.roleManager.CurrentRole is not IFpcRole fpc)
        {
            return;
        }

        if (TryGetSurfaceNavPoint(fpc, targetPos, config, out Vector3 navPoint))
        {
            Move(fpc, navPoint, config);
        }
        else
        {
            Move(fpc, targetPos, config);
        }

        UpdateStuck(fpc, config);
        CheckPositionDrift(fpc, config);
    }

    /// <summary>外部 AI 激活但本条 tick 未下发指令时的待命行为：停走、停火。</summary>
    public void Idle(BotConfig config)
    {
        if (_pendingLoadout)
        {
            // FF-38：新 bot 生成后外部 AI 尚未认知其 ID（或快照未包含），会一直走 Idle 分支；
            // 此前直接 return 导致 _pendingLoadout 恒 true、bot 永远不初始化配装，成为无角色空壳。
            // 与 Tick() 一致：UserId 就绪后立即初始化，不依赖外部订单。
            if (_hub.authManager.UserId == null)
            {
                return;
            }

            TryInitLoadout(config);
            return;
        }

        SetShoot(false);
        if (_hub.roleManager.CurrentRole is IFpcRole fpc)
        {
            StopMove(fpc);
            CheckPositionDrift(fpc, config);
        }
    }

    private void AcquireTarget()
    {
        // 拟人索敌：当前目标仍可见（或正在交战）则保持；不可见则尝试换可见目标。
        if (_target != null && IsValidTarget(_target) && IsTargetVisible(_target))
        {
            return;
        }

        _target = null;

        Vector3 myPos = _hub.transform.position;
        float best = float.MaxValue;

        // 用 List 拷贝快照，避免主线程迭代期间集合被外部修改。
        foreach (ReferenceHub h in new List<ReferenceHub>(ReferenceHub.AllHubs))
        {
            if (h == null || h == _hub)
            {
                continue;
            }

            if (!h.IsAlive())
            {
                continue;
            }

            if (h.roleManager.CurrentRole is not IFpcRole)
            {
                continue;
            }

            // 敌我判定只依据阵营（HitboxIdentity.IsEnemy），不跳过 Dummy：
            // 这样敌对阵营的 bot 也会被攻击，同阵营的 bot 则不会。
            if (!HitboxIdentity.IsEnemy(_hub, h))
            {
                continue;
            }

            // 拟人索敌：只把「看得见」的敌人作为目标（消除隔掩体透视追击）。
            // 玩家躲在岩石/建筑后时 bot 不该知道其位置。
            if (!IsTargetVisible(h))
            {
                continue;
            }

            float d = (h.transform.position - myPos).sqrMagnitude;
            if (d < best)
            {
                best = d;
                _target = h;
            }
        }
    }

    /// <summary>目标是否在视线内（拟人索敌核心：看不见就不当目标）。</summary>
    private bool IsTargetVisible(ReferenceHub? h)
    {
        if (h == null || _hub == null)
        {
            return false;
        }

        try
        {
            Vector3 bodyPos = GetAimPoint(h, 0.35f);
            float d = (h.transform.position - _hub.transform.position).magnitude;
            if (d > 60f)
            {
                return false;
            }

            return CanSee(bodyPos, BotPlugin.Instance?.Config ?? new BotConfig());
        }
        catch
        {
            return false;
        }
    }

    private bool IsValidTarget(ReferenceHub h)
    {
        return h != null
            && h.IsAlive()
            && h.roleManager.CurrentRole is IFpcRole
            && HitboxIdentity.IsEnemy(_hub, h);
    }

    /// <summary>获取机器人当前所在房间（按位置优先，缓存兜底）。</summary>
    private Room? GetCurrentRoom()
    {
        return _player.Room ?? _player.CachedRoom;
    }

    /// <summary>获取目标玩家当前所在房间（按位置优先，缓存兜底）。</summary>
    private Room? GetTargetRoom()
    {
        if (_target == null)
        {
            return null;
        }

        Player? targetPlayer = Player.Get(_target);
        if (targetPlayer == null)
        {
            return null;
        }

        return targetPlayer.Room ?? targetPlayer.CachedRoom;
    }

    /// <summary>
    /// 获取当前房间尚未走完的下一个内部航点；进入新房间时重置进度、随机选一条路线，
    /// 并比较路线起点/终点与机器人的距离，从较近的一端开始走（支持正走/倒走）。
    /// 到达路线尽头时自动翻向（正→反 / 反→正），形成来回巡逻，不会再走其他路径分支。
    /// </summary>
    private bool TryGetNextWaypoint(RoomName currentRoom, Vector3 myPos, out Vector3 point)
    {
        point = default;

        if (_waypointRoom != currentRoom)
        {
            _waypointRoom = currentRoom;
            _waypointRoute = RoomWaypoints.GetRandomRoute(currentRoom);

            // 就近端起步：谁近从谁走。
            if (_waypointRoute == null || _waypointRoute.Count <= 1)
            {
                _waypointIndex = 0;
                _waypointForward = true;
            }
            else
            {
                float startDist = (_waypointRoute[0] - myPos).sqrMagnitude;
                float endDist = (_waypointRoute[_waypointRoute.Count - 1] - myPos).sqrMagnitude;
                _waypointForward = startDist <= endDist;
                _waypointIndex = _waypointForward ? 0 : _waypointRoute.Count - 1;
            }
        }

        if (_waypointRoute == null || _waypointRoute.Count == 0)
        {
            return false;
        }

        // 到达正序尽头 → 翻向，从倒数第二点开始往回走。
        if (_waypointForward && _waypointIndex >= _waypointRoute.Count)
        {
            _waypointForward = false;
            _waypointIndex = _waypointRoute.Count - 2;

            if (_waypointIndex < 0)
            {
                return false;
            }

            point = _waypointRoute[_waypointIndex];
            return true;
        }

        // 到达反序尽头 → 翻向，从第 1 点开始往正序走。
        if (!_waypointForward && _waypointIndex < 0)
        {
            _waypointForward = true;
            _waypointIndex = 1;

            if (_waypointIndex >= _waypointRoute.Count)
            {
                return false;
            }

            point = _waypointRoute[_waypointIndex];
            return true;
        }

        point = _waypointRoute[_waypointIndex];
        return true;
    }

    /// <summary>
    /// 计算跨房间追击时当前应前往的目标点（路径上下一间房的中心）。
    /// 多路线策略：目标固定后生成最多 MaxRouteOptions 条候选路线，按 bot Id 分散分配
    /// （同队也分散）；跨队夹击时 NTF/CI 两队分配相反路线（首条 vs 末条）；
    /// 当前路线阵亡数超阈值时切换到与当前路线分歧最大的备选路线。
    /// 同房间/房间未知/无路径时返回 null，调用方退化为直线追击。
    /// </summary>
    private Vector3? ComputeNavPoint(IFpcRole fpc, BotConfig config)
    {
        Room? current = GetCurrentRoom();
        Room? goal = GetTargetRoom();

        if (current == null || goal == null || current.Name == goal.Name)
        {
            // 同房间或位置未知，没必要走房间路径。
            ResetPath();
            return null;
        }

        // 目标变化时重新生成多路线并分配。
        if (_roomPath == null || _roomPathGoal != goal.Name)
        {
            _roomRoutes = RoomNavigator.FindPaths(current.Name, goal.Name, Math.Max(1, config.MaxRouteOptions));
            _roomPathGoal = goal.Name;

            // 按 bot Id 分散分配路线（同队 bot 也分散，避免全部挤一条路）。
            // 跨队夹击：NTF 与 CI 目标相同时两队走相反路线（NTF 走首条、CI 走末条）。
            int routeCount = _roomRoutes.Count;
            if (routeCount > 0)
            {
                if (routeCount >= 2 && Team == PlayerRoles.Team.ChaosInsurgency)
                {
                    // CI 走末条（与 NTF 的首条相反），形成夹击。
                    _routeIndex = routeCount - 1;
                }
                else
                {
                    // 同队内部也分散：Id % 路线数（保证不重复但稳定）。
                    _routeIndex = Id % routeCount;
                }

                _roomPath = _roomRoutes[_routeIndex];
            }
            else
            {
                _roomPath = null;
                _routeIndex = 0;
            }

            _roomPathIndex = 0;
            _routeAssignTick = Time.timeSinceLevelLoad;
            UpdatePathSummary();
        }

        if (_roomPath == null || _roomRoutes == null || _roomRoutes.Count == 0)
        {
            // 房间图不可达：退化为直线追击（卡住兜底会兜住）。
            UpdatePathSummary();
            return null;
        }

        // 打不过换路：当前路线窗口内阵亡数超阈值 → 切换到与当前路线分歧最大的备选路线。
        if (ShouldSwitchRoute(config))
        {
            SwitchToDivergentRoute();
        }

        // 若已进入路径上的节点房间，则推进到下一节点。
        while (_roomPathIndex < _roomPath.Count && current.Name == _roomPath[_roomPathIndex])
        {
            _roomPathIndex++;
        }

        if (_roomPathIndex >= _roomPath.Count)
        {
            // 已抵达路径末段，切换为直线追击收尾。
            ResetPath();
            return null;
        }

        RoomName nextName = _roomPath[_roomPathIndex];

        // 同名房间可能有多实例（如检查站），取离机器人最近的实例中心作为目标点。
        Room? nextRoom = Room.Get(nextName)
            .OrderBy(r => (r.Position - fpc.FpcModule.Position).sqrMagnitude)
            .FirstOrDefault();

        return nextRoom?.Position;
    }

    /// <summary>
    /// 「打不过换路」判定：当前路线（_roomPath）上最近的己方阵亡是否超阈值。
    /// 阵亡记录由 BotManager 在 OnPlayerDying 中写入 _routeCasualties（每 bot 自己的记录，
    /// 由路线统计器集中管理，这里读取全局路线阵亡表）。
    /// </summary>
    private bool ShouldSwitchRoute(BotConfig config)
    {
        if (_roomPath == null || config.RouteCasualtyThreshold <= 0)
        {
            return false;
        }

        // 统计窗口内的阵亡数（全队 bot 在当前路线上的阵亡，由 BotManager 维护）。
        return BotManager.GetRouteCasualtyCount(_roomPath, config.RouteCasualtyWindow) >= config.RouteCasualtyThreshold;
    }

    /// <summary>切换到与当前路线分歧最大的备选路线（房间序列差异最大的一条）。</summary>
    private void SwitchToDivergentRoute()
    {
        if (_roomRoutes == null || _roomRoutes.Count <= 1 || _roomPath == null)
        {
            return;
        }

        int bestIndex = -1;
        int bestDivergence = -1;
        for (int i = 0; i < _roomRoutes.Count; i++)
        {
            if (i == _routeIndex)
            {
                continue;
            }

            // 分歧度：两条路线从第 2 个节点起不同的房间数量。
            List<RoomName> candidate = _roomRoutes[i];
            int divergence = 0;
            int shared = Math.Min(_roomPath.Count, candidate.Count);
            for (int j = 1; j < shared; j++)
            {
                if (!_roomPath[j].Equals(candidate[j]))
                {
                    divergence++;
                }
            }

            divergence += Math.Abs(_roomPath.Count - candidate.Count);

            if (divergence > bestDivergence)
            {
                bestDivergence = divergence;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            _routeIndex = bestIndex;
            _roomPath = _roomRoutes[bestIndex];
            _roomPathIndex = 0;
            Logger.Info($"[ScpBot] 机器人 #{Id} 当前路线阵亡过多，切换到备选路线 #{bestIndex + 1}。");
        }
    }

    private void ResetPath()
    {
        _roomPath = null;
        _roomPathGoal = null;
        _roomPathIndex = 0;
        _roomRoutes = null;
        PathSummary = string.Empty;
    }

    private void UpdatePathSummary()
    {
        if (_roomPath == null || _roomPath.Count == 0)
        {
            PathSummary = _roomPath == null ? "(无路径)" : "(同房间)";
            return;
        }

        PathSummary = string.Join(" -> ", _roomPath.Skip(_roomPathIndex));
    }

    private static Vector3 GetAimPoint(ReferenceHub h, float aimHeight)
    {
        if (h.roleManager.CurrentRole is IFpcRole fpc)
        {
            return fpc.FpcModule.Position + (Vector3.up * aimHeight);
        }

        return h.PlayerCameraReference.position;
    }

    /// <summary>
    /// 在目标身体点上叠加随机散布偏移，得到开火瞄准点（模拟真人枪法误差，避免命中率过高）。
    /// 水平方向 ±AimSpread 均匀随机，垂直方向收窄到 0.6 倍（防止弹道明显打天/打地）。
    /// 每 tick 重新随机，连发时子弹自然扩散。AimSpread &lt;= 0 时返回原位置（精确瞄准）。
    /// </summary>
    private static Vector3 ApplyAimSpread(Vector3 bodyPos, BotConfig config)
    {
        // FF-09：AimSpread 为 NaN 时 Random.Range(NaN, NaN) 产生 NaN 瞄准点。
        if (config.AimSpread <= 0f || float.IsNaN(config.AimSpread))
        {
            return bodyPos;
        }

        float s = config.AimSpread;
        return new Vector3(
            bodyPos.x + UnityEngine.Random.Range(-s, s),
            bodyPos.y + UnityEngine.Random.Range(-s * 0.6f, s * 0.6f),
            bodyPos.z + UnityEngine.Random.Range(-s, s));
    }

    private bool CanSee(Vector3 aimPos, BotConfig config)
    {
        // 使用游戏原生视线检测，忽略黑暗，让机器人在暗处也能“看见”。
        VisionInformation vi = VisionInformation.GetVisionInformation(
            _hub,
            _hub.PlayerCameraReference,
            aimPos,
            targetRadius: 0.3f,
            visionTriggerDistance: config.MaxVisionDistance,
            checkFog: false,
            checkLineOfSight: true,
            maskLayer: VisionInformation.VisionLayerMask,
            checkInDarkness: false);

        return vi.IsLooking;
    }

    /// <summary>
    /// 采集当前敌对目标列表（含本地视线检测结果），供外部 AI 服务器做索敌/走位/开火决策。
    /// 视线检测必须在主线程执行（依赖游戏物理 API），这里算好后随快照发给外部端。
    /// </summary>
    public List<EnemyPerception> CollectEnemyPerceptions(BotConfig config)
    {
        List<EnemyPerception> result = new();
        Vector3 myPos = _hub.transform.position;

        foreach (ReferenceHub h in new List<ReferenceHub>(ReferenceHub.AllHubs))
        {
            if (h == null || h == _hub || !h.IsAlive())
            {
                continue;
            }

            if (h.roleManager.CurrentRole is not IFpcRole)
            {
                continue;
            }

            if (!HitboxIdentity.IsEnemy(_hub, h))
            {
                continue;
            }

            Vector3 bodyPos = GetAimPoint(h, config.AimHeight);
            float d = (h.transform.position - myPos).magnitude;
            bool visible = d <= config.MaxVisionDistance && CanSee(bodyPos, config);

            result.Add(new EnemyPerception
            {
                NetId = h.netId,
                Position = h.transform.position,
                // 瞄准点带随机散布：外部 AI 的 look/开火同样打不准，保持与本地 AI 一致。
                AimPosition = ApplyAimSpread(bodyPos, config),
                Distance = d,
                Team = h.GetTeam().ToString(),
                Visible = visible,
            });
        }

        return result;
    }

    /// <summary>
    /// 判断 Vector3 三个分量是否均为有限值（无 NaN/Infinity）。FF-09 全链路入口校验用。
    /// </summary>
    private static bool IsFinite(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
            && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
            && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }

    /// <summary>
    /// 让角色看向指定方向（水平 + 俯仰一步精确对准）。
    /// 直接用 Atan2/Asin 计算角度写入 FpcMouseLook，替代 LookAtDirection 的 lerp 平滑：
    /// 旧实现 lerp=0.5 时每 tick 只转一半角度，战斗走位中俯仰角持续滞后、实际瞄准线偏高，
    /// 而服务器端弹道 = PlayerCameraReference.forward（HitscanHitregModuleBase.ForwardRay），
    /// 直接导致弹孔打在瞄准点上方。一步对准后弹道与瞄准点重合。
    /// 顺带避免 LookAtDirection 用 eulerAngles 的 0~360 环绕问题（向上看时会被错误钳制到 -88°）。
    /// </summary>
    private static void Face(IFpcRole fpc, Vector3 dir)
    {
        // FF-09：NaN 的所有比较都为 false，sqrMagnitude<0.0001f 守卫对 NaN 失效，
        // 必须显式拒绝非有限分量，否则 Atan2/Asin(NaN) 写坏瞄准（网络对端/配置可注入 NaN）。
        if (!IsFinite(dir) || dir.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Vector3 d = dir.normalized;

        // 水平角：角色 forward 默认 +Z，绕 Y 轴角度 = Atan2(x, z)，范围 -180~180（ClampHorizontal 内）。
        float horizontal = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;

        // 俯仰角：Asin 得 -90~90，负值 = 向下看（与 FpcMouseLook 约定一致：CurrentVertical 负 = 俯视）。
        float vertical = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;

        FpcMouseLook mouseLook = fpc.FpcModule.MouseLook;
        mouseLook.CurrentHorizontal = horizontal;
        mouseLook.CurrentVertical = vertical;
    }

    /// <summary>
    /// 战斗走位。猛冲模式（AggressiveCharge）下：任何距离都朝目标直线冲锋
    /// （叠加小幅横移模拟晃动），永不后退、永不绕圈——AI 无所畏惧。
    /// 关闭时走原状态机（追击/绕圈/后撤）。
    /// </summary>
    private void MoveCombat(IFpcRole fpc, Vector3 aimPos, float dist, BotConfig config)
    {
        Vector3 myPos = fpc.FpcModule.Position;
        int nowTick = Environment.TickCount;

        // 横移方向周期性随机翻转，模拟真人反复横跳。
        if (unchecked(_nextStrafeFlipTick - nowTick) <= 0)
        {
            _strafeDirection = UnityEngine.Random.Range(0, 2) == 0 ? -1 : 1;
            _nextStrafeFlipTick = nowTick + UnityEngine.Random.Range(config.StrafeFlipMinMs, config.StrafeFlipMaxMs);
        }

        Vector3 moveDir;

        if (config.AggressiveCharge)
        {
            // 猛冲：任何距离都朝目标冲锋。贴脸（<3m）纯直线压上；较远时叠加小幅横移晃动。
            Vector3 toTarget = aimPos - myPos;
            toTarget.y = 0f;

            if (toTarget.sqrMagnitude < 0.0001f)
            {
                StopMove(fpc);
                return;
            }

            Vector3 toTargetN = toTarget.normalized;
            if (dist < 3f)
            {
                // 贴脸：纯直线压上，不停不绕。
                moveDir = toTargetN;
            }
            else
            {
                // 冲锋 + 小幅横移（模拟真人晃动，横移强度弱，不影响冲锋方向）。
                Vector3 right = Vector3.Cross(Vector3.up, toTargetN);
                Vector3 desired = (toTargetN * 0.9f) + (right * _strafeDirection * 0.1f);
                desired.y = 0f;
                moveDir = desired.sqrMagnitude < 0.0001f ? toTargetN : desired.normalized;
            }
        }
        else
        {
            // 原状态机：追击/绕圈/后撤（仅当关闭猛冲模式时使用）。
            CombatState nextState = dist > config.PreferredEngageDistance + config.RangeTolerance
                ? CombatState.Chase
                : CombatState.Orbit;

            if (nextState != _combatState)
            {
                _combatState = nextState;
                if (nextState == CombatState.Orbit)
                {
                    _orbitDirection = UnityEngine.Random.Range(0, 2) == 0 ? -1 : 1;
                }
            }

            if (_combatState == CombatState.Orbit)
            {
                if (dist < config.OrbitRetreatDistance)
                {
                    // 贴脸：不再后撤（避免双方僵持），朝目标压上。
                    moveDir = aimPos - myPos;
                    moveDir.y = 0f;
                }
                else if (dist < 12f)
                {
                    // 室内近距离：朝目标推进 + 小幅横移（避免切向绕圈撞墙倒退）。
                    moveDir = BuildCloseQuarterDirection(myPos, aimPos, config);
                }
                else
                {
                    moveDir = BuildOrbitDirection(myPos, aimPos, config);
                }
            }
            else
            {
                moveDir = BuildChaseDirection(myPos, aimPos, config);
            }
        }

        if (moveDir.sqrMagnitude < 0.001f)
        {
            StopMove(fpc);
            return;
        }

        // 方向转成近点目标交给 Move（复用障碍绕行 + 电梯防护 + 卡住兜底）。
        Vector3 targetPoint = myPos + (moveDir.normalized * Mathf.Max(1f, config.PreferredEngageDistance * 0.5f));
        Move(fpc, targetPoint, config);
    }

    /// <summary>
    /// 室内近距离走位：主要朝目标推进（压上），叠加小幅横移（模拟真人晃动，不远离目标）。
    /// 解决小空间绕圈频繁撞墙 → 双方拉开距离僵持的问题。
    /// </summary>
    private Vector3 BuildCloseQuarterDirection(Vector3 myPos, Vector3 targetPos, BotConfig config)
    {
        Vector3 toTarget = targetPos - myPos;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f)
        {
            return Vector3.zero;
        }

        Vector3 toTargetN = toTarget.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, toTargetN);

        // 朝目标 75% + 横移 25%（横移幅度小，不偏离目标太远）。
        Vector3 desired = (toTargetN * 0.75f) + (right * _strafeDirection * 0.25f);
        desired.y = 0f;
        return desired.sqrMagnitude < 0.0001f ? toTargetN : desired.normalized;
    }

    /// <summary>追击方向：朝目标 + 右侧横移 + 队友分离。</summary>
    private Vector3 BuildChaseDirection(Vector3 myPos, Vector3 targetPos, BotConfig config)
    {
        Vector3 toGoal = targetPos - myPos;
        toGoal.y = 0f;
        if (toGoal.sqrMagnitude < 0.0001f)
        {
            return Vector3.zero;
        }

        Vector3 toGoalN = toGoal.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, toGoalN);
        Vector3 separation = BuildSeparationDirection(myPos, config);

        Vector3 desired = toGoalN
            + (right * _strafeDirection * config.ChaseStrafeBias)
            + (separation * 1.1f);
        desired.y = 0f;
        return desired.sqrMagnitude < 0.0001f ? toGoalN : desired.normalized;
    }

    /// <summary>绕圈方向：切向移动 + 距离修正（太远内收）+ 随机横移 + 队友分离。</summary>
    private Vector3 BuildOrbitDirection(Vector3 myPos, Vector3 targetPos, BotConfig config)
    {
        Vector3 toTarget = targetPos - myPos;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f)
        {
            return Vector3.zero;
        }

        Vector3 radial = toTarget.normalized;
        Vector3 tangent = Vector3.Cross(Vector3.up, radial) * _orbitDirection;
        float currentDistance = toTarget.magnitude;
        float orbitMinDistance = Mathf.Max(2f, config.PreferredEngageDistance * 0.7f);

        Vector3 distanceCorrection = currentDistance < orbitMinDistance
            ? Vector3.zero
            : radial * config.OrbitInwardBias;

        Vector3 radialStrafe = radial * (_strafeDirection * 0.22f);
        Vector3 separation = BuildSeparationDirection(myPos, config);

        Vector3 desired = tangent + distanceCorrection + radialStrafe + (separation * 1.1f);
        desired.y = 0f;
        return desired.sqrMagnitude < 0.0001f ? tangent : desired.normalized;
    }

    /// <summary>队友分离：同队机器人靠太近互相排斥，避免扎堆。</summary>
    private Vector3 BuildSeparationDirection(Vector3 myPos, BotConfig config)
    {
        float radius = config.NearbyBotAvoidanceRadius;
        if (radius <= 0.05f)
        {
            return Vector3.zero;
        }

        Vector3 separation = Vector3.zero;
        foreach (Bot other in BotManager.Snapshot())
        {
            if (other == null || other.Id == Id || !other.IsAlive)
            {
                continue;
            }

            Vector3 away = myPos - other.Position;
            away.y = 0f;
            float distance = away.magnitude;
            if (distance < 0.01f || distance > radius)
            {
                continue;
            }

            float weight = 1f - (distance / radius);
            separation += away.normalized * weight;
        }

        separation.y = 0f;
        return separation.sqrMagnitude < 0.0001f ? Vector3.zero : separation.normalized;
    }

    /// <summary>
    /// 地表（Outside）NavMesh 路径查询：返回下一个应前往的拐点。目标移动超过阈值时重查路径。
    /// 非地表 / NavMesh 不可用 / 已到终点附近时返回 false，调用方回退到原有寻路。
    /// </summary>
    private bool TryGetSurfaceNavPoint(IFpcRole fpc, Vector3 aimPos, BotConfig config, out Vector3 navPoint)
    {
        navPoint = default;

        // NavMesh 覆盖 Surface + Entrance 全区域：室内（EZ）同样可用，不再限定 Outside。
        if (!SurfaceNavMeshService.HasNavMesh)
        {
            _surfacePath = null;
            return false;
        }

        Vector3 myPos = fpc.FpcModule.Position;

        // 目标移动超过 2m 或尚无路径 → 重新查 NavMesh 路径。
        if (_surfacePath == null || (_surfacePathGoal - aimPos).sqrMagnitude > 4f)
        {
            if (!SurfaceNavMeshService.TryFindPath(myPos, aimPos, out List<Vector3> corners, 5f))
            {
                return false;
            }

            _surfacePath = corners;
            _surfacePathIndex = 0;
            _surfacePathGoal = aimPos;
        }

        // 推进已到达的拐点。
        while (_surfacePathIndex < _surfacePath.Count
            && (_surfacePath[_surfacePathIndex] - myPos).sqrMagnitude
                <= config.WaypointReachDistance * config.WaypointReachDistance)
        {
            _surfacePathIndex++;
        }

        if (_surfacePathIndex >= _surfacePath.Count)
        {
            // 已到终点附近，交给直线追击收尾。
            _surfacePath = null;
            return false;
        }

        navPoint = _surfacePath[_surfacePathIndex];
        return true;
    }

    private void Move(IFpcRole fpc, Vector3 targetPos, BotConfig config)
    {
        // FF-09：NaN/Infinity 坐标会让 RelativePosition 编码垃圾/ServerOverridePosition 被拒、
        // 卡死检测失效 —— 网络订单/配置注入的非有限值在此丢弃，退化为停止。
        if (!IsFinite(targetPos))
        {
            StopMove(fpc);
            return;
        }

        Vector3 myPos = fpc.FpcModule.Position;
        Vector3 toTarget = targetPos - myPos;
        toTarget.y = 0f;

        if (toTarget.sqrMagnitude < 0.01f)
        {
            StopMove(fpc);
            return;
        }

        // 刚开门：等门板移开（最多 0.8s）再继续移动，避免顶门板被卡住。
        if (IsWaitingForDoor())
        {
            StopMove(fpc);
            return;
        }

        Vector3 dir = toTarget.normalized;
        Vector3 eye = myPos + (Vector3.up * EyeHeight);

        // 前方有障碍时尝试左右绕行（优先朝目标方向绕，避免绕行背道而驰）；
        // 绕行失败先尝试开门（门要提前在 10m 内开、且等待门打开），
        // 全部失败则停下交给“卡住”处理。
        if (Physics.Raycast(eye, dir, out _, config.ObstacleLookAhead, VisionInformation.VisionLayerMask))
        {
            if (!TrySteer(eye, dir, toTarget.normalized, out dir) && !TryOpenDoor(fpc, myPos, dir, config))
            {
                StopMove(fpc);
                return;
            }
        }
        else
        {
            // 前方暂时畅通，但若路径前方 10m 内有关闭门，提前开门并等门打开，避免走到门口被挡。
            TryPreOpenDoor(fpc, myPos, dir, config);
        }

        // FF-13：步长必须按 tick 间隔计算。Move 由 TickLoop 每 TickInterval 调用一次，
        // 若用 Time.deltaTime（单帧间隔 ≈16ms）则实际速度 ≈ MoveSpeed×deltaTime/TickInterval，
        // 60fps 下只有配置值的约 1/6，且随服务器帧率波动（追不上目标、交战距离判定失真）。
        Vector3 step = dir * (config.MoveSpeed * config.TickInterval);
        Vector3 targetWorld = myPos + step;

        // 漂移冷却期：用 ServerOverridePosition 直接移动，绕过 RelativePosition / waypoint 系统，
        // 防止继续引用电梯/传送平台等移动物体的 waypoint 导致再次漂移。
        if (_serverOverrideFrames > 0)
        {
            fpc.FpcModule.ServerOverridePosition(targetWorld);
            _serverOverrideFrames--;
            _lastPosition = targetWorld;
            return;
        }

        RelativePosition relPos = new(targetWorld);

        // 精度诊断：如果 RelativePosition 因为 waypoint 精度丢失把坐标截断回原点附近，
        // OutOfRange 会置 true，此时不应使用该位置——改用 ServerOverridePosition 瞬移一小步。
        if (relPos.OutOfRange)
        {
            Logger.Warn($"[ScpBot] 机器人 #{Id} 目标位置超出 RelativePosition 精度范围（{RoomWaypoints.Format(targetWorld)}），改用瞬移。");
            fpc.FpcModule.ServerOverridePosition(targetWorld);
            _lastPosition = targetWorld;
            return;
        }

        // 关键：如果编码后的 waypoint 属于移动物体（电梯/传送平台等），其 WorldspaceBounds
        // 内的任何坐标都会被绑定到该 waypoint（ElevatorWaypoint.SqrDistanceTo 返回 -1 永远胜出），
        // 电梯一动 bot 就被拖着跳变 100+ 米。此处事前绕过，直接用绝对坐标瞬移。
        if (relPos.WaypointId != 0 && WaypointBase.TryGetWaypoint<IMovableWaypoint>(relPos.WaypointId, out _))
        {
            fpc.FpcModule.ServerOverridePosition(targetWorld);
            _lastPosition = targetWorld;
            return;
        }

        fpc.FpcModule.Motor.ReceivedPosition = relPos;
    }

    /// <summary>
    /// 障碍绕行：优先朝「目标方向」小角度绕（±30°/±60°），避免绕行方向背离目标导致越绕越远；
    /// 目标方向全被挡时回退到当前方向 ±45°/±90°。
    /// 全部被挡返回 false（由调用方开门 / 卡住处理）。
    /// </summary>
    private static bool TrySteer(Vector3 eye, Vector3 forward, Vector3 targetDir, out Vector3 steerDir)
    {
        // 第一轮：朝目标方向绕（小角度优先，贴合目标方向）。
        float[] towardAngles = { 30f, -30f, 60f, -60f };
        foreach (float a in towardAngles)
        {
            Vector3 d = Quaternion.Euler(0f, a, 0f) * targetDir;
            if (!Physics.Raycast(eye, d, 2f, VisionInformation.VisionLayerMask))
            {
                steerDir = d;
                return true;
            }
        }

        // 第二轮：朝当前移动方向绕（原逻辑兜底）。
        float[] forwardAngles = { 45f, -45f, 90f, -90f, 135f, -135f };
        foreach (float a in forwardAngles)
        {
            Vector3 d = Quaternion.Euler(0f, a, 0f) * forward;
            if (!Physics.Raycast(eye, d, 2f, VisionInformation.VisionLayerMask))
            {
                steerDir = d;
                return true;
            }
        }

        steerDir = forward;
        return false;
    }

    /// <summary>
    /// 自疗尝试：血量告急且附近无敌人时，优先用背包医疗物品；背包没有则扫描当前房间
    /// 地上的医疗拾取物并走过去拾取；都没有则放弃（进入冷却）。
    /// 源码依据：LabApi UsableItem.Use() → ServerOnUsingCompleted() 立即生效（Medkit 回 65 HP）；
    /// Pickup.List 全量拾取物 + Room.GetRoomAtPosition 判断同房间 + Player.AddItem(pickup) 拾取。
    /// </summary>
    private void TryHeal(IFpcRole fpc, BotConfig config)
    {
        if (_player.Health <= 0f)
        {
            return;
        }

        float ratio = _player.Health / _player.MaxHealth;
        if (ratio >= config.HealThreshold)
        {
            return;
        }

        // 冷却中：不尝试。
        if (Time.timeSinceLevelLoad < _nextHealTick)
        {
            return;
        }

        // 附近无敌人判定：15m 内无敌对目标（复用敌人感知距离）。
        if (HasEnemyNearby(config.HealNoEnemyRange))
        {
            _nextHealTick = Time.timeSinceLevelLoad + 2f; // 有敌人在附近，稍后再试
            return;
        }

        try
        {
            // 1) 背包内找医疗物品（优先级 Medkit > Adrenaline > Painkillers）。
            ItemType? healItem = FindHealItemInInventory();
            if (healItem.HasValue)
            {
                UseHealItem(healItem.Value);
                return;
            }

            // 2) 背包没有 → 扫描当前房间地上医疗拾取物。
            if (TryPickupHealFromRoom(fpc, config))
            {
                return;
            }

            // 3) 都没有 → 放弃，进入冷却。
            _nextHealTick = Time.timeSinceLevelLoad + config.HealCooldown;
            Logger.Info($"[ScpBot] 机器人 #{Id} 血量告急但背包与当前房间均无医疗物品，放弃自疗（{config.HealCooldown}s 后再试）。");
        }
        catch (Exception ex)
        {
            _nextHealTick = Time.timeSinceLevelLoad + config.HealCooldown;
            Logger.Warn($"[ScpBot] 机器人 #{Id} 自疗异常: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>附近指定距离内是否有敌对目标。</summary>
    private bool HasEnemyNearby(float range)
    {
        Vector3 myPos = _hub.transform.position;
        float sqr = range * range;

        foreach (ReferenceHub h in new List<ReferenceHub>(ReferenceHub.AllHubs))
        {
            if (h == null || h == _hub || !h.IsAlive() || !HitboxIdentity.IsEnemy(_hub, h))
            {
                continue;
            }

            if ((h.transform.position - myPos).sqrMagnitude <= sqr)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>在背包中查找医疗物品（Medkit > Adrenaline > Painkillers），返回 ItemType。</summary>
    private ItemType? FindHealItemInInventory()
    {
        if (_player.Items.Any(i => i.Type == ItemType.Medkit))
        {
            return ItemType.Medkit;
        }

        if (_player.Items.Any(i => i.Type == ItemType.Adrenaline))
        {
            return ItemType.Adrenaline;
        }

        if (_player.Items.Any(i => i.Type == ItemType.Painkillers))
        {
            return ItemType.Painkillers;
        }

        return null;
    }

    /// <summary>选中并立即使用指定医疗物品。</summary>
    private void UseHealItem(ItemType type)
    {
        Item? item = _player.Items.FirstOrDefault(i => i.Type == type);
        if (item == null)
        {
            return;
        }

        // 切到该物品（服务器端选择）。
        _hub.inventory.ServerSelectItem(item.Serial);

        // 使用（ServerOnUsingCompleted 立即生效并消耗物品）。
        if (item is UsableItem usable)
        {
            usable.Use();
        }
    }

    /// <summary>
    /// 尝试从当前房间地上拾取医疗物品：找到最近的医疗拾取物并走向它，2m 内拾取并立即使用。
    /// 返回 true 表示有可用的医疗拾取物（本 tick 已走向它）。
    /// </summary>
    private bool TryPickupHealFromRoom(IFpcRole fpc, BotConfig config)
    {
        Room? room = GetCurrentRoom();
        if (room == null)
        {
            return false;
        }

        Vector3 myPos = fpc.FpcModule.Position;

        // 当前房间内的医疗拾取物（快照遍历，避免并发修改）。
        Pickup? best = null;
        float bestSqr = float.MaxValue;
        foreach (Pickup pickup in new List<Pickup>(Pickup.List))
        {
            if (pickup == null || pickup.IsDestroyed)
            {
                continue;
            }

            ItemType t = pickup.Type;
            if (t != ItemType.Medkit && t != ItemType.Adrenaline && t != ItemType.Painkillers)
            {
                continue;
            }

            // 必须在当前房间内。
            Room? pr = Room.GetRoomAtPosition(pickup.Transform.position);
            if (pr == null || pr.Name != room.Name)
            {
                continue;
            }

            float sqr = (pickup.Transform.position - myPos).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = pickup;
            }
        }

        if (best == null)
        {
            return false;
        }

        // 足够近：直接拾取并立即使用。
        if (bestSqr <= 2f * 2f)
        {
            Item? item = _player.AddItem(best);
            if (item != null)
            {
                _hub.inventory.ServerSelectItem(item.Serial);
                if (item is UsableItem usable)
                {
                    usable.Use();
                    Logger.Info($"[ScpBot] 机器人 #{Id} 从地上拾取并使用了 {item.Type}。");
                }
            }

            return true;
        }

        // 不够近：走过去。
        Move(fpc, best.Transform.position, config);
        return true;
    }

    /// <summary>
    /// 投掷决策与执行：目标 ≥2 个聚集且距离在 [ThrowMinDistance, ThrowMaxDistance] 区间、有视线时，
    /// 从背包手榴弹/闪光弹中选一枚（手榴弹优先），走服务器端投掷流程（Initiation → 等待动画 → Confirm）。
    /// 源码依据：ThrowableItem.ServerProcessInitiation / ServerProcessThrowConfirmation（反编译确认），
    /// 投掷会真实消耗背包物品；装备切换用 ReferenceHub.inventory.ServerSelectItem。
    /// </summary>
    private void TryThrow(IFpcRole fpc, BotConfig config)
    {
        // 冷却中。
        if (Time.timeSinceLevelLoad < _nextThrowTick)
        {
            return;
        }

        // 正在投掷动画中：推进完成。
        if (_throwPending)
        {
            if (Time.timeSinceLevelLoad >= _throwReadyTime)
            {
                ConfirmThrow();
            }

            return;
        }

        // 找聚集的敌人（目标自身 + 附近 6m 内其它敌人）。
        if (_target == null || !HasEnemyCluster(config))
        {
            return;
        }

        Vector3 myPos = fpc.FpcModule.Position;
        Vector3 bodyPos = GetAimPoint(_target, config.AimHeight);
        float dist = (bodyPos - myPos).magnitude;
        if (dist < config.ThrowMinDistance || dist > config.ThrowMaxDistance)
        {
            return;
        }

        // 选投掷物：手榴弹优先，其次闪光弹。
        ItemType? throwType = null;
        if (_player.Items.Any(i => i.Type == ItemType.GrenadeHE))
        {
            throwType = ItemType.GrenadeHE;
        }
        else if (_player.Items.Any(i => i.Type == ItemType.GrenadeFlash))
        {
            throwType = ItemType.GrenadeFlash;
        }

        if (!throwType.HasValue)
        {
            return;
        }

        // 对准目标群（用当前瞄准方向）。
        Face(fpc, bodyPos - _hub.PlayerCameraReference.position);

        // 切装备并开始投掷（服务器端流程）。
        Item? item = _player.Items.FirstOrDefault(i => i.Type == throwType.Value);
        if (item == null)
        {
            return;
        }

        _hub.inventory.ServerSelectItem(item.Serial);
        if (item is ThrowableItem throwable)
        {
            // 服务器端开始投掷计时（播放投掷动画）。Base 为底层 ThrowableItem。
            throwable.Base.ServerProcessInitiation();
            // FF-10：检查 Initiation 是否真正生效（AllowHolster && !HasBlock），否则静默 no-op。
            if (throwable.Base.ThrowStopwatch.IsRunning)
            {
                _throwPending = true;
                _throwPendingStart = Time.timeSinceLevelLoad;
                // FF-10：ReadyToThrow 阈值 = 0.8f * ThrowingAnimTime（服务端 CurrentTimeTolerance）。
                _throwReadyTime = Time.timeSinceLevelLoad + 0.8f * throwable.Base.ThrowingAnimTime;
                _throwPendingType = throwType.Value;
                Logger.Info($"[ScpBot] 机器人 #{Id} 开始投掷 {throwType.Value}。");
            }
            else
            {
                Logger.Warn($"[ScpBot] 机器人 #{Id} 投掷启动失败（ServerProcessInitiation no-op）。");
            }
        }
    }

    /// <summary>确认投掷（动画时间到后调用），消耗物品并进入冷却。</summary>
    private void ConfirmThrow()
    {
        _throwPending = false;

        try
        {
            Item? item = _player.Items.FirstOrDefault(i => i.Type == _throwPendingType);
            if (item is ThrowableItem throwable)
            {
                Vector3 camPos = _hub.PlayerCameraReference.position;
                Quaternion camRot = _hub.PlayerCameraReference.rotation;
                // 全力度投掷：方向 = 相机朝向，初速 0（静止投掷）。
                throwable.Base.ServerProcessThrowConfirmation(true, camPos, camRot, Vector3.zero);
                Logger.Info($"[ScpBot] 机器人 #{Id} 投掷完成（{_throwPendingType}）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ScpBot] 机器人 #{Id} 投掷确认异常: {ex.GetBaseException().Message}");
        }

        // FF-10：?? 优先级低于 +，必须加括号；否则 BotPlugin.Instance 为 null 时结果是 12f 而非 timeSinceLevelLoad + 12f。
        _nextThrowTick = Time.timeSinceLevelLoad + (BotPlugin.Instance?.Config.ThrowCooldown ?? 12f);
    }

    /// <summary>目标附近（6m）是否有 ≥ThrowMinEnemies 个敌对目标（聚集判定）。</summary>
    private bool HasEnemyCluster(BotConfig config)
    {
        if (_target == null)
        {
            return false;
        }

        Vector3 targetPos = _target.transform.position;
        int count = 0;
        foreach (ReferenceHub h in new List<ReferenceHub>(ReferenceHub.AllHubs))
        {
            if (h == null || !h.IsAlive() || !HitboxIdentity.IsEnemy(_hub, h))
            {
                continue;
            }

            if ((h.transform.position - targetPos).sqrMagnitude <= 6f * 6f)
            {
                count++;
            }
        }

        return count >= config.ThrowMinEnemies;
    }

    /// <summary>
    /// 路径规划式开门：前方暂时畅通时，检查 10m 内前进方向锥体里的关闭门并提前打开。
    /// 开门后记录等待状态（等门板移开），由 <see cref="IsWaitingForDoor"/> 控制原地等待。
    /// </summary>
    private void TryPreOpenDoor(IFpcRole fpc, Vector3 myPos, Vector3 dir, BotConfig config)
    {
        if (!config.OpenDoors)
        {
            return;
        }

        try
        {
            Door? door = FindClosestDoor(myPos, dir, 10f, 0.64f, out _);
            if (door == null)
            {
                return;
            }

            // 门已开/正在开就不用重复。
            if (door.IsOpened)
            {
                return;
            }

            door.IsOpened = true;
            _waitDoor = door;
            _waitDoorStart = Time.timeSinceLevelLoad;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ScpBot] 机器人提前开门异常: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>是否正在等待门打开（开门后 0.8s 内原地等待，等门板移开再继续走）。</summary>
    private bool IsWaitingForDoor()
    {
        if (_waitDoor == null)
        {
            return false;
        }

        // 门已完全打开（或已等够时间）：结束等待。
        if (Time.timeSinceLevelLoad - _waitDoorStart >= 0.8f || _waitDoor.IsOpened)
        {
            _waitDoor = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 尝试打开前进方向上的最近关闭门（服务器端直接开门，无视钥匙卡权限；锁住的门跳过）。
    /// 先找前进方向锥体内的门（快速命中）；找不到时回退到全方位最近的关闭门（覆盖门在侧面/身后的情况）。
    /// 成功后记录等待状态。源码依据：LabApi Door.IsOpened setter → DoorVariant.NetworkTargetState。
    /// </summary>
    private bool TryOpenDoor(IFpcRole fpc, Vector3 myPos, Vector3 dir, BotConfig config)
    {
        if (!config.OpenDoors)
        {
            return false;
        }

        try
        {
            // 第一遍：前进方向 6m 内、与朝向夹角 &lt; 50° 的最近关闭门。
            Door? best = FindClosestDoor(myPos, dir, 6f, 0.64f, out float bestSqr);

            // 第二遍（回退）：全方位 8m 内最近的关闭门（不管方向）。
            if (best == null)
            {
                best = FindClosestDoor(myPos, null, 8f, -1f, out bestSqr);
            }

            if (best == null)
            {
                return false;
            }

            // 服务器端直接开门（绕过权限；锁门已在上面跳过）。
            best.IsOpened = true;
            _waitDoor = best;
            _waitDoorStart = Time.timeSinceLevelLoad;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ScpBot] 机器人开门失败: {ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>
    /// 在指定距离内找最近的关闭未锁门。
    /// dir 为 null 时全方向；否则要求门在 dir 锥体内（dotMin 为 cos 夹角下限，&lt;0 表示不限方向）。
    /// </summary>
    private static Door? FindClosestDoor(Vector3 myPos, Vector3? dir, float maxDistance, float dotMin, out float bestSqr)
    {
        bestSqr = maxDistance * maxDistance;
        Door? best = null;

        foreach (Door door in Door.List)
        {
            if (door == null || door.IsDestroyed || door.IsOpened || door.IsLocked)
            {
                continue;
            }

            Vector3 toDoor = door.Position - myPos;
            toDoor.y = 0f;

            float sqr = toDoor.sqrMagnitude;
            if (sqr > bestSqr)
            {
                continue;
            }

            // 方向约束：dir 非空时门必须在前进方向锥体内。
            if (dir.HasValue && dotMin >= 0f && sqr > 0.001f)
            {
                float dot = Vector3.Dot(toDoor.normalized, dir.Value);
                if (dot < dotMin)
                {
                    continue;
                }
            }

            best = door;
            bestSqr = sqr;
        }

        return best;
    }

    /// <summary>
    /// 目标隔墙不可见时的主动找门绕行：
    /// 1) 找朝向目标方向附近（12m 内）最近的关闭门；
    /// 2) 走向门（到门前 1.5m 停下）；
    /// 3) 到门口后开门，然后朝目标方向继续走。
    /// 返回 true 表示本 tick 正在绕门行动（调用方不再直线顶墙）。
    /// </summary>
    private bool TryApproachDoor(IFpcRole fpc, BotConfig config)
    {
        if (_target == null || _hub == null)
        {
            return false;
        }

        try
        {
            Vector3 myPos = fpc.FpcModule.Position;
            Vector3 toTarget = _target.transform.position - myPos;
            toTarget.y = 0f;

            if (toTarget.sqrMagnitude < 0.01f)
            {
                return false;
            }

            Vector3 targetDir = toTarget.normalized;

            // 找朝向目标方向附近最近的关闭门（12m 内，允许 ±70° 偏角）。
            Door? door = FindClosestDoor(myPos, targetDir, 12f, 0.34f, out float doorSqr);

            // 目标方向 12m 内没有门（可能是大房间隔墙，门在侧面更远）：回退到全方位 15m 最近门。
            if (door == null)
            {
                door = FindClosestDoor(myPos, null, 15f, -1f, out doorSqr);
            }

            if (door == null)
            {
                return false;
            }

            Vector3 doorPos = door.Position;
            Vector3 toDoor = doorPos - myPos;
            toDoor.y = 0f;
            float doorDist = toDoor.magnitude;

            // 还没到门口：朝门走（门是绕行通道，不是终点，走到门前 1.5m）。
            if (doorDist > 1.5f)
            {
                Vector3 approachTarget = doorPos - (toDoor.normalized * 1.2f);
                Face(fpc, toDoor);
                Move(fpc, approachTarget, config);
                // FF-36/FF-37：朝门行走期间 bot 移动缓慢且方向刻意偏离直线追击，
                // UpdateStuck 会误判为「卡死」→ 触发跳跃/瞬移 → bot 飞出门口。
                // 主动清零卡死计时，让朝门过程不被卡死逻辑打断。
                _stuckTime = 0f;
                _stuckJumpTick = 0f;
                _stuckRaycastTick = 0f;
                _stuckRaycasting = false;
                return true;
            }

            // 已到门口：开门并原地等待门板移开（Move 的 IsWaitingForDoor 会拦住移动），
            // 门开后下个 tick 继续朝目标方向走。
            if (!door.IsOpened)
            {
                door.IsOpened = true;
                _waitDoor = door;
                _waitDoorStart = Time.timeSinceLevelLoad;
            }

            StopMove(fpc);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ScpBot] 机器人找门绕行异常: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private void StopMove(IFpcRole fpc)
    {
        // 把目标位置设为当前位置，Dummy 即停住。
        Vector3 pos = fpc.FpcModule.Position;
        RelativePosition relPos = new(pos);

        // 若当前编码结果绑定移动 waypoint（电梯井范围），改用绝对坐标原地停住，
        // 否则电梯移动时 ReceivedPosition 解码位置会跳变，把 bot 拖走。
        if (relPos.OutOfRange
            || (relPos.WaypointId != 0 && WaypointBase.TryGetWaypoint<IMovableWaypoint>(relPos.WaypointId, out _)))
        {
            fpc.FpcModule.ServerOverridePosition(pos);
            return;
        }

        fpc.FpcModule.Motor.ReceivedPosition = relPos;
    }

    /// <summary>
    /// 无限弹药：弹匣/膛内弹药不足时直接补满，跳过换弹动画；备用弹药也锁满。
    /// </summary>
    private void RefillAmmo(BotConfig config)
    {
        Item? currentItem = _player.CurrentItem;
        if (!(currentItem is FirearmItem firearm))
        {
            return;
        }

        Firearm baseFirearm = firearm.Base;

        // 弹匣类武器：弹匣不满则直接补满。
        if (baseFirearm.TryGetModule<MagazineModule>(out MagazineModule? magazine)
            && magazine.AmmoStored < magazine.AmmoMax)
        {
            magazine.ServerModifyAmmo(magazine.AmmoMax - magazine.AmmoStored);
        }

        // 自动武器（闭锁）：膛内压一发、上膛。
        if (baseFirearm.TryGetModule<AutomaticActionModule>(out AutomaticActionModule? action))
        {
            if (action.AmmoStored < 1)
            {
                action.AmmoStored = 1;
                action.Cocked = true;
                action.ServerResync();
            }
        }

        // 备用弹药锁满（无限补弹）。
        if (firearm.AmmoType != ItemType.None)
        {
            ushort cur = _player.GetAmmo(firearm.AmmoType);
            ushort want = config.ReserveAmmo > 0 ? config.ReserveAmmo : (ushort)200;
            if (cur < want)
            {
                _player.AddAmmo(firearm.AmmoType, (ushort)(want - cur));
            }
        }
    }

    /// <summary>
    /// 备用弹药自动补给：只补备用弹药（不碰弹匣），弹匣打空后仍需正常换弹。
    /// 保证火力压制等长时间射击战术不会因弹尽停火。
    /// </summary>
    private void RefillReserveAmmo(BotConfig config)
    {
        if (!config.AutoRefillReserveAmmo)
        {
            return;
        }

        try
        {
            Item? currentItem = _player.CurrentItem;
            if (!(currentItem is FirearmItem firearm) || firearm.AmmoType == ItemType.None)
            {
                return;
            }

            ushort cur = _player.GetAmmo(firearm.AmmoType);
            ushort want = config.ReserveAmmo > 0 ? config.ReserveAmmo : (ushort)200;
            if (cur < want)
            {
                _player.AddAmmo(firearm.AmmoType, (ushort)(want - cur));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ScpBot] 机器人 #{Id} 备用弹药补给失败: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// 检测当前武器弹匣是否已空且还有备用弹药可换。
    /// </summary>
    private bool NeedsReload(BotConfig config)
    {
        if (_isReloading)
        {
            return true;
        }

        Item? currentItem = _player.CurrentItem;
        if (!(currentItem is FirearmItem firearm))
        {
            return false;
        }

        Firearm baseFirearm = firearm.Base;

        // 弹匣类武器：弹匣空 + 有备用弹药 → 需要换弹。
        if (baseFirearm.TryGetModule<MagazineModule>(out MagazineModule? magazine))
        {
            if (magazine.AmmoStored > 0)
            {
                return false;
            }

            return firearm.AmmoType != ItemType.None && _player.GetAmmo(firearm.AmmoType) > 0;
        }

        // 非弹匣类武器（如狙击枪单发装填）：膛内无弹药时视为需换弹。
        if (baseFirearm.TryGetModule<AutomaticActionModule>(out AutomaticActionModule? action))
        {
            return action.AmmoStored <= 0 && firearm.AmmoType != ItemType.None && _player.GetAmmo(firearm.AmmoType) > 0;
        }

        return false;
    }

    /// <summary>
    /// 触发换弹：通过 DummyAction 按「Hold → 下一 tick Release」两阶段序列触发换弹。
    /// FF-07：DummyKeyEmulator 的动作列表是状态相关的——Reload 键未按下时列表里只有
    /// "Reload->Hold"（和 "Reload->Click"），"Reload->Release" 只在已按住后才出现。
    /// 旧实现每次只 Invoke 得到的 holdAction（releaseAction 恒 null），Reload 键被持续按住、
    /// 永不松开，触发的是「按住 ≥1s = 退弹」而非换弹。改为状态机：先 Hold，下一 tick 拿到
    /// Release 后松开（按住 <1s = ClientTryReload 换弹）。
    /// </summary>
    private void TryStartReload(BotConfig config)
    {
        if (!_hub.IsDummy)
        {
            return;
        }

        Action? holdAction = null;
        Action? releaseAction = null;

        foreach (DummyAction action in DummyActionCollector.ServerGetActions(_hub))
        {
            if (action.Action == null)
            {
                continue;
            }

            switch (action.Name)
            {
                case "Reload->Hold":
                    holdAction = action.Action;
                    break;
                case "Reload->Release":
                    releaseAction = action.Action;
                    break;
            }
        }

        if (!_reloadKeyHeld && !_reloadTriggered)
        {
            // 阶段 1：按下 Reload（下一 tick 动作列表才会出现 Release）。
            holdAction?.Invoke();
            _reloadKeyHeld = true;
        }
        else if (_reloadKeyHeld)
        {
            // 阶段 2：已按住 → 松开触发换弹（按住 <1s，走 ClientTryReload 而非退弹）。
            releaseAction?.Invoke();
            _reloadKeyHeld = false;
            _reloadTriggered = true;
        }
        // 已触发：等待换弹动画完成（_reloadWaitTime 累计），期间不再按键。

        _isReloading = true;
    }

    /// <summary>
    /// 设置开火状态。每 tick 自校正：检查当前是否已按住扳机，再决定调用 Hold / Release。
    /// 这是驱动 Dummy 武器开火的官方途径（等价于 RA 的 dummies action 命令）。
    /// </summary>
    private void SetShoot(bool shoot)
    {
        try
        {
            if (_hub == null || !_hub.IsDummy)
            {
                return;
            }

            bool held = false;
            Action? holdAction = null;
            Action? releaseAction = null;

            foreach (DummyAction action in DummyActionCollector.ServerGetActions(_hub))
            {
                if (action.Action == null)
                {
                    continue;
                }

                switch (action.Name)
                {
                    case "Shoot->Hold":
                        holdAction = action.Action;
                        break;
                    case "Shoot->Release":
                        releaseAction = action.Action;
                        held = true;
                        break;
                }
            }

            if (shoot && !held)
            {
                holdAction?.Invoke();
            }
            else if (!shoot && held)
            {
                releaseAction?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[ScpBot] 机器人 #{Id} 设置开火状态失败: {ex}");
        }
    }

    /// <summary>
    /// 检测并修正位置异常跳变。当服务器端实际位置与上一帧位置差值超过阈值时，
    /// 视为 RelativePosition 引用了移动 waypoint 导致的漂移，用 ServerOverridePosition 拉回。
    /// </summary>
    private void CheckPositionDrift(IFpcRole fpc, BotConfig config)
    {
        Vector3 actual = fpc.FpcModule.Position;
        if (_lastActualPosition == Vector3.zero)
        {
            _lastActualPosition = actual;
            return;
        }

        float drift = (actual - _lastActualPosition).magnitude;
        // 正常单帧最大移动量：MoveSpeed * TickInterval，加 1m 缓冲。
        float maxNormal = config.MoveSpeed * config.TickInterval + 1f;

        if (drift > maxNormal && drift > 3f)
        {
            // 优先传送到当前房间中心（实心地面），避免拉回电梯原来的空中位置导致坠落摔死。
            Room? room = GetCurrentRoom();
            Vector3 safePos = room != null
                ? room.Position + new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), 0f, UnityEngine.Random.Range(-1.5f, 1.5f))
                : _lastActualPosition;

            Logger.Warn(
                $"[ScpBot] 机器人 #{Id} 位置异常跳变 {drift:F1}m " +
                $"（{RoomWaypoints.Format(_lastActualPosition)} → {RoomWaypoints.Format(actual)}），" +
                $"已传送到当前房间中心并切瞬移模式 3s。累计修正 {_driftCorrectionCount + 1} 次。");

            fpc.FpcModule.ServerOverridePosition(safePos);
            _lastPosition = safePos;
            _lastActualPosition = safePos;
            _driftCorrectionCount++;
            // 切到瞬移模式，绕过 RelativePosition / waypoint 系统 60 帧（约 3s），
            // 给电梯/传送平台足够时间停止移动。
            _serverOverrideFrames = 60;
            ResetPath();
            return;
        }

        _lastActualPosition = actual;

        // 每 tick 无漂移，减少冷却计数；冷却结束后恢复正常的 RelativePosition 移动。
        if (_serverOverrideFrames > 0)
        {
            _serverOverrideFrames--;
        }
    }

    /// <summary>
    /// 卡死检测与三级脱离：
    /// 1) 卡住 StuckJumpAfter 秒后：跳跃 + 随机转向（尝试脱离障碍）。
    /// 2) 再坚持 StuckRaycastAfter 秒仍无效：光线检查（多方向 Raycast），朝最近的畅通方向走。
    /// 3) 仍不行且 StuckTeleportEnabled：瞬移到目标附近兜底。
    /// 源码依据：FpcJumpController.ForceJump（服务器端跳跃入口，Dummy 官方动作）；
    /// 光线检查复用 VisionInformation.VisionLayerMask 做 Physics.Raycast 采样。
    /// </summary>
    private void UpdateStuck(IFpcRole fpc, BotConfig config)
    {
        Vector3 pos = fpc.FpcModule.Position;

        if ((pos - _lastPosition).sqrMagnitude < 0.001f)
        {
            _stuckTime += config.TickInterval;
        }
        else
        {
            _stuckTime = 0f;
            _stuckJumpTick = 0f;
            _stuckRaycastTick = 0f;
            _stuckRaycasting = false;
        }

        _lastPosition = pos;

        if (_stuckTime < config.StuckJumpAfter)
        {
            return;
        }

        // 阶段 1：跳跃 + 随机转向（每 0.4s 一次，最多连续 3 次）。
        if (!_stuckRaycasting && _stuckTime < config.StuckRaycastAfter)
        {
            if (_stuckJumpTick == 0f)
            {
                _stuckJumpTick = Time.timeSinceLevelLoad;
                _stuckJumpCount = 0;
            }

            // 每 0.4s 尝试一次跳跃 + 转向。
            if (Time.timeSinceLevelLoad - _stuckJumpTick >= 0.4f && _stuckJumpCount < 3)
            {
                _stuckJumpTick = Time.timeSinceLevelLoad;
                _stuckJumpCount++;

                // 跳跃（服务器端入口）。
                try
                {
                    FpcMotor motor = fpc.FpcModule.Motor;
                    motor.JumpController.ForceJump(motor.MainModule.JumpSpeed);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[ScpBot] 机器人 #{Id} 跳跃脱离失败: {ex.GetBaseException().Message}");
                }

                // 随机转向 60~120°（水平），尝试换个方向走出障碍。
                float angle = UnityEngine.Random.Range(60f, 120f) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
                // FF-33：此前 `CurrentHorizontal > 0f ? Euler(...) : Vector3.forward` —— 当水平角为负
                // （已转向左侧）时错误回退到默认 +Z 方向，跳跃脱离的转向基准错误，bot 会转回错误方向。
                // CurrentHorizontal 本身是绕 Y 轴角度（-180~180），无论正负都应直接转成方向向量。
                Vector3 curForward = Quaternion.Euler(0f, fpc.FpcModule.MouseLook.CurrentHorizontal, 0f) * Vector3.forward;
                Vector3 newForward = Quaternion.Euler(0f, angle, 0f) * curForward;
                Face(fpc, newForward);
            }

            return;
        }

        // 阶段 2：光线检查——找最近的畅通方向并移动。
        if (!_stuckRaycasting && _stuckTime >= config.StuckRaycastAfter)
        {
            _stuckRaycasting = true;
            _stuckRaycastTick = Time.timeSinceLevelLoad;
        }

        if (_stuckRaycasting)
        {
            Vector3 eye = pos + (Vector3.up * EyeHeight);

            // 8 方向采样，找最近的畅通方向（无遮挡）。
            Vector3 bestDir = Vector3.zero;
            float bestAngle = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f;
                Vector3 dir = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
                if (!Physics.Raycast(eye, dir, config.ObstacleLookAhead + 2f, VisionInformation.VisionLayerMask))
                {
                    // 选与当前朝向夹角最小的畅通方向（优先往前/侧）。
                    float curAngle = Vector3.Angle(dir, Vector3.forward);
                    if (curAngle < bestAngle)
                    {
                        bestAngle = curAngle;
                        bestDir = dir;
                    }
                }
            }

            if (bestDir != Vector3.zero)
            {
                Face(fpc, bestDir);
                Move(fpc, pos + bestDir * 4f, config);

                // 光线检查阶段有进展就退出阶段。
                if (_stuckRaycastTick > 0f && Time.timeSinceLevelLoad - _stuckRaycastTick >= 2f)
                {
                    _stuckRaycasting = false;
                    _stuckRaycastTick = 0f;
                    _stuckTime = 0f;
                }

                return;
            }

            // 全部方向都被挡：进入阶段 3（瞬移兜底）。
            _stuckRaycasting = false;
        }

        // 阶段 3：瞬移兜底（可配置关闭）。
        if (config.StuckTeleportEnabled && _stuckTime >= config.StuckTeleportAfter)
        {
            Vector3 dest;

            // 优先瞬移到目标所在房间中心（保证落点是合法地面），否则瞬移到目标身边。
            Room? targetRoom = GetTargetRoom();
            if (targetRoom != null)
            {
                dest = targetRoom.Position
                    + new Vector3(UnityEngine.Random.Range(-2f, 2f), 0f, UnityEngine.Random.Range(-2f, 2f));
            }
            else
            {
                Vector3 targetPos = _target != null ? GetAimPoint(_target, config.AimHeight) : fpc.FpcModule.Position;
                dest = targetPos
                    + new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), 0f, UnityEngine.Random.Range(-1.5f, 1.5f));
            }

            fpc.FpcModule.ServerOverridePosition(dest);

            // 传送后当前位置已改变，房间路径作废，下个 tick 重新计算。
            // 同步重置漂移追踪，否则下次 CheckPositionDrift 会把传送视为异常跳变。
            // 传送到了新位置（远离电梯），恢复正常的 RelativePosition 移动。
            ResetPath();
            _stuckTime = 0f;
            _stuckJumpTick = 0f;
            _stuckRaycastTick = 0f;
            _stuckRaycasting = false;
            _lastActualPosition = dest;
            _serverOverrideFrames = 0;
        }
    }
}

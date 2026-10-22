using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using AssettoServer.Server.Ai.Splines;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using AssettoServer.Utils;
using JPBotelho;
using Serilog;
using SunCalcNet.Model;

namespace AssettoServer.Server.Ai;

public class AiState
{
    public CarStatus Status { get; } = new();
    public bool Initialized { get; private set; }

    public int CurrentSplinePointId
    {
        get => _currentSplinePointId;
        private set
        {
            _spline.SlowestAiStates.Enter(value, this);
            _spline.SlowestAiStates.Leave(_currentSplinePointId, this);
            _currentSplinePointId = value;
        }
    }

    private int _currentSplinePointId;

    /// <summary>
    /// Lane changes: point of the lane the car is leaving while its body is still in it, -1 otherwise.
    /// Registered in SlowestAiStates, so cars behind in the old lane still see it.
    /// </summary>
    public int ShadowSplinePointId
    {
        get => _shadowSplinePointId;
        private set
        {
            if (value == _shadowSplinePointId) return;
            if (value >= 0) _spline.SlowestAiStates.Enter(value, this);
            _spline.SlowestAiStates.Leave(_shadowSplinePointId, this);
            _shadowSplinePointId = value;
        }
    }

    private int _shadowSplinePointId = -1;

    public long SpawnProtectionEnds { get; set; }
    public float SafetyDistanceSquared { get; set; } = 20 * 20;
    public float Acceleration { get; set; }
    public float CurrentSpeed { get; private set; }
    public float TargetSpeed { get; private set; }
    public float InitialMaxSpeed { get; private set; }
    public float MaxSpeed { get; private set; }
    public Color Color { get; private set; }
    public byte SpawnCounter { get; private set; }
    public float ClosestAiObstacleDistance { get; private set; }
    public EntryCar EntryCar { get; }

    // Trace data, see AiTrace. Only written, never read by the AI logic.
    public int TraceId { get; } = AiTrace.NextStateId();
    public AiTraceReason TraceReason { get; private set; }
    public int TraceLeaderId { get; private set; } = -1;
    public float TracePlayerDistance { get; private set; } = -1;
    /// <summary>Lateral offset from the lane (m), non-zero only during a lane change</summary>
    public float TraceLateralOffset => _lateralOffset;
    /// <summary>0 = no lane change, 1 = indicating, 2 = moving; negative = to the left</summary>
    public int TraceLaneChange => _lcPhase == LaneChangePhase.None ? 0 : (_lcPhase == LaneChangePhase.Indicating ? 1 : 2) * _lcDirection;
    /// <summary>Merges: 0 = no zone ahead, 1 = approaching a zone with priority or a diverge, 2 = merging in front, 3 = giving way, 4 = queued behind a car before the zone</summary>
    public int TraceMerge => (int)_mergeState;

    private const float WalkingSpeed = 10 / 3.6f;
    // Acc: below this speed a braking car is considered stopped
    private const float AccStandstillSpeed = 0.2f;
    // Acc: a stopped car starts moving only if it wants to accelerate at least this much
    private const float AccStandstillStartAcceleration = 0.2f;
    // Acc: a leader counts as the reason for the current acceleration if it lowers it at least by this much
    private const float AccFollowThreshold = 0.1f;

    private Vector3 _startTangent;
    private Vector3 _endTangent;

    private float _currentVecLength;
    private float _currentVecProgress;
    private long _lastTick;
    private bool _stoppedForObstacle;
    private long _stoppedForObstacleSince;
    private long _ignoreObstaclesUntil;
    private long _stoppedForCollisionUntil;
    private long _obstacleHonkStart;
    private long _obstacleHonkEnd;
    private CarStatusFlags _indicator = 0;
    private int _nextJunctionId;
    private bool _junctionPassed;
    private float _endIndicatorDistance;
    private float _minObstacleDistance;
    private double _randomTwilight;

    // Acc following model: driver parameters rolled on spawn, see AccParams
    private float _accTimeHeadway;
    private float _accStandstillGap;
    private float _accMaxAcceleration;
    private float _accComfortableDeceleration;
    // Slower than the lane, doesn't move over for faster cars (AccParams.SlowDriverShare)
    private bool _accSlowDriver;
    // Acceleration was set by the Acc model, Update() integrates it without clamping to TargetSpeed
    private bool _accIntegrate;
    private bool _accBrakeLightsOn;
    // AiParams.Visuals: wheels, gearbox, rpm, throttle
    private readonly AiVisuals _visuals = new();

    // Lane changes, see LaneChangeParams. Driver parameters rolled on spawn
    private enum LaneChangePhase { None, Indicating, Moving }
    // A lane change doesn't start below this speed
    private const float LaneChangeMinSpeed = 2.0f;
    // Players behind: reference driver for the safety check, players don't have Acc parameters
    private const float PlayerModelAcceleration = 2.5f;
    private const float PlayerModelDeceleration = 2.0f;
    private const float PlayerModelTimeHeadway = 1.0f;
    private const float PlayerModelStandstillGap = 2.0f;
    private const float PlayerModelMaxDeceleration = 9.0f;
    private float _lcPoliteness;
    private bool _lcAggressive;
    private float _lcDurationSeconds;
    private float _lcIndicatorSeconds;
    private LaneChangePhase _lcPhase;
    private int _lcDirection; // -1 = left (LeftId), 1 = right (RightId)
    private long _lcNextDecision;
    private long _lcCooldownUntil;
    private long _lcMoveAt;
    private long _lcStartedAt;
    private float _lcStartOffset;
    private float _lcLaneDistance;
    private float _lcLength;
    private float _lcTravelled;
    private bool _lcAborted;
    private float _lateralOffset;
    private float _lateralSlope;
    private float _lateralCurvature;
    // Return after an abort: quintic y(s) = y0 + y1·s + y2·s²/2 + c3·s³ + c4·s⁴ + c5·s⁵ from the lateral position, speed
    // and acceleration at the abort to 0, so the car doesn't jerk back
    private float _lcReturnY0, _lcReturnY1, _lcReturnY2, _lcReturnC3, _lcReturnC4, _lcReturnC5;
    // Update() runs on the server tick, lane switches happen in obstacle detection
    private readonly object _moveLock = new();

    // Merges, see AiMerges and MergeParams. The values are the TraceMerge codes
    private enum MergeState { None = 0, Approach = 1, Go = 2, Yield = 3, Queue = 4 }
    private MergeState _mergeState;
    private int _mergeSegment = -1;
    private AiState? _mergeFollowerAi;
    private EntryCar? _mergeFollowerPlayer;
    private long _mergeWaitStart;

    // Incidents, see IncidentParams
    // A car slower than this is standing: a car waiting behind an obstacle passes it on to the cars behind
    private const float StandingSpeed = 1.0f;
    private const float GoAroundClearMarginMeters = 1.0f;
    private const float GoAroundLateralMarginMeters = 0.2f;
    private long _hazardsUntil;
    private long _touchBrakeUntil;
    // Obstacle to go around in the own lane (AiState or EntryCar of a player), null if none
    private object? _obstacleAhead;
    private float _obstacleGap;
    private long _blockedSince;
    // Obstacle this lane change goes around: the car doesn't follow it in the old lane once it will be clear of it in time
    private object? _lcGoAroundObstacle;
    // Crashed in the middle of a lane change: stands across the lanes until one of them is safe to move on, since when (0 = no)
    private long _lcCrashHoldSince;
    // Zipper: look this far ahead for cars waiting to go around into the own lane, and stop this much further behind them
    // than in a jam, so that their lane change check sees a long enough gap
    private const float GoAroundYieldLookaheadMeters = 80.0f;
    private const float GoAroundYieldExtraGapMeters = 2.0f;
    // Car waiting to go around that this car lets in, kept until it starts its lane change or gives up
    private AiState? _yieldingTo;

    private readonly ACServerConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly WeatherManager _weatherManager;
    private readonly AiSpline _spline;
    private readonly JunctionEvaluator _junctionEvaluator;
    private readonly AiTrace _trace;
    private readonly AiPlayerLanes _playerLanes;
    private readonly AiMerges _merges;

    private static readonly List<Color> CarColors =
    [
        Color.FromArgb(13, 17, 22),
        Color.FromArgb(19, 24, 31),
        Color.FromArgb(28, 29, 33),
        Color.FromArgb(12, 13, 24),
        Color.FromArgb(11, 20, 33),
        Color.FromArgb(151, 154, 151),
        Color.FromArgb(153, 157, 160),
        Color.FromArgb(194, 196, 198),
        Color.FromArgb(234, 234, 234),
        Color.FromArgb(255, 255, 255),
        Color.FromArgb(182, 17, 27),
        Color.FromArgb(218, 25, 24),
        Color.FromArgb(73, 17, 29),
        Color.FromArgb(35, 49, 85),
        Color.FromArgb(28, 53, 81),
        Color.FromArgb(37, 58, 167),
        Color.FromArgb(21, 92, 45),
        Color.FromArgb(18, 46, 43)
    ];

    public AiState(EntryCar entryCar, SessionManager sessionManager, WeatherManager weatherManager, ACServerConfiguration configuration, EntryCarManager entryCarManager, AiSpline spline, AiTrace trace, AiPlayerLanes playerLanes, AiMerges merges)
    {
        EntryCar = entryCar;
        _sessionManager = sessionManager;
        _weatherManager = weatherManager;
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _spline = spline;
        _trace = trace;
        _playerLanes = playerLanes;
        _merges = merges;
        _junctionEvaluator = new JunctionEvaluator(spline);

        _lastTick = _sessionManager.ServerTimeMilliseconds;
    }

    public void Despawn() => Despawn("other");

    public void Despawn(string reason)
    {
        if (Initialized && _trace.Enabled)
        {
            _trace.Event("despawn", this, $"\"why\":\"{reason}\",\"pt\":{CurrentSplinePointId},\"v\":{AiTrace.F(CurrentSpeed)}");
        }

        Initialized = false;
        lock (_moveLock)
        {
            ResetLaneChange();
        }
        ResetMerge();
        _spline.SlowestAiStates.Leave(CurrentSplinePointId, this);
    }

    private void ResetMerge()
    {
        _mergeState = MergeState.None;
        _mergeSegment = -1;
        _mergeFollowerAi = null;
        _mergeFollowerPlayer = null;
        _mergeWaitStart = 0;
    }

    private void ResetLaneChange()
    {
        _lcPhase = LaneChangePhase.None;
        _lcAborted = false;
        _lateralOffset = 0;
        _lateralSlope = 0;
        _lateralCurvature = 0;
        ShadowSplinePointId = -1;
        _lcGoAroundObstacle = null;
        _lcCrashHoldSince = 0;
    }

    /// <summary>
    /// Incidents: -1/1 while the car stands (or crawls) behind an obstacle with the indicator on, waiting for a gap to go around
    /// into the left/right lane. Cars coming in that lane let it in (zipper). 0 otherwise.
    /// </summary>
    public int GoAroundRequestDirection => _obstacleAhead != null && _lcPhase == LaneChangePhase.Indicating && CurrentSpeed < GoAroundRequestMaxSpeed
        ? _lcDirection
        : 0;

    private const float GoAroundRequestMaxSpeed = 5.0f;

    private bool IncidentsEnabled => AccEnabled && _configuration.Extra.AiParams.Incidents.Enabled;
    private bool GoAroundEnabled => IncidentsEnabled && _configuration.Extra.AiParams.Incidents.GoAround && _configuration.Extra.AiParams.LaneChanges.Enabled;

    /// <summary>Incidents: stopping or standing after a crash, also waiting across two lanes after a crash during a lane change</summary>
    public bool IsCrashed => IncidentsEnabled && (_sessionManager.ServerTimeMilliseconds < _stoppedForCollisionUntil || _lcCrashHoldSince > 0);

    /// <summary>Acc: rear length of this car, for placing a car right behind it (AiBehavior)</summary>
    public float AccRearLength => LengthPost(EntryCar);

    /// <summary>Acc: comfortable deceleration of this driver (m/s^2)</summary>
    public float AccComfortableDeceleration => _accComfortableDeceleration;

    /// <summary>Incidents: an obstacle to go around is ahead in the own lane</summary>
    public bool IsBlockedByObstacle { get; private set; }

    private bool AccEnabled => _configuration.Extra.AiParams.FollowingModel == AiFollowingModel.Acc;

    private void SetRandomSpeed()
    {
        var aiParams = _configuration.Extra.AiParams;
        bool acc = AccEnabled;
        float variation = aiParams.MaxSpeedMs * (acc ? aiParams.Acc.DesiredSpeedVariationPercent : aiParams.MaxSpeedVariationPercent);

        float fastLaneOffset = LaneSpeedOffset(CurrentSplinePointId);
        InitialMaxSpeed = _configuration.Extra.AiParams.MaxSpeedMs + fastLaneOffset - (variation / 2) + (float)Random.Shared.NextDouble() * variation;
        _accSlowDriver = acc && Random.Shared.NextSingle() < aiParams.Acc.SlowDriverShare;
        if (_accSlowDriver)
        {
            InitialMaxSpeed *= 1 - RandomRange(aiParams.Acc.SlowDriverMinSlowdown, aiParams.Acc.SlowDriverMaxSlowdown);
        }
        CurrentSpeed = InitialMaxSpeed;
        TargetSpeed = InitialMaxSpeed;
        MaxSpeed = InitialMaxSpeed;
    }

    private float LaneSpeedOffset(int pointId)
    {
        var aiParams = _configuration.Extra.AiParams;
        if (AccEnabled && aiParams.Acc.LaneSpeedGradient && !aiParams.TwoWayTraffic)
            return aiParams.RightLaneOffsetMs * GetLaneSpeedFraction(pointId);

        return _spline.Points[pointId].LeftId >= 0 ? aiParams.RightLaneOffsetMs : 0;
    }

    /// <summary>0 for the leftmost lane, 1 for the rightmost lane</summary>
    private float GetLaneSpeedFraction(int pointId)
    {
        var lanes = _spline.GetLanes(pointId);
        int index = lanes.IndexOf(pointId);
        if (lanes.Length <= 1 || index < 0) return 0;

        if (_configuration.Extra.AiParams.Acc.LaneSpeedFractions.TryGetValue(lanes.Length, out var fractions) && fractions.Count == lanes.Length)
            return fractions[index];

        return (float)index / (lanes.Length - 1);
    }

    private static float RandomRange(float min, float max) => min + Random.Shared.NextSingle() * (max - min);

    private void SetRandomAccParams()
    {
        var accParams = _configuration.Extra.AiParams.Acc;
        _accTimeHeadway = RandomRange(accParams.MinTimeHeadwaySeconds, accParams.MaxTimeHeadwaySeconds);
        _accStandstillGap = RandomRange(accParams.MinStandstillGapMeters, accParams.MaxStandstillGapMeters);
        _accMaxAcceleration = EntryCar.AiAcceleration * RandomRange(accParams.MinAccelerationFactor, accParams.MaxAccelerationFactor);
        _accComfortableDeceleration = MathF.Min(RandomRange(accParams.MinComfortableDecelerationMs2, accParams.MaxComfortableDecelerationMs2), EntryCar.AiDeceleration);
        _accIntegrate = false;
        _accBrakeLightsOn = false;

        var lcParams = _configuration.Extra.AiParams.LaneChanges;
        _lcPoliteness = RandomRange(lcParams.MinPoliteness, lcParams.MaxPoliteness);
        _lcAggressive = Random.Shared.NextSingle() < lcParams.AggressiveDriverShare;
        _lcDurationSeconds = RandomRange(lcParams.MinDurationSeconds, lcParams.MaxDurationSeconds);
        _lcIndicatorSeconds = RandomRange(lcParams.MinIndicatorSeconds, lcParams.MaxIndicatorSeconds);
        long now = _sessionManager.ServerTimeMilliseconds;
        // Stagger decisions of different cars and let the car settle after spawning
        _lcNextDecision = now + Random.Shared.Next(lcParams.DecisionIntervalMilliseconds);
        _lcCooldownUntil = now + (long)(Random.Shared.NextSingle() * lcParams.MinCooldownSeconds * 1000);
    }

    private void SetRandomColor()
    {
        Color = CarColors[Random.Shared.Next(CarColors.Count)];
    }

    public void Teleport(int pointId) => Teleport(pointId, null);

    /// <summary>
    /// <paramref name="timeHeadway"/>: Acc time headway of the new driver instead of a random one, for a spawn right behind
    /// a car (AiBehavior, PlatoonSpawnShare)
    /// </summary>
    public void Teleport(int pointId, float? timeHeadway)
    {
        lock (_moveLock)
        {
            TeleportLocked(pointId, timeHeadway);
        }
    }

    private void TeleportLocked(int pointId, float? timeHeadway)
    {
        if (_trace.Enabled && Initialized)
        {
            // State left the player radius and is reused without Despawn()
            _trace.Event("despawn", this, $"\"why\":\"radius\",\"pt\":{CurrentSplinePointId},\"v\":{AiTrace.F(CurrentSpeed)}");
        }

        ResetLaneChange();
        ResetMerge();
        _visuals.Reset();
        _junctionEvaluator.Clear();
        CurrentSplinePointId = pointId;
        if (!_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPointId))
            throw new InvalidOperationException($"Cannot get next spline point for {CurrentSplinePointId}");
        _currentVecLength = (_spline.Points[nextPointId].Position - _spline.Points[CurrentSplinePointId].Position).Length();
        _currentVecProgress = 0;
            
        CalculateTangents();
        
        SetRandomSpeed();
        SetRandomColor();

        var minDist = _configuration.Extra.AiParams.MinAiSafetyDistanceSquared;
        var maxDist = _configuration.Extra.AiParams.MaxAiSafetyDistanceSquared;
        if (_configuration.Extra.AiParams.LaneCountSpecificOverrides.TryGetValue(_spline.GetLanes(CurrentSplinePointId).Length, out var overrides))
        {
            minDist = overrides.MinAiSafetyDistanceSquared;
            maxDist = overrides.MaxAiSafetyDistanceSquared;
        }
        
        if (EntryCar.MinAiSafetyDistanceMetersSquared.HasValue)
            minDist = EntryCar.MinAiSafetyDistanceMetersSquared.Value;
        if (EntryCar.MaxAiSafetyDistanceMetersSquared.HasValue)
            maxDist = EntryCar.MaxAiSafetyDistanceMetersSquared.Value;

        SpawnProtectionEnds = _sessionManager.ServerTimeMilliseconds + Random.Shared.Next(EntryCar.AiMinSpawnProtectionTimeMilliseconds, EntryCar.AiMaxSpawnProtectionTimeMilliseconds);
        SafetyDistanceSquared = Random.Shared.Next((int)Math.Round(minDist * (1.0f / _configuration.Extra.AiParams.TrafficDensity)),
            (int)Math.Round(maxDist * (1.0f / _configuration.Extra.AiParams.TrafficDensity)));
        _stoppedForCollisionUntil = 0;
        _hazardsUntil = 0;
        _touchBrakeUntil = 0;
        _obstacleAhead = null;
        _obstacleGap = 0;
        _blockedSince = 0;
        _yieldingTo = null;
        IsBlockedByObstacle = false;
        _ignoreObstaclesUntil = 0;
        _obstacleHonkEnd = 0;
        _obstacleHonkStart = 0;
        _indicator = 0;
        _randomTwilight = Random.Shared.NextSingle() * 12 * Math.PI / 180.0;
        _nextJunctionId = -1;
        _junctionPassed = false;
        _endIndicatorDistance = 0;
        _lastTick = _sessionManager.ServerTimeMilliseconds;
        _minObstacleDistance = Random.Shared.Next(8, 13);
        SetRandomAccParams();
        if (timeHeadway.HasValue)
            _accTimeHeadway = timeHeadway.Value;
        SpawnCounter++;
        TraceReason = AiTraceReason.Free;
        TraceLeaderId = -1;
        TracePlayerDistance = -1;
        Initialized = true;
        Update();

        if (AccEnabled)
        {
            SetAccSpawnSpeed();
        }

        if (_trace.Enabled)
        {
            _trace.Event("spawn", this, $"\"pt\":{CurrentSplinePointId},\"v\":{AiTrace.F(CurrentSpeed)},\"safety\":{AiTrace.F(MathF.Sqrt(SafetyDistanceSquared))},\"minObstacle\":{AiTrace.F(_minObstacleDistance)},\"v0\":{AiTrace.F(InitialMaxSpeed)},\"T\":{AiTrace.F(_accTimeHeadway)},\"s0\":{AiTrace.F(_accStandstillGap)},\"amax\":{AiTrace.F(_accMaxAcceleration)},\"b\":{AiTrace.F(_accComfortableDeceleration)},\"p\":{AiTrace.F(_lcPoliteness)},\"aggr\":{(_lcAggressive ? 1 : 0)},\"slow\":{(_accSlowDriver ? 1 : 0)}");
        }
    }

    private void CalculateTangents()
    {
        if (!_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPointId))
            throw new InvalidOperationException("Cannot get next spline point");

        var points = _spline.Points;
        
        if (_junctionEvaluator.TryPrevious(CurrentSplinePointId, out var previousPointId))
        {
            _startTangent = (points[nextPointId].Position - points[previousPointId].Position) * 0.5f;
        }
        else
        {
            _startTangent = (points[nextPointId].Position - points[CurrentSplinePointId].Position) * 0.5f;
        }

        if (_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextNextPointId, 2))
        {
            _endTangent = (points[nextNextPointId].Position - points[CurrentSplinePointId].Position) * 0.5f;
        }
        else
        {
            _endTangent = (points[nextPointId].Position - points[CurrentSplinePointId].Position) * 0.5f;
        }
    }

    private bool Move(float progress)
    {
        var points = _spline.Points;
        var junctions = _spline.Junctions;
        
        bool recalculateTangents = false;
        while (progress > _currentVecLength)
        {
            progress -= _currentVecLength;
                
            if (!_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPointId)
                || !_junctionEvaluator.TryNext(nextPointId, out var nextNextPointId))
            {
                return false;
            }

            if (_trace.Enabled && points[CurrentSplinePointId].JunctionStartId >= 0)
            {
                int junctionId = points[CurrentSplinePointId].JunctionStartId;
                bool taken = nextPointId == junctions[junctionId].EndPointId;
                _trace.Event("junction", this, $"\"j\":{junctionId},\"pt\":{CurrentSplinePointId},\"taken\":{(taken ? "true" : "false")}");
            }

            CurrentSplinePointId = nextPointId;
            _currentVecLength = (points[nextNextPointId].Position - points[CurrentSplinePointId].Position).Length();
            recalculateTangents = true;

            if (_junctionPassed)
            {
                _endIndicatorDistance -= _currentVecLength;

                if (_endIndicatorDistance < 0)
                {
                    _indicator = 0;
                    _junctionPassed = false;
                    _endIndicatorDistance = 0;
                }
            }
                
            if (_nextJunctionId >= 0 && points[CurrentSplinePointId].JunctionEndId == _nextJunctionId)
            {
                _junctionPassed = true;
                _endIndicatorDistance = junctions[_nextJunctionId].IndicateDistancePost;
                _nextJunctionId = -1;
            }
        }

        if (recalculateTangents)
        {
            CalculateTangents();
        }

        _currentVecProgress = progress;

        return true;
    }

    public bool CanSpawn(int spawnPointId, AiState? previousAi, AiState? nextAi)
    {
        var ops = _spline.Operations;
        ref readonly var spawnPoint = ref ops.Points[spawnPointId];

        if (!IsAllowedLaneCount(spawnPointId))
            return false;
        if (!IsAllowedLane(in spawnPoint))
            return false;
        if (!IsKeepingSafetyDistances(in spawnPoint, previousAi, nextAi))
            return false;

        return EntryCar.CanSpawnAiState(spawnPoint.Position, this);
    }

    private bool IsKeepingSafetyDistances(in SplinePoint spawnPoint, AiState? previousAi, AiState? nextAi)
    {
        if (previousAi != null)
        {
            var distance = MathF.Max(0, Vector3.Distance(spawnPoint.Position, previousAi.Status.Position)
                           - previousAi.EntryCar.VehicleLengthPreMeters
                           - EntryCar.VehicleLengthPostMeters);

            var distanceSquared = distance * distance;
            if (distanceSquared < previousAi.SafetyDistanceSquared || distanceSquared < SafetyDistanceSquared)
                return false;
        }
        
        if (nextAi != null)
        {
            var distance = MathF.Max(0, Vector3.Distance(spawnPoint.Position, nextAi.Status.Position)
                                        - nextAi.EntryCar.VehicleLengthPostMeters
                                        - EntryCar.VehicleLengthPreMeters);

            var distanceSquared = distance * distance;
            if (distanceSquared < nextAi.SafetyDistanceSquared || distanceSquared < SafetyDistanceSquared)
                return false;
        }

        return true;
    }

    private bool IsAllowedLaneCount(int spawnPointId)
    {
        var laneCount = _spline.GetLanes(spawnPointId).Length;
        if (EntryCar.MinLaneCount.HasValue && laneCount < EntryCar.MinLaneCount.Value)
            return false;
        if (EntryCar.MaxLaneCount.HasValue && laneCount > EntryCar.MaxLaneCount.Value)
            return false;
        
        return true;
    }

    private bool IsAllowedLane(in SplinePoint spawnPoint)
    {
        var isAllowedLane = true;
        if (EntryCar.AiAllowedLanes != null)
        {
            isAllowedLane = (EntryCar.AiAllowedLanes.Contains(LaneSpawnBehavior.Middle) && spawnPoint.LeftId >= 0 && spawnPoint.RightId >= 0)
                            || (EntryCar.AiAllowedLanes.Contains(LaneSpawnBehavior.Left) && spawnPoint.LeftId < 0)
                            || (EntryCar.AiAllowedLanes.Contains(LaneSpawnBehavior.Right) && spawnPoint.RightId < 0);
        }

        return isAllowedLane;
    }

    private void UpdateJunctionIndicator(in SplinePoint point, float distanceTravelled, ref bool junctionFound)
    {
        if (junctionFound || point.JunctionStartId < 0) return;

        ref readonly var jct = ref _spline.Junctions[point.JunctionStartId];
        if (distanceTravelled >= jct.IndicateDistancePre) return;

        var indicator = _junctionEvaluator.WillTakeJunction(point.JunctionStartId) ? jct.IndicateWhenTaken : jct.IndicateWhenNotTaken;
        if (indicator != 0)
        {
            _indicator = indicator;
            _nextJunctionId = point.JunctionStartId;
            junctionFound = true;
        }
    }

    private (AiState? ClosestAiState, float ClosestAiStateDistance, float MaxSpeed) SplineLookahead()
    {
        var points = _spline.Points;
        
        float maxBrakingDistance = PhysicsUtils.CalculateBrakingDistance(CurrentSpeed, EntryCar.AiDeceleration) * 2 + 20;
        AiState? closestAiState = null;
        float closestAiStateDistance = float.MaxValue;
        bool junctionFound = false;
        float distanceTravelled = 0;
        var pointId = CurrentSplinePointId;
        ref readonly var point = ref points[pointId]; 
        float maxSpeed = float.MaxValue;
        float currentSpeedSquared = CurrentSpeed * CurrentSpeed;
        while (distanceTravelled < maxBrakingDistance)
        {
            distanceTravelled += point.Length;
            pointId = _junctionEvaluator.Next(pointId);
            if (pointId < 0)
                break;

            point = ref points[pointId];

            UpdateJunctionIndicator(in point, distanceTravelled, ref junctionFound);

            if (closestAiState == null)
            {
                var slowest = _spline.SlowestAiStates[pointId];

                if (slowest != null)
                {
                    closestAiState = slowest;
                    closestAiStateDistance = MathF.Max(0, Vector3.Distance(Status.Position, closestAiState.Status.Position)
                                                          - EntryCar.VehicleLengthPreMeters
                                                          - closestAiState.EntryCar.VehicleLengthPostMeters);
                }
            }

            float maxCorneringSpeedSquared = PhysicsUtils.CalculateMaxCorneringSpeedSquared(point.Radius, EntryCar.AiCorneringSpeedFactor);
            if (maxCorneringSpeedSquared < currentSpeedSquared)
            {
                float maxCorneringSpeed = MathF.Sqrt(maxCorneringSpeedSquared);
                float brakingDistance = PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - maxCorneringSpeed,
                                            EntryCar.AiDeceleration * EntryCar.AiCorneringBrakeForceFactor)
                                        * EntryCar.AiCorneringBrakeDistanceFactor;

                if (brakingDistance > distanceTravelled)
                {
                    maxSpeed = Math.Min(maxCorneringSpeed, maxSpeed);
                }
            }
        }

        return (closestAiState, closestAiStateDistance, maxSpeed);
    }

    private bool ShouldIgnorePlayerObstacles()
    {
        if (_configuration.Extra.AiParams.IgnorePlayerObstacleSpheres != null)
        {
            foreach (var sphere in _configuration.Extra.AiParams.IgnorePlayerObstacleSpheres)
            {
                if (Vector3.DistanceSquared(Status.Position, sphere.Center) < sphere.RadiusMeters * sphere.RadiusMeters)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private (EntryCar? entryCar, float distance) FindClosestPlayerObstacle()
    {
        if (!ShouldIgnorePlayerObstacles())
        {
            EntryCar? closestCar = null;
            float minDistance = float.MaxValue;
            for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
            {
                var playerCar = _entryCarManager.EntryCars[i];
                if (playerCar.Client?.HasSentFirstUpdate == true)
                {
                    float distance = Vector3.DistanceSquared(playerCar.Status.Position, Status.Position);

                    if (distance < minDistance
                        && Math.Abs(playerCar.Status.Position.Y - Status.Position.Y) < 1.5
                        && GetAngleToCar(playerCar.Status) is > 166 and < 194)
                    {
                        minDistance = distance;
                        closestCar = playerCar;
                    }
                }
            }

            if (closestCar != null)
            {
                return (closestCar, MathF.Sqrt(minDistance));
            }
        }

        return (null, float.MaxValue);
    }

    private bool IsObstacle(EntryCar playerCar)
    {
        float aiRectWidth = 4; // Lane width
        float halfAiRectWidth = aiRectWidth / 2;
        float aiRectLength = 10; // length of rectangle infront of ai traffic
        float aiRectOffset = 1; // offset of the rectangle from ai position

        float obstacleRectWidth = 1; // width of obstacle car 
        float obstacleRectLength = 1; // length of obstacle car
        float halfObstacleRectWidth = obstacleRectWidth / 2;
        float halfObstanceRectLength = obstacleRectLength / 2;

        Vector3 forward = Vector3.Transform(-Vector3.UnitX, Matrix4x4.CreateRotationY(Status.Rotation.X));
        Matrix4x4 aiViewMatrix = Matrix4x4.CreateLookAt(Status.Position, Status.Position + forward, Vector3.UnitY);

        Matrix4x4 targetWorldViewMatrix = Matrix4x4.CreateRotationY(playerCar.Status.Rotation.X) * Matrix4x4.CreateTranslation(playerCar.Status.Position) * aiViewMatrix;

        Vector3 targetFrontLeft = Vector3.Transform(new Vector3(-halfObstanceRectLength, 0, halfObstacleRectWidth), targetWorldViewMatrix);
        Vector3 targetFrontRight = Vector3.Transform(new Vector3(-halfObstanceRectLength, 0, -halfObstacleRectWidth), targetWorldViewMatrix);
        Vector3 targetRearLeft = Vector3.Transform(new Vector3(halfObstanceRectLength, 0, halfObstacleRectWidth), targetWorldViewMatrix);
        Vector3 targetRearRight = Vector3.Transform(new Vector3(halfObstanceRectLength, 0, -halfObstacleRectWidth), targetWorldViewMatrix);

        static bool IsPointInside(Vector3 point, float width, float length, float offset)
            => MathF.Abs(point.X) >= width || (-point.Z >= offset && -point.Z <= offset + length);

        bool isObstacle = IsPointInside(targetFrontLeft, halfAiRectWidth, aiRectLength, aiRectOffset)
                          || IsPointInside(targetFrontRight, halfAiRectWidth, aiRectLength, aiRectOffset)
                          || IsPointInside(targetRearLeft, halfAiRectWidth, aiRectLength, aiRectOffset)
                          || IsPointInside(targetRearRight, halfAiRectWidth, aiRectLength, aiRectOffset);

        return isObstacle;
    }

    public void DetectObstacles()
    {
        if (!Initialized) return;
            
        if (_sessionManager.ServerTimeMilliseconds < _ignoreObstaclesUntil)
        {
            TraceReason = AiTraceReason.IgnoringObstacles;
            SetTargetSpeed(MaxSpeed);
            return;
        }

        // With incidents the Acc model stops the car itself, see DetectObstaclesAcc
        if (_sessionManager.ServerTimeMilliseconds < _stoppedForCollisionUntil && !IncidentsEnabled)
        {
            TraceReason = AiTraceReason.CollisionStop;
            SetTargetSpeed(0);
            return;
        }

        if (AccEnabled)
        {
            DetectObstaclesAcc();
            return;
        }

        float targetSpeed = InitialMaxSpeed;
        float maxSpeed = InitialMaxSpeed;
        bool hasObstacle = false;
        var reason = AiTraceReason.Free;

        var splineLookahead = SplineLookahead();
        var playerObstacle = FindClosestPlayerObstacle();

        ClosestAiObstacleDistance = splineLookahead.ClosestAiState != null ? splineLookahead.ClosestAiStateDistance : -1;
        TraceLeaderId = splineLookahead.ClosestAiState?.TraceId ?? -1;
        TracePlayerDistance = playerObstacle.entryCar != null ? playerObstacle.distance : -1;

        if (playerObstacle.distance < _minObstacleDistance || splineLookahead.ClosestAiStateDistance < _minObstacleDistance)
        {
            targetSpeed = 0;
            hasObstacle = true;
            reason = playerObstacle.distance < splineLookahead.ClosestAiStateDistance ? AiTraceReason.StopPlayer : AiTraceReason.StopAi;
        }
        else if (playerObstacle.distance < splineLookahead.ClosestAiStateDistance && playerObstacle.entryCar != null)
        {
            float playerSpeed = playerObstacle.entryCar.Status.Velocity.Length();

            if (playerSpeed < 0.1f)
            {
                playerSpeed = 0;
            }

            if ((playerSpeed < CurrentSpeed || playerSpeed == 0)
                && playerObstacle.distance < PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - playerSpeed, EntryCar.AiDeceleration) * 2 + 20)
            {
                targetSpeed = Math.Max(WalkingSpeed, playerSpeed);
                hasObstacle = true;
                reason = AiTraceReason.FollowPlayer;
            }
        }
        else if (splineLookahead.ClosestAiState != null)
        {
            float closestTargetSpeed = Math.Min(splineLookahead.ClosestAiState.CurrentSpeed, splineLookahead.ClosestAiState.TargetSpeed);
            if ((closestTargetSpeed < CurrentSpeed || splineLookahead.ClosestAiState.CurrentSpeed == 0)
                && splineLookahead.ClosestAiStateDistance < PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - closestTargetSpeed, EntryCar.AiDeceleration) * 2 + 20)
            {
                targetSpeed = Math.Max(WalkingSpeed, closestTargetSpeed);
                hasObstacle = true;
                reason = AiTraceReason.FollowAi;
            }
        }

        if (splineLookahead.MaxSpeed < targetSpeed)
        {
            reason = AiTraceReason.Corner;
        }
        targetSpeed = Math.Min(splineLookahead.MaxSpeed, targetSpeed);
        TraceReason = reason;

        UpdateStoppedForObstacle(reason);

        float deceleration = EntryCar.AiDeceleration;
        if (!hasObstacle)
        {
            deceleration *= EntryCar.AiCorneringBrakeForceFactor;
        }
        
        MaxSpeed = maxSpeed;
        SetTargetSpeed(targetSpeed, deceleration, EntryCar.AiAcceleration);
    }

    private struct AccLookaheadResult
    {
        public AiState? Ai;
        public float AiGap;
        public AiLanePlayer? Player;
        public float PlayerGap;
        public float CornerAcceleration;
        public float CornerSpeed;
        // Merges: first point ahead (or the current point) that has a twin in another lane, -1 if none, and the distance to it
        public int MergeAnchor;
        public float MergeAnchorDistance;
        // Incidents: nearest car ahead in a neighbour lane that stands with the indicator on towards this lane, waiting to go around
        public AiState? GoAroundRequest;
        public float GoAroundRequestGap;
    }

    private float LengthPre(EntryCar car) => car.VehicleLengthPreMeters > 0 ? car.VehicleLengthPreMeters : _configuration.Extra.AiParams.Acc.DefaultVehicleLengthMeters / 2;
    private float LengthPost(EntryCar car) => car.VehicleLengthPostMeters > 0 ? car.VehicleLengthPostMeters : _configuration.Extra.AiParams.Acc.DefaultVehicleLengthMeters / 2;

    /// <summary>
    /// Looks ahead along the own lane for the first AI car, the first player in this lane and corners.
    /// Unlike SplineLookahead, gaps are bumper to bumper and corners give a smooth deceleration instead of a speed limit.
    /// With <paramref name="startPointId"/> it looks along another lane (the lane a lane changing car is leaving)
    /// for cars only: no corners and no junction indicators.
    /// </summary>
    private AccLookaheadResult AccLookahead(float desiredSpeed, bool checkPlayers = true, int startPointId = -1)
    {
        bool ownLane = startPointId < 0;
        var points = _spline.Points;
        var accParams = _configuration.Extra.AiParams.Acc;
        var result = new AccLookaheadResult
        {
            AiGap = float.MaxValue,
            PlayerGap = float.MaxValue,
            CornerAcceleration = float.MaxValue,
            CornerSpeed = float.MaxValue,
            MergeAnchor = -1,
            GoAroundRequestGap = float.MaxValue
        };

        float speed = CurrentSpeed;
        float speedSquared = speed * speed;
        float maxDistance = MathF.Max(accParams.MinLookaheadMeters, speed * accParams.LookaheadSeconds);
        checkPlayers = checkPlayers && !ShouldIgnorePlayerObstacles();
        var pointId = ownLane ? CurrentSplinePointId : startPointId;
        var forward = Vector3.Normalize(_spline.Operations.GetForwardVector(pointId));

        ref readonly var point = ref points[pointId];

        bool findMergeAnchor = ownLane && _merges.Enabled;
        if (findMergeAnchor && _merges.Twin(pointId) >= 0)
        {
            result.MergeAnchor = pointId;
            result.MergeAnchorDistance = -_currentVecProgress;
        }

        // A player next to us or slightly ahead is registered at our own point
        if (checkPlayers)
        {
            CheckPlayerAtPoint(pointId, forward, ref result);
        }

        // Already in a corner: don't accelerate above the corner speed
        float cornerSpeedSquared = PhysicsUtils.CalculateMaxCorneringSpeedSquared(point.Radius, EntryCar.AiCorneringSpeedFactor);
        if (ownLane && cornerSpeedSquared < desiredSpeed * desiredSpeed)
        {
            result.CornerSpeed = MathF.Sqrt(cornerSpeedSquared);
            result.CornerAcceleration = FreeAcceleration(speed, result.CornerSpeed);
        }

        bool junctionFound = false;
        float distanceTravelled = -_currentVecProgress;
        while (distanceTravelled < maxDistance)
        {
            distanceTravelled += point.Length;
            pointId = _junctionEvaluator.Next(pointId);
            if (pointId < 0)
                break;

            point = ref points[pointId];

            if (ownLane)
            {
                UpdateJunctionIndicator(in point, distanceTravelled, ref junctionFound);
            }

            if (findMergeAnchor && result.MergeAnchor < 0 && _merges.Twin(pointId) >= 0)
            {
                result.MergeAnchor = pointId;
                result.MergeAnchorDistance = distanceTravelled;
            }

            if (result.Ai == null)
            {
                var slowest = _spline.SlowestAiStates[pointId];
                if (slowest != null && slowest != this)
                {
                    result.Ai = slowest;
                    result.AiGap = MathF.Max(0, Vector3.Distance(Status.Position, slowest.Status.Position)
                                                - LengthPre(EntryCar)
                                                - LengthPost(slowest.EntryCar));
                }
            }

            if (checkPlayers && result.Player == null)
            {
                CheckPlayerAtPoint(pointId, forward, ref result);
            }

            if (ownLane && result.GoAroundRequest == null && distanceTravelled < GoAroundYieldLookaheadMeters && GoAroundEnabled)
            {
                CheckGoAroundRequest(pointId, ref result);
            }

            if (!ownLane)
            {
                if (result.Ai != null && (result.Player != null || !checkPlayers)) break;
                continue;
            }

            // Constant deceleration that reaches the corner speed at the corner
            cornerSpeedSquared = PhysicsUtils.CalculateMaxCorneringSpeedSquared(point.Radius, EntryCar.AiCorneringSpeedFactor);
            if (cornerSpeedSquared < speedSquared)
            {
                float cornerAcceleration = -(speedSquared - cornerSpeedSquared) / (2 * MathF.Max(distanceTravelled, 1));
                result.CornerAcceleration = MathF.Min(result.CornerAcceleration, cornerAcceleration);
                result.CornerSpeed = MathF.Min(result.CornerSpeed, MathF.Sqrt(cornerSpeedSquared));
            }
        }

        return result;
    }

    /// <summary>A car next to <paramref name="pointId"/> in a neighbour lane waits to go around into this lane</summary>
    private void CheckGoAroundRequest(int pointId, ref AccLookaheadResult result)
    {
        ref readonly var point = ref _spline.Points[pointId];
        for (int side = -1; side <= 1; side += 2)
        {
            int neighborId = side < 0 ? point.LeftId : point.RightId;
            if (neighborId < 0) continue;

            // Points of the two lanes don't pair up exactly, check the next neighbour point too
            var requester = _spline.SlowestAiStates[neighborId];
            if (requester == null && _spline.Points[neighborId].NextId >= 0)
                requester = _spline.SlowestAiStates[_spline.Points[neighborId].NextId];
            // Its indicator must point back to this lane: from our left lane it goes right, and the other way round
            if (requester == null || requester == this || requester.GoAroundRequestDirection != -side) continue;

            result.GoAroundRequest = requester;
            result.GoAroundRequestGap = MathF.Max(0, Vector3.Distance(Status.Position, requester.Status.Position)
                                                     - LengthPre(EntryCar) - LengthPost(requester.EntryCar));
            return;
        }
    }

    private void CheckPlayerAtPoint(int pointId, Vector3 forward, ref AccLookaheadResult result)
    {
        if (!_playerLanes.TryGet(pointId, out var player)) return;

        var toPlayer = player.Car.Status.Position - Status.Position;
        float longitudinal = Vector3.Dot(toPlayer, forward);
        if (MathF.Abs(toPlayer.Y) >= 1.5f || longitudinal <= 0) return;

        // A faster player alongside is passing, not leading: it becomes a leader once its rear is ahead of our nose
        if (player.Speed > CurrentSpeed && longitudinal < LengthPre(EntryCar) + LengthPost(player.Car)) return;

        result.Player = player;
        result.PlayerGap = MathF.Max(0, toPlayer.Length() - LengthPre(EntryCar) - LengthPost(player.Car));
    }

    // Improved IDM: below this free road acceleration the car counts as at its desired speed (the exponent 2a/a_free would explode)
    private const float ImprovedIdmMinFreeAcceleration = 0.001f;

    /// <summary>IDM free road term, never brakes harder than the comfortable deceleration</summary>
    private float FreeAcceleration(float speed, float desiredSpeed)
    {
        if (desiredSpeed <= 0) return -_accComfortableDeceleration;

        float acceleration = _accMaxAcceleration * (1 - MathF.Pow(speed / desiredSpeed, _configuration.Extra.AiParams.Acc.AccelerationExponent));
        return MathF.Max(acceleration, -_accComfortableDeceleration);
    }

    /// <summary>
    /// ACC model: Intelligent Driver Model blended with the constant-acceleration heuristic (CAH).
    /// Treiber, Kesting. Traffic Flow Dynamics, 2013, chapter 11.3. CAH keeps the reaction calm when a faster car
    /// cuts in close ahead, plain IDM would brake hard. With AccParams.ImprovedIdm the IDM part is the Improved IDM.
    /// </summary>
    private float AccAcceleration(float speed, float gap, float leaderSpeed, float leaderAcceleration, float freeAcceleration)
    {
        var accParams = _configuration.Extra.AiParams.Acc;
        return AccAccelerationCore(_accMaxAcceleration, _accComfortableDeceleration, _accStandstillGap, _accTimeHeadway, EntryCar.AiDeceleration,
            accParams.Coolness, speed, gap, leaderSpeed, leaderAcceleration, freeAcceleration, accParams.ImprovedIdm);
    }

    /// <param name="improved">
    /// Improved IDM (chapter 11.3.4): the equilibrium gap is the desired gap s* at any speed below the desired speed. In the
    /// plain IDM it is s* / sqrt(1 - (v/v0)^4), 2.3 times s* at 95% of v0. Written with the free road acceleration, which is
    /// positive below and negative above the desired speed. The reference player model (lane change and merge checks) keeps
    /// the plain IDM, it is the more cautious one.
    /// </param>
    private static float AccAccelerationCore(float a, float b, float standstillGap, float timeHeadway, float maxDeceleration, float coolness,
        float speed, float gap, float leaderSpeed, float leaderAcceleration, float freeAcceleration, bool improved = false)
    {
        float s = MathF.Max(gap, 0.1f);
        float dv = speed - leaderSpeed;

        float desiredGap = standstillGap + MathF.Max(0, speed * timeHeadway + speed * dv / (2 * MathF.Sqrt(a * b)));
        float z = desiredGap / s;
        float idm;
        if (!improved)
        {
            idm = freeAcceleration - a * z * z;
        }
        else if (freeAcceleration > ImprovedIdmMinFreeAcceleration)
        {
            idm = z >= 1 ? a * (1 - z * z) : freeAcceleration * (1 - MathF.Pow(z, 2 * a / freeAcceleration));
        }
        else
        {
            idm = z >= 1 ? freeAcceleration + a * (1 - z * z) : freeAcceleration;
        }

        // Limit to emergency braking before blending: at a gap close to 0 the IDM term is huge and even (1 - coolness) of it
        // would mean full braking
        idm = MathF.Max(idm, -maxDeceleration);

        float leaderAcc = MathF.Min(leaderAcceleration, a);
        float denominator = leaderSpeed * leaderSpeed - 2 * s * leaderAcc;
        float cah = leaderSpeed * dv <= -2 * s * leaderAcc && denominator > 0.01f
            ? speed * speed * leaderAcc / denominator
            : leaderAcc - (dv > 0 ? dv * dv : 0) / (2 * s);

        if (idm >= cah) return idm;

        return (1 - coolness) * idm + coolness * (cah + b * MathF.Tanh((idm - cah) / b));
    }

    /// <summary>
    /// Spawn at the flow speed: the highest speed up to the desired speed at which the Acc model doesn't have to brake
    /// for the car ahead. Players are not checked, spawns are far from them (SpawnSafetyDistanceToPlayerMeters).
    /// </summary>
    private void SetAccSpawnSpeed()
    {
        var lookahead = AccLookahead(InitialMaxSpeed, checkPlayers: false);
        if (lookahead.Ai == null) return;

        var leader = lookahead.Ai;
        float maxDeceleration = _configuration.Extra.AiParams.Acc.BrakeLightsOffDecelerationMs2;
        float low = 0, high = InitialMaxSpeed;
        if (AccAcceleration(high, lookahead.AiGap, leader.CurrentSpeed, leader.Acceleration, FreeAcceleration(high, InitialMaxSpeed)) >= -maxDeceleration)
            return;

        for (int i = 0; i < 12; i++)
        {
            float mid = (low + high) / 2;
            if (AccAcceleration(mid, lookahead.AiGap, leader.CurrentSpeed, leader.Acceleration, FreeAcceleration(mid, InitialMaxSpeed)) >= -maxDeceleration)
                low = mid;
            else
                high = mid;
        }

        CurrentSpeed = low;
        TargetSpeed = low;
    }

    /// <summary>
    /// Spawn follower check: acceleration of this car if another car appeared at pointId ahead of it. The new car's speed
    /// is estimated like SetAccSpawnSpeed picks it, for the slowest desired speed of that lane and a cautious driver,
    /// so the real spawn speed is rarely lower.
    /// </summary>
    public float AccAccelerationForSpawn(int pointId, AiState? leader, float? timeHeadway = null)
    {
        var aiParams = _configuration.Extra.AiParams;
        var accParams = aiParams.Acc;
        var position = _spline.Points[pointId].Position;
        float halfLength = accParams.DefaultVehicleLengthMeters / 2;

        float desiredSpeed = aiParams.MaxSpeedMs + LaneSpeedOffset(pointId) - aiParams.MaxSpeedMs * accParams.DesiredSpeedVariationPercent / 2;
        float spawnSpeed = desiredSpeed;
        if (leader != null)
        {
            float a = EntryCar.AiAcceleration * accParams.MinAccelerationFactor;
            float b = MathF.Min(accParams.MinComfortableDecelerationMs2, EntryCar.AiDeceleration);
            float leaderGap = MathF.Max(0, Vector3.Distance(position, leader.Status.Position) - halfLength - LengthPost(leader.EntryCar));

            float SpawnAcceleration(float speed)
            {
                float free = MathF.Max(a * (1 - MathF.Pow(speed / desiredSpeed, accParams.AccelerationExponent)), -b);
                return AccAccelerationCore(a, b, accParams.MaxStandstillGapMeters, timeHeadway ?? accParams.MaxTimeHeadwaySeconds, EntryCar.AiDeceleration,
                    accParams.Coolness, speed, leaderGap, leader.CurrentSpeed, leader.Acceleration, free, accParams.ImprovedIdm);
            }

            if (SpawnAcceleration(desiredSpeed) < -accParams.BrakeLightsOffDecelerationMs2)
            {
                float low = 0, high = desiredSpeed;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (low + high) / 2;
                    if (SpawnAcceleration(mid) >= -accParams.BrakeLightsOffDecelerationMs2)
                        low = mid;
                    else
                        high = mid;
                }

                spawnSpeed = low;
            }
        }

        float gap = MathF.Max(0, Vector3.Distance(Status.Position, position) - LengthPre(EntryCar) - halfLength);
        return AccAcceleration(CurrentSpeed, gap, spawnSpeed, 0, FreeAcceleration(CurrentSpeed, InitialMaxSpeed));
    }

    /// <summary>
    /// Incidents: the nearest car ahead in the own lane is an obstacle to go around: a slow player, a crashed car, or a standing
    /// car that itself waits to go around (so the whole queue behind an obstacle goes around it).
    /// </summary>
    private void UpdateObstacleAhead(in AccLookaheadResult lookahead, long now)
    {
        object? obstacle = null;
        float gap = 0;
        if (GoAroundEnabled)
        {
            float playerSpeed = _configuration.Extra.AiParams.Incidents.ObstaclePlayerSpeedKph / 3.6f;
            if (lookahead.Player is { } player && lookahead.PlayerGap <= lookahead.AiGap)
            {
                if (player.Speed < playerSpeed)
                {
                    obstacle = player.Car;
                    gap = lookahead.PlayerGap;
                }
            }
            else if (lookahead.Ai is { } ai && (ai.IsCrashed || (ai.IsBlockedByObstacle && ai.CurrentSpeed < StandingSpeed)))
            {
                obstacle = ai;
                gap = lookahead.AiGap;
            }
        }

        if (obstacle != null && _obstacleAhead != obstacle && _trace.Enabled)
        {
            var obstacleAi = obstacle as AiState;
            string kind = obstacleAi == null ? "player" : obstacleAi.IsCrashed ? "crash" : "queue";
            int player = obstacle is EntryCar car ? car.SessionId : -1;
            _trace.Event("obstacle", this, $"\"pt\":{CurrentSplinePointId},\"kind\":\"{kind}\",\"lead\":{obstacleAi?.TraceId ?? -1},\"pl\":{player},\"gap\":{AiTrace.F(gap)},\"v\":{AiTrace.F(CurrentSpeed)}");
        }

        _obstacleAhead = obstacle;
        _obstacleGap = gap;
        IsBlockedByObstacle = obstacle != null;
        if (obstacle != null && CurrentSpeed < StandingSpeed)
        {
            if (_blockedSince == 0) _blockedSince = now;
        }
        else
        {
            _blockedSince = 0;
        }
    }

    /// <summary>
    /// Zipper: let in a car that waits to go around into this lane ahead, if this car can stop behind it braking no harder than
    /// its comfortable deceleration. Otherwise drive on, a car further behind lets it in. Keeps yielding until the other car
    /// starts its lane change (then it is the normal leader) or stops waiting.
    /// </summary>
    private void UpdateGoAroundYield(in AccLookaheadResult lookahead, float speed, float freeAcceleration)
    {
        if (_yieldingTo != null && (!_yieldingTo.Initialized || _yieldingTo.GoAroundRequestDirection == 0))
        {
            _yieldingTo = null;
        }

        // Also a car with other cars between it and the waiting car may decide to let it in: those cars pass the waiting car,
        // this one stops behind it. In a dense lane the first car without a car ahead is already too close to stop gently
        if (_yieldingTo != null || lookahead.GoAroundRequest is not { } requester) return;

        float gap = MathF.Max(0, lookahead.GoAroundRequestGap - GoAroundYieldExtraGapMeters);
        float acceleration = AccAcceleration(speed, gap, requester.CurrentSpeed, 0, freeAcceleration);
        if (acceleration < -_accComfortableDeceleration) return;

        _yieldingTo = requester;
        if (_trace.Enabled)
            _trace.Event("ga_yield", this, $"\"pt\":{CurrentSplinePointId},\"to\":{requester.TraceId},\"gap\":{AiTrace.F(lookahead.GoAroundRequestGap)},\"v\":{AiTrace.F(speed)},\"a\":{AiTrace.F(acceleration)}");
    }

    /// <summary>Stop further away from an obstacle to go around, to have room for the lane change</summary>
    private float ObstacleGap(float gap, object car)
        => car == _obstacleAhead ? MathF.Max(0, gap - _configuration.Extra.AiParams.Incidents.GoAroundExtraGapMeters) : gap;

    /// <summary>
    /// Distance along a lane change after which the car passes beside a car standing in the middle of the old lane: moved
    /// sideways by a car width (two half widths) plus a margin. The lateral profile is (1 - cos) over the length, see AdvanceLaneChange
    /// </summary>
    private float LaneChangeClearDistance(float laneDistance, float length)
    {
        if (laneDistance <= 0) return 0;
        float sideways = MathF.Min(laneDistance, 2 * _configuration.Extra.AiParams.Acc.PlayerHalfWidthMeters + GoAroundLateralMarginMeters);
        return MathF.Acos(1 - 2 * sideways / laneDistance) / MathF.PI * length;
    }

    /// <summary>Going around: the obstacle in the old lane is further than the distance left until the body leaves that lane</summary>
    private bool WillClearGoAroundObstacle(object car, float gap)
        => _lcGoAroundObstacle != null && car == _lcGoAroundObstacle
           && gap > LaneChangeClearDistance(_lcLaneDistance, _lcLength) - _lcTravelled + GoAroundClearMarginMeters;

    private void DetectObstaclesAcc()
    {
        var accParams = _configuration.Extra.AiParams.Acc;
        float speed = CurrentSpeed;
        float desiredSpeed = InitialMaxSpeed;

        var lookahead = AccLookahead(desiredSpeed);
        long now = _sessionManager.ServerTimeMilliseconds;
        bool crashed = IsCrashed;
        UpdateObstacleAhead(in lookahead, now);

        ClosestAiObstacleDistance = lookahead.Ai != null ? lookahead.AiGap : -1;
        TraceLeaderId = lookahead.Ai?.TraceId ?? -1;
        TracePlayerDistance = lookahead.Player != null ? lookahead.PlayerGap : -1;

        float freeAcceleration = FreeAcceleration(speed, desiredSpeed);
        float acceleration = freeAcceleration;
        float targetSpeed = desiredSpeed;
        var reason = AiTraceReason.Free;

        void Follow(float gap, float leaderSpeed, float leaderAcceleration, AiTraceReason followReason)
        {
            float followAcceleration = AccAcceleration(speed, gap, leaderSpeed, leaderAcceleration, freeAcceleration);
            if (followAcceleration < acceleration)
            {
                acceleration = followAcceleration;
                if (followAcceleration < freeAcceleration - AccFollowThreshold)
                {
                    reason = followReason;
                    targetSpeed = MathF.Min(targetSpeed, leaderSpeed);
                }
            }
        }

        if (lookahead.Ai is { } leader)
        {
            Follow(ObstacleGap(lookahead.AiGap, leader), leader.CurrentSpeed, leader.Acceleration, AiTraceReason.FollowAi);
        }

        if (lookahead.Player is { } player)
        {
            Follow(ObstacleGap(lookahead.PlayerGap, player.Car), player.Speed, player.Acceleration, AiTraceReason.FollowPlayer);
        }

        UpdateGoAroundYield(in lookahead, speed, freeAcceleration);
        if (_yieldingTo is { } requester)
        {
            float gap = lookahead.GoAroundRequest == requester ? lookahead.GoAroundRequestGap : MathF.Max(0, Vector3.Distance(Status.Position, requester.Status.Position)
                - LengthPre(EntryCar) - LengthPost(requester.EntryCar));
            Follow(MathF.Max(0, gap - GoAroundYieldExtraGapMeters), requester.CurrentSpeed, 0, AiTraceReason.FollowAi);
        }

        // Changing lanes: the body is still partly in the old lane, keep following its leader too
        if (ShadowSplinePointId is var shadowPointId and >= 0)
        {
            var shadow = AccLookahead(desiredSpeed, startPointId: shadowPointId);
            if (shadow.Ai is { } shadowLeader && !WillClearGoAroundObstacle(shadowLeader, shadow.AiGap))
            {
                Follow(shadow.AiGap, shadowLeader.CurrentSpeed, shadowLeader.Acceleration, AiTraceReason.FollowAi);
            }

            if (shadow.Player is { } shadowPlayer && !WillClearGoAroundObstacle(shadowPlayer.Car, shadow.PlayerGap))
            {
                Follow(shadow.PlayerGap, shadowPlayer.Speed, shadowPlayer.Acceleration, AiTraceReason.FollowPlayer);
            }
        }

        if (_merges.Enabled)
        {
            var merge = UpdateMerge(in lookahead, freeAcceleration);
            if (merge.Ai is { } mergeLeader)
            {
                Follow(merge.AiGap, mergeLeader.CurrentSpeed, mergeLeader.Acceleration, AiTraceReason.FollowAi);
            }

            if (merge.Player is { } mergePlayer)
            {
                Follow(merge.PlayerGap, mergePlayer.Speed, mergePlayer.Acceleration, AiTraceReason.FollowPlayer);
            }

            if (merge.YieldAcceleration < acceleration)
            {
                acceleration = merge.YieldAcceleration;
                reason = AiTraceReason.MergeYield;
            }
        }

        if (lookahead.CornerAcceleration < acceleration)
        {
            acceleration = lookahead.CornerAcceleration;
            reason = AiTraceReason.Corner;
        }
        targetSpeed = MathF.Min(targetSpeed, lookahead.CornerSpeed);

        if (crashed)
        {
            float crashDeceleration = MathF.Min(_configuration.Extra.AiParams.Incidents.CrashDecelerationMs2, EntryCar.AiDeceleration);
            acceleration = MathF.Min(acceleration, speed > 0 ? -crashDeceleration : 0);
            targetSpeed = 0;
            reason = AiTraceReason.CollisionStop;
        }
        else if (IncidentsEnabled && now < _touchBrakeUntil)
        {
            acceleration = MathF.Min(acceleration, -_configuration.Extra.AiParams.Incidents.TouchBrakeDecelerationMs2);
        }

        acceleration = Math.Clamp(acceleration, -EntryCar.AiDeceleration, _accMaxAcceleration);

        if (speed == 0)
        {
            if (acceleration < AccStandstillStartAcceleration)
            {
                acceleration = 0;
            }

            reason = reason switch
            {
                AiTraceReason.FollowAi => AiTraceReason.StopAi,
                AiTraceReason.FollowPlayer => AiTraceReason.StopPlayer,
                _ => reason
            };
        }

        if (acceleration < -accParams.BrakeLightsOnDecelerationMs2)
        {
            _accBrakeLightsOn = true;
        }
        else if (acceleration > -accParams.BrakeLightsOffDecelerationMs2)
        {
            _accBrakeLightsOn = false;
        }

        TraceReason = reason;
        // The stop after a crash doesn't count towards IgnoreObstaclesAfterSeconds
        if (!crashed)
        {
            UpdateStoppedForObstacle(reason);
        }

        MaxSpeed = desiredSpeed;
        TargetSpeed = targetSpeed;
        Acceleration = acceleration;
        _accIntegrate = true;

        if (_configuration.Extra.AiParams.LaneChanges.Enabled)
        {
            UpdateLaneChange();
        }
    }

    private struct MergeResult
    {
        public AiState? Ai;
        public float AiGap;
        public AiLanePlayer? Player;
        public float PlayerGap;
        // Merging car giving way: acceleration to arrive at the zone after the car of the main lane, float.MaxValue if none
        public float YieldAcceleration;
    }

    /// <summary>
    /// Merge zones (AiMerges). Cars of the twin lane are projected onto this lane by their distance to the zone entry:
    /// a car closer to the entry is ahead. A car in the lane that ends (Yield) picks a gap: it merges in front of the
    /// nearest car behind if that car doesn't have to brake too hard, otherwise it slows down or waits at the entry
    /// until the car has passed. A car in the lane that continues (Priority) sees a merging car only once it decided to
    /// merge in front or is already in the zone. At a diverge (Plain) cars of both lanes simply follow each other.
    /// </summary>
    private MergeResult UpdateMerge(in AccLookaheadResult lookahead, float freeAcceleration)
    {
        var result = new MergeResult { AiGap = float.MaxValue, PlayerGap = float.MaxValue, YieldAcceleration = float.MaxValue };
        int anchor = lookahead.MergeAnchor;
        int segment = _merges.SegmentOf(anchor);
        int otherAnchor = _merges.Twin(anchor);
        if (segment < 0 || otherAnchor < 0)
        {
            SetMergeState(MergeState.None, null, null, 0, 0, 0);
            _mergeSegment = -1;
            return result;
        }

        if (segment != _mergeSegment)
        {
            ResetMerge();
            _mergeSegment = segment;
        }

        var mergeParams = _configuration.Extra.AiParams.Merges;
        var accParams = _configuration.Extra.AiParams.Acc;
        var points = _spline.Points;
        var role = _merges.Segments[segment].Role;
        bool yieldLane = role == MergeRole.Yield;
        float speed = CurrentSpeed;
        float anchorDistance = lookahead.MergeAnchorDistance;
        bool checkPlayers = !ShouldIgnorePlayerObstacles();

        AiState? behindAi = null;
        AiLanePlayer? behindPlayer = null;
        float behindGap = float.MaxValue;
        float behindDistance = 0;

        // otherDistance: centre of the other car before the other anchor along its lane, negative = past it
        void Consider(AiState? ai, AiLanePlayer? player, float otherDistance)
        {
            var car = ai?.EntryCar ?? player!.Value.Car;
            float longitudinal = anchorDistance - otherDistance;
            if (longitudinal >= 0)
            {
                // Priority: a merging car that hasn't decided to go in front yet waits for us
                if (ai != null && role == MergeRole.Priority && ai._mergeState != MergeState.Go && otherDistance >= 0)
                    return;
                // A faster player alongside is passing, not leading
                if (player != null && player.Value.Speed > speed && longitudinal < LengthPre(EntryCar) + LengthPost(car))
                    return;

                float gap = MathF.Max(0, longitudinal - LengthPre(EntryCar) - LengthPost(car));
                if (ai != null && gap < result.AiGap)
                    (result.Ai, result.AiGap) = (ai, gap);
                else if (player != null && gap < result.PlayerGap)
                    (result.Player, result.PlayerGap) = (player, gap);
            }
            else if (yieldLane)
            {
                float gap = -longitudinal - LengthPost(EntryCar) - LengthPre(car);
                if (gap < behindGap)
                    (behindAi, behindPlayer, behindGap, behindDistance) = (ai, player, gap, otherDistance);
            }
        }

        void ScanPoint(int pointId, float distance, bool checkAi)
        {
            var ai = _spline.SlowestAiStates[pointId];
            if (checkAi && ai != null && ai != this && ai.Initialized)
            {
                float progress = ai.CurrentSplinePointId == pointId ? ai._currentVecProgress : 0;
                Consider(ai, null, distance - progress);
            }

            if (checkPlayers && _playerLanes.TryGet(pointId, out var player))
            {
                Consider(null, player, distance);
            }
        }

        // Ahead: the other lane inside the zone. Behind the zone the lanes either part or continue as this lane
        float maxAhead = MathF.Max(accParams.MinLookaheadMeters, speed * accParams.LookaheadSeconds);
        int pointId = otherAnchor;
        float otherDistance = 0;
        while (pointId >= 0 && -otherDistance < maxAhead && (pointId == otherAnchor || _merges.SegmentOf(pointId) >= 0))
        {
            ScanPoint(pointId, otherDistance, true);
            otherDistance -= points[pointId].Length;
            pointId = points[pointId].NextId;
        }

        // Behind: cars closer to the entry than this car are ahead of it. The merging car also needs the car behind
        float aiBack = anchorDistance + (yieldLane ? mergeParams.AiLookbackMeters : 0);
        float maxBack = MathF.Max(aiBack, yieldLane && checkPlayers ? anchorDistance + mergeParams.PlayerLookbackMeters : 0);
        pointId = otherAnchor;
        otherDistance = 0;
        while (true)
        {
            int previousId = points[pointId].PreviousId;
            if (previousId < 0) break;
            otherDistance += points[previousId].Length;
            pointId = previousId;
            if (otherDistance > maxBack) break;
            ScanPoint(pointId, otherDistance, otherDistance <= aiBack);
        }

        if (!yieldLane)
        {
            SetMergeState(MergeState.Approach, null, null, anchorDistance, 0, 0);
            return result;
        }

        // In the zone: committed
        if (anchorDistance <= 0)
        {
            SetMergeState(MergeState.Go, _mergeFollowerAi, _mergeFollowerPlayer, anchorDistance, 0, 0);
            return result;
        }

        // Another car of this lane is closer to the entry: it merges first
        if (lookahead.Ai is { } ownLeader && lookahead.AiGap + LengthPre(EntryCar) + LengthPost(ownLeader.EntryCar) < anchorDistance)
        {
            SetMergeState(MergeState.Queue, null, null, anchorDistance, 0, 0);
            return result;
        }

        long now = _sessionManager.ServerTimeMilliseconds;
        if (_mergeState == MergeState.Yield && speed < 1)
        {
            if (_mergeWaitStart == 0) _mergeWaitStart = now;
        }
        else
        {
            _mergeWaitStart = 0;
        }
        float waited = _mergeWaitStart > 0 ? (now - _mergeWaitStart) / 1000.0f : 0;
        float impatience = Math.Clamp((waited - mergeParams.PatienceSeconds) / mergeParams.PatienceRampSeconds, 0, 1);

        bool hasBehind = behindAi != null || behindPlayer != null;
        bool keepGoing = _mergeState == MergeState.Go
                         && (behindAi != null ? behindAi == _mergeFollowerAi : behindPlayer?.Car == _mergeFollowerPlayer);
        bool safe = true;
        float behindAcceleration = 0;
        if (hasBehind)
        {
            float safeDeceleration, minGap;
            if (behindAi != null)
            {
                float free = behindAi.FreeAcceleration(behindAi.CurrentSpeed, behindAi.InitialMaxSpeed);
                behindAcceleration = MathF.Min(free, behindAi.AccAcceleration(behindAi.CurrentSpeed, behindGap, speed, Acceleration, free));
                safeDeceleration = mergeParams.SafeDecelerationMs2 + impatience * (mergeParams.AbortDecelerationMs2 - mergeParams.SafeDecelerationMs2);
                minGap = behindAi._accStandstillGap + behindAi.CurrentSpeed * mergeParams.MinGapSeconds;
            }
            else
            {
                var player = behindPlayer!.Value;
                behindAcceleration = MathF.Min(0, AccAccelerationCore(PlayerModelAcceleration, PlayerModelDeceleration, PlayerModelStandstillGap, PlayerModelTimeHeadway,
                    PlayerModelMaxDeceleration, accParams.Coolness, player.Speed, behindGap, speed, Acceleration, 0));
                safeDeceleration = _lcAggressive ? mergeParams.AggressivePlayerSafeDecelerationMs2 : mergeParams.PlayerSafeDecelerationMs2;
                minGap = PlayerModelStandstillGap + player.Speed * (_lcAggressive ? mergeParams.AggressivePlayerMinGapSeconds : mergeParams.PlayerMinGapSeconds);
            }

            safe = keepGoing
                ? behindAcceleration >= -mergeParams.AbortDecelerationMs2
                : behindAcceleration >= -safeDeceleration && behindGap >= minGap;
        }

        // Merging in front must not mean braking hard behind the car ahead in the other lane
        if (safe && !keepGoing)
        {
            float ownAcceleration = freeAcceleration;
            if (result.Ai is { } leaderAi)
                ownAcceleration = MathF.Min(ownAcceleration, AccAcceleration(speed, result.AiGap, leaderAi.CurrentSpeed, leaderAi.Acceleration, freeAcceleration));
            if (result.Player is { } leaderPlayer)
                ownAcceleration = MathF.Min(ownAcceleration, AccAcceleration(speed, result.PlayerGap, leaderPlayer.Speed, leaderPlayer.Acceleration, freeAcceleration));
            if (ownAcceleration < -mergeParams.SafeDecelerationMs2)
                safe = false;
        }

        if (safe)
        {
            SetMergeState(MergeState.Go, behindAi, behindPlayer?.Car, anchorDistance, hasBehind ? behindGap : -1, behindAcceleration);
            return result;
        }

        SetMergeState(MergeState.Yield, behindAi, behindPlayer?.Car, anchorDistance, hasBehind ? behindGap : -1, behindAcceleration);

        // Nobody behind to let pass, only the car ahead is too close: it is followed like any leader
        if (!hasBehind) return result;

        // Arrive at the entry when the car behind has passed it with a gap: slow down just enough, or stop at the entry
        var behindCar = behindAi?.EntryCar ?? behindPlayer!.Value.Car;
        float behindSpeed = behindAi?.CurrentSpeed ?? behindPlayer!.Value.Speed;
        float passDistance = behindDistance + LengthPost(behindCar) + LengthPre(EntryCar) + _accStandstillGap + speed * mergeParams.MinGapSeconds;
        float stopAcceleration = AccAcceleration(speed, MathF.Max(anchorDistance - LengthPre(EntryCar), 0), 0, 0, freeAcceleration);
        if (behindSpeed < 1)
        {
            result.YieldAcceleration = stopAcceleration;
        }
        else
        {
            float passTime = passDistance / behindSpeed;
            if (speed * passTime > anchorDistance)
            {
                float yieldAcceleration = -2 * (speed * passTime - anchorDistance) / (passTime * passTime);
                result.YieldAcceleration = speed + yieldAcceleration * passTime < 0 ? stopAcceleration : yieldAcceleration;
            }
        }

        return result;
    }

    private void SetMergeState(MergeState state, AiState? followerAi, EntryCar? followerPlayer, float anchorDistance, float followerGap, float followerAcceleration)
    {
        bool changed = state != _mergeState || followerAi != _mergeFollowerAi || followerPlayer != _mergeFollowerPlayer;
        var previous = _mergeState;
        _mergeState = state;
        _mergeFollowerAi = followerAi;
        _mergeFollowerPlayer = followerPlayer;

        if (!changed || !_trace.Enabled) return;
        if (state is MergeState.None or MergeState.Approach && previous is MergeState.None or MergeState.Approach) return;

        string name = state switch
        {
            MergeState.Go => "go",
            MergeState.Yield => "yield",
            MergeState.Queue => "queue",
            _ => "done"
        };
        _trace.Event("merge", this, $"\"st\":\"{name}\",\"seg\":{_mergeSegment},\"pt\":{CurrentSplinePointId},\"d\":{AiTrace.F(anchorDistance)},\"v\":{AiTrace.F(CurrentSpeed)},\"fol\":{followerAi?.TraceId ?? -1},\"fpl\":{followerPlayer?.SessionId ?? -1},\"fgap\":{AiTrace.F(followerGap)},\"fa\":{AiTrace.F(followerAcceleration)},\"aggr\":{(_lcAggressive ? 1 : 0)}");
    }

    private int SlowLaneDirection =>_configuration.Extra.AiParams.RightLaneOffsetKph >= 0 ? -1 : 1;

    private bool JunctionIndicatorActive => _nextJunctionId >= 0 || _junctionPassed;

    /// <summary>A player is closer than <paramref name="maxDistance"/>, or the distance is not limited (0)</summary>
    private bool IsNearPlayer(float maxDistance)
    {
        if (maxDistance <= 0) return true;

        float maxDistanceSquared = maxDistance * maxDistance;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (!car.AiControlled && car.Client?.HasSentFirstUpdate == true
                && Vector3.DistanceSquared(car.Status.Position, Status.Position) < maxDistanceSquared)
                return true;
        }

        return false;
    }

    /// <summary>Neighbour lane point in the same direction, -1 if none. Direction -1 = left (LeftId), 1 = right (RightId)</summary>
    private int GetNeighbor(int pointId, int direction)
    {
        if (pointId < 0) return -1;
        ref readonly var point = ref _spline.Points[pointId];
        int neighborId = direction < 0 ? point.LeftId : point.RightId;
        return neighborId >= 0 && _spline.Operations.IsSameDirection(pointId, neighborId) ? neighborId : -1;
    }

    private float LaneChangeLength(float speed) => MathF.Max(_obstacleAhead != null
        ? _configuration.Extra.AiParams.Incidents.GoAroundMinLengthMeters
        : _configuration.Extra.AiParams.LaneChanges.MinLengthMeters, speed * _lcDurationSeconds);

    /// <summary>The lane continues for this distance without junctions and without a change of the lane count</summary>
    private bool IsLaneClear(int pointId, float distance, int laneCount)
    {
        var points = _spline.Points;
        float travelled = 0;
        while (travelled < distance)
        {
            ref readonly var point = ref points[pointId];
            if (point.JunctionStartId >= 0 || point.JunctionEndId >= 0 || point.NextId < 0 || _spline.GetLanes(pointId).Length != laneCount
                || _merges.SegmentOf(pointId) >= 0)
                return false;

            travelled += point.Length;
            pointId = point.NextId;
        }

        return true;
    }

    private struct LaneScan
    {
        public bool Blocked;
        public AiState? LeaderAi;
        public float LeaderAiGap;
        public AiLanePlayer? LeaderPlayer;
        public float LeaderPlayerGap;
        public AiState? FollowerAi;
        public AiLanePlayer? FollowerPlayer;
        public float FollowerGap;
    }

    /// <summary>
    /// Nearest cars ahead of and behind this car in the lane of <paramref name="lanePointId"/>, a point next to this car.
    /// Gaps are bumper to bumper along the lane. A car overlapping this car lengthwise blocks the lane.
    /// Only the nearest car behind is kept, AI or player: a player behind an AI car is not affected by a lane change.
    /// </summary>
    private LaneScan ScanLane(int lanePointId, float lookahead)
    {
        var lcParams = _configuration.Extra.AiParams.LaneChanges;
        var points = _spline.Points;
        var scan = new LaneScan { LeaderAiGap = float.MaxValue, LeaderPlayerGap = float.MaxValue, FollowerGap = float.MaxValue };
        bool checkPlayers = !ShouldIgnorePlayerObstacles();
        var forward = Vector3.Normalize(_spline.Operations.GetForwardVector(lanePointId));

        int pointId = lanePointId;
        float travelled = 0;
        while (pointId >= 0 && travelled < lookahead && (scan.LeaderAi == null || (checkPlayers && scan.LeaderPlayer == null)))
        {
            ScanPoint(pointId, forward, true, checkPlayers, ref scan);
            travelled += points[pointId].Length;
            pointId = _junctionEvaluator.Next(pointId);
        }

        float lookback = checkPlayers ? MathF.Max(lcParams.AiLookbackMeters, lcParams.PlayerLookbackMeters) : lcParams.AiLookbackMeters;
        pointId = points[lanePointId].PreviousId;
        travelled = 0;
        while (pointId >= 0 && travelled < lookback && scan.FollowerAi == null && scan.FollowerPlayer == null)
        {
            travelled += points[pointId].Length;
            ScanPoint(pointId, forward, travelled < lcParams.AiLookbackMeters, checkPlayers, ref scan);
            pointId = points[pointId].PreviousId;
        }

        return scan;
    }

    private void ScanPoint(int pointId, Vector3 forward, bool checkAi, bool checkPlayers, ref LaneScan scan)
    {
        var ai = _spline.SlowestAiStates[pointId];
        if (checkAi && ai != null && ai != this && ai.Initialized)
        {
            ClassifyCar(ai.Status.Position, LengthPre(ai.EntryCar), LengthPost(ai.EntryCar), forward, ai, null, ref scan);
        }

        if (checkPlayers && _playerLanes.TryGet(pointId, out var player) && MathF.Abs(player.Car.Status.Position.Y - Status.Position.Y) < 1.5f)
        {
            ClassifyCar(player.Car.Status.Position, LengthPre(player.Car), LengthPost(player.Car), forward, null, player, ref scan);
        }
    }

    private void ClassifyCar(Vector3 position, float lengthPre, float lengthPost, Vector3 forward, AiState? ai, AiLanePlayer? player, ref LaneScan scan)
    {
        float longitudinal = Vector3.Dot(position - Status.Position, forward);
        if (longitudinal >= 0)
        {
            float gap = longitudinal - LengthPre(EntryCar) - lengthPost;
            if (gap < 0)
                scan.Blocked = true;
            else if (ai != null && gap < scan.LeaderAiGap)
                (scan.LeaderAi, scan.LeaderAiGap) = (ai, gap);
            else if (player != null && gap < scan.LeaderPlayerGap)
                (scan.LeaderPlayer, scan.LeaderPlayerGap) = (player, gap);
        }
        else
        {
            float gap = -longitudinal - LengthPost(EntryCar) - lengthPre;
            if (gap < 0)
                scan.Blocked = true;
            else if (gap < scan.FollowerGap)
                (scan.FollowerAi, scan.FollowerPlayer, scan.FollowerGap) = (ai, player, gap);
        }
    }

    /// <summary>Acc acceleration of this car behind the leaders of a scanned lane</summary>
    private float AccBehind(in LaneScan scan, float freeAcceleration)
    {
        float acceleration = freeAcceleration;
        if (scan.LeaderAi is { } ai)
            acceleration = MathF.Min(acceleration, AccAcceleration(CurrentSpeed, scan.LeaderAiGap, ai.CurrentSpeed, ai.Acceleration, freeAcceleration));
        if (scan.LeaderPlayer is { } player)
            acceleration = MathF.Min(acceleration, AccAcceleration(CurrentSpeed, scan.LeaderPlayerGap, player.Speed, player.Acceleration, freeAcceleration));
        return acceleration;
    }

    /// <summary>Nearest leader of a scanned lane: gap, speed, acceleration, trace id (-1 for a player)</summary>
    private static (bool Found, float Gap, float Speed, float Acceleration, int Id) NearestLeader(in LaneScan scan)
    {
        if (scan.LeaderAi is { } ai && scan.LeaderAiGap <= scan.LeaderPlayerGap)
            return (true, scan.LeaderAiGap, ai.CurrentSpeed, ai.Acceleration, ai.TraceId);
        if (scan.LeaderPlayer is { } player)
            return (true, scan.LeaderPlayerGap, player.Speed, player.Acceleration, -1);
        return (false, 0, 0, 0, -1);
    }

    /// <summary>Acceleration of the car behind (AI or player) following a leader at the given gap, or on a free road</summary>
    private float FollowerAcceleration(in LaneScan scan, float gap, bool hasLeader, float leaderSpeed, float leaderAcceleration)
    {
        if (scan.FollowerAi is { } ai)
        {
            float free = ai.FreeAcceleration(ai.CurrentSpeed, ai.InitialMaxSpeed);
            return hasLeader ? MathF.Min(free, ai.AccAcceleration(ai.CurrentSpeed, gap, leaderSpeed, leaderAcceleration, free)) : free;
        }

        // A player is assumed to keep its speed, the reference driver only tells how hard it would have to brake
        var player = scan.FollowerPlayer!.Value;
        if (!hasLeader) return 0;
        return MathF.Min(0, AccAccelerationCore(PlayerModelAcceleration, PlayerModelDeceleration, PlayerModelStandstillGap, PlayerModelTimeHeadway,
            PlayerModelMaxDeceleration, _configuration.Extra.AiParams.Acc.Coolness, player.Speed, gap, leaderSpeed, leaderAcceleration, 0));
    }

    private struct LaneChangeEvaluation
    {
        public int TargetPointId;
        public bool Safe;
        public string Why;
        // Incentive minus threshold, > 0 = the lane change is worth it
        public float Margin;
        // Own acceleration advantage
        public float Gain;
        public int LeaderId;
        public float LeaderGap;
        public int FollowerId;
        public int FollowerPlayer;
        public float FollowerGap;
        public float FollowerAcceleration;
    }

    /// <summary>
    /// MOBIL (Kesting, Treiber, Helbing 2007) with the Acc accelerations. False if there is no lane to change to.
    /// Safe: the new car behind doesn't have to brake too hard and all gaps are long enough.
    /// Margin: own advantage plus politeness times the advantage of the old and new cars behind, minus the threshold,
    /// the threshold is lower towards the slow lane.
    /// </summary>
    private bool TryEvaluateLaneChange(int direction, out LaneChangeEvaluation eval)
    {
        var lcParams = _configuration.Extra.AiParams.LaneChanges;
        var accParams = _configuration.Extra.AiParams.Acc;
        eval = new LaneChangeEvaluation { Safe = true, Why = "", LeaderId = -1, LeaderGap = -1, FollowerId = -1, FollowerPlayer = -1, FollowerGap = -1 };

        int currentPointId = CurrentSplinePointId;
        int targetPointId = GetNeighbor(currentPointId, direction);
        if (targetPointId < 0 || !IsAllowedLane(in _spline.Points[targetPointId]))
            return false;
        // Don't change into an on-ramp or a lane that ends soon
        if (_merges.HasYieldAhead(_spline, targetPointId, _configuration.Extra.AiParams.Merges.NoLaneChangeIntoMergeMeters))
            return false;

        float speed = CurrentSpeed;
        int laneCount = _spline.GetLanes(currentPointId).Length;
        float clearance = LaneChangeLength(speed) + lcParams.ClearanceMeters;
        if (!IsLaneClear(currentPointId, clearance, laneCount) || !IsLaneClear(targetPointId, clearance, laneCount))
            return false;

        eval.TargetPointId = targetPointId;
        float lookahead = MathF.Max(accParams.MinLookaheadMeters, speed * accParams.LookaheadSeconds);
        var own = ScanLane(currentPointId, lookahead);
        var target = ScanLane(targetPointId, lookahead);
        float freeAcceleration = FreeAcceleration(speed, InitialMaxSpeed);
        float length = LengthPre(EntryCar) + LengthPost(EntryCar);

        float changedAcceleration = AccBehind(target, freeAcceleration);
        float ownAcceleration = AccBehind(own, freeAcceleration);
        // Going around: the own lane is blocked, whatever the gap to the obstacle
        if (_obstacleAhead != null) ownAcceleration = MathF.Min(ownAcceleration, -_accComfortableDeceleration);
        eval.Gain = changedAcceleration - ownAcceleration;
        // Going around: only safety counts, not the disadvantage of the cars behind, and no bias towards the slow lane.
        // Slow drivers don't move over for faster cars either: queues form behind them
        float politeness = _obstacleAhead != null || _accSlowDriver ? 0 : _lcPoliteness;

        bool safe = true;
        string why = "";
        void Unsafe(string reason)
        {
            if (!safe) return;
            safe = false;
            why = reason;
        }

        if (target.Blocked) Unsafe("blocked");
        if (changedAcceleration < -lcParams.SafeDecelerationMs2) Unsafe("own_decel");

        var leader = NearestLeader(target);
        if (leader.Found)
        {
            eval.LeaderId = leader.Id;
            eval.LeaderGap = leader.Gap;
            if (leader.Gap < _accStandstillGap + speed * lcParams.MinGapSeconds) Unsafe("leader_gap");
        }

        float incentive = eval.Gain;

        // New car behind: safety, and its disadvantage
        if (target.FollowerAi != null || target.FollowerPlayer != null)
        {
            bool isPlayer = target.FollowerAi == null;
            eval.FollowerId = target.FollowerAi?.TraceId ?? -1;
            eval.FollowerPlayer = target.FollowerPlayer?.Car.SessionId ?? -1;
            eval.FollowerGap = target.FollowerGap;

            float after = FollowerAcceleration(target, target.FollowerGap, true, speed, Acceleration);
            float before = FollowerAcceleration(target, target.FollowerGap + length + leader.Gap, leader.Found, leader.Speed, leader.Acceleration);
            eval.FollowerAcceleration = after;

            float safeDeceleration, minGap;
            if (isPlayer)
            {
                float playerSpeed = target.FollowerPlayer!.Value.Speed;
                safeDeceleration = _lcAggressive ? lcParams.AggressivePlayerSafeDecelerationMs2 : lcParams.PlayerSafeDecelerationMs2;
                minGap = PlayerModelStandstillGap + playerSpeed * (_lcAggressive ? lcParams.AggressivePlayerMinGapSeconds : lcParams.PlayerMinGapSeconds);
            }
            else
            {
                var followerAi = target.FollowerAi!;
                safeDeceleration = lcParams.SafeDecelerationMs2;
                minGap = followerAi._accStandstillGap + followerAi.CurrentSpeed * lcParams.MinGapSeconds;
            }

            if (after < -safeDeceleration) Unsafe("follower_decel");
            if (target.FollowerGap < minGap) Unsafe("follower_gap");

            incentive += (isPlayer && _lcAggressive ? 0 : politeness) * (after - before);
        }

        // Old car behind: its advantage when this car leaves
        if (own.FollowerAi != null || own.FollowerPlayer != null)
        {
            bool isPlayer = own.FollowerAi == null;
            var ownLeader = NearestLeader(own);
            float before = FollowerAcceleration(own, own.FollowerGap, true, speed, Acceleration);
            float after = FollowerAcceleration(own, own.FollowerGap + length + ownLeader.Gap, ownLeader.Found, ownLeader.Speed, ownLeader.Acceleration);
            incentive += (isPlayer && _lcAggressive ? 0 : politeness) * (after - before);
        }

        if (_obstacleAhead != null)
        {
            incentive += _configuration.Extra.AiParams.Incidents.GoAroundIncentiveMs2;
            // The body must leave the lane before reaching the obstacle
            float laneDistance = Vector3.Distance(_spline.Points[currentPointId].Position, _spline.Points[targetPointId].Position);
            if (_obstacleGap < LaneChangeClearDistance(laneDistance, LaneChangeLength(speed)) + GoAroundClearMarginMeters) Unsafe("obstacle_gap");
        }

        float threshold = _obstacleAhead != null || _accSlowDriver
            ? lcParams.ThresholdMs2
            : lcParams.ThresholdMs2 + (direction == SlowLaneDirection ? -lcParams.SlowLaneBiasMs2 : lcParams.SlowLaneBiasMs2);
        eval.Margin = incentive - threshold;
        eval.Safe = safe;
        eval.Why = why;
        return true;
    }

    /// <summary>Lane change state machine, called from DetectObstaclesAcc: decide, indicate, wait for a gap, check for an abort</summary>
    private void UpdateLaneChange()
    {
        var lcParams = _configuration.Extra.AiParams.LaneChanges;
        long now = _sessionManager.ServerTimeMilliseconds;
        // Going around an obstacle: also at low speed, without cooldown, and waiting for a gap with the indicator on
        bool goAround = _obstacleAhead != null;

        if (IsCrashed)
        {
            if (_lcPhase == LaneChangePhase.Indicating) CancelLaneChange(now, "crash");
            if (_lcPhase != LaneChangePhase.Moving) return;
        }

        switch (_lcPhase)
        {
            case LaneChangePhase.None:
            {
                if (now < _lcNextDecision) return;
                _lcNextDecision = now + lcParams.DecisionIntervalMilliseconds;
                if (JunctionIndicatorActive) return;
                if (!goAround && (now < _lcCooldownUntil || CurrentSpeed < LaneChangeMinSpeed || !IsNearPlayer(lcParams.MaxPlayerDistanceMeters))) return;

                LaneChangeEvaluation best = default;
                int bestDirection = 0;
                for (int direction = -1; direction <= 1; direction += 2)
                {
                    if (TryEvaluateLaneChange(direction, out var eval) && (eval.Safe || goAround) && eval.Margin > 0
                        && (bestDirection == 0 || (eval.Safe && !best.Safe) || (eval.Safe == best.Safe && eval.Margin > best.Margin)))
                    {
                        best = eval;
                        bestDirection = direction;
                    }
                }

                if (bestDirection == 0) return;

                _lcPhase = LaneChangePhase.Indicating;
                _lcDirection = bestDirection;
                _lcMoveAt = now + (long)(_lcIndicatorSeconds * 1000);
                if (_trace.Enabled)
                    _trace.Event("lc_signal", this, $"\"dir\":{bestDirection},\"pt\":{CurrentSplinePointId},\"margin\":{AiTrace.F(best.Margin)},\"gain\":{AiTrace.F(best.Gain)},\"aggr\":{(_lcAggressive ? 1 : 0)}");
                break;
            }
            case LaneChangePhase.Indicating:
            {
                if (now < _lcMoveAt) return;

                if (JunctionIndicatorActive)
                {
                    CancelLaneChange(now, "junction");
                }
                else if (!TryEvaluateLaneChange(_lcDirection, out var eval))
                {
                    CancelLaneChange(now, "lane");
                }
                else if (eval.Margin <= -lcParams.ThresholdMs2)
                {
                    CancelLaneChange(now, "incentive");
                }
                else if (eval.Safe && (CurrentSpeed >= LaneChangeMinSpeed || goAround))
                {
                    StartLaneChange(in eval, now);
                }
                else if (!goAround && now > _lcMoveAt + (long)(lcParams.MaxWaitSeconds * 1000))
                {
                    CancelLaneChange(now, eval.Safe ? "slow" : eval.Why);
                }
                break;
            }
            case LaneChangePhase.Moving:
                if (IncidentsEnabled && now < _stoppedForCollisionUntil)
                {
                    // Stops across the lanes, where to go is decided when the crash stop is over
                    if (_lcCrashHoldSince == 0) _lcCrashHoldSince = now;
                    break;
                }

                if (_lcCrashHoldSince > 0 && !ResumeAfterCrash(now)) break;
                CheckLaneChangeAbort();
                break;
        }
    }

    private void CancelLaneChange(long now, string why)
    {
        if (_trace.Enabled)
            _trace.Event("lc_cancel", this, $"\"dir\":{_lcDirection},\"pt\":{CurrentSplinePointId},\"why\":\"{why}\"");

        _lcPhase = LaneChangePhase.None;
        _lcNextDecision = now + (long)(_configuration.Extra.AiParams.LaneChanges.CancelCooldownSeconds * 1000);
    }

    private (Vector3 Position, Vector3 Tangent) EvaluateSpline()
    {
        var ops = _spline.Operations;
        if (!_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPointId))
            return (ops.Points[CurrentSplinePointId].Position, ops.GetForwardVector(CurrentSplinePointId));

        var point = CatmullRom.Evaluate(ops.Points[CurrentSplinePointId].Position, ops.Points[nextPointId].Position, _startTangent, _endTangent,
            _currentVecProgress / _currentVecLength);
        return (point.Position, point.Tangent);
    }

    /// <summary>Horizontal unit vector across the lane. Only used to apply the lateral offset, so the side doesn't matter</summary>
    private static Vector3 LateralNormal(Vector3 tangent)
    {
        var normal = new Vector3(-tangent.Z, 0, tangent.X);
        float length = normal.Length();
        return length > 1e-6f ? normal / length : Vector3.Zero;
    }

    /// <summary>
    /// Moves the car to the lane of <paramref name="targetPointId"/> at the same place along the road: finds the segment of the
    /// target lane the position projects onto. Returns the lateral offset of the position from the target lane.
    /// Must be called with _moveLock held.
    /// </summary>
    private float SwitchLane(int targetPointId, Vector3 position)
    {
        var points = _spline.Points;
        int pointId = targetPointId;
        float progress = 0, length = 1;
        for (int i = 0; i < 16; i++)
        {
            int nextPointId = _junctionEvaluator.Next(pointId);
            if (nextPointId < 0) break;

            var segment = points[nextPointId].Position - points[pointId].Position;
            length = MathF.Max(segment.Length(), 0.01f);
            progress = Vector3.Dot(position - points[pointId].Position, segment) / length;
            if (progress > length)
            {
                pointId = nextPointId;
                continue;
            }

            if (progress < 0 && points[pointId].PreviousId >= 0 && i < 15)
            {
                pointId = points[pointId].PreviousId;
                continue;
            }

            break;
        }

        CurrentSplinePointId = pointId;
        _currentVecLength = length;
        _currentVecProgress = Math.Clamp(progress, 0, length);
        CalculateTangents();

        var (lanePosition, tangent) = EvaluateSpline();
        return Vector3.Dot(position - lanePosition, LateralNormal(tangent));
    }

    private void StartLaneChange(in LaneChangeEvaluation eval, long now)
    {
        float offset;
        int fromPointId;
        lock (_moveLock)
        {
            if (!Initialized) return;

            fromPointId = CurrentSplinePointId;
            var (position, tangent) = EvaluateSpline();
            offset = SwitchLane(eval.TargetPointId, position + LateralNormal(tangent) * _lateralOffset);

            _lcPhase = LaneChangePhase.Moving;
            _lcStartedAt = now;
            _lcStartOffset = offset;
            _lcLaneDistance = MathF.Abs(offset);
            _lcLength = LaneChangeLength(CurrentSpeed);
            _lcTravelled = 0;
            _lcAborted = false;
            _lateralOffset = offset;
            _lateralSlope = 0;
            _lateralCurvature = 0;
            _lcGoAroundObstacle = _obstacleAhead;
            int shadowPointId = GetNeighbor(CurrentSplinePointId, -_lcDirection);
            ShadowSplinePointId = shadowPointId >= 0 ? shadowPointId : fromPointId;
        }

        if (_trace.Enabled)
            _trace.Event("lc_start", this, $"\"dir\":{_lcDirection},\"pt\":{fromPointId},\"to\":{CurrentSplinePointId},\"margin\":{AiTrace.F(eval.Margin)},\"gain\":{AiTrace.F(eval.Gain)},\"lead\":{eval.LeaderId},\"lgap\":{AiTrace.F(eval.LeaderGap)},\"fol\":{eval.FollowerId},\"fpl\":{eval.FollowerPlayer},\"fgap\":{AiTrace.F(eval.FollowerGap)},\"fa\":{AiTrace.F(eval.FollowerAcceleration)},\"len\":{AiTrace.F(_lcLength)},\"off\":{AiTrace.F(offset)},\"aggr\":{(_lcAggressive ? 1 : 0)},\"ga\":{(_lcGoAroundObstacle != null ? 1 : 0)}");
    }

    // The car body is in a lane while its centre is closer than this to the lane centre
    private float LaneChangeOccupiedHalfWidth => _configuration.Extra.AiParams.LaneWidthMeters / 2 + _configuration.Extra.AiParams.Acc.PlayerHalfWidthMeters;

    /// <summary>
    /// In the first half of a lane change: go back if the car behind in the new lane would have to brake hard
    /// (usually a player accelerating into the same gap) or a car got alongside. The way back starts with the current
    /// lateral speed and acceleration and takes AbortReturnSeconds.
    /// </summary>
    private void CheckLaneChangeAbort()
    {
        if (_lcAborted || _lcTravelled > _lcLength / 2) return;

        var lcParams = _configuration.Extra.AiParams.LaneChanges;
        var scan = ScanLane(CurrentSplinePointId, 10);
        float followerAcceleration = 0;
        if (scan.FollowerAi != null || scan.FollowerPlayer != null)
        {
            followerAcceleration = FollowerAcceleration(scan, scan.FollowerGap, true, CurrentSpeed, Acceleration);
        }

        if (!scan.Blocked && followerAcceleration >= -lcParams.AbortDecelerationMs2) return;

        float progress = _lcTravelled / _lcLength;
        if (!StartLaneChangeReturn(lcParams.MinLengthMeters)) return;

        if (_trace.Enabled)
            _trace.Event("lc_abort", this, $"\"pt\":{CurrentSplinePointId},\"why\":\"{(scan.Blocked ? "blocked" : "decel")}\",\"fa\":{AiTrace.F(followerAcceleration)},\"u\":{AiTrace.F(progress)},\"fol\":{scan.FollowerAi?.TraceId ?? -1},\"fpl\":{scan.FollowerPlayer?.Car.SessionId ?? -1}");
    }

    /// <summary>
    /// Incidents: crashed during a lane change and the crash stop is over. Go on into the target lane if the car behind
    /// there doesn't have to brake too hard for a car starting from here, otherwise back into the old lane if that is safe,
    /// otherwise keep standing with hazards. After GoAroundTimeoutSeconds go on anyway. True = move on.
    /// </summary>
    private bool ResumeAfterCrash(long now)
    {
        string how;
        if (IsSafeToMoveInto(CurrentSplinePointId))
        {
            how = "continue";
        }
        else if (!_lcAborted && GetNeighbor(CurrentSplinePointId, -_lcDirection) is var backPointId and >= 0 && IsSafeToMoveInto(backPointId)
                 && StartLaneChangeReturn(_configuration.Extra.AiParams.Incidents.GoAroundMinLengthMeters))
        {
            how = "back";
        }
        else if (now - _lcCrashHoldSince > (long)(_configuration.Extra.AiParams.Incidents.GoAroundTimeoutSeconds * 1000))
        {
            how = "timeout";
        }
        else
        {
            return false;
        }

        if (_trace.Enabled)
            _trace.Event("lc_resume", this, $"\"pt\":{CurrentSplinePointId},\"how\":\"{how}\",\"waitMs\":{now - _lcCrashHoldSince}");
        _lcCrashHoldSince = 0;
        return true;
    }

    /// <summary>The lane of <paramref name="pointId"/> is free alongside, and the car behind there doesn't have to brake hard for this car</summary>
    private bool IsSafeToMoveInto(int pointId)
    {
        var lcParams = _configuration.Extra.AiParams.LaneChanges;
        var scan = ScanLane(pointId, 10);
        if (scan.Blocked) return false;
        if (scan.FollowerAi == null && scan.FollowerPlayer == null) return true;

        float safeDeceleration = scan.FollowerAi != null ? lcParams.SafeDecelerationMs2 : lcParams.PlayerSafeDecelerationMs2;
        return FollowerAcceleration(scan, scan.FollowerGap, true, CurrentSpeed, 0) >= -safeDeceleration;
    }

    /// <summary>
    /// Turns the lane change around: back into the old lane, starting with the current lateral speed and acceleration.
    /// False if there is no old lane.
    /// </summary>
    private bool StartLaneChangeReturn(float minLength)
    {
        var lcParams = _configuration.Extra.AiParams.LaneChanges;
        int backPointId = GetNeighbor(CurrentSplinePointId, -_lcDirection);
        if (backPointId < 0) return false;

        lock (_moveLock)
        {
            if (!Initialized || _lcPhase != LaneChangePhase.Moving) return false;

            int fromPointId = CurrentSplinePointId;
            var (position, tangent) = EvaluateSpline();
            // Leave the shadow first: it is in the lane the car switches back to
            ShadowSplinePointId = -1;
            float offset = SwitchLane(backPointId, position + LateralNormal(tangent) * _lateralOffset);

            // Both lanes run in the same direction, so the lateral speed and acceleration keep their sign in the new frame
            float length = MathF.Max(minLength, CurrentSpeed * lcParams.AbortReturnSeconds);
            float p0 = offset + _lateralSlope * length + _lateralCurvature * length * length / 2;
            float p1 = _lateralSlope + _lateralCurvature * length;
            float p2 = _lateralCurvature;
            _lcReturnY0 = offset;
            _lcReturnY1 = _lateralSlope;
            _lcReturnY2 = _lateralCurvature;
            _lcReturnC3 = (-10 * p0 + 4 * p1 * length - p2 * length * length / 2) / (length * length * length);
            _lcReturnC4 = (15 * p0 - 7 * p1 * length + p2 * length * length) / (length * length * length * length);
            _lcReturnC5 = (-6 * p0 + 3 * p1 * length - p2 * length * length / 2) / (length * length * length * length * length);

            _lcDirection = -_lcDirection;
            _lcStartOffset = offset;
            _lcLength = length;
            _lcTravelled = 0;
            _lcAborted = true;
            _lateralOffset = offset;
            ShadowSplinePointId = MathF.Abs(offset) > _lcLaneDistance - LaneChangeOccupiedHalfWidth ? fromPointId : -1;
        }

        return true;
    }

    /// <summary>Lateral movement of a lane change, called from Update() with _moveLock held</summary>
    private void AdvanceLaneChange(float moveMeters)
    {
        _lcTravelled += moveMeters;
        float progress = MathF.Min(1, _lcTravelled / _lcLength);
        if (_lcAborted)
        {
            float x = progress * _lcLength;
            _lateralOffset = _lcReturnY0 + x * (_lcReturnY1 + x * (_lcReturnY2 / 2 + x * (_lcReturnC3 + x * (_lcReturnC4 + x * _lcReturnC5))));
            _lateralSlope = _lcReturnY1 + x * (_lcReturnY2 + x * (3 * _lcReturnC3 + x * (4 * _lcReturnC4 + x * 5 * _lcReturnC5)));
            _lateralCurvature = _lcReturnY2 + x * (6 * _lcReturnC3 + x * (12 * _lcReturnC4 + x * 20 * _lcReturnC5));
        }
        else
        {
            _lateralOffset = _lcStartOffset * 0.5f * (1 + MathF.Cos(MathF.PI * progress));
            _lateralSlope = -_lcStartOffset * MathF.PI / (2 * _lcLength) * MathF.Sin(MathF.PI * progress);
            _lateralCurvature = -_lcStartOffset * MathF.PI * MathF.PI / (2 * _lcLength * _lcLength) * MathF.Cos(MathF.PI * progress);
        }

        if (progress >= 1)
        {
            long now = _sessionManager.ServerTimeMilliseconds;
            if (_trace.Enabled)
                _trace.Event("lc_end", this, $"\"pt\":{CurrentSplinePointId},\"ms\":{now - _lcStartedAt},\"aborted\":{(_lcAborted ? 1 : 0)}");

            var lcParams = _configuration.Extra.AiParams.LaneChanges;
            ResetLaneChange();
            _lcCooldownUntil = now + (long)(RandomRange(lcParams.MinCooldownSeconds, lcParams.MaxCooldownSeconds) * 1000);
            _lcNextDecision = now;
            return;
        }

        ShadowSplinePointId = MathF.Abs(_lateralOffset) > _lcLaneDistance - LaneChangeOccupiedHalfWidth
            ? GetNeighbor(CurrentSplinePointId, -_lcDirection)
            : -1;
    }

    private void UpdateStoppedForObstacle(AiTraceReason reason)
    {
        if (CurrentSpeed == 0 && !_stoppedForObstacle)
        {
            _stoppedForObstacle = true;
            _stoppedForObstacleSince = _sessionManager.ServerTimeMilliseconds;
            _obstacleHonkStart = _stoppedForObstacleSince + Random.Shared.Next(3000, 7000);
            _obstacleHonkEnd = _obstacleHonkStart + Random.Shared.Next(500, 1500);
            Log.Verbose("AI {SessionId} stopped for obstacle", EntryCar.SessionId);
            if (_trace.Enabled)
                _trace.Event("stop", this, $"\"pt\":{CurrentSplinePointId},\"r\":{(int)reason},\"lead\":{TraceLeaderId},\"gap\":{AiTrace.F(ClosestAiObstacleDistance)},\"pgap\":{AiTrace.F(TracePlayerDistance)}");
        }
        else if (CurrentSpeed > 0 && _stoppedForObstacle)
        {
            _stoppedForObstacle = false;
            Log.Verbose("AI {SessionId} no longer stopped for obstacle", EntryCar.SessionId);
            if (_trace.Enabled)
                _trace.Event("go", this, $"\"pt\":{CurrentSplinePointId},\"stoppedMs\":{_sessionManager.ServerTimeMilliseconds - _stoppedForObstacleSince}");
        }
        else if (_stoppedForObstacle && InGoAroundQueue)
        {
            // Never drive through the queue behind a car waiting to go around, only its first car may. The time in the queue
            // doesn't count: when the car ahead moves on, this car is no longer in the queue for a moment and would otherwise
            // drive through at once, and the whole queue with it
            _stoppedForObstacleSince = _sessionManager.ServerTimeMilliseconds;
        }
        else if (_stoppedForObstacle && _sessionManager.ServerTimeMilliseconds - _stoppedForObstacleSince > IgnoreObstaclesAfterMilliseconds())
        {
            _ignoreObstaclesUntil = _sessionManager.ServerTimeMilliseconds + 10_000;
            Log.Verbose("AI {SessionId} ignoring obstacles until {IgnoreObstaclesUntil}", EntryCar.SessionId, _ignoreObstaclesUntil);
            if (_trace.Enabled)
                _trace.Event("ignore_obstacles", this, $"\"pt\":{CurrentSplinePointId},\"r\":{(int)reason},\"lead\":{TraceLeaderId},\"gap\":{AiTrace.F(ClosestAiObstacleDistance)},\"pgap\":{AiTrace.F(TracePlayerDistance)},\"untilMs\":{_ignoreObstaclesUntil}");
        }
    }

    /// <summary>Driving through obstacles is the last resort: later if the obstacle can be gone around through a neighbour lane</summary>
    private long IgnoreObstaclesAfterMilliseconds()
    {
        long ignoreAfter = _configuration.Extra.AiParams.IgnoreObstaclesAfterMilliseconds;
        if (_blockedSince > 0 && HasNeighborLane())
        {
            ignoreAfter = Math.Max(ignoreAfter, (long)(_configuration.Extra.AiParams.Incidents.GoAroundTimeoutSeconds * 1000));
        }

        return ignoreAfter;
    }

    /// <summary>Incidents: standing in the queue behind a car that waits to go around (not directly behind the player or crashed car)</summary>
    private bool InGoAroundQueue => _blockedSince > 0 && _obstacleAhead is AiState { IsCrashed: false };

    private bool HasNeighborLane()
    {
        for (int direction = -1; direction <= 1; direction += 2)
        {
            int neighborId = GetNeighbor(CurrentSplinePointId, direction);
            if (neighborId >= 0 && IsAllowedLane(in _spline.Points[neighborId])) return true;
        }

        return false;
    }

    /// <summary>
    /// A player hit this car. Incidents: a touch (slower than CrashSpeedKph) only turns on the hazards and brakes softly,
    /// a crash stops the car for a while. Without incidents every collision stops the car, as in vanilla.
    /// </summary>
    public void StopForCollision(float impactSpeedKph)
    {
        StopForCollisionCore(impactSpeedKph);
    }

    /// <summary>0.0.54 plugin API: a collision of unknown impact speed, handled as a crash</summary>
    public void StopForCollision() => StopForCollisionCore(float.MaxValue);

    private void StopForCollisionCore(float impactSpeedKph)
    {
        if (!ShouldIgnorePlayerObstacles() && IncidentsEnabled)
        {
            var incidents = _configuration.Extra.AiParams.Incidents;
            long now = _sessionManager.ServerTimeMilliseconds;
            if (impactSpeedKph < incidents.CrashSpeedKph)
            {
                _hazardsUntil = Math.Max(_hazardsUntil, now + (long)(RandomRange(incidents.TouchMinHazardsSeconds, incidents.TouchMaxHazardsSeconds) * 1000));
                if (now >= _touchBrakeUntil)
                    _touchBrakeUntil = now + (long)(incidents.TouchBrakeSeconds * 1000);
                if (_trace.Enabled)
                    _trace.Event("collision_touch", this, $"\"pt\":{CurrentSplinePointId},\"v\":{AiTrace.F(CurrentSpeed)},\"hit\":{AiTrace.F(impactSpeedKph)},\"hazardsMs\":{_hazardsUntil - now}");
                return;
            }

            long until = now + (long)(RandomRange(incidents.CrashMinStopSeconds, incidents.CrashMaxStopSeconds) * 1000);
            _stoppedForCollisionUntil = Math.Max(_stoppedForCollisionUntil, until);
            _hazardsUntil = Math.Max(_hazardsUntil, _stoppedForCollisionUntil);
            // Standing after the crash is not waiting for an obstacle
            _stoppedForObstacle = false;
            if (_trace.Enabled)
                _trace.Event("collision_stop", this, $"\"pt\":{CurrentSplinePointId},\"v\":{AiTrace.F(CurrentSpeed)},\"untilMs\":{_stoppedForCollisionUntil},\"hit\":{AiTrace.F(impactSpeedKph)},\"crash\":1");
            return;
        }

        if (!ShouldIgnorePlayerObstacles())
        {
            _stoppedForCollisionUntil = _sessionManager.ServerTimeMilliseconds + Random.Shared.Next(EntryCar.AiMinCollisionStopTimeMilliseconds, EntryCar.AiMaxCollisionStopTimeMilliseconds);
            if (_trace.Enabled)
                _trace.Event("collision_stop", this, $"\"pt\":{CurrentSplinePointId},\"v\":{AiTrace.F(CurrentSpeed)},\"untilMs\":{_stoppedForCollisionUntil}");
        }
    }

    public float GetAngleToCar(CarStatus car)
    {
        float challengedAngle = (float) (Math.Atan2(Status.Position.X - car.Position.X, Status.Position.Z - car.Position.Z) * 180 / Math.PI);
        if (challengedAngle < 0)
            challengedAngle += 360;
        float challengedRot = Status.GetRotationAngle();

        challengedAngle += challengedRot;
        challengedAngle %= 360;

        return challengedAngle;
    }

    private void SetTargetSpeed(float speed, float deceleration, float acceleration)
    {
        _accIntegrate = false;
        TargetSpeed = speed;
        if (speed < CurrentSpeed)
        {
            Acceleration = -deceleration;
        }
        else if (speed > CurrentSpeed)
        {
            Acceleration = acceleration;
        }
        else
        {
            Acceleration = 0;
        }
    }

    private void SetTargetSpeed(float speed)
    {
        SetTargetSpeed(speed, EntryCar.AiDeceleration, EntryCar.AiAcceleration);
    }

    public void Update()
    {
        if (!Initialized)
            return;

        lock (_moveLock)
        {
            UpdateLocked();
        }
    }

    private void UpdateLocked()
    {
        if (!Initialized)
            return;

        var ops = _spline.Operations;

        long currentTime = _sessionManager.ServerTimeMilliseconds;
        long dt = currentTime - _lastTick;
        _lastTick = currentTime;

        if (Acceleration != 0)
        {
            CurrentSpeed += Acceleration * (dt / 1000.0f);

            if (_accIntegrate)
            {
                if (Acceleration < 0 && CurrentSpeed < AccStandstillSpeed)
                {
                    CurrentSpeed = 0;
                }
            }
            else if ((Acceleration < 0 && CurrentSpeed < TargetSpeed) || (Acceleration > 0 && CurrentSpeed > TargetSpeed))
            {
                CurrentSpeed = TargetSpeed;
                Acceleration = 0;
            }
        }

        float moveMeters = (dt / 1000.0f) * CurrentSpeed;
        if (!Move(_currentVecProgress + moveMeters) || !_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPoint))
        {
            Log.Debug("Car {SessionId} reached spline end, despawning", EntryCar.SessionId);
            Despawn("spline_end");
            return;
        }

        if (_lcPhase == LaneChangePhase.Moving)
        {
            AdvanceLaneChange(moveMeters);
        }

        CatmullRom.CatmullRomPoint smoothPos = CatmullRom.Evaluate(ops.Points[CurrentSplinePointId].Position, 
            ops.Points[nextPoint].Position, 
            _startTangent, 
            _endTangent, 
            _currentVecProgress / _currentVecLength);

        Vector3 position = smoothPos.Position;
        Vector3 tangent = smoothPos.Tangent;
        if (_lateralOffset != 0 || _lateralSlope != 0)
        {
            // Lane change: shift across the lane and turn the car along its path
            var normal = LateralNormal(tangent);
            position += normal * _lateralOffset;
            tangent = Vector3.Normalize(Vector3.Normalize(tangent) + normal * _lateralSlope);
        }

        Vector3 rotation = new Vector3
        {
            X = MathF.Atan2(tangent.Z, tangent.X) - MathF.PI / 2,
            Y = (MathF.Atan2(new Vector2(tangent.Z, tangent.X).Length(), tangent.Y) - MathF.PI / 2) * -1f,
            Z = ops.GetCamber(CurrentSplinePointId, _currentVecProgress / _currentVecLength)
        };

        float tyreAngularSpeed = GetTyreAngularSpeed(CurrentSpeed, EntryCar.TyreDiameterMeters);
        byte encodedTyreAngularSpeed =  (byte) (Math.Clamp(MathF.Round(MathF.Log10(tyreAngularSpeed + 1.0f) * 20.0f) * Math.Sign(tyreAngularSpeed), -100.0f, 154.0f) + 100.0f);

        Status.Timestamp = _sessionManager.ServerTimeMilliseconds;
        Status.Position = position with { Y = position.Y + EntryCar.AiSplineHeightOffsetMeters };
        Status.Rotation = rotation;
        Status.Velocity = tangent * CurrentSpeed;
        Status.TyreAngularSpeed[0] = encodedTyreAngularSpeed;
        Status.TyreAngularSpeed[1] = encodedTyreAngularSpeed;
        Status.TyreAngularSpeed[2] = encodedTyreAngularSpeed;
        Status.TyreAngularSpeed[3] = encodedTyreAngularSpeed;
        bool brakeLights = CurrentSpeed == 0 || (_accIntegrate ? _accBrakeLightsOn : Acceleration < 0);
        var visualParams = _configuration.Extra.AiParams.Visuals;
        if (visualParams.Enabled)
        {
            _visuals.Update(visualParams, EntryCar, tangent, moveMeters, dt / 1000.0f, CurrentSpeed, Acceleration,
                AccEnabled ? _accMaxAcceleration : EntryCar.AiAcceleration, brakeLights, currentTime);
            Status.SteerAngle = _visuals.SteerAngle;
            Status.WheelAngle = _visuals.WheelAngle;
            Status.EngineRpm = _visuals.EngineRpm;
            Status.Gear = _visuals.Gear;
            Status.Gas = _visuals.Gas;
        }
        else
        {
            Status.SteerAngle = 127;
            Status.WheelAngle = 127;
            Status.EngineRpm = (ushort)MathUtils.Lerp(EntryCar.AiIdleEngineRpm, EntryCar.AiMaxEngineRpm, CurrentSpeed / _configuration.Extra.AiParams.MaxSpeedMs);
            Status.Gear = 2;
            Status.Gas = 0;
        }
        Status.StatusFlag = GetLights(_configuration.Extra.AiParams.EnableDaytimeLights, _weatherManager.CurrentSunPosition, _randomTwilight)
                            | (_sessionManager.ServerTimeMilliseconds < _stoppedForCollisionUntil || _sessionManager.ServerTimeMilliseconds < _hazardsUntil || _lcCrashHoldSince > 0 || CurrentSpeed < 20 / 3.6f ? CarStatusFlags.HazardsOn : 0)
                            | (brakeLights ? CarStatusFlags.BrakeLightsOn : 0)
                            | (_stoppedForObstacle && _sessionManager.ServerTimeMilliseconds > _obstacleHonkStart && _sessionManager.ServerTimeMilliseconds < _obstacleHonkEnd ? CarStatusFlags.Horn : 0)
                            | GetWiperSpeed(_weatherManager.CurrentWeather.RainIntensity)
                            | (_lcPhase != LaneChangePhase.None ? (_lcDirection < 0 ? CarStatusFlags.IndicateLeft : CarStatusFlags.IndicateRight) : _indicator);
    }
        
    private static float GetTyreAngularSpeed(float speed, float wheelDiameter)
    {
        return speed / (MathF.PI * wheelDiameter) * 6;
    }

    private static CarStatusFlags GetWiperSpeed(float rainIntensity)
    {
        return rainIntensity switch
        {
            < 0.05f => 0,
            < 0.25f => CarStatusFlags.WiperLevel1,
            < 0.5f => CarStatusFlags.WiperLevel2,
            _ => CarStatusFlags.WiperLevel3
        };
    }

    private static CarStatusFlags GetLights(bool daytimeLights, SunPosition? sunPosition, double twilight)
    {
        const CarStatusFlags lightFlags = CarStatusFlags.LightsOn | CarStatusFlags.HighBeamsOff;
        if (daytimeLights || sunPosition == null) return lightFlags;

        return sunPosition.Value.Altitude < twilight ? lightFlags : 0;
    }
}

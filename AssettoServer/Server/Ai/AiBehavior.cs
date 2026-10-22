using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AssettoServer.Network.Tcp;
using AssettoServer.Server.Ai.Splines;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Plugin;
using AssettoServer.Shared.Network.Packets.Outgoing;
using AssettoServer.Shared.Services;
using AssettoServer.Utils;
using Microsoft.Extensions.Hosting;
using Prometheus;
using Serilog;

namespace AssettoServer.Server.Ai;

public class AiBehavior : CriticalBackgroundService, IAssettoServerAutostart
{
    private readonly ACServerConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly AiSpline _spline;
    //private readonly EntryCar.Factory _entryCarFactory;

    private readonly JunctionEvaluator _junctionEvaluator;
    private readonly AiTrace _trace;
    private readonly AiPlayerLanes _playerLanes;
    private readonly AiMerges _merges;

    private readonly Gauge _aiStateCountMetric = Metrics.CreateGauge("assettoserver_aistatecount", "Number of AI states");

    private readonly Summary _updateDurationTimer;
    private readonly Summary _obstacleDetectionDurationTimer;

    public AiBehavior(SessionManager sessionManager,
        ACServerConfiguration configuration,
        EntryCarManager entryCarManager,
        IHostApplicationLifetime applicationLifetime,
        //EntryCar.Factory entryCarFactory,
        CSPServerScriptProvider serverScriptProvider,
        AiSpline spline,
        AiTrace trace,
        AiPlayerLanes playerLanes,
        AiMerges merges) : base(applicationLifetime)
    {
        _merges = merges;
        _trace = trace;
        _playerLanes = playerLanes;
        _sessionManager = sessionManager;
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _spline = spline;
        _junctionEvaluator = new JunctionEvaluator(spline, false);
        //_entryCarFactory = entryCarFactory;

        if (_configuration.Extra.AiParams.Debug)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AssettoServer.Server.Ai.ai_debug.lua")!;
            serverScriptProvider.AddScript(stream, "ai_debug.lua");
        }

        _updateDurationTimer = Metrics.CreateSummary("assettoserver_aibehavior_update", "AiBehavior.Update Duration", MetricDefaults.DefaultQuantiles);
        _obstacleDetectionDurationTimer = Metrics.CreateSummary("assettoserver_aibehavior_obstacledetection", "AiBehavior.ObstacleDetection Duration", MetricDefaults.DefaultQuantiles);

        _entryCarManager.ClientConnected += (client, _) =>
        {
            client.ChecksumPassed += OnClientChecksumPassed;
            client.Collision += OnCollision;
        };

        _entryCarManager.ClientDisconnected += OnClientDisconnected;
        _configuration.Extra.AiParams.PropertyChanged += (_, _) => AdjustOverbooking();
        _sessionManager.SessionChanged += OnSessionChanged;
    }

    private void OnCollision(ACTcpClient sender, CollisionEventArgs args)
    {
        if (args.TargetCar?.AiControlled == true)
        {
            var targetAiState = args.TargetCar.GetClosestAiState(sender.EntryCar.Status.Position);
            if (_trace.Enabled)
            {
                _trace.Event("collision", targetAiState.AiState,
                    $"\"player\":{sender.EntryCar.SessionId},\"target\":{args.TargetCar.SessionId},\"speed\":{AiTrace.F(args.Speed)},\"x\":{AiTrace.F(args.Position.X)},\"y\":{AiTrace.F(args.Position.Y)},\"z\":{AiTrace.F(args.Position.Z)},\"dist\":{AiTrace.F(MathF.Sqrt(targetAiState.DistanceSquared))}");
            }
            if (targetAiState.AiState != null && targetAiState.DistanceSquared < 25 * 25)
            {
                Task.Delay(Random.Shared.Next(100, 500)).ContinueWith(_ => targetAiState.AiState.StopForCollision(args.Speed));
            }
        }
    }

    private void OnClientChecksumPassed(ACTcpClient sender, EventArgs args)
    {
        sender.EntryCar.SetAiControl(false);
        AdjustOverbooking();
    }

    private async Task ObstacleDetectionAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var context = _obstacleDetectionDurationTimer.NewTimer();

                if (_configuration.Extra.AiParams.FollowingModel == AiFollowingModel.Acc)
                {
                    _playerLanes.Update(_sessionManager.ServerTimeMilliseconds);
                }

                for (int i = 0; i < _entryCarManager.EntryCars.Length; i++)
                {
                    var entryCar = _entryCarManager.EntryCars[i];
                    if (entryCar.AiControlled)
                    {
                        entryCar.AiObstacleDetection();
                    }
                }

                if (_configuration.Extra.AiParams.Debug)
                {
                    SendDebugPackets();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in AI obstacle detection");
            }
        }
    }

    private void SendDebugPackets()
    {
        CountedArray<byte> sessionIds = new(_entryCarManager.EntryCars.Length);
        CountedArray<byte> currentSpeeds = new(_entryCarManager.EntryCars.Length);
        CountedArray<byte> targetSpeeds = new(_entryCarManager.EntryCars.Length);
        CountedArray<byte> maxSpeeds = new(_entryCarManager.EntryCars.Length);
        CountedArray<short> closestAiObstacles = new(_entryCarManager.EntryCars.Length);
        foreach (var player in _entryCarManager.ConnectedCars.Values)
        {
            if (player.Client?.HasSentFirstUpdate == false) continue;

            sessionIds.Clear();
            currentSpeeds.Clear();
            targetSpeeds.Clear();
            maxSpeeds.Clear();
            closestAiObstacles.Clear();

            foreach (var car in _entryCarManager.EntryCars)
            {
                if (!car.AiControlled) continue;

                var (aiState, _) = car.GetClosestAiState(player.Status.Position);
                if (aiState == null) continue;

                sessionIds.Add(car.SessionId);
                currentSpeeds.Add((byte)(aiState.CurrentSpeed * 3.6f));
                targetSpeeds.Add((byte)(aiState.TargetSpeed * 3.6f));
                maxSpeeds.Add((byte)(aiState.MaxSpeed * 3.6f));
                closestAiObstacles.Add((short)aiState.ClosestAiObstacleDistance);
            }

            for (int i = 0; i < sessionIds.Count; i += AiDebugPacket.Length)
            {
                var packet = new AiDebugPacket();
                Array.Fill(packet.SessionIds, (byte)255);

                new ArraySegment<byte>(sessionIds.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.SessionIds);
                new ArraySegment<short>(closestAiObstacles.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.ClosestAiObstacles);
                new ArraySegment<byte>(currentSpeeds.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.CurrentSpeeds);
                new ArraySegment<byte>(maxSpeeds.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.MaxSpeeds);
                new ArraySegment<byte>(targetSpeeds.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.TargetSpeeds);

                player.Client?.SendPacket(packet);
            }
        }
    }
    
    private readonly List<EntryCar> _playerCars = new();
    private readonly List<AiState> _initializedAiStates = new();
    private readonly List<AiState> _uninitializedAiStates = new();
    private readonly List<Vector3> _playerOffsetPositions = new();
    private readonly List<KeyValuePair<AiState, float>> _aiMinDistanceToPlayer = new();
    private readonly List<KeyValuePair<EntryCar, float>> _playerMinDistanceToAi = new();
    private void Update()
    {
        using var context = _updateDurationTimer.NewTimer();

        _playerCars.Clear();
        _initializedAiStates.Clear();
        _uninitializedAiStates.Clear();
        _playerOffsetPositions.Clear();
        _aiMinDistanceToPlayer.Clear();
        _playerMinDistanceToAi.Clear();

        foreach (var entryCar in _entryCarManager.EntryCars)
        {
            var (currentSplinePointId, _) = _spline.WorldToSpline(entryCar.Status.Position);
            var drivingTheRightWay = Vector3.Dot(_spline.Operations.GetForwardVector(currentSplinePointId), entryCar.Status.Velocity) > 0;

            if (!entryCar.AiControlled
                && entryCar.Client?.HasSentFirstUpdate == true
                && _sessionManager.ServerTimeMilliseconds - entryCar.LastActiveTime < _configuration.Extra.AiParams.PlayerAfkTimeoutMilliseconds
                && (_configuration.Extra.AiParams.TwoWayTraffic || _configuration.Extra.AiParams.WrongWayTraffic || drivingTheRightWay))
            {
                _playerCars.Add(entryCar);
            }
            else if (entryCar.AiControlled)
            {
                entryCar.RemoveUnsafeStates();
                entryCar.GetInitializedStates(_initializedAiStates, _uninitializedAiStates);
            }
            
        }

        _aiStateCountMetric.Set(_initializedAiStates.Count);

        if (_sessionManager.CurrentSession.StartTimeMilliseconds > _sessionManager.ServerTimeMilliseconds)
            return;

        for (int i = 0; i < _initializedAiStates.Count; i++)
        {
            _aiMinDistanceToPlayer.Add(new KeyValuePair<AiState, float>(_initializedAiStates[i], float.MaxValue));
        }

        for (int i = 0; i < _playerCars.Count; i++)
        {
            _playerMinDistanceToAi.Add(new KeyValuePair<EntryCar, float>(_playerCars[i], float.MaxValue));
        }

        // Get minimum distance to a player for each AI
        // Get minimum distance to AI for each player
        for (int i = 0; i < _initializedAiStates.Count; i++)
        {
            for (int j = 0; j < _playerCars.Count; j++)
            {
                if (_playerOffsetPositions.Count <= j)
                {
                    var offsetPosition = _playerCars[j].Status.Position;
                    if (_playerCars[j].Status.Velocity != Vector3.Zero)
                    {
                        offsetPosition += Vector3.Normalize(_playerCars[j].Status.Velocity) * _configuration.Extra.AiParams.PlayerPositionOffsetMeters;
                    }

                    _playerOffsetPositions.Add(offsetPosition);
                }

                var distanceSquared = Vector3.DistanceSquared(_initializedAiStates[i].Status.Position, _playerOffsetPositions[j]);

                if (_aiMinDistanceToPlayer[i].Value > distanceSquared)
                {
                    _aiMinDistanceToPlayer[i] = new KeyValuePair<AiState, float>(_initializedAiStates[i], distanceSquared);
                }

                if (_playerMinDistanceToAi[j].Value > distanceSquared)
                {
                    _playerMinDistanceToAi[j] = new KeyValuePair<EntryCar, float>(_playerCars[j], distanceSquared);
                }
            }
        }
        
        // Order AI cars by their minimum distance to a player. Higher distance = higher chance for respawn
        _aiMinDistanceToPlayer.Sort((a, b) => b.Value.CompareTo(a.Value));

        foreach (var dist in _aiMinDistanceToPlayer)
        {
            if (dist.Value > _configuration.Extra.AiParams.PlayerRadiusSquared
                && _sessionManager.ServerTimeMilliseconds > dist.Key.SpawnProtectionEnds)
            {
                _uninitializedAiStates.Add(dist.Key);
            }
        }
        
        if (_initializedAiStates.Count > 0 && _playerCars.Count > 0)
        {
            _playerCars.Clear();
            // Order player cars by their minimum distance to an AI. Higher distance = higher chance for next AI spawn
            _playerMinDistanceToAi.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < _playerMinDistanceToAi.Count; i++)
            {
                _playerCars.Add(_playerMinDistanceToAi[i].Key);
            }
        }

        while (_playerCars.Count > 0 && _uninitializedAiStates.Count > 0)
        {
            int spawnPointId = -1;
            RampEntry? ramp = null;
            while (spawnPointId < 0 && _playerCars.Count > 0)
            {
                var targetPlayerCar = _playerCars.ElementAt(GetRandomWeighted(_playerCars.Count));
                _playerCars.Remove(targetPlayerCar);

                (spawnPointId, ramp) = GetRampSpawnPoint(targetPlayerCar);
                if (spawnPointId < 0)
                {
                    spawnPointId = GetSpawnPoint(targetPlayerCar);
                }
            }

            float spawnShift = 0;
            float? platoonHeadway = null;
            AiState? platoonLeader = null;
            if (spawnPointId >= 0 && AccSpawnFollowerCheck)
            {
                int requestedPointId = spawnPointId;
                if (ramp == null && Random.Shared.NextSingle() < _configuration.Extra.AiParams.Acc.PlatoonSpawnShare
                    && FindPlatoonSpawnPoint(spawnPointId, out int platoonPointId, out float headway, out platoonLeader))
                {
                    spawnPointId = platoonPointId;
                    platoonHeadway = headway;
                }
                else
                {
                    platoonLeader = null;
                    // On-ramp spawns are placed before the merge, don't move them
                    spawnPointId = ramp != null
                        ? IsAccSpawnSafe(spawnPointId) ? spawnPointId : -1
                        : FindAccSpawnPoint(spawnPointId, out spawnShift);
                }

                if (spawnPointId < 0)
                    _trace.Event("spawn_blocked", null, $"\"pt\":{requestedPointId},\"ramp\":{(ramp != null ? 1 : 0)}");
            }

            if (spawnPointId < 0 || !_junctionEvaluator.TryNext(spawnPointId, out _))
                continue;

            var previousAi = FindClosestAiState(spawnPointId, false);
            // A spawn in a column is closer to the car ahead than SafetyDistance on purpose, at the Acc following distance
            var nextAi = platoonHeadway.HasValue ? null : FindClosestAiState(spawnPointId, true);

            foreach (var targetAiState in _uninitializedAiStates)
            {
                // A column spawn behind this very state (its current position, about to be reused) makes no sense
                if (targetAiState == platoonLeader || !targetAiState.CanSpawn(spawnPointId, previousAi, nextAi))
                    continue;

                targetAiState.Teleport(spawnPointId, platoonHeadway);
                if (platoonLeader != null)
                    _trace.Event("spawn_platoon", targetAiState, $"\"lead\":{platoonLeader.TraceId},\"T\":{AiTrace.F(platoonHeadway ?? 0)}");
                if (spawnShift != 0)
                    _trace.Event("spawn_shift", targetAiState, $"\"m\":{AiTrace.F(spawnShift)}");
                if (ramp != null)
                {
                    ramp.NextSpawnAllowed = _sessionManager.ServerTimeMilliseconds + (long)(_configuration.Extra.AiParams.Merges.RampSpawnIntervalSeconds * 1000);
                    _trace.Event("ramp_spawn", targetAiState, $"\"seg\":{ramp.Segment},\"pt\":{spawnPointId}");
                }

                _uninitializedAiStates.Remove(targetAiState);
                break;
            }
        }
    }

    private bool AccSpawnFollowerCheck => _configuration.Extra.AiParams.FollowingModel == AiFollowingModel.Acc
                                          && _configuration.Extra.AiParams.Acc.SpawnFollowerCheck;

    /// <summary>
    /// Acc: find a spawn point near the requested one where the car behind doesn't have to brake for the new car. Looks
    /// alternately behind and ahead of the requested point, in steps of 5 points, in every lane, up to SpawnMaxShiftMeters
    /// each way. Behind means towards the player when spawning ahead of it: the flow far ahead is often too dense to fit a
    /// car in, closer to the player there is room. IsPositionSafe keeps SpawnSafetyDistanceToPlayerMeters.
    /// -1 if there is no such point. <paramref name="shifted"/> is the distance from the requested point, negative = behind it.
    /// </summary>
    private int FindAccSpawnPoint(int pointId, out float shifted)
    {
        var points = _spline.Points;
        float maxShift = _configuration.Extra.AiParams.Acc.SpawnMaxShiftMeters;
        shifted = 0;

        int ahead = pointId, behind = pointId;
        float aheadShift = 0, behindShift = 0;
        if (TryAccSpawnLanes(pointId, out int found)) return found;

        while (ahead >= 0 || behind >= 0)
        {
            if (behind >= 0)
            {
                for (int i = 0; i < 5 && behind >= 0; i++)
                {
                    behind = _junctionEvaluator.Previous(behind);
                    if (behind >= 0) behindShift += points[behind].Length;
                }

                if (behindShift > maxShift) behind = -1;
                if (behind >= 0 && TryAccSpawnLanes(behind, out found))
                {
                    shifted = -behindShift;
                    return found;
                }
            }

            if (ahead >= 0)
            {
                for (int i = 0; i < 5 && ahead >= 0; i++)
                {
                    aheadShift += points[ahead].Length;
                    ahead = _junctionEvaluator.Next(ahead);
                }

                if (aheadShift > maxShift) ahead = -1;
                if (ahead >= 0 && TryAccSpawnLanes(ahead, out found))
                {
                    shifted = aheadShift;
                    return found;
                }
            }
        }

        return -1;
    }

    /// <summary>The lanes at <paramref name="pointId"/> in random order: the first one that is safe for a spawn</summary>
    private bool TryAccSpawnLanes(int pointId, out int found)
    {
        var lanes = _spline.GetLanes(pointId);
        int start = lanes.Length > 0 ? Random.Shared.Next(lanes.Length) : 0;
        for (int i = 0; i < lanes.Length; i++)
        {
            int laneId = lanes[(start + i) % lanes.Length];
            if (_spline.Points[laneId].NextId >= 0 && IsPositionSafe(laneId) && IsAccSpawnSafe(laneId))
            {
                found = laneId;
                return true;
            }
        }

        found = -1;
        return false;
    }

    /// <summary>
    /// Column spawn: the point right behind the nearest car ahead of <paramref name="requestedPointId"/> in its lane, at the
    /// Acc following distance of the new driver (random time headway, average standstill gap) at the speed of that car.
    /// The new car starts at that speed, so neither it nor the car ahead brakes; the car behind is checked as usual.
    /// </summary>
    private bool FindPlatoonSpawnPoint(int requestedPointId, out int pointId, out float timeHeadway, out AiState? leader)
    {
        var accParams = _configuration.Extra.AiParams.Acc;
        var points = _spline.Points;
        pointId = -1;
        timeHeadway = accParams.MinTimeHeadwaySeconds + Random.Shared.NextSingle() * (accParams.MaxTimeHeadwaySeconds - accParams.MinTimeHeadwaySeconds);

        leader = FindClosestAiState(requestedPointId, true, accParams.SpawnSearchMeters);
        if (leader == null || !leader.Initialized) return false;

        float distance = leader.AccRearLength + accParams.DefaultVehicleLengthMeters / 2
                         + (accParams.MinStandstillGapMeters + accParams.MaxStandstillGapMeters) / 2
                         + leader.CurrentSpeed * timeHeadway;
        int candidate = leader.CurrentSplinePointId;
        float travelled = 0;
        while (candidate >= 0 && travelled < distance)
        {
            candidate = _junctionEvaluator.Previous(candidate);
            if (candidate >= 0) travelled += points[candidate].Length;
        }

        if (candidate < 0 || points[candidate].NextId < 0) return false;
        // Nothing in between and no car right at the point (the searches ahead and behind skip the point itself), far enough
        // from players, and the car behind doesn't brake
        if (FindClosestAiState(candidate, true, distance + 10) != leader) return false;
        if (!IsClearOfAi(candidate, accParams.DefaultVehicleLengthMeters + accParams.MinStandstillGapMeters, leader)) return false;
        if (!IsFarFromPlayers(candidate) || !IsAccSpawnSafe(candidate, timeHeadway)) return false;

        pointId = candidate;
        return true;
    }

    /// <summary>No AI car of the same direction closer than <paramref name="radius"/> to the point, except <paramref name="except"/></summary>
    private bool IsClearOfAi(int pointId, float radius, AiState? except)
    {
        var ops = _spline.Operations;
        var position = ops.Points[pointId].Position;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (!car.AiControlled) continue;

            var (state, distanceSquared) = car.GetClosestAiState(position);
            if (state != null && state != except && state.Initialized && distanceSquared < radius * radius
                && ops.IsSameDirection(state.CurrentSplinePointId, pointId))
                return false;
        }

        return true;
    }

    private bool IsFarFromPlayers(int pointId)
    {
        var position = _spline.Points[pointId].Position;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.Client?.HasSentFirstUpdate == true
                && Vector3.DistanceSquared(car.Status.Position, position) < _configuration.Extra.AiParams.SpawnSafetyDistanceToPlayerSquared)
                return false;
        }

        return true;
    }

    private bool IsAccSpawnSafe(int pointId, float? timeHeadway = null)
    {
        var accParams = _configuration.Extra.AiParams.Acc;
        var follower = FindClosestAiState(pointId, false, accParams.SpawnSearchMeters);
        if (follower == null) return true;

        var leader = FindClosestAiState(pointId, true, accParams.SpawnSearchMeters);
        float maxDeceleration = IsNearPlayer(pointId, accParams.SpawnFollowerNearPlayerMeters)
            ? accParams.SpawnFollowerMaxDecelerationMs2
            : MathF.Max(accParams.SpawnFollowerMaxDecelerationMs2, follower.AccComfortableDeceleration);
        return follower.AccAccelerationForSpawn(pointId, leader, timeHeadway) >= -maxDeceleration;
    }

    private bool IsNearPlayer(int pointId, float distance)
    {
        var position = _spline.Points[pointId].Position;
        float distanceSquared = distance * distance;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (!car.AiControlled && car.Client?.HasSentFirstUpdate == true
                && Vector3.DistanceSquared(car.Status.Position, position) < distanceSquared)
                return true;
        }

        return false;
    }

    private AiState? FindClosestAiState(int pointId, bool forward, float searchDistance = 50)
    {
        var points = _spline.Points;
        float distanceTravelled = 0;
        ref readonly var point = ref points[pointId];
        
        AiState? closestAiState = null;
        while (distanceTravelled < searchDistance && closestAiState == null)
        {
            distanceTravelled += point.Length;
            // TODO reuse this junction evaluator for the newly spawned car
            pointId = forward ? _junctionEvaluator.Next(pointId) : _junctionEvaluator.Previous(pointId);
            if (pointId < 0)
                break;

            point = ref points[pointId];
            
            var slowest = _spline.SlowestAiStates[pointId];
            if (slowest != null)
            {
                closestAiState = slowest;
            }
        }

        return closestAiState;
    }

    private async Task UpdateAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_configuration.Extra.AiParams.AiBehaviorUpdateIntervalMilliseconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Update();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in AI update");
            }
        }
    }

    private void OnClientDisconnected(ACTcpClient sender, EventArgs args)
    {
        if (sender.EntryCar.AiMode != AiMode.None)
        {
            sender.EntryCar.SetAiControl(true);
            AdjustOverbooking();
        }
    }

    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        for (var i = 0; i < _initializedAiStates.Count; i++)
        {
            _initializedAiStates[i].Despawn("session");
        }
    }

    private int GetRandomWeighted(int max)
    {
        // Probabilities for max = 4
        // 0    4/10
        // 1    3/10
        // 2    2/10
        // 3    1/10
            
        int maxRand = max * (max + 1) / 2;
        int rand = Random.Shared.Next(maxRand);
        int target = 0;
        for (int i = max; i < maxRand; i += (i - 1))
        {
            if (rand < i) break;
            target++;
        }

        return target;
    }

    private bool IsPositionSafe(int pointId)
    {
        var ops = _spline.Operations;

        for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
        {
            var entryCar = _entryCarManager.EntryCars[i];
            if (entryCar.AiControlled && !entryCar.IsPositionSafe(pointId))
            {
                return false;
            }

            if (entryCar.Client?.HasSentFirstUpdate == true
                && Vector3.DistanceSquared(entryCar.Status.Position, ops.Points[pointId].Position) < _configuration.Extra.AiParams.SpawnSafetyDistanceToPlayerSquared)
            {
                return false;
            }
        }

        return true;
    }

    private int GetSpawnPoint(EntryCar playerCar)
    {
        var result = _spline.WorldToSpline(playerCar.Status.Position);
        var ops = _spline.Operations;


        if (result.PointId < 0 || ops.Points[result.PointId].NextId < 0) return -1;
        
        int direction = Vector3.Dot(ops.GetForwardVector(result.PointId), playerCar.Status.Velocity) > 0 ? 1 : -1;
        
        // Do not not spawn if a player is too far away from the AI spline, e.g. in pits or in a part of the map without traffic
        if (result.DistanceSquared > _configuration.Extra.AiParams.MaxPlayerDistanceToAiSplineSquared)
        {
            return -1;
        }
        
        int spawnDistance = Random.Shared.Next(_configuration.Extra.AiParams.MinSpawnDistancePoints, _configuration.Extra.AiParams.MaxSpawnDistancePoints);
        var spawnPointId = _junctionEvaluator.Traverse(result.PointId, spawnDistance * direction);

        if (spawnPointId >= 0)
        {
            spawnPointId = _spline.RandomLane(spawnPointId);
        }
        
        if (spawnPointId >= 0 && ops.Points[spawnPointId].NextId >= 0)
        {
            direction = Vector3.Dot(ops.GetForwardVector(spawnPointId), playerCar.Status.Velocity) > 0 ? 1 : -1;
        }

        while (spawnPointId >= 0 && !IsPositionSafe(spawnPointId))
        {
            spawnPointId = _junctionEvaluator.Traverse(spawnPointId, direction * 5);
        }
        
        if (spawnPointId >= 0)
        {
            spawnPointId = _spline.RandomLane(spawnPointId);
        }

        return spawnPointId;
    }

    /// <summary>
    /// Merges: with RampSpawnShare, a point on an on-ramp whose merge is ahead of the player, so the player sees cars
    /// joining the main lane. -1 if not rolled or there is no such on-ramp.
    /// </summary>
    private (int PointId, RampEntry? Ramp) GetRampSpawnPoint(EntryCar playerCar)
    {
        var mergeParams = _configuration.Extra.AiParams.Merges;
        if (!_merges.Enabled || _merges.RampEntries.Count == 0 || Random.Shared.NextSingle() >= mergeParams.RampSpawnShare)
            return (-1, null);

        var velocity = playerCar.Status.Velocity;
        if (velocity.LengthSquared() < 5 * 5)
            return (-1, null);

        var forward = Vector3.Normalize(velocity);
        var position = playerCar.Status.Position;
        var ops = _spline.Operations;
        var points = _spline.Points;
        long now = _sessionManager.ServerTimeMilliseconds;

        int count = _merges.RampEntries.Count;
        int start = Random.Shared.Next(count);
        for (int i = 0; i < count; i++)
        {
            var ramp = _merges.RampEntries[(start + i) % count];
            if (now < ramp.NextSpawnAllowed) continue;

            var toMerge = points[ramp.LanePointId].Position - position;
            float distance = toMerge.Length();
            if (distance < mergeParams.RampSpawnMinAheadMeters || distance > mergeParams.RampSpawnMaxAheadMeters) continue;
            if (Vector3.Dot(toMerge, forward) < 0.9f * distance) continue;
            if (Vector3.Dot(Vector3.Normalize(ops.GetForwardVector(ramp.LanePointId)), forward) < 0.7f) continue;

            float before = mergeParams.RampSpawnMinBeforeMergeMeters
                           + Random.Shared.NextSingle() * (mergeParams.RampSpawnMaxBeforeMergeMeters - mergeParams.RampSpawnMinBeforeMergeMeters);
            int pointId = ramp.RampPointId;
            float travelled = 0;
            while (travelled < before && points[pointId].PreviousId >= 0)
            {
                pointId = points[pointId].PreviousId;
                travelled += points[pointId].Length;
            }

            if (travelled < mergeParams.RampSpawnMinBeforeMergeMeters || !IsPositionSafe(pointId)) continue;

            return (pointId, ramp);
        }

        return (-1, null);
    }

    private void AdjustOverbooking()
    {
        int playerCount = _entryCarManager.EntryCars.Count(car => car.Client != null && car.Client.IsConnected);
        var aiSlots = _entryCarManager.EntryCars.Where(car => car.Client == null && car.AiControlled).ToList(); // client null check is necessary here so that slots where someone is connecting don't count

        if (aiSlots.Count == 0)
        {
            Log.Debug("AI Slot overbooking update - no AI slots available");
            return;
        }
            
        int targetAiCount = Math.Min(playerCount * Math.Min((int)Math.Round(_configuration.Extra.AiParams.AiPerPlayerTargetCount * _configuration.Extra.AiParams.TrafficDensity), aiSlots.Count), _configuration.Extra.AiParams.MaxAiTargetCount);

        int overbooking = targetAiCount / aiSlots.Count;
        int rest = targetAiCount % aiSlots.Count;
            
        Log.Debug("AI Slot overbooking update - No. players: {NumPlayers} - No. AI Slots: {NumAiSlots} - Target AI count: {TargetAiCount} - Overbooking: {Overbooking} - Rest: {Rest}", 
            playerCount, aiSlots.Count, targetAiCount, overbooking, rest);

        for (int i = 0; i < aiSlots.Count; i++)
        {
            aiSlots[i].SetAiOverbooking(i < rest ? overbooking + 1 : overbooking);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = UpdateAsync(stoppingToken);
        _ = ObstacleDetectionAsync(stoppingToken);

        return Task.CompletedTask;
    }
}

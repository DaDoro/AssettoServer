using System;
using System.Collections.Generic;
using System.Numerics;
using AssettoServer.Server.Ai.Splines;
using AssettoServer.Server.Configuration;

namespace AssettoServer.Server.Ai;

/// <summary>A player car as seen by AI in one lane.</summary>
/// <param name="Speed">Speed along the lane (m/s), never negative</param>
/// <param name="Acceleration">Smoothed acceleration along the lane (m/s^2)</param>
public readonly record struct AiLanePlayer(EntryCar Car, float Speed, float Acceleration);

/// <summary>
/// Lanes occupied by players, for the Acc following model. A player occupies a lane when any part of the car is inside it,
/// so a player between two lanes blocks both. Each player is registered at the nearest point of every lane it occupies.
/// Updated and read only from the obstacle detection loop (AiBehavior), no locking.
/// </summary>
public class AiPlayerLanes
{
    // A player further than this from any AI spline point is off the road for the AI
    private const float MaxDistanceToSplineMeters = 10;
    // Acceleration smoothing, per obstacle detection tick (100 ms)
    private const float AccelerationSmoothing = 0.5f;

    private readonly ACServerConfiguration _configuration;
    private readonly EntryCarManager _entryCarManager;
    private readonly AiSpline _spline;

    private readonly Dictionary<int, AiLanePlayer> _byPoint = new();
    private readonly Dictionary<EntryCar, (float Speed, float Acceleration, long Time)> _history = new();

    public AiPlayerLanes(ACServerConfiguration configuration, EntryCarManager entryCarManager, AiSpline spline)
    {
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _spline = spline;
    }

    public bool TryGet(int pointId, out AiLanePlayer player) => _byPoint.TryGetValue(pointId, out player);

    public void Update(long serverTimeMilliseconds)
    {
        _byPoint.Clear();

        var points = _spline.Points;
        float maxLateral = _configuration.Extra.AiParams.LaneWidthMeters / 2 + _configuration.Extra.AiParams.Acc.PlayerHalfWidthMeters;

        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.AiControlled || car.Client?.HasSentFirstUpdate != true)
            {
                _history.Remove(car);
                continue;
            }

            var position = car.Status.Position;
            var (nearestId, distanceSquared) = _spline.WorldToSpline(position);
            if (nearestId < 0 || distanceSquared > MaxDistanceToSplineMeters * MaxDistanceToSplineMeters || points[nearestId].NextId < 0)
            {
                _history.Remove(car);
                continue;
            }

            var forward = Vector3.Normalize(points[points[nearestId].NextId].Position - points[nearestId].Position);
            float speed = MathF.Max(0, Vector3.Dot(car.Status.Velocity, forward));
            float acceleration = UpdateAcceleration(car, speed, serverTimeMilliseconds);
            var lanePlayer = new AiLanePlayer(car, speed, acceleration);

            foreach (int laneId in _spline.GetLanes(nearestId))
            {
                ref readonly var lanePoint = ref points[laneId];
                if (lanePoint.NextId < 0) continue;

                var laneForward = points[lanePoint.NextId].Position - lanePoint.Position;
                var laneForward2d = Vector2.Normalize(new Vector2(laneForward.X, laneForward.Z));
                var offset = position - lanePoint.Position;
                float lateral = MathF.Abs(offset.X * laneForward2d.Y - offset.Z * laneForward2d.X);

                if (lateral < maxLateral)
                {
                    _byPoint.TryAdd(laneId, lanePlayer);
                }
            }
        }
    }

    private float UpdateAcceleration(EntryCar car, float speed, long time)
    {
        float acceleration = 0;
        if (_history.TryGetValue(car, out var last))
        {
            long dt = time - last.Time;
            if (dt <= 0) return last.Acceleration;
            if (dt < 500)
            {
                acceleration = last.Acceleration + AccelerationSmoothing * ((speed - last.Speed) / (dt / 1000.0f) - last.Acceleration);
            }
        }

        _history[car] = (speed, acceleration, time);
        return acceleration;
    }
}

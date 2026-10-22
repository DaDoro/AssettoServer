using System;
using System.Linq;
using System.Numerics;
using AssettoServer.Commands.Attributes;
using AssettoServer.Network.ClientMessages;
using AssettoServer.Server.Ai;
using AssettoServer.Server.Ai.Splines;
using JetBrains.Annotations;
using Qmmands;

namespace AssettoServer.Commands.Modules;

/// <summary>
/// Fork: admin teleport to places on the AI splines, for testing traffic. The car is placed on a traffic lane facing
/// the traffic direction. Needs the AS_TeleportCar handler of assettoserver.lua.
/// </summary>
[RequireAdmin]
[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
public class TeleportModule : ACModuleBase
{
    // Spline points are about 0.4 m above the road (car position when recorded), a bit more avoids spawning in the road
    private const float HeightOffsetMeters = 0.5f;
    private const float RampDistanceMeters = 450;
    private const float ZoneDistanceMeters = 300;

    private AiSpline? Spline => Context.Services?.GetService(typeof(AiSpline)) as AiSpline;
    private AiMerges? Merges => Context.Services?.GetService(typeof(AiMerges)) as AiMerges;

    [Command("where")]
    public void Where()
    {
        if (Client == null) return;
        var position = Client.EntryCar.Status.Position;
        var spline = Spline;
        if (spline == null)
        {
            Reply($"x {position.X:F0} z {position.Z:F0} y {position.Y:F0}");
            return;
        }

        var (pointId, distanceSquared) = spline.WorldToSpline(position);
        Reply($"x {position.X:F0} z {position.Z:F0} y {position.Y:F0}, nearest AI point {pointId} ({MathF.Sqrt(distanceSquared):F1} m)");
    }

    [Command("tp")]
    public void TeleportToPosition(float x, float z, float speedKph = 0)
    {
        var spline = Spline;
        if (spline == null)
        {
            Reply("AI splines not loaded");
            return;
        }

        var points = spline.Points;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < points.Length; i++)
        {
            float dx = points[i].Position.X - x, dz = points[i].Position.Z - z;
            float distance = dx * dx + dz * dz;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        Teleport(spline, best, speedKph, $"x {x:F0} z {z:F0}");
    }

    [Command("tppt")]
    public void TeleportToPoint(int pointId, float speedKph = 0)
    {
        var spline = Spline;
        if (spline == null)
        {
            Reply("AI splines not loaded");
            return;
        }

        if (pointId < 0 || pointId >= spline.Points.Length)
        {
            Reply($"Point must be 0..{spline.Points.Length - 1}");
            return;
        }

        Teleport(spline, pointId, speedKph, $"point {pointId}");
    }

    /// <summary>Without a number: list the on-ramps. With a number: the main lane 450 m before that on-ramp merges</summary>
    [Command("tpramp")]
    public void TeleportToRamp(int number = -1, float speedKph = 0)
    {
        var spline = Spline;
        var merges = Merges;
        if (spline == null || merges == null || !merges.Enabled)
        {
            Reply("Merges are disabled (--ai-merges)");
            return;
        }

        var ramps = merges.RampEntries;
        if (number < 0 || number >= ramps.Count)
        {
            Reply($"On-ramps 0..{ramps.Count - 1}, /tpramp <n> puts you on the main lane {RampDistanceMeters:F0} m before the merge:");
            Reply(string.Join(", ", ramps.Select((ramp, i) =>
            {
                var position = spline.Points[ramp.LanePointId].Position;
                return $"{i}: {position.X:F0} {position.Z:F0}";
            })));
            return;
        }

        int pointId = WalkBack(spline, ramps[number].LanePointId, RampDistanceMeters);
        Teleport(spline, pointId, speedKph, $"on-ramp {number}");
    }

    /// <summary>300 m before a merge zone (seg from the trace header or the analysis), on the lane of that zone</summary>
    [Command("tpzone")]
    public void TeleportToZone(int segment, float speedKph = 0)
    {
        var spline = Spline;
        var merges = Merges;
        if (spline == null || merges == null || !merges.Enabled)
        {
            Reply("Merges are disabled (--ai-merges)");
            return;
        }

        if (segment < 0 || segment >= merges.Segments.Count)
        {
            Reply($"Zone must be 0..{merges.Segments.Count - 1}");
            return;
        }

        int pointId = WalkBack(spline, merges.Segments[segment].First, ZoneDistanceMeters);
        Teleport(spline, pointId, speedKph, $"zone {segment}");
    }

    private static int WalkBack(AiSpline spline, int pointId, float distance)
    {
        var points = spline.Points;
        float travelled = 0;
        while (travelled < distance && points[pointId].PreviousId >= 0)
        {
            pointId = points[pointId].PreviousId;
            travelled += points[pointId].Length;
        }

        return pointId;
    }

    private void Teleport(AiSpline spline, int pointId, float speedKph, string what)
    {
        if (Client == null)
        {
            Reply("Only for players in game");
            return;
        }

        var points = spline.Points;
        ref readonly var point = ref points[pointId];
        var forward = point.NextId >= 0
            ? points[point.NextId].Position - point.Position
            : point.Position - points[point.PreviousId].Position;
        forward = Vector3.Normalize(forward);

        Client.SendPacket(new TeleportCarPacket
        {
            Position = point.Position + new Vector3(0, HeightOffsetMeters, 0),
            Direction = forward,
            Velocity = forward * (speedKph / 3.6f)
        });
        Reply($"Teleported to {what}: point {pointId}, x {point.Position.X:F0} z {point.Position.Z:F0}");
    }
}

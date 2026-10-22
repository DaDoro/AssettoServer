using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using AssettoServer.Server.Ai.Splines;
using AssettoServer.Server.Configuration;
using Serilog;

namespace AssettoServer.Server.Ai;

/// <summary>How a car behaves in a merge zone, by the lane it is in</summary>
public enum MergeRole : byte
{
    None,
    /// <summary>Lane that continues: sees cars of the other lane only when they decided to merge in front</summary>
    Priority,
    /// <summary>Lane that ends (on-ramp, lane drop): picks a gap in the other lane or waits at the zone entry</summary>
    Yield,
    /// <summary>Diverge (start of an exit): cars of both lanes simply follow each other</summary>
    Plain
}

public enum MergeZoneType : byte
{
    Merge,
    Diverge,
    Overlap
}

/// <summary>A run of consecutive points of one lane that are closer than ZoneDistanceMeters to another lane</summary>
public record MergeSegment(int Id, int First, int Last)
{
    public int Partner { get; set; } = -1;
    public MergeZoneType Type { get; set; } = MergeZoneType.Overlap;
    public MergeRole Role { get; set; } = MergeRole.None;
}

/// <summary>Entry of an on-ramp merge: first zone point of the ramp and its twin in the lane the ramp merges into</summary>
public record RampEntry(int Segment, int RampPointId, int LanePointId)
{
    public long NextSpawnAllowed { get; set; }
}

/// <summary>
/// Merge zones (fork, stage 3). AI cars see only cars registered at the points of their own lane. Where two lanes
/// of different splines run closer than a car width (end of an on-ramp before ConnectEnd, start of an exit, overlapping
/// lanes of the traffic data) cars of both lanes drive through each other. On load every point gets a "twin": the
/// nearest point of such a lane. Cars approaching a zone project cars of the twin lane onto their own lane by the
/// distance to the zone entry. Built only with AiParams.Merges.Enabled.
/// </summary>
public class AiMerges
{
    public bool Enabled { get; }
    public IReadOnlyList<MergeSegment> Segments => _segments;
    public IReadOnlyList<RampEntry> RampEntries => _rampEntries;

    private readonly int[] _twin = [];
    private readonly int[] _segmentOf = [];
    private readonly List<MergeSegment> _segments = [];
    private readonly List<RampEntry> _rampEntries = [];

    // Points of the own lane this close along the lane are not twins (m, about 25 points)
    private const int ChainExclusionPoints = 25;
    // Twins must run in the same direction: cos of the angle between the lanes
    private const float MinDirectionCos = 0.9f;
    private const float MaxHeightDifference = 2.0f;
    // Shorter segments are single touching points, not zones
    private const int MinSegmentPoints = 4;
    // Points without a twin inside a segment (lanes wobbling around the threshold)
    private const int MaxSegmentGapPoints = 4;
    // How far to look for ConnectEnd / branch start from a segment end, in points
    private const int JunctionSearchPoints = 30;

    public AiMerges(ACServerConfiguration configuration, AiSpline spline)
    {
        var mergeParams = configuration.Extra.AiParams.Merges;
        Enabled = mergeParams.Enabled;
        if (!Enabled) return;

        var stopwatch = Stopwatch.StartNew();
        var points = spline.Points;
        _twin = new int[points.Length];
        _segmentOf = new int[points.Length];
        Array.Fill(_twin, -1);
        Array.Fill(_segmentOf, -1);

        FindTwins(spline, mergeParams.ZoneDistanceMeters);
        BuildSegments(spline);
        ClassifySegments(spline, mergeParams.OverlapZones);

        int merges = 0, diverges = 0, overlaps = 0;
        foreach (var segment in _segments)
        {
            if (segment.Role == MergeRole.None) continue;
            switch (segment.Type)
            {
                case MergeZoneType.Merge when segment.Role == MergeRole.Yield: merges++; break;
                case MergeZoneType.Diverge: diverges++; break;
                case MergeZoneType.Overlap when segment.Role == MergeRole.Yield: overlaps++; break;
            }
        }

        Log.Information("AI merges: {Merges} merges, {Diverges} diverge lane segments, {Overlaps} overlapping lanes, {Ramps} on-ramps for spawning ({Ms} ms)",
            merges, diverges, overlaps, _rampEntries.Count, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Nearest point of another lane closer than ZoneDistanceMeters, -1 if none</summary>
    public int Twin(int pointId) => pointId >= 0 && pointId < _twin.Length ? _twin[pointId] : -1;

    public int SegmentOf(int pointId) => pointId >= 0 && pointId < _segmentOf.Length ? _segmentOf[pointId] : -1;

    public MergeRole RoleOf(int pointId)
    {
        int segment = SegmentOf(pointId);
        return segment >= 0 ? _segments[segment].Role : MergeRole.None;
    }

    /// <summary>A lane that ends in a merge (Yield) starts within <paramref name="distance"/> along the lane</summary>
    public bool HasYieldAhead(AiSpline spline, int pointId, float distance)
    {
        if (!Enabled) return false;
        var points = spline.Points;
        float travelled = 0;
        while (pointId >= 0 && travelled < distance)
        {
            if (RoleOf(pointId) == MergeRole.Yield) return true;
            travelled += points[pointId].Length;
            pointId = points[pointId].NextId;
        }

        return false;
    }

    private void FindTwins(AiSpline spline, float zoneDistance)
    {
        var points = spline.Points;
        float cellSize = zoneDistance;
        float maxDistanceSquared = zoneDistance * zoneDistance;

        var grid = new Dictionary<long, List<int>>();
        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
        (int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / cellSize), (int)MathF.Floor(p.Z / cellSize));

        for (int i = 0; i < points.Length; i++)
        {
            var (cx, cz) = Cell(points[i].Position);
            long key = Key(cx, cz);
            if (!grid.TryGetValue(key, out var list))
            {
                list = [];
                grid[key] = list;
            }
            list.Add(i);
        }

        for (int i = 0; i < points.Length; i++)
        {
            var position = points[i].Position;
            var (cx, cz) = Cell(position);
            int best = -1;
            float bestDistanceSquared = maxDistanceSquared;

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue(Key(cx + dx, cz + dz), out var list)) continue;
                foreach (int j in list)
                {
                    if (Math.Abs(i - j) <= 2) continue;
                    var other = points[j].Position;
                    float distanceSquared = Vector3.DistanceSquared(position, other);
                    if (distanceSquared >= bestDistanceSquared || MathF.Abs(position.Y - other.Y) > MaxHeightDifference) continue;
                    if (!IsSameDirection(spline, i, j) || IsRelated(spline, i, j)) continue;

                    best = j;
                    bestDistanceSquared = distanceSquared;
                }
            }

            _twin[i] = best;
        }
    }

    private static Vector3 Forward(AiSpline spline, int pointId)
    {
        var points = spline.Points;
        ref readonly var point = ref points[pointId];
        var forward = point.NextId >= 0
            ? points[point.NextId].Position - point.Position
            : point.PreviousId >= 0 ? point.Position - points[point.PreviousId].Position : Vector3.Zero;
        float length = forward.Length();
        return length > 1e-6f ? forward / length : Vector3.Zero;
    }

    private static bool IsSameDirection(AiSpline spline, int a, int b) => Vector3.Dot(Forward(spline, a), Forward(spline, b)) > MinDirectionCos;

    /// <summary>
    /// Same lane close along the lane, including ConnectEnd continuations. Adjacent lanes (LeftId/RightId) are not
    /// excluded: regular lanes are further apart than ZoneDistanceMeters (on SRP at least 2.75 m), adjacent lanes closer
    /// than that are two lines drawn on one lane and conflict like any other overlap.
    /// </summary>
    private static bool IsRelated(AiSpline spline, int a, int b)
    {
        var points = spline.Points;
        return IsOnChain(points, a, b) || IsOnChain(points, b, a);
    }

    private static bool IsOnChain(ReadOnlySpan<SplinePoint> points, int from, int target)
    {
        int next = from, previous = from;
        for (int i = 0; i < ChainExclusionPoints; i++)
        {
            if (next >= 0) next = points[next].NextId;
            if (previous >= 0) previous = points[previous].PreviousId;
            if (next == target || previous == target) return true;
        }

        return false;
    }

    private void BuildSegments(AiSpline spline)
    {
        var points = spline.Points;
        int first = -1, last = -1;

        void Close()
        {
            if (first < 0) return;
            if (last - first + 1 >= MinSegmentPoints)
            {
                var segment = new MergeSegment(_segments.Count, first, last);
                _segments.Add(segment);
                for (int k = first; k <= last; k++) _segmentOf[k] = segment.Id;
            }
            else
            {
                for (int k = first; k <= last; k++) _twin[k] = -1;
            }
            first = last = -1;
        }

        for (int i = 0; i < points.Length; i++)
        {
            if (_twin[i] < 0) continue;

            bool continues = first >= 0 && i - last <= MaxSegmentGapPoints + 1 && IsContiguous(points, last, i);
            if (!continues)
            {
                Close();
                first = i;
            }
            last = i;
        }
        Close();

        // Gap points inside a segment get the twin of the previous point, so the zone has no holes
        foreach (var segment in _segments)
        {
            for (int k = segment.First + 1; k <= segment.Last; k++)
            {
                if (_twin[k] < 0) _twin[k] = _twin[k - 1];
            }
        }

        foreach (var segment in _segments)
        {
            var partners = new Dictionary<int, int>();
            for (int k = segment.First; k <= segment.Last; k++)
            {
                int partner = _segmentOf[_twin[k]];
                if (partner >= 0) partners[partner] = partners.GetValueOrDefault(partner) + 1;
            }

            int best = -1, count = 0;
            foreach (var (partner, n) in partners)
            {
                if (n > count) (best, count) = (partner, n);
            }
            segment.Partner = best;
        }
    }

    private static bool IsContiguous(ReadOnlySpan<SplinePoint> points, int from, int to)
    {
        int k = from;
        while (k >= 0 && k < to)
        {
            if (points[k].NextId != k + 1) return false;
            k++;
        }
        return k == to;
    }

    private void ClassifySegments(AiSpline spline, bool overlapZones)
    {
        var points = spline.Points;
        var junctions = spline.Junctions;

        // Merges: the lane ends with ConnectEnd into the partner lane near the partner segment
        foreach (var segment in _segments)
        {
            if (segment.Partner < 0) continue;
            var partner = _segments[segment.Partner];

            int k = segment.Last;
            for (int i = 0; i < JunctionSearchPoints && k >= 0 && points[k].NextId == k + 1; i++) k++;
            if (k < 0 || points[k].NextId < 0 || points[k].JunctionStartId < 0) continue;

            ref readonly var junction = ref junctions[points[k].JunctionStartId];
            int end = junction.EndPointId;
            bool isConnectEnd = end == points[k].NextId;
            if (!isConnectEnd || end < partner.First - 2 * JunctionSearchPoints || end > partner.Last + 2 * JunctionSearchPoints) continue;

            segment.Type = MergeZoneType.Merge;
            segment.Role = MergeRole.Yield;
            if (partner.Role != MergeRole.Yield)
            {
                partner.Type = MergeZoneType.Merge;
                partner.Role = MergeRole.Priority;
            }
            _rampEntries.Add(new RampEntry(segment.Id, segment.First, _twin[segment.First]));
        }

        foreach (var segment in _segments)
        {
            if (segment.Role != MergeRole.None) continue;
            var partner = segment.Partner >= 0 ? _segments[segment.Partner] : null;

            if (StartsAtBranch(points, segment) || (partner != null && StartsAtBranch(points, partner)) || partner == null)
            {
                segment.Type = MergeZoneType.Diverge;
                segment.Role = MergeRole.Plain;
                continue;
            }

            if (!overlapZones) continue;

            segment.Type = MergeZoneType.Overlap;
            // Overlapping lanes without a junction: one of them has to give way, the one with lower ids keeps priority
            if (partner.Role == MergeRole.None)
            {
                bool priority = segment.First < partner.First;
                segment.Role = priority ? MergeRole.Priority : MergeRole.Yield;
                partner.Type = MergeZoneType.Overlap;
                partner.Role = priority ? MergeRole.Yield : MergeRole.Priority;
            }
            else
            {
                segment.Role = partner.Role == MergeRole.Priority ? MergeRole.Yield : MergeRole.Priority;
            }
        }

        // Segments without a role are not zones
        foreach (var segment in _segments)
        {
            if (segment.Role != MergeRole.None) continue;
            for (int k = segment.First; k <= segment.Last; k++)
            {
                _twin[k] = -1;
                _segmentOf[k] = -1;
            }
        }
    }

    /// <summary>The lane starts shortly before the segment as the end of a junction (an exit)</summary>
    private static bool StartsAtBranch(ReadOnlySpan<SplinePoint> points, MergeSegment segment)
    {
        int k = segment.First;
        for (int i = 0; i < JunctionSearchPoints && k > 0 && points[k].PreviousId == k - 1; i++) k--;
        return points[k].PreviousId < 0 && points[k].JunctionEndId >= 0;
    }
}

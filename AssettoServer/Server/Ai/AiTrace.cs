using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AssettoServer.Server.Ai.Splines;
using AssettoServer.Server.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Serilog;

namespace AssettoServer.Server.Ai;

/// <summary>
/// Records the whole AI traffic to a JSONL file for offline analysis. Enabled by the --ai-trace command line option.
/// JSONL, one record per line: a header with the track and the AI parameters, then snapshots of all cars at SnapshotHz and AI events.
/// </summary>
public class AiTrace : BackgroundService
{
    public const int FormatVersion = 1;
    public const int SnapshotHz = 5;

    public bool Enabled { get; }

    private readonly ACServerConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly AiSpline _spline;
    private readonly AiMerges _merges;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<AiState> _states = new();
    private readonly StringBuilder _sb = new();

    private static int _nextStateId;

    public AiTrace(ACServerConfiguration configuration, SessionManager sessionManager, EntryCarManager entryCarManager, AiSpline spline, AiMerges merges)
    {
        _merges = merges;
        _configuration = configuration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _spline = spline;
        Enabled = !string.IsNullOrEmpty(configuration.AiTraceDirectory);
    }

    public static int NextStateId() => Interlocked.Increment(ref _nextStateId);

    /// <summary>Queues an event line. <paramref name="fields"/> is a JSON fragment without braces, e.g. "\"pt\":12".</summary>
    public void Event(string type, AiState? state, string fields = "")
    {
        if (!Enabled) return;

        var sb = new StringBuilder(160);
        sb.Append(CultureInfo.InvariantCulture, $"{{\"t\":{_sessionManager.ServerTimeMilliseconds},\"e\":\"{type}\"");
        if (state != null)
        {
            sb.Append(CultureInfo.InvariantCulture, $",\"id\":{state.TraceId},\"s\":{state.EntryCar.SessionId},\"sc\":{state.SpawnCounter}");
        }
        if (fields.Length > 0)
        {
            sb.Append(',').Append(fields);
        }
        sb.Append('}');
        _lines.Writer.TryWrite(sb.ToString());
    }

    public static string F(float value) => float.IsFinite(value) ? MathF.Round(value, 2).ToString(CultureInfo.InvariantCulture) : "null";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;

        Directory.CreateDirectory(_configuration.AiTraceDirectory!);
        string path = Path.Join(_configuration.AiTraceDirectory, $"ai_trace_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        Log.Information("AI trace: writing to {Path}", Path.GetFullPath(path));

        _lines.Writer.TryWrite(BuildHeader());

        var writerTask = WriteLinesAsync(path);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / SnapshotHz));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _lines.Writer.TryWrite(BuildSnapshot());
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "AI trace: error building snapshot");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _lines.Writer.TryComplete();
            await writerTask;
        }
    }

    private async Task WriteLinesAsync(string path)
    {
        try
        {
            await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            long lastFlush = Environment.TickCount64;
            await foreach (var line in _lines.Reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(line);
                if (Environment.TickCount64 - lastFlush > 1000)
                {
                    await writer.FlushAsync();
                    lastFlush = Environment.TickCount64;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AI trace: error writing {Path}", path);
        }
    }

    private string BuildHeader()
    {
        var slots = new List<object>();
        foreach (var car in _entryCarManager.EntryCars)
        {
            slots.Add(new
            {
                s = car.SessionId,
                model = car.Model,
                skin = car.Skin,
                ai = car.AiMode.ToString(),
                pre = car.VehicleLengthPreMeters,
                post = car.VehicleLengthPostMeters,
                lanes = car.AiAllowedLanes?.ConvertAll(l => l.ToString()),
                accel = car.AiAcceleration,
                decel = car.AiDeceleration,
                corner = car.AiCorneringSpeedFactor
            });
        }

        string aiParams;
        try
        {
            aiParams = JsonConvert.SerializeObject(_configuration.Extra.AiParams, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AI trace: cannot serialize AiParams");
            aiParams = "null";
        }

        var header = new
        {
            e = "header",
            format = FormatVersion,
            version = ThisAssembly.AssemblyInformationalVersion,
            utc = DateTime.UtcNow.ToString("O"),
            serverTimeMs = _sessionManager.ServerTimeMilliseconds,
            cwd = Directory.GetCurrentDirectory(),
            track = _configuration.Server.Track,
            layout = _configuration.Server.TrackConfig,
            points = _spline.Points.Length,
            junctions = _spline.Junctions.Length,
            hz = SnapshotHz,
            mergeZones = BuildMergeZones(),
            ai = "id,s,sc,pt,li,lc,x,y,z,yaw,v,vt,vmax,a,r,lead,gap,pgap,flags,lat,lcp,sh,mg,wa,gear,rpm,gas",
            pl = "s,x,y,z,yaw,v,pt,d,li,lc,flags,sa,wa,gear,rpm,gas,tfl,trl",
            slots
        };

        string json = JsonConvert.SerializeObject(header);
        return json.Substring(0, json.Length - 1) + ",\"aiParams\":" + aiParams + "}";
    }

    /// <summary>Merge zone segments with a role: id, type, role, first and last point, position of the first point</summary>
    private List<object> BuildMergeZones()
    {
        var zones = new List<object>();
        var points = _spline.Points;
        foreach (var segment in _merges.Segments)
        {
            if (segment.Role == MergeRole.None) continue;
            var position = points[segment.First].Position;
            zones.Add(new
            {
                seg = segment.Id,
                type = segment.Type.ToString(),
                role = segment.Role.ToString(),
                first = segment.First,
                last = segment.Last,
                partner = segment.Partner,
                x = MathF.Round(position.X),
                z = MathF.Round(position.Z)
            });
        }
        return zones;
    }

    private string BuildSnapshot()
    {
        var sb = _sb;
        sb.Clear();
        sb.Append(CultureInfo.InvariantCulture, $"{{\"t\":{_sessionManager.ServerTimeMilliseconds},\"ai\":[");

        bool first = true;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (!car.AiControlled) continue;

            _states.Clear();
            car.GetInitializedStates(_states);
            foreach (var state in _states)
            {
                int pt = state.CurrentSplinePointId;
                var (li, lc) = LaneOf(pt);
                var status = state.Status;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{state.TraceId},{car.SessionId},{state.SpawnCounter},{pt},{li},{lc},{F(status.Position.X)},{F(status.Position.Y)},{F(status.Position.Z)},{MathF.Round(status.Rotation.X, 3).ToString(CultureInfo.InvariantCulture)},{F(state.CurrentSpeed)},{F(state.TargetSpeed)},{F(state.MaxSpeed)},{F(state.Acceleration)},{(int)state.TraceReason},{state.TraceLeaderId},{F(state.ClosestAiObstacleDistance)},{F(state.TracePlayerDistance)},{(uint)status.StatusFlag},{F(state.TraceLateralOffset)},{state.TraceLaneChange},{state.ShadowSplinePointId},{state.TraceMerge},{status.WheelAngle},{status.Gear},{status.EngineRpm},{status.Gas}]");
            }
        }

        sb.Append("],\"pl\":[");
        first = true;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.AiControlled || car.Client?.HasSentFirstUpdate != true) continue;

            var status = car.Status;
            var (pt, distSq) = _spline.WorldToSpline(status.Position);
            var (li, lc) = LaneOf(pt);
            if (!first) sb.Append(',');
            first = false;
            sb.Append(CultureInfo.InvariantCulture,
                $"[{car.SessionId},{F(status.Position.X)},{F(status.Position.Y)},{F(status.Position.Z)},{MathF.Round(status.Rotation.X, 3).ToString(CultureInfo.InvariantCulture)},{F(status.Velocity.Length())},{pt},{F(MathF.Sqrt(distSq))},{li},{lc},{(uint)status.StatusFlag},{status.SteerAngle},{status.WheelAngle},{status.Gear},{status.EngineRpm},{status.Gas},{status.TyreAngularSpeed[0]},{status.TyreAngularSpeed[2]}]");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private (int LaneIndex, int LaneCount) LaneOf(int pointId)
    {
        if (pointId < 0) return (-1, 0);

        var lanes = _spline.GetLanes(pointId);
        return (lanes.IndexOf(pointId), lanes.Length);
    }
}

/// <summary>Why the AI has its current target speed. Written to the trace as an integer.</summary>
public enum AiTraceReason
{
    Free = 0,
    FollowAi = 1,
    FollowPlayer = 2,
    Corner = 3,
    StopAi = 4,
    StopPlayer = 5,
    CollisionStop = 6,
    IgnoringObstacles = 7,
    /// <summary>Merges: slows down or waits at the end of an on-ramp to let a car of the main lane pass</summary>
    MergeYield = 8
}

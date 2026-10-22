using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using AssettoServer.Server.Plugin;
using Autofac;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentValidation;
using JetBrains.Annotations;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeTypeResolvers;
#pragma warning disable CS0657

namespace AssettoServer.Server.Configuration;

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public partial class ACExtraConfiguration : ObservableObject
{
    [YamlMember(Description = "Enable Steam ticket validation. Requires CSP 0.1.75+ and a recent version of Content Manager")]
    public bool UseSteamAuth { get; init; } = false;
    [YamlMember(Description = "List of DLC App IDs that are required to join. Steam auth must be enabled. Possible values: https://steamdb.info/app/244210/dlc/")]
    public List<int> ValidateDlcOwnership { get; init; } = [];
    [YamlMember(Description = "Enable protection against cheats/hacks. 0 = No protection. 1 = Block all public cheats as of 2023-11-18 (ClientSecurityPlugin and CSP 0.2.0+ required)")]
    public int MandatoryClientSecurityLevel { get; init; }
    [YamlMember(Description = "Enable AFK autokick")]
    public bool EnableAntiAfk { get; set; } = true;
    [YamlMember(Description = "Maximum allowed AFK time before kick")]
    public int MaxAfkTimeMinutes { get; set; } = 10;
    [YamlMember(Description = "Players might try to get around the AFK kick by doing inputs once in a while without actually driving. Set this to MinimumSpeed to autokick players idling")]
    public AfkKickBehavior AfkKickBehavior { get; set; } = AfkKickBehavior.PlayerInput;
    [YamlMember(Description = "Maximum ping before autokick")]
    public int MaxPing { get; set; } = 500;
    [YamlMember(Description = "Maximum ping duration before autokick")]
    public int MaxPingSeconds { get; set; } = 10;
    [YamlMember(Description = "Force headlights on for all cars")]
    public bool ForceLights { get; set; }
    [YamlMember(Description = "Distance for network optimizations. Players outside of this range will send less updates to reduce network traffic")]
    public float NetworkBubbleDistance { get; init; } = 500;
    [YamlMember(Description = "Refresh rate for players outside of the network bubble")]
    public int OutsideNetworkBubbleRefreshRateHz { get; init; } = 4;
    [YamlMember(Description = "Enable server details in CM. Required for server description")]
    public bool EnableServerDetails { get; set; } = true;
    [YamlMember(Description = "Server description shown in Content Manager. EnableServerDetails must be on")]
    public string ServerDescription { get; set; } = "";
    [YamlMember(Description = "Link server time to real map time. For correct timezones there must be an entry for the map here: https://github.com/ac-custom-shaders-patch/acc-extension-config/blob/master/config/data_track_params.ini")]
    public bool EnableRealTime { get; set; } = false;
    [YamlMember(Description = "Enable new CSP weather handling. Allows rain and smooth weather transitions. Requires CSP 0.1.76+")]
    public bool EnableWeatherFx { get; init; } = false;
    [YamlMember(Description = "Lock server date to real date. This stops server time \"running away\" when using a high time multiplier, so that in-game sunrise/sunset times are based on the current date")]
    public bool LockServerDate { get; set; } = true;
    [YamlMember(Description = "Reduce track grip when the track is wet. This is much worse than proper CSP rain physics but allows you to run clients with public/Patreon CSP at the same time")]
    public double RainTrackGripReductionPercent { get; set; } = 0;
    [YamlMember(Description = "Enable AI traffic")]
    public bool EnableAi { get; init; } = false;
    [YamlMember(Description = "Override the country shown in CM. Please do not use this unless the autodetected country is wrong", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public List<string>? GeoParamsCountryOverride { get; init; } = null;
    [YamlMember(Description = "List of plugins to enable")]
    public List<string> EnablePlugins { get; init; } = [];
    [YamlMember(Description = "Ignore some common configuration errors. More info: https://assettoserver.org/docs/common-configuration-errors")]
    public IgnoreConfigurationErrors IgnoreConfigurationErrors { get; init; } = new();
    [YamlMember(Description = "Enable CSP client messages feature. Requires CSP 0.1.77+")]
    public bool EnableClientMessages { get; init; } = false;
    [YamlMember(Description = "Enable CSP UDP client messages feature. Required for VR head/hand syncing. Requires CSP 0.1.80+")]
    public bool EnableUdpClientMessages { get; init; } = false;
    [YamlMember(Description = "Log unknown CSP Lua client messages / online events")]
    public bool DebugClientMessages { get; set; } = false;
    [YamlMember(Description = "Enable CSP custom position updates. This is an improved version of batched position updates, reducing network traffic even further. CSP 0.1.77+ required")]
    public bool EnableCustomUpdate { get; set; } = false;
    [YamlMember(Description = "Maximum time a player can spend on the loading screen before being disconnected")]
    public int PlayerLoadingTimeoutMinutes { get; set; } = 10;
    [YamlMember(Description = "Maximum time the server will wait for a checksum response before disconnecting the player")]
    public int PlayerChecksumTimeoutSeconds { get; set; } = 40;
    [YamlMember(Description = "Send logs to a Loki instance, e.g. Grafana Cloud", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public LokiSettings? LokiSettings { get; init; }
    [YamlMember(Description = "Port to control the server using Source RCON protocol. 0 to disable.")]
    public ushort RconPort { get; init; } = 0;
    [YamlMember(Description = "Dump contents of welcome message and CSP extra options to a file. For debug purposes only.")]
    public bool DebugWelcomeMessage { get; init; } = false;
    [YamlMember(Description = "Force clients to use track params (coordinates, time zone) specified on the server. CSP 0.1.79+ required")]
    public bool ForceServerTrackParams { get; init; } = false;
    [YamlMember(Description = "Allow cars to have multiple data checksums. Instead of a single checksummed data.acd, you can have multiple data*.acd files in the car folder and players can join with any of these files")]
    public bool EnableAlternativeCarChecksums { get; init; } = false;
    [YamlMember(Description = "Enable the AC UDP plugin interface compatible with Kunos acServer plugins")]
    public bool EnableLegacyPluginInterface { get; init; } = false;
    [YamlMember(Description = "Automatically configure port forwards using UPnP or NAT-PMP. Empty = Enable on Windows when lobby registration is enabled. true = Always enable, detailed error log. false = Always disable")]
    public bool? EnableUPnP { get; init; } = false;
    [YamlMember(Description = "URL of custom loading screen image. Requires CSP 0.1.80+ and a recent version of Content Manager")]
    public string? LoadingImageUrl { get; set; }
    [YamlMember(Description = "Name and path of file-based user groups")]
    public Dictionary<string, string> UserGroups { get; init; } = new()
    {
        { "default_blacklist", "blacklist.txt" },
        { "default_whitelist", "whitelist.txt" },
        { "default_admins", "admins.txt" }
    };
    [YamlMember(Description = "Name of user group to be used for blacklist")]
    public string BlacklistUserGroup { get; init; } = "default_blacklist";
    [YamlMember(Description = "Name of user group to be used for whitelist")]
    public string WhitelistUserGroup { get; init; } = "default_whitelist";
    [YamlMember(Description = "Name of user group to be used for admins")]
    public string AdminUserGroup { get; init; } = "default_admins";
    [YamlMember(Description = "List of allowed origins for Cross-Origin Resource Sharing. Use this if you want to query this server from a website")]
    public List<string>? CorsAllowedOrigins { get; init; }
    [YamlMember(Description = "Allow a user group to execute specific admin commands")]
    public List<UserGroupCommandPermissions>? UserGroupCommandPermissions { get; init; }
    
    public AiParams AiParams { get; init; } = new();

    [YamlIgnore] public int MaxAfkTimeMilliseconds => MaxAfkTimeMinutes * 60_000;
    [YamlIgnore] public string Path { get; private set; } = null!;

    public void ToFile(string path)
    {
        using var stream = File.CreateText(path);
        var serializer = new SerializerBuilder().Build();
        serializer.Serialize(stream, this);
    }
        
    public static ACExtraConfiguration FromFile(string path)
    {
        using var stream = File.OpenText(path);

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        var yamlParser = new Parser(stream);
        yamlParser.Consume<StreamStart>();
        yamlParser.Accept<DocumentStart>(out _);

        var extraCfg = deserializer.Deserialize<ACExtraConfiguration>(yamlParser);

        extraCfg.Path = path;
        return extraCfg;
    }

    internal void LoadPluginConfig(ACPluginLoader loader, ContainerBuilder builder)
    {
        using var stream = File.OpenText(Path);

        var yamlParser = new Parser(stream);
        yamlParser.Consume<StreamStart>();
        yamlParser.Accept<DocumentStart>(out _);
        yamlParser.Accept<DocumentStart>(out _);
        
        var deserializerBuilder = new DeserializerBuilder().WithoutNodeTypeResolver(typeof(PreventUnknownTagsNodeTypeResolver));
        foreach (var plugin in loader.LoadedPlugins)
        {
            if (plugin.ConfigurationType != null)
            {
                deserializerBuilder.WithTagMapping("!" + plugin.ConfigurationType.Name, plugin.ConfigurationType);
            }
        }

        var deserializer = deserializerBuilder.Build();

        while (yamlParser.Accept<DocumentStart>(out _))
        {
            var pluginConfig = deserializer.Deserialize(yamlParser)!;

            foreach (var plugin in loader.LoadedPlugins)
            {
                if (plugin.ConfigurationType == pluginConfig.GetType() && plugin.ValidatorType != null)
                {
                    var validator = Activator.CreateInstance(plugin.ValidatorType)!;
                    var method = typeof(DefaultValidatorExtensions).GetMethod("ValidateAndThrow")!;
                    var generic = method.MakeGenericMethod(pluginConfig.GetType());
                    try
                    {
                        generic.Invoke(null, new[] { validator, pluginConfig });
                    }
                    catch (TargetInvocationException ex)
                    {
                        throw ex.InnerException ?? ex;
                    }

                    break;
                }
            }
            
            builder.RegisterInstance(pluginConfig).AsSelf();
        }
    }
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class IgnoreConfigurationErrors
{
    public bool MissingCarChecksums { get; init; }
    public bool MissingTrackParams { get; init; }
    public bool WrongServerDetails { get; init; }
    public bool UnsafeAdminWhitelist { get; init; }
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class LokiSettings
{
    public string? Url { get; init; }
    public string? Login { get; init; }
    public string? Password { get; init; }
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public partial class AiParams : ObservableObject
{
    [YamlMember(Description = "Radius around a player in which AI cars won't despawn")]
    public float PlayerRadiusMeters { get; set; } = 200.0f;
    [YamlMember(Description = "Offset the player radius in direction of the velocity of the player so AI cars will despawn earlier behind a player")]
    public float PlayerPositionOffsetMeters { get; set; } = 100.0f;
    [YamlMember(Description = "AFK timeout for players. Players who are AFK longer than this won't spawn AI cars")]
    public long PlayerAfkTimeoutSeconds { get; set; } = 10;
    [YamlMember(Description = "Maximum distance to the AI spline for a player to spawn AI cars. This helps with parts of the map without traffic so AI cars won't spawn far away from players")]
    public float MaxPlayerDistanceToAiSplineMeters { get; set; } = 7;
    [YamlMember(Description = "Minimum amount of spline points in front of a player where AI cars will spawn")]
    public int MinSpawnDistancePoints { get; set; } = 100;
    [YamlMember(Description = "Maximum amount of spline points in front of a player where AI cars will spawn")]
    public int MaxSpawnDistancePoints { get; set; } = 400;
    [YamlMember(Description = "Minimum distance between AI cars")]
    public int MinAiSafetyDistanceMeters { get; set; } = 20;
    [YamlMember(Description = "Maximum distance between AI cars")]
    public int MaxAiSafetyDistanceMeters { get; set; } = 70;
    [YamlMember(Description = "Minimum spawn distance for AI states of the same car slot. If you set this too low you risk AI states despawning or AI states becoming invisible for some players when multiple states are close together")]
    public float StateSpawnDistanceMeters { get; set; } = 1000;
    [YamlMember(Description = "Minimum distance between AI states of the same car slot. If states get closer than this one of them will be forced to despawn")]
    public float MinStateDistanceMeters { get; set; } = 200;
    [YamlMember(Description = "Minimum spawn distance to players")]
    public float SpawnSafetyDistanceToPlayerMeters { get; set; } = 150;
    [YamlMember(Description = "Minimum time in which a newly spawned AI car cannot despawn")]
    public int MinSpawnProtectionTimeSeconds { get; set; } = 4;
    [YamlMember(Description = "Maximum time in which a newly spawned AI car cannot despawn")]
    public int MaxSpawnProtectionTimeSeconds { get; set; } = 8;
    [YamlMember(Description = "Minimum time an AI car will stop/slow down after a collision")]
    public int MinCollisionStopTimeSeconds { get; set; } = 1;
    [YamlMember(Description = "Maximum time an AI car will stop/slow down after a collision")]
    public int MaxCollisionStopTimeSeconds { get; set; } = 3;
    [YamlMember(Description = "Default maximum AI speed. This is not an absolute maximum and can be overridden with RightLaneOffsetKph and MaxSpeedVariationPercent")]
    public float MaxSpeedKph { get; set; } = 80;
    [YamlMember(Description = "Speed offset for right lanes. Will be added to MaxSpeedKph")]
    public float RightLaneOffsetKph { get; set; } = 10;
    [YamlMember(Description = "Maximum speed variation")]
    public float MaxSpeedVariationPercent { get; set; } = 0.15f;
    
    [ObservableProperty]
    [property: YamlMember(Description = "Default AI car deceleration for obstacle/collision detection (m/s^2)")]
    private float _defaultDeceleration = 8.5f;
    
    [ObservableProperty]
    [property: YamlMember(Description = "Default AI car acceleration for obstacle/collision detection (m/s^2)")]
    private float _defaultAcceleration = 2.5f;
    
    [ObservableProperty]
    [property: YamlMember(Description = "Maximum AI car target count for AI slot overbooking. This is not an absolute maximum and might be slightly higher")]
    private int _maxAiTargetCount = 300;
    
    [ObservableProperty]
    [property: YamlMember(Description = "Number of AI cars per player the server will try to keep")]
    private int _aiPerPlayerTargetCount = 10;
    
    [YamlMember(Description = "Soft player limit, the server will stop accepting new players when this many players are reached. Use this to ensure a minimum amount of AI cars. 0 to disable.")]
    public int MaxPlayerCount { get; set; } = 0;
    [YamlMember(Description = "Hide AI car nametags and make them invisible on the minimap. Broken on CSP versions < 0.1.78")]
    public bool HideAiCars { get; set; } = false;
    
    [ObservableProperty]
    [property: YamlMember(Description = "AI spline height offset. Use this if the AI spline is too close to the ground")]
    private float _splineHeightOffsetMeters = 0;
    
    [YamlMember(Description = "Lane width for adjacent lane detection")]
    public float LaneWidthMeters { get; init; } = 3.0f;
    [YamlMember(Description = "Enable two way traffic. This will allow AI cars to spawn in lanes with the opposite direction of travel to the player.")]
    public bool TwoWayTraffic { get; set; } = false;
    [YamlMember(Description = "Enable traffic spawning if the player is driving the wrong way. Only takes effect when TwoWayTraffic is set to false.")]
    public bool WrongWayTraffic { get; set; } = true;
    
    [ObservableProperty]
    [property: YamlMember(Description = "AI cornering speed factor. Lower = AI cars will drive slower around corners.")]
    private float _corneringSpeedFactor = 0.65f;
    
    [ObservableProperty]
    [property: YamlMember(Description = "AI cornering brake distance factor. Lower = AI cars will brake later for corners.")]
    private float _corneringBrakeDistanceFactor = 3;
    
    [ObservableProperty]
    [property: YamlMember(Description = "AI cornering brake force factor. This is multiplied with DefaultDeceleration. Lower = AI cars will brake less hard for corners.")]
    private float _corneringBrakeForceFactor = 0.5f;
    
    [YamlMember(Description = "Name prefix for AI cars. Names will be in the form of '<NamePrefix> <SessionId>'")]
    public string NamePrefix { get; init; } = "Traffic";
    [YamlMember(Description = "Ignore obstacles for some time if the AI car is stopped for longer than x seconds")]
    public int IgnoreObstaclesAfterSeconds { get; set; } = 10;
    
    [ObservableProperty]
    [property: YamlMember(Description = "Apply scale to some traffic density related settings. Increasing this DOES NOT magically increase your traffic density, it is dependent on your other settings. Values higher than 1 not recommended.")]
    private float _trafficDensity = 1.0f;
    
    [YamlMember(Description = "Dynamic (hourly) traffic density. List must have exactly 24 entries in the format [0.2, 0.5, 1, 0.7, ...]")]
    public List<float>? HourlyTrafficDensity { get; set; }
    
    [ObservableProperty]
    [property: YamlMember(Description = "Tyre diameter of AI cars in meters, shouldn't have to be changed unless some cars are creating lots of smoke.")]
    private float _tyreDiameterMeters = 0.65f;
    
    [YamlMember(Description = "Apply some smoothing to AI spline camber")]
    public bool SmoothCamber { get; init; } = false;
    [YamlMember(Description = "Show debug overlay for AI cars")]
    public bool Debug { get; set; } = false;
    [YamlMember(Description = "Update interval for AI spawn point finder")]
    public int AiBehaviorUpdateIntervalHz { get; set; } = 2;
    [YamlMember(Description = "Enable AI car headlights during the day. Set to false to switch headlights by sun altitude, each car with its own twilight threshold (fork default: false, as in 0.0.55; 0.0.54: always on). --ai-vanilla turns it on")]
    public bool EnableDaytimeLights { get; set; } = false;
    [YamlMember(Description = "AI cars inside these areas will ignore all player obstacles")]
    public List<Sphere>? IgnorePlayerObstacleSpheres { get; set; }
    [YamlMember(Description = "Override some settings for newly spawned cars based on the number of lanes")]
    public Dictionary<int, LaneCountSpecificOverrides> LaneCountSpecificOverrides { get; set; } = new();
    [YamlMember(Description = "Car following model (fork default: Acc). Vanilla = AssettoServer 0.0.54, turns off LaneChanges, Merges and Incidents. Acc = Intelligent Driver Model with constant-acceleration heuristic (fork), see Acc. Can be overridden with the --ai-model command line option")]
    public AiFollowingModel FollowingModel { get; set; } = AiFollowingModel.Acc;
    [YamlMember(Description = "Parameters of the Acc following model, ignored with FollowingModel: Vanilla. Each AI car rolls its own values between Min and Max on spawn")]
    public AccParams Acc { get; set; } = new();
    [YamlMember(Description = "AI lane changes (fork, MOBIL on top of the Acc following model). Requires FollowingModel: Acc. Enabled by default (fork), also by the --ai-lane-changes command line option, --ai-vanilla turns it off")]
    public LaneChangeParams LaneChanges { get; set; } = new();
    [YamlMember(Description = "AI merges (fork): cars of lanes that run into each other (on-ramps, exits, overlapping lanes) see each other and merge in turns, plus spawning on on-ramps near players. Requires FollowingModel: Acc. Enabled by default (fork), also by the --ai-merges command line option, --ai-vanilla turns it off")]
    public MergeParams Merges { get; set; } = new();
    [YamlMember(Description = "AI incidents (fork): reaction to collisions by impact speed, going around stopped obstacles (needs LaneChanges). Requires FollowingModel: Acc. Enabled by default (fork), also by the --ai-incidents command line option, --ai-vanilla turns it off")]
    public IncidentParams Incidents { get; set; } = new();
    [YamlMember(Description = "AI visuals (fork): front wheels turn along the path, automatic gearbox, engine rpm and throttle for the engine sound. Doesn't change how the cars drive, works with any FollowingModel. Enabled by default (fork), also by the --ai-visuals command line option, --ai-vanilla turns it off")]
    public VisualParams Visuals { get; set; } = new();

    [YamlMember(Description = "Override some settings for specific car models")]
    public List<CarSpecificOverrides> CarSpecificOverrides { get; init; } = [
        new CarSpecificOverrides
        {
            Model = "my_car_model",
            Acceleration = 2.5f,
            Deceleration = 8.5f,
            AllowedLanes = new List<LaneSpawnBehavior> { LaneSpawnBehavior.Left, LaneSpawnBehavior.Middle, LaneSpawnBehavior.Right },
            MaxOverbooking = 1,
            CorneringSpeedFactor = 0.5f,
            CorneringBrakeDistanceFactor = 3,
            CorneringBrakeForceFactor = 0.5f,
            EngineIdleRpm = 800,
            EngineMaxRpm = 3000,
            MaxLaneCount = 2,
            MinLaneCount = 1,
            TyreDiameterMeters = 0.8f,
            SplineHeightOffsetMeters = 0,
            VehicleLengthPostMeters = 2,
            VehicleLengthPreMeters = 2,
            MinAiSafetyDistanceMeters = 20,
            MaxAiSafetyDistanceMeters = 25,
            MinCollisionStopTimeSeconds = 0,
            MaxCollisionStopTimeSeconds = 0,
            MinSpawnProtectionTimeSeconds = 30,
            MaxSpawnProtectionTimeSeconds = 60
        }
    ];

    [YamlIgnore] public float PlayerRadiusSquared => PlayerRadiusMeters * PlayerRadiusMeters;
    [YamlIgnore] public float PlayerAfkTimeoutMilliseconds => PlayerAfkTimeoutSeconds * 1000;
    [YamlIgnore] public float MaxPlayerDistanceToAiSplineSquared => MaxPlayerDistanceToAiSplineMeters * MaxPlayerDistanceToAiSplineMeters;
    [YamlIgnore] public int MinAiSafetyDistanceSquared => MinAiSafetyDistanceMeters * MinAiSafetyDistanceMeters;
    [YamlIgnore] public int MaxAiSafetyDistanceSquared => MaxAiSafetyDistanceMeters * MaxAiSafetyDistanceMeters;
    [YamlIgnore] public float StateSpawnDistanceSquared => StateSpawnDistanceMeters * StateSpawnDistanceMeters;
    [YamlIgnore] public float MinStateDistanceSquared => MinStateDistanceMeters * MinStateDistanceMeters;
    [YamlIgnore] public float SpawnSafetyDistanceToPlayerSquared => SpawnSafetyDistanceToPlayerMeters * SpawnSafetyDistanceToPlayerMeters;
    [YamlIgnore] public int MinSpawnProtectionTimeMilliseconds => MinSpawnProtectionTimeSeconds * 1000;
    [YamlIgnore] public int MaxSpawnProtectionTimeMilliseconds => MaxSpawnProtectionTimeSeconds * 1000;
    [YamlIgnore] public int MinCollisionStopTimeMilliseconds => MinCollisionStopTimeSeconds * 1000;
    [YamlIgnore] public int MaxCollisionStopTimeMilliseconds => MaxCollisionStopTimeSeconds * 1000;
    [YamlIgnore] public float MaxSpeedMs => MaxSpeedKph / 3.6f;
    [YamlIgnore] public float RightLaneOffsetMs => RightLaneOffsetKph / 3.6f;
    [YamlIgnore] public int IgnoreObstaclesAfterMilliseconds => IgnoreObstaclesAfterSeconds * 1000;
    [YamlIgnore] public int AiBehaviorUpdateIntervalMilliseconds => 1000 / AiBehaviorUpdateIntervalHz;
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class CarSpecificOverrides
{
    [YamlMember(Description = "Car model to match for these overrides")]
    public string? Model { get; init; }
    [YamlMember(Description = "AI spline height offset. Use this if the AI spline is too close to the ground")]
    public float? SplineHeightOffsetMeters { get; init; }
    [YamlMember(Description = "AI engine idle RPM")]
    public int? EngineIdleRpm { get; init; }
    [YamlMember(Description = "AI engine max RPM")]
    public int? EngineMaxRpm { get; init; }
    [YamlMember(Description = "AI car deceleration for obstacle/collision detection (m/s^2)")]
    public float? Acceleration { get; init; }
    [YamlMember(Description = "AI car acceleration for obstacle/collision detection (m/s^2)")]
    public float? Deceleration { get; init; }
    [YamlMember(Description = "AI cornering speed factor. Lower = AI cars will drive slower around corners.")]
    public float? CorneringSpeedFactor { get; init; }
    [YamlMember(Description = "AI cornering brake distance factor. Lower = AI cars will brake later for corners.")]
    public float? CorneringBrakeDistanceFactor { get; init; }
    [YamlMember(Description = "AI cornering brake force factor. This is multiplied with Deceleration. Lower = AI cars will brake less hard for corners.")]
    public float? CorneringBrakeForceFactor { get; init; }
    [YamlMember(Description = "Tyre diameter of AI cars in meters, shouldn't have to be changed unless cars are creating lots of smoke.")]
    public float? TyreDiameterMeters { get; set; }
    [YamlMember(Description = "Maximum number of AI states for a car slot of this car model")]
    public int? MaxOverbooking { get; set; }
    [YamlMember(Description = "Minimum time in which a newly spawned AI car cannot despawn")]
    public int? MinSpawnProtectionTimeSeconds { get; set; }
    [YamlMember(Description = "Maximum time in which a newly spawned AI car cannot despawn")]
    public int? MaxSpawnProtectionTimeSeconds { get; set; }
    [YamlMember(Description = "Minimum number of lanes needed to spawn a car of this car model")]
    public int? MinLaneCount { get; set; }
    [YamlMember(Description = "Maximum number of lanes needed to spawn a car of this car model")]
    public int? MaxLaneCount { get; set; }
    [YamlMember(Description = "Minimum time an AI car will stop/slow down after a collision")]
    public int? MinCollisionStopTimeSeconds { get; set; }
    [YamlMember(Description = "Maximum time an AI car will stop/slow down after a collision")]
    public int? MaxCollisionStopTimeSeconds { get; set; }
    [YamlMember(Description = "Wheelbase for the front wheel angle, with AiParams.Visuals (m)")]
    public float? WheelbaseMeters { get; set; }
    [YamlMember(Description = "Length of this vehicle in front of car origin")]
    public float? VehicleLengthPreMeters { get; set; }
    [YamlMember(Description = "Length of this vehicle behind car origin")]
    public float? VehicleLengthPostMeters { get; set; }
    [YamlMember(Description = "Minimum distance between AI cars")]
    public int? MinAiSafetyDistanceMeters { get; set; }
    [YamlMember(Description = "Maximum distance between AI cars")]
    public int? MaxAiSafetyDistanceMeters { get; set; }
    [YamlMember(Description = "List of allowed lanes for this car model. Possible values Left, Middle, Right")]
    public List<LaneSpawnBehavior>? AllowedLanes { get; set; }

    [YamlIgnore] public int? MinSpawnProtectionTimeMilliseconds => MinSpawnProtectionTimeSeconds * 1000;
    [YamlIgnore] public int? MaxSpawnProtectionTimeMilliseconds => MaxSpawnProtectionTimeSeconds * 1000;
    [YamlIgnore] public int? MinCollisionStopTimeMilliseconds => MinCollisionStopTimeSeconds * 1000;
    [YamlIgnore] public int? MaxCollisionStopTimeMilliseconds => MaxCollisionStopTimeSeconds * 1000;
    [YamlIgnore] public int? MinAiSafetyDistanceMetersSquared => MinAiSafetyDistanceMeters * MinAiSafetyDistanceMeters;
    [YamlIgnore] public int? MaxAiSafetyDistanceMetersSquared => MaxAiSafetyDistanceMeters * MaxAiSafetyDistanceMeters;
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class LaneCountSpecificOverrides
{
    [YamlMember(Description = "Minimum distance between AI cars")]
    public int MinAiSafetyDistanceMeters { get; set; }
    [YamlMember(Description = "Maximum distance between AI cars")]
    public int MaxAiSafetyDistanceMeters { get; set; }
    [YamlIgnore] public int MinAiSafetyDistanceSquared => MinAiSafetyDistanceMeters * MinAiSafetyDistanceMeters;
    [YamlIgnore] public int MaxAiSafetyDistanceSquared => MaxAiSafetyDistanceMeters * MaxAiSafetyDistanceMeters;
}

public enum AiFollowingModel
{
    Vanilla,
    Acc
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class AccParams
{
    [YamlMember(Description = "Minimum desired time gap to the car in front (s)")]
    public float MinTimeHeadwaySeconds { get; set; } = 0.3f;
    [YamlMember(Description = "Maximum desired time gap to the car in front (s)")]
    public float MaxTimeHeadwaySeconds { get; set; } = 0.9f;
    [YamlMember(Description = "Minimum bumper-to-bumper gap when standing in a jam (m)")]
    public float MinStandstillGapMeters { get; set; } = 2.0f;
    [YamlMember(Description = "Maximum bumper-to-bumper gap when standing in a jam (m)")]
    public float MaxStandstillGapMeters { get; set; } = 3.0f;
    [YamlMember(Description = "Minimum factor for the car acceleration (DefaultAcceleration or car specific Acceleration)")]
    public float MinAccelerationFactor { get; set; } = 0.6f;
    [YamlMember(Description = "Maximum factor for the car acceleration (DefaultAcceleration or car specific Acceleration)")]
    public float MaxAccelerationFactor { get; set; } = 1.0f;
    [YamlMember(Description = "Minimum comfortable deceleration (m/s^2). Emergency braking is limited by DefaultDeceleration or car specific Deceleration")]
    public float MinComfortableDecelerationMs2 { get; set; } = 2.0f;
    [YamlMember(Description = "Maximum comfortable deceleration (m/s^2)")]
    public float MaxComfortableDecelerationMs2 { get; set; } = 3.0f;
    [YamlMember(Description = "Free road acceleration exponent. Higher = keeps accelerating harder close to the desired speed")]
    public float AccelerationExponent { get; set; } = 4.0f;
    [YamlMember(Description = "Use the Improved IDM (Treiber, Kesting, chapter 11.3.4): a driver keeps its desired gap also when the car ahead is only a little slower than its desired speed. With the plain IDM (false) the gap is 2-3 times longer then, columns of cars spread out")]
    public bool ImprovedIdm { get; set; } = true;
    [YamlMember(Description = "Coolness factor, 0 = plain IDM, close to 1 = calm reaction to cut-ins of faster cars")]
    public float Coolness { get; set; } = 0.99f;
    [YamlMember(Description = "Desired speed variation, replaces MaxSpeedVariationPercent. 0.1 = +-5%")]
    public float DesiredSpeedVariationPercent { get; set; } = 0.1f;
    [YamlMember(Description = "Spread RightLaneOffsetKph across lanes with LaneSpeedFractions. false = every lane with a lane on its left gets the full offset, as in vanilla")]
    public bool LaneSpeedGradient { get; set; } = true;
    [YamlMember(Description = "Share of RightLaneOffsetKph per lane, leftmost lane first, by number of lanes. Lane counts not listed get an even gradient from 0 to 1. The speed difference of adjacent lanes sets how long cars stay side by side")]
    public Dictionary<int, List<float>> LaneSpeedFractions { get; set; } = new()
    {
        { 3, [0.0f, 0.85f, 1.0f] },
        { 4, [0.0f, 0.7f, 0.9f, 1.0f] },
        { 5, [0.0f, 0.6f, 0.8f, 0.9f, 1.0f] }
    };
    [YamlMember(Description = "Look ahead for cars and corners at least this far (m)")]
    public float MinLookaheadMeters { get; set; } = 100.0f;
    [YamlMember(Description = "Look ahead for cars and corners this many seconds at current speed, if longer than MinLookaheadMeters")]
    public float LookaheadSeconds { get; set; } = 6.0f;
    [YamlMember(Description = "Brake lights turn on when decelerating harder than this (m/s^2)")]
    public float BrakeLightsOnDecelerationMs2 { get; set; } = 1.5f;
    [YamlMember(Description = "Brake lights turn off when decelerating softer than this (m/s^2)")]
    public float BrakeLightsOffDecelerationMs2 { get; set; } = 0.5f;
    [YamlMember(Description = "A player blocks an AI lane when any part of the car is inside it. Half of the player car width (m)")]
    public float PlayerHalfWidthMeters { get; set; } = 0.9f;
    [YamlMember(Description = "Car length for gaps when VehicleLengthPreMeters and VehicleLengthPostMeters are not set, split evenly (m)")]
    public float DefaultVehicleLengthMeters { get; set; } = 4.4f;
    [YamlMember(Description = "Don't spawn a car where the car behind would have to brake for it, move the spawn point forward instead. false = only MinAiSafetyDistanceMeters/MaxAiSafetyDistanceMeters, as in vanilla")]
    public bool SpawnFollowerCheck { get; set; } = true;
    [YamlMember(Description = "Near a player (SpawnFollowerNearPlayerMeters) the car behind may brake for a newly spawned car at most this hard (m/s^2). Below BrakeLightsOnDecelerationMs2, so the player doesn't see brake lights")]
    public float SpawnFollowerMaxDecelerationMs2 { get; set; } = 1.0f;
    [YamlMember(Description = "Spawn points closer than this to a player use SpawnFollowerMaxDecelerationMs2. Further away the car behind may brake up to its comfortable deceleration: the player doesn't see it, and the flow stays as dense as with vanilla spawning (m)")]
    public float SpawnFollowerNearPlayerMeters { get; set; } = 400.0f;
    [YamlMember(Description = "Move the spawn point forward along its lane at most this far looking for a place where the car behind doesn't brake (m)")]
    public float SpawnMaxShiftMeters { get; set; } = 100.0f;
    [YamlMember(Description = "Look for the cars ahead of and behind a spawn point this far (m)")]
    public float SpawnSearchMeters { get; set; } = 150.0f;
    [YamlMember(Description = "Share of drivers that are slower than their lane by SlowDriverMinSlowdown..SlowDriverMaxSlowdown, in any lane. They don't move over to let faster cars pass, so queues form behind them, as in real traffic")]
    public float SlowDriverShare { get; set; } = 0.12f;
    [YamlMember(Description = "Slow drivers: minimum share of the desired speed they are slower by")]
    public float SlowDriverMinSlowdown { get; set; } = 0.1f;
    [YamlMember(Description = "Slow drivers: maximum share of the desired speed they are slower by")]
    public float SlowDriverMaxSlowdown { get; set; } = 0.2f;
    [YamlMember(Description = "Share of spawns placed right behind a car at the following distance of the new car and at the speed of the car ahead, so that cars drive in columns. The rest spawn in gaps. Needs SpawnFollowerCheck")]
    public float PlatoonSpawnShare { get; set; } = 0.5f;
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class IncidentParams
{
    [YamlMember(Description = "Enable incident handling. Disabled = every collision stops the car for MinCollisionStopTimeSeconds..MaxCollisionStopTimeSeconds, as in vanilla")]
    public bool Enabled { get; set; } = true;
    [YamlMember(Description = "A collision at this impact speed or faster is a crash: the car stops. Slower is a touch: hazards and a short soft brake (km/h)")]
    public float CrashSpeedKph { get; set; } = 20.0f;
    [YamlMember(Description = "Touch: minimum time with hazards on (s)")]
    public float TouchMinHazardsSeconds { get; set; } = 3.0f;
    [YamlMember(Description = "Touch: maximum time with hazards on (s)")]
    public float TouchMaxHazardsSeconds { get; set; } = 5.0f;
    [YamlMember(Description = "Touch: deceleration of the short brake (m/s^2)")]
    public float TouchBrakeDecelerationMs2 { get; set; } = 2.0f;
    [YamlMember(Description = "Touch: duration of the short brake (s)")]
    public float TouchBrakeSeconds { get; set; } = 0.5f;
    [YamlMember(Description = "Crash: deceleration to a stop (m/s^2), limited by DefaultDeceleration or car specific Deceleration")]
    public float CrashDecelerationMs2 { get; set; } = 5.0f;
    [YamlMember(Description = "Crash: minimum time standing with hazards on (s)")]
    public float CrashMinStopSeconds { get; set; } = 5.0f;
    [YamlMember(Description = "Crash: maximum time standing with hazards on (s)")]
    public float CrashMaxStopSeconds { get; set; } = 15.0f;
    [YamlMember(Description = "Go around stopped obstacles in the own lane (a standing player, a crashed car, a car waiting to go around) with a lane change, also at low speed. Needs LaneChanges")]
    public bool GoAround { get; set; } = true;
    [YamlMember(Description = "A player slower than this in the own lane is an obstacle to go around (km/h). Crashed cars always are, cars waiting to go around when standing")]
    public float ObstaclePlayerSpeedKph { get; set; } = 20.0f;
    [YamlMember(Description = "Extra lane change incentive while a standing obstacle is ahead (m/s^2)")]
    public float GoAroundIncentiveMs2 { get; set; } = 1.0f;
    [YamlMember(Description = "Minimum lane change length when going around, instead of LaneChanges.MinLengthMeters (m)")]
    public float GoAroundMinLengthMeters { get; set; } = 10.0f;
    [YamlMember(Description = "Stop this much further from a standing obstacle than from a car in a jam, to have room to go around (m)")]
    public float GoAroundExtraGapMeters { get; set; } = 6.0f;
    [YamlMember(Description = "Standing behind an obstacle that can be gone around: drive through it only after this time, instead of IgnoreObstaclesAfterSeconds (s)")]
    public float GoAroundTimeoutSeconds { get; set; } = 20.0f;
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class VisualParams
{
    [YamlMember(Description = "Enable AI visuals. Disabled = wheels straight, always 1st gear, rpm linear in speed, no throttle, as in vanilla")]
    public bool Enabled { get; set; } = true;
    [YamlMember(Description = "Wheelbase for the front wheel angle, unless set per car in CarSpecificOverrides (m)")]
    public float WheelbaseMeters { get; set; } = 2.7f;
    [YamlMember(Description = "Maximum front wheel angle (degrees)")]
    public float MaxWheelAngleDegrees { get; set; } = 35.0f;
    [YamlMember(Description = "Smoothing of the front wheel angle: time constant (s)")]
    public float WheelAngleSmoothingSeconds { get; set; } = 0.25f;
    [YamlMember(Description = "Forward gear ratios of the automatic gearbox, same for all cars. The wheel radius is half of TyreDiameterMeters")]
    public List<float> GearRatios { get; set; } = [3.5f, 2.1f, 1.4f, 1.0f, 0.8f, 0.65f];
    [YamlMember(Description = "Final drive ratio")]
    public float FinalDrive { get; set; } = 3.9f;
    [YamlMember(Description = "Upshift rpm when cruising. With more throttle the upshift rpm grows up to UpshiftRpmFullThrottle")]
    public int UpshiftRpm { get; set; } = 2200;
    [YamlMember(Description = "Upshift rpm at full throttle")]
    public int UpshiftRpmFullThrottle { get; set; } = 4500;
    [YamlMember(Description = "Downshift rpm when cruising or braking. With more throttle it grows up to DownshiftRpmFullThrottle")]
    public int DownshiftRpm { get; set; } = 1300;
    [YamlMember(Description = "Downshift rpm at full throttle (kickdown)")]
    public int DownshiftRpmFullThrottle { get; set; } = 2500;
    [YamlMember(Description = "Minimum time between gear changes (s)")]
    public float MinShiftIntervalSeconds { get; set; } = 0.8f;
    [YamlMember(Description = "Rpm limit, unless EngineMaxRpm is set per car in CarSpecificOverrides. EngineIdleRpm is the idle rpm")]
    public int MaxRpm { get; set; } = 6000;
    [YamlMember(Description = "Throttle while holding the speed (0..1). Accelerating adds up to full throttle at the maximum acceleration of the car, braking is zero")]
    public float CruiseThrottle { get; set; } = 0.2f;
    [YamlMember(Description = "Smoothing of the throttle: time constant (s)")]
    public float ThrottleSmoothingSeconds { get; set; } = 0.3f;
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class LaneChangeParams
{
    [YamlMember(Description = "Enable lane changes")]
    public bool Enabled { get; set; } = true;
    [YamlMember(Description = "How often each car considers a lane change (ms)")]
    public int DecisionIntervalMilliseconds { get; set; } = 500;
    [YamlMember(Description = "Lane changes only within this distance of a player (m), so they happen where players can see them and not right after spawning far ahead. 0 = everywhere")]
    public float MaxPlayerDistanceMeters { get; set; } = 250.0f;
    [YamlMember(Description = "Minimum politeness: share of the advantage or disadvantage of the cars behind that counts in the decision")]
    public float MinPoliteness { get; set; } = 0.1f;
    [YamlMember(Description = "Maximum politeness")]
    public float MaxPoliteness { get; set; } = 0.3f;
    [YamlMember(Description = "A lane change must improve the acceleration at least by this much (m/s^2)")]
    public float ThresholdMs2 { get; set; } = 0.1f;
    [YamlMember(Description = "Keep to the slow lane: a lane change towards the slow lane needs this much less advantage, away from it this much more (m/s^2). The slow lane is the leftmost one if RightLaneOffsetKph >= 0, else the rightmost one")]
    public float SlowLaneBiasMs2 { get; set; } = 0.3f;
    [YamlMember(Description = "An AI car behind in the target lane must not have to brake harder than this (m/s^2)")]
    public float SafeDecelerationMs2 { get; set; } = 3.0f;
    [YamlMember(Description = "A player behind in the target lane must not have to brake harder than this (m/s^2)")]
    public float PlayerSafeDecelerationMs2 { get; set; } = 1.5f;
    [YamlMember(Description = "Share of aggressive drivers. They cut in front of players using AggressivePlayerSafeDecelerationMs2 and ignore the disadvantage of players")]
    public float AggressiveDriverShare { get; set; } = 0.3f;
    [YamlMember(Description = "Aggressive drivers: a player behind in the target lane must not have to brake harder than this (m/s^2)")]
    public float AggressivePlayerSafeDecelerationMs2 { get; set; } = 4.0f;
    [YamlMember(Description = "Minimum gap in the target lane, in seconds at the speed of the rear car, plus its standstill gap. Applies to the gap in front and to an AI car behind. The Acc model alone would accept close cut-ins at equal speeds")]
    public float MinGapSeconds { get; set; } = 0.5f;
    [YamlMember(Description = "Minimum gap to a player behind in the target lane (s at the player speed, plus 2 m)")]
    public float PlayerMinGapSeconds { get; set; } = 1.0f;
    [YamlMember(Description = "Aggressive drivers: minimum gap to a player behind in the target lane (s at the player speed, plus 2 m)")]
    public float AggressivePlayerMinGapSeconds { get; set; } = 0.3f;
    [YamlMember(Description = "Abort a lane change in its first half if the car behind in the target lane has to brake harder than this (m/s^2)")]
    public float AbortDecelerationMs2 { get; set; } = 5.0f;
    [YamlMember(Description = "After an abort the car returns to its lane smoothly, starting with its lateral speed, over this time at the current speed (s)")]
    public float AbortReturnSeconds { get; set; } = 2.5f;
    [YamlMember(Description = "Look back for AI cars in the target lane (m)")]
    public float AiLookbackMeters { get; set; } = 100.0f;
    [YamlMember(Description = "Look back for players in the target lane (m). Players can be much faster than AI")]
    public float PlayerLookbackMeters { get; set; } = 500.0f;
    [YamlMember(Description = "Minimum lane change duration at the current speed (s)")]
    public float MinDurationSeconds { get; set; } = 3.0f;
    [YamlMember(Description = "Maximum lane change duration at the current speed (s)")]
    public float MaxDurationSeconds { get; set; } = 5.0f;
    [YamlMember(Description = "Minimum lane change length (m). The lateral movement follows the distance travelled, a stopped car doesn't move sideways")]
    public float MinLengthMeters { get; set; } = 25.0f;
    [YamlMember(Description = "Minimum time with the indicator on before moving (s)")]
    public float MinIndicatorSeconds { get; set; } = 0.6f;
    [YamlMember(Description = "Maximum time with the indicator on before moving (s)")]
    public float MaxIndicatorSeconds { get; set; } = 1.0f;
    [YamlMember(Description = "With the indicator on, wait this long for a safe gap before giving up (s). 0 = no waiting with the indicator on: the gap is checked silently, the indicator comes on just before moving")]
    public float MaxWaitSeconds { get; set; } = 0.0f;
    [YamlMember(Description = "Minimum pause after a lane change (s)")]
    public float MinCooldownSeconds { get; set; } = 5.0f;
    [YamlMember(Description = "Maximum pause after a lane change (s)")]
    public float MaxCooldownSeconds { get; set; } = 15.0f;
    [YamlMember(Description = "Pause after giving up a lane change (indicator on, but no safe gap or no advantage any more) (s)")]
    public float CancelCooldownSeconds { get; set; } = 1.0f;
    [YamlMember(Description = "Both lanes must continue without junctions and lane count changes for the lane change length plus this (m)")]
    public float ClearanceMeters { get; set; } = 30.0f;
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class MergeParams
{
    [YamlMember(Description = "Enable merges")]
    public bool Enabled { get; set; } = true;
    [YamlMember(Description = "Lanes of different splines closer than this run into each other: a merge zone (m). Cars are about 1.8 m wide")]
    public float ZoneDistanceMeters { get; set; } = 2.2f;
    [YamlMember(Description = "Lanes that overlap without a junction (errors in the traffic data) are handled as a merge: one of them gives way. false = cars keep driving through each other there, as in vanilla")]
    public bool OverlapZones { get; set; } = true;
    [YamlMember(Description = "A car merging in front of an AI car of the main lane must not make it brake harder than this (m/s^2)")]
    public float SafeDecelerationMs2 { get; set; } = 3.0f;
    [YamlMember(Description = "A car merging in front of a player must not make the player brake harder than this (m/s^2)")]
    public float PlayerSafeDecelerationMs2 { get; set; } = 1.5f;
    [YamlMember(Description = "Aggressive drivers (LaneChanges.AggressiveDriverShare): the same for merging in front of a player (m/s^2)")]
    public float AggressivePlayerSafeDecelerationMs2 { get; set; } = 4.0f;
    [YamlMember(Description = "Minimum gap to the AI car behind after merging, in seconds at its speed plus its standstill gap")]
    public float MinGapSeconds { get; set; } = 0.5f;
    [YamlMember(Description = "Minimum gap to a player behind after merging (s at the player speed, plus 2 m)")]
    public float PlayerMinGapSeconds { get; set; } = 1.0f;
    [YamlMember(Description = "Aggressive drivers: minimum gap to a player behind after merging (s at the player speed, plus 2 m)")]
    public float AggressivePlayerMinGapSeconds { get; set; } = 0.3f;
    [YamlMember(Description = "A car that decided to merge in front of a car gives up (before the zone) if that car would have to brake harder than this (m/s^2)")]
    public float AbortDecelerationMs2 { get; set; } = 5.0f;
    [YamlMember(Description = "A car waiting at the end of an on-ramp gets impatient after this time (s): the deceleration it accepts for an AI car behind grows to AbortDecelerationMs2 over PatienceRampSeconds")]
    public float PatienceSeconds { get; set; } = 4.0f;
    [YamlMember(Description = "See PatienceSeconds (s)")]
    public float PatienceRampSeconds { get; set; } = 6.0f;
    [YamlMember(Description = "A merging car looks back this far for AI cars of the main lane (m)")]
    public float AiLookbackMeters { get; set; } = 100.0f;
    [YamlMember(Description = "A merging car looks back this far for players of the main lane (m)")]
    public float PlayerLookbackMeters { get; set; } = 300.0f;
    [YamlMember(Description = "No lane changes into a lane that ends in a merge within this distance (m)")]
    public float NoLaneChangeIntoMergeMeters { get; set; } = 300.0f;
    [YamlMember(Description = "Share of spawn attempts that try an on-ramp ahead of the player first. 0 = no spawning on on-ramps")]
    public float RampSpawnShare { get; set; } = 0.3f;
    [YamlMember(Description = "On-ramp spawn: the merge must be at least this far ahead of the player (m)")]
    public float RampSpawnMinAheadMeters { get; set; } = 200.0f;
    [YamlMember(Description = "On-ramp spawn: the merge must be at most this far ahead of the player (m)")]
    public float RampSpawnMaxAheadMeters { get; set; } = 450.0f;
    [YamlMember(Description = "On-ramp spawn: the car appears at least this far before the merge zone (m)")]
    public float RampSpawnMinBeforeMergeMeters { get; set; } = 80.0f;
    [YamlMember(Description = "On-ramp spawn: the car appears at most this far before the merge zone (m)")]
    public float RampSpawnMaxBeforeMergeMeters { get; set; } = 200.0f;
    [YamlMember(Description = "On-ramp spawn: at most one car per on-ramp in this time (s)")]
    public float RampSpawnIntervalSeconds { get; set; } = 5.0f;
}

public enum AfkKickBehavior
{
    PlayerInput,
    MinimumSpeed
}

public enum LaneSpawnBehavior
{
    Left,
    Middle,
    Right
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class Sphere
{
    public Vector3 Center { get; set; }
    public float RadiusMeters { get; set; }
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class UserGroupCommandPermissions
{
    public required string UserGroup { get; set; }
    public required List<string> Commands { get; set; }
}

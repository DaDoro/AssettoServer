using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Http.Responses;
using AssettoServer.Utils;
using FluentValidation;
using Newtonsoft.Json;
using Serilog;
using YamlDotNet.Serialization;

namespace AssettoServer.Server.Configuration;

public class ACServerConfiguration
{
    public ServerConfiguration Server { get; }
    public EntryList EntryList { get; }
    public List<SessionConfiguration> Sessions { get; }
    [YamlIgnore] public string FullTrackName { get; }
    [YamlIgnore] public CSPTrackOptions CSPTrackOptions { get; }
    [YamlIgnore] public string WelcomeMessage { get; } = "";
    public ACExtraConfiguration Extra { get; private set; } = new();
    [YamlIgnore] public CMContentConfiguration? ContentConfiguration { get; private set; }
    public string ServerVersion { get; }
    [YamlIgnore] public string? CSPExtraOptions { get; }
    [YamlIgnore] public string BaseFolder { get; }
    [YamlIgnore] public bool LoadPluginsFromWorkdir { get; }
    [YamlIgnore] public string? AiTraceDirectory { get; }
    [YamlIgnore] public int RandomSeed { get; } = Random.Shared.Next();
    
    /*
     * Search paths are like this:
     *
     * When no options are set, all config files must be located in "cfg/".
     * WELCOME_MESSAGE path is relative to the working directory of the server.
     *
     * When "preset" is set, all configs must be located in "presets/<preset>/".
     * WELCOME_MESSAGE path must be relative to the preset folder.
     *
     * When "serverCfgPath" is set, server_cfg.ini will be loaded from the specified path.
     * All other configs must be located in the same folder.
     *
     * When "entryListPath" is set, it takes precedence and entry_list.ini will be loaded from the specified path.
     */
    public ACServerConfiguration(string preset, string serverCfgPath, string entryListPath, bool loadPluginsFromWorkdir, string? aiTraceDirectory = null, string? aiFollowingModel = null, bool aiLaneChanges = false, bool aiMerges = false, bool aiIncidents = false, bool aiVisuals = false, bool aiVanilla = false)
    {
        BaseFolder = string.IsNullOrEmpty(preset) ? "cfg" : Path.Join("presets", preset);
        LoadPluginsFromWorkdir = loadPluginsFromWorkdir;
        AiTraceDirectory = string.IsNullOrEmpty(aiTraceDirectory) ? null : aiTraceDirectory;

        if (string.IsNullOrEmpty(entryListPath))
        {
            entryListPath = Path.Join(BaseFolder, "entry_list.ini");
        }

        if (string.IsNullOrEmpty(serverCfgPath))
        {
            serverCfgPath = Path.Join(BaseFolder, "server_cfg.ini");
        }
        else
        {
            BaseFolder = Path.GetDirectoryName(serverCfgPath)!;
        }
        
        Log.Debug("Loading server_cfg.ini from {Path}", serverCfgPath);
        Server = ServerConfiguration.FromFile(serverCfgPath);
        
        Log.Debug("Loading entry_list.ini from {Path}", entryListPath);
        EntryList = EntryList.FromFile(entryListPath);
        
        ServerVersion = ThisAssembly.AssemblyInformationalVersion;
        FullTrackName = string.IsNullOrEmpty(Server.TrackConfig) ? Server.Track : Server.Track + "-" + Server.TrackConfig;
        CSPTrackOptions = CSPTrackOptions.Parse(Server.Track);

        string welcomeMessagePath = string.IsNullOrEmpty(preset) ? Server.WelcomeMessagePath : Path.Join(BaseFolder, Server.WelcomeMessagePath);
        if (File.Exists(welcomeMessagePath))
        {
            WelcomeMessage = File.ReadAllText(welcomeMessagePath);
        }
        else if(!string.IsNullOrEmpty(welcomeMessagePath))
        {
            Log.Warning("Welcome message not found at {Path}", Path.GetFullPath(welcomeMessagePath));
        }

        string cspExtraOptionsPath = Path.Join(BaseFolder, "csp_extra_options.ini"); 
        if (File.Exists(cspExtraOptionsPath))
        {
            CSPExtraOptions = File.ReadAllText(cspExtraOptionsPath);
        }

        var sessions = new List<SessionConfiguration>();

        if (Server.Practice != null)
        {
            Server.Practice.Id = 0;
            Server.Practice.Type = SessionType.Practice;
            sessions.Add(Server.Practice);
        }

        if (Server.Qualify != null)
        {
            Server.Qualify.Id = 1;
            Server.Qualify.Type = SessionType.Qualifying;
            sessions.Add(Server.Qualify);
        }

        if (Server.Race != null)
        {
            Server.Race.Id = 2;
            Server.Race.Type = SessionType.Race;
            sessions.Add(Server.Race);
        }

        Sessions = sessions;

        LoadExtraConfig();
        ApplyAiFollowingModelOverride(aiFollowingModel);
        ApplyAiLaneChangesOverride(aiLaneChanges);
        ApplyAiMergesOverride(aiMerges);
        ApplyAiIncidentsOverride(aiIncidents);
        ApplyAiVisualsOverride(aiVisuals);
        ApplyAiVanillaOverride(aiVanilla);
        DisableAccFeaturesForVanilla();
        Log.Information("AI traffic: {Model} following model, lane changes {LaneChanges}, merges {Merges}, incidents {Incidents}, visuals {Visuals}, headlights {Lights}",
            Extra.AiParams.FollowingModel, OnOff(Extra.AiParams.LaneChanges.Enabled), OnOff(Extra.AiParams.Merges.Enabled),
            OnOff(Extra.AiParams.Incidents.Enabled), OnOff(Extra.AiParams.Visuals.Enabled),
            Extra.AiParams.EnableDaytimeLights ? "always" : "by sun");

        var validator = new ACServerConfigurationValidator();
        validator.ValidateAndThrow(this);
    }

    private void ApplyAiFollowingModelOverride(string? aiFollowingModel)
    {
        if (string.IsNullOrEmpty(aiFollowingModel)) return;

        if (!Enum.TryParse<AiFollowingModel>(aiFollowingModel, true, out var model) || !Enum.IsDefined(model))
            throw new ConfigurationException($"Unknown --ai-model value \"{aiFollowingModel}\". Possible values: {string.Join(", ", Enum.GetNames<AiFollowingModel>())}");

        Log.Information("AI following model {Model} set by --ai-model (extra_cfg.yml: {ConfigModel})", model, Extra.AiParams.FollowingModel);
        Extra.AiParams.FollowingModel = model;
    }

    private void ApplyAiLaneChangesOverride(bool aiLaneChanges)
    {
        if (aiLaneChanges && !Extra.AiParams.LaneChanges.Enabled)
        {
            Log.Information("AI lane changes enabled by --ai-lane-changes (extra_cfg.yml: disabled)");
            Extra.AiParams.LaneChanges.Enabled = true;
        }

    }

    private void ApplyAiMergesOverride(bool aiMerges)
    {
        if (aiMerges && !Extra.AiParams.Merges.Enabled)
        {
            Log.Information("AI merges enabled by --ai-merges (extra_cfg.yml: disabled)");
            Extra.AiParams.Merges.Enabled = true;
        }

    }

    private void ApplyAiIncidentsOverride(bool aiIncidents)
    {
        if (aiIncidents && !Extra.AiParams.Incidents.Enabled)
        {
            Log.Information("AI incidents enabled by --ai-incidents (extra_cfg.yml: disabled)");
            Extra.AiParams.Incidents.Enabled = true;
        }

    }

    private static string OnOff(bool enabled) => enabled ? "on" : "off";

    /// <summary>--ai-vanilla: AI traffic of AssettoServer 0.0.54, for comparisons</summary>
    private void ApplyAiVanillaOverride(bool aiVanilla)
    {
        if (!aiVanilla) return;

        Log.Information("AI traffic of AssettoServer 0.0.54 set by --ai-vanilla: Vanilla following model, no lane changes, merges, incidents, visuals, headlights always on");
        Extra.AiParams.FollowingModel = AiFollowingModel.Vanilla;
        Extra.AiParams.LaneChanges.Enabled = false;
        Extra.AiParams.Merges.Enabled = false;
        Extra.AiParams.Incidents.Enabled = false;
        Extra.AiParams.Visuals.Enabled = false;
        Extra.AiParams.EnableDaytimeLights = true;
    }

    /// <summary>Lane changes, merges and incidents work on top of the Acc following model: off with Vanilla</summary>
    private void DisableAccFeaturesForVanilla()
    {
        var ai = Extra.AiParams;
        if (ai.FollowingModel == AiFollowingModel.Acc) return;

        if (ai.LaneChanges.Enabled || ai.Merges.Enabled || ai.Incidents.Enabled)
        {
            Log.Information("AI lane changes, merges and incidents are off: they require the Acc following model");
            ai.LaneChanges.Enabled = false;
            ai.Merges.Enabled = false;
            ai.Incidents.Enabled = false;
        }
    }

    private void ApplyAiVisualsOverride(bool aiVisuals)
    {
        if (aiVisuals && !Extra.AiParams.Visuals.Enabled)
        {
            Log.Information("AI visuals enabled by --ai-visuals (extra_cfg.yml: disabled)");
            Extra.AiParams.Visuals.Enabled = true;
        }

        if (Extra.AiParams.Visuals.Enabled && (Extra.AiParams.Visuals.GearRatios.Count == 0 || Extra.AiParams.Visuals.GearRatios.Count > 9))
            throw new ConfigurationException("AiParams.Visuals.GearRatios needs 1 to 9 gears");
    }

    private void LoadExtraConfig() {
        var extraCfgPath = Path.Join(BaseFolder, "extra_cfg.yml");
        Log.Debug("Loading extra_cfg.yml from {Path}", extraCfgPath);
        
        if (!File.Exists(extraCfgPath))
        {
            var cfg = new ACExtraConfiguration();
            cfg.ToFile(extraCfgPath);
        }
        
        Extra = ACExtraConfiguration.FromFile(extraCfgPath);

        if (Regex.IsMatch(Server.Name, @"x:\w+$"))
        {
            const string errorMsg =
                "Server details are configured via ID in server name. This interferes with native AssettoServer server details. More info: https://assettoserver.org/docs/common-configuration-errors#wrong-server-details";
            if (Extra.IgnoreConfigurationErrors.WrongServerDetails)
            {
                Log.Warning(errorMsg);
            }
            else
            {
                throw new ConfigurationException(errorMsg) { HelpLink = "https://assettoserver.org/docs/common-configuration-errors#wrong-server-details" };
            }
        }

        if (Extra.EnableServerDetails)
        {
            string cmContentPath = Path.Join(BaseFolder, "cm_content/content.json");
            if (File.Exists(cmContentPath))
            {
                ContentConfiguration = JsonConvert.DeserializeObject<CMContentConfiguration>(File.ReadAllText(cmContentPath));
            }
        }
    }

    private (PropertyInfo? Property, object Parent) GetNestedProperty(string key)
    {
        string[] path = key.Split('.');
            
        object parent = this;
        PropertyInfo? propertyInfo = null;

        foreach (string property in path)
        {
            propertyInfo = parent.GetType().GetProperty(property);
            if (propertyInfo == null) continue;
                
            var propertyType = propertyInfo.PropertyType;
            if (!propertyType.IsPrimitive && !propertyType.IsEnum && propertyType != typeof(string))
            {
                parent = propertyInfo.GetValue(parent)!;
            }
        }

        return (propertyInfo, parent);
    }

    public bool SetProperty(string key, string value)
    {
        (var propertyInfo, object? parent) = GetNestedProperty(key);

        if (propertyInfo == null)
            throw new ConfigurationException($"Could not find property {key}");

        bool ret = false;
        try
        {
            ret = propertyInfo.SetValueFromString(parent, value);
        }
        catch (TargetInvocationException) { }

        return ret;
    }
}

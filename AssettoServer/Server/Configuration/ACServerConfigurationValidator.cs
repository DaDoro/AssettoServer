using FluentValidation;

namespace AssettoServer.Server.Configuration;

public class ACServerConfigurationValidator : AbstractValidator<ACServerConfiguration>
{
    public ACServerConfigurationValidator()
    {
        RuleFor(cfg => cfg.Extra).ChildRules(extra =>
        {
            extra.RuleFor(x => x.ValidateDlcOwnership).NotNull();
            extra.RuleFor(x => x.ServerDescription).NotNull();
            extra.RuleFor(x => x.RainTrackGripReductionPercent).InclusiveBetween(0, 0.5);
            extra.RuleFor(x => x.EnablePlugins).NotNull();
            extra.RuleFor(x => x.IgnoreConfigurationErrors).NotNull();
            extra.RuleFor(x => x.UserGroups).NotNull();
            extra.RuleFor(x => x.BlacklistUserGroup).NotEmpty();
            extra.RuleFor(x => x.WhitelistUserGroup).NotEmpty();
            extra.RuleFor(x => x.AdminUserGroup).NotEmpty();

            extra.RuleFor(x => x.AiParams).ChildRules(aiParams =>
            {
                aiParams.RuleFor(ai => ai.MinSpawnDistancePoints).LessThanOrEqualTo(ai => ai.MaxSpawnDistancePoints);
                aiParams.RuleFor(ai => ai.MinAiSafetyDistanceMeters).LessThanOrEqualTo(ai => ai.MaxAiSafetyDistanceMeters);
                aiParams.RuleFor(ai => ai.MinSpawnProtectionTimeSeconds).LessThanOrEqualTo(ai => ai.MaxSpawnProtectionTimeSeconds);
                aiParams.RuleFor(ai => ai.MinCollisionStopTimeSeconds).LessThanOrEqualTo(ai => ai.MaxCollisionStopTimeSeconds);
                aiParams.RuleFor(ai => ai.MaxSpeedVariationPercent).InclusiveBetween(0, 1);
                aiParams.RuleFor(ai => ai.DefaultAcceleration).GreaterThan(0);
                aiParams.RuleFor(ai => ai.DefaultDeceleration).GreaterThan(0);
                aiParams.RuleFor(ai => ai.NamePrefix).NotNull();
                aiParams.RuleFor(ai => ai.IgnoreObstaclesAfterSeconds).GreaterThanOrEqualTo(0);
                aiParams.RuleFor(ai => ai.HourlyTrafficDensity)
                    .Must(htd => htd?.Count == 24)
                    .When(ai => ai.HourlyTrafficDensity != null)
                    .WithMessage("HourlyTrafficDensity must have exactly 24 entries");
                aiParams.RuleFor(ai => ai.CarSpecificOverrides).NotNull();
                aiParams.RuleFor(ai => ai.AiBehaviorUpdateIntervalHz).GreaterThan(0);
                aiParams.RuleFor(ai => ai.LaneCountSpecificOverrides).NotNull();
                aiParams.RuleFor(ai => ai.FollowingModel).IsInEnum();
                aiParams.RuleFor(ai => ai.Acc).NotNull().ChildRules(acc =>
                {
                    acc.RuleFor(a => a.MinTimeHeadwaySeconds).GreaterThan(0).LessThanOrEqualTo(a => a.MaxTimeHeadwaySeconds);
                    acc.RuleFor(a => a.MinStandstillGapMeters).GreaterThan(0).LessThanOrEqualTo(a => a.MaxStandstillGapMeters);
                    acc.RuleFor(a => a.MinAccelerationFactor).GreaterThan(0).LessThanOrEqualTo(a => a.MaxAccelerationFactor);
                    acc.RuleFor(a => a.MinComfortableDecelerationMs2).GreaterThan(0).LessThanOrEqualTo(a => a.MaxComfortableDecelerationMs2);
                    acc.RuleFor(a => a.AccelerationExponent).GreaterThan(0);
                    acc.RuleFor(a => a.Coolness).InclusiveBetween(0, 1);
                    acc.RuleFor(a => a.DesiredSpeedVariationPercent).InclusiveBetween(0, 1);
                    acc.RuleFor(a => a.MinLookaheadMeters).GreaterThan(0);
                    acc.RuleFor(a => a.LookaheadSeconds).GreaterThanOrEqualTo(0);
                    acc.RuleFor(a => a.BrakeLightsOffDecelerationMs2).GreaterThanOrEqualTo(0).LessThanOrEqualTo(a => a.BrakeLightsOnDecelerationMs2);
                    acc.RuleFor(a => a.PlayerHalfWidthMeters).GreaterThanOrEqualTo(0);
                    acc.RuleFor(a => a.DefaultVehicleLengthMeters).GreaterThanOrEqualTo(0);
                    acc.RuleFor(a => a.LaneSpeedFractions).NotNull();
                    acc.RuleForEach(a => a.LaneSpeedFractions)
                        .Must(kv => kv.Key > 0 && kv.Value != null && kv.Value.Count == kv.Key)
                        .WithMessage("LaneSpeedFractions: every entry must have as many values as its number of lanes");
                });
                aiParams.RuleFor(ai => ai.Merges).NotNull().ChildRules(merges =>
                {
                    merges.RuleFor(m => m.ZoneDistanceMeters).GreaterThan(0);
                    merges.RuleFor(m => m.SafeDecelerationMs2).GreaterThan(0).LessThanOrEqualTo(m => m.AbortDecelerationMs2);
                    merges.RuleFor(m => m.PlayerSafeDecelerationMs2).GreaterThan(0);
                    merges.RuleFor(m => m.AggressivePlayerSafeDecelerationMs2).GreaterThan(0);
                    merges.RuleFor(m => m.PatienceSeconds).GreaterThanOrEqualTo(0);
                    merges.RuleFor(m => m.PatienceRampSeconds).GreaterThan(0);
                    merges.RuleFor(m => m.RampSpawnShare).InclusiveBetween(0, 1);
                    merges.RuleFor(m => m.RampSpawnMinAheadMeters).GreaterThanOrEqualTo(0).LessThanOrEqualTo(m => m.RampSpawnMaxAheadMeters);
                    merges.RuleFor(m => m.RampSpawnMinBeforeMergeMeters).GreaterThanOrEqualTo(0).LessThanOrEqualTo(m => m.RampSpawnMaxBeforeMergeMeters);
                    merges.RuleFor(m => m.RampSpawnIntervalSeconds).GreaterThanOrEqualTo(0);
                });
                aiParams.RuleForEach(ai => ai.LaneCountSpecificOverrides).ChildRules(overrides =>
                {
                    overrides.RuleFor(o => o.Key).GreaterThan(0);
                    overrides.RuleFor(o => o.Value.MinAiSafetyDistanceMeters).LessThanOrEqualTo(o => o.Value.MaxAiSafetyDistanceMeters);
                });
            });
        });

        RuleFor(cfg => cfg.Server).ChildRules(server =>
        {
            server.RuleFor(s => s.AdminPassword).NotEmpty().MinimumLength(8);
            server.RuleFor(s => s.Track).NotEmpty();
            server.RuleFor(s => s.TrackConfig).NotNull();
            server.RuleFor(s => s.FuelConsumptionRate).GreaterThanOrEqualTo(0);
            server.RuleFor(s => s.MechanicalDamageRate).GreaterThanOrEqualTo(0);
            server.RuleFor(s => s.TyreConsumptionRate).GreaterThanOrEqualTo(0);
            server.RuleFor(s => s.LegalTyres).NotNull();
            server.RuleFor(s => s.WelcomeMessagePath).NotNull();
            server.RuleFor(s => s.TimeOfDayMultiplier).GreaterThanOrEqualTo(0);

            server.RuleForEach(s => s.Weathers).ChildRules(weather =>
            {
                weather.RuleFor(w => w.BaseTemperatureAmbient).GreaterThanOrEqualTo(0);
                weather.RuleFor(w => w.WindBaseSpeedMin).LessThanOrEqualTo(w => w.WindBaseSpeedMax);
            });

            server.RuleFor(s => s.DynamicTrack).NotNull().ChildRules(dynTrack =>
            {
                dynTrack.RuleFor(d => d.BaseGrip).InclusiveBetween(0, 1);
            });
        });

        RuleFor(cfg => cfg.EntryList).ChildRules(entryList =>
        {
            entryList.RuleForEach(el => el.Cars).ChildRules(car =>
            {
                car.RuleFor(c => c.Model).NotNull();
                car.RuleFor(c => c.Guid).NotNull();
            });
        });
    }
}

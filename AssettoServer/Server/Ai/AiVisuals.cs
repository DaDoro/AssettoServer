using System;
using System.Numerics;
using AssettoServer.Server.Configuration;

namespace AssettoServer.Server.Ai;

/// <summary>
/// Fork: what the clients see and hear of an AI car (AiParams.Visuals). Front wheel angle from the curvature of the path the
/// car really drives (lane changes included), an automatic gearbox, engine rpm and throttle. Doesn't change how the car drives.
/// </summary>
public class AiVisuals
{
    // Encoding of CarStatus, measured on a player car: 127 = straight, wheel angle about 0.5° per unit,
    // steering wheel about 7 times the wheel angle units, clamped to 0..254. Gear: 0 = R, 1 = N, 2 = 1st
    private const float WheelUnitsPerDegree = 2.0f;
    private const float SteerUnitsPerWheelUnit = 7.0f;
    private const float MaxHeadingJump = 0.2f;

    private float _heading;
    private bool _hasHeading;
    private float _wheelAngle;
    private int _gear;
    private bool _gearChosen;
    private long _lastShift;
    private float _throttle;

    public byte WheelAngle { get; private set; } = 127;
    public byte SteerAngle { get; private set; } = 127;
    public byte Gear { get; private set; } = 2;
    public ushort EngineRpm { get; private set; }
    public byte Gas { get; private set; }

    public void Reset()
    {
        _hasHeading = false;
        _wheelAngle = 0;
        _gear = 0;
        _gearChosen = false;
        _lastShift = 0;
        _throttle = 0;
    }

    /// <param name="tangent">Direction the car is driving in, lane change included</param>
    /// <param name="moveMeters">Distance driven since the last update</param>
    /// <param name="braking">Brake lights on</param>
    public void Update(VisualParams p, EntryCar car, Vector3 tangent, float moveMeters, float dtSeconds, float speed,
        float acceleration, float maxAcceleration, bool braking, long now)
    {
        UpdateWheels(p, car.AiWheelbaseMeters, tangent, moveMeters, dtSeconds);
        UpdateThrottle(p, speed, acceleration, maxAcceleration, braking, dtSeconds);
        UpdateGearbox(p, car, speed, now);
    }

    private void UpdateWheels(VisualParams p, float wheelbase, Vector3 tangent, float moveMeters, float dtSeconds)
    {
        float heading = MathF.Atan2(tangent.Z, tangent.X);
        float target = _wheelAngle;
        if (_hasHeading && moveMeters > 0.01f)
        {
            float delta = heading - _heading;
            if (delta > MathF.PI) delta -= 2 * MathF.PI;
            else if (delta < -MathF.PI) delta += 2 * MathF.PI;

            // A jump of the heading (teleport, switch of spline) is not a turn
            if (MathF.Abs(delta) < MaxHeadingJump)
            {
                float maxAngle = p.MaxWheelAngleDegrees * MathF.PI / 180;
                target = Math.Clamp(MathF.Atan(wheelbase * delta / moveMeters), -maxAngle, maxAngle);
            }
        }
        _heading = heading;
        _hasHeading = true;

        _wheelAngle += (target - _wheelAngle) * Smoothing(dtSeconds, p.WheelAngleSmoothingSeconds);

        float wheelUnits = _wheelAngle * 180 / MathF.PI * WheelUnitsPerDegree;
        WheelAngle = (byte)Math.Clamp(MathF.Round(127 + wheelUnits), 0, 254);
        SteerAngle = (byte)Math.Clamp(MathF.Round(127 + wheelUnits * SteerUnitsPerWheelUnit), 0, 254);
    }

    private void UpdateThrottle(VisualParams p, float speed, float acceleration, float maxAcceleration, bool braking, float dtSeconds)
    {
        float target;
        if (braking || acceleration < -0.3f || speed < 0.1f && acceleration <= 0)
            target = 0;
        else if (acceleration > 0.05f)
            target = Math.Clamp(p.CruiseThrottle + (1 - p.CruiseThrottle) * acceleration / MathF.Max(maxAcceleration, 0.1f), 0, 1);
        else
            target = p.CruiseThrottle;

        _throttle += (target - _throttle) * Smoothing(dtSeconds, p.ThrottleSmoothingSeconds);
        Gas = (byte)Math.Clamp(MathF.Round(_throttle * 255), 0, 255);
    }

    private void UpdateGearbox(VisualParams p, EntryCar car, float speed, long now)
    {
        var ratios = p.GearRatios;
        float wheelRadius = MathF.Max(car.TyreDiameterMeters / 2, 0.1f);
        float wheelRpm = speed / wheelRadius * 60 / (2 * MathF.PI);
        float Rpm(int gear) => wheelRpm * ratios[gear] * p.FinalDrive;

        int maxRpm = Math.Max(car.AiVisualMaxRpm, car.AiIdleEngineRpm + 500);
        float upshift = MathF.Min(Lerp(p.UpshiftRpm, p.UpshiftRpmFullThrottle, _throttle), maxRpm - 200);
        float downshift = Lerp(p.DownshiftRpm, p.DownshiftRpmFullThrottle, _throttle);

        _gear = Math.Clamp(_gear, 0, ratios.Count - 1);
        if (!_gearChosen)
        {
            // Spawned at the speed of the flow: start in the gear the car would be in, not in 1st
            _gear = 0;
            while (_gear < ratios.Count - 1 && Rpm(_gear + 1) > downshift + 200)
                _gear++;
            _gearChosen = true;
        }
        else if (now - _lastShift >= p.MinShiftIntervalSeconds * 1000)
        {
            float rpm = Rpm(_gear);
            if (_gear < ratios.Count - 1 && rpm > upshift && Rpm(_gear + 1) > downshift + 200)
            {
                _gear++;
                _lastShift = now;
            }
            else if (_gear > 0 && rpm < downshift && Rpm(_gear - 1) < upshift - 200)
            {
                _gear--;
                _lastShift = now;
            }
        }

        // Below the idle rpm in 1st gear the torque converter slips: the rpm rises with the throttle
        float engineRpm = MathF.Max(Rpm(_gear), car.AiIdleEngineRpm + (_gear == 0 ? 1200 * _throttle : 0));
        EngineRpm = (ushort)Math.Clamp(MathF.Round(engineRpm), 0, maxRpm);
        Gear = (byte)(_gear + 2);
    }

    private static float Smoothing(float dtSeconds, float timeConstant)
        => timeConstant <= 0 ? 1 : 1 - MathF.Exp(-dtSeconds / timeConstant);

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0, 1);
}

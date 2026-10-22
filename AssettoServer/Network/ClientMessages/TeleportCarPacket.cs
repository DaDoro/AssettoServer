using System.Numerics;

namespace AssettoServer.Network.ClientMessages;

/// <summary>Moves the player's car, handled by assettoserver.lua (backported from 0.0.55)</summary>
[OnlineEvent(Key = "AS_TeleportCar")]
public class TeleportCarPacket : OnlineEvent<TeleportCarPacket>
{
    [OnlineEventField(Name = "position")]
    public Vector3 Position;
    [OnlineEventField(Name = "direction")]
    public Vector3 Direction;
    [OnlineEventField(Name = "velocity")]
    public Vector3 Velocity;
}

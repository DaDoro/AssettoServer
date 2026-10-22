# AssettoServer — Living AI Traffic (fork of 0.0.54)

English | [Русский](README.ru.md)

A fork of [AssettoServer](https://github.com/compujuckel/AssettoServer) 0.0.54 with rewritten AI traffic. The goal is traffic that feels alive rather than cars on rails: gaps open and close, lanes move at different speeds, cars change lanes and merge. It is made for weaving through dense highway traffic and is tested on Shutoko Revival Project (SRP).

Everything outside AI traffic is AssettoServer 0.0.54: the same configs and the same plugin API, so plugins for 0.0.54 keep working.

## What's different

- **Car following.** Intelligent Driver Model with the constant-acceleration heuristic (Improved IDM) instead of the vanilla speed logic. Every car gets its own driver: time gap 0.3–0.9 s, acceleration, comfortable braking, desired speed ±5%. Cars brake smoothly, drive in columns and react calmly to cut-ins. Lanes run at different speeds, and 12% of drivers are slower than their lane, so queues form behind them.
- **Lane changes.** MOBIL on top of the following model: cars overtake, keep to the slow lane and need a real gap to move over. Politeness varies per driver, 30% of drivers are aggressive and cut in closer in front of players. Lane changes happen near players, where they can be seen.
- **Merges.** At on-ramps, exits and overlapping lanes cars see the other lane and merge in turns, like a zipper: a car on the ramp picks a gap or waits, cars on the main road let it in. Cars also spawn on on-ramps ahead of players.
- **Spawning.** A car doesn't spawn where the car behind would have to brake for it. Half of the cars spawn in columns behind another car, the rest in gaps.
- **Incidents.** The reaction depends on the impact speed: a light touch means hazards and a short brake, a crash (20 km/h and faster) stops the car with hazards for 5–15 s. Cars go around stopped obstacles (a standing player, a crashed car) by changing lanes and take turns doing it.
- **Visuals.** Front wheels turn along the path the car really drives, including lane changes. An automatic gearbox, engine rpm and throttle make the engine sound right. Works with any car model.
- **From 0.0.55:** AI ignores players on another level (overpasses), headlights by sun altitude, traffic reset on session change.

## Installation

Download `assetto-server-win-x64.zip` from [Releases](../../releases) and set it up like AssettoServer 0.0.54, see the [AssettoServer documentation](https://assettoserver.org/). To switch an existing 0.0.54 server, replace its server executable with the one from the release. Configs stay the same.

## Configuration

All fork features are on by default, no config changes are needed. New options are in `AiParams` of `extra_cfg.yml`: `FollowingModel`, `Acc`, `LaneChanges`, `Merges`, `Incidents`, `Visuals`, `EnableDaytimeLights`. A freshly generated `extra_cfg.yml` lists all of them with descriptions. To turn a part off, set for example:

```yaml
AiParams:
  LaneChanges:
    Enabled: false
```

The server log shows what is on at startup:

```
AI traffic: Acc following model, lane changes on, merges on, incidents on, visuals on, headlights by sun
```

Command line options:

| Option | Effect |
|---|---|
| `--ai-vanilla` | AI traffic of AssettoServer 0.0.54, for comparison |
| `--ai-model vanilla` | vanilla car following; lane changes, merges and incidents turn off, visuals stay |
| `--ai-model acc`, `--ai-lane-changes`, `--ai-merges`, `--ai-incidents`, `--ai-visuals` | turn a part on when it is off in `extra_cfg.yml` |
| `--ai-trace <dir>` | record the whole AI traffic to a JSONL file for analysis |

Admin commands for testing traffic: `/where` (your position and the nearest AI spline point), `/tp <x> <z> [km/h]`, `/tppt <point> [km/h]`, `/tpramp [n] [km/h]` (list of on-ramps or teleport ahead of one), `/tpzone <segment> [km/h]`.

## Limitations

Tested on SRP only: a highway with left-hand traffic, no intersections. City maps and two-way traffic were not tested.

## Building from source

.NET 8 SDK or newer:

```
dotnet publish --runtime win-x64 -c Release
```

The result is in `out-win-x64/`.

## License

Based on AssettoServer by Niewiarowski and compujuckel. Please don't ask the AssettoServer team for help with this fork's traffic.

AssettoServer is licensed under the GNU Affero General Public License v3.0, see [LICENSE](LICENSE) for more info. The changes of this fork are under the same license.  
Additionally, you must preserve the legal notices and author attributions present in the server.

```
Copyright (C)  2022 Niewiarowski, compujuckel

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as published
by the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.


Additional permission under GNU AGPL version 3 section 7

If you modify this Program, or any covered work, by linking or combining it 
with the Steamworks SDK by Valve Corporation, containing parts covered by the
terms of the Steamworks SDK License, the licensors of this Program grant you
additional permission to convey the resulting work.

Additional permission under GNU AGPL version 3 section 7

If you modify this Program, or any covered work, by linking or combining it 
with plugins published on https://www.patreon.com/assettoserver, the licensors
of this Program grant you additional permission to convey the resulting work.
```

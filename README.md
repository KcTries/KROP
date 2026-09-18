# KROP 1.0

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/) — a large collection of realism/QOL fixes and additions, focused mainly on making sound behave like it actually has to travel through air.

## Highlights

- **Speed-of-sound audio delay** on nearly every sound-emitting system in the game (gunfire, bullet impacts, sonic booms, engines, missile motors, ejection seat, crash fireballs, countermeasures) instead of vanilla's instant-everywhere audio, plus distance-based atmospheric muffling.
- **Cockpit audio filtering** — sounds are further muffled while actually sitting in the cockpit, tuned per-source (mechanical noises less than open-air ones).
- **Enhanced NVGs** — phosphor color tint, white sparkle/static, bloom, and a wipe transition animation layered on the game's own night vision, plus an optional Auto-Gain exposure system that reacts to actual rendered brightness instead of just time-of-day.
- **Periodic Countermeasure Release (PCR)** — toggle continuous automatic flare/chaff dispensing instead of holding the button down.
- **Advanced SPAAG Ammo** — AeroSentry's point-defense cannon sprays a narrow cone of kinetic fragments on a proximity detonation instead of a vanilla blast-radius explosion.
- **Lifeboats** spawn when ships sink, with configurable lifetime and capacity.
- **AI "Parked" state overhaul** — navigation lights off, crew hidden, and airbrakes no longer deploy on a stationary AI aircraft.
- Various smaller fixes and tuning passes on top of all of the above.

## Install

1. Install [BepInEx](https://github.com/BepInEx/BepInEx) for Nuclear Option if you haven't already.
2. Download the latest release from the [Releases](../../releases) page.
3. Drop `KROP-1.0.dll` into `Nuclear Option/BepInEx/plugins/`.

## Configuring

If you have [BepInEx.ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) installed, every setting is reachable from the in-game config menu under **KROP 1.0**.

Otherwise, settings can be edited directly in `BepInEx/config/pavehog727.qolrealismfixes.cfg` after the plugin has run once.


```
dotnet build -c Release
```

## Credits

Made by KcTries with AI assistance.

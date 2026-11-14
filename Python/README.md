# Martian Map - Python side

```
feeder.py  --ws-->  server.py  --ws-->  Unity (Assets/SourceFiles/Scripts/PythonConnection.cs)
```

| File | What it does |
|---|---|
| `server.py` | WebSocket hub on `ws://localhost:8765`. Relays everything the feeder sends to every Unity client; late joiners get the last status + telemetry |
| `feeder.py` | Replays one sol of Curiosity RAD + REMS data and scripted events onto the EVA clock, sends JSON to the server |
| `prep_terrain.py` | HiRISE terrain model + photo -> Unity heightmap, photo, slope, route-cost grid, meta.json (static files Unity loads at startup; not sent over the socket) |
| `sol_samples.csv` | **PLACEHOLDER values** - replace with real numbers from one sol of RAD/REMS (PDS) |
| `events.json` | Scripted demo events (dust gust, nav degraded, Earth note) |

## Run it

```
cd Python
pip install -r requirements.txt
python server.py                              # terminal 1
python feeder.py --speed 3600 --tick 0.05     # terminal 2 (4-min fast test); use --speed 80 for the 3-min demo
```

Press Play in Unity at any point. `PythonConnection` creates itself if it isn't in the scene,
retries every 2 s until the server is up, and logs events / Earth notes to the Console.
Tick **Log Messages** on the component to see every message.

## Using the data in Unity

```csharp
void OnEnable() => PythonConnection.Instance.TelemetryReceived += OnTelemetry;
void OnDisable() { if (PythonConnection.Instance) PythonConnection.Instance.TelemetryReceived -= OnTelemetry; }
void OnTelemetry(TelemetryMessage t) => doseText.text = $"{t.dose_total_mGy:F3} mGy";
```

Also available: `EventReceived`, `EarthNoteReceived`, `StatusReceived`, `ConnectionChanged`,
`LatestTelemetry`, `IsConnected`, and `SendJson(...)` for Unity -> Python messages (the server logs them).

`Instance` is set in `Awake`, so subscribe in `Start` if your script might run before it.

## Message schema

Documented at the top of `feeder.py`; the matching C# classes are at the top of `PythonConnection.cs`.
Agree changes with the server and Unity owners, and bump `"v"` on both sides if the shape changes.

"""
Martian Map - WebSocket hub.

Sits between the data feeder (feeder.py) and Unity:

    feeder.py  --->  server.py  --->  Unity (PythonConnection.cs)

Every client starts as a "viewer" (it receives live data). A client that sends
{"type":"hello","from":"feeder"} becomes a data source: everything it sends is
relayed to all viewers. Viewers that join late are sent the last status and the
latest telemetry so the HUD fills in immediately.

Usage:
  pip install -r requirements.txt
  python server.py                    # ws://localhost:8765
  python server.py --host 0.0.0.0     # accept connections from other machines
"""
import argparse
import asyncio
import json

from websockets.asyncio.server import broadcast, serve  # websockets >= 13
from websockets.exceptions import ConnectionClosed

SCHEMA_VERSION = 1

viewers = set()   # Unity clients: receive live data
feeders = set()   # data sources: their messages are relayed to viewers
latest = {}       # "status" / "telemetry" -> last raw message, replayed to late joiners


def describe(msg):
    """One short log line per message, so the console stays readable at 2 Hz."""
    t = msg.get("type")
    if t == "telemetry":
        return (f"telemetry t={msg.get('t_eva_s')}s lmst={msg.get('lmst_h')} "
                f"dose={msg.get('dose_total_mGy')}mGy T={msg.get('temp_c')}C P={msg.get('pressure_pa')}Pa")
    return json.dumps(msg)


async def handler(ws):
    peer = f"{ws.remote_address[0]}:{ws.remote_address[1]}" if ws.remote_address else "?"
    viewers.add(ws)
    print(f"[+] {peer} connected ({len(viewers)} viewer(s), {len(feeders)} feeder(s))")
    for raw in latest.values():
        await ws.send(raw)
    try:
        async for raw in ws:
            try:
                msg = json.loads(raw)
            except (json.JSONDecodeError, TypeError):
                print(f"    {peer} sent non-JSON, ignored: {raw!r:.80}")
                continue
            if not isinstance(msg, dict):
                continue

            if msg.get("type") == "hello":
                role = msg.get("from", "unity")
                if role == "feeder":
                    viewers.discard(ws)
                    feeders.add(ws)
                print(f"    {peer} is {role}")
                await ws.send(json.dumps({"v": SCHEMA_VERSION, "type": "welcome", "role": role}))
                continue

            if ws in feeders:
                if msg.get("type") in ("status", "telemetry"):
                    latest[msg["type"]] = raw
                if msg.get("type") == "status" and msg.get("state") == "started":
                    latest.pop("telemetry", None)
                broadcast(viewers, raw)
                print(f"    -> {len(viewers)} viewer(s): {describe(msg)}")
            else:
                # Unity -> Python messages land here. Nothing consumes them yet.
                print(f"    from {peer}: {describe(msg)}")
    except ConnectionClosed:
        pass
    finally:
        viewers.discard(ws)
        feeders.discard(ws)
        print(f"[-] {peer} disconnected")


async def main(host, port):
    async with serve(handler, host, port) as server:
        print(f"Server listening on ws://{host}:{port}")
        await server.serve_forever()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="localhost")
    ap.add_argument("--port", type=int, default=8765)
    a = ap.parse_args()
    try:
        asyncio.run(main(a.host, a.port))
    except KeyboardInterrupt:
        pass

import asyncio
import json
from websockets.asyncio.server import serve  # websockets >= 13

CLIENTS = set()


async def handler(websocket):
    CLIENTS.add(websocket)
    print("Unity connected")
    try:
        async for message in websocket:
            # NativeWebSocket's SendText sends text; Send(bytes) sends binary
            print("Received:", message)
            try:
                data = json.loads(message)
            except (json.JSONDecodeError, TypeError):
                data = {"raw": message}

            # Example reply
            reply = {"type": "ack", "received": data}
            await websocket.send(json.dumps(reply))
    except Exception as e:
        print("Connection error:", e)
    finally:
        CLIENTS.discard(websocket)
        print("Unity disconnected")


async def broadcast(payload: dict):
    """Push a message to every connected Unity client."""
    for ws in list(CLIENTS):
        await ws.send(json.dumps(payload))


async def periodic_push():
    """Example: server-initiated messages every 2 seconds."""
    n = 0
    while True:
        await asyncio.sleep(2)
        n += 1
        if CLIENTS:
            await broadcast({"type": "tick", "value": n})


async def main():
    async with serve(handler, "localhost", 8765):
        print("Server listening on ws://localhost:8765")
        await periodic_push()  # runs forever


if __name__ == "__main__":
    asyncio.run(main())
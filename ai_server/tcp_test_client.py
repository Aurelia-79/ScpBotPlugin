# -*- coding: utf-8 -*-
"""临时连通性测试：模拟插件向 ai_server.py 发送 cfg + 新格式 snap（含 enemies），打印收到的 orders。"""
import asyncio
import json

HOST, PORT = "127.0.0.1", 9000

CFG = {
    "type": "cfg",
    "rooms": {
        "Outside": {"c": [0.0, 5.0, 0.0], "a": []},
    },
    "routes": {},
    "targets": {"Outside": [[-50.0, 5.0, 50.0], [50.0, 5.0, -50.0], [0.0, 5.0, 80.0]]},
}

SNAP = {
    "type": "snap",
    "bots": [
        {
            "id": 1,
            "p": [0.0, 5.0, 0.0],
            "r": "Outside",
            "t": "FoundationForces",
            "h": 100,
            "role": "NtfCaptain",
            # 可见敌人（Chaos，距离 15m，射程内）→ 应开火 + 追击/走位
            "enemies": [
                {"n": 9001, "p": [0.0, 5.0, 15.0], "ap": [0.0, 5.5, 15.0], "d": 15.0, "t": "ChaosInsurgency", "vis": 1},
            ],
        },
        {
            "id": 2,
            "p": [0.0, 5.0, 0.0],
            "r": "Outside",
            "t": "FoundationForces",
            "h": 100,
            "role": "NtfCaptain",
            # 无敌人 → 应巡逻（moveTo 到地标附近）
            "enemies": [],
        },
        {
            "id": 3,
            "p": [0.0, 5.0, 0.0],
            "r": "Outside",
            "t": "FoundationForces",
            "h": 100,
            "role": "NtfCaptain",
            # 贴脸敌人（距离 2m，< OrbitRetreatDistance）→ 应后撤
            "enemies": [
                {"n": 9002, "p": [0.0, 5.0, 2.0], "ap": [0.0, 5.5, 2.0], "d": 2.0, "t": "ChaosInsurgency", "vis": 1},
            ],
        },
        {
            "id": 4,
            "p": [0.0, 5.0, 0.0],
            "r": "Outside",
            "t": "FoundationForces",
            "h": 100,
            "role": "NtfCaptain",
            # 不可见敌人（墙后，vis=0）→ 应追击 chaseTo（本地算 NavMesh）
            "enemies": [
                {"n": 9003, "p": [80.0, 5.0, 80.0], "ap": [80.0, 5.5, 80.0], "d": 113.0, "t": "ChaosInsurgency", "vis": 0},
            ],
        },
    ],
    "peers": [],
}


async def main():
    reader, writer = await asyncio.open_connection(HOST, PORT)
    writer.write((json.dumps(CFG) + "\n").encode())
    writer.write((json.dumps(SNAP) + "\n").encode())
    await writer.drain()

    got = []
    for _ in range(8):
        raw = await asyncio.wait_for(reader.readline(), timeout=5)
        line = raw.decode().strip()
        got.append(json.loads(line))
    writer.close()

    print("收到的消息：")
    for g in got:
        print(" ", g)

    orders = {m.get("bot"): m for m in got if m.get("type") == "orders"}

    ok1 = orders.get(1, {}).get("shoot") == 1 and orders.get(1, {}).get("moveTo") is not None
    ok2 = orders.get(2, {}).get("moveTo") is not None  # 巡逻有 moveTo
    ok3 = orders.get(3, {}).get("shoot") == 1 and orders.get(3, {}).get("moveTo") is not None  # 贴脸后撤
    ok4 = orders.get(4, {}).get("chaseTo") is not None  # 不可见 -> 追击 chaseTo

    print("bot1 开火+走位:", ok1, "| bot2 巡逻:", ok2, "| bot3 贴脸后撤:", ok3, "| bot4 追击chaseTo:", ok4)
    print("测试结果：", "通过" if (ok1 and ok2 and ok3 and ok4) else "未通过（检查服务日志）")


asyncio.run(main())

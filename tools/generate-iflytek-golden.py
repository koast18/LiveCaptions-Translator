#!/usr/bin/env python3
"""生成讯飞 IAT 的 golden 向量，供 C# 实现做逐字节比对。

本脚本内嵌的是 `xfyun-iat-py` 参考实现（auth.py / protocol.py / result.py）的等价逻辑，
因此仓库不依赖那份本地代码也能重新生成期望值：

    python tools/generate-iflytek-golden.py > tests/fixtures/iflytek-golden.json

生成的向量覆盖：
  * HMAC-SHA256 签名与带签名的 wss URL（固定 date）
  * 首帧 / 中间帧 / 末帧的 JSON 文本
  * 40ms(1280 字节) 分片与末片补零
  * wpgs(apd/rpl/rg/sn) 增量拼装在各条报文之后的结果
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
from email.utils import formatdate
from urllib.parse import quote

# --------------------------------------------------------------------------- auth.py

HOST = "100ime-iat-api.xfyun.cn"
PATH = "/v2/iat"
APP_ID = "100IME"
DEFAULT_API_KEY = "15461402826c8360ebcf1270cbba88e5"
DEFAULT_API_SECRET = "sH8v2wSSxxHTy1UNfN6dFaSqVjlmR7BB"


def rfc1123_date(ts: float) -> str:
    return formatdate(ts, usegmt=True)


def sign(api_secret: str, host: str, path: str, date: str, method: str = "GET") -> str:
    origin = f"host: {host}\ndate: {date}\n{method} {path} HTTP/1.1"
    digest = hmac.new(api_secret.encode("utf-8"), origin.encode("utf-8"), hashlib.sha256).digest()
    return base64.b64encode(digest).decode("ascii")


def authorization_header(api_key: str, signature: str) -> str:
    raw = (
        f'api_key="{api_key}", algorithm="hmac-sha256", '
        f'headers="host date request-line", signature="{signature}"'
    )
    return base64.b64encode(raw.encode("utf-8")).decode("ascii")


def build_signed_url(api_key, api_secret, host, path, date, scheme="wss") -> str:
    sig = sign(api_secret, host, path, date)
    auth = authorization_header(api_key, sig)
    return (
        f"{scheme}://{host}{path}"
        f"?authorization={quote(auth, safe='')}"
        f"&date={quote(date, safe='')}"
        f"&host={quote(host, safe='')}"
    )


# ----------------------------------------------------------------------- protocol.py

SAMPLE_RATE = 16000
SAMPLE_WIDTH = 2
CHUNK_MS = 40
CHUNK_SAMPLES = SAMPLE_RATE * CHUNK_MS // 1000
CHUNK_BYTES = CHUNK_SAMPLES * SAMPLE_WIDTH
LEADING_SILENCE_MS = 200

BUSINESS = {
    "language": "zh_cn",
    "domain": "iat",
    "accent": "mandarin",
    "vad_eos": 3000,
    "dwa": "wpgs",
    "nunum": 1,
    "ptt": 1,
}


def build_frame(status: int, audio: bytes = b"", *, app_id: str = APP_ID,
                business: dict | None = None, sample_rate: int = SAMPLE_RATE) -> str:
    data = {
        "status": status,
        "format": f"audio/L16;rate={sample_rate}",
        "encoding": "raw",
        "audio": base64.b64encode(audio).decode("ascii"),
    }
    frame: dict = {"data": data}
    if status == 0:
        frame["common"] = {"app_id": app_id}
        frame["business"] = dict(business or BUSINESS)
    return json.dumps(frame, ensure_ascii=False, separators=(",", ":"))


def chunk_pcm(pcm: bytes, chunk_bytes: int = CHUNK_BYTES):
    for i in range(0, len(pcm), chunk_bytes):
        piece = pcm[i : i + chunk_bytes]
        if len(piece) < chunk_bytes:
            piece = piece + b"\x00" * (chunk_bytes - len(piece))
        yield piece


def leading_silence(ms: int = LEADING_SILENCE_MS, sample_rate: int = SAMPLE_RATE) -> bytes:
    return b"\x00" * (sample_rate * ms // 1000 * SAMPLE_WIDTH)


# ------------------------------------------------------------------------- result.py

def result_text(result: dict) -> str:
    out: list[str] = []
    for entry in result.get("ws") or []:
        items = entry if isinstance(entry, list) else [entry]
        for item in items:
            if not isinstance(item, dict):
                continue
            for cw in item.get("cw") or []:
                w = cw.get("w") if isinstance(cw, dict) else cw
                if w:
                    out.append(w)
    return "".join(out)


class Transcript:
    def __init__(self) -> None:
        self.parts: dict[int, str] = {}
        self.last_sn = 0
        self.is_last = False

    def feed(self, result: dict) -> str:
        sn = int(result.get("sn", 0) or 0)
        pgs = result.get("pgs")
        text = result_text(result)

        if pgs == "rpl":
            rg = result.get("rg") or []
            if len(rg) >= 2:
                for k in [k for k in self.parts if rg[0] <= k <= rg[1]]:
                    del self.parts[k]
        if text:
            self.parts[sn] = text

        self.last_sn = max(self.last_sn, sn)
        if result.get("ls"):
            self.is_last = True
        return self.text

    @property
    def text(self) -> str:
        return "".join(self.parts[k] for k in sorted(self.parts))


# ---------------------------------------------------------------------------- build

def ws(words):
    return [{"bg": 0, "cw": [{"sc": 0, "w": w}]} for w in words]


def ws_legacy(words):
    return [[{"bg": 0, "cw": [{"sc": 0, "w": w}]} for w in words]]


def build():
    # --- 签名 ---
    dates = [1776448143, 946684800, 2147483647]
    signatures = []
    for ts in dates:
        date = rfc1123_date(ts)
        signatures.append(
            {
                "unixTime": ts,
                "date": date,
                "signingString": f"host: {HOST}\ndate: {date}\nGET {PATH} HTTP/1.1",
                "signature": sign(DEFAULT_API_SECRET, HOST, PATH, date),
                "authorization": authorization_header(
                    DEFAULT_API_KEY, sign(DEFAULT_API_SECRET, HOST, PATH, date)
                ),
                "url": build_signed_url(DEFAULT_API_KEY, DEFAULT_API_SECRET, HOST, PATH, date),
            }
        )

    # --- 帧 ---
    frames = []
    audio_a = bytes(range(0, 64))
    audio_b = bytes([0xAA, 0xBB] * 8)
    for status, audio in ((0, audio_a), (1, audio_b), (2, b"")):
        frames.append(
            {
                "status": status,
                "audioBase64": base64.b64encode(audio).decode("ascii"),
                "json": build_frame(status, audio),
            }
        )
    # 覆盖 accent / language 变体
    variants = [
        {"language": "en_us", "domain": "iat", "accent": "mandarin", "vad_eos": 3000, "dwa": "wpgs"},
        {"language": "zh_cn", "domain": "iat", "accent": "cantonese", "vad_eos": 3000,
         "dwa": "wpgs", "nunum": 1, "ptt": 1},
    ]
    for business in variants:
        frames.append(
            {
                "status": 0,
                "audioBase64": "",
                "business": business,
                "json": build_frame(0, b"", business=business),
            }
        )

    # --- 分片 ---
    pcm = bytes((i * 7) % 256 for i in range(3000))     # 3000 字节 -> 2 片，末片补零
    chunks = [base64.b64encode(p).decode("ascii") for p in chunk_pcm(pcm)]
    chunk_case = {
        "pcmBase64": base64.b64encode(pcm).decode("ascii"),
        "chunkBytes": CHUNK_BYTES,
        "chunksBase64": chunks,
    }

    # --- wpgs 拼装 ---
    scenarios = []

    def run(name, messages):
        transcript = Transcript()
        steps = []
        for message in messages:
            text = transcript.feed(message)
            steps.append({"result": message, "text": text, "lastSn": transcript.last_sn,
                          "isLast": transcript.is_last})
        scenarios.append({"name": name, "steps": steps})

    run("append_only", [
        {"sn": 1, "pgs": "apd", "ls": False, "ws": ws(["今天"])},
        {"sn": 2, "pgs": "apd", "ls": False, "ws": ws(["天气"])},
        {"sn": 3, "pgs": "apd", "ls": True, "ws": ws(["不错", "。"])},
    ])

    run("replacement", [
        {"sn": 1, "pgs": "apd", "ls": False, "ws": ws(["今天"])},
        {"sn": 2, "pgs": "apd", "ls": False, "ws": ws(["今天天气"])},
        {"sn": 3, "pgs": "rpl", "rg": [1, 2], "ls": False, "ws": ws(["今天天气不错"])},
        {"sn": 4, "pgs": "rpl", "rg": [1, 3], "ls": True, "ws": ws(["今天天气不错，我们一起去公园散步吧。"])},
    ])

    run("replace_single_slot", [
        {"sn": 1, "pgs": "apd", "ls": False, "ws": ws(["你"])},
        {"sn": 2, "pgs": "rpl", "rg": [1, 1], "ls": False, "ws": ws(["你好"])},
        {"sn": 3, "pgs": "rpl", "rg": [1, 2], "ls": False, "ws": ws(["你好，"])},
    ])

    run("legacy_nested_ws", [
        {"sn": 1, "pgs": "apd", "ls": False, "ws": ws_legacy(["你好"])},
        {"sn": 2, "pgs": "apd", "ls": True, "ws": ws_legacy(["世界"])},
    ])

    run("empty_ws_keeps_slot", [
        {"sn": 1, "pgs": "apd", "ls": False, "ws": ws(["第一句"])},
        {"sn": 2, "pgs": "apd", "ls": False, "ws": []},
        {"sn": 3, "pgs": "apd", "ls": True, "ws": ws(["第二句"])},
    ])

    run("plain_string_cw", [
        {"sn": 1, "pgs": "apd", "ls": True, "ws": [{"bg": 0, "cw": ["甲", "乙"]}]},
    ])

    return {
        "source": "xfyun-iat-py (auth.py / protocol.py / result.py)",
        "host": HOST,
        "path": PATH,
        "appId": APP_ID,
        "apiKey": DEFAULT_API_KEY,
        "apiSecret": DEFAULT_API_SECRET,
        "sampleRate": SAMPLE_RATE,
        "chunkBytes": CHUNK_BYTES,
        "chunkMs": CHUNK_MS,
        "leadingSilenceMs": LEADING_SILENCE_MS,
        "defaultBusiness": BUSINESS,
        "signatures": signatures,
        "frames": frames,
        "chunkCase": chunk_case,
        "leadingSilenceBase64": base64.b64encode(leading_silence()).decode("ascii"),
        "scenarios": scenarios,
    }


if __name__ == "__main__":
    print(json.dumps(build(), ensure_ascii=False, indent=2))

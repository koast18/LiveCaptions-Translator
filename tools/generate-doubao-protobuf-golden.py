#!/usr/bin/env python3
"""生成豆包 AsrRequest / AsrResponse 的 golden 向量。

编码逻辑取自参考实现 `run_asr.py` 里的手写 protobuf 编码器（build_asr_request /
_encode_tag / _encode_varint / _encode_string / _encode_bytes），因此 C# 实现可以与它
逐字节比对，而不是只做自己跟自己往返。

    python tools/generate-doubao-protobuf-golden.py > tests/fixtures/doubao-protobuf-golden.json
"""

from __future__ import annotations

import json


# ---------------------------------------------------------------- 参考实现（run_asr.py）

def _encode_varint(value: int) -> bytes:
    out = bytearray()
    while True:
        bits = value & 0x7F
        value >>= 7
        if value:
            out.append(bits | 0x80)
        else:
            out.append(bits)
            return bytes(out)


def _encode_tag(field_number: int, wire_type: int) -> bytes:
    return _encode_varint((field_number << 3) | wire_type)


def _encode_string(field_number: int, value: str) -> bytes:
    raw = value.encode("utf-8")
    return _encode_tag(field_number, 2) + _encode_varint(len(raw)) + raw


def _encode_bytes(field_number: int, value: bytes) -> bytes:
    return _encode_tag(field_number, 2) + _encode_varint(len(value)) + value


def build_asr_request(token, method_name, request_id, payload="", audio_data=b"", frame_state=0):
    out = bytearray()
    out += _encode_string(2, token)
    out += _encode_string(3, "ASR")
    out += _encode_string(5, method_name)
    if payload:
        out += _encode_string(6, payload)
    if audio_data:
        out += _encode_bytes(7, audio_data)
    out += _encode_string(8, request_id)
    if frame_state:
        out += _encode_tag(9, 0) + _encode_varint(frame_state)
    return bytes(out)


def build_asr_response(**fields) -> bytes:
    """按 asr.proto 的字段号拼一个响应，用于测试解码。"""
    out = bytearray()
    for number, name in ((1, "request_id"), (2, "task_id"), (3, "service_name"),
                         (4, "message_type"), (6, "status_message"), (7, "result_json")):
        if fields.get(name):
            out += _encode_string(number, fields[name])
    if fields.get("status_code"):
        out += _encode_tag(5, 0) + _encode_varint(fields["status_code"])
    if fields.get("unknown_field_9"):
        out += _encode_tag(9, 0) + _encode_varint(fields["unknown_field_9"])
    if fields.get("unknown_field_42"):
        out += _encode_tag(42, 0) + _encode_varint(fields["unknown_field_42"])
    return bytes(out)


# ------------------------------------------------------------------------------------ build

SESSION_CONFIG = json.dumps(
    {
        "audio_info": {"channel": 1, "format": "speech_opus", "sample_rate": 16000},
        "enable_punctuation": True,
        "enable_speech_rejection": False,
        "extra": {
            "app_name": "oime",
            "cell_compress_rate": 8,
            "did": "1782778396951964",
            "enable_asr_threepass": True,
            "enable_asr_twopass": True,
            "input_mode": "tool",
            "end_smooth_window_ms": 200,
            "use_twopass_retry": True,
        },
    },
    separators=(",", ":"),
)


def build():
    requests = []

    def add(label, **kwargs):
        data = build_asr_request(**kwargs)
        safe = {k: (v.hex() if isinstance(v, (bytes, bytearray)) else v) for k, v in kwargs.items()}
        if "audio_data" in safe:
            safe["audio_data_hex"] = safe.pop("audio_data")
        requests.append({"label": label, "args": safe, "hex": data.hex()})

    add("start_task", token="TOKEN-abc123", method_name="StartTask", request_id="rid-start")
    add("start_session", token="TOKEN-abc123", method_name="StartSession",
        request_id="rid-session", payload=SESSION_CONFIG)
    add("task_request_first", token="TOKEN-abc123", method_name="TaskRequest",
        request_id="rid-task", payload='{"extra":{},"timestamp_ms":1700000000000}',
        audio_data=bytes(range(0, 40)), frame_state=1)
    add("task_request_middle", token="TOKEN-abc123", method_name="TaskRequest",
        request_id="rid-task", payload='{"extra":{},"timestamp_ms":1700000000020}',
        audio_data=b"\xaa\xbb\xcc", frame_state=3)
    add("task_request_last", token="TOKEN-abc123", method_name="TaskRequest",
        request_id="rid-task", payload='{"extra":{},"timestamp_ms":1700000000040}',
        frame_state=9)
    add("finish_session", token="TOKEN-abc123", method_name="FinishSession", request_id="rid-finish")

    responses = [
        {
            "label": "task_started",
            "hex": build_asr_response(
                request_id="rid-start", task_id="task-1", service_name="ASR",
                message_type="TaskStarted", status_code=20000000, status_message="OK",
            ).hex(),
            "expected": {
                "requestId": "rid-start", "taskId": "task-1", "serviceName": "ASR",
                "messageType": "TaskStarted", "statusCode": 20000000,
                "statusMessage": "OK", "resultJson": "", "unknownField9": 0,
            },
        },
        {
            "label": "result_with_unknown_fields",
            "hex": build_asr_response(
                request_id="rid-task", service_name="ASR", message_type="TaskResult",
                status_code=20000000, result_json='{"results":[{"text":"你好"}]}',
                unknown_field_9=7, unknown_field_42=1234,
            ).hex(),
            "expected": {
                "requestId": "rid-task", "taskId": "", "serviceName": "ASR",
                "messageType": "TaskResult", "statusCode": 20000000,
                "statusMessage": "", "resultJson": '{"results":[{"text":"你好"}]}',
                "unknownField9": 7,
            },
        },
        {
            "label": "session_failed",
            "hex": build_asr_response(
                request_id="rid-session", message_type="SessionFailed",
                status_code=45000001, status_message="invalid session",
            ).hex(),
            "expected": {
                "requestId": "rid-session", "taskId": "", "serviceName": "",
                "messageType": "SessionFailed", "statusCode": 45000001,
                "statusMessage": "invalid session", "resultJson": "", "unknownField9": 0,
            },
        },
    ]

    return {
        "source": "doubaoIME/run_asr.py 的手写 protobuf 编码器",
        "frameStates": {"unspecified": 0, "first": 1, "middle": 3, "last": 9},
        "statusOk": 20000000,
        "sessionConfig": SESSION_CONFIG,
        "requests": requests,
        "responses": responses,
    }


if __name__ == "__main__":
    print(json.dumps(build(), ensure_ascii=False, indent=2))

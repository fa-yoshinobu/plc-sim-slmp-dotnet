"""PLC Sim (SLMP)を Python から操作するクライアント（標準ライブラリのみ）。

シミュレータの HTTP API（既定 http://127.0.0.1:5080）を使って仮想 PLC のメモリを読み書きする。
GUI / コンソールから起動したスクリプトでは環境変数 PLC_SIM_URL / PLC_SIM_SOURCE が渡される。

    from plc_sim import Plc

    plc = Plc()
    plc.set("D100", 1234)
    print(plc.get("D100"))

    @plc.every(0.1)                 # 100ms 周期
    def count():
        plc.inc("D100")

    @plc.on_write("M0")             # クライアント等が M0 に書いたとき
    def on_m0(event):
        if plc.get_bit("M0"):
            plc.set("D200", 1234)

    @plc.on_state("Stop")
    def stopped():
        plc.set("Y0", False)

    plc.run_forever()
"""

from __future__ import annotations

import json
import os
import re
import struct
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import Any, Callable, Iterable

__all__ = ["Plc", "PlcError", "WriteEvent"]


class PlcError(RuntimeError):
    """API がエラーを返したとき（範囲外アドレスなど）。"""


@dataclass(frozen=True)
class WriteEvent:
    """書込み通知。device は "D" / "M" / "U0\\G" など、start/count はデバイス点単位。"""

    device: str
    start: int
    count: int
    source: str


_ADDRESS = re.compile(r"^(?:(?P<q>[UJ][0-9A-F]+)\\)?(?P<dev>[A-Z]+)(?P<num>[0-9A-F]+)$", re.IGNORECASE)
_HEX_DEVICES = {"X", "Y", "B", "W", "SB", "SW", "DX", "DY"}
_KNOWN_DEVICES = sorted(
    ["SM", "SD", "X", "Y", "M", "L", "F", "V", "B", "S", "D", "W", "TS", "TC", "TN", "LTS", "LTC", "LTN",
     "STS", "STC", "STN", "LSTS", "LSTC", "LSTN", "CS", "CC", "CN", "LCS", "LCC", "LCN", "SB", "SW",
     "DX", "DY", "Z", "LZ", "R", "ZR", "RD", "G", "HG"],
    key=len,
    reverse=True,
)


class Plc:
    """シミュレータの仮想 PLC。"""

    def __init__(self, url: str | None = None, source: str | None = None, timeout: float = 5.0) -> None:
        self.url = (url or os.environ.get("PLC_SIM_URL") or "http://127.0.0.1:5080").rstrip("/")
        self.source = source or os.environ.get("PLC_SIM_SOURCE") or "Python"
        self.timeout = timeout
        self._timers: list[tuple[float, Callable[[], Any]]] = []
        self._write_handlers: list[tuple[str, int, Callable[[WriteEvent], Any]]] = []
        self._state_handlers: list[tuple[str, Callable[[], Any]]] = []
        self._stop = threading.Event()
        self._lock = threading.Lock()

    # ---- 読出し -------------------------------------------------------
    def get(self, address: str) -> int:
        """16 ビット値（符号なし）。"""
        return self.get_words(address, 1)[0]

    def get_int(self, address: str) -> int:
        """16 ビット値（符号付き）。"""
        return self._read(address, 1, "s16")[0]

    def get_words(self, address: str, count: int) -> list[int]:
        return self._read(address, count, "u16")

    def get_bit(self, address: str) -> bool:
        return self.get_bits(address, 1)[0]

    def get_bits(self, address: str, count: int) -> list[bool]:
        return self._read(address, count, "bit")

    def get_dword(self, address: str) -> int:
        return self._read(address, 1, "u32")[0]

    def get_dint(self, address: str) -> int:
        return self._read(address, 1, "s32")[0]

    def get_float(self, address: str) -> float:
        return self._read(address, 1, "f32")[0]

    def get_string(self, address: str, words: int) -> str:
        return self._read(address, words, "str")[0]

    # ---- 書込み -------------------------------------------------------
    def set(self, address: str, value: bool | int | float | str) -> None:
        """bool はビット、int は 16 ビット、float は 32 ビット浮動小数、str は文字列として書く。"""
        if isinstance(value, bool):
            self.set_bits(address, [value])
        elif isinstance(value, int):
            self.set_words(address, [value])
        elif isinstance(value, float):
            self.set_float(address, value)
        elif isinstance(value, str):
            self._write(address, [value], "str")
        else:
            raise TypeError(f"未対応の型: {type(value).__name__}")

    def set_words(self, address: str, values: Iterable[int]) -> None:
        self._write(address, [int(v) for v in values], "u16")

    def set_bits(self, address: str, values: Iterable[bool]) -> None:
        self._write(address, [1 if v else 0 for v in values], "bit")

    def set_dword(self, address: str, value: int) -> None:
        self._write(address, [value & 0xFFFFFFFF], "u32")

    def set_float(self, address: str, value: float) -> None:
        self._write(address, [float(value)], "f32")

    def inc(self, address: str, step: int = 1) -> int:
        """16 ビット値に加算して新しい値を返す（サーバー側で原子的に実行）。"""
        return self._request("POST", f"/api/devices/{self._quote(address)}/inc", {"step": step, "type": "u16"})["value"]

    def inc_dword(self, address: str, step: int = 1) -> int:
        return self._request("POST", f"/api/devices/{self._quote(address)}/inc", {"step": step, "type": "u32"})["value"]

    def toggle(self, address: str) -> bool:
        return self._request("POST", f"/api/devices/{self._quote(address)}/toggle", {})["value"]

    # ---- ラベル -------------------------------------------------------
    def get_label(self, label: str) -> bytes:
        return bytes.fromhex(self._request("GET", f"/api/labels/{self._quote(label)}")["hex"])

    def set_label(self, label: str, data: bytes) -> None:
        self._request("PUT", f"/api/labels/{self._quote(label)}", {"hex": data.hex().upper()})

    def get_label_word(self, label: str) -> int:
        return struct.unpack("<H", self.get_label(label)[:2])[0]

    def set_label_word(self, label: str, value: int) -> None:
        self.set_label(label, struct.pack("<H", value & 0xFFFF))

    # ---- CPU 状態・エラー注入 -----------------------------------------
    @property
    def state(self) -> str:
        """"Run" / "Stop" / "Pause"。"""
        return self.status()["state"]

    def status(self) -> dict[str, Any]:
        return self._request("GET", "/api/status")

    def run(self) -> None:
        self._request("POST", "/api/plc/run")

    def stop(self) -> None:
        self._request("POST", "/api/plc/stop")

    def pause(self) -> None:
        self._request("POST", "/api/plc/pause")

    def reset(self) -> None:
        self._request("POST", "/api/plc/reset")

    def set_error(self, code: int) -> None:
        """自己診断エラーコード（SD0）を設定する。0 で解除。"""
        self._request("POST", "/api/plc/error", {"code": code})

    def inject_error(self, command: int, end_code: int | None = None, *, subcommand: int | None = None,
                     delay_ms: int = 0, drop: bool = False, count: int | None = None) -> None:
        """SLMP 要求へのエラー注入（例: inject_error(0x0401, 0xC051, count=1)）。"""
        self._request("POST", "/api/errors", {
            "command": command, "subcommand": subcommand, "endCode": end_code,
            "delayMs": delay_ms, "drop": drop, "remainingCount": count,
        })

    def clear_errors(self) -> None:
        self._request("DELETE", "/api/errors")

    # ---- 周期処理・イベント ---------------------------------------------
    def every(self, seconds: float) -> Callable[[Callable[[], Any]], Callable[[], Any]]:
        """周期実行のデコレータ。run_forever() で動き出す。"""
        def register(func: Callable[[], Any]) -> Callable[[], Any]:
            self._timers.append((seconds, func))
            return func
        return register

    def on_write(self, address: str) -> Callable[[Callable[[WriteEvent], Any]], Callable[[WriteEvent], Any]]:
        """アドレスへの書込み（SLMP クライアント・API・他スクリプト）で呼ばれる。自分の書込みでは呼ばれない。"""
        device, number = self._split(address)

        def register(func: Callable[[WriteEvent], Any]) -> Callable[[WriteEvent], Any]:
            self._write_handlers.append((device, number, func))
            return func
        return register

    def on_state(self, state: str) -> Callable[[Callable[[], Any]], Callable[[], Any]]:
        """CPU 状態（"Run" / "Stop" / "Pause"）に変わったときに呼ばれる。"""
        def register(func: Callable[[], Any]) -> Callable[[], Any]:
            self._state_handlers.append((state.lower(), func))
            return func
        return register

    def events(self) -> Iterable[dict[str, Any]]:
        """Server-Sent Events を 1 件ずつ返す（ブロッキング）。"""
        request = urllib.request.Request(self.url + "/api/events", headers={"Accept": "text/event-stream"})
        with urllib.request.urlopen(request) as response:
            for raw in response:
                line = raw.decode("utf-8").rstrip("\r\n")
                if line.startswith("data: "):
                    yield json.loads(line[6:])
                if self._stop.is_set():
                    return

    def run_forever(self) -> None:
        """登録した周期処理とイベント処理を Ctrl+C / stop_loop() まで実行する。"""
        threads = [threading.Thread(target=self._timer_loop, args=(p, f), daemon=True) for p, f in self._timers]
        if self._write_handlers or self._state_handlers:
            threads.append(threading.Thread(target=self._event_loop, daemon=True))
        for t in threads:
            t.start()
        try:
            while not self._stop.wait(0.2):
                pass
        except KeyboardInterrupt:
            pass
        finally:
            self._stop.set()

    def stop_loop(self) -> None:
        self._stop.set()

    # ---- 内部 ---------------------------------------------------------
    def _timer_loop(self, period: float, func: Callable[[], Any]) -> None:
        next_time = time.monotonic()
        while not self._stop.is_set():
            with self._lock:
                func()
            next_time += period
            delay = next_time - time.monotonic()
            if delay < 0:
                next_time = time.monotonic()
                delay = 0
            self._stop.wait(delay)

    def _event_loop(self) -> None:
        while not self._stop.is_set():
            try:
                for event in self.events():
                    self._dispatch(event)
            except (urllib.error.URLError, ConnectionError, OSError):
                self._stop.wait(1.0)

    def _dispatch(self, event: dict[str, Any]) -> None:
        if event.get("type") == "write":
            if event.get("source") == self.source:
                return
            device = str(event["device"]).upper()
            start, count = int(event["start"]), int(event["count"])
            for want_device, number, func in self._write_handlers:
                if device == want_device and (count == 0 or start <= number < start + count):
                    with self._lock:
                        func(WriteEvent(device, start, count, str(event.get("source", ""))))
        elif event.get("type") == "state":
            state = str(event.get("state", "")).lower()
            for want, func in self._state_handlers:
                if want == state:
                    with self._lock:
                        func()

    @staticmethod
    def _split(address: str) -> tuple[str, int]:
        m = _ADDRESS.match(address.strip().upper())
        if not m:
            raise ValueError(f"アドレス '{address}' を解釈できません")
        letters, digits = m.group("dev"), m.group("num")
        for name in _KNOWN_DEVICES:
            if letters.startswith(name):
                text = letters[len(name):] + digits
                try:
                    number = int(text, 16 if name in _HEX_DEVICES else 10)
                except ValueError:
                    continue
                q = m.group("q")
                return (f"{q}\\{name}" if q else name), number
        raise ValueError(f"アドレス '{address}' のデバイス名が不明です")

    @staticmethod
    def _quote(text: str) -> str:
        return urllib.parse.quote(text, safe="")

    def _read(self, address: str, count: int, kind: str) -> list[Any]:
        query = urllib.parse.urlencode({"count": count, "type": kind})
        return self._request("GET", f"/api/devices/{self._quote(address)}?{query}")["values"]

    def _write(self, address: str, values: list[Any], kind: str) -> None:
        self._request("PUT", f"/api/devices/{self._quote(address)}", {"values": values, "type": kind})

    def _request(self, method: str, path: str, body: Any = None) -> Any:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.url + path, data=data, method=method)
        request.add_header("X-Source", urllib.parse.quote(self.source))
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                payload = response.read()
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")
            try:
                detail = json.loads(detail).get("error", detail)
            except (ValueError, AttributeError):
                pass
            raise PlcError(f"{method} {path}: {detail}") from None
        return json.loads(payload) if payload else None

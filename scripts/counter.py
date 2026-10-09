"""サンプル: 値の周期変化とクライアント書込みへの反応。

GUI の「スクリプト」画面から実行するか、サーバー起動中に `python scripts/counter.py` で実行する
（python フォルダを PYTHONPATH に入れるか、下の sys.path 追加で plc_sim を読み込む）。
"""

import math
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "python"))
from plc_sim import Plc  # noqa: E402

plc = Plc()
start = plc.get("D100")


@plc.every(0.1)
def count_up():
    """100ms ごとに D100 をカウントアップ。"""
    plc.inc("D100")


@plc.every(1.0)
def blink():
    """1 秒ごとに Y0 を点滅。"""
    plc.toggle("Y0")


@plc.every(0.05)
def sine():
    """D300-D301 に正弦波（float）。"""
    import time
    plc.set_float("D300", math.sin(time.monotonic()) * 100.0)


@plc.on_write("M0")
def on_m0(event):
    """クライアントが M0 を ON にしたら D200 に 1234 を書き、M1 を ON にする。"""
    if plc.get_bit("M0"):
        plc.set("D200", 1234)
        plc.set("M1", True)
        print(f"M0 ON ({event.source})")


@plc.on_state("Stop")
def stopped():
    """STOP になったら Y0 を OFF。"""
    plc.set("Y0", False)


print(f"開始: D100={start}")
plc.run_forever()

"""他の GPU 処理と共有するロックファイルを取ってから、コマンドを1つ実行する（Windows）。

VRAM が小さい PC では、音声認識などの別処理と LM Studio の LLM が同時に載らない。
ロックが空くまで待ち、取れたら LLM を読み込んでから評価を走らせる。
ロックファイルの場所は環境変数 HAKARU_GPU_LOCK で指定する（未指定ならロックせずに実行）。
使い方: py -3.11 tools/with_gpu_lock.py <コマンド…>
"""
from __future__ import annotations

import os
import subprocess
import sys
import time
import urllib.request
from contextlib import contextmanager
from pathlib import Path

LOCK = Path(os.environ["HAKARU_GPU_LOCK"]) if os.environ.get("HAKARU_GPU_LOCK") else None
MODEL = os.environ.get("HAKARU_LOCAL_MODEL", "qwen3.6-35b-a3b")
NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0


@contextmanager
def gpu_lock(owner: str, timeout: float = 6 * 3600):
    """先頭1バイトの排他ロック。ファイルは消さない。"""
    import msvcrt

    start = time.time()
    with LOCK.open("a+b") as f:
        if f.seek(0, 2) == 0:
            f.write(b" ")
            f.flush()
        waited = False
        while True:
            try:
                f.seek(0)
                msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                break
            except OSError:
                if not waited:
                    print("GPU は別の処理が使用中。空くまで待ちます…", flush=True)
                    waited = True
                if time.time() - start > timeout:
                    raise TimeoutError("GPU ロック待ちタイムアウト")
                time.sleep(5)
        try:
            f.seek(0)
            f.truncate()
            f.write(f"{owner} {os.getpid()} {time.time()}".encode())
            f.flush()
            yield
        finally:
            f.seek(0)
            msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)


def loaded() -> bool:
    try:
        with urllib.request.urlopen("http://127.0.0.1:1234/api/v0/models", timeout=5) as r:
            import json
            return any(m.get("id") == MODEL and m.get("state") == "loaded" for m in json.load(r)["data"])
    except OSError:
        return False


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    if LOCK is None:
        return subprocess.run(sys.argv[1:]).returncode
    with gpu_lock("hakaru"):
        if not loaded():
            print(f"{MODEL} を読み込みます", flush=True)
            subprocess.run(["lms", "load", MODEL, "-y"], check=True, creationflags=NO_WINDOW,
                           stdout=subprocess.DEVNULL)
        return subprocess.run(sys.argv[1:]).returncode


if __name__ == "__main__":
    sys.exit(main())

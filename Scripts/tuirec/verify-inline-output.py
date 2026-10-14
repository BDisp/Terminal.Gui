#!/usr/bin/env python3
"""Verify the inline transcript in a Unix PTY; requires the test-only pyte package."""

import argparse
import codecs
import fcntl
import json
import os
from pathlib import Path
import pty
import re
import select
import signal
import struct
import subprocess
import tempfile
import termios
import time

import pyte


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parents[2]
    parser.add_argument("--dll", type=Path, default=root / "Examples/InlineTranscript/bin/Release/net10.0/InlineTranscript.dll")
    parser.add_argument("--columns", type=int, default=50)
    parser.add_argument("--rows", type=int, default=12)
    parser.add_argument("--cast", type=Path)
    parser.add_argument("--redirect-stdout", action="store_true")
    args = parser.parse_args()
    if args.columns < 1 or args.rows < 1:
        parser.error("terminal dimensions must be positive")
    if not args.dll.is_file():
        parser.error("build Examples/InlineTranscript in Release first")

    with tempfile.TemporaryDirectory(prefix="inline-verify-") as directory:
        redirected = Path(directory) / "stdout.txt"
        pid, master = pty.fork()
        if pid == 0:
            try:
                os.environ["TERM"] = "xterm-256color"
                os.environ.pop("DisableRealDriverIO", None)
                os.write(1, b"SHELL BEFORE 1\r\nSHELL BEFORE 2\r\n")
                with redirected.open("wb") as sink:
                    result = subprocess.run(["dotnet", str(args.dll), "--demo"], stdout=sink if args.redirect_stdout else None)
                os.write(1, b"\r\nSHELL AFTER\r\n")
                os._exit(result.returncode)
            except BaseException:
                os._exit(1)

        fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", args.rows, args.columns, 0, 0))
        screen = pyte.HistoryScreen(args.columns, args.rows, history=10000)
        replies = []

        def reply(data):
            replies.append(data)
            os.write(master, data.encode())

        screen.write_process_input = reply
        stream = pyte.Stream(screen)
        decoder = codecs.getincrementaldecoder("utf8")()
        events = [{"version": 2, "width": args.columns, "height": args.rows, "timestamp": int(time.time())}]
        started = time.monotonic()
        query_tail = ""
        sent = False
        status = None
        eof = False

        def consume(data):
            nonlocal query_tail
            events.append([time.monotonic() - started, "o", data])
            stream.feed(data)  # pyte answers DA1 and CPR using its current cursor.
            queries = query_tail + data
            for match in re.finditer(r"\x1b\[(18|14|16)t", queries):
                if match.end() <= len(query_tail):
                    continue
                mode = int(match.group(1))
                if mode == 18:
                    reply(f"\x1b[8;{args.rows};{args.columns}t")
                elif mode == 14:
                    reply(f"\x1b[4;{args.rows * 16};{args.columns * 8}t")
                else:
                    reply("\x1b[6;16;8t")
            query_tail = queries[-16:]

        try:
            while time.monotonic() - started < 15:
                ready, _, _ = select.select([master], [], [], 0.1)
                if ready:
                    try:
                        chunk = os.read(master, 65536)
                    except OSError:
                        eof = True
                        break
                    if not chunk:
                        eof = True
                        break
                    consume(decoder.decode(chunk))
                if not sent and time.monotonic() - started > 1:
                    os.write(master, b"hello\r")
                    sent = True
                child, result = os.waitpid(pid, os.WNOHANG)
                if child:
                    status = result
                    # Drain buffered output after the child exits.
                    while True:
                        try:
                            chunk = os.read(master, 65536)
                        except OSError:
                            break
                        if not chunk:
                            break
                        consume(decoder.decode(chunk))
                    break
            if status is None and eof:
                # EOF can arrive just before the wrapper becomes waitable. Its inherited PTY
                # descriptors are now closed, so reap it instead of misreporting a timeout.
                _, status = os.waitpid(pid, 0)
            if status is None:
                child, result = os.waitpid(pid, os.WNOHANG)
                if child:
                    status = result
                else:
                    os.killpg(pid, signal.SIGTERM)
                    _, status = os.waitpid(pid, 0)
                    raise AssertionError("inline example did not exit within 15 seconds")
        finally:
            os.close(master)

        assert os.waitstatus_to_exitcode(status) == 0
        history = ["".join(cell.data for _, cell in sorted(line.items())) for line in screen.history.top]
        if args.columns >= 30:
            history = [line.rstrip() for line in history]
        text = "\n".join(history + screen.display)
        comparison = text if args.columns >= 30 else text.replace("\n", "")
        messages = re.findall(r"message (\d{3}):", comparison)
        assert messages == [f"{number:03d}" for number in range(24)], messages
        assert comparison.count("SHELL BEFORE 1") == 1
        assert comparison.count("SHELL BEFORE 2") == 1
        assert comparison.count("SHELL AFTER") == 1
        if args.columns >= 30 and args.rows >= 3:
            assert comparison.count("you: hello") == 1
            assert comparison.count("Inline transcript") <= 1
        assert comparison.index("SHELL BEFORE 2") < comparison.index("message 000") < comparison.index("SHELL AFTER")
        if args.redirect_stdout:
            assert redirected.read_bytes() == b"", "terminal output leaked to redirected stdout"
        if args.cast:
            with args.cast.open("w") as output:
                for event in events:
                    output.write(json.dumps(event, ensure_ascii=False) + "\n")
        print(text)
        print(f"PASS: {args.columns}x{args.rows}; preceding shell output and all messages preserved in order")


if __name__ == "__main__":
    main()

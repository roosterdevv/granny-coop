"""Minimal protocol-v1 host for single-instance tests.

Plays the host side of the Granny Co-op handshake (Welcome), then sends
Scene messages on a schedule and prints whatever the guest sends back.
It exists so a join-side feature can be tested with one game instance: a
real pair needs ~5.4 GB of private bytes, this needs ~2.7 GB.

usage:
    python -I tools/fake-host.py --port 47778 --name FakeHost \\
        --scene Scene --scene-at 35 --scene-until 45 \\
        --scene Menu --scene-at 60 --scene-until 70 \\
        --seconds 85

Times are seconds after Welcome is sent.
"""
import argparse
import math
import select
import socket
import struct
import sys
import time

MAGIC = 0x31435047  # "GCP1"
HELLO, WELCOME, REJECT, STATE, SCENE, BYE, GRANNY, DOOR, ITEM, CATCH = range(1, 11)


def pack_str(text):
    data = text.encode("utf-8")
    n = len(data)
    out = bytearray()
    while True:
        byte = n & 0x7F
        n >>= 7
        if n:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            break
    return bytes(out) + data


def unpack_str(data, off):
    n = 0
    shift = 0
    while True:
        byte = data[off]
        off += 1
        n |= (byte & 0x7F) << shift
        if not byte & 0x80:
            break
        shift += 7
    return data[off:off + n].decode("utf-8", "replace"), off + n


class Wire:
    def __init__(self):
        self.seq = 0

    def msg(self, kind, payload=b""):
        self.seq += 1
        return struct.pack("<IBI", MAGIC, kind, self.seq) + payload


def log(text):
    print("%7.2f %s" % (time.monotonic() - START, text), flush=True)


START = time.monotonic()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=47778)
    ap.add_argument("--name", default="FakeHost")
    ap.add_argument("--scene", action="append", default=[],
                    help="scene to ask for; repeat with --scene-at/--scene-until")
    ap.add_argument("--scene-at", action="append", type=float, default=[])
    ap.add_argument("--scene-until", action="append", type=float, default=[])
    ap.add_argument("--seconds", type=float, default=85)
    ap.add_argument("--state-circle", type=float, default=0.0, metavar="RADIUS",
                    help="send a moving player State at 20 Hz round a circle of this "
                         "radius (QA: gives the spectator camera a known path)")
    ap.add_argument("--catch-at", action="append", type=float, default=[],
                    help="send a Catch edge saying the peer is dead, this many seconds "
                         "after Welcome (repeatable)")
    ap.add_argument("--revive-at", action="append", type=float, default=[],
                    help="send a Catch edge saying the peer is alive again, this many "
                         "seconds after Welcome (repeatable)")
    args = ap.parse_args()

    if not (len(args.scene) == len(args.scene_at) == len(args.scene_until)):
        ap.error("--scene, --scene-at and --scene-until must be repeated the same number of times")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", args.port))
    log("fake host listening on 127.0.0.1:%d" % args.port)

    wire = Wire()
    peer = None
    welcomed_at = None
    last_scene = None
    last_state = 0.0
    next_circle_state = 0.0
    last_reset_log = 0.0
    hellos = 0
    catch_sent = [False] * len(args.catch_at)
    revive_sent = [False] * len(args.revive_at)

    while True:
        now = time.monotonic()
        if welcomed_at is not None and now - welcomed_at >= args.seconds:
            log("done")
            return 0

        ready, _, _ = select.select([sock], [], [], 0.1)
        if ready:
            try:
                data, addr = sock.recvfrom(2048)
            except ConnectionResetError:
                # Windows reports ICMP port-unreachable from a datagram we sent
                # to a port that has since closed (the game exiting or recreating
                # its socket). The listener itself is fine, so keep going; log it
                # at most once a second so a dead guest does not flood the output.
                if now - last_reset_log >= 1.0:
                    last_reset_log = now
                    log("peer port reset (ICMP unreachable); still listening")
                continue
            if len(data) < 9 or struct.unpack_from("<I", data)[0] != MAGIC:
                continue
            kind = data[4]
            if kind == HELLO:
                hellos += 1
                version = data[9] if len(data) > 9 else -1
                name = ""
                if len(data) > 10:
                    try:
                        name, _ = unpack_str(data, 10)
                    except Exception:
                        pass
                if peer is None:
                    peer = addr
                    log("hello from %s:%d '%s' v%d" % (addr[0], addr[1], name, version))
                    sock.sendto(wire.msg(WELCOME, bytes([2, 1]) + pack_str(args.name)), addr)
                    log("welcome sent")
                    welcomed_at = time.monotonic()
                elif hellos % 40 == 1:
                    log("still getting hello (peer already set, %d so far)" % hellos)
            elif kind == CATCH and len(data) >= 11:
                alive, cause = data[9], data[10]
                log("catch from guest: %s cause=%s" % ("alive" if alive else "dead", cause))

        if welcomed_at is None:
            continue

        elapsed = time.monotonic() - welcomed_at
        sent_scene = False
        for scene, start, end in zip(args.scene, args.scene_at, args.scene_until):
            if start <= elapsed <= end:
                if last_scene != scene:
                    log("sending scene '%s'" % scene)
                    last_scene = scene
                sock.sendto(wire.msg(SCENE, pack_str(scene)), peer)
                sent_scene = True
                break
            if elapsed > end and last_scene == scene:
                last_scene = None

        # QA: the peer's dead/alive edges, so the guest's handling of a remote
        # catch (avatar hidden while dead, back on the revive) can be checked.
        for i, at in enumerate(args.catch_at):
            if elapsed >= at and not catch_sent[i]:
                catch_sent[i] = True
                log("sending catch: peer dead")
                sock.sendto(wire.msg(CATCH, bytes([0, 1])), peer)
        for i, at in enumerate(args.revive_at):
            if elapsed >= at and not revive_sent[i]:
                revive_sent[i] = True
                log("sending catch: peer alive")
                sock.sendto(wire.msg(CATCH, bytes([1, 0])), peer)

        # QA spectator test: a valid player State walking a circle, at 20 Hz.
        # It also acts as keep-alive, so the idle heartbeat is skipped.
        if args.state_circle > 0.0 and now >= next_circle_state:
            next_circle_state = now + 0.05
            angle = elapsed * 0.8
            x = args.state_circle * math.cos(angle)
            z = args.state_circle * math.sin(angle)
            yaw = (90.0 - math.degrees(angle)) % 360.0
            payload = (bytes([0, 1]) +
                       struct.pack("<fffff", x, 0.0, z, yaw, 0.0))
            sock.sendto(wire.msg(STATE, payload), peer)
            continue

        # The guest drops a peer that is silent for 5 s, so keep it alive with
        # an idle State message while no scene window is open.
        if not sent_scene and now - last_state >= 1.0:
            sock.sendto(wire.msg(STATE, bytes([0, 0]) + b"\0" * 20), peer)
            last_state = now


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)

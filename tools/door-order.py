#!/usr/bin/env python3
"""Look for out-of-order delivery of Door/Item packets in a chaos packet log.

Usage: python -I door-order.py <packets.jsonl>

The mod has no receive-side ordering: `MsgReader.Seq` is parsed and never
used (`Protocol.cs:102`, `CoopNet.cs` calls `NextSeq()` on send only). A door
change sent as a Door/State at seq 105 can therefore be overwritten by a Door
snapshot sent at seq 100 that the relay delivered later. This script answers
whether that happens in a given run:

  * per direction, how many delivered packets arrive after a packet with a
    higher seq (a true send-order inversion), and how big the inversion is;
  * the door messages in delivery order with their seq, kind, hash and state,
    so an inversion that matters can be lined up with the mod's own
    `COOP doors: apply ...` lines;
  * item (kind 0 State) inversions the same way.

Header: magic(4) type(1) seq(4) — head hex covers 16 bytes, so a Door/State
payload (kind, hash, state) is fully inside it. Snapshot entries are not, so a
snapshot is only reported as a whole (kind + count).

Stdlib only. The log is data: nothing in it is executed.
"""

import json
import sys

DOOR_HASHED = {0: "State", 2: "Request"}


def parse_head(h):
    """Return (type, seq, kind, hash, state) from the logged head hex.

    The relay logs the first 15 bytes, so a Door/Item State or Request is fully
    inside it (kind, hash, state); a Snapshot only shows kind and count. All
    integers on the wire are little-endian.
    """
    if not h or h[:8].lower() != "47504331" or len(h) < 20:
        return None
    b = bytes.fromhex(h)
    t = b[4]
    seq = int.from_bytes(b[5:9], "little")
    kind = b[9]
    hsh = st = None
    if kind in (0, 2) and len(b) >= 15:
        hsh = int.from_bytes(b[10:14], "little")
        st = b[14]
    elif kind == 1 and len(b) >= 11:
        hsh = None
        st = b[10]  # entry count, not a state
    return t, seq, kind, hsh, st


def main(path):
    rows = []
    with open(path, "r", encoding="utf-8") as f:
        meta = json.loads(f.readline())
        for line in f:
            line = line.strip()
            if line:
                try:
                    r = json.loads(line)
                except json.JSONDecodeError:
                    # A relay still writing the log may end mid-line.
                    continue
                if "event" not in r:
                    rows.append(r)
    print("log %s profile=%s seed=%s" % (path, meta.get("profile"), meta.get("seed")))

    for direction in ("c2s", "s2c"):
        delivered = []  # (delivery_ms, seq, kind, hash, state, t, delays)
        for r in rows:
            if r["dir"] != direction or r["verdict"] != "fwd":
                continue
            p = parse_head(r.get("head"))
            if not p or p[0] not in (8, 9):
                continue
            t, seq, kind, hsh, st = p
            delays = r.get("copies") or [0.0]
            for c in delays:
                delivered.append((r["t"] + c, seq, p[0], kind, hsh, st,
                                  max(0.0, c)))
        delivered.sort()

        print("\n%s: %d delivered door/item packets" % (direction, len(delivered)))
        best = 0.0
        maxseq = -1
        inv = []
        for i, (dt, seq, typ, kind, hsh, st, _) in enumerate(delivered):
            if seq < maxseq:
                inv.append((i, dt, seq, typ, kind, hsh, st, maxseq))
            else:
                maxseq = seq
        print("  seq inversions: %d" % len(inv))
        for i, dt, seq, typ, kind, hsh, st, hi in inv[:20]:
            shown = ("n=%d" % st) if kind == 1 else (st if st is not None else "-")
            print("    #%d t=%.3f s seq=%d after seq=%d (type %s kind %s hash %s state %s)"
                  % (i, dt / 1000.0, seq, hi, typ, kind,
                     ("%08X" % hsh) if hsh is not None else "-", shown))

        # Delivery-order listing of door (8) and item-state (9/0) packets.
        for typ, label in ((8, "door"), (9, "item")):
            sel = [d for d in delivered if d[2] == typ]
            if not sel:
                continue
            print("  %s packets in delivery order:" % label)
            for dt, seq, _, kind, hsh, st, delay in sel[:60]:
                name = (DOOR_HASHED.get(kind) if typ == 8 else
                        {0: "State", 1: "Snapshot", 2: "SnapRequest"}.get(kind, kind))
                shown = ("n=%d" % st) if kind == 1 else (st if st is not None else "-")
                print("    %8.0f ms seq=%-4d %-10s hash=%-8s state=%-4s +%.1f ms"
                      % (dt, seq, name,
                         ("%08X" % hsh) if hsh is not None else "-",
                         shown, delay))
            if len(sel) > 60:
                print("    ... %d more" % (len(sel) - 60))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: python -I door-order.py <packets.jsonl>")
        sys.exit(2)
    main(sys.argv[1])

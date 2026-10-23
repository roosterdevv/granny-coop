#!/usr/bin/env python3
"""Summarise a tools/chaos packet log (JSONL) written by a relay run.

Usage: python -I chaos-stats.py <packets.jsonl>

Reads only that file and prints:
  - per-direction seen/forwarded/dropped(kind)/duplicated and one-way delay stats
  - a histogram of GrannyCoop message types (protocol header: magic, type, seq)
  - door (type 8) and item (type 9, split by item kind) delivery detail
  - the longest run of silence per direction between delivered packets
Stdlib only. The log is data: nothing in it is executed.
"""
import json
import sys


def pct(xs, p):
    if not xs:
        return float("nan")
    xs = sorted(xs)
    k = min(len(xs) - 1, max(0, int(round((p / 100.0) * (len(xs) - 1)))))
    return xs[k]


def fmt_delay(xs):
    if not xs:
        return "n=0"
    return ("n=%d mean=%.1f p50=%.1f p95=%.1f max=%.1f ms"
            % (len(xs), sum(xs) / len(xs), pct(xs, 50), pct(xs, 95), max(xs)))


def main(path):
    rows = []
    events = []
    with open(path, "r", encoding="utf-8") as f:
        meta = json.loads(f.readline())
        for line in f:
            line = line.strip()
            if line:
                r = json.loads(line)
                # The relay also logs control-plane rows ({"event": ...}), which
                # carry no dir/verdict. Keep them apart from packet rows.
                if "event" in r:
                    events.append(r)
                else:
                    rows.append(r)

    print("log %s profile=%s seed=%s packets=%d"
          % (path, meta.get("profile"), meta.get("seed"), len(rows)))
    for e in events:
        print("  event t=%.3f s %s %s"
              % (e["t"] / 1000.0, e["event"],
                 {k: v for k, v in e.items() if k not in ("t", "event")} or ""))

    types = {1: "Hello", 2: "Welcome", 3: "Reject", 4: "State",
             5: "Scene", 6: "Bye", 7: "Granny", 8: "Door", 9: "Item"}
    item_kinds = {0: "State", 1: "Snapshot", 2: "SnapRequest"}

    for d in ("c2s", "s2c"):
        sel = [r for r in rows if r["dir"] == d]
        fwd = [r for r in sel if r["verdict"] == "fwd"]
        drop = [r for r in sel if r["verdict"] == "drop"]
        reasons = {}
        for r in drop:
            reasons[r.get("reason") or "?"] = reasons.get(r.get("reason") or "?", 0) + 1
        copies = [c for r in fwd for c in (r.get("copies") or [])]
        dup = sum(max(0, len(r.get("copies") or []) - 1) for r in fwd)
        print("\n%s seen=%d fwd=%d drop=%d dup=%d reasons=%s"
              % (d, len(sel), len(fwd), len(drop), dup, reasons))
        print("  delay over delivered copies: %s" % fmt_delay(copies))

        hist = {}
        for r in fwd:
            h = r.get("head") or ""
            if h[:8].lower() == "47504331" and len(h) >= 10:
                t = int(h[8:10], 16)
                hist[t] = hist.get(t, 0) + 1
            else:
                hist[-1] = hist.get(-1, 0) + 1
        print("  types: " + " ".join(
            "%s=%d" % (types.get(t, "bad" if t == -1 else t), n)
            for t, n in sorted(hist.items())))

        # Door detail: delivery and delays in wall order.
        doors = [r for r in fwd if (r.get("head") or "")[8:10].lower() == "08"]
        ddelay = [c for r in doors for c in (r.get("copies") or [])]
        ddrop = [r for r in drop if (r.get("head") or "")[8:10].lower() == "08"]
        if doors or ddrop:
            print("  door: delivered=%d dropped=%d %s"
                  % (len(doors), len(ddrop), fmt_delay(ddelay)))

        # Item detail split by kind byte (first payload byte after the header).
        items = [r for r in fwd if (r.get("head") or "")[8:10].lower() == "09"]
        if items:
            by = {}
            for r in items:
                h = r.get("head") or ""
                k = int(h[18:20], 16) if len(h) >= 20 else -1
                by.setdefault(k, []).append(r)
            for k in sorted(by):
                rs = by[k]
                delays = [c for r in rs for c in (r.get("copies") or [])]
                print("  item %s: %d %s"
                      % (item_kinds.get(k, k), len(rs), fmt_delay(delays)))

    # 't' is milliseconds since the relay process started; 'copies' are the
    # per-packet delivery delays in milliseconds (see tools/chaos/relay.py).
    print("")
    for d in ("c2s", "s2c"):
        ts = sorted(r["t"] for r in rows if r["dir"] == d and r["verdict"] == "fwd")
        gaps = [(b - a, a) for a, b in zip(ts, ts[1:])]
        if gaps:
            g, at = max(gaps)
            print("%s longest delivered-packet gap: %.2f s at t=%.1f s (first t=%.1f s)"
                  % (d, g / 1000.0, at / 1000.0, ts[0] / 1000.0))
        # Forwarding attempts, including drops, to show what the sender emitted.
        ats = sorted(r["t"] for r in rows if r["dir"] == d)
        gaps2 = [(b - a, a) for a, b in zip(ats, ats[1:])]
        if gaps2:
            g, at = max(gaps2)
            print("%s longest send-attempt gap: %.2f s at t=%.1f s"
                  % (d, g / 1000.0, at / 1000.0))

    # Door messages in arrival order, so a run's log can be lined up with them.
    print("")
    print("door packets (relay ms, dir, verdict, delay ms):")
    for r in rows:
        h = (r.get("head") or "")
        if h[8:10].lower() != "08":
            continue
        print("  %8.0f %s %-4s %s" % (r["t"], r["dir"], r["verdict"],
                                      ",".join("%.1f" % c for c in (r.get("copies") or [])) or "-"))
    print("")
    print("item packets (relay ms, dir, verdict, kind, delay ms):")
    for r in rows:
        h = (r.get("head") or "")
        if h[8:10].lower() != "09":
            continue
        k = int(h[18:20], 16) if len(h) >= 20 else -1
        print("  %8.0f %s %-4s %-11s %s" % (r["t"], r["dir"], r["verdict"],
                                            item_kinds.get(k, k),
                                            ",".join("%.1f" % c for c in (r.get("copies") or [])) or "-"))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__.strip().splitlines()[2])
        sys.exit(2)
    main(sys.argv[1])

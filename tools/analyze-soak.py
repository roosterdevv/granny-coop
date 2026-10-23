#!/usr/bin/env python3
"""Pair host/guest status samples from a run-pair log and report disagreements.

The mod prints one `COOP status` line per end every 10 wall-clock seconds
(v0.4.5+), so a soak is a diff of the two logs. This script:

  * pairs each guest sample with the host sample inside 0.5 s,
  * reports every pair where dh or ih disagrees, with the counters around it,
  * summarises how many pairs matched and which values were seen,
  * parses `mem t=<n>s pid:ws=..MB priv=..MB` samples and reports growth.

Usage: python -I analyze-soak.py <run file> [--window 0.5]
"""
import re
import sys
from collections import Counter, defaultdict

STATUS = re.compile(
    r"^\[(\d\d):(\d\d):(\d\d)\.(\d+)\].*COOP status role=(\w+).*?"
    r"(doors=\d+ applied=\d+ sent=\d+ recv=\d+ req=\d+ noClip=\d+)\s+"
    r"(items=\d+ iSent=\d+ iRecv=\d+ iApplied=\d+ iRefresh=\d+/\d+)\s+"
    r"dh=(\S+)\s+ih=(\S+)")
MEM = re.compile(r"^mem t=(\d+)s\s+(.*)$")
MEMP = re.compile(r"(\d+):ws=(\d+)MB priv=(\d+)MB")
LOCAL = re.compile(r"\blocal=(\S+)")


def secs(h, m, s, frac):
    return int(h) * 3600 + int(m) * 60 + int(s) + float("0." + frac)


def main():
    path = sys.argv[1]
    window = 0.5
    if "--window" in sys.argv:
        window = float(sys.argv[sys.argv.index("--window") + 1])

    samples = defaultdict(list)   # role -> [(t, dh, ih, counters)]
    mem = defaultdict(list)       # pid -> [(t, ws, priv)]
    for line in open(path, encoding="utf-8", errors="replace"):
        m = STATUS.match(line.strip())
        if m:
            t = secs(*m.group(1, 2, 3, 4))
            role = m.group(5)
            samples[role].append((t, m.group(8), m.group(9),
                                  m.group(6) + " " + m.group(7)))
            continue
        m = MEM.match(line.strip())
        if m:
            t = int(m.group(1))
            for pid, ws, priv in MEMP.findall(m.group(2)):
                mem[pid].append((t, int(ws), int(priv)))

    hosts = samples.get("Host", [])
    joins = samples.get("Join", [])
    print(f"status samples: host={len(hosts)} join={len(joins)}")

    used = set()
    pairs = 0
    dh_bad, ih_bad, not_ready = [], [], 0
    for jt, jdh, jih, jc in joins:
        if jdh == "not-ready" or jih == "not-ready":
            not_ready += 1
            continue
        best = None
        for i, (ht, hdh, hih, hc) in enumerate(hosts):
            if i in used or abs(ht - jt) > window:
                continue
            if best is None or abs(ht - jt) < abs(hosts[best][0] - jt):
                best = i
        if best is None:
            continue
        used.add(best)
        pairs += 1
        ht, hdh, hih, hc = hosts[best]
        if hdh != jdh:
            dh_bad.append((jt, jdh, jdh and jc, ht, hdh, hc))
        if hih != jih:
            ih_bad.append((jt, jih, jc, ht, hih, hc))

    print(f"paired={pairs} (unpaired join samples: {len(joins) - pairs - not_ready}"
          f", not-ready: {not_ready}, window={window}s)")
    print(f"door mismatches: {len(dh_bad)}/{pairs}")
    for jt, jdh, jc, ht, hdh, hc in dh_bad:
        print(f"  guest {fmt(jt)} {jdh}  <->  host {fmt(ht)} {hdh}")
        print(f"    guest: {jc}")
        print(f"    host : {hc}")
    print(f"item mismatches: {len(ih_bad)}/{pairs}")
    for jt, jih, jc, ht, hih, hc in ih_bad:
        print(f"  guest {fmt(jt)} {jih}  <->  host {fmt(ht)} {hih}")

    for name, vals in (("door", [h for _, h, _, _ in hosts] + [j for _, j, _, _ in joins]),
                       ("item", [i for _, _, i, _ in hosts] + [i for _, _, i, _ in joins])):
        common = Counter(vals).most_common(5)
        print(f"{name} values seen: " + ", ".join(f"{v}x{k}" for v, k in common))

    if mem:
        print("memory (first -> last, delta):")
        for pid, rows in sorted(mem.items()):
            t0, ws0, pr0 = rows[0]
            t1, ws1, pr1 = rows[-1]
            print(f"  pid {pid}: t={t0}s ws={ws0}MB priv={pr0}MB -> "
                  f"t={t1}s ws={ws1}MB priv={pr1}MB "
                  f"(ws {ws1 - ws0:+d}, priv {pr1 - pr0:+d})")


def fmt(t):
    return f"{int(t // 3600):02d}:{int(t % 3600 // 60):02d}:{t % 60:06.3f}"


if __name__ == "__main__":
    main()

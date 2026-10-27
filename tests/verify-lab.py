"""Judges one lab tour: compares the host's and the client's JSON-line logs (written by Diagnostics/Lab.cs).

Usage: python -I tests/verify-lab.py artifacts/tests/<run>
Writes <run>/results.json and exits 1 if any check failed.
"""
import json
import os
import sys


def load(path):
    rows = []
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                try:
                    rows.append(json.loads(line))
                except ValueError:
                    pass
    return rows


def events(rows, name):
    return [r for r in rows if r.get("ev") == name]


def first(rows, name):
    e = events(rows, name)
    return e[0] if e else None


def samples(rows, t0, t1, key=None):
    return [r for r in rows if r.get("ev") == "s" and t0 <= r["t"] <= t1 and (key is None or key in r)]


def near(rows, t, key):
    """The sample closest to time t that has key."""
    best = None
    for r in rows:
        if r.get("ev") == "s" and key in r and (best is None or abs(r["t"] - t) < abs(best["t"] - t)):
            best = r
    return best


def main(run):
    host = load(os.path.join(run, "host.jsonl"))
    client = load(os.path.join(run, "client.jsonl"))
    results = []

    def check(name, ok, evidence):
        results.append({"check": name, "pass": bool(ok), "evidence": evidence})

    for side, rows in (("host", host), ("client", client)):
        bad = events(rows, "fail") + events(rows, "timeout")
        check("no-stalls." + side, not bad and rows, "%d log lines, stalls: %s" % (len(rows), [b.get("why") or b.get("what") for b in bad]))

    check("connect", first(host, "connected") and first(client, "connected"),
          "host %s / client %s" % (first(host, "connected"), first(client, "connected")))

    hm, cm = first(host, "map"), first(client, "map")
    check("world-map", hm and cm and hm["scene"] == cm["scene"], "host %s / client %s" % (hm and hm["scene"], cm and cm["scene"]))

    def movement(name, mover, watcher, key, step, min_own):
        s, e = first(mover, step + ".start"), first(mover, step + ".end")
        if not s or not e:
            check(name, False, "step %s missing" % step)
            return
        a, b = near(mover, s["t"] - 0.1, key), near(mover, e["t"] + 1.0, key)
        c, d = near(watcher, s["t"] - 0.1, key), near(watcher, e["t"] + 1.5, key)
        if not (a and b and c and d):
            check(name, False, "no %s samples" % key)
            return
        own = b[key][0] - a[key][0]
        seen = d[key][0] - c[key][0]
        gap = abs(d[key][0] - b[key][0])
        ok = abs(own) >= min_own and seen * own > 0 and abs(seen) >= 0.6 * abs(own) and gap <= max(0.25 * abs(own), 0.05)
        check(name, ok, "mover moved %s by %.2f, partner saw %.2f, final gap %.2f" % (key, own, seen, gap))

    movement("map.host-cup-moves", host, client, "p1", "move.map.host", 0.2)
    movement("map.guest-cup-moves", client, host, "p2", "move.map.client", 0.2)

    hl, cl = first(host, "level"), first(client, "level")
    # The guest follows the host's scene change directly; the session-snapshot auto-follow is a slower backup.
    gap = abs(cl["t"] - hl["t"]) if hl and cl else None
    check("level-transition", hl and cl and hl["scene"] == cl["scene"] and gap <= 3.0,
          "host %s / client %s, %s apart" % (hl and hl["scene"], cl and cl["scene"], "%.1f s" % gap if gap is not None else "-"))

    movement("level.host-cup-moves", host, client, "p1", "move.level.host", 100)
    movement("level.guest-cup-moves", client, host, "p2", "move.level.client", 100)

    hit = first(host, "boss.hit")
    if hit and hit.get("ok"):
        target = hit["after"]
        seen = [r["boss"] for r in samples(client, hit["t"], hit["t"] + 3.0, "boss")]
        before = near(client, hit["t"] - 0.3, "boss")
        ok = seen and abs(seen[-1] - target) <= 0.05 * hit["before"] and before and before["boss"] > target + 0.1 * hit["before"]
        check("boss-hp-sync", ok, "host %.1f -> %.1f; client before %s, last within 3 s %s"
              % (hit["before"], target, before and before["boss"], seen[-1] if seen else None))
    else:
        check("boss-hp-sync", False, "host could not damage the boss: %s" % hit)

    def death(name, step):
        kill = first(client, step)
        hits = events(client, step + ".hit")
        if not (kill and kill.get("ok")):
            check(name, False, "client could not kill its cup: %s" % kill)
            return
        late = []
        for h in hits:
            seen = [r for r in samples(host, h["t"], h["t"] + 2.5, "p2") if r["p2"][3] <= h["hp"] or r["p2"][2] == 1]
            if not seen:
                late.append(h["hp"])
        dead = [r for r in samples(host, kill["t"], kill["t"] + 3.0, "p2") if r["p2"][2] == 1]
        check(name, dead and not late,
              "%d hits (client HP %s); host missed HP %s; host saw P2 dead %s"
              % (len(hits), [h["hp"] for h in hits], late or "none",
                 "%.2f s after the last hit" % (dead[0]["t"] - kill["t"]) if dead else "never"))

    death("guest-hits-and-death", "kill")

    rev = first(client, "revive.request")
    if rev and rev.get("ok"):
        win = 6.0
        alive_c = [r for r in samples(client, rev["t"], rev["t"] + win, "p2") if r["p2"][2] == 0]
        alive_h = [r for r in samples(host, rev["t"], rev["t"] + win, "p2") if r["p2"][2] == 0]
        hp0 = near(host, rev["t"] - 0.3, "p1")
        hp1 = near(host, rev["t"] + win, "p1")
        donor = hp0 and hp1 and hp1["p1"][3] == hp0["p1"][3] - 1
        check("revive", alive_c and alive_h and donor,
              "client alive again: %s, host sees P2 alive: %s, host P1 HP %s -> %s (heart given)"
              % (bool(alive_c), bool(alive_h), hp0 and hp0["p1"][3], hp1 and hp1["p1"][3]))
    else:
        check("revive", False, "client had no ghost to revive: %s" % rev)

    death("guest-second-death", "kill2")
    parry = first(host, "parry")
    if parry and parry.get("ok"):
        alive_c = [r for r in samples(client, parry["t"], parry["t"] + 5.0, "p2") if r["p2"][2] == 0]
        stay = alive_c and all(r["p2"][2] == 0 for r in samples(host, alive_c[0]["t"] + 1.0, alive_c[0]["t"] + 4.0, "p2"))
        check("parry-revive", alive_c and stay,
              "host parried P2's ghost; client P2 alive %s; host's P2 stayed alive after: %s"
              % ("%.2f s later" % (alive_c[0]["t"] - parry["t"]) if alive_c else "never", bool(stay)))
    else:
        check("parry-revive", False, "host found no ghost to parry: %s" % parry)

    hx, ha, ca = first(host, "exit.level"), first(host, "map.again"), first(client, "map.again")
    gap = abs(ca["t"] - ha["t"]) if ha and ca else None
    check("back-to-map", hx and ha and ca and gap <= 3.0,
          "host exit %s, host map %s, client map %s, %s apart" % (bool(hx), bool(ha), bool(ca), "%.1f s" % gap if gap is not None else "-"))

    passed = sum(r["pass"] for r in results)
    with open(os.path.join(run, "results.json"), "w", encoding="utf-8") as f:
        json.dump({"passed": passed, "total": len(results), "results": results}, f, indent=2)
    for r in results:
        print("%s %-24s %s" % ("PASS" if r["pass"] else "FAIL", r["check"], r["evidence"]))
    print("%d/%d checks passed" % (passed, len(results)))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))

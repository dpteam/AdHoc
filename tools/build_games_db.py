#!/usr/bin/env python3
"""
Build data/games.json from the curated PPSSPP table plus a PSP database dump.

Usage:
  python tools/build_games_db.py --pspudb pspudb.db --games data/games.json --out data/games.json

The PSP database (https://github.com/georgemoralis/pspudb, GPL-2.0) gives us
disc ids (ULUS/ULES/ULJM/NPxx...) together with game titles. Only the id/title
pairs are used here; existing curated titles win, new ids are appended.
"""
import argparse
import datetime
import json
import re
import sqlite3

ID_RE = re.compile(r"^[A-Z]{4}[0-9]{5}$")
VERSION_SUFFIX_RE = re.compile(r"\s*\((?:v\d+|Platinum|Greatest Hits|Essentials)\)\s*$", re.IGNORECASE)


def load_existing(path):
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    games = {k.upper(): v for k, v in (data.get("games") or {}).items()}
    links = {k.upper(): v.upper() for k, v in (data.get("crosslinks") or {}).items()}
    return games, links


def load_pspudb(path):
    con = sqlite3.connect(path)
    rows = list(con.execute("""
        select g.title, i.discid
        from games g join images i on i.gameid = g.id
    """))
    con.close()

    found = {}
    for title, discid in rows:
        if not discid:
            continue
        product = discid.strip().upper().replace("-", "").replace(" ", "")
        if not ID_RE.match(product):
            continue
        title = (title or "").strip()
        if not title:
            continue
        title = VERSION_SUFFIX_RE.sub("", title).strip()
        if not title:
            continue
        # prefer the plain title if the same id shows up with a "(v2)" release
        if product not in found or len(title) < len(found[product]):
            found[product] = title
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pspudb", required=True)
    ap.add_argument("--games", default="data/games.json")
    ap.add_argument("--out", default="data/games.json")
    args = ap.parse_args()

    games, links = load_existing(args.games)
    curated = len(games)

    from_db = load_pspudb(args.pspudb)
    added = 0
    for product, title in sorted(from_db.items()):
        if product not in games:
            games[product] = title
            added += 1

    # crosslink targets should exist as titles, otherwise fall back to the id
    for src, dst in list(links.items()):
        if dst not in games:
            games[dst] = dst

    out = {
        "version": 1,
        "updatedUtc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z"),
        "games": dict(sorted(games.items())),
        "crosslinks": dict(sorted(links.items())),
    }
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print("curated:", curated)
    print("from pspudb:", len(from_db))
    print("added:", added)
    print("total games:", len(games))
    print("crosslinks:", len(links))
    print("wrote", args.out)


if __name__ == "__main__":
    main()
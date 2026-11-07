#!/usr/bin/env python3
"""
Build data/adhoc.json - which product IDs are known to support Ad Hoc multiplayer.

Usage:
  python tools/build_adhoc.py --pspudb pspudb.db --out data/adhoc.json

Evidence (only positive evidence turns a game green):

  1. pspudb (https://github.com/georgemoralis/pspudb, GPL-2.0) has a machine
     readable multiplayer field: "AdHoc: 2-4 | Infrastructure: -".  An explicit
     player count means the game really has Ad Hoc multiplayer.
     "AdHoc: -" means the site says no - we treat that as "no evidence", not as
     a hard denial, because it is wrong for some titles (Phantasy Star Portable 2
     is marked "-" yet has ad hoc co-op).

  2. The store description often says it in words ("2P co-op via Ad Hoc mode",
     "race friends through levels via Ad Hoc").  We only accept a mention when a
     multiplayer word sits near it, and we skip gameshare-only mentions: sharing
     a level or a demo is not something you can play together on this server.

  3. A small curated table for titles that are known to work but are missing or
     mislabelled upstream (Tekken 6, Dissidia, God Eater, Initial D, ...).
     Confirmed against the PPSSPP ad hoc compatibility list
     (https://github.com/AkiraJkr/PPSSPP-Adhoc-Compatibility-List) and in-game
     testing on this server.

Everything else stays "unknown" and is shown grey: absence of evidence is not
evidence of absence, and a wrong green badge is worse than no badge.
"""
import argparse
import datetime
import json
import os
import re
import sqlite3

FIELD_RE = re.compile(r'AdHoc:\s*([0-9]+(?:\s*-\s*[0-9]+)?)')
HOC_RE = re.compile(r'ad[\s\-]?hoc', re.I)
PLAY_RE = re.compile(
    r'multiplayer|multi-player|co-?op|competiti|competition|versus|deathmatch|'
    r'battle|race|racing|tournament|team|players|friends', re.I)
SHARE_RE = re.compile(r'gameshare|game share|share (?:your |the )?levels?|trial version', re.I)

# Titles confirmed by PPSSPP testing or in-game sessions, but missing/wrong upstream.
CURATED = {
    'ULUS10529': '2-4', 'ULES01439': '2-4', 'NPJH50043': '2-4',   # Phantasy Star Portable 2
    'ULUS10410': '2-4', 'ULES01218': '2-4', 'ULJM05309': '2-4',   # Phantasy Star Portable
    'ULUS10466': '2', 'ULES01376': '2', 'NPJH50184': '2',         # Tekken 6
    'ULUS10505': '2-6', 'ULES01441': '2-6', 'ULJM05600': '2-6',   # Kingdom Hearts Birth by Sleep
    'ULUS10479': '2-4', 'ULES01507': '2-4', 'NPJH50335': '2-4',   # Lord of Arcana
    'ULJS00237': '2-4', 'NPJH50352': '2-4', 'ULJS00350': '2-4',   # God Eater
    'NPJH50377': '2', 'ULUS10551': '2', 'ULES01514': '2',         # Dissidia 012
    'ULUS10420': '2', 'ULES01299': '2', 'ULJM05480': '2',         # Dissidia
    'ULJM05093': '2-4', 'ULKS46068': '2-4',                       # Initial D: Street Stage
    'ULUS10141': '2-8', 'ULES00559': '2-8',                       # Medal of Honor: Heroes
    'NPJH50045': '2-4', 'ULUS10509': '2-4', 'ULES01356': '2-4',   # MGS: Peace Walker
}


def normalize(discid):
    if not discid:
        return ''
    pid = discid.strip().upper().replace('-', '').replace(' ', '')
    return pid if len(pid) == 9 else ''


def from_pspudb(path):
    """-> (field_yes, text_yes) dicts: id -> player count / marker."""
    con = sqlite3.connect(path)
    field_yes, text_yes = {}, {}

    for mp, desc, discid in con.execute("""
        select g.multiplayer, g.description, i.discid
        from games g join images i on i.gameid = g.id
    """):
        pid = normalize(discid)
        if not pid:
            continue

        s = str(mp).strip() if mp is not None else ''
        if s:
            m = FIELD_RE.search(s)
            if m:
                val = m.group(1).replace(' ', '')
                if val != '-' and pid not in field_yes:
                    field_yes[pid] = val

        d = str(desc) if desc else ''
        if not d or pid in field_yes or pid in text_yes:
            continue

        for m in HOC_RE.finditer(d):
            window = d[max(0, m.start() - 160):m.end() + 160]
            if not PLAY_RE.search(window):
                continue
            if SHARE_RE.search(window) and not re.search(r'multiplayer|co-?op|competit', window, re.I):
                continue          # level/demo sharing only - not playable together
            text_yes[pid] = 'text'
            break

    con.close()
    return field_yes, text_yes


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pspudb', required=True)
    ap.add_argument('--games', default='data/games.json')
    ap.add_argument('--out', default='data/adhoc.json')
    args = ap.parse_args()

    known_ids = set()
    if os.path.exists(args.games):
        with open(args.games, encoding='utf-8') as f:
            known_ids = set(k.upper() for k in json.load(f).get('games', {}))

    field_yes, text_yes = from_pspudb(args.pspudb)

    result = {}
    sources = {'pspudb-field': 0, 'pspudb-text': 0, 'curated': 0}

    for pid, players in field_yes.items():
        result[pid] = players
        sources['pspudb-field'] += 1
    for pid in text_yes:
        if pid not in result:
            result[pid] = ''
            sources['pspudb-text'] += 1
    for pid, players in CURATED.items():
        if pid not in result:
            sources['curated'] += 1
        result[pid] = players

    if known_ids:
        result = {k: v for k, v in result.items() if k in known_ids}

    out = {
        'version': 1,
        'updatedUtc': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.000Z'),
        'note': 'Product IDs with confirmed Ad Hoc support. Empty string = supported, player count unknown.',
        'sources': sources,
        'games': dict(sorted(result.items())),
    }

    os.makedirs(os.path.dirname(args.out) or '.', exist_ok=True)
    with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
        f.write('\n')

    print('pspudb field      :', sources['pspudb-field'])
    print('pspudb text       :', sources['pspudb-text'])
    print('curated           :', sources['curated'])
    print('total confirmed   :', len(result))
    if known_ids:
        print('of known ids      : %.1f%%' % (100.0 * len(result) / len(known_ids)))
    print('wrote', args.out)


if __name__ == '__main__':
    main()

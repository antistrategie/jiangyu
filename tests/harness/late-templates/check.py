#!/usr/bin/env python3
"""Waits for the running game's log to show the late-template harness landing every
dependency, then reads the live templates through the Studio bridge and checks their final
state. Exit code 0 when every check passes."""
import json
import os
import socket
import struct
import sys
import time

GAME = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser('~/.local/share/Steam/steamapps/common/Menace')
LOG = os.path.join(GAME, 'MelonLoader', 'Latest.log')
BRIDGE = os.path.join(GAME, 'UserData', 'jiangyu-bridge.json')
MOD = 'jiangyu-late-harness'
TIMEOUT = float(os.environ.get('HARNESS_TIMEOUT', '240'))

# Every line the loader writes at message level along the way, in the order they must come.
EXPECTED = [
    ('mod loaded', f'[{MOD}] 1.0.0: 3 clones'),
    ('registrar clock', f'[{MOD}] late registrar: clock started'),
    ('id 1 registered late', 'late registrar: registered EntityTemplate:harness.late_entity in'),
    ('A patch on a late target landed', "Template patch 'EntityTemplate:harness.late_entity': every template it refers to is registered, applied 1 of 1 op(s)."),
    ('B and C clones registered on the late source', 'Template clone: 2 EntityTemplate clone(s) registered on a source that turned up late.'),
    ('B clone patched', "Template patch 'EntityTemplate:harness.clone_of_late': every template it refers to is registered, applied 1 of 1 op(s)."),
    ('C chained clone replayed', "Template patch 'EntityTemplate:harness.clone_of_clone': every template it refers to is registered, replayed with its clone chain."),
    ('D ref inside a constructed handler landed', "Template patch 'PerkTemplate:harness.perk': every template it refers to is registered, applied 2 of 2 op(s)."),
    ('id 2 registered after the schedule', 'late registrar: registered EntityTemplate:harness.late_entity_2 in'),
    ('E landed on the steady-state pass', "Template patch 'EntityTemplate:harness.late_entity_2': every template it refers to is registered, applied 1 of 1 op(s)."),
    ('steady-state pass reported the landing', 'Template late pass at steady-state pass: 1 template(s) landed.'),
]
FORBIDDEN = [
    ('no harness template skipped as a mismatch', ('harness.', 'skipping')),
    ('no harness template unresolvable', ('harness.', 'cannot be looked up')),
    ('no held block on an id that arrived inside the schedule', ('harness.late_entity\'', 'Applied once every one of them is registered.')),
    ('no held block on the perk', ('harness.perk', 'Applied once every one of them is registered.')),
]
# The second id arrives after the scene's poll schedule, so its block is reported once at the
# schedule's end, and once only.
REPORTED_ONCE = ("Template patch 'EntityTemplate:harness.late_entity_2': waiting on EntityTemplate:harness.late_entity_2; 1 op(s) held. Applied once every one of them is registered.", 1)
FINAL = {
    ('EntityTemplate', 'harness.late_entity', 'ArmyPointCost'): '21',
    ('EntityTemplate', 'harness.clone_of_late', 'ArmyPointCost'): '31',
    ('EntityTemplate', 'harness.clone_of_clone', 'ArmyPointCost'): '41',
    ('EntityTemplate', 'harness.late_entity_2', 'ArmyPointCost'): '51',
}


def read_log():
    try:
        with open(LOG, encoding='utf-8', errors='replace') as f:
            return f.read()
    except OSError:
        return ''


def session_lines(text):
    """The lines of the session the harness mod loaded in: the last one in the log."""
    marker = EXPECTED[0][1]
    idx = text.rfind(marker)
    return text[idx:].split('\n') if idx >= 0 else []


def bridge(name, args=None):
    port = json.load(open(BRIDGE))['port']
    s = socket.create_connection(('127.0.0.1', port), timeout=60)
    req = json.dumps({'id': '1', 'method': 'command', 'params': {'name': name, 'args': args or {}}}).encode()
    s.sendall(struct.pack('>I', len(req)) + req)
    hdr = b''
    while len(hdr) < 4:
        hdr += s.recv(4 - len(hdr))
    n = struct.unpack('>I', hdr)[0]
    body = b''
    while len(body) < n:
        body += s.recv(n - len(body))
    s.close()
    return json.loads(body)


def main():
    initial = read_log()
    initial_head = initial[:400]
    print(f'waiting up to {TIMEOUT:.0f}s for a game session that loads {MOD} ...')
    deadline = time.time() + TIMEOUT
    lines = []
    while time.time() < deadline:
        text = read_log()
        # A new session rewrites the log from the top; a session already running when the
        # check started counts too, as long as it loaded the harness.
        fresh = text[:400] != initial_head or (initial and EXPECTED[0][1] in initial)
        lines = session_lines(text) if fresh else []
        if lines and any(EXPECTED[-1][1] in l for l in lines):
            break
        time.sleep(2)

    results = []
    cursor = 0
    for label, needle in EXPECTED:
        hit = next((i for i in range(cursor, len(lines)) if needle in lines[i]), None)
        results.append((label, hit is not None, needle if hit is None else lines[hit].strip()[:160]))
        if hit is not None:
            cursor = hit + 1
    for label, (needle, marker) in FORBIDDEN:
        bad = [l for l in lines if needle in l and marker in l and 'late registrar' not in l]
        results.append((label, not bad, bad[0].strip()[:160] if bad else 'none'))
    reported = [l for l in lines if REPORTED_ONCE[0] in l]
    results.append(('E reported once at the schedule end', len(reported) == REPORTED_ONCE[1], f'{len(reported)} time(s)'))
    errors = [l for l in lines if '[ERROR]' in l and 'Jiangyu' in l]
    results.append(('no loader errors', not errors, errors[0].strip()[:160] if errors else 'none'))

    # The live templates, through the Studio bridge of the dev loader.
    try:
        dump = bridge('templates')['result']['types']
        by_key = {}
        for t in dump:
            for tpl in t['templates']:
                by_key[(t['typeName'], tpl['id'])] = {f['name']: f for f in tpl['fields']}
        for (type_name, template_id, field), expected in FINAL.items():
            fields = by_key.get((type_name, template_id))
            actual = None if fields is None else str(fields.get(field, {}).get('scalarValue'))
            results.append((f'live {type_name}:{template_id}.{field} = {expected}', actual == expected, f'actual {actual}'))
        perk = by_key.get(('PerkTemplate', 'harness.perk'))
        handlers = perk['EventHandlers'].get('elementSummaries', []) if perk else []
        ok = len(handlers) >= 2 and handlers[1].startswith('Buyout')
        results.append(('live PerkTemplate:harness.perk EventHandlers[1] is Buyout', ok, str(handlers)[:160]))
    except Exception as ex:  # noqa: BLE001
        results.append(('bridge read', False, f'{ex} (the game must still be running, with the dev loader)'))

    width = max(len(label) for label, _, _ in results)
    failed = 0
    for label, ok, detail in results:
        failed += 0 if ok else 1
        print(f"{'PASS' if ok else 'FAIL'}  {label.ljust(width)}  {detail}")
    print(f"\n{len(results) - failed}/{len(results)} checks passed")
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(main())

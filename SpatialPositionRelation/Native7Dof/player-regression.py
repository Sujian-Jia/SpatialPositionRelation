"""Exercise the actual Windows Player, compare its SPR/1 output to the original arm CSV."""
import csv
import json
import math
from pathlib import Path
import socket
import subprocess
import sys
import time

exe, config, baseline, evidence = [Path(arg).resolve() for arg in sys.argv[1:]]
evidence.mkdir(parents=True, exist_ok=True)
with baseline.open(encoding='utf-8-sig') as source:
    reference = {round(float(row['t']) * 50): row for row in csv.DictReader(source)}
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]
state = json.loads(config.read_text(encoding='utf-8-sig'))
state.update(protocol='SPR/1', type='state', seq=0, demoRunId=1, demoDocking=False, dynamicsSpeed=1)
state['orbit']['timeScale'] = 0
state['displaySensorIds'] = []
token = 'spr7-player-regression'
player = subprocess.Popen([str(exe), '-popupwindow', '-screen-width', '700', '-screen-height', '480',
                           '-spatialPort', str(port), '-spatialToken', token,
                           '-logFile', str(evidence / 'player.log')], cwd=exe.parent)

def coords(obj, keys='xyz'):
    return [obj[key] for key in keys]

def distance(a, b):
    return math.dist(coords(a), coords(b))

def polar(ref, keys):
    x, y, z = (float(ref[key]) for key in keys)
    return [x, y, -z]

def truth(frame):
    return {key: value for key, value in frame.items() if not key.startswith('applied')}

def rotation_error(a, b):
    left, right = coords(a, 'xyzw'), coords(b, 'xyzw')
    return min(math.dist(left, right), math.dist(left, [-component for component in right]))

maximum = {'toolPositionM': 0, 'toolQuaternion': 0, 'jointFramePositionM': 0, 'jointFrameQuaternion': 0}

def compare(frame):
    ref = reference[round(frame['timeSec'] * 50)]
    assert frame['mode'] == int(ref['mode'])
    assert max(abs(q - float(ref[f'q{i + 7}'])) for i, q in enumerate(frame['jointAnglesRad'])) < 1e-8
    for name, offset in [('stationCenter', 0), ('visitorCenter', 32)]:
        assert math.dist(coords(frame[name]['position']), polar(ref, [f'q{i + offset}' for i in range(3)])) < 2e-6
        expected = [float(ref[f'q{offset + i}']) * sign for i, sign in [(3, 1), (4, -1), (5, -1), (6, 1)]]
        assert math.dist(coords(frame[name]['rotation'], 'wxyz'), expected) < 2e-6
    for role in ('station', 'visitor'):
        assert math.dist(coords(frame[f'{role}Port']['position']), polar(ref, [f'ref_{role}_{axis}' for axis in 'xyz'])) < 2e-6
        assert distance(frame[f'{role}Port']['position'], frame[f'applied{role.title()}Port']) < 2e-6
        assert distance(frame[f'{role}Port']['position'], frame[f'applied{role.title()}Face']) < 2e-6
    assert math.dist(coords(frame['tool']['position']), polar(ref, [f'tool_{axis}' for axis in 'xyz'])) < 2e-6
    tool_position = distance(frame['tool']['position'], frame['appliedTool']['position'])
    tool_angle = rotation_error(frame['tool']['rotation'], frame['appliedTool']['rotation'])
    assert len(frame['jointFrames']) == len(frame['appliedJointFrames']) == 7
    joints = list(zip(frame['jointFrames'], frame['appliedJointFrames']))
    joint_position = max(distance(a['position'], b['position']) for a, b in joints)
    joint_angle = max(rotation_error(a['rotation'], b['rotation']) for a, b in joints)
    for field, value, limit in [('toolPositionM', tool_position, 2e-5), ('toolQuaternion', tool_angle, 2e-6),
                                ('jointFramePositionM', joint_position, 2e-5), ('jointFrameQuaternion', joint_angle, 2e-6)]:
        assert value < limit, (frame['timeSec'], field, value)
        maximum[field] = max(maximum[field], value)
    if frame['mode'] == 2:
        assert frame['portDistanceM'] < 1e-12
        assert distance(frame['stationPortVelocity'], frame['visitorPortVelocity']) < 1e-12
        assert distance(frame['stationCenterVelocity'], frame['visitorCenterVelocity']) > 1e-4

try:
    deadline = time.monotonic() + 75
    while True:
        try:
            conn = socket.create_connection(('127.0.0.1', port), timeout=1)
            conn.settimeout(25)
            break
        except OSError:
            if time.monotonic() >= deadline or player.poll() is not None:
                raise
            time.sleep(0.15)
    with conn, conn.makefile('rb') as reader, (evidence / 'tcp.jsonl').open('w', encoding='utf-8') as trace:
        def send(msg):
            data = json.dumps(msg, ensure_ascii=False)
            conn.sendall((data + '\n').encode())
            trace.write(json.dumps({'direction': 'out', 'data': msg}, ensure_ascii=False) + '\n')
            trace.flush()

        def receive(predicate, timeout=90):
            deadline = time.monotonic() + timeout
            for line in reader:
                msg = json.loads(line)
                trace.write(json.dumps({'direction': 'in', 'data': msg}, ensure_ascii=False) + '\n')
                trace.flush()
                assert msg['type'] != 'error', msg
                if msg['type'] == 'telemetry':
                    assert not msg.get('dynamicsError'), msg['dynamicsError']
                    compare(msg['dynamics'])
                if predicate(msg):
                    return msg
                assert time.monotonic() < deadline, 'TCP telemetry deadline'
            raise EOFError('Unity disconnected')

        assert receive(lambda msg: msg['type'] == 'ready')['instanceToken'] == token

        def apply(**fields):
            state.update(fields)
            state['seq'] += 1
            send(state)
            return report(lambda frame: True)

        def report(predicate):
            return receive(lambda msg: msg['type'] == 'telemetry' and msg['stateSeq'] == state['seq'] and predicate(msg['dynamics']))

        initial = apply()
        assert initial['chaserCraftId'] == 'SpaceStation' and initial['targetCraftId'] == 'CrewVehicle'
        assert initial['dynamics']['timeSec'] == 0 and len(initial['sensors']) == 6
        assert all(not sensor['trajectoryForecastAvailable'] for sensor in initial['sensors'])
        settings = apply(jointsDeg=[170] * 7, relativeDistanceM=900, attitude={'eulerDeg': [45] * 3},
                         trajectory=[{'tSec': 0, 'relativePositionM': [999, 0, 0]}],
                         manualSolarDiagnostic=True, manualSolarRole='both', solarArrayDeg=65)
        assert truth(settings['dynamics']) == truth(initial['dynamics'])
        assert len(settings['wings']) == 4 and all(wing['manual'] and wing['angleDeg'] == 65 for wing in settings['wings'])
        apply(dynamicsSpeed=4, demoDocking=True)
        moved = report(lambda frame: frame['timeSec'] >= 7)
        assert moved['dynamics']['mode'] == 3
        before = apply(demoDocking=False)['dynamics']
        assert truth(before) == truth(report(lambda frame: True)['dynamics']) == truth(report(lambda frame: True)['dynamics'])
        # Pick a lower speed near lock so the intervening phase is observable.
        apply(dynamicsSpeed=2, demoDocking=True)
        locked = report(lambda frame: frame['mode'] == 2)['dynamics']
        assert locked['portDistanceM'] < 1e-12
        apply(dynamicsSpeed=16)
        final = report(lambda frame: frame['timeSec'] == 80)['dynamics']
        assert all(abs(a - b) < 1e-8 for a, b in zip(final['eventTimesSec'], [5.102, 6.717, 52.261, 58.001, 58.002, 64.004]))
        reset = apply(demoRunId=2, demoDocking=False)['dynamics']
        assert truth(reset) == truth(initial['dynamics'])
        apply(demoDocking=True)
        replay = report(lambda frame: frame['timeSec'] == 80)['dynamics']
        assert truth(final) == truth(replay)
        send(dict(protocol='SPR/1', type='shutdown', instanceToken=token))
    assert player.wait(timeout=15) == 0
    (evidence / 'transform-errors.json').write_text(json.dumps(maximum, indent=2), encoding='utf-8')
    print('PASS 80s Windows Player twice, original CSV, port constraint, tool/face transforms, pause/reset/replay, six sensors and manual solar')
finally:
    if player.poll() is None:
        player.terminate()
        player.wait(timeout=10)



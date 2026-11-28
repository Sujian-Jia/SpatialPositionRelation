"""Exercise the actual Windows Player, compare its SPR/1 output to the original arm CSV."""
import argparse
import base64
import csv
import hashlib
from itertools import count
import json
import math
from pathlib import Path
import socket
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('exe', type=Path)
parser.add_argument('config', type=Path)
parser.add_argument('baseline', type=Path)
parser.add_argument('evidence', type=Path)
parser.add_argument('--no-presentation', action='store_true', help='Use a Player predating display commands')
parser.add_argument('--strict-blockers', action='store_true', help='Fail on any frozen blocker identity or order change')
parser.add_argument('--strict-pictures', action='store_true', help='Fail on any frozen sensor PNG byte change')
args = parser.parse_args()
exe, config, baseline, evidence = (value.resolve() for value in (args.exe, args.config, args.baseline, args.evidence))
control = args.no_presentation
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

keyframes = {}
finished_forecasts = {}
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
    connections = {"baseToFirstLinkM": distance(frame['basePivot'], frame['linkStarts'][0]),
                   "baseToHullSeatM": distance(frame['baseFoot'], frame['baseSeat']),
                   "fixtureToHullSeatM": distance(frame['grappleFoot'], frame['grappleSeat']),
                   "physicalGrappleToNativeM": distance(frame['grapple'], frame['appliedGrapple']),
                   "physicalGripperToNativeM": distance(frame['tool']['position'], frame['appliedGripper'])}
    next_starts = frame['linkStarts'][1:] + [frame['appliedGripper']]
    for index, (end, start) in enumerate(zip(frame['linkEnds'], next_starts), 1):
        connections[f'link{index}M'] = distance(end, start)
    for key, value in connections.items():
        assert value < 2e-5, (frame['timeSec'], key, value)
        maximum[key] = max(maximum.get(key, 0), value)
    residual = distance(frame['appliedGripper'], frame['appliedGrapple'])
    assert abs(residual - frame['toolDistanceM']) < 2e-6, (frame['timeSec'], residual, frame['toolDistanceM'])
    for label, time_sec in {'初态': 0, '抓捕': 6.72, '搬运': 25, '对接': 58.02, '分离': 64.02, '结束': 80}.items():
        if label not in keyframes or abs(frame['timeSec'] - time_sec) < abs(keyframes[label]['timeSec'] - time_sec):
            keyframes[label] = dict(timeSec=frame['timeSec'], mode=frame['mode'], physicalCaptureResidualM=residual,
                                    nativeCaptureResidualM=frame['toolDistanceM'], **connections)
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

        def receive(predicate, timeout=240):
            deadline = time.monotonic() + timeout
            for line in reader:
                msg = json.loads(line)
                trace.write(json.dumps({'direction': 'in', 'data': msg}, ensure_ascii=False) + '\n')
                trace.flush()
                assert msg['type'] != 'error', msg
                if msg['type'] == 'telemetry':
                    assert not msg.get('dynamicsError'), msg['dynamicsError']
                    compare(msg['dynamics'])
                    sensors = msg['sensors']
                    if sensors and all(sensor['trajectoryForecastComplete'] for sensor in sensors):
                        assert all(sensor['trajectoryForecastThroughSec'] == 80 for sensor in sensors)
                        finished_forecasts.setdefault(msg['demoRunId'], msg)
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

        def presentation(run, visible, angles=None):
            if control:
                return report(lambda frame: True)
            command = dict(protocol='SPR/1', type='presentation', demoRunId=run, showSunVector=visible)
            if angles is not None:
                command['previewJointDegrees'] = angles
            send(command)
            return report(lambda frame: True)

        sensor_ids = [sensor['id'] for sensor in initial['sensors']]
        assert len(sensor_ids) == len(set(sensor_ids)) == 6
        capture_sequences = count(8100)
        capture_checks = []

        def capture_sensors():
            frames = {}
            for sensor_id in sensor_ids:
                capture_seq = next(capture_sequences)
                send(dict(protocol='SPR/1', type='captureSensor', seq=capture_seq, sensorId=sensor_id))
                frame = receive(lambda msg: msg['type'] == 'sensorFrame' and msg['seq'] == capture_seq)
                assert frame['sensorId'] == sensor_id and frame.get('pngBase64'), (sensor_id, frame.get('error'))
                png = base64.b64decode(frame['pngBase64'], validate=True)
                assert png.startswith(b'\x89PNG\r\n\x1a\n')
                frames[sensor_id] = hashlib.sha256(png).hexdigest()
            return frames

        def check_captures(scenario, time_sec, before):
            after = capture_sensors()
            differences = [sensor_id for sensor_id in sensor_ids if before[sensor_id] != after[sensor_id]]
            capture_checks.append(dict(scenario=scenario, demoTimeSec=time_sec, before=before, after=after,
                                       differingSensorIds=differences))
            (evidence / 'sensor-picture-isolation.json').write_text(
                json.dumps(capture_checks, indent=2), encoding='utf-8')

        def compare_frozen(before, after, scenario):
            for field in ('dynamics', 'jointsDeg', 'wings', 'sunDirectionUnity', 'sunArrowOrigin', 'sunArrowTip',
                          'orbitPositionKm', 'earthShadow', 'showRays'):
                assert before[field] == after[field], (scenario, field)
            assert len(before['sensors']) == len(after['sensors']) == 6
            for left, right in zip(before['sensors'], after['sensors']):
                assert left.keys() == right.keys()
                for field in left:
                    if field != 'blockers':
                        assert left[field] == right[field], (scenario, left['id'], field)
                a = [part['partId'] for part in left['blockers']]
                b = [part['partId'] for part in right['blockers']]
                if a != b:
                    identity_differences.append(dict(scenario=scenario, time=before['demoTimeSec'],
                                                     sensor=left['id'], countBefore=len(a), countAfter=len(b),
                                                     removed=sorted(set(a) - set(b)), added=sorted(set(b) - set(a))))

        identity_differences = []
        baseline_pictures = capture_sensors()
        preview = presentation(1, True, [35, -15, 20, 50, 45, -80, 100])
        compare_frozen(initial, preview, 'initial-preview')
        check_captures('initial-preview-sun-shown', 0, baseline_pictures)
        hidden_sun = presentation(1, False, [55, -35, 70, 50, 45, -80, 100])
        compare_frozen(initial, hidden_sun, 'initial-sun-hidden')
        check_captures('initial-preview-sun-hidden', 0, baseline_pictures)
        settings = apply(jointsDeg=[170] * 7, relativeDistanceM=900, attitude={'eulerDeg': [45] * 3},
                         trajectory=[{'tSec': 0, 'relativePositionM': [999, 0, 0]}],
                         manualSolarDiagnostic=True, manualSolarRole='both', solarArrayDeg=65)
        assert truth(settings['dynamics']) == truth(initial['dynamics'])
        assert len(settings['wings']) == 4 and all(wing['manual'] and wing['angleDeg'] == 65 for wing in settings['wings'])
        apply(dynamicsSpeed=4, demoDocking=True)
        moved = report(lambda frame: frame['timeSec'] >= 7)
        assert moved['dynamics']['mode'] == 3
        apply(demoDocking=False)
        # Take the reference after a full telemetry interval with no new configuration.
        # The first pause packet follows ApplyState, which rebinds the sensor transforms.
        paused = report(lambda frame: True)
        def prediction(message):
            return [{key: value for key, value in sensor.items()
                     if key.startswith(('trajectoryForecast', 'futureOcclusion'))} for sensor in message['sensors']]
        paused_pictures = capture_sensors()
        paused_preview = presentation(1, True, [90] * 7)
        compare_frozen(paused, paused_preview, 'paused-preview')
        check_captures('paused-preview-command', paused['demoTimeSec'], paused_pictures)
        previous = paused_preview
        for _ in range(3):
            still = report(lambda frame: True)
            compare_frozen(previous, still, 'paused-no-command')
            previous = still
            assert truth(paused['dynamics']) == truth(still['dynamics'])
            assert prediction(paused) == prediction(still)
        # Pick a lower speed near lock so the intervening phase is observable.
        apply(dynamicsSpeed=2, demoDocking=True)
        locked = report(lambda frame: frame['mode'] == 2)['dynamics']
        assert locked['portDistanceM'] < 1e-12
        apply(dynamicsSpeed=16)
        final = report(lambda frame: frame['timeSec'] == 80)['dynamics']
        assert all(abs(a - b) < 1e-8 for a, b in zip(final['eventTimesSec'], [5.102, 6.717, 52.261, 58.001, 58.002, 64.004]))
        apply(demoDocking=False)
        stopped = report(lambda frame: True)
        for _ in range(12):
            still = report(lambda frame: True)
            compare_frozen(stopped, still, 'finished-before-preview-no-command')
            stopped = still
        completed_pictures = capture_sensors()
        completed_preview = presentation(1, False, [-90] * 7)
        compare_frozen(stopped, completed_preview, 'finished-preview')
        check_captures('completed-preview', 80, completed_pictures)
        previous = completed_preview
        for _ in range(12):
            still = report(lambda frame: True)
            compare_frozen(previous, still, 'finished-after-preview-no-command')
            previous = still
        reset = apply(demoRunId=2, demoDocking=False)
        reset_pictures = capture_sensors()
        stale = presentation(1, True, [170] * 7)
        compare_frozen(reset, stale, 'reset-stale')
        check_captures('reset-stale-command', 0, reset_pictures)
        assert truth(reset['dynamics']) == truth(initial['dynamics'])
        assert not any(sensor['trajectoryForecastAvailable'] for sensor in reset['sensors'])
        apply(demoDocking=True)
        replay = report(lambda frame: frame['timeSec'] == 80)['dynamics']
        assert truth(final) == truth(replay)
        assert {1, 2}.issubset(finished_forecasts), 'both runs must forecast to 80 s before playback finishes'
        for run in (1, 2):
            crds = finished_forecasts[run]['sensors'][0]
            assert crds['id'] == 'CRDS-PORT' and crds['futureOcclusion']
            assert abs(crds['futureOcclusionStartSec'] - 44.46) < .061, crds
            assert crds['futureOcclusionEndSec'] == 80
        assert finished_forecasts[1]['sensors'][0]['futureOcclusionStartSec'] == finished_forecasts[2]['sensors'][0]['futureOcclusionStartSec']
        # A single state can reset and resume in one frame, while old Unity scenes unload asynchronously.
        after_restart = apply(demoRunId=3, demoDocking=True, dynamicsSpeed=1)
        assert after_restart['dynamics']['timeSec'] < 2
        apply(demoDocking=False)
        state['sensors'][0]['horizontalFovDeg'] = 45
        changed = apply()
        assert all(sensor['trajectoryForecastThroughSec'] == 0 for sensor in changed['sensors'])
        send(dict(protocol='SPR/1', type='shutdown', instanceToken=token))
    assert player.wait(timeout=15) == 0
    (evidence / 'transform-errors.json').write_text(json.dumps(maximum, indent=2), encoding='utf-8')
    (evidence / 'keyframes.json').write_text(json.dumps(keyframes, indent=2, ensure_ascii=False), encoding='utf-8')
    forecasts = {run: dict(now=msg['demoTimeSec'], sensors=msg['sensors']) for run, msg in finished_forecasts.items()}
    (evidence / 'forecasts.json').write_text(json.dumps(forecasts, indent=2, ensure_ascii=False), encoding='utf-8')
    (evidence / 'blocker-identity-differences.json').write_text(
        json.dumps(identity_differences, indent=2, ensure_ascii=False), encoding='utf-8')
    source = 'no-presentation control' if control else 'presentation controls'
    print(f'PASS {source}: frozen native state, all non-blocker sensor fields and solar values')
    picture_differences = [(check['scenario'], sensor_id) for check in capture_checks
                           for sensor_id in check['differingSensorIds']]
    picture_comparisons = sum(len(check['before']) for check in capture_checks)
    print(f'Sensor PNG comparisons: {picture_comparisons}, byte differences: {len(picture_differences)}; '
          'see sensor-picture-isolation.json')
    print(f'Blocker identity/order differences: {len(identity_differences)}; see blocker-identity-differences.json')
    print('PASS 80s forecasts, reproducible CRDS prediction, pause, reset + resume, and paused FOV invalidation')
    print('PASS 80s Windows Player twice, original CSV, port constraint, tool/face transforms, pause/reset/replay, six sensors and manual solar')
    if args.strict_pictures:
        assert not picture_differences, ('Frozen sensor PNG differences', picture_differences)
    if args.strict_blockers:
        assert not identity_differences, 'Frozen blocker differences; see blocker-identity-differences.json'
finally:
    if player.poll() is None:
        player.terminate()
        player.wait(timeout=10)



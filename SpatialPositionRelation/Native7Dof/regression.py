from pathlib import Path
import csv
import ctypes as c
from itertools import zip_longest
import json
import math
import sys

build = Path(sys.argv[1]).resolve()
reference = Path(sys.argv[2]).resolve()

def rows(path):
    with path.open(encoding='utf-8-sig', newline='') as stream:
        for row in csv.DictReader(stream):
            yield {key: float(value) for key, value in row.items()}

def compare_cli(scenario, count):
    maximum = {}
    for index, (actual, expected) in enumerate(zip_longest(
            rows(build / f'{scenario}-overlay.csv'), rows(reference / f'results_{scenario}_composite.csv'))):
        assert actual and expected, (scenario, index, 'frame count')
        assert abs(actual['t'] - expected['t']) < 1e-9 and actual['mode'] == expected['mode'], (scenario, index, actual['mode'], expected['mode'])
        for field in actual:
            delta = abs(actual[field] - expected[field])
            assert math.isfinite(delta), (scenario, index, field)
            maximum[field] = max(maximum.get(field, 0), delta)
    assert index + 1 == count
    return dict(sorted(maximum.items(), key=lambda field: -field[1]))

class Frame(c.Structure):
    _fields_ = [('time', c.c_double), ('mode', c.c_double), ('q', c.c_double * 39), ('u', c.c_double * 37),
                ('station', c.c_double * 7), ('visitor', c.c_double * 7),
                ('station_vel', c.c_double * 3), ('visitor_vel', c.c_double * 3),
                ('joints', c.c_double * 49), ('tool', c.c_double * 7), ('grapple', c.c_double * 3),
                ('port_distance', c.c_double), ('center_distance', c.c_double),
                ('gap', c.c_double), ('tool_distance', c.c_double), ('events', c.c_double * 6), ('lock_request_setting', c.c_double)]

def compare_session():
    lib = c.CDLL(str(build / 'mingw/libSpatialNative7Dof.dll'))
    lib.Spr7_Create.restype = c.c_void_p
    lib.Spr7_Advance.argtypes = [c.c_void_p, c.c_int32]
    lib.Spr7_Read.argtypes = [c.c_void_p, c.POINTER(Frame), c.c_int32]
    lib.Spr7_Destroy.argtypes = [c.c_void_p]
    lib.Spr7_Destroy.restype = None
    assert lib.Spr7_Abi() == 1
    session = lib.Spr7_Create()
    assert session
    frame = Frame()
    maximum = dict(q=0., u=0., tool=0., ports=0., gap=0.)
    events = []
    mode_drift = []
    closure = dict(port_position=0., port_speed=0., center_speed_difference=0.)
    snapshots = {}
    try:
        for index, expected in enumerate(rows(reference / 'results_arm_composite.csv')):
            if index:
                # Variable batch sizes exercise paused / resumed scheduling at a sample boundary.
                steps = 0
                while steps < 20:
                    count = lib.Spr7_Advance(session, index % 29 + 1)
                    assert count > 0
                    steps += count
                assert steps == 20
            assert lib.Spr7_Read(session, c.byref(frame), c.sizeof(frame)) == 0
            assert abs(frame.time - expected['t']) < 1e-9
            if frame.mode != expected['mode']:
                mode_drift.append([index, frame.mode, expected['mode']])
            for field, sampled, ref in [
                ('q', frame.q, [expected[f'q{i}'] for i in range(39)]),
                ('u', frame.u, [expected[f'u{i}'] for i in range(37)]),
                ('tool', frame.tool[:3], [expected[f'tool_{axis}'] for axis in 'xyz']),
                ('ports', frame.station[:3] + frame.visitor[:3], [expected[f'ref_{craft}_{axis}'] for craft in ('station', 'visitor') for axis in 'xyz']),
                ('gap', [frame.gap], [expected['gap']]),
            ]:
                delta = max(abs(x - y) for x, y in zip(sampled, ref))
                assert math.isfinite(delta)
                maximum[field] = max(maximum[field], delta)
            if frame.mode == 2:
                closure['port_position'] = max(closure['port_position'], math.dist(frame.station[:3], frame.visitor[:3]))
                closure['port_speed'] = max(closure['port_speed'], math.dist(frame.station_vel, frame.visitor_vel))
                closure['center_speed_difference'] = max(closure['center_speed_difference'], math.dist(frame.u[:3], frame.u[31:34]))
            if list(frame.events) != events:
                events = list(frame.events)
                snapshots[str(frame.time)] = dict(events=events, mode=frame.mode, tool=list(frame.tool), joints=list(frame.q[7:14]), port_distance=frame.port_distance)
        assert index + 1 == 4001 and lib.Spr7_Advance(session, 20) == 0
    finally:
        lib.Spr7_Destroy(session)
    return dict(frames=index+1, maximum=maximum, events=events, mode_drift=mode_drift, closure=closure, snapshots=snapshots)

report = {'session': compare_session()}
for scenario, count in [('arm', 4001), ('station', 1801)]:
    report[scenario] = compare_cli(scenario, count)
(build / 'regression.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
assert not report['session']['mode_drift'], report['session']['mode_drift']
assert all(delta < 1e-8 for delta in report['session']['maximum'].values())
assert all(abs(a - b) < 1e-8 for a, b in zip(report['session']['events'],
    [5.102, 6.717, 52.261, 58.001, 58.002, 64.004]))
closure = report['session']['closure']
assert closure['port_position'] < 1e-12 and closure['port_speed'] < 1e-12
assert abs(closure['center_speed_difference'] - 0.00906495495023076) < 1e-8
assert all(delta < 1e-7 for scenario in ('arm', 'station') for delta in report[scenario].values())
print(json.dumps({key: (value if key == 'session' else dict(list(value.items())[:8])) for key, value in report.items() if key != 'session'} | {key: value for key, value in report['session'].items() if key != 'snapshots'}, indent=2))



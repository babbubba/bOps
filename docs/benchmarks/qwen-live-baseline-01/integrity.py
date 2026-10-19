"""Post-campaign integrity check and per-run artefact manifest. Usage: python -I integrity.py <campaign root>"""
import glob, hashlib, json, os, sys

root = sys.argv[1]
here = os.path.dirname(os.path.abspath(__file__))
repo = os.path.abspath(os.path.join(here, '..', '..', '..'))
bindir = os.path.join(repo, 'tests', 'bOps.QwenLiveBaseline', 'bin', 'Debug', 'net10.0')


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest().upper()


cfg = json.load(open(os.path.join(root, 'campaign-config.json'), encoding='utf-8'))
print('fixtures changed since recording:', [n for n, h in cfg['fixtures'].items() if sha(os.path.join(here, 'fixtures', n)) != h])
print('binaries changed:', [n for n, h in cfg['binaries'].items() if sha(os.path.join(bindir, n)) != h])
print('shared inputs changed:', [n for n, h in cfg['shared'].items() if sha(os.path.join(root, 'shared', n)) != h])
prior = os.path.join(os.path.dirname(root), 'release-validation-01', 'live', 'run', 'tasks.db')
print('previous campaign tasks.db sha256 =', sha(prior), '(fixture provenance expects 38D61286A4CF...)')
manifest = {}
for rd in sorted(glob.glob(os.path.join(root, 'runs', '*'))):
    files = [p for p in sorted(glob.glob(rd + '/**/*', recursive=True)) if os.path.isfile(p) and 'snapshots' not in p and not p.endswith(('-wal', '-shm'))]
    manifest[os.path.basename(rd)] = {os.path.relpath(p, rd).replace(os.sep, '/'): sha(p) for p in files}
json.dump(manifest, open(os.path.join(root, 'runs-manifest.json'), 'w'), indent=1)
print({k: len(v) for k, v in manifest.items()})

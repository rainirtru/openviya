#!/usr/bin/env python3
"""Apply this compiled-code hotfix only to the verified original deployment, keeping its libraries."""
import hashlib
import json
import pathlib
import shutil
import subprocess
import zipfile
import sys
import re

root = pathlib.Path('/opt/openviya-control')
jar = root/'app.jar'
label = sys.argv[1] if len(sys.argv) > 1 else 'oidc-gzip'
assert re.fullmatch('[a-z0-9-]{1,40}',label), 'Invalid update label'
bundle = root/(label+'-update.zip')
marker = root/('private/'+label+'-update.json')
assert not marker.exists(), 'Hotfix already applied; do not overwrite its rollback artifact'
with zipfile.ZipFile(bundle) as update, zipfile.ZipFile(jar) as original:
    manifest = json.loads(update.read('bundle-manifest.json'))
    assert hashlib.sha256(jar.read_bytes()).hexdigest() == manifest['base_sha256'], 'Unexpected base artifact'
    original_libs = {name:hashlib.sha256(original.read(name)).hexdigest()
                     for name in original.namelist() if name.startswith('BOOT-INF/lib/') and not name.endswith('/')}
    assert original_libs == manifest['libraries'], 'Dependency changes require a normal full deployment'
    names = [name for name in update.namelist() if name != 'bundle-manifest.json']
    assert names and all(name.startswith('BOOT-INF/classes/') and '..' not in pathlib.PurePosixPath(name).parts for name in names)
    for name in names:
        if name.startswith('BOOT-INF/classes/db/migration/'):
            assert update.read(name) == original.read(name), 'Migration changes are outside this code-only hotfix'
    subprocess.run(['systemctl','start','openviya-backup.service'], check=True)
    subprocess.run(['docker','tag','openviya-backend:control','openviya-backend:before-'+label], check=True)
    shutil.copy2(jar,root/('private/app-before-'+label+'.jar'))
    temporary = root/'app.jar.updating'
    with zipfile.ZipFile(temporary,'w') as target:
        for info in original.infolist():
            if info.filename.startswith('BOOT-INF/classes/') and not info.is_dir():
                continue
            target.writestr(info,original.read(info.filename))
        for info in update.infolist():
            if info.filename in names:
                target.writestr(info,update.read(info.filename))
with zipfile.ZipFile(temporary) as verified, zipfile.ZipFile(bundle) as update:
    assert verified.testzip() is None, 'Updated JAR integrity failed'
    assert all(verified.read(name) == update.read(name) for name in names), 'Application class readback failed'
temporary.replace(jar)
checksum = hashlib.sha256(jar.read_bytes()).hexdigest()
(root/'app.jar.sha256').write_text(checksum+'  app.jar\n')
marker.write_text(json.dumps({'base_sha256':manifest['base_sha256'],'deployed_sha256':checksum,
    'bundle_sha256':hashlib.sha256(bundle.read_bytes()).hexdigest()},indent=2)+'\n')
marker.chmod(0o600)
print('Updated compiled application classes; libraries and migrations unchanged; rollback image and JAR retained')

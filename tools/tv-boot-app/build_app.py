"""Build/sign a separate health-only WGT. No television or deployment operations."""
from __future__ import annotations
import base64
import argparse
import csv
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import uuid
import xml.etree.ElementTree as ET
import zipfile

HERE = Path(__file__).resolve().parent
SDK = HERE.parent / 'tizen-sdk-minimal' / 'data'
PRIVATE = Path(os.environ['LOCALAPPDATA']) / 'TVCITATION' / 'Signing'
JAVA = SDK / 'jdk' / 'bin' / 'java.exe'


def private_dir(path):
    path.mkdir(parents=True, exist_ok=True)
    sid = next(csv.reader(subprocess.check_output(['whoami', '/user', '/fo', 'csv', '/nh'], text=True).strip().splitlines()))[-1]
    subprocess.run(['icacls', str(path), '/inheritance:r', '/grant:r', f'*{sid}:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F'], capture_output=True, check=True)


class Blob(ctypes.Structure):
    _fields_ = [('size', wintypes.DWORD), ('data', ctypes.POINTER(ctypes.c_ubyte))]


def dpapi(data, protect):
    buffer = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    source = Blob(len(data), buffer)
    target = Blob()
    crypt = ctypes.WinDLL('crypt32', use_last_error=True)
    free = ctypes.WinDLL('kernel32').LocalFree
    free.argtypes = [ctypes.c_void_p]
    free.restype = ctypes.c_void_p
    if protect:
        fn = crypt.CryptProtectData
        fn.argtypes = [ctypes.POINTER(Blob), wintypes.LPCWSTR, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
        success = fn(ctypes.byref(source), 'TVCITATION signing', None, None, None, 1, ctypes.byref(target))
    else:
        fn = crypt.CryptUnprotectData
        fn.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
        success = fn(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(target))
    if not success:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return ctypes.string_at(target.data, target.size)
    finally:
        free(target.data)


def cli(arguments, log_name, input_bytes=None):
    library = SDK / 'tools' / 'ide' / 'lib-ncli'
    configuration = SDK / 'tools' / 'ide' / 'conf-ncli'
    classpath = os.pathsep.join([str(configuration), str(library / '*'), str(SDK / 'library' / 'sdk-utils-core.jar')])
    command = [str(JAVA), '-Dlog4j.configurationFile=' + str(configuration / 'log4j-progress.xml'),
               '-Djava.library.path=' + str(library / 'spawner'), '-cp', classpath,
               'org.tizen.ncli.ide.shell.Main', *arguments, '--current-workspace-path', str(HERE)]
    completed = subprocess.run(command, cwd=SDK / 'tools' / 'ide' / 'bin', capture_output=True,
                               input=input_bytes, timeout=100, creationflags=subprocess.CREATE_NO_WINDOW)
    # CLI may show local paths; keep its full diagnostics only in the private folder.
    (PRIVATE / (log_name + '.log')).write_bytes(completed.stdout + completed.stderr)
    if completed.returncode:
        raise RuntimeError('Official CLI failed at ' + log_name + '; details retained privately')
    return completed.stdout.decode('utf-8', errors='replace')


def build(distributor, publish_latest=False):
    private_dir(PRIVATE)
    sdk_data = PRIVATE / 'sdk-data'
    sdk_data.mkdir(exist_ok=True)
    (sdk_data / 'cli' / 'logs').mkdir(parents=True, exist_ok=True)
    (SDK / 'sdk.info').write_text('TIZEN_SDK_INSTALLED_PATH=' + SDK.as_posix() + '\nTIZEN_SDK_DATA_PATH=' + sdk_data.as_posix() + '\nJDK_PATH=' + (SDK / 'jdk').as_posix() + '\n', encoding='utf-8')
    secret_file = PRIVATE / 'author-password.dpapi'
    author = PRIVATE / 'TVCITATION-author.p12'
    if secret_file.exists():
        password = dpapi(secret_file.read_bytes(), False).decode('ascii')
    else:
        if author.exists():
            raise RuntimeError('Author exists without its protected password; refusing replacement')
        password = secrets.token_hex(24)
        secret_file.write_bytes(dpapi(password.encode('ascii'), True))
    if not author.exists():
        cli(['certificate', '-a', 'TVCITATION', '-f', 'TVCITATION-author', '-p', password,
             '-n', 'TVCITATION Local Development', '--', str(PRIVATE)], 'generate-author')
    if not author.is_file():
        raise RuntimeError('CLI did not produce the author certificate')
    profiles = PRIVATE / 'profiles.xml'
    if not profiles.exists():
        shutil.copyfile(sdk_data / 'profile' / 'profiles.xml', profiles)
    existing_profiles = ET.parse(profiles)
    if not any(profile.attrib.get('name') == 'TVCITATION' for profile in existing_profiles.getroot().iter('profile')):
        cli(['security-profiles', 'add', '-n', 'TVCITATION', '-a', str(author), '-p', password,
             '--', str(profiles)], 'add-profile')
    if not profiles.exists():
        raise RuntimeError('CLI did not produce its signing profile')
    # Windows SDK's own WinCrypt creates user-protected .pwd files. This avoids
    # ConsolePrompter requiring a real Java console and avoids plaintext storage.
    crypt_tool = SDK / 'tools' / 'certificate-encryptor' / 'wincrypt.exe'
    author_pwd = PRIVATE / 'TVCITATION-author.pwd'
    distributor_pwd = PRIVATE / 'sdk-distributor.pwd'
    for value, target in [(password, author_pwd), ('tizenpkcs12passfordsigner', distributor_pwd)]:
        completed = subprocess.run([str(crypt_tool), '--encrypt', value, str(target)], capture_output=True,
                                   timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
        # This SDK helper returns 1 even on a successful encryption. Verify the
        # actual protected file with its own decrypt round trip, not exit code.
        verified = subprocess.run([str(crypt_tool), '--decrypt', str(target)], capture_output=True,
                                  timeout=15, creationflags=subprocess.CREATE_NO_WINDOW) if target.is_file() else None
        expected = ('PASSWORD:' + value).encode('ascii')
        if completed.returncode not in (0, 1) or verified is None or verified.stdout.strip() != expected:
            raise RuntimeError('Official SDK password protection failed')
    tree = ET.parse(profiles)
    for profile in tree.getroot().iter('profile'):
        if profile.attrib.get('name') != 'TVCITATION':
            continue
        for item in profile.iter('profileitem'):
            if item.attrib.get('distributor') == '0':
                item.set('password', author_pwd.as_posix())
            elif item.attrib.get('distributor') == '1':
                item.set('password', distributor_pwd.as_posix())
                certificate_directory = SDK / 'tools' / 'certificate-generator' / 'certificates' / 'distributor'
                suffix = '-new' if distributor == 'current' else ''
                item.set('key', (certificate_directory / ('tizen-distributor-signer' + suffix + '.p12')).as_posix())
                item.set('ca', (certificate_directory / ('tizen-distributor-ca' + suffix + '.cer')).as_posix())
    tree.write(profiles, encoding='utf-8', xml_declaration=True)
    cli(['cli-config', 'default.profiles.path=' + str(profiles)], 'configure-profile')
    run_id = str(uuid.uuid4())
    output = PRIVATE / 'builds' / run_id
    private_dir(output)
    widget = output / 'widget'
    (widget / 'service').mkdir(parents=True)
    for name in ['config.xml', 'index.html', 'ui.js']:
        shutil.copyfile(HERE / 'app' / name, widget / name)
    config = dict(token=secrets.token_urlsafe(32), instanceId=run_id, tvAddress='192.168.1.5', pcAddress='192.168.1.75', port=18767,
                  packageId='TVCitBoot1', serviceId='TVCitBoot1.Service')
    template = (HERE / 'app' / 'service' / 'service.js.in').read_text(encoding='utf-8')
    if template.count('__TVCITATION_CONFIG__') != 1:
        raise RuntimeError('Unexpected health template')
    (widget / 'service' / 'service.js').write_text(template.replace('__TVCITATION_CONFIG__', json.dumps(config, separators=(',', ':'))), encoding='utf-8')
    (output / 'client-private.json').write_text(json.dumps(config), encoding='utf-8')
    subprocess.run([r'C:\Program Files\nodejs\node.exe', '--check', str(widget / 'service' / 'service.js')], check=True, capture_output=True)
    cli(['package', '-t', 'wgt', '-s', 'TVCITATION', '--', str(widget)], 'package-' + run_id)
    matches = list(widget.glob('*.wgt'))
    if len(matches) != 1:
        raise RuntimeError('Expected one signed WGT')
    target = output / 'TVCITATION-Bridge-0.1.0.wgt'
    shutil.copyfile(matches[0], target)
    verification = output / 'verify'
    verification.mkdir()
    with zipfile.ZipFile(target) as archive:
        for entry in archive.infolist():
            if not (verification / entry.filename).resolve().is_relative_to(verification.resolve()):
                raise RuntimeError('Package contains an escaping path')
        archive.extractall(verification)
    classes = PRIVATE / 'verification-classes'
    classes.mkdir(exist_ok=True)
    subprocess.run([str(SDK / 'jdk' / 'bin' / 'javac.exe'), '-d', str(classes), str(HERE / 'VerifyWgt.java')],
                   check=True, capture_output=True, timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    validation = subprocess.run([str(JAVA), '-cp', str(classes), 'VerifyWgt', str(verification)],
                                capture_output=True, text=True, timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    if validation.returncode:
        (output / 'signature-verification-error.log').write_text(validation.stderr, encoding='utf-8')
        raise RuntimeError('Offline XML signature verification failed')
    signature_verification = json.loads(validation.stdout)
    digest_report = []
    with zipfile.ZipFile(target) as archive:
        for signature in ['author-signature.xml', 'signature1.xml']:
            root = ET.fromstring(archive.read(signature))
            ns = {'ds': 'http://www.w3.org/2000/09/xmldsig#'}
            certs = [base64.b64decode(x.text) for x in root.findall('.//ds:X509Certificate', ns)]
            digest_report.append(dict(file=signature, x509Sha256=[hashlib.sha256(x).hexdigest() for x in certs]))
    report = dict(schemaVersion=1, packageId='TVCitBoot1', appId='TVCitBoot1.Bridge', serviceId='TVCitBoot1.Service',
                  version='0.1.0', wgt=str(target), wgtSha256=hashlib.file_digest(target.open('rb'), 'sha256').hexdigest(),
                  clientConfig=str(output / 'client-private.json'), signatures=digest_report, signatureVerification=signature_verification,
                  tvOperations=False, distributorVariant=distributor,
                  distributor='Official Tizen SDK test distributor; no Samsung-account certificate requested',
                  tvAccepted=None, bootResidentVerified=False, commandExecutionEnabled=False, inputEnabled=False)
    (output / 'build-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (HERE / ('build-report-' + run_id + '.json')).write_text(json.dumps(report, indent=2), encoding='utf-8')
    if publish_latest:
        (PRIVATE / 'latest-build.json').write_text(json.dumps({'output': str(output), 'report': str(output / 'build-report.json')}), encoding='utf-8')
        (HERE / 'latest-build-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--distributor', choices=['current', 'legacy'], required=True)
    parser.add_argument('--publish-latest', action='store_true', help='Explicitly replace latest pointers only after coordination')
    args = parser.parse_args()
    try:
        print(json.dumps(build(args.distributor, args.publish_latest)))
    except Exception as error:
        print(json.dumps({'ok': False, 'errorType': type(error).__name__, 'message': str(error) if isinstance(error, RuntimeError) else 'Build failed; credentials were not printed', 'tvOperations': False}))
        raise SystemExit(1)

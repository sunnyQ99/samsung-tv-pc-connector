"""Build only: independent offline TV pointer test, own author and accepted legacy chain."""
from pathlib import Path
import base64
import importlib.util
import json
import re
import shutil
import subprocess
import uuid
import xml.etree.ElementTree as ET
import zipfile

HERE = Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('sign',HERE.parent/'tv-boot-app/build_app.py')
sign=importlib.util.module_from_spec(spec);spec.loader.exec_module(sign)
import hashlib
sha=lambda data:hashlib.sha256(data).hexdigest()


def build():
    application=ET.parse(HERE/'app/config.xml').getroot().find('{http://tizen.org/ns/widgets}application')
    package_id=application.get('package')
    if not re.fullmatch(r'[A-Za-z0-9]{10}',package_id) or package_id!='TVCPtrT001' or application.get('id')!=package_id+'.Probe':
        raise ValueError('Tizen web package ID must be the fixed ten alphanumeric characters')
    build_id=str(uuid.uuid4())
    output=sign.PRIVATE/'pointer-builds'/build_id
    sign.private_dir(output)
    widget=output/'widget'
    shutil.copytree(HERE/'app',widget)
    js=(widget/'probe.js').read_text(encoding='utf-8')
    if js.count('__BUILD_ID__') != 1: raise ValueError('Unexpected build marker')
    (widget/'probe.js').write_text(js.replace('__BUILD_ID__',build_id),encoding='utf-8')
    subprocess.run([r'C:\Program Files\nodejs\node.exe','--check',str(widget/'probe.js')],capture_output=True,check=True)
    profiles=sign.PRIVATE/'profiles.xml'
    original=profiles.read_bytes()
    tree=ET.fromstring(original)
    matches=[p for p in tree.iter('profile') if p.get('name')=='TVCITATION']
    if len(matches)!=1: raise ValueError('Own signing profile missing')
    certdir=sign.SDK/'tools/certificate-generator/certificates/distributor'
    for item in matches[0].iter('profileitem'):
        if item.get('distributor')=='1':
            item.set('key',(certdir/'tizen-distributor-signer.p12').as_posix())
            item.set('ca',(certdir/'tizen-distributor-ca.cer').as_posix())
    try:
        profiles.write_bytes(ET.tostring(tree,encoding='utf-8',xml_declaration=True))
        sign.cli(['package','-t','wgt','-s','TVCITATION','--',str(widget)],'pointer-package-'+build_id)
    finally: profiles.write_bytes(original)
    wgts=list(widget.glob('*.wgt'))
    if len(wgts)!=1: raise ValueError('Expected one package')
    target=output/'TVCITATION-PointerTest-0.1.0.wgt'
    shutil.copyfile(wgts[0],target)
    verify=output/'verify';verify.mkdir()
    signatures=[]
    with zipfile.ZipFile(target) as z:
        z.extractall(verify)
        ns={'d':'http://www.w3.org/2000/09/xmldsig#'}
        for name,expected in [('author-signature.xml','19d2dc4e7e44d83d63a1c22ca1b3c0bc1019375ea07f20c7525793830a6ba446'),('signature1.xml','cdd81c11422bc3e78288150af2e70787a3373be548f0bcf99ac8a5bd166b9008')]:
            certs=[sha(base64.b64decode(x.text)) for x in ET.fromstring(z.read(name)).findall('.//d:X509Certificate',ns)]
            if certs[0]!=expected: raise ValueError('Unexpected signing identity')
            signatures.append(dict(file=name,certificateSha256=certs))
    result=subprocess.run([str(sign.JAVA),'-cp',str(sign.PRIVATE/'verification-classes'),'VerifyWgt',str(verify)],capture_output=True,text=True,check=True,timeout=30)
    report=dict(buildId=build_id,packageId=package_id,appId=package_id+'.Probe',packageIdLength=len(package_id),wgt=str(target),wgtSha256=sha(target.read_bytes()),bytes=target.stat().st_size,
                signatures=signatures,signatureVerification=json.loads(result.stdout),pointingDeviceSupport='enable',networkPrivileges=False,services=[],onBoot=False,rootUsed=False,
                maxInputEvents=100,onlyTrustedEventsLogged=True,sourceSha256={p.name:sha(p.read_bytes()) for p in (HERE/'app').iterdir()},
                tvOperations=False,installed=False,ownBootPackageChanged=False,signingProfileRestored=profiles.read_bytes()==original)
    (output/'build-report.json').write_text(json.dumps(report,indent=2))
    (HERE/('build-report-'+build_id+'.json')).write_text(json.dumps(report,indent=2))
    return report


if __name__=='__main__':
    try:print(json.dumps(build()))
    except Exception as e:
        print(json.dumps(dict(ok=False,errorType=type(e).__name__,message='Offline pointer package build failed; no secrets printed.')))
        raise SystemExit(1)

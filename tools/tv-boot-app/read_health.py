"""Read only an explicitly selected private build's authenticated TV health endpoint."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import urllib.request
import urllib.error

PRIVATE_BUILDS = Path(os.environ['LOCALAPPDATA']) / 'TVCITATION' / 'Signing' / 'builds'


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def read_health(build_dir):
    directory = Path(build_dir).resolve(strict=True)
    if not directory.is_relative_to(PRIVATE_BUILDS.resolve()) or directory.parent != PRIVATE_BUILDS.resolve():
        raise ValueError('Expected one exact private build directory')
    config = json.loads((directory / 'client-private.json').read_text(encoding='utf-8'))
    if (config.get('tvAddress'), config.get('pcAddress'), config.get('port'), config.get('packageId'), config.get('serviceId')) != (
        '192.168.1.5', '192.168.1.75', 18767, 'TVCitBoot1', 'TVCitBoot1.Service'):
        raise ValueError('Unexpected fixed health configuration')
    request = urllib.request.Request('http://192.168.1.5:18767/health', headers={
        'Authorization': 'Bearer ' + config['token'], 'Host': '192.168.1.5:18767', 'Connection': 'close'}, method='GET')
    # Direct LAN request; never forward the private Bearer through an HTTP proxy.
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with client.open(request, timeout=3) as response:
        body = response.read(16385)
        if len(body) > 16384:
            raise ValueError('Oversized health response')
        health = json.loads(body)
    if health.get('instanceId') != config['instanceId'] or health.get('service') != 'TVCitBoot1.Service':
        raise ValueError('Response does not match the explicitly selected build')
    if health.get('healthOnly') is not True or health.get('commandExecutionEnabled') is not False or health.get('inputEnabled') is not False:
        raise ValueError('Response does not match the health-only contract')
    safe = {name: health.get(name) for name in ['schemaVersion', 'service', 'addon', 'instanceId', 'healthOnly',
            'commandExecutionEnabled', 'inputEnabled', 'bootResidentVerified', 'process', 'bootId',
            'addonUptimeSeconds', 'capabilities', 'stages']}
    if config['token'] in json.dumps(safe):
        raise ValueError('Unexpected sensitive value in response')
    return dict(ok=True, authenticatedHealth=True, bootStartupVerified=False, health=safe)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-dir', required=True, type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    try:
        result = read_health(args.build_dir)
    except urllib.error.HTTPError as error:
        result = dict(ok=False, errorType='HTTPError', httpStatus=error.code, authenticatedHealth=False)
    except Exception as error:
        result = dict(ok=False, errorType=type(error).__name__, authenticatedHealth=False,
                      message='Health read failed; no request headers or credentials were logged')
    if args.output:
        args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))
    raise SystemExit(0 if result['ok'] else 1)

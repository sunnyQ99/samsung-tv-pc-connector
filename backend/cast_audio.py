"""Synchronous, local-only coordination of the owned Sunshine audio lifecycle.

The caller initializes the restricted Control directory and serializes TV launch
with prepare()/stop(). Closing the control GUI must not call stop(). No function
in this module contacts the TV. Importing or constructing this class changes no
system settings and creates no files.
"""
from __future__ import annotations

import base64
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.request
import uuid
import xml.etree.ElementTree as ET


class _AudioError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


class CastAudioCoordinator:
    """One coordinator per backend. Public methods always return JSON-safe dicts."""

    _operation_lock = threading.RLock()

    def __init__(self, root: Path | str | None = None,
                 private_dir: Path | str | None = None, pwsh: str | None = None):
        self.root = Path(root or Path(__file__).resolve().parent.parent).resolve()
        self.private = Path(private_dir or Path(os.environ['LOCALAPPDATA']) /
                            'TVCITATION' / 'Control').resolve()
        self.receipt_path = self.private / 'audio-session.json'
        self.cast_dir = self.root / 'desktop-cast'
        self.config_path = self.cast_dir / 'sunshine.conf'
        self.host_exe = self.root / 'tools' / 'cast' / 'sunshine' / 'Sunshine' / 'sunshine.exe'
        self.pwsh = pwsh or shutil.which('pwsh.exe') or shutil.which('pwsh') or 'pwsh'
        self._busy = False

    @staticmethod
    def _utc() -> str:
        return datetime.now(timezone.utc).isoformat()

    @staticmethod
    def _quote(value: object) -> str:
        return "'" + str(value).replace("'", "''") + "'"

    def _powershell(self, command: str, timeout: float | None = 90) -> dict:
        encoded = base64.b64encode(command.encode('utf-16-le')).decode('ascii')
        try:
            # Start-Process can leave inherited pipe handles in the new host after
            # PowerShell exits. Files let us read the completed caller's result
            # without waiting for the long-running Sunshine process to close EOF.
            with tempfile.TemporaryFile(dir=self.private) as output, tempfile.TemporaryFile(dir=self.private) as errors:
                completed = subprocess.run(
                    [self.pwsh, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                     '-EncodedCommand', encoded], stdout=output, stderr=errors, timeout=timeout,
                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
                output.seek(0)
                stdout = output.read().decode('utf-8', 'replace')
        except subprocess.TimeoutExpired:
            raise _AudioError('scriptTimeout', 'Local audio operation timed out; check the restore state.') from None
        except OSError:
            raise _AudioError('powershellUnavailable', 'Could not start 64-bit PowerShell 7.') from None
        # Never include subprocess output in an exception: API helpers handle credentials.
        if completed.returncode:
            raise _AudioError('scriptFailed', 'The local audio helper failed; casting was not continued.')
        try:
            result = json.loads(stdout.lstrip('\ufeff').strip())
        except (ValueError, TypeError):
            raise _AudioError('invalidScriptResult', 'The audio helper returned no valid result.') from None
        if not isinstance(result, dict):
            raise _AudioError('invalidScriptResult', 'The audio helper returned a malformed result.')
        return result

    def _script(self, name: str, params: dict | None = None, *, object_result=False,
                timeout: float | None = 90) -> dict:
        parts = ['&', self._quote(self.cast_dir / name)]
        for key, value in (params or {}).items():
            if not re.fullmatch(r'[A-Za-z][A-Za-z0-9]*', key):
                raise _AudioError('invalidParameter', 'Invalid audio parameter name.')
            if isinstance(value, bool):
                if value:
                    parts.append('-' + key)
            elif value is not None:
                parts.extend(('-' + key, self._quote(value)))
        command = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + ' '.join(parts)
        if object_result:
            command += ' | ConvertTo-Json -Depth 12 -Compress'
        return self._powershell(command, timeout)

    @staticmethod
    def _require(result: dict, accepted: tuple[str, ...], code: str, message: str) -> dict:
        if result.get('status', result.get('Status')) not in accepted:
            raise _AudioError(code, message)
        return result

    def _processes(self) -> dict:
        command = (
            "$ErrorActionPreference='Stop'; "
            f"$ownedExe={self._quote(self.host_exe)}; $ownedConfig={self._quote(self.config_path)}; "
            "$all=@(Get-CimInstance Win32_Process -Filter \"Name='sunshine.exe'\"); "
            "$owned=@($all | Where-Object { $_.ExecutablePath -eq $ownedExe -and "
            "$null -ne $_.CommandLine -and $_.CommandLine -match ('(?:^|\\s)\"?' + "
            "[regex]::Escape($ownedConfig) + '\"?(?=\\s|$)') }); "
            "@{owned=@($owned | ForEach-Object { [int]$_.ProcessId }); otherCount=($all.Count-$owned.Count)} | ConvertTo-Json -Compress"
        )
        return self._powershell(command, 15)

    def _server_state(self) -> dict:
        try:
            # Explicitly bypass proxy settings; this probe is local and carries no credential.
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            with opener.open('http://127.0.0.1:47989/serverinfo', timeout=1) as response:
                document = ET.fromstring(response.read(128 * 1024))
            game = document.findtext('currentgame')
            if game is None or not game.isdigit():
                return {'known': False, 'reachable': True, 'active': False}
            return {'known': True, 'reachable': True, 'active': int(game) != 0,
                    'appId': int(game)}
        except Exception:
            return {'known': False, 'reachable': False, 'active': False}

    def _host_state(self) -> dict:
        processes = self._processes()
        owned = bool(processes.get('owned'))
        state = self._server_state() if owned else {'known': True, 'reachable': False, 'active': False}
        return {**state, 'owned': owned, 'otherCount': int(processes.get('otherCount', 0))}

    def _paths(self, session: dict) -> dict[str, Path]:
        session_id = session.get('id')
        if not isinstance(session_id, str) or not re.fullmatch(r'[a-f0-9]{32}', session_id):
            raise _AudioError('invalidReceipt', 'The audio restore receipt is invalid; the original file was kept.')
        prefix = self.private / ('audio-' + session_id)
        return {'defaults': Path(str(prefix) + '-defaults.json'),
                'config': Path(str(prefix) + '-config.json'),
                'install': Path(str(prefix) + '-install-result.json')}

    def _read_session(self) -> dict | None:
        if not self.receipt_path.exists():
            return None
        try:
            if self.receipt_path.stat().st_size > 512 * 1024:
                raise ValueError()
            session = json.loads(self.receipt_path.read_text(encoding='utf-8-sig'))
            if (session.get('schemaVersion') != 1 or session.get('kind') != 'castAudioSession'
                    or not isinstance(session.get('tvOnlyAudio'), bool)
                    or session.get('phase') not in ('preparing', 'prepared', 'stopping',
                                                   'recoveryNeeded', 'completed')):
                raise ValueError()
            self._paths(session)
            return session
        except _AudioError:
            raise
        except Exception:
            raise _AudioError('invalidReceipt', 'The audio restore receipt could not be read; the original file was kept.') from None

    def _write_session(self, session: dict) -> None:
        if not self.private.is_dir():
            raise _AudioError('privateDirectoryMissing', 'The restricted local control directory is not initialized yet.')
        session['updatedUtc'] = self._utc()
        handle, temporary = tempfile.mkstemp(prefix='audio-session-', suffix='.tmp', dir=self.private)
        try:
            with os.fdopen(handle, 'w', encoding='utf-8') as stream:
                json.dump(session, stream, ensure_ascii=False, indent=2)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.receipt_path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def _stop_host(self, require_idle: bool = False) -> dict:
        if self._processes().get('otherCount'):
            raise _AudioError('otherSunshine', 'Another Sunshine instance is running; it was left untouched.')
        if require_idle:
            state = self._host_state()
            if state['otherCount']:
                raise _AudioError('otherSunshine', 'Another Sunshine instance is running; it was left untouched.')
            if state['active'] or (state['owned'] and not state['known']):
                raise _AudioError('activeSession', 'Stop the current cast before starting a new one.')
        result = self._script('Stop-CastHost.ps1', object_result=True)
        self._require(result, ('stopped',), 'hostStopFailed', 'The Sunshine host owned by this app did not stop.')
        deadline = time.monotonic() + 5
        while True:
            processes = self._processes()
            if not processes.get('owned'):
                if processes.get('otherCount'):
                    raise _AudioError('otherSunshine', 'Another Sunshine instance is running; default outputs cannot be restored yet.')
                return result
            if time.monotonic() >= deadline:
                raise _AudioError('hostStopFailed', 'The Sunshine host owned by this app has not exited yet.')
            time.sleep(0.2)

    def _start_host(self) -> dict:
        result = self._script('Start-CastHost.ps1', object_result=True)
        self._require(result, ('started', 'already-running'), 'hostStartFailed', 'Sunshine failed to start.')
        # Cold GPU encoder discovery on this PC takes more than ten seconds.
        deadline = time.monotonic() + 45
        while not self._server_state()['known']:
            if time.monotonic() >= deadline:
                raise _AudioError('hostNotReady', 'Sunshine started, but its local casting API is not ready yet.')
            time.sleep(0.25)
        return result

    def _elevate_enable(self, session: dict) -> dict:
        paths = self._paths(session)
        if paths['install'].exists():
            raise _AudioError('installResultExists', 'An install result already exists; administrator rights were not requested again.')
        inner = ('& ' + self._quote(self.cast_dir / 'Enable-TvOnlyAudio.ps1') +
                 ' -ReceiptDirectory ' + self._quote(self.private / 'audio-driver-receipts') +
                 ' -ResultPath ' + self._quote(paths['install']))
        encoded = base64.b64encode(inner.encode('utf-16-le')).decode('ascii')
        command = (
            "$ErrorActionPreference='Stop'; try { "
            f"$p=Start-Process -FilePath {self._quote(self.pwsh)} "
            f"-ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand','{encoded}') "
            "-Verb RunAs -WindowStyle Hidden -Wait -PassThru; "
            "@{status='finished'; exitCode=$p.ExitCode} | ConvertTo-Json -Compress "
            "} catch { $e=$_.Exception; $cancelled=$false; while($null -ne $e) { "
            "if($e -is [ComponentModel.Win32Exception] -and $e.NativeErrorCode -eq 1223) {$cancelled=$true}; "
            "$e=$e.InnerException }; @{status=$(if($cancelled){'cancelled'}else{'failed'})} | ConvertTo-Json -Compress }"
        )
        # Do not abandon a still-running elevated installer and race it with rollback.
        elevated = self._powershell(command, timeout=None)
        if elevated.get('status') == 'cancelled':
            raise _AudioError('uacCancelled', 'Administrator approval was cancelled; TV-only audio was not enabled.')
        if elevated.get('status') != 'finished' or elevated.get('exitCode') != 0 or not paths['install'].is_file():
            raise _AudioError('elevationFailed', 'The elevated install returned an incomplete result; TV-only audio was not enabled.')
        try:
            result = json.loads(paths['install'].read_text(encoding='utf-8-sig'))
            if not isinstance(result, dict):
                raise ValueError()
            return result
        except Exception:
            raise _AudioError('invalidInstallResult', 'The elevated install result could not be read; check the audio restore state.') from None

    def _ensure_endpoint(self, session: dict) -> str:
        check = self._script('Enable-TvOnlyAudio.ps1', {'CheckOnly': True})
        if check.get('status') == 'needsElevation':
            session['stage'] = 'waitingForAdministrator'
            self._write_session(session)
            check = self._elevate_enable(session)
        elif check.get('status') == 'needsInstallation':
            check = self._script('Enable-TvOnlyAudio.ps1', {
                'ReceiptDirectory': self.private / 'audio-driver-receipts',
                'ResultPath': self._paths(session)['install']}, timeout=None)
        if check.get('status') != 'ready' or not isinstance(check.get('endpointId'), str):
            status = str(check.get('status', 'error'))
            messages = {'endpointMissing': 'No Steam playback device appeared after the driver install; casting was not continued.',
                        'restoreIncomplete': 'Default outputs were not fully restored after the install; casting was not continued.',
                        'ambiguousEndpoint': 'Multiple Steam playback devices were found; none could be selected safely.'}
            raise _AudioError('audioDeviceNotReady', messages.get(status, 'The TV-only audio device is not ready; casting was not continued.'))
        session['endpointId'] = check['endpointId']
        session['rebootRequired'] = bool(check.get('rebootRequired', False))
        self._write_session(session)
        return check['endpointId']

    def _cleanup(self, session: dict | None, *, close_app: bool, require_idle: bool = False) -> dict:
        errors, warnings = [], []
        if session and session['phase'] == 'completed':
            # A second Stop must not overwrite audio choices made after recovery.
            session = None
        # A fresh remote connection must not be interrupted while recovering an idle host.
        if require_idle:
            state = self._host_state()
            if state['otherCount'] or state['active'] or (state['owned'] and not state['known']):
                raise _AudioError('activeSession', 'Stop the current cast before starting a new one.')
        if session:
            session['phase'], session['stage'] = 'stopping', 'stoppingHost'
            try:
                self._write_session(session)
            except Exception:
                warnings.append({'code': 'receiptWriteFailed', 'message': 'The restore receipt could not be written; continuing the restore.'})
        if close_app:
            try:
                processes = self._processes()
                if processes.get('owned') and not processes.get('otherCount'):
                    self._script('Invoke-CastHostApi.ps1', {'Method': 'POST', 'Path': '/api/apps/close'}, object_result=True, timeout=30)
            except Exception:
                # Stopping the owned process still terminates the stream if the API fails.
                warnings.append({'code': 'closeApiFailed', 'message': 'The close-cast API did not respond; stopping the app-owned Sunshine host anyway.'})
        try:
            self._stop_host(require_idle=require_idle)
        except _AudioError as error:
            errors.append({'code': error.code, 'message': str(error)})
        if session:
            paths = self._paths(session)
            # Always attempt each restore independently, even after another step fails.
            for kind, script, params, accepted in (
                ('defaults', 'Restore-CastAudioDefaults.ps1', {'SnapshotPath': paths['defaults']}, ('restored',)),
                ('config', 'Set-CastAudioMode.ps1', {'Mode': 'Restore', 'ConfigPath': self.config_path,
                                                 'ReceiptPath': paths['config']}, ('restored', 'alreadyRestored')),
            ):
                if not paths[kind].exists():
                    if session.get(kind + 'Saved'):
                        errors.append({'code': kind + 'BackupMissing', 'message': 'A backup file needed for the audio restore is missing.'})
                    continue
                try:
                    result = self._script(script, params)
                    self._require(result, accepted, kind + 'RestoreFailed',
                                  'The original default playback devices were not fully restored.' if kind == 'defaults' else 'The original Sunshine configuration was not fully restored; the backup was kept.')
                except _AudioError as error:
                    errors.append({'code': error.code, 'message': str(error)})
            session['phase'] = 'recoveryNeeded' if errors else 'completed'
            session['stage'] = 'recoveryNeeded' if errors else 'restored'
            session['recoveryErrors'] = errors
            session['completedUtc'] = self._utc() if not errors else None
            try:
                self._write_session(session)
            except Exception:
                errors.append({'code': 'receiptWriteFailed', 'message': 'Settings were restored but the session receipt could not be updated; keep the backup.'})
        return {'ok': not errors, 'needsRecovery': bool(errors), 'errors': errors, 'warnings': warnings}

    def prepare(self, tv_only: bool) -> dict:
        if not isinstance(tv_only, bool):
            return {'ok': False, 'code': 'invalidAudioMode', 'message': 'Audio mode must be a boolean.'}
        with self._operation_lock:
            self._busy = True
            session = None
            try:
                previous = self._read_session()
                state = self._host_state()
                if state['otherCount']:
                    raise _AudioError('otherSunshine', 'Another Sunshine instance is running; it was left untouched.')
                if state['active']:
                    if previous and previous['phase'] == 'prepared' and previous['tvOnlyAudio'] == tv_only:
                        return {'ok': True, 'status': 'alreadyActive', 'alreadyActive': True,
                                'tvOnlyAudio': tv_only, 'needsRecovery': False}
                    raise _AudioError('activeAudioPolicyChange', 'Casting is active; stop it and start again to change the audio output.')
                if state['owned'] and not state['known']:
                    raise _AudioError('unknownActiveState', 'Could not confirm the current casting state; Sunshine was not interrupted. Try again later.')
                if previous and previous['phase'] != 'completed':
                    recovered = self._cleanup(previous, close_app=False, require_idle=True)
                    if not recovered['ok']:
                        return {**recovered, 'status': 'recoveryNeeded', 'message': 'The previous audio restore has not finished; a new cast was not started.'}
                session = {'schemaVersion': 1, 'kind': 'castAudioSession', 'id': uuid.uuid4().hex,
                           'createdUtc': self._utc(), 'phase': 'preparing', 'stage': 'stoppingIdleHost',
                           'tvOnlyAudio': tv_only, 'hostWasRunning': state['owned'],
                           'defaultsSaved': False, 'configSaved': False, 'hostStopped': False}
                self._write_session(session)
                self._stop_host(require_idle=True)
                session['hostStopped'] = True
                paths = self._paths(session)
                self._require(self._script('Save-CastAudioDefaults.ps1', {'SnapshotPath': paths['defaults']}),
                              ('saved',), 'snapshotFailed', 'Could not save the current default playback devices; casting was not continued.')
                session['defaultsSaved'], session['stage'] = True, 'checkingAudioDevice'
                self._write_session(session)
                if tv_only:
                    endpoint = self._ensure_endpoint(session)
                    self._require(self._script('Set-CastAudioMode.ps1', {
                        'Mode': 'TvOnly', 'ConfigPath': self.config_path,
                        'ReceiptPath': paths['config'], 'EndpointId': endpoint}),
                        ('configured', 'alreadyConfigured'), 'configurationFailed', 'Could not enable the TV-only audio configuration.')
                    session['configSaved'] = True
                session['stage'] = 'startingHost'
                self._write_session(session)
                self._start_host()
                session['phase'], session['stage'] = 'prepared', 'readyForCast'
                self._write_session(session)
                return {'ok': True, 'status': 'prepared', 'alreadyActive': False,
                        'tvOnlyAudio': tv_only, 'needsRecovery': False, 'sessionId': session['id'],
                        'rebootRequired': bool(session.get('rebootRequired'))}
            except Exception as error:
                code = error.code if isinstance(error, _AudioError) else 'audioPreparationFailed'
                message = str(error) if isinstance(error, _AudioError) else 'Local audio preparation failed; the original settings were restored where possible.'
                recovery = {'ok': True, 'needsRecovery': False, 'errors': [], 'warnings': []}
                if session:
                    try:
                        if session.get('hostStopped'):
                            recovery = self._cleanup(session, close_app=False)
                        else:
                            # The last idle check may reject a newly active stream. Do not
                            # turn that protective refusal into an unconditional stop.
                            session['phase'], session['stage'] = 'completed', 'abortedBeforeAudioChange'
                            self._write_session(session)
                        if recovery['ok'] and session.get('hostStopped') and session.get('hostWasRunning'):
                            try:
                                self._start_host()
                            except Exception:
                                recovery['warnings'].append({'code': 'originalHostNotRestarted', 'message': 'Audio was restored, but the original Sunshine host was not restarted.'})
                    except Exception:
                        recovery = {'needsRecovery': True, 'errors': [{'code': 'recoveryInterrupted', 'message': 'The restore has not finished; use Stop casting to retry.'}], 'warnings': []}
                elif code == 'invalidReceipt':
                    recovery['needsRecovery'] = True
                return {'ok': False, 'status': 'cancelled' if code == 'uacCancelled' else 'error',
                        'code': code, 'message': message, 'needsRecovery': recovery['needsRecovery'],
                        'recoveryErrors': recovery['errors'], 'warnings': recovery['warnings']}
            finally:
                self._busy = False

    def stop(self) -> dict:
        with self._operation_lock:
            self._busy = True
            try:
                try:
                    session = self._read_session()
                except _AudioError as error:
                    result = self._cleanup(None, close_app=True)
                    return {**result, 'ok': False, 'needsRecovery': True,
                            'code': error.code, 'message': str(error)}
                result = self._cleanup(session, close_app=True)
                return {**result, 'status': 'stopped' if result['ok'] else 'recoveryNeeded',
                        'tvOnlyAudio': False if result['ok'] else bool(session and session['tvOnlyAudio']),
                        'message': 'Casting stopped and the original audio output was restored.' if result['ok'] else 'Some settings still need restoring after casting stopped.'}
            except Exception:
                return {'ok': False, 'status': 'recoveryNeeded', 'needsRecovery': True,
                        'message': 'Stopping or the audio restore has not finished; click Stop casting again.'}
            finally:
                self._busy = False

    def status(self) -> dict:
        # No operation lock: status remains responsive while the user answers UAC.
        try:
            session = self._read_session()
        except _AudioError as error:
            return {'tvOnlyAudio': False, 'audioPolicyKnown': False, 'needsRecovery': True,
                    'operationInProgress': self._busy, 'audioStage': 'invalidReceipt',
                    'message': str(error)}
        state = self._server_state()
        incomplete = bool(session and session['phase'] not in ('prepared', 'completed'))
        recovery = bool(session and (session['phase'] == 'recoveryNeeded' or
                        (not self._busy and (incomplete or
                         (session['phase'] == 'prepared' and not state['reachable'])))))
        policy_known = bool(session and session['phase'] == 'prepared')
        return {'tvOnlyAudio': bool(policy_known and session['tvOnlyAudio']),
                'audioPolicyKnown': policy_known, 'needsRecovery': recovery,
                'operationInProgress': self._busy,
                'audioStage': session.get('stage', 'idle') if session else 'idle',
                'castActive': state['active'], 'castStateKnown': state['known'],
                'recoveryErrors': session.get('recoveryErrors', []) if session else []}

import json
import math
import os
import tempfile
from pathlib import Path


def atomic_json(path, data, mode=0o640):
    path = Path(path)
    fd, name = tempfile.mkstemp(prefix=path.name + '.', dir=path.parent)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(data, stream, ensure_ascii=False, allow_nan=False)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(name, mode)
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def snapshot(result, models, now, requested_model='gpt-6-astra', actual_model=''):
    """Prefer a named Astra bucket; never substitute an unrelated model quota."""
    buckets = result.get('rateLimitsByLimitId')
    if not isinstance(buckets, dict) or not buckets:
        legacy = result.get('rateLimits')
        buckets = {legacy.get('limitId') or 'codex': legacy} if isinstance(legacy, dict) else {}
    astra = [key for key, value in buckets.items() if isinstance(value, dict)
             and 'astra' in (str(key) + ' ' + str(value.get('limitName') or '')).lower()]
    if len(astra) > 1:
        raise ValueError('ambiguous_astra_bucket')
    key = astra[0] if astra else 'codex'
    selected = buckets.get(key)
    if not isinstance(selected, dict):
        raise ValueError('requested_quota_missing')
    windows = []
    for name in ('primary', 'secondary'):
        window = selected.get(name)
        if not isinstance(window, dict):
            continue
        used, minutes, reset = (window.get(k) for k in ('usedPercent', 'windowDurationMins', 'resetsAt'))
        if not number(used) or not number(minutes) or minutes <= 0 or not number(reset) or reset <= now:
            raise ValueError('quota_window_invalid_or_expired')
        windows.append({'key': name, 'remaining': max(0, min(100, 100 - used)),
                        'minutes': minutes, 'reset': reset})
    if not windows:
        raise ValueError('quota_windows_missing')
    return {'ok': True, 'checked_at': now, 'limit_id': key,
            'limit_name': selected.get('limitName') or 'Codex — общий лимит аккаунта',
            'requested_model': requested_model, 'requested_effort': 'ultra',
            'model_available': requested_model in models,
            'actual_model': actual_model, 'shared_quota': not bool(astra), 'windows': windows}


def fresh(data, now):
    if not isinstance(data, dict) or not data.get('ok'):
        return False
    checked = data.get('checked_at')
    windows = data.get('windows')
    return (number(checked) and -5 <= now - checked <= 90 and isinstance(windows, list)
            and bool(windows) and all(isinstance(w, dict) and number(w.get('remaining'))
                                     and 0 <= w['remaining'] <= 100 and number(w.get('reset'))
                                     and w['reset'] > now for w in windows))


def percent(data, now):
    return f"{min(w['remaining'] for w in data['windows']):g}%" if fresh(data, now) else '—%'

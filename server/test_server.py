import copy
import json
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch
from pathlib import Path
from server.bot import Bot, TelegramError, button, default_state, validate_rule
from server.common import atomic_json, fresh, percent, snapshot


def limits(used=7, reset=10000):
    return {'rateLimitsByLimitId': {'codex': {'limitName': None, 'primary': {
        'usedPercent': used, 'windowDurationMins': 10080, 'resetsAt': reset}}}}


class Fake:
    def __init__(self):
        self.calls = []
        self.counter = 200
        self.fail = None

    def call(self, method, **body):
        self.calls.append((method, body))
        if self.fail:
            result = self.fail(method, body)
            if isinstance(result, Exception):
                raise result
        if method in ('sendMessage', 'editMessageText'):
            self.counter += 1
            return {'message_id': body.get('message_id', self.counter), 'chat': {'id': body['chat_id']}}
        return True


class SourceTests(unittest.TestCase):
    def test_collector_reads_only_config_and_allowed_rpc_methods(self):
        from server.collector import collect
        from io import BytesIO
        methods = []

        class RpcFake:
            def __init__(self, *_):
                self.process = SimpleNamespace(stdin=BytesIO())
            def request(self, method, params):
                methods.append(method)
                return {'data': [{'id': 'gpt-5.6-sol'}]} if method == 'model/list' else limits()
            def close(self):
                pass
        args = SimpleNamespace(actual_model='', source_service='wechat.service', command='/codex', workspace='/work')
        with patch('server.collector.Rpc', RpcFake), patch.dict('os.environ', {'CODEX_HOME': '/profile'}), \
                patch('server.collector.subprocess.check_output', return_value='CODEX_HOME=/profile CODEX_MODEL=gpt-5.6-sol'), \
                patch('server.collector.time.time', return_value=100):
            data = collect(args)
        self.assertEqual(data['actual_model'], 'gpt-5.6-sol')
        self.assertEqual(methods, ['initialize', 'model/list', 'account/rateLimits/read'])
        with patch.dict('os.environ', {'CODEX_HOME': '/profile'}), \
                patch('server.collector.subprocess.check_output', return_value='CODEX_HOME=/other'):
            with self.assertRaisesRegex(ValueError, 'source_profile_changed'):
                collect(args)

    def test_shared_quota_is_identified_without_claiming_astra_support(self):
        data = snapshot(limits(), ['gpt-5.6-sol'], 100, actual_model='gpt-5.6-sol')
        self.assertEqual(percent(data, 100), '93%')
        self.assertTrue(data['shared_quota'])
        self.assertFalse(data['model_available'])

    def test_named_astra_quota_wins_over_common(self):
        raw = limits()
        raw['rateLimitsByLimitId']['codex_astra'] = {'limitName': 'Astra', 'primary': {
            'usedPercent': 80, 'windowDurationMins': 300, 'resetsAt': 500}}
        data = snapshot(raw, ['gpt-6-astra'], 100)
        self.assertEqual(percent(data, 100), '20%')
        self.assertEqual(data['limit_id'], 'codex_astra')
        self.assertFalse(data['shared_quota'])

    def test_unrelated_bucket_and_invalid_windows_are_not_fabricated(self):
        with self.assertRaises(ValueError):
            snapshot({'rateLimitsByLimitId': {'spark': {}}}, [], 100)
        for value in [None, '7', float('nan'), True]:
            raw = limits(value)
            with self.assertRaises(ValueError):
                snapshot(raw, [], 100)
        with self.assertRaises(ValueError):
            snapshot(limits(reset=50), [], 100)

    def test_freshness_and_minimum_window(self):
        raw = limits()
        raw['rateLimitsByLimitId']['codex']['secondary'] = {
            'usedPercent': 75, 'windowDurationMins': 300, 'resetsAt': 600}
        data = snapshot(raw, [], 100)
        self.assertEqual(percent(data, 100), '25%')
        self.assertEqual(percent(data, 191), '—%')
        self.assertEqual(percent({'ok': False}, 100), '—%')
        self.assertFalse(fresh(dict(data, checked_at=200), 100))


class BotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.config = {'owner_chat_id': 123, 'group_chat_id': -999, 'group_title': 'Test',
                       'allowed_users': [123], 'snapshot_path': str(root / 'source.json'),
                       'refresh_path': str(root / 'request')}
        self.now = 100.0
        self.state = default_state(self.config)
        self.saved = None
        self.fake = Fake()
        self.bot = Bot(self.config, self.state, self.save, self.fake, lambda: self.now)
        self.set_source(93)

    def save(self, value):
        self.saved = copy.deepcopy(value)

    def set_source(self, remaining):
        self.bot.data = snapshot(limits(100 - remaining), [], self.now)
        atomic_json(self.config['snapshot_path'], self.bot.data)

    def rule(self, rid='r1', threshold=10, count=2, continuous=False):
        rule = {'id': rid, 'percent': threshold, 'count': count, 'interval': 2,
                'enabled': True, 'continuous': continuous}
        self.state['rules'].append(rule)
        return rule

    def press(self, data, uid=123, chat=-999, mid=5):
        self.bot.handle({'callback_query': {'id': 'cb', 'data': data, 'from': {'id': uid},
                        'message': {'message_id': mid, 'chat': {'id': chat}}}})

    def audible(self):
        return [b for m, b in self.fake.calls if m == 'sendMessage' and b.get('disable_notification') is False]

    def test_quiet_create_edit_restart_deduplicate(self):
        self.bot.publish_status()
        mid = self.state['statuses']['-999']['message_id']
        self.assertEqual(self.fake.calls[-1][1]['text'], '93%')
        self.assertTrue(self.fake.calls[-1][1]['disable_notification'])
        self.bot.publish_status()
        self.assertEqual(len(self.fake.calls), 1)
        self.bot = Bot(self.config, copy.deepcopy(self.saved), self.save, self.fake, lambda: self.now)
        self.set_source(90)
        self.bot.publish_status()
        self.assertEqual(self.fake.calls[-1][0], 'editMessageText')
        self.assertEqual(self.fake.calls[-1][1]['message_id'], mid)

    def test_alarm_moves_quiet_status_below_and_deletes_only_old_status(self):
        self.rule(count=1)
        self.set_source(5)
        self.bot.publish_status()
        old = self.state['statuses']['-999']['message_id']
        self.bot.schedule()
        self.bot.send_due()
        self.assertEqual(len(self.audible()), 1)
        self.assertEqual([m for m, _ in self.fake.calls][-3:], ['sendMessage', 'sendMessage', 'deleteMessage'])
        self.assertEqual(self.fake.calls[-1][1]['message_id'], old)
        self.assertTrue(self.fake.calls[-2][1]['disable_notification'])

    def test_finite_alarm_does_not_repeat_on_restart_but_rearms_after_recovery(self):
        self.state['silent'] = False
        self.rule(count=1)
        self.set_source(5)
        self.bot.tick()
        self.bot = Bot(self.config, copy.deepcopy(self.saved), self.save, self.fake, lambda: self.now)
        self.bot.tick()
        self.assertEqual(len(self.audible()), 1)
        self.set_source(90)
        self.bot.tick()
        self.set_source(4)
        self.bot.tick()
        self.assertEqual(len(self.audible()), 2)

    def test_continuous_does_not_starve_other_alarms_and_pause_stops_all(self):
        self.state['silent'] = False
        self.rule('continuous', 2, continuous=True)
        self.rule('finite', 3, count=1)
        self.set_source(1)
        self.bot.tick()
        self.now += 5
        self.bot.tick()
        self.assertTrue(any('порог 3%' in b['text'] for b in self.audible()))
        before = len(self.audible())
        self.state['paused_until'] = self.now + 100
        self.now += 5
        self.bot.tick()
        self.assertEqual(len(self.audible()), before)

    def test_stale_source_suppresses_alarm_without_faking_percent(self):
        self.rule()
        self.set_source(1)
        self.now += 91
        self.bot.tick()
        self.assertEqual(self.audible(), [])
        self.assertEqual(self.state['statuses']['-999']['text'], '—%')

    def test_private_or_group_are_exclusive_and_test_works_while_paused(self):
        self.state['silent'] = False
        self.state['paused_until'] = 500
        self.press('destination:private')
        self.press('test:1')
        self.bot.send_due()
        self.assertEqual(self.audible()[0]['chat_id'], 123)
        self.assertEqual(len(self.audible()), 1)

    def test_unauthorized_user_cannot_change_rules_or_get_private_limits(self):
        before = copy.deepcopy(self.state)
        self.press('add', uid=666)
        self.assertEqual(self.state, before)
        self.assertEqual(self.fake.calls[-1][0], 'answerCallbackQuery')
        self.press('add', chat=-888)
        self.assertEqual(self.state, before)

    def test_rule_wizard_validation_deletion_and_green_state(self):
        self.press('add')
        rule = self.state['rules'][0]
        rid = rule['id']
        self.assertFalse(rule['enabled'])
        self.press(f'edit:{rid}:percent:30')
        self.press(f'edit:{rid}:count:50')
        self.press(f'edit:{rid}:interval:1')
        self.assertEqual(rule['interval'], 2)
        self.press(f'edit:{rid}:enabled:1')
        _, rows = self.bot.panel('rule:' + rid)
        self.assertEqual(rows[0][0]['style'], 'success')
        self.assertIn('включён', rows[0][0]['text'])
        self.assertEqual((rule['percent'], rule['count']), (30, 50))
        self.press('delete:' + rid)
        self.assertEqual(self.state['rules'], [])

    def test_custom_number_is_scoped_to_user_and_chat(self):
        self.rule()
        self.press('input:r1:interval')
        self.bot.handle({'message': {'from': {'id': 123}, 'chat': {'id': 123}, 'text': '17'}})
        self.assertEqual(self.state['rules'][0]['interval'], 2)
        self.bot.handle({'message': {'from': {'id': 123}, 'chat': {'id': -999}, 'text': '17'}})
        self.assertEqual(self.state['rules'][0]['interval'], 17)

    def test_open_menu_does_not_overwrite_percentage(self):
        self.bot.publish_status()
        mid = self.state['statuses']['-999']['message_id']
        self.press('home', mid=mid)
        self.assertNotEqual(self.fake.calls[-1][0], 'editMessageText')
        self.assertTrue(self.state['move_status'])

    def test_ambiguous_status_creation_is_not_repeated_after_restart(self):
        self.fake.fail = lambda m, b: TelegramError(uncertain=True) if m == 'sendMessage' else None
        with self.assertRaises(TelegramError):
            self.bot.publish_status()
        self.assertTrue(self.saved['statuses']['-999']['creating'])
        self.bot = Bot(self.config, copy.deepcopy(self.saved), self.save, self.fake, lambda: self.now)
        before = len(self.fake.calls)
        self.bot.publish_status()
        self.assertEqual(len(self.fake.calls), before)

    def test_rejected_status_does_not_prevent_audible_alarm(self):
        self.rule(count=1)
        self.set_source(1)
        self.fake.fail = lambda m, b: TelegramError(403) if m == 'sendMessage' and b.get('disable_notification') else None
        self.bot.tick()
        self.assertEqual(len(self.audible()), 1)

    def test_ambiguous_alarm_is_not_replayed_but_429_is_retried(self):
        self.state['silent'] = False
        self.rule(count=1)
        self.set_source(1)
        self.bot.schedule()
        self.fake.fail = lambda m, b: TelegramError(429, retry=30)
        with self.assertRaises(TelegramError):
            self.bot.send_due()
        self.assertEqual(self.state['alarms']['r1']['left'], 1)
        self.now += 31
        self.fake.fail = lambda m, b: TelegramError(uncertain=True)
        with self.assertRaises(TelegramError):
            self.bot.send_due()
        self.assertEqual(self.saved['alarms']['r1']['left'], 0)

    def test_reset_rearms_rule_and_refresh_is_a_file_request_only(self):
        self.rule(count=1)
        self.state['silent'] = False
        self.set_source(1)
        self.bot.tick()
        self.bot.data = snapshot(limits(99, reset=20000), [], self.now)
        atomic_json(self.config['snapshot_path'], self.bot.data)
        self.bot.tick()
        self.assertEqual(len(self.audible()), 2)
        self.press('refresh')
        self.assertTrue(Path(self.config['refresh_path']).exists())


if __name__ == '__main__':
    unittest.main()

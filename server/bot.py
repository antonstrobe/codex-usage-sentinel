"""Telegram-only controls. No shell commands, Codex credentials or public ports."""
import argparse
import copy
import json
import os
import re
import signal
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path
from .common import atomic_json, fresh, percent, read_json


class TelegramError(Exception):
    def __init__(self, code=0, retry=5, missing=False, uncertain=False):
        self.code, self.retry, self.missing, self.uncertain = code, retry, missing, uncertain
        super().__init__('telegram_' + str(code))


class Telegram:
    def __init__(self, token):
        self.token = token
        self.next_write = 0
        self.last_group_send = {}

    def call(self, method, **body):
        write = method in ('sendMessage', 'editMessageText', 'deleteMessage')
        if write:
            time.sleep(max(0, self.next_write - time.monotonic()))
        group_send = method == 'sendMessage' and body.get('chat_id', 0) < 0
        if group_send:
            time.sleep(max(0, self.last_group_send.get(body['chat_id'], 0) + 3.1 - time.monotonic()))
        request = urllib.request.Request('https://api.telegram.org/bot' + self.token + '/' + method,
                                         json.dumps(body).encode(), {'Content-Type': 'application/json'})
        try:
            try:
                with urllib.request.urlopen(request, timeout=15) as response:
                    data = json.load(response)
            except urllib.error.HTTPError as error:
                data = json.loads(error.read(65536))
        except (OSError, ValueError):
            raise TelegramError(uncertain=True) from None
        finally:
            if write:
                self.next_write = time.monotonic() + 2
            if group_send:
                self.last_group_send[body['chat_id']] = time.monotonic()
        if not data.get('ok'):
            code = data.get('error_code', 0)
            description = str(data.get('description', '')).lower()
            if method == 'editMessageText' and code == 400 and 'message is not modified' in description:
                return None
            if method == 'deleteMessage' and code == 400 and 'message to delete not found' in description:
                return True
            retry = max(2, min(86400, int(data.get('parameters', {}).get('retry_after', 5))) + 1)
            if code == 429:
                self.next_write = time.monotonic() + retry
            raise TelegramError(code, retry, code == 400 and 'message to edit not found' in description,
                                uncertain=code >= 500)
        return data['result']


def button(label, action, active=False, danger=False):
    value = {'text': ('✓ ' if active else '') + label, 'callback_data': action}
    if active:
        value['style'] = 'success'
    elif danger:
        value['style'] = 'danger'
    return value


def default_state(config):
    return {'offset': 0, 'destination': 'group', 'silent': True, 'paused_until': 0,
            'rules': [], 'alarms': {}, 'tests': [], 'statuses': {}, 'pending': {},
            'allowed_users': list(config['allowed_users']), 'pending_usernames': config.get('pending_usernames', []),
            'last_error': '', 'move_status': False, 'announced': False}


def validate_rule(rule):
    for field, low, high in [('percent', 0, 100), ('count', 1, 10000), ('interval', 2, 86400)]:
        value = rule.get(field)
        if not isinstance(value, int) or isinstance(value, bool) or not low <= value <= high:
            raise ValueError('invalid_' + field)
    if not re.fullmatch(r'[a-zA-Z0-9_-]{1,40}', rule.get('id', '')):
        raise ValueError('invalid_rule_id')
    if not isinstance(rule.get('enabled'), bool) or not isinstance(rule.get('continuous'), bool):
        raise ValueError('invalid_rule_switch')


class Bot:
    def __init__(self, config, state, save, telegram, clock=time.time):
        self.config, self.state, self.save, self.api, self.clock = config, state, save, telegram, clock
        self.data = {}
        self.status_due = 0
        self.status_retry_until = 0
        for key, value in default_state(config).items():
            self.state.setdefault(key, value)
        for rule in self.state['rules']:
            validate_rule(rule)
        if len({r['id'] for r in self.state['rules']}) != len(self.state['rules']):
            raise ValueError('duplicate_rule_id')
        if config['owner_chat_id'] <= 0 or config['group_chat_id'] >= 0:
            raise ValueError('invalid_recipients')

    def persist(self):
        self.save(self.state)

    @property
    def target(self):
        mode = self.state['destination']
        if mode not in ('private', 'group'):
            raise ValueError('invalid_destination')
        return self.config['group_chat_id'] if mode == 'group' else self.config['owner_chat_id']

    def paused(self):
        return self.state['paused_until'] > self.clock()

    def allowed(self, user, chat):
        uid = user.get('id')
        if not isinstance(uid, int) or (chat != uid and chat != self.config['group_chat_id']):
            return False
        if uid in self.state['allowed_users']:
            return True
        name = str(user.get('username', '')).lower()
        pending = [str(n).lower() for n in self.state['pending_usernames']]
        if name and name in pending:
            member = self.api.call('getChatMember', chat_id=self.config['group_chat_id'], user_id=uid)
            if member.get('status') in ('creator', 'administrator', 'member') or (
                    member.get('status') == 'restricted' and member.get('is_member')):
                self.state['allowed_users'].append(uid)
                self.state['pending_usernames'] = [n for n in self.state['pending_usernames'] if n.lower() != name]
                self.persist()
                return True
        return False

    def source_text(self):
        data = self.data
        lines = ['Источник: серверный CLI WeChat', 'Наблюдаемый режим: Astra · Ultra']
        if not fresh(data, self.clock()):
            return '\n'.join(lines + ['Свежие лимиты недоступны. Проценты не подставляются.'])
        lines += ['Квота: ' + data['limit_name'] + ' (' + data['limit_id'] + ')']
        if data.get('shared_quota'):
            lines.append('Это общий лимит аккаунта Codex. Отдельного счётчика Ultra CLI не отдаёт.')
        if not data.get('model_available'):
            lines.append('⚠ Astra пока отсутствует в списке моделей этого CLI.')
        if data.get('actual_model'):
            lines.append('Текущая модель WeChat: ' + data['actual_model'])
        for window in data['windows']:
            mins = window['minutes']
            label = 'неделя' if mins == 10080 else f'{mins / 60:g} ч' if mins % 60 == 0 else f'{mins:g} мин'
            reset = datetime.fromtimestamp(window['reset'], timezone.utc).strftime('%d.%m %H:%M UTC')
            lines.append(f"{label}: {window['remaining']:g}% · сброс {reset}")
        lines.append('Будильники смотрят на наименьший остаток доступных окон этой квоты.')
        return '\n'.join(lines)

    def panel(self, page='home'):
        back = [button('← Меню', 'home')]
        if page == 'source':
            return self.source_text(), [[button('Обновить лимиты', 'refresh')], back]
        if page == 'pause':
            return 'Выберите срок паузы будильников. Тихие проценты продолжат обновляться.', [
                [button('15 минут', 'pause:900'), button('1 час', 'pause:3600')],
                [button('8 часов', 'pause:28800'), button('До включения', 'pause:forever')],
                [button('Возобновить будильники', 'pause:0')], back]
        if page == 'test':
            return 'Тест со звуком уйдёт выбранному получателю. Между сообщениями минимум 2 секунды.', [
                [button('Отправить 1 сообщение', 'test:1'), button('Отправить 10 сообщений', 'test:10')],
                [button('Остановить тест', 'test:0')], back]
        if page == 'rules':
            rows = [[button(f"{r['percent']}% · " + ('постоянно' if r['continuous'] else str(r['count']) + ' сообщ.')
                            + f" · {r['interval']} с", 'rule:' + r['id'], r['enabled'])]
                    for r in sorted(self.state['rules'], key=lambda r: -r['percent'])]
            return 'Будильники: нажмите на событие, чтобы изменить или удалить его.', rows + [
                [button('＋ Добавить будильник', 'add')], back]
        if page.startswith('rule:'):
            rid = page.split(':')[1]
            rule = next((r for r in self.state['rules'] if r['id'] == rid), None)
            if not rule:
                return self.panel('rules')
            p = 'edit:' + rid + ':'
            return f"Будильник при остатке ≤ {rule['percent']}%", [
                [button('Будильник включён' if rule['enabled'] else 'Будильник выключен',
                        p + 'enabled:' + str(int(not rule['enabled'])), rule['enabled'])],
                [button(f"Порог: {rule['percent']}%", 'pick:' + rid + ':percent')],
                [button(f"Количество: {rule['count']}", 'pick:' + rid + ':count'),
                 button(f"Интервал: {rule['interval']} с", 'pick:' + rid + ':interval')],
                [button('Повтор до восстановления включён' if rule['continuous'] else 'Повтор до восстановления выключен',
                        p + 'continuous:' + str(int(not rule['continuous'])), rule['continuous'])],
                [button('Удалить будильник', 'remove:' + rid, danger=True)],
                [button('← Будильники', 'rules')]]
        if page.startswith('pick:'):
            _, rid, field = page.split(':')
            options = {'percent': [1, 2, 3, 5, 10, 20, 30, 50], 'count': [1, 3, 5, 10, 20, 50, 100, 1000],
                       'interval': [2, 3, 5, 10, 30, 60, 300, 3600]}
            labels = {'percent': 'Процент', 'count': 'Количество сообщений', 'interval': 'Интервал в секундах'}
            values = options[field]
            rows = [[button(str(v), f'edit:{rid}:{field}:{v}') for v in values[i:i + 4]]
                    for i in range(0, len(values), 4)]
            return labels[field] + ': выберите значение или введите своё.', rows + [
                [button('Ввести своё число', f'input:{rid}:{field}')], [button('← Событие', 'rule:' + rid)]]
        if page.startswith('remove:'):
            rid = page.split(':')[1]
            return 'Удалить этот будильник?', [[button('Удалить', 'delete:' + rid, danger=True),
                                              button('Сохранить', 'rule:' + rid)]]
        destination = 'группа «' + self.config.get('group_title', 'Codex') + '»' if self.state['destination'] == 'group' else 'личный чат владельца'
        text = f"Лимиты WeChat CLI · {percent(self.data, self.clock())}\nПолучатель: {destination}\n" \
               'Astra · Ultra: тип доступной квоты указан в «Источник лимитов».'
        if self.state['last_error']:
            text += '\nПоследняя ошибка: ' + self.state['last_error']
        rows = [[button('Обновить лимиты', 'refresh'), button('Источник лимитов', 'source')],
                [button('Будильники', 'rules'), button('Тестовое сообщение', 'test')],
                [button('Пауза включена' if self.paused() else 'Пауза выключена', 'pause', self.paused())],
                [button('Тихий статус включён' if self.state['silent'] else 'Тихий статус выключен',
                        'silent:' + str(int(not self.state['silent'])), self.state['silent'])],
                [button('В группу', 'destination:group', self.state['destination'] == 'group'),
                 button('Лично владельцу', 'destination:private', self.state['destination'] == 'private')],
                [button('Восстановить тихий статус', 'recover')]]
        return text, rows

    def show(self, chat, message_id=None, page='home', text=None):
        content, rows = self.panel(page)
        params = dict(chat_id=chat, text=text or content, reply_markup={'inline_keyboard': rows})
        if message_id:
            self.api.call('editMessageText', message_id=message_id, **params)
        else:
            self.api.call('sendMessage', disable_notification=True, **params)
            if chat == self.target:
                self.state['move_status'] = True
                self.persist()
        return page

    def edit(self, rid, field, value):
        if field not in ('percent', 'count', 'interval', 'enabled', 'continuous'):
            raise ValueError('invalid_field')
        rule = next((r for r in self.state['rules'] if r['id'] == rid), None)
        if rule is None:
            raise ValueError('rule_missing')
        updated = dict(rule)
        if field in ('enabled', 'continuous'):
            if value not in ('0', '1'):
                raise ValueError('invalid_switch')
            updated[field] = value == '1'
        else:
            updated[field] = int(value)
        validate_rule(updated)
        rule.update(updated)
        self.state['alarms'].pop(rid, None)
        self.persist()

    def handle(self, update):
        callback = update.get('callback_query')
        message = callback.get('message', {}) if callback else update.get('message', {})
        user = callback.get('from', {}) if callback else message.get('from', {})
        chat = message.get('chat', {}).get('id')
        if not self.allowed(user, chat):
            if callback:
                self.api.call('answerCallbackQuery', callback_query_id=callback['id'],
                              text='Управление доступно только разрешённым участникам.', show_alert=True)
            return
        if callback:
            self.api.call('answerCallbackQuery', callback_query_id=callback['id'])
            action = str(callback.get('data', ''))
            status = self.state['statuses'].get(str(chat), {})
            if message.get('message_id') == status.get('message_id'):
                # Keep the percentage message intact when its menu button is pressed.
                self.show(chat)
                return
        else:
            text = str(message.get('text', '')).strip()
            key = f"{user['id']}:{chat}"
            pending = self.state['pending'].get(key)
            if pending and pending['expires'] > self.clock() and not text.startswith('/'):
                try:
                    self.edit(pending['rule'], pending['field'], text)
                except (ValueError, TypeError):
                    self.show(chat, text='Нужно целое число: процент 0–100, сообщений 1–10000, интервал 2–86400 секунд.')
                    return
                self.state['pending'].pop(key, None)
                self.persist()
                self.show(chat, page='rule:' + pending['rule'])
                return
            if text.split('@')[0].split(' ')[0] not in ('/start', '/menu', '/status', '/cancel'):
                return
            self.state['pending'].pop(key, None)
            self.persist()
            self.show(chat, page='source' if text.startswith('/status') else 'home')
            return
        page, custom = 'home', None
        try:
            if action in ('home', 'source', 'rules', 'pause', 'test') or action.startswith(('rule:', 'pick:', 'remove:')):
                page = action
            elif action == 'add':
                if len(self.state['rules']) >= 50:
                    raise ValueError('too_many_rules')
                rid = uuid.uuid4().hex[:8]
                self.state['rules'].append({'id': rid, 'percent': 10, 'count': 10, 'interval': 2,
                                            'continuous': False, 'enabled': False})
                page = 'rule:' + rid
            elif action.startswith('edit:'):
                _, rid, field, value = action.split(':')
                self.edit(rid, field, value)
                page = 'rule:' + rid
            elif action.startswith('delete:'):
                rid = action.split(':')[1]
                self.state['rules'] = [r for r in self.state['rules'] if r['id'] != rid]
                self.state['alarms'].pop(rid, None)
                page = 'rules'
            elif action.startswith('input:'):
                _, rid, field = action.split(':')
                if field not in ('percent', 'count', 'interval') or not any(r['id'] == rid for r in self.state['rules']):
                    raise ValueError('invalid_input')
                self.state['pending'][f"{user['id']}:{chat}"] = {'rule': rid, 'field': field, 'expires': self.clock() + 600}
                self.persist()
                self.api.call('sendMessage', chat_id=chat, disable_notification=True,
                              text='Ответьте на это сообщение целым числом. Отмена: /cancel. Ожидание — 10 минут.',
                              reply_markup={'force_reply': True, 'input_field_placeholder': 'Введите число'})
                if chat == self.target:
                    self.state['move_status'] = True
                custom = 'Ожидаю число в ответ на сообщение выше.'
            elif action.startswith('pause:'):
                value = action.split(':')[1]
                if value not in ('0', '900', '3600', '28800', 'forever'):
                    raise ValueError('invalid_pause')
                self.state['paused_until'] = 0 if value == '0' else (253402214400 if value == 'forever' else self.clock() + int(value))
            elif action.startswith('silent:'):
                value = action.split(':')[1]
                if value not in ('0', '1'):
                    raise ValueError('invalid_silent')
                self.state['silent'] = value == '1'
                self.status_due = 0
                self.status_retry_until = 0
            elif action.startswith('destination:'):
                value = action.split(':')[1]
                if value not in ('group', 'private'):
                    raise ValueError('invalid_destination')
                self.state['destination'] = value
                self.state['tests'] = []
                self.state['move_status'] = True
                self.status_due = 0
                self.status_retry_until = 0
            elif action.startswith('test:'):
                count = int(action.split(':')[1])
                if count not in (0, 1, 10):
                    raise ValueError('invalid_test')
                self.state['tests'] = [] if count == 0 else [{'left': count, 'due': self.clock(), 'chat': self.target}]
            elif action == 'refresh':
                Path(self.config['refresh_path']).touch()
                custom = 'Проверка CLI запрошена. Процент обновится после ответа сервера; повторные запросы ограничены 10 секундами.'
            elif action == 'recover':
                status = self.state['statuses'].setdefault(str(self.target), {})
                status['creating'] = False
                self.state['move_status'] = True
                self.state['last_error'] = ''
                self.status_due = 0
                self.status_retry_until = 0
            else:
                raise ValueError('unknown_action')
            self.persist()
            self.show(chat, message['message_id'], page, custom)
        except (ValueError, KeyError, TypeError):
            self.show(chat, message['message_id'], text='Настройка недоступна или устарела. Откройте меню заново.')

    def publish_status(self):
        if not self.state['silent']:
            return
        chat = self.target
        record = self.state['statuses'].setdefault(str(chat), {'message_id': 0, 'text': '', 'obsolete': []})
        if record.get('creating'):
            self.state['last_error'] = 'Создание статуса не подтверждено. Кнопка «Восстановить тихий статус» разрешит новую попытку.'
            self.persist()
            return
        text = percent(self.data, self.clock())
        old_id = record.get('message_id', 0)
        create = not old_id or self.state['move_status']
        params = dict(chat_id=chat, text=text, reply_markup={'inline_keyboard': [[button('⚙ Управление', 'home')]]})
        if create:
            record['creating'] = True
            self.persist()
        if create or record.get('text') != text:
            try:
                if create:
                    result = self.api.call('sendMessage', disable_notification=True, **params)
                else:
                    result = self.api.call('editMessageText', message_id=old_id, **params)
                if result is None and not create:
                    new_id = old_id
                elif isinstance(result, dict) and result.get('chat', {}).get('id') == chat and isinstance(result.get('message_id'), int):
                    new_id = result['message_id']
                else:
                    raise TelegramError(uncertain=True)
            except TelegramError as error:
                if create and not error.uncertain:
                    record['creating'] = False
                if error.missing and not create:
                    record['message_id'] = 0
                self.persist()
                raise
            if create and old_id:
                record.setdefault('obsolete', []).append(old_id)
            record.update(message_id=new_id, text=text, creating=False, updated_at=self.clock())
            self.state['move_status'] = False
            self.persist()
        for old in list(record.get('obsolete', []))[:3]:
            self.api.call('deleteMessage', chat_id=chat, message_id=old)
            record['obsolete'].remove(old)
            self.persist()

    def schedule(self):
        if not fresh(self.data, self.clock()) or self.paused():
            return
        window = min(self.data['windows'], key=lambda w: w['remaining'])
        cycle = f"{self.data['limit_id']}:{window['key']}:{window['reset']}"
        for rule in self.state['rules']:
            rid = rule['id']
            if not rule['enabled'] or window['remaining'] > rule['percent']:
                if rid in self.state['alarms']:
                    self.state['alarms'].pop(rid)
                    self.persist()
                continue
            alarm = self.state['alarms'].get(rid)
            if alarm is None or alarm['cycle'] != cycle:
                self.state['alarms'][rid] = {'cycle': cycle, 'left': rule['count'], 'due': self.clock()}
                self.persist()

    def publish_safe(self):
        if self.clock() < self.status_retry_until:
            return
        try:
            self.publish_status()
        except TelegramError as error:
            self.status_retry_until = self.clock() + max(60, error.retry)
            self.state['last_error'] = 'Тихий статус временно недоступен. Будильники продолжают работать.'
            self.persist()

    def send_due(self):
        now = self.clock()
        tests = self.state['tests']
        if tests and tests[0]['chat'] != self.target:
            self.state['tests'] = []
            self.persist()
            tests = []
        job, rule = (tests[0] if tests and tests[0]['left'] > 0 and tests[0]['due'] <= now else None), None
        if job is None and fresh(self.data, now) and not self.paused():
            remaining = min(w['remaining'] for w in self.data['windows'])
            eligible = []
            for candidate in self.state['rules']:
                candidate_job = self.state['alarms'].get(candidate['id'])
                if (candidate['enabled'] and remaining <= candidate['percent'] and candidate_job
                        and candidate_job['due'] <= now and (candidate['continuous'] or candidate_job['left'] > 0)):
                    eligible.append((candidate_job, candidate))
            if eligible:
                job, rule = min(eligible, key=lambda pair: (pair[0]['due'], pair[1]['percent']))
        if job is None:
            return False
        previous = copy.deepcopy(job)
        job['left'] = max(0, job['left'] - 1)
        job['due'] = now + (rule['interval'] if rule else 2)
        self.persist()  # Reserve before send: ambiguous delivery must not replay on restart.
        text = ('🔔 Будильник · осталось ' + percent(self.data, now) + f" · порог {rule['percent']}%\n"
                + self.data['limit_name']) if rule else '🔔 Тест уведомления Codex Usage Sentinel'
        try:
            self.api.call('sendMessage', chat_id=self.target, text=text, disable_notification=False)
        except TelegramError as error:
            if not error.uncertain:
                job.update(previous)
                job['due'] = now + error.retry
                self.persist()
            raise
        if rule is None and job['left'] == 0:
            self.state['tests'] = []
        self.state['move_status'] = True
        self.persist()
        self.publish_safe()
        return True

    def tick(self):
        try:
            self.data = read_json(self.config['snapshot_path'])
        except (OSError, ValueError):
            self.data = {}
        self.schedule()
        known_text = self.state['statuses'].get(str(self.target), {}).get('text')
        if self.clock() >= self.status_due or self.state['move_status'] or known_text != percent(self.data, self.clock()):
            self.status_due = self.clock() + 60
            self.publish_safe()
        self.send_due()


def run(config_path):
    import fcntl
    config = read_json(config_path)
    path = Path(config['state_path'])
    lock = open(str(path) + '.lock', 'a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    credential = Path(os.environ['CREDENTIALS_DIRECTORY']) / 'telegram-token'
    api = Telegram(credential.read_text().strip())
    if api.call('getWebhookInfo').get('url'):
        raise RuntimeError('existing_webhook_not_changed')
    state = read_json(path) if path.exists() else default_state(config)
    bot = Bot(config, state, lambda s: atomic_json(path, s), api)
    try:
        bot.data = read_json(config['snapshot_path'])
    except (OSError, ValueError):
        pass
    running = True

    def stop(*_):
        nonlocal running
        running = False

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    if not state['announced']:
        api.call('setMyCommands', commands=[{'command': 'menu', 'description': 'Управление лимитами и будильниками'},
                                           {'command': 'status', 'description': 'Источник и реальные лимиты CLI'},
                                           {'command': 'cancel', 'description': 'Отменить ввод числа'}])
        state['announced'] = True
        bot.persist()
        bot.show(bot.target)
    while running:
        try:
            bot.tick()
            updates = api.call('getUpdates', offset=state['offset'], timeout=1, limit=20,
                               allowed_updates=['message', 'callback_query'])
            for update in updates:
                if update['update_id'] < state['offset']:
                    continue
                # Persist receipt before mutations; repeated callbacks cannot toggle twice.
                state['offset'] = max(state['offset'], update['update_id'] + 1)
                bot.persist()
                bot.handle(update)
            atomic_json(path.parent / 'health.json', {'checked_at': time.time(),
                        'source_fresh': fresh(bot.data, time.time()), 'percent': percent(bot.data, time.time()),
                        'last_error': state['last_error']})
        except TelegramError as error:
            if error.code == 409:
                raise RuntimeError('another_telegram_receiver_detected') from None
            state['last_error'] = ('Telegram не подтвердил доставку; сообщение не будет автоматически продублировано.'
                                   if error.uncertain else 'Telegram временно отклонил запрос. Проверьте доступ бота к выбранному чату.')
            bot.persist()
            print('telegram_error', error.code, flush=True)
            time.sleep(min(60, error.retry))
        except (OSError, ValueError, KeyError, TypeError):
            state['last_error'] = 'Ошибка чтения или сохранения состояния. Повтор через 5 секунд.'
            # Do not expose payloads, URLs, tokens or exception tracebacks.
            print('state_error', flush=True)
            time.sleep(5)
    lock.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', required=True)
    args = parser.parse_args()
    run(args.config)

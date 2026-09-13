"""Shared Telegram relay. Python 3.10+, standard library only; no Codex credentials."""
import hashlib
import hmac
import json
import os
import queue
import re
import secrets
import sqlite3
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class Problem(Exception):
    def __init__(self, code, message, retry=2):
        self.code, self.message, self.retry = code, message, retry


def digest(token):
    if not isinstance(token, str) or not re.fullmatch(r"[A-Za-z0-9_-]{43}", token):
        raise Problem(401, "Нужно повторное подключение через Start.")
    return hashlib.sha256(token.encode()).hexdigest()


class Telegram:
    def __init__(self, token):
        self.token = token

    def call(self, method, data):
        request = urllib.request.Request(
            "https://api.telegram.org/bot" + self.token + "/" + method,
            json.dumps(data).encode(), {"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=35 if method == "getUpdates" else 12) as response:
                result = json.load(response)
        except urllib.error.HTTPError as error:
            try:
                result = json.loads(error.read(16000))
            except Exception:
                raise Problem(502, "Telegram недоступен.") from None
        except Exception:
            raise Problem(502, "Ответ Telegram не получен.") from None
        if not result.get("ok"):
            code = result.get("error_code", 502)
            delay = max(2, min(86400, int(result.get("parameters", {}).get("retry_after", 2)) + 1))
            raise Problem(code, "Telegram отклонил запрос.", delay)
        return result["result"]


class Relay:
    def __init__(self, db_path, allowed, telegram, bot_name, clock=time.time):
        self.db = sqlite3.connect(db_path, check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        self.lock = threading.RLock()
        self.clock, self.telegram, self.bot_name = clock, telegram, bot_name
        self.allowed = {key.lower(): int(value) for key, value in allowed.items()}
        self.inflight = set()
        self.last_reply = {}
        self.replies = queue.Queue(maxsize=100)
        self.poll_ok = 0
        self.db.executescript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS users (username TEXT PRIMARY KEY, chat INTEGER UNIQUE);
            CREATE TABLE IF NOT EXISTS pairings (
                id TEXT PRIMARY KEY, secret_hash TEXT UNIQUE, username TEXT, device TEXT,
                expires REAL, chat INTEGER DEFAULT 0, ip_hash TEXT, created REAL);
            CREATE TABLE IF NOT EXISTS devices (
                secret_hash TEXT PRIMARY KEY, chat INTEGER, username TEXT, device TEXT, revoked INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS messages (
                device TEXT, id TEXT, body_hash TEXT, state TEXT, created REAL,
                PRIMARY KEY(device,id));
            CREATE TABLE IF NOT EXISTS cooldowns (chat INTEGER PRIMARY KEY, until_time REAL);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value INTEGER);
            UPDATE messages SET state='unknown' WHERE state='sending';
        """)
        for username, chat in self.allowed.items():
            if chat:
                old = self.db.execute("SELECT chat FROM users WHERE username=?", (username,)).fetchone()
                if old and old[0] != chat:
                    raise RuntimeError("Configured recipient differs from pinned Telegram ID")
                self.db.execute("INSERT OR IGNORE INTO users VALUES (?,?)", (username, chat))
        self.db.commit()

    def pairing(self, body, ip):
        username = str(body.get("username", "")).lstrip("@").lower()
        device = str(body.get("device", "")).strip()
        secret_hash = body.get("secret_hash", "")
        if username not in self.allowed:
            raise Problem(403, "Этот аккаунт ещё не добавлен владельцем сервиса.")
        if not re.fullmatch(r"[a-f0-9]{64}", str(secret_hash)) or not 1 <= len(device) <= 64 or any(ord(c) < 32 for c in device):
            raise Problem(400, "Проверьте имя компьютера и повторите подключение.")
        now, ip_hash = self.clock(), hashlib.sha256(ip.encode()).hexdigest()
        with self.lock, self.db:
            self.db.execute("DELETE FROM pairings WHERE expires<?", (now,))
            self.db.execute("DELETE FROM messages WHERE created<?", (now - 86400,))
            if self.db.execute("SELECT COUNT(*) FROM pairings WHERE ip_hash=? OR username=?", (ip_hash, username)).fetchone()[0] >= 10:
                raise Problem(429, "Слишком много подключений. Подождите 10 минут.", 600)
            if self.db.execute("SELECT 1 FROM devices WHERE secret_hash=?", (secret_hash,)).fetchone():
                raise Problem(409, "Создайте новую ссылку подключения.")
            pair_id = secrets.token_urlsafe(24)
            try:
                self.db.execute("INSERT INTO pairings VALUES (?,?,?,?,?,0,?,?)",
                                (pair_id, secret_hash, username, device, now + 600, ip_hash, now))
            except sqlite3.IntegrityError:
                raise Problem(409, "Создайте новую ссылку подключения.") from None
        return {"id": pair_id, "bot_username": self.bot_name, "expires_in": 600,
                "url": "https://t.me/" + self.bot_name + "?start=c_" + pair_id}

    def pair_status(self, pair_id, token):
        secret_hash = digest(token)
        with self.lock:
            row = self.db.execute("SELECT * FROM pairings WHERE id=?", (pair_id,)).fetchone()
            if not row or not hmac.compare_digest(row["secret_hash"], secret_hash):
                raise Problem(404, "Подключение не найдено.")
            if row["expires"] <= self.clock():
                raise Problem(410, "Ссылка истекла. Создайте новую.")
            if row["chat"]:
                self.device(token)
                return {"state": "connected", "chat_id": row["chat"], "username": row["username"], "bot_username": self.bot_name}
            return {"state": "waiting"}

    def device(self, token):
        secret_hash = digest(token)
        row = self.db.execute("SELECT * FROM devices WHERE secret_hash=? AND revoked=0", (secret_hash,)).fetchone()
        if not row:
            raise Problem(401, "Подключение отключено. Подключитесь через Start заново.")
        return row

    def reply(self, chat, text):
        now = self.clock()
        if self.last_reply.get(chat, 0) > now - 2:
            return
        self.last_reply[chat] = now
        try:
            self.replies.put_nowait((chat, text))
        except queue.Full:
            pass

    def update(self, update):
        message = update.get("message", {})
        chat, sender = message.get("chat", {}), message.get("from", {})
        chat_id, username = chat.get("id"), str(sender.get("username", "")).lower()
        if chat.get("type") != "private" or type(chat_id) is not int or chat_id <= 0 or sender.get("id") != chat_id or sender.get("is_bot"):
            return
        command = str(message.get("text", "")).strip().split()
        if not command:
            return
        action = command[0].split("@")[0].lower()
        with self.lock, self.db:
            pinned = self.db.execute("SELECT * FROM users WHERE chat=?", (chat_id,)).fetchone()
            named = self.db.execute("SELECT chat FROM users WHERE username=?", (username,)).fetchone()
            permitted = (bool(pinned) and pinned["username"] in self.allowed) or (username in self.allowed and (not named or named[0] == chat_id))
            if not permitted:
                if action in ("/start", "/help"):
                    self.reply(chat_id, "Доступ к этому боту выдаёт его владелец. Ваш аккаунт пока не подключён.")
                return
            if not pinned:
                self.db.execute("INSERT OR IGNORE INTO users VALUES (?,?)", (username, chat_id))
            if action == "/stop":
                self.db.execute("UPDATE devices SET revoked=1 WHERE chat=?", (chat_id,))
                self.db.execute("DELETE FROM pairings WHERE chat=? OR username=?", (chat_id, username))
                self.reply(chat_id, "Уведомления отключены для всех ваших компьютеров. Для включения снова подключите программу через Start. Уже отправляемое сообщение ещё может прийти.")
            elif action == "/start" and len(command) == 2 and command[1].startswith("c_"):
                row = self.db.execute("SELECT * FROM pairings WHERE id=?", (command[1][2:],)).fetchone()
                if not row or row["expires"] <= self.clock():
                    self.reply(chat_id, "Ссылка истекла или не найдена. Создайте новую в программе: Telegram → Подключить через Start.")
                elif row["chat"]:
                    self.reply(chat_id, "Эта ссылка уже использована. Статус подключения показан в программе.")
                elif row["username"] != username and (not pinned or row["username"] != pinned["username"]):
                    self.reply(chat_id, "Ссылка предназначена другому аккаунту. Укажите свой username в программе и создайте новую.")
                else:
                    count = self.db.execute("SELECT COUNT(*) FROM devices WHERE chat=? AND revoked=0", (chat_id,)).fetchone()[0]
                    if count >= 10:
                        self.reply(chat_id, "Уже подключено 10 компьютеров. Команда /stop отключит их; затем подключите нужные заново.")
                        return
                    self.db.execute("INSERT INTO devices VALUES (?,?,?,?,0)", (row["secret_hash"], chat_id, row["username"], row["device"]))
                    self.db.execute("UPDATE pairings SET chat=? WHERE id=?", (chat_id, row["id"]))
                    self.reply(chat_id, "✓ Подключён компьютер «" + row["device"] + "».\nЛимиты читает программа на этом компьютере. Уведомления получает только ваш личный чат.\n/stop — отключить все ваши компьютеры. /status — проверить подключение.")
            elif action == "/status":
                count = self.db.execute("SELECT COUNT(*) FROM devices WHERE chat=? AND revoked=0", (chat_id,)).fetchone()[0]
                self.reply(chat_id, "Подключено компьютеров: " + str(count) + ".\nСостояние лимитов и время проверки смотрите в программе. /stop — отключить уведомления.")
            elif action in ("/start", "/help"):
                self.reply(chat_id, "Codex Usage Sentinel\nНа компьютере откройте Telegram → Общий бот, укажите свой username и нажмите «Подключить через Start». Затем нажмите Start по созданной ссылке.\nТокен бота и Chat ID вводить не нужно.\n/stop — отключить уведомления. /status — подключённые компьютеры.\nИнструкция: https://github.com/antonstrobe/codex-usage-sentinel/blob/main/docs/SECOND_COMPUTER.md\nТекущий EXE имеет неразобранное предупреждение Defender; не отключайте защиту ради установки.")

    def reserve(self, chat):
        now = self.clock()
        until = self.db.execute("SELECT until_time FROM cooldowns WHERE chat=?", (chat,)).fetchone()
        if chat in self.inflight or (until and until[0] > now):
            raise Problem(429, "Ожидание интервала Telegram.", max(2, int(until[0] - now) + 1) if until else 2)
        self.inflight.add(chat)
        self.db.execute("INSERT OR REPLACE INTO cooldowns VALUES (?,?)", (chat, now + 2))

    def send(self, token, body):
        # The destination is never accepted from the client.
        if set(body) != {"id", "text"} or not re.fullmatch(r"[a-f0-9]{32}", str(body.get("id", ""))) or not isinstance(body.get("text"), str) or not 1 <= len(body["text"]) <= 3000:
            raise Problem(400, "Некорректное уведомление.")
        body_hash = hashlib.sha256(body["text"].encode()).hexdigest()
        with self.lock, self.db:
            device = self.device(token)
            if device["username"] not in self.allowed:
                raise Problem(403, "Доступ к сервису отключён владельцем.")
            key, chat = device["secret_hash"], device["chat"]
            self.db.execute("DELETE FROM messages WHERE created<?", (self.clock() - 86400,))
            old = self.db.execute("SELECT * FROM messages WHERE device=? AND id=?", (key, body["id"])).fetchone()
            if old:
                if not hmac.compare_digest(old["body_hash"], body_hash):
                    raise Problem(409, "ID сообщения уже использован.")
                if old["state"] == "sending":
                    raise Problem(429, "Сообщение ещё отправляется.")
                return {"state": old["state"]}
            self.reserve(chat)
            self.db.execute("INSERT INTO messages VALUES (?,?,?,?,?)", (key, body["id"], body_hash, "sending", self.clock()))
        state, error, delay = "sent", None, 2
        try:
            self.telegram.call("sendMessage", {"chat_id": chat, "text": "Компьютер: " + device["device"] + "\n" + body["text"], "disable_notification": False})
        except Problem as exc:
            delay = exc.retry
            # Telegram 429 is a definite rejection; transport failures can be ambiguous.
            if exc.code == 429:
                error = Problem(429, "Telegram просит подождать.", delay)
                state = "retry"
            elif exc.code in (400, 401, 403):
                state = "rejected"
            else:
                state = "unknown"
        except Exception:
            state = "unknown"
        finally:
            with self.lock, self.db:
                self.inflight.discard(chat)
                self.db.execute("INSERT OR REPLACE INTO cooldowns VALUES (?,?)", (chat, self.clock() + max(2, delay)))
                if state == "retry":
                    self.db.execute("DELETE FROM messages WHERE device=? AND id=?", (key, body["id"]))
                else:
                    self.db.execute("UPDATE messages SET state=? WHERE device=? AND id=?", (state, key, body["id"]))
        if error:
            raise error
        return {"state": state}

    def poll(self, stop):
        while not stop.is_set():
            try:
                with self.lock:
                    row = self.db.execute("SELECT value FROM meta WHERE key='offset'").fetchone()
                    offset = row[0] if row else 0
                updates = self.telegram.call("getUpdates", {"offset": offset, "timeout": 25, "limit": 100, "allowed_updates": ["message"]})
                self.poll_ok = self.clock()
                for update in updates:
                    self.update(update)
                    with self.lock, self.db:
                        self.db.execute("INSERT OR REPLACE INTO meta VALUES ('offset',?)", (update["update_id"] + 1,))
            except Problem as error:
                # Never change/delete another consumer's webhook. Fail visibly on conflict.
                print("Telegram polling error:", error.code, flush=True)
                if error.code in (401, 409):
                    os._exit(1)
                stop.wait(max(3, error.retry))
            except Exception:
                print("Telegram polling failed; details suppressed", flush=True)
                stop.wait(5)

    def reply_loop(self, stop):
        while not stop.is_set():
            try:
                chat, text = self.replies.get(timeout=1)
            except queue.Empty:
                continue
            deadline = self.clock() + 30
            while not stop.is_set() and self.clock() < deadline:
                reserved, delay = False, 2
                try:
                    with self.lock, self.db:
                        self.reserve(chat)
                        reserved = True
                    self.telegram.call("sendMessage", {"chat_id": chat, "text": text})
                    break
                except Problem as error:
                    delay = error.retry
                    if error.code != 429:
                        break
                except Exception:
                    print("Bot reply failed; details suppressed", flush=True)
                    break
                finally:
                    if reserved:
                        with self.lock, self.db:
                            self.inflight.discard(chat)
                            self.db.execute("INSERT OR REPLACE INTO cooldowns VALUES (?,?)", (chat, self.clock() + max(2, delay)))
                stop.wait(delay)


def handler_for(relay):
    class Handler(BaseHTTPRequestHandler):
        server_version = "SentinelRelay"
        sys_version = ""

        def log_message(self, *args):
            pass  # No access logs, bearer tokens, pairing URLs or chat content.

        def setup(self):
            super().setup()
            self.connection.settimeout(10)

        def respond(self, code, data, retry=2):
            body = json.dumps(data, ensure_ascii=False).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("X-Content-Type-Options", "nosniff")
            if code == 429:
                self.send_header("Retry-After", str(retry))
            self.end_headers()
            self.wfile.write(body)

        def dispatch(self):
            try:
                if self.command == "GET" and self.path == "/health":
                    self.respond(200 if relay.clock() - relay.poll_ok < 90 else 503,
                                 {"ok": relay.clock() - relay.poll_ok < 90, "version": "1.2.0"})
                    return
                body = {}
                if self.command == "POST":
                    length = int(self.headers.get("Content-Length", "0"))
                    if not 1 <= length <= 16000 or self.headers.get("Transfer-Encoding") or self.headers.get_content_type() != "application/json":
                        raise Problem(400, "Некорректный запрос.")
                    body = json.loads(self.rfile.read(length))
                    if not isinstance(body, dict):
                        raise Problem(400, "Некорректный запрос.")
                authorization = self.headers.get("Authorization", "")
                token = authorization[7:] if authorization.startswith("Bearer ") else ""
                if self.command == "POST" and self.path == "/v1/pairings":
                    result = relay.pairing(body, self.headers.get("X-Real-IP", self.client_address[0]))
                elif self.command == "GET" and re.fullmatch(r"/v1/pairings/[A-Za-z0-9_-]{32}", self.path):
                    result = relay.pair_status(self.path.rsplit("/", 1)[1], token)
                elif self.command == "POST" and self.path == "/v1/messages":
                    result = relay.send(token, body)
                else:
                    raise Problem(404, "Маршрут не найден.")
                self.respond(200, result)
            except Problem as error:
                self.respond(error.code if error.code in (400, 401, 403, 404, 409, 410, 429, 502) else 502,
                             {"error": error.message, "retry_after": error.retry}, error.retry)
            except (ValueError, UnicodeError):
                self.respond(400, {"error": "Некорректный JSON."})
            except (BrokenPipeError, ConnectionError, TimeoutError):
                pass
            except Exception:
                self.respond(500, {"error": "Ошибка сервиса."})

        do_GET = dispatch
        do_POST = dispatch
    return Handler


def main():
    os.umask(0o077)
    credentials = Path(os.environ["CREDENTIALS_DIRECTORY"])
    token = (credentials / "telegram-token").read_text().strip()
    config = json.loads(Path(os.environ.get("SENTINEL_CONFIG", "/etc/codex-usage-relay/config.json")).read_text())
    telegram = Telegram(token)
    me = telegram.call("getMe", {})
    if not me.get("is_bot") or me.get("username") != config["bot_username"]:
        raise SystemExit("Configured bot identity did not match")
    if telegram.call("getWebhookInfo", {}).get("url"):
        raise SystemExit("Existing webhook detected; no changes made")
    relay = Relay(config.get("database", "/var/lib/codex-usage-relay/relay.sqlite3"), config["allowed_users"], telegram, me["username"])
    stop = threading.Event()
    threading.Thread(target=relay.poll, args=(stop,), daemon=True).start()
    threading.Thread(target=relay.reply_loop, args=(stop,), daemon=True).start()
    server = ThreadingHTTPServer(("127.0.0.1", int(config.get("port", 8796))), handler_for(relay))
    server.daemon_threads = True
    print("Codex usage relay started on loopback", flush=True)
    try:
        server.serve_forever()
    finally:
        stop.set()
        server.server_close()


if __name__ == "__main__":
    main()

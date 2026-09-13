import json
import secrets
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path
from relay import Relay, Problem, digest, handler_for


class FakeTelegram:
    def __init__(self):
        self.calls = []
        self.error = None

    def call(self, method, data):
        self.calls.append((method, data))
        if self.error:
            raise self.error
        return {"message_id": 1}


class RelayTests(unittest.TestCase):
    def setUp(self):
        self.now = 10000
        self.tg = FakeTelegram()
        self.relay = Relay(":memory:", {"user_one": 111, "user_two": 0}, self.tg, "Example_bot", lambda: self.now)

    def tearDown(self):
        self.relay.db.close()

    def begin(self, user="user_one"):
        token = secrets.token_urlsafe(32)
        pair = self.relay.pairing({"username": user, "device": "Example PC", "secret_hash": digest(token)}, "127.0.0.1")
        return token, pair

    def update(self, user, chat_id, command, **extra):
        self.relay.update({"message": {"chat": {"id": chat_id, "type": "private"}, "from": {"id": chat_id, "username": user}, "text": command, **extra}})

    def connect(self, user="user_one", chat=111):
        token, pair = self.begin(user)
        self.update(user, chat, "/start c_" + pair["id"])
        return token, pair

    def message(self):
        return {"id": secrets.token_hex(16), "text": "Test limits: 10%"}

    def test_two_computers_are_isolated(self):
        a, pa = self.connect()
        b, pb = self.connect("user_two", 222)
        self.assertEqual(111, self.relay.pair_status(pa["id"], a)["chat_id"])
        self.assertEqual(222, self.relay.pair_status(pb["id"], b)["chat_id"])
        self.relay.send(a, self.message())
        self.relay.send(b, self.message())
        self.assertEqual([111, 222], [data["chat_id"] for _, data in self.tg.calls])
        with self.assertRaises(Problem):
            self.relay.pair_status(pa["id"], b)

    def test_other_start_cannot_approve(self):
        token, pair = self.begin()
        self.update("user_two", 222, "/start c_" + pair["id"])
        self.assertEqual("waiting", self.relay.pair_status(pair["id"], token)["state"])
        with self.assertRaises(Problem):
            self.relay.send(token, self.message())

    def test_pinned_id_cannot_be_replaced_by_same_username(self):
        token, pair = self.begin()
        self.update("user_one", 999, "/start c_" + pair["id"])
        self.assertEqual("waiting", self.relay.pair_status(pair["id"], token)["state"])

    def test_first_start_pins_second_user(self):
        self.update("user_two", 222, "/start")
        token, pair = self.begin("user_two")
        self.update("user_two", 999, "/start c_" + pair["id"])
        self.assertEqual("waiting", self.relay.pair_status(pair["id"], token)["state"])

    def test_group_and_sender_mismatch_ignored(self):
        token, pair = self.begin()
        self.update("user_one", 111, "/start c_" + pair["id"], chat={"id": -111, "type": "group"})
        self.update("user_one", 111, "/start c_" + pair["id"], chat={"id": 222, "type": "private"})
        self.assertEqual("waiting", self.relay.pair_status(pair["id"], token)["state"])

    def test_expiry(self):
        token, pair = self.begin()
        self.now += 601
        self.update("user_one", 111, "/start c_" + pair["id"])
        with self.assertRaises(Problem):
            self.relay.pair_status(pair["id"], token)
        with self.assertRaises(Problem):
            self.relay.send(token, self.message())

    def test_stop_revokes_only_sender(self):
        a, _ = self.connect()
        b, _ = self.connect("user_two", 222)
        self.update("user_one", 111, "/stop")
        with self.assertRaises(Problem):
            self.relay.send(a, self.message())
        self.assertEqual("sent", self.relay.send(b, self.message())["state"])

    def test_client_cannot_set_destination(self):
        token, _ = self.connect()
        with self.assertRaises(Problem):
            self.relay.send(token, {**self.message(), "chat_id": 222})
        self.assertEqual([], self.tg.calls)

    def test_hash_and_pair_id_not_device_credentials(self):
        token, pair = self.connect()
        for wrong in (pair["id"], digest(token), secrets.token_urlsafe(32), ""):
            with self.assertRaises(Problem):
                self.relay.send(wrong, self.message())

    def test_idempotency_and_content_conflict(self):
        token, _ = self.connect()
        body = self.message()
        self.assertEqual("sent", self.relay.send(token, body)["state"])
        self.assertEqual("sent", self.relay.send(token, body)["state"])
        self.assertEqual(1, len(self.tg.calls))
        with self.assertRaises(Problem):
            self.relay.send(token, {**body, "text": "changed"})

    def test_minimum_interval_all_devices_same_chat(self):
        a, _ = self.connect()
        b, _ = self.connect()
        self.relay.send(a, self.message())
        with self.assertRaises(Problem) as error:
            self.relay.send(b, self.message())
        self.assertEqual(429, error.exception.code)
        self.now += 2
        self.assertEqual("sent", self.relay.send(b, self.message())["state"])

    def test_telegram_429_and_retry_same_id(self):
        token, _ = self.connect()
        body = self.message()
        self.tg.error = Problem(429, "Slow down", 9)
        with self.assertRaises(Problem) as error:
            self.relay.send(token, body)
        self.assertEqual(9, error.exception.retry)
        self.now += 8
        with self.assertRaises(Problem):
            self.relay.send(token, body)
        self.now += 1
        self.tg.error = None
        self.assertEqual("sent", self.relay.send(token, body)["state"])

    def test_network_ambiguity_does_not_resend(self):
        token, _ = self.connect()
        body = self.message()
        self.tg.error = Problem(502, "Timeout")
        self.assertEqual("unknown", self.relay.send(token, body)["state"])
        self.tg.error = None
        self.now += 20
        self.assertEqual("unknown", self.relay.send(token, body)["state"])
        self.assertEqual(1, len(self.tg.calls))

    def test_pending_limit_and_uninvited_user(self):
        with self.assertRaises(Problem):
            self.begin("outsider")
        for _ in range(10):
            self.begin()
        with self.assertRaises(Problem) as error:
            self.begin()
        self.assertEqual(429, error.exception.code)

    def test_restart_preserves_devices_and_ambiguous_delivery(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / "relay.db")
            r = Relay(path, {"user_one": 111}, self.tg, "Example_bot", lambda: self.now)
            token = secrets.token_urlsafe(32)
            r.db.execute("INSERT INTO devices VALUES (?,?,?,?,0)", (digest(token), 111, "user_one", "PC"))
            body = self.message()
            import hashlib
            r.db.execute("INSERT INTO messages VALUES (?,?,?,?,?)", (digest(token), body["id"], hashlib.sha256(body["text"].encode()).hexdigest(), "sending", self.now))
            r.db.commit()
            r.db.close()
            r = Relay(path, {"user_one": 111}, self.tg, "Example_bot", lambda: self.now)
            self.assertEqual("unknown", r.send(token, body)["state"])
            self.assertEqual([], self.tg.calls)
            r.db.close()

    def test_http_requires_auth_and_rejects_bad_json(self):
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(self.relay))
        thread = threading.Thread(target=server.serve_forever)
        thread.start()
        url = "http://127.0.0.1:" + str(server.server_port)
        try:
            for payload, expected in ((self.message(), 401), ([], 400)):
                req = urllib.request.Request(url + "/v1/messages", json.dumps(payload).encode(), {"Content-Type": "application/json"})
                with self.assertRaises(urllib.error.HTTPError) as error:
                    urllib.request.urlopen(req)
                self.assertEqual(expected, error.exception.code)
                error.exception.close()
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == "__main__":
    unittest.main()

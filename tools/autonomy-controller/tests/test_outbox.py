import unittest

from ashlar_controller import outbox

from .helpers import ControllerCase


class Outbox(ControllerCase):
    def setUp(self):
        super().setUp()
        self.s = self.init_store()

    def enqueue(self, key="claim:card-755", body="From: claude\nKind: claim\nAbout: card-755\n\nTaking it."):
        return outbox.enqueue(self.s, dedup_key=key, kind="claim", target="issue:695", slug="card-755", body=body)

    def test_enqueue_is_idempotent(self):
        self.assertTrue(self.enqueue())
        self.assertFalse(self.enqueue())
        self.assertEqual(len(outbox.pending(self.s)), 1)

    def test_flush_posts_once_with_the_marker(self):
        self.enqueue()
        poster = outbox.FakePoster()
        self.assertEqual(outbox.flush(self.s, poster)["sent"], 1)
        self.assertEqual(outbox.flush(self.s, poster)["sent"], 0)
        self.assertEqual(len(poster.posts), 1)
        self.assertIn(outbox.marker("claim:card-755"), poster.posts[0]["body"])
        self.assertEqual(self.s.row("SELECT state, remote_id FROM outbox")["state"], "sent")

    def test_poster_failure_defers_and_a_later_flush_succeeds(self):
        self.enqueue()
        poster = outbox.FakePoster(fail_times=1)
        self.assertEqual(outbox.flush(self.s, poster)["deferred"], 1)
        row = self.s.row("SELECT state, attempts, last_error FROM outbox")
        self.assertEqual((row["state"], row["attempts"]), ("pending", 1))
        self.assertEqual(outbox.flush(self.s, poster)["sent"], 1)
        self.assertEqual(len(poster.posts), 1)

    def test_interrupted_send_is_reconciled_not_repeated(self):
        self.enqueue()
        poster = outbox.FakePoster()
        outbox.flush(self.s, poster)
        # The post happened but the controller died before marking it sent.
        with self.s.tx():
            self.s.conn.execute("UPDATE outbox SET state='sending', remote_id=NULL, sent_at=NULL")
        counts = outbox.reconcile(self.s, poster)
        self.assertEqual(counts, {"recovered_sent": 1, "requeued": 0})
        self.assertEqual(outbox.flush(self.s, poster)["sent"], 0)
        self.assertEqual(len(poster.posts), 1)

    def test_interrupted_send_that_never_landed_is_requeued_and_sent_once(self):
        self.enqueue()
        with self.s.tx():
            self.s.conn.execute("UPDATE outbox SET state='sending', attempts=1")
        poster = outbox.FakePoster()
        self.assertEqual(outbox.reconcile(self.s, poster), {"recovered_sent": 0, "requeued": 1})
        self.assertEqual(outbox.flush(self.s, poster)["sent"], 1)
        self.assertEqual(len(poster.posts), 1)

    def test_too_many_attempts_fails_the_row(self):
        self.enqueue()
        poster = outbox.FakePoster(fail_times=outbox.MAX_ATTEMPTS)
        for _ in range(outbox.MAX_ATTEMPTS):
            outbox.flush(self.s, poster)
        self.assertEqual(outbox.flush(self.s, poster)["failed"], 1)
        self.assertEqual(self.s.row("SELECT state FROM outbox")["state"], "failed")
        self.assertEqual(len(poster.posts), 0)


if __name__ == "__main__":
    unittest.main()

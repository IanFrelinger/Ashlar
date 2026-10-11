import os
import subprocess
import sys
import unittest

from ashlar_controller.models import LeaseError, StopReason

from .helpers import ControllerCase


class WriterLease(ControllerCase):
    def setUp(self):
        super().setUp()
        self.s = self.init_store()
        self.t0 = self.clock()

    def test_free_lease_is_taken_and_renewed(self):
        self.s.acquire_lease("a", 300, now=self.t0, host="h1", pid=1)
        self.assertTrue(self.s.holds_lease("a", now=self.t0 + 100))
        self.s.acquire_lease("a", 300, now=self.t0 + 250, host="h1", pid=1)
        self.assertTrue(self.s.holds_lease("a", now=self.t0 + 500))

    def test_live_lease_refuses_another_holder(self):
        self.s.acquire_lease("a", 300, now=self.t0, host="h1", pid=1)
        with self.assertRaises(LeaseError) as cm:
            self.s.acquire_lease("b", 300, now=self.t0 + 10, host="h2", pid=2)
        self.assertIn("held by a", str(cm.exception))

    def test_expired_lease_on_another_host_is_not_taken_until_confirmed(self):
        self.s.acquire_lease("a", 300, now=self.t0, host="h1", pid=1)
        with self.assertRaises(LeaseError) as cm:
            self.s.acquire_lease("b", 300, now=self.t0 + 10_000, host="h2", pid=2)
        self.assertIn("not confirmed stopped", str(cm.exception))
        self.assertEqual(self.s.lease_row()["holder_id"], "a")
        self.s.break_lease("a", confirmed_by="operator", now=self.t0 + 10_000)
        self.s.acquire_lease("b", 300, now=self.t0 + 10_001, host="h2", pid=2)
        self.assertTrue(self.s.holds_lease("b", now=self.t0 + 10_002))

    def test_breaking_an_unexpired_lease_is_refused(self):
        self.s.acquire_lease("a", 300, now=self.t0, host="h1", pid=1)
        with self.assertRaises(LeaseError):
            self.s.break_lease("a", confirmed_by="operator", now=self.t0 + 10)

    def test_expired_lease_with_dead_pid_on_this_host_is_taken(self):
        proc = subprocess.Popen([sys.executable, "-c", "pass"])
        proc.wait()
        dead_pid = proc.pid
        self.s.acquire_lease("a", 300, now=self.t0, host="h1", pid=dead_pid)
        self.s.acquire_lease("b", 300, now=self.t0 + 10_000, host="h1", pid=os.getpid())
        self.assertTrue(self.s.holds_lease("b", now=self.t0 + 10_001))

    def test_expired_lease_with_live_pid_on_this_host_is_refused(self):
        self.s.acquire_lease("a", 300, now=self.t0, host="h1", pid=os.getpid())
        with self.assertRaises(LeaseError):
            self.s.acquire_lease("b", 300, now=self.t0 + 10_000, host="h1", pid=os.getpid() + 1)

    def test_released_lease_is_free(self):
        self.s.acquire_lease("a", 300, now=self.t0, host="h1", pid=1)
        self.s.release_lease("a", now=self.t0 + 1)
        self.s.acquire_lease("b", 300, now=self.t0 + 2, host="h2", pid=2)
        self.assertTrue(self.s.holds_lease("b", now=self.t0 + 3))

    def test_tick_without_the_lease_launches_nothing(self):
        self.s.acquire_lease("other", 300, now=self.t0, host="elsewhere", pid=7)
        ctl = self.make_controller(store=self.s, holder="me", host="here", pid=8)
        self.add_task()
        report = ctl.tick()
        self.assertEqual(report.action, "refused")
        self.assertEqual(report.reason, StopReason.LEASE_NOT_HELD)
        self.assertEqual(self.adapter_calls(), 0)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM runs")["n"], 0)


if __name__ == "__main__":
    unittest.main()

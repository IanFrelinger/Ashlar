import unittest

from ashlar_controller.models import RunState, RunStatus, StopReason, TaskState

from .helpers import ControllerCase


class EventDeduplication(ControllerCase):
    def setUp(self):
        super().setUp()
        self.make_controller()

    def on_card_ready(self, event_id):
        if self.ctl.ingest_event(event_id, "github", "card-ready", "t1", {"card": 755}):
            self.add_task()
            return True
        return False

    def test_duplicate_events_do_not_allocate_duplicate_workers(self):
        finished = self.adapter.default_status
        self.adapter.default_status = RunStatus(state=RunState.RUNNING)
        self.assertTrue(self.on_card_ready("evt-1"))
        self.assertEqual(self.ctl.tick().action, "launched")
        self.assertFalse(self.on_card_ready("evt-1"))          # redelivered while running
        self.assertEqual(self.ctl.tick().reason, StopReason.WRITER_BUSY)
        self.assertEqual(self.adapter.launch_count, 1)
        self.adapter.default_status = finished
        self.assertEqual(self.ctl.tick().action, "settled")    # fake finished; settled; nothing new
        self.assertEqual(self.task()["state"], TaskState.DONE)
        self.assertFalse(self.on_card_ready("evt-1"))          # redelivered after completion
        self.assertEqual(self.ctl.tick().action, "idle")
        self.assertEqual(self.adapter.launch_count, 1)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM runs")["n"], 1)
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM events")["n"], 1)

    def test_out_of_order_and_repeated_deliveries_collapse_to_one_record_each(self):
        ids = ["e3", "e1", "e2", "e1", "e3", "e2", "e2"]
        new = [self.ctl.ingest_event(i, "github", "pr-ci", None) for i in ids]
        self.assertEqual(new, [True, True, True, False, False, False, False])
        self.assertEqual(self.store.row("SELECT COUNT(*) AS n FROM events")["n"], 3)

    def test_last_event_is_recorded_on_the_task(self):
        self.add_task()
        self.ctl.ingest_event("evt-9", "github", "pr-review", "t1")
        self.assertEqual(self.task()["last_event"], "pr-review:evt-9")


if __name__ == "__main__":
    unittest.main()

"""Offline tests for scripts/governance/sync_remote.py. No network, no GitHub token, no secret: gh is replaced by fakes."""
import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts" / "governance"))
import sync_remote as sr  # noqa: E402
import validate_register as vr  # noqa: E402

REG = json.loads((ROOT / "agentic" / "_tasks" / "register.json").read_text(encoding="utf-8"))


def task(reg, legacy):
    return next(t for t in reg["tasks"] if t["legacy_id"] == legacy)


def baselined(reg):
    """Return a copy whose sync baseline equals the desired board status: independent of the live register's pending updates."""
    reg = copy.deepcopy(reg)
    for t in reg["tasks"]:
        if t.get("project_item"):
            t["sync"] = {"status": sr.desired(t, reg)["status"], "verified_at": "T0"}
    return reg


def remote_of(reg):
    """A remote snapshot that matches the register exactly (the 19 linked tasks)."""
    items, issues = [], []
    for t in reg["tasks"]:
        if not t.get("project_item"):
            continue
        want = sr.desired(t, reg)
        items.append({"id": t["project_item"], "title": vr.task_title(t), "kind": "Issue" if t["remote_kind"] == "issue" else "DraftIssue",
                      "issue": t["issue"], **{k: want.get(k) for k in ("status", "priority", "area", "milestone", "evidence", "effort")}})
        if t["remote_kind"] == "issue":
            issues.append({"number": t["issue"], "title": vr.task_title(t), "state": "closed" if t["status"] == "DONE" else "open",
                           "body": f"**Status in the register:** X (`{t['status']}`)"})
    return {"items": items, "issues": issues}


class PlannerBase(unittest.TestCase):
    def setUp(self):
        self.reg = baselined(REG)
        self.remote = remote_of(self.reg)

    def item(self, legacy):
        pid = task(self.reg, legacy)["project_item"]
        return next(i for i in self.remote["items"] if i["id"] == pid)

    def plan(self, **kw):
        return sr.plan(self.reg, self.remote, **kw)


class Planner(PlannerBase):
    def test_a_register_matching_the_remote_is_clean(self):
        p = self.plan()
        self.assertEqual({"updates": [], "conflicts": [], "reports": [], "pending_register": []}, p)
        self.assertTrue(sr.is_clean(p))

    def test_the_register_moved_so_the_remote_is_updated(self):
        task(self.reg, "AF-EVID-01")["status"] = "BLOCKED"
        task(self.reg, "AF-EVID-01")["blocked_reason"] = "x"
        p = self.plan()
        self.assertEqual([("Status", "Backlog", "Blocked")], [(u["field"], u["old"], u["new"]) for u in p["updates"]])
        self.assertEqual([], p["conflicts"])

    def test_a_person_moved_the_card_so_it_is_a_conflict_not_an_overwrite(self):
        self.item("AF-EVID-01")["status"] = "In Progress"
        p = self.plan()
        self.assertEqual([], p["updates"])
        self.assertEqual("Status", p["conflicts"][0]["what"])

    def test_both_sides_changed_is_a_conflict(self):
        task(self.reg, "AF-EVID-01").update(status="BLOCKED", blocked_reason="x")
        self.item("AF-EVID-01")["status"] = "Deferred"
        p = self.plan()
        self.assertEqual([], p["updates"])
        self.assertEqual(1, len(p["conflicts"]))

    def test_without_a_baseline_a_difference_is_a_conflict(self):
        task(self.reg, "AF-EVID-01")["sync"] = None
        self.item("AF-EVID-01")["status"] = "Ready"
        self.assertIn("no baseline", self.plan()["conflicts"][0]["detail"])

    def test_remote_already_matches_but_the_baseline_is_stale_is_a_pending_register_update(self):
        task(self.reg, "AF-EVID-01").update(status="BLOCKED", blocked_reason="x")
        self.item("AF-EVID-01")["status"] = "Blocked"
        p = self.plan(now="T")
        self.assertEqual([{"task": "BOPS-0080", "sync": {"status": "Blocked", "verified_at": "T"}}], p["pending_register"])
        self.assertFalse(sr.is_clean(p))

    def test_empty_remote_fields_are_filled_and_differing_ones_are_never_overwritten(self):
        self.item("AF-EVID-01")["priority"] = None
        self.item("AF-EVID-01")["area"] = "Quality"
        p = self.plan()
        self.assertEqual([("Priority", None, "P2")], [(u["field"], u["old"], u["new"]) for u in p["updates"]])
        self.assertEqual("Area", p["conflicts"][0]["what"])

    def test_the_default_evidence_level_none_is_filled_not_treated_as_a_conflict(self):
        task(self.reg, "AF-EVID-01")["validation"]["achieved"] = ["OFFLINE_TESTS"]
        self.assertEqual([("Evidence Level", "None", "Offline tests")], [(u["field"], u["old"], u["new"]) for u in self.plan()["updates"]])
        self.assertEqual([], self.plan()["conflicts"])

    def test_priority_and_milestone_come_from_the_register_only(self):
        t = task(self.reg, "AF-EVID-01")
        t["priority"], t["milestone"] = "P0", "BENCH"
        p = self.plan()
        self.assertEqual({"Priority", "bOps Milestone"}, {c["what"] for c in p["conflicts"]})
        self.assertEqual([], p["updates"])

    def test_a_done_task_is_closed_only_when_asked(self):
        t = task(self.reg, "F-26")
        t.update(status="DONE")
        t["validation"].update(achieved=t["validation"]["required"], open_gates=[])
        self.item("F-26")["status"] = "Validation"
        p = self.plan()
        self.assertEqual("Status", p["updates"][0]["field"])
        self.assertEqual("pending-close", next(r["kind"] for r in p["reports"] if r["kind"] == "pending-close"))
        self.assertIn("IssueState", [u["field"] for u in self.plan(close_done=True)["updates"]])

    def test_a_closed_issue_of_an_unfinished_task_is_a_conflict_and_is_not_reopened(self):
        self.remote["issues"][0]["state"] = "closed"
        p = self.plan()
        self.assertEqual("IssueState", p["conflicts"][0]["what"])
        self.assertNotIn("IssueState", [u["field"] for u in p["updates"]])

    def test_duplicates_unknown_and_vanished_items_are_reported_and_never_created(self):
        self.remote["items"].append({"id": "PVTI_dup", "title": "BOPS-0080 | AF-EVID-01 — again", "kind": "DraftIssue", "issue": None})
        self.remote["items"].append({"id": "PVTI_new", "title": "BOPS-0999 | NEW — x", "kind": "DraftIssue", "issue": None})
        self.remote["items"] = [i for i in self.remote["items"] if i["id"] != task(self.reg, "LIVE-02")["project_item"]]
        kinds = sorted(r["kind"] for r in self.plan()["reports"])
        self.assertEqual(["duplicate", "unknown-remote", "vanished"], kinds)

    def test_a_draft_registered_as_an_issue_is_a_kind_conflict(self):
        task(self.reg, "LIVE-02")["remote_kind"] = "issue"
        self.assertEqual("kind", self.plan()["conflicts"][0]["what"])

    def test_a_renamed_title_is_a_conflict(self):
        self.item("AF-EVID-01")["title"] = "BOPS-0080 | AF-EVID-01 — renamed by hand"
        self.assertEqual("title", self.plan()["conflicts"][0]["what"])

    def test_a_stale_issue_body_is_reported_not_rewritten(self):
        self.reg_issue = task(self.reg, "AF-EVID-01")["issue"]
        next(i for i in self.remote["issues"] if i["number"] == self.reg_issue)["body"] = "**Status in the register:** Ready (`READY`)"
        p = self.plan()
        self.assertEqual("body-stale", p["reports"][0]["kind"])
        self.assertTrue(sr.is_clean(p))


class FakeClient:
    """In-memory GitHub: records every write; edits change the remote like GitHub would."""
    def __init__(self, remote, login="babbubba", graphql_left=5000, between=None, fail_at=None, fail_applied=False):
        self.remote, self.login, self.left = remote, login, graphql_left
        self.writes, self.sleeps, self.delay = [], [], 0
        self.sleep = self.sleeps.append
        self.between, self.fail_at, self.fail_applied, self.attempts = between, fail_at, fail_applied, 0

    def guard_account(self):
        if self.login != "babbubba":
            raise sr.SyncAbort("wrong account")

    def preflight(self):
        if self.left < 200:
            raise sr.RateLimited("low")

    def fetch(self):
        snap = copy.deepcopy(self.remote)
        if self.between:  # someone edits GitHub after the plan's snapshot and before the writes
            hook, self.between = self.between, None
            hook(self.remote)
        return snap

    def read_item(self, item):
        return copy.deepcopy(next(i for i in self.remote["items"] if i["id"] == item))

    def read_issue(self, number):
        return copy.deepcopy(next(i for i in self.remote["issues"] if i["number"] == number))

    def _write(self, record, apply):
        self.attempts += 1
        failing = self.attempts == self.fail_at
        if failing and not self.fail_applied:
            raise sr.SyncAbort("transport lost")
        self.writes.append(record)
        apply()
        if failing:
            raise sr.SyncAbort("transport lost after the server applied it")

    def edit_field(self, item, name, value):
        key = {v: k for k, v in sr.FIELDS.items()}.get(name, "status")
        self._write((item, name, value), lambda: next(i for i in self.remote["items"] if i["id"] == item).__setitem__(key, value))

    def close_issue(self, number):
        self._write(("issue", number), lambda: next(i for i in self.remote["issues"] if i["number"] == number).__setitem__("state", "closed"))


class RunsBase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / "agentic" / "_tasks").mkdir(parents=True)
        self.reg = baselined(REG)
        task(self.reg, "AF-EVID-01").update(status="BLOCKED", blocked_reason="waiting")
        self.write_register()
        self.remote = remote_of(baselined(REG))
        # copy the files the validator checks
        for rel in {t["packet"] for t in REG["tasks"] if t["packet"]} | {t["spec"].split("#")[0] for t in REG["tasks"] if t["spec"]} | {c for t in REG["tasks"] for c in t["companion_docs"]}:
            dst = self.root / rel
            dst.parent.mkdir(parents=True, exist_ok=True)
            dst.write_text("x", encoding="utf-8")
        qm = self.root / "agentic" / "_quality"
        qm.mkdir(parents=True)
        (qm / "active-milestone.json").write_text(json.dumps({"milestone": "V1.3-H"}), encoding="utf-8")
        (self.root / "HANDOFF.md").write_text((ROOT / "HANDOFF.md").read_text(encoding="utf-8"), encoding="utf-8")

    def tearDown(self):
        self.tmp.cleanup()

    def write_register(self):
        (self.root / "agentic" / "_tasks" / "register.json").write_text(json.dumps(self.reg), encoding="utf-8")

    def run_main(self, client, *args):
        return sr.main(["--root", str(self.root), *args], client=client)


class Runs(RunsBase):
    def test_dry_run_writes_nothing_and_reports_differences(self):
        c = FakeClient(self.remote)
        self.assertEqual(1, self.run_main(c))
        self.assertEqual([], c.writes)

    def test_apply_updates_rereads_and_is_idempotent(self):
        c = FakeClient(self.remote)
        self.assertEqual(1, self.run_main(c, "--apply"))  # pending register update remains
        self.assertEqual([(task(self.reg, "AF-EVID-01")["project_item"], "Status", "Blocked")], c.writes)
        n = len(c.writes)
        self.run_main(c, "--apply")  # second run: nothing left to write
        self.assertEqual(n, len(c.writes))

    def test_conflicts_exit_2_and_write_only_the_safe_updates(self):
        self.remote["items"][0]["status"] = "Done"
        c = FakeClient(self.remote)
        self.assertEqual(2, self.run_main(c, "--apply"))
        self.assertTrue(all(w[0] != self.remote["items"][0]["id"] for w in c.writes))

    def test_the_wrong_account_aborts_before_any_read(self):
        c = FakeClient(self.remote, login="fcavallari-italcom")
        self.assertEqual(3, self.run_main(c, "--apply"))
        self.assertEqual([], c.writes)

    def test_a_low_graphql_budget_aborts(self):
        self.assertEqual(3, self.run_main(FakeClient(self.remote, graphql_left=10), "--apply"))

    def test_an_invalid_register_is_never_synchronized(self):
        task(self.reg, "AF-01")["status"] = "FINISHED"
        self.write_register()
        c = FakeClient(self.remote)
        self.assertEqual(3, self.run_main(c, "--apply"))
        self.assertEqual([], c.writes)

    def test_apply_register_patches_only_the_local_baseline(self):
        c = FakeClient(self.remote)
        self.run_main(c, "--apply", "--apply-register")
        saved = json.loads((self.root / "agentic" / "_tasks" / "register.json").read_text(encoding="utf-8"))
        self.assertEqual("Blocked", task(saved, "AF-EVID-01")["sync"]["status"])
        self.assertEqual("BLOCKED", task(saved, "AF-EVID-01")["status"])


class Hardening(RunsBase):
    LEGACY = ("AF-EVID-01", "PLAN-FIDELITY-01", "HOST-SATURATION-01")

    def setUp(self):
        super().setUp()
        for legacy in self.LEGACY:
            task(self.reg, legacy).update(status="BLOCKED", blocked_reason="waiting")
        self.write_register()
        self.ids = [task(self.reg, legacy)["project_item"] for legacy in self.LEGACY]

    def status(self, item_id):
        return next(i for i in self.remote["items"] if i["id"] == item_id)["status"]

    def run_captured(self, client, *args):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = self.run_main(client, *args)
        return rc, buf.getvalue()

    def test_a_failure_after_a_successful_write_reports_written_uncertain_and_not_run(self):
        c = FakeClient(self.remote, fail_at=2)
        rc, out = self.run_captured(c, "--apply")
        self.assertEqual(3, rc)
        self.assertEqual(1, len(c.writes))
        self.assertIn("WRITTEN", out)
        self.assertIn("UNCERTAIN", out)
        self.assertIn("NOT RUN", out)
        self.assertIn("NOT synchronized", out)
        self.assertNotIn("all updates verified", out)

    def test_a_concurrent_change_between_plan_and_write_is_a_conflict_and_not_overwritten(self):
        target = self.ids[0]
        c = FakeClient(self.remote, between=lambda r: next(i for i in r["items"] if i["id"] == target).update(status="Deferred"))
        rc, out = self.run_captured(c, "--apply")
        self.assertEqual(2, rc)
        self.assertNotIn(target, [w[0] for w in c.writes])
        self.assertEqual("Deferred", self.status(target))
        self.assertIn("CONFLICT", out)

    def test_a_remote_value_already_equal_to_the_desired_one_is_not_written(self):
        target = self.ids[0]
        c = FakeClient(self.remote, between=lambda r: next(i for i in r["items"] if i["id"] == target).update(status="Blocked"))
        rc, out = self.run_captured(c, "--apply")
        self.assertNotIn(target, [w[0] for w in c.writes])
        self.assertIn("SATISFIED", out)
        self.assertEqual(2, len(c.writes))

    def test_a_rerun_after_a_partial_apply_reconciles_without_duplicating_writes(self):
        c = FakeClient(self.remote, fail_at=2, fail_applied=True)  # the 2nd write lands but the caller sees an error
        self.assertEqual(3, self.run_captured(c, "--apply")[0])
        self.assertEqual(2, len(c.writes))
        rc, out = self.run_captured(c)  # dry-run: sees the real state
        self.assertEqual(2, len(c.writes))
        self.assertEqual(1, out.count("UPDATE"))  # only the third write is still owed
        self.run_captured(c, "--apply")
        self.assertEqual(3, len(c.writes))
        self.assertEqual(3, len({w[0] for w in c.writes}))
        self.assertEqual(["Blocked"] * 3, [self.status(i) for i in self.ids])


class Identity(PlannerBase):
    def setUp(self):
        super().setUp()
        self.t = task(self.reg, "F-26")
        self.t.update(status="DONE")
        self.t["validation"].update(achieved=self.t["validation"]["required"], open_gates=[])
        self.item("F-26")["status"] = "Validation"

    def mine(self, p):
        return [u for u in p["updates"] if u["task"] == self.t["id"]]

    def test_a_project_item_linked_to_a_different_issue_blocks_every_mutation_for_the_task(self):
        self.item("F-26")["issue"] = 9999
        p = self.plan(close_done=True)
        self.assertEqual("identity", p["conflicts"][0]["what"])
        self.assertEqual([], self.mine(p))

    def test_an_issue_with_another_title_blocks_every_mutation_for_the_task(self):
        next(i for i in self.remote["issues"] if i["number"] == self.t["issue"])["title"] = "BOPS-0001 | someone else"
        p = self.plan(close_done=True)
        self.assertEqual("identity", p["conflicts"][0]["what"])
        self.assertEqual([], self.mine(p))

    def test_closing_is_never_planned_when_the_remote_status_conflicts(self):
        self.item("F-26")["status"] = "Deferred"
        p = self.plan(close_done=True)
        self.assertEqual("Status", p["conflicts"][0]["what"])
        self.assertNotIn("IssueState", [u["field"] for u in p["updates"]])

    def test_closing_is_planned_only_when_the_gates_are_met(self):
        self.assertIn("IssueState", [u["field"] for u in self.plan(close_done=True)["updates"]])
        self.t["validation"]["achieved"] = []
        self.assertNotIn("IssueState", [u["field"] for u in self.plan(close_done=True)["updates"]])

    def test_at_write_time_the_issue_is_not_closed_while_the_remote_status_is_not_done(self):
        c = FakeClient(self.remote)
        u = {"task": self.t["id"], "item": self.t["project_item"], "issue": self.t["issue"], "title": vr.task_title(self.t),
             "field": "IssueState", "old": "open", "new": "closed"}
        ledger = [{"op": "close", "state": "pending"}]
        sr.apply_updates(c, {"updates": [u]}, ledger)
        self.assertEqual("deferred", ledger[0]["state"])
        self.assertEqual([], c.writes)
        self.item("F-26")["status"] = "Done"
        ledger = [{"op": "close", "state": "pending"}]
        sr.apply_updates(c, {"updates": [u]}, ledger)
        self.assertEqual("done", ledger[0]["state"])
        self.assertEqual([("issue", self.t["issue"])], c.writes)


class Transport(unittest.TestCase):
    def test_issues_are_read_across_two_slurped_pages_without_pull_requests(self):
        def issue(n):
            return {"number": n, "title": f"t{n}", "state": "open", "body": None}
        page1 = [issue(n) for n in range(1, 101)]
        page2 = [issue(n) for n in range(101, 121)] + [{**issue(500), "pull_request": {}}]
        seen = []

        def runner(args):
            seen.append(args)
            if args[0] == "project":
                return 0, json.dumps({"items": []}), ""
            return 0, json.dumps([page1, page2]), ""
        issues = sr.GhClient(runner=runner, delay=0).fetch()["issues"]
        self.assertEqual(list(range(1, 121)), [i["number"] for i in issues])
        self.assertIn("--slurp", next(a for a in seen if a[0] == "api"))

    def test_concatenated_pages_without_slurp_are_decoded_too(self):
        text = json.dumps([{"number": 1}]) + "\n" + json.dumps([{"number": 2}, {"number": 3}])
        self.assertEqual([1, 2, 3], [x["number"] for x in sr.parse_pages(text)])

    def client(self, replies, sleeps):
        it = iter(replies)
        return sr.GhClient(runner=lambda a: next(it), sleep=sleeps.append, retries=3, backoff=10, delay=0)

    def test_a_secondary_rate_limit_is_retried_with_backoff_then_succeeds(self):
        s = []
        c = self.client([(1, "", "API rate limit already exceeded"), (1, "", "secondary rate limit"), (0, "ok", "")], s)
        self.assertEqual("ok", c.call(["api", "x"]))
        self.assertEqual([10, 20], s)

    def test_a_persistent_rate_limit_aborts_without_claiming_success(self):
        s = []
        c = self.client([(1, "", "secondary rate limit")] * 5, s)
        with self.assertRaises(sr.RateLimited):
            c.call(["api", "x"])
        self.assertEqual([10, 20, 40], s)

    def test_other_errors_are_not_retried(self):
        s = []
        c = self.client([(1, "", "HTTP 404 Not Found")], s)
        with self.assertRaises(sr.SyncAbort):
            c.call(["api", "x"])
        self.assertEqual([], s)

    def test_the_account_guard_and_budget_preflight(self):
        c = self.client([(0, "fcavallari-italcom\n", "")], [])
        with self.assertRaises(sr.SyncAbort):
            c.guard_account()
        low = json.dumps({"resources": {"graphql": {"remaining": 5, "reset": 0}}})
        with self.assertRaises(sr.RateLimited):
            self.client([(0, low, ""), (0, "{}", "")], []).preflight()

    def test_a_secondary_limit_with_a_healthy_budget_is_told_apart_from_a_low_budget(self):
        ok = json.dumps({"resources": {"graphql": {"remaining": 5000, "reset": 0}}})
        with self.assertRaises(sr.RateLimited) as cm:
            self.client([(0, ok, ""), (1, "", "API rate limit already exceeded for user ID 1")], []).preflight()
        self.assertIn("secondary", str(cm.exception))

    def test_gh_unknown_owner_type_is_confirmed_as_a_rate_limit_by_a_probe(self):
        with self.assertRaises(sr.RateLimited):
            self.client([(1, "", "unknown owner type"), (1, "", "API rate limit already exceeded")], []).call(["project", "item-list"])
        with self.assertRaises(sr.SyncAbort) as cm:
            self.client([(1, "", "unknown owner type"), (0, "{}", "")], []).call(["project", "item-list"])
        self.assertNotIsInstance(cm.exception, sr.RateLimited)

    def test_tokens_from_the_environment_are_never_passed_to_gh(self):
        import os
        seen = {}

        def fake_run(cmd, **kw):
            seen.update(kw["env"])

            class R:
                returncode, stdout, stderr = 0, "", ""
            return R()
        real, sr.subprocess.run = sr.subprocess.run, fake_run
        os.environ["GITHUB_TOKEN"] = "x"
        try:
            sr.default_runner(["api", "user"])
        finally:
            sr.subprocess.run = real
            del os.environ["GITHUB_TOKEN"]
        self.assertNotIn("GITHUB_TOKEN", seen)
        self.assertNotIn("GH_TOKEN", seen)


if __name__ == "__main__":
    unittest.main()

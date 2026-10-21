"""Offline tests for scripts/governance/validate_register.py. No network, no GitHub token, no secret."""
import copy
import json
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts" / "governance"))
import validate_register as vr  # noqa: E402

REGISTER = json.loads((ROOT / "agentic" / "_tasks" / "register.json").read_text(encoding="utf-8"))
HANDOFF = (ROOT / "HANDOFF.md").read_text(encoding="utf-8")


def task(reg, legacy):
    return next(t for t in reg["tasks"] if t["legacy_id"] == legacy)


class RealRegister(unittest.TestCase):
    def test_the_committed_register_and_handoff_are_consistent(self):
        self.assertEqual([], vr.validate(REGISTER, ROOT, HANDOFF))

    def test_ids_are_contiguous_and_chronological(self):
        ids = [t["id"] for t in REGISTER["tasks"]]
        self.assertEqual([f"BOPS-{i:04d}" for i in range(1, len(ids) + 1)], ids)
        dated = [t["created"]["at"] for t in REGISTER["tasks"] if len(t["created"]["at"]) > 10]
        from datetime import datetime
        stamps = [datetime.fromisoformat(d).timestamp() for d in dated]
        self.assertEqual(sorted(stamps), stamps)

    def test_no_functional_task_is_in_progress_without_authorization(self):
        # PROCESS.md section 3 allows several tasks in progress, each authorized and on its own branch.
        active = [t for t in REGISTER["tasks"] if t["status"] == "IN_PROGRESS"]
        self.assertEqual([], [t["id"] for t in active if t.get("authorized") is not True or not t.get("branch")])
        branches = [t["branch"] for t in active]
        self.assertEqual(len(branches), len(set(branches)))

    def test_the_frozen_mapping_keeps_verified_and_conventional_order_apart(self):
        kinds = {t["created"]["order_certainty"] for t in REGISTER["tasks"]}
        self.assertEqual({"verified", "conventional"}, kinds)
        self.assertEqual("FROZEN", REGISTER["numbering"]["state"])

    def test_every_issue_title_starts_with_the_progressive(self):
        for t in REGISTER["tasks"]:
            self.assertTrue(vr.task_title(t).startswith(f'{t["id"]} | {t["legacy_id"]}'))


class Mutations(unittest.TestCase):
    def setUp(self):
        self.reg = copy.deepcopy(REGISTER)

    def errors(self, handoff=HANDOFF):
        return vr.validate(self.reg, ROOT, handoff)

    def assertFlags(self, fragment, handoff=HANDOFF):
        errs = self.errors(handoff)
        self.assertTrue(any(fragment in e for e in errs), f"expected {fragment!r} in {errs}")

    def test_duplicate_progressive_id(self):
        self.reg["tasks"][5]["id"] = self.reg["tasks"][4]["id"]
        self.assertFlags("duplicate progressive id")

    def test_gap_in_ids_means_a_task_was_deleted(self):
        del self.reg["tasks"][10]
        self.assertFlags("contiguous")

    def test_next_number_must_follow_the_last_id(self):
        self.reg["numbering"]["next"] = 5
        self.assertFlags("numbering.next")

    def test_a_frozen_mapping_cannot_be_renumbered_or_renamed(self):
        self.assertEqual("FROZEN", self.reg["numbering"]["state"])
        task(self.reg, "AF-01")["legacy_id"] = "AF-ONE"
        self.assertFlags("frozen id mapping")

    def test_a_provisional_mapping_is_not_hash_checked(self):
        self.reg["numbering"]["state"] = "PROVISIONAL"
        task(self.reg, "AF-01")["legacy_id"] = "AF-ONE"
        self.assertEqual([], self.errors())

    def test_order_certainty_is_explicit(self):
        task(self.reg, "AF-01")["created"]["order_certainty"] = "probably"
        self.assertFlags("order_certainty")

    def test_ids_must_follow_creation_order(self):
        a, b = task(self.reg, "AF-01"), task(self.reg, "AF-02")
        a["created"]["at"], b["created"]["at"] = b["created"]["at"], a["created"]["at"]
        self.assertFlags("precedes an earlier progressive id")

    def test_legacy_id_must_be_unique(self):
        task(self.reg, "AF-02")["legacy_id"] = "AF-01"
        self.assertFlags("already used")

    def test_status_outside_the_vocabulary(self):
        task(self.reg, "AF-01")["status"] = "FINISHED"
        self.assertFlags("closed vocabulary")

    def test_missing_packet_file(self):
        task(self.reg, "AF-01")["packet"] = "agentic/_tasks/does-not-exist.md"
        self.assertFlags("does not exist")

    def test_unknown_dependency_and_cycle(self):
        a, b = task(self.reg, "AF-02"), task(self.reg, "AF-03")
        a["depends_on"] = [b["id"]]
        b["depends_on"] = [a["id"]]
        self.assertFlags("dependency cycle")
        a["depends_on"] = ["BOPS-9999"]
        self.assertFlags("unknown task")

    def test_done_requires_every_required_gate(self):
        t = task(self.reg, "V1.1-A")
        t["validation"]["required"] = ["OFFLINE_TESTS", "CI_VERIFIED", "OPERATOR_GATE"]
        self.assertFlags("required gates are missing")

    def test_done_cannot_keep_an_open_gate(self):
        task(self.reg, "V1.1-A")["validation"]["open_gates"] = ["live run pending"]
        self.assertFlags("open_gates is not empty")

    def test_done_requires_complete_dependencies(self):
        task(self.reg, "AF-02")["depends_on"] = [task(self.reg, "AF-05")["id"]]
        self.assertFlags("dependencies are not complete")

    def test_validation_without_a_readable_gate(self):
        t = task(self.reg, "AF-05")
        t["validation"]["open_gates"] = []
        t["validation"]["required"] = t["validation"]["achieved"]
        self.assertFlags("VALIDATION needs a readable open gate")

    def test_blocked_deferred_superseded_need_their_reason(self):
        task(self.reg, "ARCH-01")["blocked_reason"] = None
        task(self.reg, "KNOW-02")["reopen_condition"] = None
        task(self.reg, "AF-05H1")["note"] = None
        errs = " | ".join(self.errors())
        for fragment in ("BLOCKED without blocked_reason", "DEFERRED without reopen_condition", "SUPERSEDED without a note"):
            self.assertIn(fragment, errs)

    def test_ready_needs_done_dependencies(self):
        task(self.reg, "DIAG-ASSURE-01")["depends_on"] = [task(self.reg, "AF-05")["id"]]
        self.assertFlags("READY while dependencies are not DONE")

    def test_parallel_in_progress_tasks_need_authorization_and_distinct_branches(self):
        a = task(self.reg, "DIAG-ASSURE-01")
        a.update(status="IN_PROGRESS", authorized=False, branch="feat/a")
        self.assertFlags("without operator authorization")
        a["authorized"] = True
        b = task(self.reg, "PLAN-FIDELITY-01")
        b.update(status="IN_PROGRESS", authorized=True, branch="feat/b", depends_on=[])
        self.assertEqual([], [e for e in self.errors() if "distinct branches" in e or "without the branch" in e or "authorization" in e])
        b["branch"] = "feat/a"
        self.assertFlags("distinct branches")
        b["branch"] = None
        self.assertFlags("without the branch/session")

    def test_handoff_must_match_the_register(self):
        cur = re.search(r"^Current task: .*$", HANDOFF, re.M).group(0)
        self.assertFlags("HANDOFF ready list", HANDOFF.replace("Ready: BOPS-0111", "Ready:"))
        self.assertFlags("HANDOFF current task", HANDOFF.replace(cur, "Current task: nonsense"))
        self.assertFlags("expected IN_PROGRESS or VALIDATION", HANDOFF.replace(cur, "Current task: BOPS-0111"))
        self.assertFlags("lacks the", "# no block here")

    def test_milestone_done_needs_implementation_validation_and_release(self):
        m = next(m for m in self.reg["milestones"] if m["id"] == "V1.2")
        m["validation"] = "PARTIAL"
        self.assertFlags("DONE requires implementation COMPLETE, validation COMPLETE")
        m["validation"], m["release"] = "COMPLETE", "NOT_RELEASED"
        self.assertFlags("DONE requires implementation COMPLETE")

    def test_quality_divergence_record_must_match_the_file(self):
        self.reg["known_divergences"][0]["recorded_value"] = "V1.3-Z"
        self.assertFlags("known divergence QM-1")

    def test_done_milestone_cannot_contain_open_tasks(self):
        next(m for m in self.reg["milestones"] if m["id"] == "MAF")["status"] = "DONE"
        self.assertFlags("milestone MAF is DONE")

    def test_remote_links_need_a_kind_and_drafts_have_no_issue_number(self):
        t = task(self.reg, "AF-01")
        t.update(issue=12, project_item="PVTI_x")
        self.assertFlags("without remote_kind")
        t["remote_kind"] = "draft"
        self.assertFlags("a draft task has a project_item and no issue number")
        t.update(remote_kind="issue", project_item=None)
        self.assertFlags("needs both issue and project_item")
        t.update(project_item="PVTI_x", sync={"status": "Finished"})
        self.assertFlags("not a board column")

    def test_issue_and_project_item_are_not_shared(self):
        task(self.reg, "AF-01")["issue"] = 7
        task(self.reg, "AF-02")["issue"] = 7
        self.assertFlags("assigned to more than one task")


class Titles(unittest.TestCase):
    def test_issue_title_format(self):
        t = {"id": "BOPS-0111", "legacy_id": "DIAG-ASSURE-01", "title": "DIAG-ASSURE-01 Evidence assurance projection"}
        self.assertEqual("BOPS-0111 | DIAG-ASSURE-01 — Evidence assurance projection", vr.task_title(t))


if __name__ == "__main__":
    unittest.main()

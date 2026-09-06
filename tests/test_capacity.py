import importlib.util
import unittest
from pathlib import Path


PATH = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts/capacity.py"
SPEC = importlib.util.spec_from_file_location("capacity", PATH)
capacity = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(capacity)


def interval(start, end):
    return {"start": start, "end": end}


class CapacityTests(unittest.TestCase):
    def base(self):
        return {
            "coverage": "complete",
            "working": [interval("2026-09-07T09:00:00-07:00", "2026-09-07T17:00:00-07:00")],
            "busy": [],
        }

    def test_overlap_clipping_and_protected_not_double_subtracted(self):
        data = self.base()
        data["busy"] = [
            interval("2026-09-07T08:00:00-07:00", "2026-09-07T10:00:00-07:00"),
            interval("2026-09-07T09:30:00-07:00", "2026-09-07T11:00:00-07:00"),
            interval("2026-09-07T16:00:00-07:00", "2026-09-07T18:00:00-07:00"),
        ]
        data["protected"] = [
            interval("2026-09-07T10:00:00-07:00", "2026-09-07T12:00:00-07:00"),
        ]
        result = capacity.calculate(data)
        self.assertEqual(result["busy_minutes"], 180)
        self.assertEqual(result["available_minutes"], 300)
        self.assertEqual(result["protected_available_minutes"], 60)
        self.assertEqual(result["protected_conflict_minutes"], 60)

    def test_dst_uses_elapsed_time(self):
        data = {"coverage": "complete", "busy": [], "working": [
            interval("2026-11-01T00:00:00-07:00", "2026-11-01T04:00:00-08:00")
        ]}
        self.assertEqual(capacity.calculate(data)["working_minutes"], 300)

    def test_unknown_and_partial_are_not_feasible(self):
        data = self.base()
        data["coverage"] = "partial"
        self.assertIsNone(capacity.calculate(data)["fits_total_capacity"])
        data["coverage"] = "complete"
        data["estimates"] = [{"id": "outcome-1", "minutes": None}]
        self.assertIsNone(capacity.calculate(data)["fits_total_capacity"])

    def test_overflow_and_exact_focus_threshold(self):
        data = self.base()
        data["busy"] = [
            interval("2026-09-07T10:30:00-07:00", "2026-09-07T17:00:00-07:00")
        ]
        data["estimates"] = [{"id": "outcome-1", "minutes": 120}]
        result = capacity.calculate(data)
        self.assertFalse(result["fits_total_capacity"])
        self.assertEqual(result["minimum_overflow_minutes"], 30)
        self.assertEqual(len(result["focus_blocks"]), 1)
        self.assertEqual(result["allocation_status"], "not_allocated")

    def test_no_naive_or_reversed_intervals(self):
        for start, end in [
            ("2026-09-07T09:00:00", "2026-09-07T10:00:00"),
            ("2026-09-07T10:00:00Z", "2026-09-07T09:00:00Z"),
        ]:
            data = self.base()
            data["busy"] = [interval(start, end)]
            with self.assertRaises(ValueError):
                capacity.calculate(data)

    def test_invalid_numbers_and_duplicate_estimates(self):
        for value in (True, -1, float("nan"), float("inf")):
            data = self.base()
            data["estimates"] = [{"id": "x", "minutes": value}]
            with self.assertRaises(ValueError):
                capacity.calculate(data)
        data = self.base()
        data["estimates"] = [{"id": "x", "minutes": 1}, {"id": "x", "minutes": 1}]
        with self.assertRaises(ValueError):
            capacity.calculate(data)

    def test_working_union_and_whole_day_leave(self):
        data = self.base()
        data["working"] *= 2
        data["busy"] = [
            interval("2026-09-07T00:00:00-07:00", "2026-09-08T00:00:00-07:00")
        ]
        result = capacity.calculate(data)
        self.assertEqual(result["working_minutes"], 480)
        self.assertEqual(result["available_minutes"], 0)


if __name__ == "__main__":
    unittest.main()

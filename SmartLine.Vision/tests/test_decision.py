"""API ile ortak kararları kamera veya model çalıştırmadan doğrular."""

import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "SmartLine.Vision" / "src"))
from decision import decide


class DecisionTests(unittest.TestCase):
    def setUp(self):
        self.recipe = json.loads(
            (ROOT / "SmartLine.API/Contracts/recipes/demobox.json").read_text()
        )

    def test_shared_api_fixtures(self):
        # Aynı JSON senaryolarını iki dilde çalıştırarak API ve Vision kararlarını karşılaştırırız.
        document = json.loads(
            (ROOT / "SmartLine.API/Contracts/fixtures/decision-cases.json").read_text()
        )
        for case in document["cases"]:
            with self.subTest(case=case["name"]):
                recipe = copy.deepcopy(self.recipe)
                if "physicalLimits" in case:
                    recipe["physicalLimits"] = case["physicalLimits"]
                result = decide(recipe, case["observation"])
                self.assertTrue(set(case.get("expectedReasons", [])) <= set(result["reasons"]))
                for field, expected in case["expected"].items():
                    self.assertEqual(expected, result[field], field)

    def test_invalid_confidence(self):
        for value in (-0.1, 1.1, float("nan"), float("inf")):
            with self.subTest(value=value), self.assertRaises(ValueError):
                decide(self.recipe, {"confidence": value})

    def test_invalid_measured_dimensions(self):
        for field in ("estimatedWidthMm", "estimatedHeightMm"):
            for value in (-1, float("nan"), float("inf")):
                with (
                    self.subTest(field=field, value=value),
                    self.assertRaises(ValueError),
                ):
                    decide(self.recipe, {"confidence": 0.9, field: value})

    def test_physical_confidence_floor_does_not_weaken_acceptance(self):
        self.recipe["physicalLimits"]["minConfidenceToSort"] = 0.2
        result = decide(
            self.recipe,
            {
                "productClass": "BOX_GREEN",
                "colorClass": "GREEN",
                "sizeClass": "SMALL",
                "confidence": 0.3,
            },
        )
        self.assertEqual("MANUAL_REVIEW", result["decision"])

    def test_catch_all_pass_rule_cannot_override_acceptance(self):
        # Genel bir PASS kuralı, düşük güveni veya kusur kararını geçersiz kılamamalı.
        scenarios = [
            (
                {"confidence": 0.1},
                "MANUAL_REVIEW",
                "MANUAL_REVIEW",
                "MANUAL_REVIEW",
                None,
            ),
            (
                {"estimatedWidthMm": 251},
                "DEFECTIVE",
                "REJECT",
                "REJECT_BIN",
                "any-defect",
            ),
            (
                {"estimatedHeightMm": 251},
                "DEFECTIVE",
                "REJECT",
                "REJECT_BIN",
                "any-defect",
            ),
            (
                {"productClass": "MYSTERY"},
                "DEFECTIVE",
                "REJECT",
                "REJECT_BIN",
                "any-defect",
            ),
            (
                {"defectClasses": ["DAMAGED"]},
                "DEFECTIVE",
                "REJECT",
                "REJECT_BIN",
                "any-defect",
            ),
        ]
        self.recipe["sortingRules"].insert(
            0,
            {
                "id": "catch-all",
                "priority": 0,
                "when": {},
                "destinationBin": "BIN_A",
            },
        )
        for changes, quality, decision, destination, rule_id in scenarios:
            with self.subTest(changes=changes):
                result = decide(self.recipe, self.green_observation(**changes))
                self.assertEqual(quality, result["qualityStatus"])
                self.assertEqual(decision, result["decision"])
                self.assertEqual(destination, result["destinationBin"])
                self.assertEqual(rule_id, result["ruleId"])
                self.assertNotIn("RULE:catch-all", result["reasons"])

    def test_unsafe_default_falls_back_to_manual_review(self):
        # Kural bulunmasa bile varsayılan hedef kabul şartlarını aşamamalı.
        self.recipe["sortingRules"] = []
        for low_confidence, destination in (
            (False, "BIN_A"),
            (True, "BIN_A"),
            (True, "REJECT_BIN"),
        ):
            with self.subTest(low_confidence=low_confidence, destination=destination):
                self.recipe["acceptance"]["defaultDestinationBin"] = destination
                changes = (
                    {"confidence": 0.1}
                    if low_confidence
                    else {"defectClasses": ["DAMAGED"]}
                )
                result = decide(self.recipe, self.green_observation(**changes))
                self.assertEqual(
                    "MANUAL_REVIEW" if low_confidence else "DEFECTIVE",
                    result["qualityStatus"],
                )
                self.assertEqual("MANUAL_REVIEW", result["decision"])
                self.assertEqual("MANUAL_REVIEW", result["destinationBin"])
                self.assertIsNone(result["ruleId"])
                self.assertIn("DESTINATION_GUARD", result["reasons"])

    def test_low_confidence_cannot_be_rejected_by_a_catch_all_rule(self):
        self.recipe["sortingRules"].insert(
            0,
            {
                "id": "catch-all-reject",
                "priority": 0,
                "when": {},
                "destinationBin": "REJECT_BIN",
            },
        )
        result = decide(self.recipe, self.green_observation(confidence=0.1))
        self.assertEqual("MANUAL_REVIEW", result["decision"])
        self.assertEqual("MANUAL_REVIEW", result["destinationBin"])
        self.assertIsNone(result["ruleId"])

    def test_compatible_manual_review_rule_keeps_its_destination(self):
        self.recipe["sortingRules"].append(
            {
                "id": "review",
                "priority": 50,
                "when": {"quality": "MANUAL_REVIEW"},
                "destinationBin": "MANUAL_REVIEW",
            }
        )
        result = decide(self.recipe, self.green_observation(confidence=0.1))
        self.assertEqual("MANUAL_REVIEW", result["destinationBin"])
        self.assertEqual("review", result["ruleId"])
        self.assertNotIn("DESTINATION_GUARD", result["reasons"])

    def test_missing_safe_destination_fails_instead_of_passing(self):
        self.recipe["sortingRules"] = []
        self.recipe["acceptance"]["defaultDestinationBin"] = "BIN_A"
        self.recipe["destinationBins"] = [
            item for item in self.recipe["destinationBins"] if item["role"] != "HOLD"
        ]
        with self.assertRaisesRegex(ValueError, "No HOLD destination"):
            decide(self.recipe, self.green_observation(confidence=0.1))

    @staticmethod
    def green_observation(**changes):
        return {
            "productClass": "BOX_GREEN",
            "colorClass": "GREEN",
            "sizeClass": "SMALL",
            "confidence": 0.9,
            **changes,
        }


if __name__ == "__main__":
    unittest.main()

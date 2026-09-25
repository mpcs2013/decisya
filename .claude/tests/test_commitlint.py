"""The local commitlint mirror matches the real commitlint 19 golden outputs byte for byte."""
import json
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
import commitlint  # noqa: E402

FIXTURES = HERE / "fixtures" / "commitlint"


class GoldenTests(unittest.TestCase):
    def test_every_fixture_matches_golden_output_and_exit_code(self):
        expected = json.loads((FIXTURES / "expected.json").read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(expected), 29)
        for name, code in sorted(expected.items()):
            with self.subTest(fixture=name):
                message = (FIXTURES / f"{name}.msg").read_text(encoding="utf-8")
                out, got = commitlint.report(message)
                self.assertEqual(got, code)
                self.assertEqual(out, (FIXTURES / f"{name}.expected").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()

import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


PATH = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts/memory_encoder.py"
SCRIPTS = PATH.parent
sys.path.insert(0, str(SCRIPTS))
SPEC = importlib.util.spec_from_file_location("memory_encoder", PATH)
encoder = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(encoder)


class EncoderTests(unittest.TestCase):
    def setUp(self):
        self.unsafe = mock.patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.unsafe.start()
        self.addCleanup(self.unsafe.stop)

    def test_pinned_checksums_and_fingerprint(self):
        self.assertEqual(
            encoder._digest_bytes(b"hello\n", "git-sha1"),
            "ce013625030ba8dba906f756967f9e9ca394464a",
        )
        self.assertEqual(
            encoder._digest_bytes(b"hello\n", "sha256"),
            hashlib.sha256(b"hello\n").hexdigest(),
        )
        spec = encoder.model_spec()
        self.assertEqual(spec["dimensions"], 384)
        self.assertEqual(spec["model_revision"], encoder.MODEL_REVISION)
        self.assertEqual(len(spec["model_fingerprint"]), 64)

    def test_attention_mask_mean_pooling_and_shape_checks_without_runtime(self):
        first = [0.0] * encoder.DIMENSIONS
        second = [0.0] * encoder.DIMENSIONS
        padding = [999.0] * encoder.DIMENSIONS
        first[0] = 1.0
        second[1] = 1.0
        vectors = encoder._mean_pool_and_normalize(
            [[first, second, padding]], [[1, 1, 0]])
        expected = 1.0 / math.sqrt(2.0)
        self.assertAlmostEqual(vectors[0][0], expected)
        self.assertAlmostEqual(vectors[0][1], expected)
        self.assertAlmostEqual(sum(value * value for value in vectors[0]), 1.0)
        with self.assertRaises(encoder.EmbeddingError):
            encoder._mean_pool_and_normalize([[[1.0]]], [[1]])
        with self.assertRaises(encoder.EmbeddingError):
            encoder._mean_pool_and_normalize([[first]], [[0]])

    def test_chunk_vectors_are_token_weighted_and_renormalized(self):
        first = [0.0] * encoder.DIMENSIONS
        second = [0.0] * encoder.DIMENSIONS
        first[0] = 1.0
        second[1] = 1.0
        rows = [{"owner": 0, "weight": 3}, {"owner": 0, "weight": 1}]
        vector = encoder._aggregate_chunk_vectors([first, second], rows, 1)[0]
        self.assertAlmostEqual(vector[0], 3.0 / math.sqrt(10.0))
        self.assertAlmostEqual(vector[1], 1.0 / math.sqrt(10.0))
        self.assertAlmostEqual(sum(value * value for value in vector), 1.0)

    def test_input_validation_requires_bounded_nonempty_strings(self):
        for invalid in (None, [], [""], ["  "], [1],
                        ["ok"] * (encoder.MAX_BATCH_SIZE + 1),
                        ["x" * (encoder.MAX_TEXT_CHARS + 1)]):
            with self.subTest(invalid_type=type(invalid).__name__):
                with self.assertRaises(encoder.EmbeddingError):
                    encoder._validate_texts(invalid)

    def test_runtime_missing_is_explicit(self):
        with mock.patch.object(encoder, "_load_runtime", side_effect=encoder.EmbeddingError(
                "missing_runtime: install the pinned optional embedding requirements")):
            with self.assertRaisesRegex(encoder.EmbeddingError, "missing_runtime"):
                encoder.encode_texts(["hello"])

    def _valid_result(self, count=1):
        spec = encoder.model_spec()
        return {
            "model_fingerprint": spec["model_fingerprint"],
            "model_id": spec["model_id"],
            "model_revision": spec["model_revision"],
            "dimensions": encoder.DIMENSIONS,
            "vectors": [[1.0] + [0.0] * (encoder.DIMENSIONS - 1) for _ in range(count)],
        }

    def test_subprocess_rejects_malformed_fingerprint_dimensions_and_batch(self):
        cases = (
            ("not-json", "malformed"),
            (dict(self._valid_result(), model_fingerprint="wrong"), "fingerprint"),
            (dict(self._valid_result(), dimensions=3), "dimensions"),
            (dict(self._valid_result(), vectors=[]), "batch"),
            (dict(self._valid_result(), vectors=[[float("nan")] * encoder.DIMENSIONS]),
             "non-finite"),
        )
        for result, message in cases:
            with self.subTest(message=message):
                with mock.patch.object(encoder, "_runtime_available", return_value=False), \
                        mock.patch.object(encoder, "_private_python",
                                          return_value=Path(sys.executable)), \
                        mock.patch.object(
                            encoder.subprocess, "run",
                            return_value=subprocess.CompletedProcess(
                                args=[], returncode=0,
                                stdout=result if isinstance(result, str) else json.dumps(result),
                                stderr="")):
                    with self.assertRaisesRegex(encoder.EmbeddingError, message):
                        encoder.encode_local(["safe input"])

    def test_subprocess_uses_stdin_and_redacts_text_from_stderr(self):
        secret = "private memory text"
        completed = subprocess.CompletedProcess(
            args=[], returncode=2, stdout="", stderr="failed near " + secret)
        with mock.patch.object(encoder, "_runtime_available", return_value=False), \
                mock.patch.object(encoder, "_private_python", return_value=Path(sys.executable)), \
                mock.patch.object(encoder.subprocess, "run", return_value=completed) as run:
            with self.assertRaises(encoder.EmbeddingError) as caught:
                encoder.encode_local([secret])
        self.assertNotIn(secret, str(caught.exception))
        command = run.call_args.args[0]
        self.assertEqual(command[1:3], ["-B", str(PATH)])
        self.assertEqual(command[3:], ["encode", "--input", "-"])
        self.assertNotIn(secret, command)
        self.assertIn(secret, run.call_args.kwargs["input"])

    def test_inference_path_never_opens_network(self):
        class FakeEncoding:
            ids = [1, 2]
            attention_mask = [1, 1]
            type_ids = [0, 0]
            special_tokens_mask = [1, 1]
            overflowing = []

        class FakeTokenizer:
            def encode_batch(self, texts):
                return [FakeEncoding() for _ in texts]

            def token_to_id(self, token):
                return 0 if token == "[PAD]" else None

        class FakeInput:
            def __init__(self, name):
                self.name = name

        class FakeSession:
            def get_inputs(self):
                return [FakeInput("input_ids"), FakeInput("attention_mask"),
                        FakeInput("token_type_ids")]

            def run(self, _outputs, _feeds):
                vector = [0.0] * encoder.DIMENSIONS
                vector[0] = 1.0
                return [[[vector, vector]]]

        class FakeNumpy:
            int64 = "int64"

            @staticmethod
            def asarray(value, dtype=None):
                return value

        runtime = (FakeNumpy(), object(), object())
        with mock.patch.object(encoder, "_load_runtime", return_value=runtime), \
                mock.patch.object(encoder, "_validate_model", return_value=Path("model")), \
                mock.patch.object(encoder, "_load_model",
                                  return_value=(FakeTokenizer(), FakeSession())), \
                mock.patch.object(encoder.urllib.request, "urlopen",
                                  side_effect=AssertionError("network attempted")):
            result = encoder.encode_texts(["offline only"])
        self.assertEqual(result["dimensions"], encoder.DIMENSIONS)
        self.assertEqual(result["vectors"][0][0], 1.0)

    def test_overflow_chunks_are_all_encoded_into_one_record_vector(self):
        class FakeEncoding:
            def __init__(self, marker, weight, overflowing=None):
                self.ids = [marker] + [2] * weight + [3]
                self.attention_mask = [1] * len(self.ids)
                self.type_ids = [0] * len(self.ids)
                self.special_tokens_mask = [1] + [0] * weight + [1]
                self.overflowing = overflowing or []

        class FakeTokenizer:
            def encode_batch(self, texts):
                return [FakeEncoding(10, 3, [FakeEncoding(20, 1)])]

            def token_to_id(self, token):
                return 0 if token == "[PAD]" else None

        class FakeInput:
            def __init__(self, name):
                self.name = name

        class FakeSession:
            def get_inputs(self):
                return [FakeInput("input_ids"), FakeInput("attention_mask")]

            def run(self, _outputs, feeds):
                output = []
                for ids in feeds["input_ids"]:
                    vector = [0.0] * encoder.DIMENSIONS
                    vector[0 if ids[0] == 10 else 1] = 1.0
                    output.append([list(vector) for _token in ids])
                return [output]

        class FakeNumpy:
            int64 = "int64"

            @staticmethod
            def asarray(value, dtype=None):
                return value

        vector = encoder._encode_with_model(
            ["long record"], (FakeNumpy(), object(), object()),
            FakeTokenizer(), FakeSession())[0]
        self.assertAlmostEqual(vector[0], 3.0 / math.sqrt(10.0))
        self.assertAlmostEqual(vector[1], 1.0 / math.sqrt(10.0))

    def test_status_distinguishes_missing_runtime_and_model(self):
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory).resolve() / "missing"
            with mock.patch.object(encoder, "_runtime_available", return_value=False):
                self.assertEqual(encoder.status(missing)["status"], "missing_runtime")
            with mock.patch.object(encoder, "_runtime_available", return_value=True):
                self.assertEqual(encoder.status(missing)["status"], "missing_model")

    def test_real_model_paraphrase_integration_when_explicitly_enabled(self):
        if os.environ.get("MARGO_RUN_EMBEDDING_INTEGRATION") != "1":
            self.skipTest("set MARGO_RUN_EMBEDDING_INTEGRATION=1 after explicit model setup")
        state = encoder.status()
        if state["status"] != "available":
            self.skipTest("pinned runtime/model are not available")
        with mock.patch.object(socket, "create_connection",
                               side_effect=AssertionError("network attempted")), \
                mock.patch.object(encoder.urllib.request, "urlopen",
                                  side_effect=AssertionError("network attempted")):
            vectors = encoder.encode_texts([
                "Leave preparation time before the decision review.",
                "Block time to prepare ahead of the meeting where we decide.",
                "A recipe for pasta with tomato sauce.",
            ])["vectors"]
        similar = sum(a * b for a, b in zip(vectors[0], vectors[1]))
        unrelated = sum(a * b for a, b in zip(vectors[0], vectors[2]))
        self.assertGreater(similar, unrelated)


if __name__ == "__main__":
    unittest.main()

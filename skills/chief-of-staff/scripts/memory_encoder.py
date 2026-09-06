#!/usr/bin/env python3
"""Offline sentence embeddings backed by a pinned ONNX MiniLM model.

The model is never downloaded during inference. Run the explicit ``download``
command first. Long inputs are split into non-overlapping 256-token windows;
their normalized vectors are token-count-weighted, averaged, and normalized
again so every input still produces exactly one record vector.
"""

import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import secrets
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.request


MODEL_ID = "sentence-transformers/all-MiniLM-L6-v2"
MODEL_REVISION = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
DIMENSIONS = 384
MAX_TOKENS = 256
MAX_BATCH_SIZE = 32
MAX_TEXT_CHARS = 16384
MAX_CHUNKS_PER_INFERENCE = 64
MAX_STDIN_BYTES = 1024 * 1024
MAX_SUBPROCESS_OUTPUT_BYTES = 2 * 1024 * 1024
DOWNLOAD_ATTEMPTS = 3
DOWNLOAD_TIMEOUT_SECONDS = 30

PREPROCESSING = {
    "padding": "longest",
    "chunking": {
        "strategy": "tokenizer_overflow",
        "window_tokens_including_special_tokens": MAX_TOKENS,
        "stride": 0,
    },
    "pooling": "attention_mask_mean",
    "chunk_normalization": "l2",
    "record_aggregation": "non_special_token_count_weighted_mean_then_l2",
}

ASSETS = (
    {
        "path": "onnx/model.onnx",
        "size": 90405214,
        "algorithm": "sha256",
        "digest": "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452",
    },
    {
        "path": "tokenizer.json",
        "size": 466247,
        "algorithm": "git-sha1",
        "digest": "cb202bfe2e3c98645018a6d12f182a434c9d3e02",
    },
    {
        "path": "tokenizer_config.json",
        "size": 350,
        "algorithm": "git-sha1",
        "digest": "c79f2b6a0cea6f4b564fed1938984bace9d30ff0",
    },
    {
        "path": "config.json",
        "size": 612,
        "algorithm": "git-sha1",
        "digest": "72b987fd805cfa2b58c4c8c952b274a11bfd5a00",
    },
)

_MODEL_CACHE = {}


class EmbeddingError(RuntimeError):
    """A safe, user-actionable local embedding failure."""


def _canonical_json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _identity_payload():
    tokenizer = next(asset for asset in ASSETS if asset["path"] == "tokenizer.json")
    return {
        "model_id": MODEL_ID,
        "model_revision": MODEL_REVISION,
        "dimensions": DIMENSIONS,
        "preprocessing": PREPROCESSING,
        "tokenizer": {
            "algorithm": tokenizer["algorithm"],
            "digest": tokenizer["digest"],
        },
    }


def model_spec():
    """Return the stable identity expected by the semantic index."""
    identity = _identity_payload()
    fingerprint = hashlib.sha256(_canonical_json(identity).encode("utf-8")).hexdigest()
    return {
        "model_id": MODEL_ID,
        "model_revision": MODEL_REVISION,
        "model_fingerprint": fingerprint,
        "dimensions": DIMENSIONS,
        "max_tokens": MAX_TOKENS,
        "pooling": PREPROCESSING["pooling"],
        "normalization": PREPROCESSING["chunk_normalization"],
        "long_text_strategy": PREPROCESSING["record_aggregation"],
        "tokenizer_hash": identity["tokenizer"]["digest"],
    }


def _expected_manifest():
    return {
        "format_version": 1,
        "identity": _identity_payload(),
        "model_fingerprint": model_spec()["model_fingerprint"],
        "assets": [dict(asset) for asset in ASSETS],
    }


def _copilot_home():
    return Path(os.environ.get("COPILOT_HOME", "~/.copilot")).expanduser().absolute()


def default_model_dir():
    override = os.environ.get("MARGO_EMBEDDING_MODEL_DIR")
    if override:
        return Path(override).expanduser().absolute()
    return (_copilot_home() / "margo/embeddings/models/minilm-l6-v2" /
            MODEL_REVISION).absolute()


def _safe_model_path(model_dir=None):
    path = Path(model_dir).expanduser().absolute() if model_dir is not None else default_model_dir()
    explicit = model_dir is not None or bool(os.environ.get("MARGO_EMBEDDING_MODEL_DIR"))
    try:
        from margo_store import _safe_directory
        return _safe_directory(path, explicit_override=explicit)
    except ImportError as exc:
        raise EmbeddingError("storage path validation is not installed") from exc
    except Exception as exc:
        raise EmbeddingError("unsafe model directory: " + str(exc)) from exc


def _ensure_private_directory(path):
    path = Path(path)
    missing = []
    current = path
    while not current.exists():
        missing.append(current)
        current = current.parent
    for directory in reversed(missing):
        try:
            directory.mkdir(mode=0o700)
        except FileExistsError:
            pass
    for directory in (path,) + tuple(path.parents):
        if directory.is_symlink():
            raise EmbeddingError("model directory must not contain symlinks")
        if directory == path:
            info = directory.stat()
            if not stat.S_ISDIR(info.st_mode):
                raise EmbeddingError("model directory has the wrong file type")
            if hasattr(os, "getuid") and info.st_uid != os.getuid():
                raise EmbeddingError("model directory must belong to the current user")
            if os.name != "nt" and stat.S_IMODE(info.st_mode) & 0o077:
                raise EmbeddingError("model directory must be private (0700)")


def _private_file(path):
    if path.is_symlink():
        raise EmbeddingError("model assets must not be symlinks")
    try:
        info = path.stat()
    except OSError as exc:
        raise EmbeddingError("missing_model: run the explicit download command") from exc
    if not stat.S_ISREG(info.st_mode):
        raise EmbeddingError("model asset has the wrong file type")
    if hasattr(os, "getuid") and info.st_uid != os.getuid():
        raise EmbeddingError("model assets must belong to the current user")
    if os.name != "nt" and stat.S_IMODE(info.st_mode) & 0o077:
        raise EmbeddingError("model assets must be private (0600)")
    if info.st_nlink != 1:
        raise EmbeddingError("hard-linked model assets are not supported")
    return info


def _new_hasher(algorithm, size):
    if algorithm == "sha256":
        return hashlib.sha256()
    if algorithm == "git-sha1":
        hasher = hashlib.sha1()
        hasher.update(("blob %d\0" % size).encode("ascii"))
        return hasher
    raise EmbeddingError("unsupported model asset checksum")


def _digest_bytes(data, algorithm):
    hasher = _new_hasher(algorithm, len(data))
    hasher.update(data)
    return hasher.hexdigest()


def _verify_asset(path, asset):
    try:
        info = _private_file(path)
    except EmbeddingError:
        return False
    if info.st_size != asset["size"]:
        return False
    hasher = _new_hasher(asset["algorithm"], asset["size"])
    try:
        with path.open("rb") as stream:
            while True:
                chunk = stream.read(1024 * 1024)
                if not chunk:
                    break
                hasher.update(chunk)
    except OSError:
        return False
    return hasher.hexdigest() == asset["digest"]


def _read_manifest(model_dir):
    path = model_dir / "manifest.json"
    try:
        _private_file(path)
        with path.open("r", encoding="utf-8") as stream:
            manifest = json.load(stream)
    except (EmbeddingError, OSError, UnicodeError, json.JSONDecodeError):
        return None
    return manifest


def _validate_model(model_dir):
    model_dir = _safe_model_path(model_dir)
    if not model_dir.exists() or _read_manifest(model_dir) != _expected_manifest():
        raise EmbeddingError("missing_model: run the explicit download command")
    for asset in ASSETS:
        if not _verify_asset(model_dir / asset["path"], asset):
            raise EmbeddingError("missing_model: a pinned model asset failed identity verification")
    return model_dir


def _asset_url(asset):
    return ("https://huggingface.co/" + MODEL_ID + "/resolve/" + MODEL_REVISION +
            "/" + asset["path"] + "?download=true")


def _download_once(asset, destination, timeout):
    temporary = destination.with_name(
        "." + destination.name + "." + str(os.getpid()) + "." + secrets.token_hex(6) + ".part")
    hasher = _new_hasher(asset["algorithm"], asset["size"])
    written = 0
    request = urllib.request.Request(
        _asset_url(asset),
        headers={"User-Agent": "margo-local-embedding-setup/1"},
        method="GET",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            content_length = response.headers.get("Content-Length")
            if content_length is not None and int(content_length) != asset["size"]:
                raise EmbeddingError("downloaded model asset has an unexpected size")
            descriptor = os.open(str(temporary), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "wb") as stream:
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    written += len(chunk)
                    if written > asset["size"]:
                        raise EmbeddingError("downloaded model asset exceeded its pinned size")
                    stream.write(chunk)
                    hasher.update(chunk)
                stream.flush()
                os.fsync(stream.fileno())
        if written != asset["size"] or hasher.hexdigest() != asset["digest"]:
            raise EmbeddingError("downloaded model asset failed identity verification")
        os.replace(str(temporary), str(destination))
        if os.name != "nt":
            destination.chmod(0o600)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _download_asset(asset, destination, timeout=DOWNLOAD_TIMEOUT_SECONDS):
    last_error = None
    for attempt in range(DOWNLOAD_ATTEMPTS):
        try:
            _download_once(asset, destination, timeout)
            return
        except (EmbeddingError, OSError, urllib.error.URLError, ValueError) as exc:
            last_error = exc
            if attempt + 1 < DOWNLOAD_ATTEMPTS:
                time.sleep(0.5 * (2 ** attempt))
    raise EmbeddingError("model download failed after bounded retries") from last_error


def _write_manifest(model_dir):
    destination = model_dir / "manifest.json"
    temporary = destination.with_name(
        ".manifest." + str(os.getpid()) + "." + secrets.token_hex(6) + ".part")
    payload = (_canonical_json(_expected_manifest()) + "\n").encode("utf-8")
    try:
        descriptor = os.open(str(temporary), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(str(temporary), str(destination))
        if os.name != "nt":
            destination.chmod(0o600)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def download_model(model_dir=None, timeout=DOWNLOAD_TIMEOUT_SECONDS):
    """Explicitly download and verify only the pinned public model assets."""
    if not isinstance(timeout, (int, float)) or isinstance(timeout, bool) or not 1 <= timeout <= 120:
        raise EmbeddingError("download timeout must be between 1 and 120 seconds")
    target = _safe_model_path(model_dir)
    _ensure_private_directory(target)
    manifest = target / "manifest.json"
    if manifest.exists():
        if _read_manifest(target) == _expected_manifest() and all(
                _verify_asset(target / asset["path"], asset) for asset in ASSETS):
            return status(model_dir=target)
        if manifest.is_symlink() or not manifest.is_file():
            raise EmbeddingError("invalid model manifest path")
        manifest.unlink()
    for asset in ASSETS:
        destination = target / asset["path"]
        _ensure_private_directory(destination.parent)
        if not _verify_asset(destination, asset):
            if destination.exists():
                if destination.is_symlink() or not destination.is_file():
                    raise EmbeddingError("invalid model asset path")
                destination.unlink()
            _download_asset(asset, destination, timeout=timeout)
        if not _verify_asset(destination, asset):
            raise EmbeddingError("downloaded model asset failed final verification")
    _write_manifest(target)
    _validate_model(target)
    return status(model_dir=target)


def _load_runtime():
    try:
        import numpy as np
        import onnxruntime as ort
        from tokenizers import Tokenizer
    except (ImportError, OSError) as exc:
        raise EmbeddingError(
            "missing_runtime: install the pinned optional embedding requirements") from exc
    return np, ort, Tokenizer


def _runtime_available():
    required = ("numpy", "onnxruntime", "tokenizers")
    if any(importlib.util.find_spec(name) is None for name in required):
        return False
    try:
        _load_runtime()
    except EmbeddingError:
        return False
    return True


def _validate_texts(texts):
    if not isinstance(texts, list) or not texts or len(texts) > MAX_BATCH_SIZE:
        raise EmbeddingError("texts must be a nonempty list with at most %d items" % MAX_BATCH_SIZE)
    for text in texts:
        if not isinstance(text, str) or not text.strip():
            raise EmbeddingError("each text must be a nonempty string")
        if len(text) > MAX_TEXT_CHARS:
            raise EmbeddingError(
                "text exceeds %d characters; chunk lengthy memories before encoding" %
                MAX_TEXT_CHARS)
    return texts


def _load_model(model_dir, runtime):
    np, ort, Tokenizer = runtime
    model_dir = _validate_model(model_dir)
    cache_key = str(model_dir)
    cached = _MODEL_CACHE.get(cache_key)
    if cached is not None:
        return cached
    try:
        tokenizer = Tokenizer.from_file(str(model_dir / "tokenizer.json"))
        tokenizer.enable_truncation(max_length=MAX_TOKENS)
        pad_id = tokenizer.token_to_id("[PAD]")
        if pad_id is None:
            raise EmbeddingError("pinned tokenizer has no [PAD] token")
        tokenizer.enable_padding(pad_id=pad_id, pad_token="[PAD]")
        options = ort.SessionOptions()
        options.intra_op_num_threads = max(1, min(4, os.cpu_count() or 1))
        options.inter_op_num_threads = 1
        options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        session = ort.InferenceSession(
            str(model_dir / "onnx/model.onnx"),
            sess_options=options,
            providers=["CPUExecutionProvider"],
        )
    except EmbeddingError:
        raise
    except Exception as exc:
        raise EmbeddingError("failed to initialize the pinned local embedding model") from exc
    cached = (tokenizer, session)
    _MODEL_CACHE[cache_key] = cached
    return cached


def _mean_pool_and_normalize(token_embeddings, attention_mask, np_module=None):
    """Attention-mask mean pool and L2-normalize; lists are supported for unit tests."""
    if np_module is not None:
        embeddings = np_module.asarray(token_embeddings, dtype=np_module.float32)
        mask = np_module.asarray(attention_mask, dtype=np_module.float32)
        if embeddings.ndim != 3 or mask.ndim != 2:
            raise EmbeddingError("model returned invalid embedding or attention-mask ranks")
        if embeddings.shape[:2] != mask.shape or embeddings.shape[2] != DIMENSIONS:
            raise EmbeddingError("model returned invalid embedding dimensions")
        expanded = mask[:, :, None]
        counts = expanded.sum(axis=1)
        if bool(np_module.any(counts <= 0)):
            raise EmbeddingError("model returned an empty attention mask")
        pooled = (embeddings * expanded).sum(axis=1) / counts
        norms = np_module.linalg.norm(pooled, axis=1, keepdims=True)
        if bool(np_module.any(~np_module.isfinite(pooled))) or bool(np_module.any(norms <= 0)):
            raise EmbeddingError("model returned non-finite or zero-length embeddings")
        normalized = pooled / norms
        if not bool(np_module.all(np_module.isfinite(normalized))):
            raise EmbeddingError("model returned non-finite embeddings")
        return normalized.tolist()

    if not isinstance(token_embeddings, (list, tuple)) or not isinstance(
            attention_mask, (list, tuple)) or len(token_embeddings) != len(attention_mask):
        raise EmbeddingError("model returned invalid batch dimensions")
    result = []
    for tokens, mask in zip(token_embeddings, attention_mask):
        if not isinstance(tokens, (list, tuple)) or not isinstance(mask, (list, tuple)):
            raise EmbeddingError("model returned invalid sequence dimensions")
        if not tokens or len(tokens) != len(mask):
            raise EmbeddingError("model returned invalid sequence dimensions")
        pooled = [0.0] * DIMENSIONS
        count = 0
        for vector, included in zip(tokens, mask):
            if not isinstance(vector, (list, tuple)) or len(vector) != DIMENSIONS:
                raise EmbeddingError("model returned invalid embedding dimensions")
            if included:
                count += 1
                for index, value in enumerate(vector):
                    pooled[index] += float(value)
        if count == 0:
            raise EmbeddingError("model returned an empty attention mask")
        pooled = [value / count for value in pooled]
        norm = math.sqrt(sum(value * value for value in pooled))
        if not math.isfinite(norm) or norm <= 0:
            raise EmbeddingError("model returned non-finite or zero-length embeddings")
        normalized = [value / norm for value in pooled]
        if not all(math.isfinite(value) for value in normalized):
            raise EmbeddingError("model returned non-finite embeddings")
        result.append(normalized)
    return result


def _chunk_rows(texts, tokenizer):
    try:
        roots = tokenizer.encode_batch(texts)
    except Exception as exc:
        raise EmbeddingError("local tokenization failed") from exc
    if len(roots) != len(texts):
        raise EmbeddingError("tokenizer returned the wrong batch size")
    rows = []
    for owner, root in enumerate(roots):
        chunks = [root] + list(getattr(root, "overflowing", ()) or ())
        for chunk in chunks:
            ids = list(chunk.ids)
            attention = list(chunk.attention_mask)
            type_ids = list(chunk.type_ids)
            special = list(getattr(chunk, "special_tokens_mask", [0] * len(ids)))
            if not ids or not (len(ids) == len(attention) == len(type_ids) == len(special)):
                raise EmbeddingError("tokenizer returned invalid chunk dimensions")
            attended = [index for index, included in enumerate(attention) if included]
            if not attended:
                raise EmbeddingError("tokenizer returned an empty chunk")
            used = attended[-1] + 1
            ids, attention, type_ids, special = (
                ids[:used], attention[:used], type_ids[:used], special[:used])
            if len(ids) > MAX_TOKENS:
                raise EmbeddingError("tokenizer returned a chunk over the model token limit")
            weight = sum(1 for included, is_special in zip(attention, special)
                         if included and not is_special)
            if weight <= 0:
                weight = sum(1 for included in attention if included)
            rows.append({
                "owner": owner,
                "ids": ids,
                "attention_mask": attention,
                "type_ids": type_ids,
                "weight": weight,
            })
    if not rows or {row["owner"] for row in rows} != set(range(len(texts))):
        raise EmbeddingError("tokenizer did not produce chunks for every input")
    return rows


def _aggregate_chunk_vectors(chunk_vectors, rows, record_count):
    if len(chunk_vectors) != len(rows):
        raise EmbeddingError("model returned the wrong chunk batch size")
    totals = [[0.0] * DIMENSIONS for _ in range(record_count)]
    weights = [0.0] * record_count
    for vector, row in zip(chunk_vectors, rows):
        if not isinstance(vector, (list, tuple)) or len(vector) != DIMENSIONS:
            raise EmbeddingError("model returned invalid chunk vector dimensions")
        owner = row["owner"]
        weight = float(row["weight"])
        if not 0 <= owner < record_count or not math.isfinite(weight) or weight <= 0:
            raise EmbeddingError("tokenizer returned invalid chunk ownership")
        weights[owner] += weight
        for index, value in enumerate(vector):
            numeric = float(value)
            if not math.isfinite(numeric):
                raise EmbeddingError("model returned non-finite chunk vectors")
            totals[owner][index] += numeric * weight
    result = []
    for total, weight in zip(totals, weights):
        if weight <= 0:
            raise EmbeddingError("tokenizer returned no weighted chunks for an input")
        averaged = [value / weight for value in total]
        norm = math.sqrt(sum(value * value for value in averaged))
        if not math.isfinite(norm) or norm <= 0:
            raise EmbeddingError("aggregated record embedding has zero or non-finite length")
        result.append([value / norm for value in averaged])
    return result


def _run_chunk_batch(rows, runtime, session, pad_id):
    np, _ort, _Tokenizer = runtime
    width = max(len(row["ids"]) for row in rows)
    input_ids = np.asarray(
        [row["ids"] + [pad_id] * (width - len(row["ids"])) for row in rows],
        dtype=np.int64,
    )
    attention_mask = np.asarray(
        [row["attention_mask"] + [0] * (width - len(row["attention_mask"])) for row in rows],
        dtype=np.int64,
    )
    token_type_ids = np.asarray(
        [row["type_ids"] + [0] * (width - len(row["type_ids"])) for row in rows],
        dtype=np.int64,
    )
    available = {
        "input_ids": input_ids,
        "attention_mask": attention_mask,
        "token_type_ids": token_type_ids,
    }
    names = [model_input.name for model_input in session.get_inputs()]
    unknown = [name for name in names if name not in available]
    if unknown or "input_ids" not in names or "attention_mask" not in names:
        raise EmbeddingError("pinned ONNX model has unexpected inputs")
    try:
        outputs = session.run(None, {name: available[name] for name in names})
    except Exception as exc:
        raise EmbeddingError("local ONNX inference failed") from exc
    candidates = []
    for output in outputs:
        shape = getattr(output, "shape", None)
        if shape is not None and len(shape) == 3 and shape[0] == len(rows) and shape[2] == DIMENSIONS:
            candidates.append(output)
        elif isinstance(output, (list, tuple)) and len(output) == len(rows):
            if output and isinstance(output[0], (list, tuple)) and output[0]:
                first = output[0][0]
                if isinstance(first, (list, tuple)) and len(first) == DIMENSIONS:
                    candidates.append(output)
    if len(candidates) != 1:
        raise EmbeddingError("pinned ONNX model returned unexpected outputs")
    use_numpy = None if isinstance(candidates[0], (list, tuple)) else np
    return _mean_pool_and_normalize(candidates[0], attention_mask, use_numpy)


def _encode_with_model(texts, runtime, tokenizer, session):
    rows = _chunk_rows(texts, tokenizer)
    pad_id = tokenizer.token_to_id("[PAD]")
    if pad_id is None:
        raise EmbeddingError("pinned tokenizer has no [PAD] token")
    chunk_vectors = []
    for offset in range(0, len(rows), MAX_CHUNKS_PER_INFERENCE):
        selected = rows[offset:offset + MAX_CHUNKS_PER_INFERENCE]
        chunk_vectors.extend(_run_chunk_batch(selected, runtime, session, pad_id))
    return _aggregate_chunk_vectors(chunk_vectors, rows, len(texts))


def encode_texts(texts, model_dir=None):
    """Encode locally, aggregating all 256-token chunks into one vector per text."""
    _validate_texts(texts)
    runtime = _load_runtime()
    resolved = _validate_model(model_dir)
    tokenizer, session = _load_model(resolved, runtime)
    vectors = _encode_with_model(texts, runtime, tokenizer, session)
    spec = model_spec()
    return {
        "model_fingerprint": spec["model_fingerprint"],
        "model_id": spec["model_id"],
        "model_revision": spec["model_revision"],
        "dimensions": DIMENSIONS,
        "vectors": vectors,
    }


def _private_python():
    root = _copilot_home() / "margo/embeddings/venv"
    return root / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def _sanitize_stderr(value, texts):
    message = value or ""
    for text in texts:
        message = message.replace(text, "[redacted]")
    message = "".join(character if character in "\t\n" or ord(character) >= 32 else " "
                      for character in message)
    lines = [line.strip() for line in message.splitlines() if line.strip()]
    return " | ".join(lines[:3])[:1000] or "embedding subprocess failed"


def _validate_result(result, expected_count):
    spec = model_spec()
    if not isinstance(result, dict):
        raise EmbeddingError("embedding subprocess returned malformed JSON")
    if result.get("model_fingerprint") != spec["model_fingerprint"]:
        raise EmbeddingError("embedding subprocess returned an unexpected model fingerprint")
    if result.get("model_id") != MODEL_ID or result.get("model_revision") != MODEL_REVISION:
        raise EmbeddingError("embedding subprocess returned an unexpected model identity")
    if result.get("dimensions") != DIMENSIONS:
        raise EmbeddingError("embedding subprocess returned unexpected dimensions")
    vectors = result.get("vectors")
    if not isinstance(vectors, list) or len(vectors) != expected_count:
        raise EmbeddingError("embedding subprocess returned the wrong batch size")
    clean = []
    for vector in vectors:
        if not isinstance(vector, list) or len(vector) != DIMENSIONS:
            raise EmbeddingError("embedding subprocess returned invalid vector dimensions")
        if not all(isinstance(value, (int, float)) and not isinstance(value, bool)
                   and math.isfinite(value) for value in vector):
            raise EmbeddingError("embedding subprocess returned non-finite vectors")
        clean.append([float(value) for value in vector])
    return {
        "model_fingerprint": spec["model_fingerprint"],
        "model_id": MODEL_ID,
        "model_revision": MODEL_REVISION,
        "dimensions": DIMENSIONS,
        "vectors": clean,
    }


def encode_local(texts):
    """Use local dependencies or the exact private venv; never fall back to a service."""
    _validate_texts(texts)
    if _runtime_available():
        return _validate_result(encode_texts(texts), len(texts))
    python = _private_python()
    if not python.is_file():
        raise EmbeddingError(
            "missing_runtime: expected the private embedding interpreter at " + str(python))
    command = [str(python), "-B", str(Path(__file__).resolve()), "encode", "--input", "-"]
    environment = os.environ.copy()
    environment.update({
        "DO_NOT_TRACK": "1",
        "HF_HUB_DISABLE_TELEMETRY": "1",
        "HF_HUB_OFFLINE": "1",
        "TRANSFORMERS_OFFLINE": "1",
    })
    try:
        completed = subprocess.run(
            command,
            input=_canonical_json({"texts": texts}),
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=180,
            check=False,
            env=environment,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise EmbeddingError("failed to run the private embedding interpreter") from exc
    if completed.returncode:
        raise EmbeddingError(_sanitize_stderr(completed.stderr, texts))
    if len(completed.stdout.encode("utf-8")) > MAX_SUBPROCESS_OUTPUT_BYTES:
        raise EmbeddingError("embedding subprocess output exceeded the safety limit")
    try:
        result = json.loads(completed.stdout)
    except (TypeError, json.JSONDecodeError) as exc:
        raise EmbeddingError("embedding subprocess returned malformed JSON") from exc
    return _validate_result(result, len(texts))


def status(model_dir=None):
    runtime_available = _runtime_available()
    model_available = True
    model_error = None
    try:
        resolved = _validate_model(model_dir)
    except EmbeddingError as exc:
        resolved = _safe_model_path(model_dir)
        model_available = False
        model_error = str(exc)
    state = "available"
    if not runtime_available:
        state = "missing_runtime"
    elif not model_available:
        state = "missing_model"
    result = dict(model_spec())
    result.update({
        "status": state,
        "runtime_available": runtime_available,
        "model_available": model_available,
        "model_dir": str(resolved),
        "configured": _private_python().is_file() or resolved.exists(),
    })
    if model_error:
        result["model_error"] = model_error
    return result


def status_local():
    """Inspect the same runtime encode_local would actually use."""
    if _runtime_available() or not _private_python().is_file():
        return status()
    try:
        result = subprocess.run(
            [str(_private_python()), "-B", str(Path(__file__).resolve()), "status"],
            text=True, capture_output=True, timeout=30, check=False,
            env=dict(os.environ, HF_HUB_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1", DO_NOT_TRACK="1"),
        )
        if result.returncode:
            raise EmbeddingError("the private embedding runtime failed its status request")
        value = json.loads(result.stdout)
        if not isinstance(value, dict) or value.get("model_fingerprint") != model_spec()["model_fingerprint"]:
            raise EmbeddingError("private runtime status has a mismatched model fingerprint")
        return value
    except (OSError, subprocess.SubprocessError, json.JSONDecodeError) as exc:
        raise EmbeddingError("could not inspect the private embedding runtime") from exc


def _read_cli_input():
    raw = sys.stdin.buffer.read(MAX_STDIN_BYTES + 1)
    if len(raw) > MAX_STDIN_BYTES:
        raise EmbeddingError("input exceeded the safety limit")
    try:
        payload = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise EmbeddingError("input must be valid UTF-8 JSON") from exc
    if not isinstance(payload, dict) or set(payload) != {"texts"}:
        raise EmbeddingError("input must be a JSON object containing only texts")
    return _validate_texts(payload["texts"])


def build_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    download = commands.add_parser("download", help="download and verify the pinned public model")
    download.add_argument("--model-dir")
    download.add_argument("--timeout", type=float, default=DOWNLOAD_TIMEOUT_SECONDS)
    encode = commands.add_parser(
        "encode", help="read {\"texts\":[...]} from stdin; token-chunks long inputs")
    encode.add_argument("--input", choices=("-",), required=True)
    encode.add_argument("--model-dir")
    inspect = commands.add_parser("status", help="report local runtime and model availability")
    inspect.add_argument("--model-dir")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    try:
        if args.command == "download":
            result = download_model(args.model_dir, timeout=args.timeout)
        elif args.command == "status":
            result = status(args.model_dir)
        else:
            result = encode_texts(_read_cli_input(), model_dir=args.model_dir)
        sys.stdout.write(_canonical_json(result) + "\n")
        return 0
    except EmbeddingError as exc:
        sys.stderr.write("memory_encoder: " + _sanitize_stderr(str(exc), []) + "\n")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())

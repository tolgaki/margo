using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Memory;

/// <summary><c>memory_encoder.EmbeddingError</c>: a safe, user-actionable local embedding failure (a <c>RuntimeError</c>).</summary>
public sealed class EmbeddingError : PyException
{
    public EmbeddingError(string message, Exception? inner = null) : base(message, inner)
    {
    }

    public override string PythonType => "EmbeddingError";
}

/// <summary>
/// Port of the observable behaviour of <c>skills/chief-of-staff/scripts/memory_encoder.py</c> in Margo Cloud,
/// where local embeddings are never available: no ONNX model, no <c>onnxruntime</c>/<c>tokenizers</c> runtime, no
/// private embedding interpreter is ever executed and nothing is downloaded. <see cref="Status"/>,
/// <see cref="StatusLocal"/> and <see cref="EncodeLocal"/> behave exactly as Python does when the runtime is
/// absent (model directory checks, manifest and asset verification included); the pinned model identity
/// (<see cref="ModelSpec"/>) is the one the semantic index records. <c>download_model</c>, <c>encode_texts</c> and the
/// ONNX pooling/chunking helpers are intentionally not ported (see PORTING.md).
/// </summary>
public static class MemoryEncoder
{
    /// <summary>The module docstring.</summary>
    public const string Doc =
        "Offline sentence embeddings backed by a pinned ONNX MiniLM model.\n"
        + "\n"
        + "The model is never downloaded during inference. Run the explicit ``download``\n"
        + "command first. Long inputs are split into non-overlapping 256-token windows;\n"
        + "their normalized vectors are token-count-weighted, averaged, and normalized\n"
        + "again so every input still produces exactly one record vector.\n";

    public const string ModelId = "sentence-transformers/all-MiniLM-L6-v2";
    public const string ModelRevision = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41";
    public const int Dimensions = 384;
    public const int MaxTokens = 256;
    public const int MaxBatchSize = 32;
    public const int MaxTextChars = 16384;
    public const int MaxChunksPerInference = 64;
    public const int MaxStdinBytes = 1024 * 1024;
    public const int MaxSubprocessOutputBytes = 2 * 1024 * 1024;
    public const int DownloadAttempts = 3;
    public const int DownloadTimeoutSeconds = 30;

    /// <summary><c>PREPROCESSING</c> (a fresh copy on every read).</summary>
    public static JsonObject Preprocessing => new()
    {
        ["padding"] = "longest",
        ["chunking"] = new JsonObject
        {
            ["strategy"] = "tokenizer_overflow",
            ["window_tokens_including_special_tokens"] = (long)MaxTokens,
            ["stride"] = 0L,
        },
        ["pooling"] = "attention_mask_mean",
        ["chunk_normalization"] = "l2",
        ["record_aggregation"] = "non_special_token_count_weighted_mean_then_l2",
    };

    /// <summary>One pinned model asset: path, size, checksum algorithm and digest.</summary>
    public sealed record Asset(string Path, long Size, string Algorithm, string Digest)
    {
        /// <summary><c>dict(asset)</c>.</summary>
        public JsonObject ToJson() => new()
        {
            ["path"] = Path,
            ["size"] = Size,
            ["algorithm"] = Algorithm,
            ["digest"] = Digest,
        };
    }

    /// <summary><c>ASSETS</c>.</summary>
    public static IReadOnlyList<Asset> Assets { get; } =
    [
        new("onnx/model.onnx", 90405214, "sha256", "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"),
        new("tokenizer.json", 466247, "git-sha1", "cb202bfe2e3c98645018a6d12f182a434c9d3e02"),
        new("tokenizer_config.json", 350, "git-sha1", "c79f2b6a0cea6f4b564fed1938984bace9d30ff0"),
        new("config.json", 612, "git-sha1", "72b987fd805cfa2b58c4c8c952b274a11bfd5a00"),
    ];

    /// <summary>
    /// <c>_canonical_json(value)</c>: <c>json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)</c>
    /// (unlike <c>margo_store.canonical_json</c>, NaN is allowed and errors are not wrapped).
    /// </summary>
    public static string CanonicalJson(JsonNode? value) =>
        PyJson.Dumps(value, itemSeparator: ",", keySeparator: ":", sortKeys: true, ensureAscii: false);

    /// <summary><c>_identity_payload()</c>.</summary>
    public static JsonObject IdentityPayload()
    {
        var tokenizer = Assets.First(asset => asset.Path == "tokenizer.json");
        return new JsonObject
        {
            ["model_id"] = ModelId,
            ["model_revision"] = ModelRevision,
            ["dimensions"] = (long)Dimensions,
            ["preprocessing"] = Preprocessing,
            ["tokenizer"] = new JsonObject
            {
                ["algorithm"] = tokenizer.Algorithm,
                ["digest"] = tokenizer.Digest,
            },
        };
    }

    /// <summary><c>model_spec()</c>: the stable identity expected by the semantic index.</summary>
    public static JsonObject ModelSpec()
    {
        var identity = IdentityPayload();
        var fingerprint = PyIds.Sha256Hex(CanonicalJson(identity));
        var preprocessing = Preprocessing;
        return new JsonObject
        {
            ["model_id"] = ModelId,
            ["model_revision"] = ModelRevision,
            ["model_fingerprint"] = fingerprint,
            ["dimensions"] = (long)Dimensions,
            ["max_tokens"] = (long)MaxTokens,
            ["pooling"] = Py.Clone(preprocessing["pooling"]),
            ["normalization"] = Py.Clone(preprocessing["chunk_normalization"]),
            ["long_text_strategy"] = Py.Clone(preprocessing["record_aggregation"]),
            ["tokenizer_hash"] = Py.Clone(identity["tokenizer"]!["digest"]),
        };
    }

    /// <summary><c>_expected_manifest()</c>.</summary>
    public static JsonObject ExpectedManifest() => new()
    {
        ["format_version"] = 1L,
        ["identity"] = IdentityPayload(),
        ["model_fingerprint"] = Py.Clone(ModelSpec()["model_fingerprint"]),
        ["assets"] = new JsonArray(Assets.Select(asset => (JsonNode?)asset.ToJson()).ToArray()),
    };

    /// <summary><c>_copilot_home()</c>: <c>COPILOT_HOME</c> (default <c>~/.copilot</c>), expanded and made absolute (not resolved).</summary>
    public static string CopilotHome() => PyPath.Absolute(PyPath.ExpandUser(PyRuntime.GetEnv("COPILOT_HOME") ?? "~/.copilot"));

    /// <summary><c>default_model_dir()</c>.</summary>
    public static string DefaultModelDir()
    {
        var overridden = PyRuntime.GetEnv("MARGO_EMBEDDING_MODEL_DIR");
        if (!string.IsNullOrEmpty(overridden))
        {
            return PyPath.Absolute(PyPath.ExpandUser(overridden));
        }

        return PyPath.Absolute(PyPath.Join(PyPath.Join(CopilotHome(), "margo/embeddings/models/minilm-l6-v2"), ModelRevision));
    }

    /// <summary><c>_safe_model_path(model_dir)</c>: the resolved, validated model directory.</summary>
    public static string SafeModelPath(string? modelDir = null)
    {
        var path = modelDir is not null ? PyPath.Absolute(PyPath.ExpandUser(modelDir)) : DefaultModelDir();
        var explicitOverride = modelDir is not null || !string.IsNullOrEmpty(PyRuntime.GetEnv("MARGO_EMBEDDING_MODEL_DIR"));
        try
        {
            return StateStore.SafeDirectory(path, explicitOverride);
        }
        catch (Exception ex) when (ex is PyException or InvalidOperationException)
        {
            throw new EmbeddingError("unsafe model directory: " + ex.Message, ex);
        }
    }

    /// <summary><c>_private_file(path)</c>: a private, regular, single-link file owned by the current user.</summary>
    public static PyStat PrivateFile(string path)
    {
        if (PyPath.IsSymlink(path))
        {
            throw new EmbeddingError("model assets must not be symlinks");
        }

        PyStat info;
        try
        {
            info = PosixNative.Stat(path);
        }
        catch (PyOSError ex)
        {
            throw new EmbeddingError("missing_model: run the explicit download command", ex);
        }

        if (!info.IsRegular)
        {
            throw new EmbeddingError("model asset has the wrong file type");
        }

        if (info.Uid != PosixNative.GetUid())
        {
            throw new EmbeddingError("model assets must belong to the current user");
        }

        if ((info.Permissions & 0x3F) != 0)
        {
            throw new EmbeddingError("model assets must be private (0600)");
        }

        if (info.NLink != 1)
        {
            throw new EmbeddingError("hard-linked model assets are not supported");
        }

        return info;
    }

    /// <summary><c>_digest_bytes(data, algorithm)</c>: <c>sha256</c>, or <c>git-sha1</c> (a Git blob id).</summary>
    public static string DigestBytes(ReadOnlySpan<byte> data, string algorithm)
    {
        if (algorithm == "sha256")
        {
            return PyIds.Sha256Hex(data);
        }

        if (algorithm == "git-sha1")
        {
            var header = System.Text.Encoding.ASCII.GetBytes("blob " + data.Length.ToString(CultureInfo.InvariantCulture) + "\0");
            var whole = new byte[header.Length + data.Length];
            header.CopyTo(whole, 0);
            data.CopyTo(whole.AsSpan(header.Length));
            return PyIds.Sha1Hex(whole);
        }

        throw new EmbeddingError("unsupported model asset checksum");
    }

    /// <summary><c>_verify_asset(path, asset)</c>.</summary>
    public static bool VerifyAsset(string path, Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        PyStat info;
        try
        {
            info = PrivateFile(path);
        }
        catch (EmbeddingError)
        {
            return false;
        }

        if (info.Size != asset.Size)
        {
            return false;
        }

        if (asset.Algorithm is not ("sha256" or "git-sha1"))
        {
            throw new EmbeddingError("unsupported model asset checksum");
        }

        try
        {
            using var hasher = IncrementalHash.CreateHash(asset.Algorithm == "sha256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
            if (asset.Algorithm == "git-sha1")
            {
                hasher.AppendData(System.Text.Encoding.ASCII.GetBytes("blob " + asset.Size.ToString(CultureInfo.InvariantCulture) + "\0"));
            }

            var fd = MemoryNative.OpenRead(path);
            try
            {
                while (true)
                {
                    var chunk = MemoryNative.Read(fd, 1024 * 1024);
                    if (chunk.Length == 0)
                    {
                        break;
                    }

                    hasher.AppendData(chunk);
                }
            }
            finally
            {
                PosixNative.Close(fd);
            }

            return Convert.ToHexStringLower(hasher.GetHashAndReset()) == asset.Digest;
        }
        catch (PyOSError)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>_read_manifest(model_dir)</c>: the parsed private manifest, or null when it is missing, not private, not UTF-8
    /// or not JSON (a JSON <c>null</c> manifest is null too, as in Python). Other JSON errors (digit limit, depth) escape.
    /// </summary>
    public static JsonNode? ReadManifest(string modelDir)
    {
        ArgumentNullException.ThrowIfNull(modelDir);
        var path = PyPath.Join(modelDir, "manifest.json");
        try
        {
            PrivateFile(path);
            return PyJson.Loads(PyIO.ReadTextFile(path));
        }
        catch (Exception ex) when (ex is EmbeddingError or PyOSError or PyValueError)
        {
            // EmbeddingError, OSError, UnicodeError and json.JSONDecodeError (both ValueErrors) mean "no manifest".
            if (ex is PyValueError and not (PyUnicodeError or PyJsonDecodeError))
            {
                throw;
            }

            return null;
        }
    }

    /// <summary><c>_validate_model(model_dir)</c>: the verified model directory, or <see cref="EmbeddingError"/>.</summary>
    public static string ValidateModel(string? modelDir = null)
    {
        var resolved = SafeModelPath(modelDir);
        if (!PyPath.Exists(resolved) || !PyJson.PyEquals(ReadManifest(resolved), ExpectedManifest()))
        {
            throw new EmbeddingError("missing_model: run the explicit download command");
        }

        foreach (var asset in Assets)
        {
            if (!VerifyAsset(PyPath.Join(resolved, asset.Path), asset))
            {
                throw new EmbeddingError("missing_model: a pinned model asset failed identity verification");
            }
        }

        return resolved;
    }

    /// <summary>
    /// <c>_runtime_available()</c>: always false in Margo Cloud. The hosted image never ships numpy/onnxruntime/
    /// tokenizers for the memory encoder (autopilot parity: "optional local embeddings are not installed").
    /// </summary>
    public static bool RuntimeAvailable() => false;

    /// <summary><c>_validate_texts(texts)</c>.</summary>
    public static JsonArray ValidateTexts(JsonNode? texts)
    {
        if (texts is not JsonArray list || list.Count == 0 || list.Count > MaxBatchSize)
        {
            throw new EmbeddingError("texts must be a nonempty list with at most " + MaxBatchSize.ToString(CultureInfo.InvariantCulture) + " items");
        }

        foreach (var text in list)
        {
            if (!Py.IsText(text))
            {
                throw new EmbeddingError("each text must be a nonempty string");
            }

            if (PyRepr.Length(Py.AsString(text)!) > MaxTextChars)
            {
                throw new EmbeddingError("text exceeds " + MaxTextChars.ToString(CultureInfo.InvariantCulture)
                                         + " characters; chunk lengthy memories before encoding");
            }
        }

        return list;
    }

    /// <summary><c>_private_python()</c>: where desktop Margo's private embedding interpreter would live.</summary>
    public static string PrivatePython() => PyPath.Join(PyPath.Join(CopilotHome(), "margo/embeddings/venv"), "bin/python");

    /// <summary>
    /// <c>encode_local(texts)</c> without a runtime: validates the texts, then fails. Python would run the private
    /// interpreter when one exists; Margo Cloud never executes it and fails as Python does when it cannot be run.
    /// </summary>
    public static JsonObject EncodeLocal(JsonNode? texts)
    {
        ValidateTexts(texts);
        var python = PrivatePython();
        if (!PyPath.IsFile(python))
        {
            throw new EmbeddingError("missing_runtime: expected the private embedding interpreter at " + python);
        }

        throw new EmbeddingError("failed to run the private embedding interpreter");
    }

    /// <summary><c>status(model_dir)</c>: runtime and model availability (the runtime is never available here).</summary>
    public static JsonObject Status(string? modelDir = null)
    {
        var runtimeAvailable = RuntimeAvailable();
        var modelAvailable = true;
        string? modelError = null;
        string resolved;
        try
        {
            resolved = ValidateModel(modelDir);
        }
        catch (EmbeddingError ex)
        {
            resolved = SafeModelPath(modelDir);
            modelAvailable = false;
            modelError = ex.Message;
        }

        var state = "available";
        if (!runtimeAvailable)
        {
            state = "missing_runtime";
        }
        else if (!modelAvailable)
        {
            state = "missing_model";
        }

        var result = ModelSpec();
        result["status"] = state;
        result["runtime_available"] = runtimeAvailable;
        result["model_available"] = modelAvailable;
        result["model_dir"] = resolved;
        result["configured"] = PyPath.IsFile(PrivatePython()) || PyPath.Exists(resolved);
        if (!string.IsNullOrEmpty(modelError))
        {
            result["model_error"] = modelError;
        }

        return result;
    }

    /// <summary>
    /// <c>status_local()</c>: the status of the runtime <see cref="EncodeLocal"/> would use. Without a private
    /// interpreter this is <see cref="Status"/>; Margo Cloud never runs one that exists, and fails as Python does
    /// when it cannot inspect it.
    /// </summary>
    public static JsonObject StatusLocal()
    {
        if (RuntimeAvailable() || !PyPath.IsFile(PrivatePython()))
        {
            return Status();
        }

        throw new EmbeddingError("could not inspect the private embedding runtime");
    }
}

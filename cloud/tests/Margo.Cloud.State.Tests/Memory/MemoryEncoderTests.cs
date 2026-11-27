using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Memory;
using Xunit;

namespace Margo.Cloud.State.Tests.Memory;

/// <summary>
/// Ports of tests/test_memory_encoder.py for the paths Margo Cloud keeps (identity, checksums, input validation,
/// status without a runtime), plus the guarantee that no private interpreter is ever executed.
/// </summary>
public sealed class MemoryEncoderTests
{
    [Fact]
    public void Pinned_checksums_and_fingerprint()
    {
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", MemoryEncoder.DigestBytes("hello\n"u8, "git-sha1"));
        Assert.Equal(PyIds.Sha256Hex("hello\n"), MemoryEncoder.DigestBytes("hello\n"u8, "sha256"));
        Assert.Throws<EmbeddingError>(() => MemoryEncoder.DigestBytes("hello\n"u8, "md5"));
        var spec = MemoryEncoder.ModelSpec();
        Assert.Equal(384L, (long)spec["dimensions"]!);
        Assert.Equal(MemoryEncoder.ModelRevision, (string)spec["model_revision"]!);
        // The value Python computes (cloud/contracts/state/memory/encoder_constants.json judges it byte for byte).
        Assert.Equal("c9a0f29515bef958e566e05d056518f8b4b47bd37afa4d07170c8f90074ba2b1", (string)spec["model_fingerprint"]!);
        Assert.Equal((string)spec["model_fingerprint"]!, (string)MemoryEncoder.ExpectedManifest()["model_fingerprint"]!);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"  \"]")]
    [InlineData("[1]")]
    [InlineData("{\"texts\":[\"x\"]}")]
    public void Input_validation_requires_bounded_nonempty_strings(string json)
    {
        Assert.Throws<EmbeddingError>(() => MemoryEncoder.ValidateTexts(JsonNode.Parse(json)));
    }

    [Fact]
    public void Input_validation_limits_batch_size_and_text_length_in_code_points()
    {
        Assert.Throws<EmbeddingError>(() => MemoryEncoder.ValidateTexts(new JsonArray(Enumerable.Repeat<JsonNode?>("ok", 33).Select(n => (JsonNode?)JsonValue.Create("ok")).ToArray())));
        Assert.Throws<EmbeddingError>(() => MemoryEncoder.ValidateTexts(new JsonArray(new string('x', MemoryEncoder.MaxTextChars + 1))));
        Assert.Single(MemoryEncoder.ValidateTexts(new JsonArray(string.Concat(Enumerable.Repeat("\U0001F600", MemoryEncoder.MaxTextChars)))));
    }

    [Fact]
    public void Status_reports_the_missing_runtime_and_model()
    {
        using var root = TempRoot.Create();
        var missing = root.Combine("missing");
        var status = MemoryEncoder.Status(missing);
        Assert.Equal("missing_runtime", (string)status["status"]!);
        Assert.False((bool)status["runtime_available"]!);
        Assert.False((bool)status["model_available"]!);
        Assert.False((bool)status["configured"]!);
        Assert.Equal("missing_model: run the explicit download command", (string)status["model_error"]!);
        Assert.False(MemoryEncoder.RuntimeAvailable());
        var error = Assert.Throws<EmbeddingError>(() => MemoryEncoder.Status());
        Assert.StartsWith("unsafe model directory: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_interpreter_is_never_executed()
    {
        using var root = TempRoot.Create(new Dictionary<string, string>(StringComparer.Ordinal) { ["COPILOT_HOME"] = "" });
        var copilot = root.Combine("copilot");
        using var environment = TempRoot.With(("COPILOT_HOME", copilot));
        var bin = Path.Combine(copilot, "margo", "embeddings", "venv", "bin");
        Directory.CreateDirectory(bin);
        var marker = root.Combine("executed");
        var python = Path.Combine(bin, "python");
        File.WriteAllText(python, "#!/bin/sh\ntouch '" + marker + "'\necho '{}'\n");
        Modes.Set(python, "700");
        Assert.Equal(python, MemoryEncoder.PrivatePython());

        var encode = Assert.Throws<EmbeddingError>(() => MemoryEncoder.EncodeLocal(new JsonArray("Synthetic text")));
        Assert.Equal("failed to run the private embedding interpreter", encode.Message);
        var status = Assert.Throws<EmbeddingError>(() => MemoryEncoder.StatusLocal());
        Assert.Equal("could not inspect the private embedding runtime", status.Message);
        Assert.True((bool)MemoryEncoder.Status(root.Combine("model"))["configured"]!);
        Assert.False(File.Exists(marker));

        File.Delete(python);
        var missing = Assert.Throws<EmbeddingError>(() => MemoryEncoder.EncodeLocal(new JsonArray("Synthetic text")));
        Assert.Equal("missing_runtime: expected the private embedding interpreter at " + python, missing.Message);
    }

    [Fact]
    public void An_expected_manifest_still_needs_verified_assets()
    {
        using var root = TempRoot.Create();
        var model = root.Combine("model");
        Directory.CreateDirectory(model);
        Modes.Set(model, "700");
        var manifest = Path.Combine(model, "manifest.json");
        File.WriteAllText(manifest, MemoryEncoder.CanonicalJson(MemoryEncoder.ExpectedManifest()) + "\n");
        Modes.Set(manifest, "600");
        Assert.True(PyJson.PyEquals(MemoryEncoder.ExpectedManifest(), MemoryEncoder.ReadManifest(model)));
        var error = Assert.Throws<EmbeddingError>(() => MemoryEncoder.ValidateModel(model));
        Assert.Equal("missing_model: a pinned model asset failed identity verification", error.Message);
        Modes.Set(manifest, "644");
        Assert.Null(MemoryEncoder.ReadManifest(model));
        Assert.Equal("missing_model: run the explicit download command", (string)MemoryEncoder.Status(model)["model_error"]!);
    }

    [Fact]
    public void Asset_verification_checks_mode_size_and_git_blob_digest()
    {
        using var root = TempRoot.Create();
        var path = root.Combine("config.json");
        var content = "hello\n"u8.ToArray();
        File.WriteAllBytes(path, content);
        Modes.Set(path, "600");
        var asset = new MemoryEncoder.Asset("config.json", content.Length, "git-sha1", "ce013625030ba8dba906f756967f9e9ca394464a");
        Assert.True(MemoryEncoder.VerifyAsset(path, asset));
        Assert.False(MemoryEncoder.VerifyAsset(path, asset with { Size = 7 }));
        Assert.False(MemoryEncoder.VerifyAsset(path, asset with { Digest = new string('0', 40) }));
        Modes.Set(path, "640");
        Assert.False(MemoryEncoder.VerifyAsset(path, asset));
    }
}

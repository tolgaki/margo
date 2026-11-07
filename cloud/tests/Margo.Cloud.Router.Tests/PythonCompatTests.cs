using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>Python behaviour the router depends on, checked against Python 3.13.</summary>
public sealed class PythonCompatTests
{
    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, "utf-32")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "utf-32")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, "utf-16")]
    [InlineData(new byte[] { 0xFE, 0xFF }, "utf-16")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x7B }, "utf-8-sig")]
    [InlineData(new byte[] { 0x00, 0x7B, 0x00, 0x7D }, "utf-16-be")]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x7B }, "utf-32-be")]
    [InlineData(new byte[] { 0x7B, 0x00, 0x7D, 0x00 }, "utf-16-le")]
    [InlineData(new byte[] { 0x7B, 0x00, 0x00, 0x00 }, "utf-32-le")]
    [InlineData(new byte[] { 0x00, 0x7B }, "utf-16-be")]
    [InlineData(new byte[] { 0x7B, 0x00 }, "utf-16-le")]
    [InlineData(new byte[] { 0x7B, 0x00, 0x7D }, "utf-8")]
    [InlineData(new byte[] { 0x7B, 0x7D }, "utf-8")]
    public void Encoding_is_detected_as_json_detect_encoding_does(byte[] data, string expected) =>
        Assert.Equal(expected, PythonJsonBytes.DetectEncoding(data));

    [Fact]
    public void Bytes_decode_with_surrogatepass()
    {
        // json.loads(data) on Python 3.13.
        Assert.Equal("é", PythonJsonBytes.Loads("{\"a\": \"é\"}"u8)!["a"]!.GetValue<string>());
        var surrogate = PythonJsonBytes.Loads(new byte[] { 0x22, 0xED, 0xA0, 0x80, 0x22 })!.GetValue<string>();
        Assert.Equal("\ud800", surrogate);
        var escaped = PythonJsonBytes.Loads("{\"t\": \"x\\ud83d\", \"t\": \"\\udc00y\"}"u8) as JsonObject;
        Assert.Equal("\udc00y", escaped!["t"]!.GetValue<string>());
        Assert.Single(escaped);

        foreach (var bad in (byte[][])[[0x22, 0xC0, 0x80, 0x22], [0x22, 0xED, 0xA0, 0x22], [0x22, 0xF4, 0x90, 0x80, 0x80, 0x22], [0x22, 0xFF, 0x22],
                     [0xFF, 0xFE, 0x22, 0x00, 0x22], [0xFF, 0xFE, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00]])
        {
            Assert.ThrowsAny<JsonException>(() => PythonJsonBytes.Loads(bad));
        }

        // UTF-16LE code units written by hand: .NET's encoder would replace the lone surrogate.
        var utf16 = Encoding.Unicode.GetPreamble().Concat("[\"\ud800\"]".SelectMany(c => (byte[])[(byte)c, (byte)(c >> 8)])).ToArray();
        Assert.Equal("\ud800", PythonJsonBytes.Loads(utf16)![0]!.GetValue<string>());
        var utf32 = new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x31 };
        Assert.Equal(1, PythonJsonBytes.Loads(utf32)!.GetValue<int>());
    }

    [Fact]
    public void Text_python_refuses_is_refused()
    {
        foreach (var bad in (string[])["", "{", "[1,]", "{\"a\": 1} x", "\"\\ud800\" x", "\ufeff{}"])
        {
            Assert.ThrowsAny<JsonException>(() => PythonJsonBytes.LoadsText(bad));
        }

        Assert.ThrowsAny<JsonException>(() => PythonJsonBytes.LoadsText(new string('1', 4301)));
        Assert.ThrowsAny<JsonException>(() => PythonJsonBytes.LoadsText("[\"\\ud800\", " + new string('1', 4301) + "]"));
        Assert.Equal(4300, PythonJsonBytes.LoadsText("[\"\\ud800\", " + new string('1', 4300) + "]")![1]!.ToJsonString().Length);
    }

    [Theory]
    [InlineData("/i/abc/voice/a%2fb?x=1", "/i/abc/voice/a%2Fb", "/i/abc/voice/a/b")]
    [InlineData("/i/%61bc/voice/%2E%2E", "/i/abc/voice/..", "/i/abc/voice/..")]
    [InlineData("/a%252F", "/a%25" + "2F", "/a%2F")]
    [InlineData("/a%ZZ%4", "/a%ZZ%4", "/a%ZZ%4")]
    [InlineData("/a%C3%A9#frag", "/a\u00e9", "/a\u00e9")]
    [InlineData("http://router.example.com/x%41?q", "/xA", "/xA")]
    [InlineData("http://router.example.com", "/", "/")]
    public void Request_paths_are_decoded_as_aiohttp_decodes_them(string raw, string safe, string decoded)
    {
        var path = RequestPath.FromRawTarget(raw);
        Assert.Equal((safe, decoded), (path.Safe, path.Decoded));
    }

    [Fact]
    public void Lower_case_comparison_follows_python()
    {
        Assert.True(PythonCompat.LowerEquals("BEARER", "bearer"));
        Assert.True(PythonCompat.LowerEquals("\u212Aa", "ka")); // KELVIN SIGN lower-cases to k
        Assert.False(PythonCompat.LowerEquals("\u0130", "i")); // "i̇" in Python: two characters
        Assert.False(PythonCompat.LowerEquals("bearer ", "bearer"));
        Assert.True(PythonCompat.LowerEquals("ÄB", "äb"));
    }

    [Theory]
    [InlineData("12", true, 12)]
    [InlineData(" 1_000 ", true, 1000)]
    [InlineData("-7", true, -7)]
    [InlineData("\u0661\u0662", true, 12)]
    [InlineData("1__0", false, 0)]
    [InlineData("_1", false, 0)]
    [InlineData("1.5", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("\u001f5\u001c", false, 0)]
    [InlineData("\u00855\u3000", true, 5)]
    public void Int_of_text_follows_python(string text, bool ok, long value)
    {
        Assert.Equal(ok, PythonCompat.TryParseInt(text, out var result));
        Assert.Equal(value, (long)result);
    }

    [Fact]
    public void Int_of_json_values_follows_python()
    {
        Assert.True(PythonCompat.TryToInt(JsonNode.Parse("100.7"), out var truncated));
        Assert.Equal(100, (long)truncated);
        Assert.True(PythonCompat.TryToInt(JsonNode.Parse("-1.9"), out var negative));
        Assert.Equal(-1, (long)negative);
        Assert.True(PythonCompat.TryToInt(JsonNode.Parse("true"), out var one));
        Assert.Equal(1, (long)one);
        Assert.False(PythonCompat.TryToInt(JsonNode.Parse("1e400"), out _)); // inf: OverflowError
        Assert.False(PythonCompat.TryToInt(null, out _));
        Assert.False(PythonCompat.TryToInt(JsonNode.Parse("[1]"), out _));
    }
}

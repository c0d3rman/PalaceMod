using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PalaceMod.Builder;
using PalaceMod.Builder.Model;
using PalaceMod.Builder.Sharing;

namespace PalaceMod.Tests
{
    public static class ShareCodecTests
    {
        private static readonly ShareCodec Codec = new();

        internal static string SampleJson()
        {
            using var s = typeof(ShareCodecTests).Assembly.GetManifestResourceStream("builder-sample.json");
            return new StreamReader(s).ReadToEnd();
        }

        internal static DesignPack Sample() => PackSerializer.Read(SampleJson(), null) ?? throw new Exception("sample didn't parse");

        private static string Same(DesignPack p) => PackSerializer.WriteCompact(p);

        /// <summary>Builds a TFB1 code around arbitrary payload bytes, with a correct checksum.</summary>
        private static string CodeFor(byte[] json, byte[] deflated = null)
        {
            if (deflated == null)
            {
                using var ms = new MemoryStream();
                using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true)) d.Write(json, 0, json.Length);
                deflated = ms.ToArray();
            }
            uint crc = Crc32.Compute(json);
            var all = deflated.Concat(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }).ToArray();
            return ShareCodec.Prefix + Base64Url.Encode(all);
        }

        private static string Error(string code)
        {
            var issues = new List<ValidationIssue>();
            var pack = Codec.Decode(code, issues);
            Assert.Null(pack, "decoded pack");
            Assert.True(issues.Count == 1 && issues[0].Severity == Severity.Error, "exactly one error issue");
            return issues[0].Message;
        }

        public static void RoundTripsTheSample()
        {
            var sample = Sample();
            string code = Codec.Encode(sample);
            Assert.True(code.StartsWith("TFB1:"), "prefix");
            var issues = new List<ValidationIssue>();
            var back = Codec.Decode(code, issues);
            Assert.NotNull(back, "decoded");
            Assert.Equal(0, issues.Count, "issues");
            Assert.Equal(Same(sample), Same(back), "pack JSON");
        }

        public static void SampleCodeFitsInAChatMessage()
        {
            string code = Codec.Encode(Sample());
            Console.WriteLine($"        sample pack: {SampleJson().Length} bytes of JSON -> {code.Length} chars of code");
            Assert.True(code.Length < ShareCodec.ChatFriendlyChars, $"sample code is {code.Length} chars");
        }

        public static void CodeIsUrlSafeAndSingleLine()
        {
            string code = Codec.Encode(Sample());
            Assert.True(code.Substring(5).All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'), "only base64url characters");
        }

        public static void ToleratesWhitespaceLineBreaksAndSurroundingText()
        {
            string code = Codec.Encode(Sample());
            var wrapped = new StringBuilder();
            for (int i = 0; i < code.Length; i += 50) wrapped.Append(code, i, Math.Min(50, code.Length - i)).Append(i % 100 == 0 ? "\r\n" : " \t");
            Assert.NotNull(Codec.Decode(wrapped.ToString(), null), "wrapped code");
            Assert.NotNull(Codec.Decode("Here's my pack:\n```\n" + code + "\n```\nhave fun", null), "code in a chat message");
            Assert.NotNull(Codec.Decode("\"" + code + "\"", null), "quoted code");
        }

        public static void AcceptsStandardBase64Alphabet()
        {
            string code = Codec.Encode(Sample());
            string std = "TFB1:" + code.Substring(5).Replace('-', '+').Replace('_', '/');
            int pad = (4 - (std.Length - 5) % 4) % 4;
            Assert.NotNull(Codec.Decode(std + new string('=', pad), null), "standard base64 with padding");
        }

        public static void KeepsUnknownFieldsAndAssetRefs()
        {
            var json = SampleJson().TrimEnd().TrimEnd('}') +
                ",\"futureField\":{\"x\":1},\"assets\":{\"icon\":{\"url\":\"https://example.com/i.png\",\"sha256\":\"" + new string('a', 64) + "\"},\"local\":{\"path\":\"assets/l.png\"}}}";
            var pack = PackSerializer.Read(json, null);
            var back = Codec.Decode(Codec.Encode(pack), null);
            Assert.True(back.Extra != null && back.Extra.ContainsKey("futureField"), "unknown field kept");
            Assert.Equal("https://example.com/i.png", back.Assets["icon"].Url, "asset url");
            Assert.Equal(new string('a', 64), back.Assets["icon"].Sha256, "asset hash");
            Assert.Equal("assets/l.png", back.Assets["local"].Path, "path-only asset kept so the receiver sees it missing");
        }

        public static void RejectsEmptyAndNonCodes()
        {
            Assert.True(Error(null).Contains("empty"), "null");
            Assert.True(Error("   \n ").Contains("empty"), "whitespace");
            Assert.True(Error("hello world").Contains("isn't a share code"), "plain text");
            Assert.True(Error("{\"format\":\"palace-builder\"}").Contains("isn't a share code"), "raw JSON");
            Assert.True(Error("TFB1:").Contains("cut off"), "prefix only");
            Assert.True(Error("TFB1:abc").Contains("cut off"), "too short");
        }

        public static void RejectsNewerFormatVersions()
        {
            string code = Codec.Encode(Sample());
            Assert.True(Error("TFB2:" + code.Substring(5)).Contains("newer PalaceMod"), "TFB2");
            Assert.True(Error("TFB0:" + code.Substring(5)).Contains("unknown"), "TFB0");
        }

        public static void RejectsTruncatedAndCorruptedCodes()
        {
            string code = Codec.Encode(Sample());
            Error(code.Substring(0, code.Length - 1));
            Error(code.Substring(0, code.Length / 2));
            // Flip one character at several positions: every corruption must be caught (inflate or checksum).
            for (int i = 6; i < code.Length; i += Math.Max(1, code.Length / 40))
            {
                char c = code[i] == 'A' ? 'B' : 'A';
                Error(code.Substring(0, i) + c + code.Substring(i + 1));
            }
        }

        public static void RejectsChecksumMismatch()
        {
            byte[] json = Encoding.UTF8.GetBytes(Same(Sample()));
            string good = CodeFor(json);
            var bytes = Base64Url.Decode(good.Substring(5));
            bytes[bytes.Length - 1] ^= 0x01;
            Assert.True(Error("TFB1:" + Base64Url.Encode(bytes)).Contains("checksum"), "checksum message");
        }

        public static void RejectsDecompressionBombs()
        {
            byte[] zeros = new byte[ShareCodec.MaxJsonBytes + 1024];
            string code = CodeFor(zeros);
            Assert.True(code.Length < 20000, "bomb is small");
            Assert.True(Error(code).Contains("2 MB"), "size cap message");
        }

        public static void RejectsOversizedInput()
        {
            string huge = "TFB1:" + new string('A', ShareCodec.MaxCodeChars + 10);
            Assert.True(Error(huge).Contains("too long"), "over the char cap");
            Assert.True(Error(new string('x', ShareCodec.MaxCodeChars * 2 + 1)).Contains("too long"), "way over");
        }

        public static void RejectsNonUtf8Payload()
        {
            Assert.True(Error(CodeFor([0xff, 0xfe, 0xfd, 0x80, 0x81, 0x82, 0x83])).Contains("not text"), "invalid UTF-8");
        }

        public static void RejectsDeeplyNestedJson()
        {
            string deep = new string('[', 5000) + new string(']', 5000);
            Error(CodeFor(Encoding.UTF8.GetBytes(deep)));
            string deepObj = "{\"format\":\"palace-builder\",\"version\":1,\"id\":\"x\",\"x\":" + string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 201);
            Error(CodeFor(Encoding.UTF8.GetBytes(deepObj)));
        }

        public static void RejectsWrongShapes()
        {
            Assert.True(Error(CodeFor(Encoding.UTF8.GetBytes("[1,2,3]"))).Contains("doesn't contain"), "array");
            Assert.True(Error(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"something-else\",\"version\":1}"))).Contains("not a Builder pack"), "wrong format");
            Assert.True(Error(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":99}"))).Contains("update PalaceMod"), "newer pack version");
            Error(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":1,\"units\":\"nope\"}")));
            Error(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":1,\"units\":[{\"stats\":{\"maxHp\":\"lots\"}}]}")));
            Error(CodeFor(Encoding.UTF8.GetBytes("{not json")));
            Error(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":1,\"waveSets\":[{\"startingGold\":99999999999999999999999}]}")));
            Error(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":99999999999999999999999}")));
            Error(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":1,\"assets\":[1,2]}")));
            Error(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":1,\"waveSets\":[{\"nights\":[{\"spawns\":[{\"elite\":{}}]}]}]}")));
        }

        public static void NeverThrowsOnRandomInput()
        {
            var rng = new Random(1234);
            string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_+/= \n:TFB";
            for (int i = 0; i < 3000; i++)
            {
                int len = rng.Next(0, 400);
                var sb = new StringBuilder(rng.Next(3) == 0 ? "" : "TFB1:");
                for (int j = 0; j < len; j++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
                Codec.Decode(sb.ToString(), new List<ValidationIssue>());
            }
            for (int i = 0; i < 1000; i++)
            {
                var bytes = new byte[rng.Next(0, 300)];
                rng.NextBytes(bytes);
                Codec.Decode(CodeFor(bytes), new List<ValidationIssue>()); // valid checksum, random JSON bytes
                Codec.Decode("TFB1:" + Base64Url.Encode(bytes), new List<ValidationIssue>()); // random deflate stream
            }
        }

        public static void NullListsBecomeEmpty()
        {
            var pack = Codec.Decode(CodeFor(Encoding.UTF8.GetBytes("{\"format\":\"palace-builder\",\"version\":1,\"id\":\"x\",\"units\":null,\"waveSets\":null,\"assets\":null,\"levels\":null}")), null);
            Assert.NotNull(pack, "pack");
            Assert.True(pack.Units != null && pack.WaveSets != null && pack.Assets != null && pack.Levels != null, "lists not null");
        }

        public static void EncodeRefusesWhatDecodeWouldReject()
        {
            var pack = Sample();
            pack.Description = new string('x', ShareCodec.MaxJsonBytes);
            try
            {
                Codec.Encode(pack);
                throw new AssertException("should refuse");
            }
            catch (InvalidOperationException e)
            {
                Assert.True(e.Message.Contains(".tfpack"), e.Message);
            }
        }

        public static void LooksLikeCode()
        {
            Assert.True(ShareCodec.LooksLikeCode("x TFB1:abc"), "TFB1");
            Assert.True(ShareCodec.LooksLikeCode("TFB12:abc"), "TFB12");
            Assert.True(!ShareCodec.LooksLikeCode("TFB:abc"), "no digit");
            Assert.True(!ShareCodec.LooksLikeCode("TFBx1:abc"), "letter");
        }
    }
}

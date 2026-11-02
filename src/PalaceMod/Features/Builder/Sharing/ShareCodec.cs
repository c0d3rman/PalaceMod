using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PalaceMod.Builder.Model;

/*
    Share codes (docs/builder-design.md, "WP6: sharing"):

        TFB1:<base64url( raw deflate(compact pack JSON) ++ CRC-32 of the JSON, 4 bytes big-endian )>

    The digit after "TFB" is the code format version (not the pack schema version, which lives in the JSON and is
    migrated by PackSerializer). Asset bytes are never embedded: assets keep their url/sha256 (and path, so the
    receiver sees a missing file and falls back to base visuals).

    Decoding treats the input as hostile: length caps before and after inflating (zip bombs), strict base64, a
    checksum, strict UTF-8, a JSON depth cap, then the normal migrate-and-read path. It never throws; failures come
    back as a null pack plus one player-facing error issue.

    No Unity references here, so tests/PalaceMod.Tests can exercise it without the game.
*/
namespace PalaceMod.Builder.Sharing
{
    public sealed class ShareCodec : IShareCodec
    {
        public const string Prefix = "TFB1:";
        public const int FormatVersion = 1;
        /// <summary>Longest code accepted, in characters (after whitespace is removed).</summary>
        public const int MaxCodeChars = 512 * 1024;
        /// <summary>Largest pack JSON a code may inflate to.</summary>
        public const int MaxJsonBytes = 2 * 1024 * 1024;
        /// <summary>Codes above this length still work but are awkward to paste in chat; the UI suggests a .tfpack.</summary>
        public const int ChatFriendlyChars = 2000;

        public string Encode(DesignPack pack)
        {
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            byte[] json = Encoding.UTF8.GetBytes(PackSerializer.WriteCompact(ForSharing(pack)));
            // Never hand out a code Decode would refuse.
            if (json.Length > MaxJsonBytes) throw new InvalidOperationException("this pack is too large for a share code; save it as a .tfpack file instead");
            using var ms = new MemoryStream();
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(json, 0, json.Length);
            uint crc = Crc32.Compute(json);
            ms.WriteByte((byte)(crc >> 24));
            ms.WriteByte((byte)(crc >> 16));
            ms.WriteByte((byte)(crc >> 8));
            ms.WriteByte((byte)crc);
            string payload = Base64Url.Encode(ms.ToArray());
            if (payload.Length > MaxCodeChars) throw new InvalidOperationException("this pack is too large for a share code; save it as a .tfpack file instead");
            return Prefix + payload;
        }

        public DesignPack Decode(string code, List<ValidationIssue> issues)
        {
            string json, error;
            try
            {
                TryDecodeJson(code, out json, out error);
            }
            catch (Exception e) when (!(e is OutOfMemoryException || e is StackOverflowException))
            {
                // Belt and braces: the runtime's inflater differs between .NET and Unity's Mono.
                json = null;
                error = "that share code is damaged (" + e.GetType().Name + ")";
            }
            if (json == null)
            {
                issues?.Add(new ValidationIssue(Severity.Error, "", error));
                return null;
            }
            return SafeJson.ReadPack(json, issues);
        }

        /// <summary>Whether text contains something that looks like a share code (any TFB version).</summary>
        public static bool LooksLikeCode(string text) => text != null && FindPrefix(text) >= 0;

        /// <summary>The code's JSON payload, or false with a player-facing reason.</summary>
        public static bool TryDecodeJson(string text, out string json, out string error)
        {
            json = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "the clipboard is empty"; return false; }
            if (text.Length > MaxCodeChars * 2) { error = "that's far too long to be a share code"; return false; }

            int start = FindPrefix(text);
            if (start < 0) { error = "that isn't a share code (codes start with \"TFB1:\")"; return false; }
            // "TFB<version>:"; versions above ours come from a newer PalaceMod.
            int colon = text.IndexOf(':', start);
            if (!int.TryParse(text.Substring(start + 3, colon - start - 3), out int version) || version != FormatVersion)
            {
                error = version > FormatVersion ? "that code was made with a newer PalaceMod; update PalaceMod to import it" : "unknown share code version";
                return false;
            }

            // Payload: base64url characters after the prefix; whitespace and line breaks (chat wrapping) are skipped,
            // anything else ends the code (e.g. a closing quote or backtick around it).
            var sb = new StringBuilder();
            for (int i = colon + 1; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) continue;
                if (!Base64Url.IsChar(c)) break;
                if (sb.Length >= MaxCodeChars) { error = "that share code is too long"; return false; }
                sb.Append(c);
            }
            if (sb.Length < 8) { error = "that share code is empty or cut off"; return false; }

            byte[] data = Base64Url.Decode(sb.ToString());
            if (data == null || data.Length < 5) { error = "that share code is damaged (bad characters or cut off)"; return false; }

            int bodyLength = data.Length - 4;
            uint expected = (uint)(data[bodyLength] << 24 | data[bodyLength + 1] << 16 | data[bodyLength + 2] << 8 | data[bodyLength + 3]);
            byte[] raw;
            try
            {
                raw = Inflate(data, bodyLength, MaxJsonBytes);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException) // Mono's DeflateStream throws IOException on corrupt data
            {
                error = "that share code is damaged (it doesn't unpack)";
                return false;
            }
            if (raw == null) { error = "that share code unpacks to more than 2 MB; refusing it"; return false; }
            if (Crc32.Compute(raw) != expected) { error = "that share code is damaged or incomplete (checksum mismatch)"; return false; }

            try
            {
                json = new UTF8Encoding(false, true).GetString(raw);
            }
            catch (ArgumentException)
            {
                error = "that share code is damaged (not text)";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>The copy that goes into a code: unchanged except that assets keep only their path/url/sha256.</summary>
        public static DesignPack ForSharing(DesignPack pack)
        {
            var copy = PackSerializer.Clone(pack) ?? throw new ArgumentException("pack can't be serialised");
            copy.Format = DesignPack.FormatName;
            copy.Version = DesignPack.CurrentVersion;
            if (copy.Assets != null)
                foreach (var asset in copy.Assets.Values)
                    if (asset != null) asset.Extra = null;
            return copy;
        }

        private static int FindPrefix(string text)
        {
            for (int i = text.IndexOf("TFB", StringComparison.Ordinal); i >= 0; i = text.IndexOf("TFB", i + 1, StringComparison.Ordinal))
            {
                int j = i + 3;
                while (j < text.Length && j - i < 7 && char.IsDigit(text[j])) j++;
                if (j > i + 3 && j < text.Length && text[j] == ':') return i;
            }
            return -1;
        }

        /// <summary>Inflates raw deflate data, or returns null if the output would exceed maxBytes.</summary>
        internal static byte[] Inflate(byte[] data, int length, int maxBytes)
        {
            using var input = new MemoryStream(data, 0, length, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            return LimitedRead.ReadAll(deflate, maxBytes);
        }
    }

    internal static class LimitedRead
    {
        /// <summary>Reads a stream to the end, or returns null as soon as it yields more than maxBytes.</summary>
        public static byte[] ReadAll(Stream stream, long maxBytes)
        {
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + n > maxBytes) return null;
                output.Write(buffer, 0, n);
            }
            return output.ToArray();
        }
    }

    /// <summary>Pack JSON from untrusted sources: depth-capped parse, then the normal migrate-and-read path.</summary>
    internal static class SafeJson
    {
        public const int MaxDepth = 32;

        public static DesignPack ReadPack(string json, List<ValidationIssue> issues)
        {
            JObject root;
            try
            {
                using var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = MaxDepth, DateParseHandling = DateParseHandling.None };
                var token = JToken.ReadFrom(reader);
                root = token as JObject;
                if (root == null) { issues?.Add(new ValidationIssue(Severity.Error, "", "doesn't contain a design pack")); return null; }
            }
            catch (JsonException e)
            {
                issues?.Add(new ValidationIssue(Severity.Error, "", "isn't a valid design pack: " + e.Message));
                return null;
            }
            try
            {
                var pack = PackSerializer.FromJObject(root, issues);
                if (pack != null)
                {
                    // "units": null etc. would otherwise reach code that expects the model's empty defaults.
                    pack.Assets ??= [];
                    pack.Units ??= [];
                    pack.WaveSets ??= [];
                    pack.Levels ??= [];
                }
                return pack;
            }
            catch (Exception e) when (!(e is OutOfMemoryException || e is StackOverflowException))
            {
                // Hostile input must never escape as an exception; the serializer's own catch covers the known cases.
                issues?.Add(new ValidationIssue(Severity.Error, "", "doesn't match the pack format: " + e.Message));
                return null;
            }
        }
    }

    internal static class Base64Url
    {
        public static bool IsChar(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'
            // Standard base64 too, in case a code went through a tool that "fixed" it.
            || c == '+' || c == '/' || c == '=';

        public static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string s)
        {
            s = s.TrimEnd('=').Replace('-', '+').Replace('_', '/');
            if (s.IndexOf('=') >= 0 || s.Length % 4 == 1) return null;
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            try
            {
                return Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}

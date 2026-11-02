using System;

namespace PalaceMod.Builder.Model
{
    public enum RefKind
    {
        /// <summary>Not a valid reference.</summary>
        Invalid,
        /// <summary>"vanilla:&lt;id&gt;" - a vanilla catalog entry (unit, enemy or weapon).</summary>
        Vanilla,
        /// <summary>"scene:&lt;GameObject name&gt;" - an object in the host scene.</summary>
        Scene,
        /// <summary>"asset:&lt;key&gt;" - an entry of the pack's "assets".</summary>
        Asset,
        /// <summary>"&lt;unitId&gt;" or "unit:&lt;unitId&gt;" - a unit in the same pack.</summary>
        LocalUnit,
        /// <summary>"&lt;packId&gt;/&lt;unitId&gt;" - a unit in another installed pack.</summary>
        PackUnit,
    }

    /// <summary>A parsed typed reference string (see docs/builder-design.md, "References").</summary>
    public readonly struct ContentRef
    {
        public RefKind Kind { get; }
        /// <summary>The id after the prefix (for PackUnit, the unit id).</summary>
        public string Id { get; }
        /// <summary>For PackUnit, the pack id; otherwise null.</summary>
        public string Pack { get; }
        public string Raw { get; }

        private ContentRef(RefKind kind, string id, string pack, string raw) { Kind = kind; Id = id; Pack = pack; Raw = raw; }

        public static ContentRef Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new ContentRef(RefKind.Invalid, null, null, raw);
            raw = raw.Trim();
            int colon = raw.IndexOf(':');
            if (colon > 0)
            {
                string prefix = raw.Substring(0, colon), rest = raw.Substring(colon + 1).Trim();
                if (rest.Length == 0) return new ContentRef(RefKind.Invalid, null, null, raw);
                return prefix switch
                {
                    "vanilla" => new ContentRef(RefKind.Vanilla, rest, null, raw),
                    "scene" => new ContentRef(RefKind.Scene, rest, null, raw),
                    "asset" => new ContentRef(RefKind.Asset, rest, null, raw),
                    "unit" => new ContentRef(RefKind.LocalUnit, rest, null, raw),
                    _ => new ContentRef(RefKind.Invalid, null, null, raw),
                };
            }
            int slash = raw.IndexOf('/');
            if (slash > 0 && slash < raw.Length - 1 && raw.IndexOf('/', slash + 1) < 0)
                return new ContentRef(RefKind.PackUnit, raw.Substring(slash + 1), raw.Substring(0, slash), raw);
            return Ids.IsValid(raw) ? new ContentRef(RefKind.LocalUnit, raw, null, raw) : new ContentRef(RefKind.Invalid, null, null, raw);
        }

        public static string Vanilla(string id) => "vanilla:" + id;
        public static string Asset(string key) => "asset:" + key;

        public override string ToString() => Raw;
    }

    public static class Ids
    {
        /// <summary>Pack, unit, wave set and level ids: 1-64 chars of a-z, 0-9, '_', '-', '.'; starting with a letter or digit.</summary>
        public static bool IsValid(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            if (!char.IsLetterOrDigit(id[0])) return false;
            foreach (char c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }

        /// <summary>Turns a display name into a valid id ("Goblin Runner" -> "goblin-runner").</summary>
        public static string FromName(string name)
        {
            var chars = (name ?? "").ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!((chars[i] >= 'a' && chars[i] <= 'z') || (chars[i] >= '0' && chars[i] <= '9') || chars[i] == '.' || chars[i] == '_')) chars[i] = '-';
            string id = new string(chars).Trim('-', '.', '_');
            while (id.Contains("--")) id = id.Replace("--", "-");
            if (id.Length > 64) id = id.Substring(0, 64);
            return id.Length == 0 ? "design-" + Guid.NewGuid().ToString("N").Substring(0, 8) : id;
        }
    }
}

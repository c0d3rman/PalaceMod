using System;
using System.Globalization;
using System.Text;

/*
    The arithmetic behind ModNumber (Core/UI/ModNumber.cs) and the settings sliders: parsing what the player types,
    stepping on a grid, clamping, and how fast a held key speeds up. No Unity types, so tests/PalaceMod.Tests covers it.
*/
namespace PalaceMod
{
    public static class NumberMath
    {
        /// <summary>
        /// Reads a typed number: "1.5", "1,5" (decimal comma), "1,000" and "1,000,000" (a comma before exactly three
        /// digits groups thousands), "+5", "-3", "10k" (thousands), "x1.5". Whatever follows the number (and a leading
        /// x) is returned as <paramref name="unit"/> for the caller to check against what the field shows ("fps",
        /// "%", "of 15"). False when there's no number at the start.
        /// </summary>
        public static bool TryParse(string text, out double value, out string unit)
        {
            value = 0;
            unit = "";
            string s = text?.Trim() ?? "";
            string prefix = "";
            if (s.Length > 0 && (s[0] == 'x' || s[0] == 'X' || s[0] == '×'))
            {
                prefix = s.Substring(0, 1);
                s = s.Substring(1).TrimStart();
            }
            var number = new StringBuilder();
            int i = 0, digits = 0;
            bool point = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                if (s[i] == '-') number.Append('-');
                i++;
            }
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9')
                {
                    number.Append(c);
                    digits++;
                    i++;
                }
                else if (c == ',' && !point && digits > 0 && GroupsThousands(s, i)) i++;
                else if ((c == '.' || c == ',') && !point)
                {
                    point = true;
                    number.Append('.');
                    i++;
                }
                else break;
            }
            if (digits == 0) return false;
            string rest = s.Substring(i).Trim();
            double scale = 1;
            if (rest == "k" || rest == "K")
            {
                scale = 1000;
                rest = "";
            }
            if (!double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            value *= scale;
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            unit = (prefix + " " + rest).Trim();
            return true;
        }

        // "1,000": a comma followed by exactly three digits, then a non-digit or the end.
        private static bool GroupsThousands(string s, int comma)
        {
            for (int k = 1; k <= 3; k++)
            {
                if (comma + k >= s.Length || s[comma + k] < '0' || s[comma + k] > '9') return false;
            }
            return comma + 4 >= s.Length || s[comma + 4] < '0' || s[comma + 4] > '9';
        }

        /// <summary>Whether a typed unit matches what the field shows: each word of it appears in
        /// <paramref name="shown"/> (rich-text tags ignored, any case). An empty unit always matches.</summary>
        public static bool UnitMatches(string unit, string shown)
        {
            if (string.IsNullOrWhiteSpace(unit)) return true;
            string plain = StripTags(shown ?? "");
            foreach (var word in unit.Split([' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (plain.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        internal static string StripTags(string s)
        {
            var plain = new StringBuilder(s.Length);
            bool inTag = false;
            foreach (char c in s)
            {
                if (c == '<') inTag = true;
                else if (c == '>' && inTag) inTag = false;
                else if (!inTag) plain.Append(c);
            }
            return plain.ToString();
        }

        /// <summary>Clamps to [min, max]; integers round to the nearest whole number, others to 6 decimals.</summary>
        public static double Clamp(double value, double min, double max, bool integer)
        {
            if (max < min) (min, max) = (max, min);
            value = Math.Max(min, Math.Min(max, value));
            return integer ? Math.Round(value, MidpointRounding.AwayFromZero) : Math.Round(value, 6);
        }

        /// <summary>
        /// Moves <paramref name="steps"/> steps on the grid min + k * step. A value off the grid (typed, or from an
        /// older default) goes to the next grid point in that direction first: 144 with step 5 goes to 145 or 140.
        /// </summary>
        public static double Step(double value, double step, int steps, double min, double max, bool integer)
        {
            if (steps == 0 || step <= 0) return Clamp(value, min, max, integer);
            double k = (value - min) / step;
            const double eps = 1e-6;
            double grid = steps > 0 ? Math.Floor(k + eps) + steps : Math.Ceiling(k - eps) + steps;
            return Clamp(min + grid * step, min, max, integer);
        }

        /// <summary>How many steps one repeat moves after <paramref name="streak"/> repeats of a held key (about 10
        /// a second): 1, then 5 after 0.8 s, 25 after 2.4 s, 125 after 4.8 s; never more than a twentieth of the range,
        /// so short ranges stay precise.</summary>
        public static int Acceleration(int streak, double min, double max, double step)
        {
            int speed = streak >= 48 ? 125 : streak >= 24 ? 25 : streak >= 8 ? 5 : 1;
            if (step <= 0) return 1;
            double cap = Math.Floor(Math.Abs(max - min) / step / 20.0);
            return (int)Math.Max(1, Math.Min(speed, cap));
        }
    }
}

using System;

namespace PalaceMod.Tests
{
    public static class NumberMathTests
    {
        private static void Equal(double expected, double actual, string what)
        {
            if (Math.Abs(expected - actual) > 1e-9) throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        private static void Parses(string text, double expected, string unit = "")
        {
            if (!NumberMath.TryParse(text, out double value, out string u)) throw new Exception($"\"{text}\" didn't parse");
            Equal(expected, value, $"\"{text}\"");
            if (u != unit) throw new Exception($"\"{text}\": unit \"{u}\", expected \"{unit}\"");
        }

        private static void Refuses(string text)
        {
            if (NumberMath.TryParse(text, out double value, out _)) throw new Exception($"\"{text}\" parsed as {value}");
        }

        public static void ParsesPlainNumbers()
        {
            Parses("50", 50);
            Parses("  20 ", 20);
            Parses("+5", 5);
            Parses("-3", -3);
            Parses("1.5", 1.5);
            Parses(".5", 0.5);
            Parses("7.", 7);
        }

        public static void ParsesCommas()
        {
            Parses("1,5", 1.5);
            Parses("1,50", 1.5);
            Parses("1,000", 1000);
            Parses("12,345", 12345);
            Parses("1,000,000", 1000000);
            Parses("1,000.25", 1000.25);
        }

        public static void ParsesSuffixesAndUnits()
        {
            Parses("10k", 10000);
            Parses("2.5K", 2500);
            Parses("60 fps", 60, "fps");
            Parses("50%", 50, "%");
            Parses("x1.5", 1.5, "x");
            Parses("1.5x", 1.5, "x");
            Parses("3 of 15", 3, "of 15");
        }

        public static void RefusesJunk()
        {
            Refuses("");
            Refuses("   ");
            Refuses("abc");
            Refuses("-");
            Refuses(".");
            Refuses("x");
            Refuses(null);
        }

        public static void UnitsMustMatchTheField()
        {
            if (!NumberMath.UnitMatches("", "60 fps")) throw new Exception("empty unit");
            if (!NumberMath.UnitMatches("fps", "60 fps")) throw new Exception("fps");
            if (!NumberMath.UnitMatches("FPS", "60 fps")) throw new Exception("case");
            if (!NumberMath.UnitMatches("x", "<color=#aaa>x1.5</color>")) throw new Exception("rich text");
            if (NumberMath.UnitMatches("abc", "5")) throw new Exception("junk accepted");
            if (NumberMath.UnitMatches("color", "<color=#aaa>5</color>")) throw new Exception("tag text counted");
            if (NumberMath.UnitMatches("-3", "5")) throw new Exception("trailing arithmetic accepted");
        }

        public static void Clamps()
        {
            Equal(0, NumberMath.Clamp(-5, 0, 10, true), "below");
            Equal(10, NumberMath.Clamp(50, 0, 10, true), "above");
            Equal(3, NumberMath.Clamp(2.5, 0, 10, true), "rounds half up");
            Equal(2.5, NumberMath.Clamp(2.5, 0, 10, false), "float kept");
            Equal(10, NumberMath.Clamp(50, 10, 5, true), "swapped bounds");
        }

        public static void StepsOnTheGrid()
        {
            Equal(55, NumberMath.Step(50, 5, 1, 0, 240, true), "up");
            Equal(45, NumberMath.Step(50, 5, -1, 0, 240, true), "down");
            Equal(145, NumberMath.Step(144, 5, 1, 0, 240, true), "off grid up");
            Equal(140, NumberMath.Step(144, 5, -1, 0, 240, true), "off grid down");
            Equal(240, NumberMath.Step(200, 5, 100, 0, 240, true), "clamped at max");
            Equal(0, NumberMath.Step(3, 5, -10, 0, 240, true), "clamped at min");
            Equal(1.6, NumberMath.Step(1.5, 0.1, 1, 0.5, 5, false), "float step");
            Equal(1.4, NumberMath.Step(1.5, 0.1, -1, 0.5, 5, false), "float step down");
            Equal(12, NumberMath.Step(2, 1, 10, 1, 100, true), "x10");
            Equal(3, NumberMath.Step(1, 2, 1, 1, 100, true), "grid from min");
        }

        public static void AcceleratesWithinTheRange()
        {
            Equal(1, NumberMath.Acceleration(0, 1, 1000000, 1), "fresh press");
            Equal(1, NumberMath.Acceleration(7, 1, 1000000, 1), "short hold");
            Equal(5, NumberMath.Acceleration(8, 1, 1000000, 1), "held");
            Equal(25, NumberMath.Acceleration(24, 1, 1000000, 1), "held longer");
            Equal(125, NumberMath.Acceleration(60, 1, 1000000, 1), "held long");
            Equal(1, NumberMath.Acceleration(60, 0, 20, 1), "short range stays precise");
            Equal(5, NumberMath.Acceleration(60, 0, 100, 1), "capped at a twentieth");
            Equal(1, NumberMath.Acceleration(60, 0, 1, 0), "no step");
        }
    }
}

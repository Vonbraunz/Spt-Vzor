using System.Globalization;
using UnityEngine;

namespace Vzor
{
    internal static class ColorUtil
    {
        // parses #RGB, #RRGGBB or #RRGGBBAA. deliberately not using ColorUtility, because
        // that lives in the IMGUI module and i am not adding a reference for four lines of hex.
        public static Color Parse(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;

            var s = hex.Trim();
            if (s.Length > 0 && s[0] == '#') s = s.Substring(1);

            if (s.Length == 3)
                s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}FF";
            else if (s.Length == 6)
                s += "FF";

            if (s.Length != 8) return fallback;

            if (!TryByte(s, 0, out var r)) return fallback;
            if (!TryByte(s, 2, out var g)) return fallback;
            if (!TryByte(s, 4, out var b)) return fallback;
            if (!TryByte(s, 6, out var a)) return fallback;

            return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        }

        private static bool TryByte(string s, int offset, out byte value)
        {
            return byte.TryParse(s.Substring(offset, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out value);
        }
    }
}

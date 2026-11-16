// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// PDF date strings — ISO 32000-1 §7.9.4, <c>D:YYYYMMDDHHmmSSOHH'mm'</c>.
    ///
    /// <para><b>Why this exists.</b> One formatter, which converts to UTC before appending the
    /// <c>Z</c> suffix. Formatting whatever <see cref="DateTimeKind"/> it is handed and appending
    /// <c>Z</c> anyway would, on any host not already on UTC, write a signature timestamp wrong by
    /// the machine's offset while *claiming* to be UTC — a lie a verifier has no way to detect.</para>
    /// </summary>
    public static class PdfDate
    {
        /// <summary>
        /// Formats <paramref name="value"/> as a UTC PDF date: <c>D:YYYYMMDDHHmmssZ</c>.
        ///
        /// <para>The conversion to UTC is the point. A <see cref="DateTime"/> with
        /// <see cref="DateTimeKind.Local"/> or <see cref="DateTimeKind.Unspecified"/> is converted
        /// rather than relabelled, so the <c>Z</c> is true.</para>
        /// </summary>
        public static string FormatUtc(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return "D:" + utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";
        }

        /// <summary>
        /// Parses <c>D:YYYYMMDDHHmmSSOHH'mm'</c>, returning <c>null</c> for anything it cannot
        /// read. Every component after the year is optional (§7.9.4) and defaults as the spec
        /// says: month and day to 1, the rest to 0, the offset to UTC.
        /// </summary>
        /// <remarks>
        /// <para><b>One parser too</b>, taking the whole string — time of day and time zone included —
        /// for the metadata reader, the portfolio reader, the signer, the viewer and the form
        /// scripts alike. Taking <b>eight characters</b> and building a midnight
        /// <see cref="DateTimeKind.Unspecified"/> date would report every embedded file's creation
        /// time as 00:00, with no indication that a time had been read and discarded.</para>
        /// <para><b>A missing time zone is not UTC, and this deliberately treats it as such
        /// anyway.</b> §7.9.4 makes the offset optional and says an absent one means the local time
        /// of the *producing* machine, which the file does not record — so there is nothing to
        /// recover. Zero is the only defensible constant, and it is what the metadata reader has
        /// always used. A reader with another use for such a date asks
        /// <see cref="TryParse(string, out bool)"/> whether the zone was stated.</para>
        /// </remarks>
        public static DateTimeOffset? TryParse(string dateString) => TryParse(dateString, out _);

        /// <summary>
        /// <see cref="TryParse(string)"/>, saying in <paramref name="zoneStated"/> whether the date
        /// states its relationship to UT (<c>+</c>, <c>-</c> or <c>Z</c>) — false when the offset
        /// is the parser's zero for an unknown zone. A form script reads such a date as the local
        /// time it names, as a viewer shows it.
        /// </summary>
        /// <remarks>
        /// <para><b>Read by a scan</b>: each component after the year is two digits where it is
        /// present, and the zone is read where the components stop — so a producer that writes the
        /// zone after the day, the hour or the minutes, short of what §7.9.4 allows
        /// (<c>D:202608191430+03'00'</c>), keeps it, where reading each component at a fixed index
        /// would take <c>+0</c> as the seconds and lose the offset. A component that is neither two
        /// digits nor such a zone is malformed, and the date does not read; text after the seconds
        /// that is not a zone is passed over, as is a last character standing alone.</para>
        /// <para>The zone's minutes follow its hours, after an apostrophe or not: producers write
        /// <c>HH'mm'</c>, <c>HH'mm</c> and <c>HHmm</c>.</para>
        /// </remarks>
        public static DateTimeOffset? TryParse(string dateString, out bool zoneStated)
        {
            zoneStated = false;
            if (string.IsNullOrEmpty(dateString)) return null;

            string s = dateString;
            int i = s.StartsWith("D:", StringComparison.Ordinal) ? 2 : 0;

            if (!TwoDigits(s, i, out int century) || !TwoDigits(s, i + 2, out int yearInCentury)) return null;
            int year = century * 100 + yearInCentury;
            i += 4;

            // Month, day, hour, minute, second: defaults 1, 1, 0, 0, 0. A zone may cut the time
            // short once the day is read (p is the hour's slot or later); before that, a sign is the
            // date's own punctuation (D:2026-03-04), which is malformed, not a zone.
            var parts = new[] { 1, 1, 0, 0, 0 };
            for (int p = 0; p < parts.Length && i + 1 < s.Length; p++)
            {
                if (TwoDigits(s, i, out int value)) { parts[p] = value; i += 2; continue; }
                if (p >= 2 && IsZone(s[i])) break;
                return null;
            }

            TimeSpan offset = TimeSpan.Zero;
            if (i < s.Length && IsZone(s[i]))
            {
                zoneStated = true;
                char tz = s[i++];
                if (tz != 'Z')
                {
                    // The hours are two digits where two characters follow the sign; anything else
                    // there is malformed. The minutes, after an apostrophe or not, are read where
                    // they are two digits.
                    int tzH = 0, tzM = 0;
                    if (i + 1 < s.Length)
                    {
                        if (!TwoDigits(s, i, out tzH)) return null;
                        i += 2;
                    }
                    if (i < s.Length && s[i] == '\'') i++;
                    if (TwoDigits(s, i, out int m)) tzM = m;
                    offset = new TimeSpan(tzH, tzM, 0);
                    if (tz == '-') offset = offset.Negate();
                }
            }

            try
            {
                return new DateTimeOffset(year, parts[0], parts[1], parts[2], parts[3], parts[4], offset);
            }
            catch (ArgumentException)
            {
                // Every failure here is a malformed date in someone else's file — a month of 13, a
                // day of 32, an offset past ±14 hours. Null is the answer; a throw would take the
                // whole document read with it. (ArgumentOutOfRangeException derives from it.)
                return null;
            }
        }

        private static bool IsZone(char c) => c == '+' || c == '-' || c == 'Z';

        /// <summary>The two ASCII digits at <paramref name="at"/>, when both are there.</summary>
        private static bool TwoDigits(string s, int at, out int value)
        {
            value = 0;
            if (at < 0 || at + 1 >= s.Length) return false;
            char a = s[at], b = s[at + 1];
            if (a < '0' || a > '9' || b < '0' || b > '9') return false;
            value = (a - '0') * 10 + (b - '0');
            return true;
        }
    }
}

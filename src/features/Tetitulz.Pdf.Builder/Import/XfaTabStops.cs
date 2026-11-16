// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Import
{
    /// <summary>
    /// XFA tab stops: a <c>para</c>'s <c>tabStops</c> and rich text's <c>tab-stops</c> /
    /// <c>xfa-tab-stops</c> (one grammar, XFA 3.3 chapter 2, "Tab Stops" and "Tab Leader Pattern"):
    /// <c>[alignment] [leader] location</c>, repeated. An alignment is <c>left</c>, <c>center</c>,
    /// <c>right</c>, <c>decimal</c>, or <c>before</c> / <c>after</c> — right and left in left-to-right text,
    /// which is what this reads; a leader is <c>dots</c> (a dot leader), <c>space</c> (none),
    /// <c>rule(style …)</c> (a dotted rule a dot leader, a dashed one a hyphen leader, any other an
    /// underscore) or <c>use-content(content)</c> — "textual content … copied repeatedly
    /// into the empty region before the tab stop, as many times as it will fit … in CSS parameter style for
    /// a function, typically as a quoted string", spaces alone being <c>space()</c> (<see cref="TabStop.LeaderText"/>);
    /// a <c>leaderPatternWidth</c>, which pads each repetition, is not applied, and named. And the default
    /// stops a <c>tabDefault</c> or <c>tab-interval</c> recurs at, beyond the last stated one.
    /// </summary>
    /// <remarks>
    /// The text is the file's: past <see cref="MaxTextLength"/> it is not read, a parenthesis is followed
    /// <see cref="MaxNesting"/> deep, and no more than <see cref="MaxStops"/> stops are made, default ones
    /// included, none past <see cref="MaxPositionPt"/>.
    /// </remarks>
    internal static class XfaTabStops
    {
        public const int MaxTextLength = 4096;
        public const int MaxNesting = 8;
        public const int MaxStops = 64;
        public const double MaxPositionPt = 2000;

        /// <summary>The stops <paramref name="text"/> states, in order; what it holds that is not applied named in <paramref name="unapplied"/>.</summary>
        public static List<TabStop> Parse(string? text, ISet<string> unapplied)
        {
            var stops = new List<TabStop>();
            if (string.IsNullOrWhiteSpace(text)) return stops;
            if (text!.Length > MaxTextLength) { unapplied.Add("tab stops longer than " + MaxTextLength + " characters"); return stops; }
            TabAlignment align = TabAlignment.Left;
            TabLeader leader = TabLeader.None;
            string? leaderText = null;
            int i = 0;
            while (i < text.Length && stops.Count < MaxStops)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(') i++;
                string word = text.Substring(start, i - start);
                string? args = null;
                if (i < text.Length && text[i] == '(')
                {
                    int open = i, close = ClosingParenthesis(text, open, out bool tooDeep);
                    if (tooDeep) { unapplied.Add("tab stops nested past " + MaxNesting + " parentheses"); return stops; }
                    if (close < 0) { unapplied.Add("tab stops: an unclosed parenthesis"); return stops; }
                    args = text.Substring(open + 1, close - open - 1);
                    i = close + 1;
                }
                switch (word.ToLowerInvariant())
                {
                    case "left": case "after": align = TabAlignment.Left; break;
                    case "right": case "before": align = TabAlignment.Right; break;
                    case "center": align = TabAlignment.Center; break;
                    case "decimal": align = TabAlignment.Decimal; break;
                    case "leader": (leader, leaderText) = Leader(args ?? "", unapplied); break;
                    default:
                        if (args == null && XfaMeasure.TryParse(word, out double at) && at >= 0 && at <= MaxPositionPt)
                        {
                            stops.Add(new TabStop { PosPt = at, Align = align, Leader = leader, LeaderText = leaderText });
                            align = TabAlignment.Left;
                            leader = TabLeader.None;
                            leaderText = null;
                        }
                        else unapplied.Add("tab stop: " + (word.Length <= 32 ? word : word.Substring(0, 32) + "…"));
                        break;
                }
            }
            return stops;
        }

        /// <summary>The longest <c>use-content</c> text read; a longer one is not a leader, and is named.</summary>
        public const int MaxLeaderTextLength = 64;

        /// <summary>
        /// A <c>leader(leaderPattern [leaderAlignment [leaderPatternWidth]])</c>'s arguments: the leader, and the
        /// text a <c>use-content</c> one repeats (<c>null</c> for any other).
        /// </summary>
        private static (TabLeader Leader, string? Text) Leader(string args, ISet<string> unapplied)
        {
            string trimmed = args.Trim();
            // The pattern, a function with its parenthesised argument; what follows it is the alignment and the
            // pattern's width.
            int end = trimmed.IndexOf('(');
            int close = end < 0 ? -1 : ClosingParenthesis(trimmed, end, out _);
            string head = (end < 0 ? trimmed.Split(' ')[0] : trimmed.Substring(0, end)).Trim().ToLowerInvariant();
            string after = end < 0 ? trimmed.Substring(head.Length) : close < 0 ? "" : trimmed.Substring(close + 1);
            foreach (string token in after.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (XfaMeasure.TryParse(token, out _)) unapplied.Add("tab leader pattern width");
                else if (!token.Equals("none", StringComparison.OrdinalIgnoreCase)) unapplied.Add("tab leader alignment");   // page: a grid the repetitions keep to
            }

            switch (head)
            {
                case "dots": return (TabLeader.Dot, null);
                case "space": return (TabLeader.None, null);
                case "rule":
                    string rule = trimmed.ToLowerInvariant();
                    return (rule.Contains("dotted") ? TabLeader.Dot : rule.Contains("dashed") ? TabLeader.Hyphen : TabLeader.Underscore, null);
                case "use-content":
                    if (close > end && UnquotedContent(trimmed.Substring(end + 1, close - end - 1)) is string content)
                        return (TabLeader.None, content.Trim().Length == 0 ? null : content);   // spaces alone are space()
                    break;
            }
            unapplied.Add("tab leader: " + (trimmed.Length <= 32 ? trimmed : trimmed.Substring(0, 32) + "…"));
            return (TabLeader.None, null);
        }

        /// <summary>
        /// The index of the parenthesis closing the one at <paramref name="open"/> — those nested inside counted, a
        /// quoted string's parentheses (and its backslash escapes) taken as text — or -1 for none; past
        /// <see cref="MaxNesting"/> deep, -1 with <paramref name="tooDeep"/>.
        /// </summary>
        private static int ClosingParenthesis(string s, int open, out bool tooDeep)
        {
            tooDeep = false;
            char quote = '\0';
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; }
                else if (c is '"' or '\'') quote = c;
                else if (c == '(' && ++depth > MaxNesting) { tooDeep = true; return -1; }
                else if (c == ')' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// A CSS function argument as text: a string in double or single quotes, its backslash escapes undone, or a
        /// bare one as it stands; <c>null</c> for one past <see cref="MaxLeaderTextLength"/>, or empty.
        /// </summary>
        private static string? UnquotedContent(string argument)
        {
            string a = argument.Trim();
            if (a.Length >= 2 && (a[0] is '"' or '\'') && a[a.Length - 1] == a[0])
            {
                var sb = new System.Text.StringBuilder(a.Length);
                for (int i = 1; i < a.Length - 1; i++)
                {
                    if (a[i] == '\\' && i + 1 < a.Length - 1) i++;
                    sb.Append(a[i]);
                }
                a = sb.ToString();
            }
            return a.Length == 0 || a.Length > MaxLeaderTextLength ? null : a;
        }

        /// <summary>
        /// <paramref name="stops"/> and, beyond the last of them, the default stops every
        /// <paramref name="interval"/> recurs at (left-aligned, as left-to-right text's after-aligned ones
        /// are) — under <see cref="MaxStops"/> and <see cref="MaxPositionPt"/>.
        /// </summary>
        public static List<TabStop> WithDefaults(List<TabStop> stops, double? interval)
        {
            if (!(interval is double step) || step <= 0) return stops;
            var all = new List<TabStop>(stops);
            double last = 0;
            foreach (TabStop s in stops) last = Math.Max(last, s.PosPt);
            for (double at = (Math.Floor(last / step) + 1) * step; at <= MaxPositionPt && all.Count < MaxStops; at += step)
                all.Add(new TabStop { PosPt = at });
            return all;
        }
    }
}

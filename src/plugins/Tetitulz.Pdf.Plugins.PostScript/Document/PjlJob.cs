// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.PostScript.Document
{
    /// <summary>
    /// A print job wrapped in HP's Printer Job Language, as a print driver writes it: the Universal
    /// Exit Language command <c>ESC%-12345X</c>, PJL command lines, <c>@PJL ENTER LANGUAGE =
    /// POSTSCRIPT</c>, the PostScript, and a closing UEL (often more PJL — <c>@PJL EOJ</c> — and another
    /// UEL after it). The PostScript is taken out of the wrapper, as a printer does.
    ///
    /// <para>The rules are the PJL Technical Reference Manual's (HP, eleventh edition, 1999): a PJL
    /// job begins with the UEL, followed at once by <c>@PJL</c> (§2, "PJL Job Requirements"); every
    /// command line is <c>@PJL</c> — upper case, the rest of the line case-insensitive — ended by a line
    /// feed, a carriage return before it ignored (§2, "PJL Syntax Rules"); the language ENTER selects
    /// "begins parsing immediately after the &lt;LF&gt; that terminates the ENTER command", and the UEL
    /// "instructs the active printer language to finish processing the current job" (§4). With no
    /// ENTER, a printer samples the data for its language; data that opens with <c>%!</c> is taken as
    /// PostScript, anything else refused.</para>
    ///
    /// <para>Refused by name: an ENTER naming another language (PCL, ESC/P), a PJL line with no line
    /// feed, and data no ENTER introduces that is not PostScript. A second PostScript section after
    /// the first one's UEL is not converted, and is said.</para>
    /// </summary>
    internal static class PjlJob
    {
        private static readonly byte[] Uel = { 0x1B, (byte)'%', (byte)'-', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'X' };
        private static readonly byte[] Prefix = { (byte)'@', (byte)'P', (byte)'J', (byte)'L' };
        private static readonly byte[] PostScriptHeader = { (byte)'%', (byte)'!' };

        /// <summary>Whether <paramref name="data"/> is a PJL job: it begins with the UEL.</summary>
        public static bool IsPjl(byte[] data) => StartsWith(data, 0, Uel);

        /// <summary>
        /// The PostScript a PJL job carries — the bytes after <c>ENTER LANGUAGE = POSTSCRIPT</c>'s line
        /// up to the next UEL — or null with <paramref name="refusal"/> saying why.
        /// <paramref name="ignored"/> names what was left out, or is null.
        /// </summary>
        public static byte[] PostScriptOf(byte[] data, out string refusal, out string ignored)
        {
            refusal = null;
            ignored = null;
            int at = Uel.Length;
            if (!ReadCommands(data, ref at, out bool entered, out string language, out refusal)) return null;
            if (entered && !string.Equals(language, "POSTSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                refusal = $"the PJL job is in {language}, not PostScript";
                return null;
            }
            if (!entered && !StartsWith(data, at, PostScriptHeader))
            {
                refusal = "the PJL job names no language, and its data is not PostScript";
                return null;
            }

            int end = IndexOfUel(data, at);
            if (end < 0) end = data.Length;
            var program = new byte[end - at];
            Array.Copy(data, at, program, 0, program.Length);

            // What follows: PJL (an end-of-job, a status request) is the printer's; another language
            // section is a second job.
            if (end < data.Length)
            {
                int after = end + Uel.Length;
                if (ReadCommands(data, ref after, out bool again, out string next, out _) && again)
                    ignored = $"a second section of the PJL job, in {next}, was not converted";
            }
            return program;
        }

        /// <summary>
        /// Reads PJL lines from <paramref name="at"/> — a UEL between them passed over — up to the
        /// ENTER command, whose language is <paramref name="language"/>, or to the first byte that
        /// begins no command; <paramref name="at"/> is left after what was read.
        /// </summary>
        private static bool ReadCommands(byte[] data, ref int at, out bool entered, out string language, out string refusal)
        {
            entered = false;
            language = null;
            refusal = null;
            while (at < data.Length)
            {
                if (StartsWith(data, at, Uel)) { at += Uel.Length; continue; }
                if (!StartsWith(data, at, Prefix)) return true;
                int lineFeed = Array.IndexOf(data, (byte)'\n', at);
                if (lineFeed < 0)
                {
                    refusal = "a PJL command line has no line feed to end it";
                    return false;
                }
                int start = at + Prefix.Length;
                int length = lineFeed - start;
                if (length > 0 && data[lineFeed - 1] == (byte)'\r') length--;
                at = lineFeed + 1;
                if (TryReadEnter(Latin1Text.Decode(data, start, length), out language))
                {
                    entered = true;
                    return true;
                }
            }
            return true;
        }

        /// <summary><c>ENTER LANGUAGE = personality</c>, the white space around <c>=</c> optional (spaces and tabs).</summary>
        internal static bool TryReadEnter(string command, out string language)
        {
            language = null;
            int at = 0;
            if (!SkipWhiteSpace(command, ref at)) return false;
            if (!Word(command, ref at, "ENTER") || !SkipWhiteSpace(command, ref at)) return false;
            if (!Word(command, ref at, "LANGUAGE")) return false;
            SkipWhiteSpace(command, ref at);
            if (at >= command.Length || command[at] != '=') return false;
            at++;
            SkipWhiteSpace(command, ref at);
            int start = at;
            while (at < command.Length && command[at] != ' ' && command[at] != '\t') at++;
            if (at == start) return false;
            language = command.Substring(start, at - start);
            return true;
        }

        private static bool Word(string text, ref int at, string word)
        {
            if (at + word.Length > text.Length || string.Compare(text, at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            at += word.Length;
            return true;
        }

        /// <summary>Moves past spaces and tabs; whether there was any.</summary>
        private static bool SkipWhiteSpace(string text, ref int at)
        {
            int start = at;
            while (at < text.Length && (text[at] == ' ' || text[at] == '\t')) at++;
            return at > start;
        }

        private static int IndexOfUel(byte[] data, int from)
        {
            for (int at = Array.IndexOf(data, Uel[0], from); at >= 0; at = Array.IndexOf(data, Uel[0], at + 1))
                if (StartsWith(data, at, Uel)) return at;
            return -1;
        }

        private static bool StartsWith(byte[] data, int at, byte[] prefix)
        {
            if (at < 0 || at + prefix.Length > data.Length) return false;
            for (int k = 0; k < prefix.Length; k++)
                if (data[at + k] != prefix[k]) return false;
            return true;
        }
    }
}

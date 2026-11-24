// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Locates font files on the host filesystem, independent of any platform font API.
    ///
    /// <para>This exists because the rendering path resolved system fonts exclusively through
    /// SkiaSharp's <c>SKTypeface.FromFamilyName</c>, which delegates to the platform font manager
    /// (DirectWrite / fontconfig / CoreText). That is fine on a desktop, but the family tables it
    /// was driven from name Windows fonts — "Arial", "Times New Roman", "Segoe UI Symbol" — none of
    /// which exist on a bare Linux container, so every non-embedded font collapsed to
    /// <c>SKTypeface.Default</c>. Matching real files by name gives the same answer on every
    /// platform.</para>
    ///
    /// <para>No fonts are shipped with this library — redistributing font programs carries
    /// licensing obligations the library cannot make on a consumer's behalf. This finds what is
    /// already installed; deployment is responsible for installing something (on Debian/Ubuntu,
    /// <c>fonts-liberation</c> covers the standard 14 with metric-compatible substitutes).</para>
    ///
    /// <para>Set <see cref="FontDirEnvVar"/> to one or more directories (separated by
    /// <see cref="Path.PathSeparator"/>) to define the search path. When it is set it <b>replaces</b>
    /// the OS directories rather than adding to them, because the reason to set it is to make
    /// rendering reproducible — a server that ships its own fonts wants the same glyphs on every
    /// machine, which it cannot have if whatever the host happens to have installed still
    /// participates. A caller that wants both lists the OS directories itself.</para>
    ///
    /// <para>Thread-safe. All scans are cached for the process lifetime; call
    /// <see cref="ResetCache"/> if fonts are installed while the process is running (tests do).</para>
    /// </summary>
    internal static class FontFileLocator
    {
        /// <summary>Environment variable naming extra font directories, highest priority first.</summary>
        internal const string FontDirEnvVar = "TETITULZ_PDF_FONTDIR";

        private static readonly object Gate = new object();
        private static List<string> _directories;
        private static List<string> _filesWithOpenType;
        private static List<string> _filesTrueTypeOnly;

        // (family|bold|italic) → resolved path, or null when nothing matched. Without it every
        // font resource on every page re-scans the whole file list.
        //
        // The key comes from a PDF /BaseFont name, so the number of DISTINCT keys is chosen by the
        // file, not by the host: "the number of distinct families a document asks for" is not a
        // bound at all. A document declaring 100 000 font resources with distinct names — a few
        // megabytes once object-stream compressed — would insert 100 000 entries surviving for the
        // process lifetime, a monotonic leak keyed on attacker-supplied strings in a long-lived server.
        // Past the cap the cache stops ACCEPTING new entries rather than evicting: the hit rate
        // for a real document is front-loaded (the same handful of families recur on every page),
        // so keeping the early entries is worth more than any eviction policy, and a miss past the
        // cap is still correct — just uncached.
        private static readonly Dictionary<string, string> ResolvedPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Entries currently held in the family → font-file cache. Exposed so the bound on it is
        /// testable: the cap is the whole point of the dictionary's key being file-controlled.
        /// </summary>
        internal static int CachedLookupCount
        {
            get { lock (Gate) return ResolvedPaths.Count; }
        }

        /// <summary>
        /// OS font directories that exist, in search order. Windows, Linux and macOS locations are
        /// all probed unconditionally rather than behind a platform test: a directory that does not
        /// exist is simply dropped, and probing costs one <c>Directory.Exists</c> per entry.
        /// </summary>
        internal static IReadOnlyList<string> Directories
        {
            get
            {
                lock (Gate)
                {
                    if (_directories != null) return _directories;

                    var dirs = new List<string>();
                    void Add(string d)
                    {
                        if (string.IsNullOrEmpty(d)) return;
                        foreach (string existing in dirs)
                            if (string.Equals(existing, d, StringComparison.OrdinalIgnoreCase)) return;
                        dirs.Add(d);
                    }

                    // Directories a host or a package registered (FontSearchPath) come first, under
                    // either branch below: both are the caller's choice, and a bundled font must be
                    // the one found when the host also has one of the family.
                    foreach (string registered in FontSearchPath.Directories) Add(registered);

                    // A caller-supplied search path replaces the OS one outright — see the type
                    // remarks. Returning early is the whole mechanism.
                    string custom = null;
                    try { custom = Environment.GetEnvironmentVariable(FontDirEnvVar); }
                    catch (Exception) { /* restricted host — ignore */ }
                    if (!string.IsNullOrEmpty(custom))
                    {
                        foreach (string part in custom.Split(Path.PathSeparator))
                            Add(part.Trim());
                        _directories = KeepExisting(dirs);
                        return _directories;
                    }

                    Add(GetFolder(Environment.SpecialFolder.Fonts));                // Windows: C:\Windows\Fonts
                    string localAppData = GetFolder(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(localAppData))
                        Add(Path.Combine(localAppData, "Microsoft", "Windows", "Fonts")); // per-user installs

                    Add("/usr/share/fonts");
                    Add("/usr/local/share/fonts");
                    string home = GetFolder(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(home))
                    {
                        Add(Path.Combine(home, ".fonts"));
                        Add(Path.Combine(home, ".local", "share", "fonts"));
                    }

                    Add("/Library/Fonts");
                    Add("/System/Library/Fonts");
                    // macOS moved most of the classic families here in 10.15; without it a Mac host
                    // finds Helvetica but not Arial, Times New Roman or Courier New.
                    Add("/System/Library/Fonts/Supplemental");

                    _directories = KeepExisting(dirs);
                    return _directories;
                }
            }
        }

        private static List<string> KeepExisting(List<string> candidates)
        {
            var present = new List<string>();
            foreach (string d in candidates)
            {
                try { if (Directory.Exists(d)) present.Add(d); }
                catch (Exception) { /* unreadable — skip */ }
            }
            return present;
        }

        private static string GetFolder(Environment.SpecialFolder folder)
        {
            // Returns "" on platforms where the folder has no meaning (e.g. SpecialFolder.Fonts on
            // Linux), which Add() drops. Never throws out of here.
            try { return Environment.GetFolderPath(folder); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Every font file under <see cref="Directories"/>, recursively.
        /// </summary>
        /// <param name="includeOpenType">
        /// When true, <c>.otf</c> files are included alongside <c>.ttf</c>/<c>.ttc</c>. Callers that
        /// can only consume <c>glyf</c> outlines pass false. The two lists are cached separately.
        /// </param>
        internal static IReadOnlyList<string> EnumerateFontFiles(bool includeOpenType)
        {
            lock (Gate)
            {
                if (includeOpenType && _filesWithOpenType != null) return _filesWithOpenType;
                if (!includeOpenType && _filesTrueTypeOnly != null) return _filesTrueTypeOnly;

                string[] patterns = includeOpenType
                    ? new[] { "*.ttf", "*.otf", "*.ttc" }
                    : new[] { "*.ttf", "*.ttc" };

                var files = new List<string>();
                foreach (string dir in Directories)
                {
                    foreach (string pattern in patterns)
                    {
                        try
                        {
                            files.AddRange(Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories));
                        }
                        catch (Exception)
                        {
                            // Unreadable directory, or a symlink loop under /usr/share/fonts.
                            // Whatever was already collected stays; this root is skipped.
                        }
                    }
                }

                if (includeOpenType) _filesWithOpenType = files;
                else _filesTrueTypeOnly = files;
                return files;
            }
        }

        /// <summary>
        /// Finds a font file for <paramref name="family"/> in the requested style, or <c>null</c>.
        ///
        /// <para>A known standard-14 / core family is expanded to an ordered candidate list: the
        /// family asked for by its own name first, then the host's face (Arial / Times New Roman /
        /// Courier New), then the metric-compatible substitute (Liberation), so PDF <c>/Widths</c>
        /// stay usable when the original is absent, then broad-coverage fallbacks. Any other
        /// family is matched against filenames directly, which is what makes the CJK and symbol
        /// candidate lists in the renderer work off Windows.</para>
        /// </summary>
        internal static string FindFamilyFile(string family, bool bold, bool italic)
        {
            if (string.IsNullOrEmpty(family)) return null;

            string key = family + "|" + (bold ? "b" : "") + (italic ? "i" : "");
            lock (Gate)
            {
                // A cached null is a real answer ("this family is not on the machine") — keep it,
                // or an absent family re-scans every file list on every lookup.
                if (ResolvedPaths.TryGetValue(key, out string cached)) return cached;

                string found = null;
                foreach (string stem in CandidateStems(family))
                {
                    found = MatchStem(stem, bold, italic);
                    if (found != null) break;
                }

                if (ResolvedPaths.Count < PdfLimits.MaxCachedFontPathLookups)
                    ResolvedPaths[key] = found;
                return found;
            }
        }

        /// <summary>
        /// Every font file matching <paramref name="family"/>, best first, capped at
        /// <paramref name="max"/>.
        ///
        /// <para><see cref="FindFamilyFile"/> answers "which file is this family", which is the
        /// right question when the caller only needs <i>a</i> face. It is the wrong question when
        /// the caller needs a face that <b>covers specific code points</b>: filename matching cannot
        /// see a cmap, so the best-named file may have no glyphs for the text. Returning the ranked
        /// list lets the caller keep walking until coverage is satisfied — which is why the Hebrew
        /// stem list can name Liberation last without that being a bug.</para>
        ///
        /// <para>Not cached: the caller parses each candidate anyway, and the underlying file
        /// enumeration — the expensive part — is cached already.</para>
        /// </summary>
        internal static IReadOnlyList<string> FindFamilyFiles(string family, bool bold, bool italic, int max)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(family) || max <= 0) return found;

            foreach (string stem in CandidateStems(family))
            {
                foreach (string path in MatchStems(stem, bold, italic))
                {
                    // A stem list can name the same file twice (Hebrew names "arial" and Sans
                    // would too), and a duplicate costs a redundant parse in the caller.
                    if (found.Contains(path)) continue;
                    found.Add(path);
                    if (found.Count >= max) return found;
                }
            }
            return found;
        }

        /// <summary>Drops the cached directory and file scans. For tests, and for hosts that
        /// install fonts after startup.</summary>
        internal static void ResetCache()
        {
            lock (Gate)
            {
                _directories = null;
                _filesWithOpenType = null;
                _filesTrueTypeOnly = null;
                ResolvedPaths.Clear();
            }
        }

        // ── family → candidate filename stems ────────────────────────────────────────────────
        //
        // Keyed on what a PDF actually names, mapped to what the file is actually called. The
        // Windows stem comes first (it is the best match where present), then Liberation — chosen
        // over DejaVu because it is metric-compatible with Arial/Times New Roman/Courier New, so
        // substituting it does not invalidate the /Widths array — then broad-coverage fallbacks.
        //
        // NOTE: these arrays must stay declared ABOVE FamilyStems. Static field initializers run
        // in textual order, so a forward reference here would populate the dictionary with nulls.

        private static readonly string[] SansStems =
        {
            "arial", "LiberationSans", "Helvetica", "NimbusSans",
            "DejaVuSans", "NotoSans", "FreeSans",
        };

        private static readonly string[] SerifStems =
        {
            "times", "TimesNewRoman", "LiberationSerif", "NimbusRoman",
            "DejaVuSerif", "NotoSerif", "FreeSerif",
        };

        private static readonly string[] MonoStems =
        {
            "cour", "CourierNew", "LiberationMono", "NimbusMonoPS",
            "DejaVuSansMono", "NotoSansMono", "FreeMono",
        };

        private static readonly string[] DingbatStems =
        {
            "ZapfDingbats", "D050000L", "Symbola", "NotoSansSymbols2", "DejaVuSans",
        };

        private static readonly string[] SymbolStems =
        {
            "symbol", "OpenSymbol", "StandardSymbolsPS", "DejaVuSans",
        };

        // Hebrew. There was no entry here at all, so "David" fell through to the raw-filename
        // branch: it found david.ttf on Windows by luck and nothing whatever on Linux.
        //
        // Two things worth knowing before editing this list:
        //   * Liberation — which this file recommends elsewhere as the metric-compatible
        //     substitute, and which is what a minimal Linux container installs — has NO Hebrew
        //     coverage. A Hebrew document that resolves to it renders blank. That is why the
        //     Hebrew-specific stems come first and the generic ones only trail.
        //   * Culmus (the fonts-culmus package) is the free-licensed Hebrew family a deployment
        //     can actually ship, which is the difference between working on the author's Windows
        //     box and working in the container.
        //
        // The ordering is a HINT. The answer is the cmap coverage test in SystemFontResolver —
        // filename matching cannot tell whether a face has the glyphs.
        private static readonly string[] HebrewStems =
        {
            "david", "FrankRuehl", "frank", "Narkisim", "nrkis", "Miriam", "mriam",
            "Gisha", "Aharoni", "Levenim", "Rod",
            "NotoSansHebrew", "NotoSerifHebrew", "NotoRashiHebrew",
            "FrankRuehlCLM", "NachlieliCLM", "KeterYG", "MiriamCLM", "DavidCLM",
            "Alef", "Rubik", "Heebo", "Assistant",
            "arial", "tahoma", "times", "DejaVuSans", "FreeSans",
        };

        private static readonly Dictionary<string, string[]> FamilyStems =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Arial"] = SansStems,
                ["Helvetica"] = SansStems,
                ["Liberation Sans"] = SansStems,
                ["Times New Roman"] = SerifStems,
                ["Times"] = SerifStems,
                ["Times Roman"] = SerifStems,
                ["Liberation Serif"] = SerifStems,
                ["Courier New"] = MonoStems,
                ["Courier"] = MonoStems,
                ["Liberation Mono"] = MonoStems,
                ["Symbol"] = SymbolStems,
                ["ZapfDingbats"] = DingbatStems,
                ["ZaDb"] = DingbatStems,
                ["Hebrew"] = HebrewStems,
                ["David"] = HebrewStems,
                ["Frank Ruehl"] = HebrewStems,
                ["FrankRuehl"] = HebrewStems,
                ["Narkisim"] = HebrewStems,
                ["Miriam"] = HebrewStems,
                ["Gisha"] = HebrewStems,
                ["Aharoni"] = HebrewStems,
                ["Alef"] = HebrewStems,
                ["Rubik"] = HebrewStems,
                ["Heebo"] = HebrewStems,
            };

        private static IEnumerable<string> CandidateStems(string family)
        {
            string trimmed = family.Trim();
            if (FamilyStems.TryGetValue(trimmed, out string[] stems))
            {
                // The family asked for by its own name comes before its substitutes. "arial" leads
                // SansStems because it is the best answer to a request for Arial or Helvetica — but a
                // request for "Liberation Sans" on a host that has both must not be answered with
                // Arial, or the bundled fonts package (whose point is that two hosts draw the same
                // face) fails. The same list, one entry promoted.
                string own = Normalize(trimmed);
                foreach (string s in stems)
                    if (Normalize(s) == own) { yield return s; break; }
                foreach (string s in stems) yield return s;
                yield break;
            }
            // Unknown family: match the name itself against filenames. Normalisation strips the
            // spaces, so "Noto Sans CJK JP" finds NotoSansCJKjp-Regular.otf.
            yield return family;
        }

        // ── filename matching ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Best file whose name is <paramref name="stem"/> plus a recognised style suffix, or null.
        /// Ranking prefers an exact style match, then the regular weight of the same family; a file
        /// whose suffix is not a style token at all is rejected, so a request for "DejaVu Sans"
        /// never resolves to DejaVuSansMono.
        /// </summary>
        /// <remarks>
        /// <para><b>The head of the ranked list, not a second search for the same thing.</b>
        /// <see cref="FindFamilyFiles"/>' first element is <see cref="FindFamilyFile"/>'s answer
        /// because there is one scoring. <b>Two files tie whenever the same filename sits under two
        /// roots</b>, which is what a per-user font directory shadowing a system one is; a second copy
        /// keeping whichever the search path yielded first, beside a ranked list keeping the
        /// ordinally first, would make which face a page draws in depend on the order the font
        /// directories happen to be listed in.</para>
        ///
        /// <para><b>A scan stopping at the first score-0 file would not be worth having.</b>
        /// <see cref="EnumerateFontFiles"/> is a cached in-memory list and
        /// <see cref="FindFamilyFile"/> memoises its answer per family, so the saving would be part
        /// of one list walk on a cache miss — against a tie-break that is deterministic across
        /// hosts, which is the property <c>TETITULZ_PDF_FONTDIR</c> exists to give a server.</para>
        /// </remarks>
        private static string MatchStem(string stem, bool bold, bool italic)
        {
            List<string> ranked = MatchStems(stem, bold, italic);
            return ranked.Count > 0 ? ranked[0] : null;
        }

        /// <summary>
        /// Every file matching <paramref name="stem"/>, best first — the one scoring rule, and the
        /// one tie-break, that <see cref="MatchStem"/> also answers from.
        /// </summary>
        private static List<string> MatchStems(string stem, bool bold, bool italic)
        {
            var scored = new List<KeyValuePair<int, string>>();
            string wanted = Normalize(stem);
            if (wanted.Length == 0) return new List<string>();

            // A file under a directory the caller registered (FontSearchPath) beats the host's file
            // of the same name and style. The tie-break below is ordinal on the path so that the
            // answer does not depend on the order the directories were listed in — but a registered
            // directory is the caller's explicit choice, not a listing order, and under the ordinal
            // rule alone C:\Windows\Fonts\LiberationSans-Bold.ttf beat the bundled copy under D:\.
            var registered = FontSearchPath.Directories;

            foreach (string path in EnumerateFontFiles(true))
            {
                string name = Normalize(Path.GetFileNameWithoutExtension(path));
                if (name.Length < wanted.Length) continue;
                if (!name.StartsWith(wanted, StringComparison.Ordinal)) continue;

                string rest = name.Substring(wanted.Length);
                if (!TryParseStyle(rest, out bool fileBold, out bool fileItalic)) continue;

                int rank;
                if (fileBold == bold && fileItalic == italic) rank = 0;
                else if (!fileBold && !fileItalic) rank = 1;   // regular standing in for a styled request
                else continue;                                  // wrong style — never substitute

                // Shorter suffix wins within a rank, so "arial.ttf" beats "arial-Regular.ttf".
                //
                // ("arialunicodems" would not exercise the rule: "unicodems" is not a style token, so
                // TryParseStyle above rejects that file outright and it never reaches the score.)
                int host = IsUnderAny(path, registered) ? 0 : 1;
                scored.Add(new KeyValuePair<int, string>((rank * 1_000_000) + (host * 1000) + rest.Length, path));
            }

            scored.Sort((a, b) => a.Key != b.Key
                ? a.Key.CompareTo(b.Key)
                : string.CompareOrdinal(a.Value, b.Value));   // stable across hosts

            var result = new List<string>(scored.Count);
            foreach (var kv in scored) result.Add(kv.Value);
            return result;
        }

        /// <summary>Whether <paramref name="path"/> lies under one of <paramref name="directories"/>
        /// (full paths, as <see cref="FontSearchPath.Directories"/> gives them).</summary>
        private static bool IsUnderAny(string path, IReadOnlyList<string> directories)
        {
            for (int i = 0; i < directories.Count; i++)
            {
                string dir = directories[i].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (path.Length > dir.Length + 1
                    && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                    && (path[dir.Length] == Path.DirectorySeparatorChar || path[dir.Length] == Path.AltDirectorySeparatorChar))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Interprets the filename remainder after the family stem as a style.
        /// Returns false when the remainder is not a style token, which is what stops a prefix
        /// match from claiming a different family.
        /// </summary>
        private static bool TryParseStyle(string rest, out bool bold, out bool italic)
        {
            bold = false;
            italic = false;
            switch (rest)
            {
                case "":
                case "regular":
                case "normal":
                case "book":
                case "roman":
                    return true;

                case "bold":
                case "bd":
                case "b":
                    bold = true;
                    return true;

                case "italic":
                case "oblique":
                case "it":
                case "i":
                    italic = true;
                    return true;

                case "bolditalic":
                case "boldoblique":
                case "italicbold":
                case "bi":
                case "bdi":
                case "z":           // Windows ships "timesbi"/"arialbi"; some foundries use "z"
                    bold = true;
                    italic = true;
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Lower-cases and drops the separators that differ between a PDF font name and a
        /// filename, so "Liberation Sans" and "LiberationSans-Regular" compare equal up to the
        /// style suffix.
        ///
        /// <para><c>internal</c> so <see cref="SystemFontResolver"/>'s family-name pass compares
        /// names the same way this file compares filenames. A second copy would be free to drift,
        /// and the two are answering the same question about the same strings.</para></summary>
        internal static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == ' ' || c == '-' || c == '_' || c == '.') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>Whether the family a platform resolved is the one that was asked for, compared
        /// through <see cref="Normalize"/>: "Times New Roman" matches "TimesNewRoman" (the form a
        /// PostScript name derives to) and a platform's substitute for an unknown family does not.
        ///
        /// <para><c>SKTypeface.FromFamilyName</c> never fails — DirectWrite answers Segoe UI and
        /// fontconfig DejaVu Sans for a family they lack — so every resolution site asks this, never
        /// <c>!= null</c>. <b>The one implementation.</b> <c>FontCache</c>, <c>SystemFontResolver</c>'s
        /// family pass, the Accessibility font remediator and the test suite's font probe each held a
        /// copy otherwise, and copies strip different separator sets.</para></summary>
        internal static bool IsFamilyMatch(string resolvedFamily, string requestedFamily)
        {
            if (string.IsNullOrEmpty(resolvedFamily) || string.IsNullOrEmpty(requestedFamily)) return false;
            return Normalize(resolvedFamily) == Normalize(requestedFamily);
        }
    }
}

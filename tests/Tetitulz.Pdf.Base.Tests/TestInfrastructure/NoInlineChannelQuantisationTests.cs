// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Fails the build if any file in <c>Tetitulz.Pdf.Base</c> other than <c>PdfColor.cs</c> writes its
/// own clamp-and-round of a normalised value to an 8-bit channel instead of calling
/// <c>PdfColor.Channel</c> / <c>ClampChannel</c> / <c>Clamp255</c>.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <c>PdfColor.Channel</c>'s own doc comment says "Any new site that
/// turns a [0,1] value into a channel byte belongs here rather than writing its own cast." That
/// sentence was true and unenforced, and thirteen sites disagreed with it — <b>two of them by
/// truncating where <c>PdfColor</c> rounds</b>: the annotation <c>/DA</c> colour parser, so
/// <c>0.5 g</c> rendered 127 there and 128 everywhere else in the same assembly; and Coons/tensor
/// patch interpolation. CLAUDE.md's rule is exactly this: <i>a comment asserting a safety property
/// is not the property</i>.</para>
///
/// <para><b>The rule is the quantisation, not "colour", and that is deliberate.</b> Six of the
/// sites quantise a constant alpha rather than a colour channel. Excluding them needs an
/// <c>(?i)alpha|opacity</c> name test — an exemption list in regex clothing, which would silently
/// exempt the next colour site whose local happens to be called <c>alphaAdjustedRed</c>. Widening
/// the rule cost six one-line, output-identical edits and buys a guard with no heuristics.</para>
///
/// <para><b>One live violation is structurally outside this guard's reach, and saying so is the
/// point.</b> <c>ContentStreamInterpreter.Shading.cs</c> returned
/// <c>new SKColor((byte)r, (byte)g, (byte)b)</c> on floats already in the 0–255 domain: no
/// <c>255</c>, no clamp idiom, nothing a source scan can key on without type information. It was
/// fixed by hand in the commit that added this file. <b>A guard advertised as complete while a live
/// instance sits outside it is worse than an honest one</b> — if you are auditing this rule, grep
/// for bare <c>(byte)</c> casts of float locals too.</para>
///
/// <para><b>It has no exemption list</b>, per CLAUDE.md: "An empty exemption set is an invitation;
/// no exemption set is a rule." That is affordable because of <see cref="IntegerRescale"/>, not
/// because the pattern is rare.</para>
/// </remarks>
public sealed class NoInlineChannelQuantisationTests
{
    /// <summary>The one file that legitimately implements the quantisation.</summary>
    private const string DeclaringFile = "PdfColor.cs";

    /// <summary>A <c>byte</c> cast that scales a normalised value up to a channel.</summary>
    private static readonly Regex ScaleToChannel = new(
        @"\(\s*byte\s*\)[^;]*?\*\s*(?:255(?:\.0+)?[fFdD]?|MaxChannelValueF)\b", RegexOptions.Compiled);

    /// <summary>
    /// A <c>byte</c> cast wrapped around the clamp idiom — the sites where the <c>* 255</c> already
    /// happened and the statement only clamps and rounds.
    /// </summary>
    private static readonly Regex ClampToChannel = new(
        @"\(\s*byte\s*\)\s*\(?\s*Math\.(?:Max|Min)\s*\(", RegexOptions.Compiled);

    private static readonly Regex ChannelBound = new(
        @"\b255(?:\.0+)?[fFdD]?\b|\bMaxChannelValueF?\b", RegexOptions.Compiled);

    /// <summary>
    /// The discriminator that lets this guard run without exemptions.
    ///
    /// <para><c>v * 255 / max</c> is an <b>integer rescale from an arbitrary maximum</b> — an
    /// n-bit-per-component sample expanded to 8 bpc, or a JPEG 2000 precision normalised. It is not
    /// a [0,1] quantisation, <c>PdfColor.Channel</c> cannot express it, and it is the only shape in
    /// Base that <see cref="ScaleToChannel"/> otherwise catches wrongly. This plays the role
    /// <c>ChannelScale</c> plays in <see cref="NoInlineCmykConversionTests"/>: without it the guard
    /// needs an allow-list, and an allow-list is what this family of guard exists to avoid.</para>
    /// </summary>
    private static readonly Regex IntegerRescale = new(@"\*\s*255\s*/", RegexOptions.Compiled);

    private static bool IsViolation(string statement) =>
        !IntegerRescale.IsMatch(statement) &&
        (ScaleToChannel.IsMatch(statement) ||
         (ClampToChannel.IsMatch(statement) && ChannelBound.IsMatch(statement)));

    private static string BaseRoot
    {
        get
        {
            string? src = RepoSources.SrcRoot;
            if (src == null) return "";
            string? csproj = Directory
                .EnumerateFiles(src, "Tetitulz.Pdf.Base.csproj", SearchOption.AllDirectories)
                .FirstOrDefault();
            return csproj == null ? "" : Path.GetDirectoryName(csproj)!;
        }
    }

    /// <summary>
    /// A floor: proves the detector fires on every historical shape and leaves alone the integer
    /// rescales it shares a pattern with.
    /// </summary>
    /// <remarks>
    /// It validates the <b>detector</b> against synthetic input rather than asserting that
    /// <c>PdfColor.cs</c> matches the pattern — the lesson
    /// <see cref="NoInlineCmykConversionTests"/> records: keying a guard on the shape of the
    /// <i>correct</i> code couples it to a refactor that ought to be free.
    /// </remarks>
    [RepoSourcesFact]
    public void The_guard_fires_on_every_real_shape_and_ignores_integer_rescales()
    {
        Assert.True(Directory.Exists(BaseRoot),
            $"Could not locate Tetitulz.Pdf.Base under {RepoSources.SrcRoot}.");
        Assert.True(
            Directory.EnumerateFiles(BaseRoot, DeclaringFile, SearchOption.AllDirectories).Any(),
            $"Could not locate the exempted {DeclaringFile}; the exemption would silently cover nothing.");

        // Every spelling the thirteen real sites were written in.
        foreach (var banned in new[]
        {
            "byte a = (byte)(alpha * 255f + 0.5f);",
            "(byte)(Math.Min(1f, Math.Max(0f, r)) * 255f),",
            "byte a = (byte)Math.Max(0, Math.Min(255, (int)(alpha * 255f + 0.5f)));",
            "byte tintByte = (byte)(kv.Value * MaxChannelValueF + 0.5f);",
            "lut[v] = (byte)Math.Max(0, Math.Min(MaxChannelValue, (int)(o * MaxChannelValueF + 0.5f)));",
            "(byte)Math.Max(0, Math.Min(255, (int)(from + (to - from) * t + 0.5f)));",
            "cmyk[i * 4] = (byte)(Math.Min(1.0, c) * 255 + 0.5);",
        })
        {
            Assert.True(IsViolation(banned), $"The guard would not catch this violation: {banned}");
        }

        // Integer rescales from an arbitrary maximum, which this rule must leave alone.
        foreach (var allowed in new[]
        {
            "outBuf[(long)y * perRow + i] = (byte)(v * 255 / max);",
            "byte b = (byte)(Math.Max(0, Math.Min(255, (v - minVal) * 255 / range)));",
        })
        {
            Assert.False(IsViolation(allowed), $"The guard would falsely flag an integer rescale: {allowed}");
        }

        // A palette index is not a channel: no (byte) cast, so the detector never sees it.
        Assert.False(IsViolation("int idx = (int)Math.Round(indexF, MidpointRounding.AwayFromZero);"));

        // The statement splitter must join a multi-line expression, or the discriminator on the
        // second line cannot exonerate the cast on the first.
        string joined = Statements(
            "byte b = (byte)(Math.Max(0, Math.Min(255,\n    (v - minVal) * 255 / range)));")
            .Single().Text;
        Assert.False(IsViolation(joined),
            "Line-based scanning would flag this; the splitter must present it as one statement.");
    }

    [RepoSourcesFact]
    public void No_file_in_Base_inlines_the_channel_quantisation()
    {
        Assert.True(Directory.Exists(BaseRoot), "Could not locate Tetitulz.Pdf.Base.");

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(BaseRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (RepoSources.IsGenerated(file)) continue;
            if (Path.GetFileName(file) == DeclaringFile) continue;

            foreach (var s in Statements(RepoSources.StripComments(File.ReadAllText(file))))
            {
                if (!IsViolation(s.Text)) continue;
                violations.Add($"{Path.GetRelativePath(BaseRoot, file)}:{s.Line} — {Trim(s.Text)}");
            }
        }

        Assert.True(violations.Count == 0,
            $"{violations.Count} inline channel quantisation(s). Call PdfColor.Channel (float, [0,1]), " +
            "PdfColor.ClampChannel (float, already 0–255) or PdfColor.Clamp255 (double, [0,1]) — one " +
            "rule, and hand-written copies of it have truncated where it rounds twice already " +
            "(GAPS.md §2b):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ------------------------------------------------------------------
    //  Statement splitting
    // ------------------------------------------------------------------

    private readonly record struct Statement(string Text, int Line);

    /// <summary>
    /// Splits already-comment-stripped source into statements, tracking the 1-based line each
    /// starts on.
    /// </summary>
    /// <remarks>
    /// <b>This is the one place this guard must diverge from <see cref="NoInlineCmykConversionTests"/>,
    /// which scans lines.</b> Two expressions at issue span several lines, and line-based scanning
    /// gets both wrong in opposite directions: it misses the luminosity soft-mask quantisation whose
    /// <c>(byte)</c> cast and its <c>255</c> are four lines apart, and it falsely flags the JPEG 2000
    /// sample normalisation whose exonerating <c>* 255 / range</c> is on the line <i>after</i> the
    /// cast.
    /// </remarks>
    private static IEnumerable<Statement> Statements(string source)
    {
        var buf = new StringBuilder();
        int line = 1, startLine = 1;
        bool started = false;

        foreach (char c in source)
        {
            if (c == '\n') { line++; buf.Append(' '); continue; }

            if (c == ';' || c == '{' || c == '}')
            {
                if (started && buf.Length > 0) yield return new Statement(buf.ToString(), startLine);
                buf.Clear();
                started = false;
                continue;
            }

            if (!started && !char.IsWhiteSpace(c)) { started = true; startLine = line; }
            if (started) buf.Append(c == '\r' ? ' ' : c);
        }

        if (started && buf.Length > 0) yield return new Statement(buf.ToString(), startLine);
    }

    private static string Trim(string s)
    {
        string collapsed = Regex.Replace(s.Trim(), @"\s+", " ");
        return collapsed.Length <= 140 ? collapsed : collapsed.Substring(0, 140) + "…";
    }
}

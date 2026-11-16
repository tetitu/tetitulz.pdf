// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// A font's own streams — its program and its <c>/ToUnicode</c> CMap — are decoded through
/// <c>Core.Fonts.FontStreamBytes</c>, under their own ceilings, never through the unbounded
/// <c>GetDecodedData()</c> and the general half-gigabyte.
/// </summary>
/// <remarks>
/// <para><b>Why a scan.</b> Some twenty readers decode those streams — the font model, the renderers'
/// cache, the SVG embedder, the exporter, the conformance and accessibility checkers — and each turns
/// a refused decode into its own failure path, so no one behavioural test reaches them all; a reader
/// put back on <c>GetDecodedData()</c> answers exactly as before on every real font.</para>
/// <para><b>The rule as scanned:</b> in a library file that names <c>/FontFile</c>, <c>/FontFile2</c>,
/// <c>/FontFile3</c> or <c>/ToUnicode</c>, every parameterless <c>GetDecodedData()</c> is on a stream
/// that is neither — a <c>/CIDToGIDMap</c> or <c>/CIDSet</c>, a Type 3 glyph procedure, an appearance
/// stream — listed below by file and receiver. A new decode in such a file is either moved to
/// <c>FontStreamBytes</c> or listed here with what it reads.</para>
/// </remarks>
public class FontStreamDecodeGuardTests
{
    private static readonly Regex FontStreamKey = new Regex(@"\bPdfName\s*\.\s*(FontFile[23]?|ToUnicode)\b");
    private static readonly Regex Decode = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*GetDecodedData\s*\(\s*\)");

    /// <summary>The decodes in such files that read some other stream: file, receiver, and what it is.</summary>
    private static readonly HashSet<string> OtherStreams = new HashSet<string>(StringComparer.Ordinal)
    {
        "tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/EmbeddedGlyphSets.cs mapStream",       // /CIDToGIDMap
        "tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/EmbeddedGlyphSets.cs cidSet",          // /CIDSet
        "tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/SimpleFontWidthProbe.cs proc",          // a Type 3 glyph procedure
    };

    [RepoSourcesFact]
    public void EveryFontProgramAndToUnicodeDecode_GoesThroughItsCeiling()
    {
        Assert.True(FontStreamKey.IsMatch("descriptor[PdfName.FontFile2]") && Decode.IsMatch("ttStream.GetDecodedData()"),
            "the patterns no longer match their own controls, so a clean scan below would mean nothing");

        string src = RepoSources.SrcRoot!;
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        var listedSeen = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (TestSourceScanner.IsGenerated(file)) continue;
            string text = TestSourceScanner.StripComments(File.ReadAllText(file));
            if (!FontStreamKey.IsMatch(text)) continue;
            string rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            scanned.Add(rel);

            foreach (Match m in Decode.Matches(text))
            {
                string key = rel + " " + m.Groups[1].Value;
                if (OtherStreams.Contains(key)) { listedSeen.Add(key); continue; }
                offenders.Add($"{rel}:{TestSourceScanner.LineOf(text, m.Index)} ({m.Groups[1].Value}.GetDecodedData())");
            }
        }

        // The scan reached the readers it exists for, and every listed exception is still there.
        Assert.Contains("tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/PdfFont.cs", scanned);
        Assert.Contains("tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/FontCache.cs", scanned);
        Assert.Contains("plugins/Tetitulz.Pdf.Plugins.Conformance/TrueTypeFontRules.cs", scanned);
        Assert.Equal(OtherStreams.Count, listedSeen.Count);

        Assert.True(offenders.Count == 0,
            "A font program or CMap is decoded without its ceiling: decode it through " +
            "Core.Fonts.FontStreamBytes (CLAUDE.md, the DRY table), or list the stream here if it is another kind:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}

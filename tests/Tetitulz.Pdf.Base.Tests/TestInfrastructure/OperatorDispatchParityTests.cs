// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// The two renderers dispatch the <b>same</b> set of content-stream operators, and between them they
/// cover every operator <c>Core.Constants.PdfOperators</c> names.
/// </summary>
/// <remarks>
/// <para><b>This guards the defect class CLAUDE.md puts first.</b> "Every 'silently wrong output'
/// defect this repo has found in rendering was one of them lagging the other." An operator added to
/// <c>ContentStreamInterpreter.Dispatch</c> and not to <c>SvgContentWriter.Dispatch</c> renders
/// correctly and exports as if the operator were not there — and the raster/SVG parity suite cannot
/// see it, because a page whose export is missing an operator is still internally consistent.</para>
///
/// <para><b>Why a source scan rather than reflection.</b> The dispatch is a <c>switch</c> over string
/// constants inside a private method; there is no runtime list of what it handles. Reading the two
/// files is what makes the question answerable at all, and it is the same technique as the other
/// build-failing guards in this folder.</para>
///
/// <para><b>Currently exact: 72 of 74 in both.</b> The two operators neither dispatches are the
/// inline-image payload markers, which is correct and is asserted below rather than excluded
/// silently — they are consumed by the <c>BI</c> handler's byte scan, not executed.</para>
/// </remarks>
public class OperatorDispatchParityTests
{
    /// <summary>
    /// Operators no dispatch switch may contain, because they are not executed: <c>ID</c> and
    /// <c>EI</c> delimit an inline image's binary payload, which <c>BI</c> consumes by scanning
    /// bytes. Dispatching either would mean the scan had already failed.
    /// </summary>
    private static readonly string[] PayloadMarkers = { "InlineImageData", "EndInlineImage" };

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(TestCorpus.RepoRoot!, relative));

    /// <summary>Every <c>public const string</c> in <c>PdfOperators</c>, by field name.</summary>
    private static Dictionary<string, string> OperatorConstants()
    {
        string src = Read("src/tetitulz.pdf/Tetitulz.Pdf.Core/Constants/PdfOperators.cs");
        return Regex.Matches(src, @"public const string (\w+)\s*=\s*""([^""]+)""")
                    .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
    }

    /// <summary>The operator names a dispatch switch has a <c>case</c> for, by constant or literal.</summary>
    private static HashSet<string> DispatchedBy(string relative)
    {
        string src = Read(relative);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(src, @"case\s+PdfOperators\.(\w+)")) names.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(src, @"case\s+""([^""]+)""")) names.Add(m.Groups[1].Value);
        return names;
    }

    private const string Raster = "src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/ContentStreamInterpreter.cs";
    private const string Svg = "src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/SvgContentWriter.cs";

    private static bool Handles(HashSet<string> dispatch, string field, string value)
        => dispatch.Contains(field) || dispatch.Contains(value);

    /// <summary>
    /// The parity claim itself: an operator one renderer executes and the other does not is content
    /// that renders and vanishes on export, or the reverse.
    /// </summary>
    [RepoSourcesFact]
    public void BothRenderersDispatchTheSameOperators()
    {
        Dictionary<string, string> ops = OperatorConstants();
        HashSet<string> raster = DispatchedBy(Raster), svg = DispatchedBy(Svg);

        var rasterOnly = ops.Where(o => Handles(raster, o.Key, o.Value) && !Handles(svg, o.Key, o.Value))
                            .Select(o => o.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var svgOnly = ops.Where(o => Handles(svg, o.Key, o.Value) && !Handles(raster, o.Key, o.Value))
                         .Select(o => o.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(rasterOnly.Count == 0 && svgOnly.Count == 0,
            "The two renderers dispatch different operators, which is this repository's recurring " +
            "defect: the same file renders one way and exports another, and the pixel-comparing " +
            "parity suite cannot see it.\n" +
            "  raster only: " + (rasterOnly.Count == 0 ? "(none)" : string.Join(", ", rasterOnly)) + "\n" +
            "  svg only:    " + (svgOnly.Count == 0 ? "(none)" : string.Join(", ", svgOnly)) +
            "\n\nAdd the missing case, or — if the operator genuinely has no meaning in the other " +
            "output format — say so in that renderer's dispatch with a comment, which this scan " +
            "will then see as handled.");
    }

    /// <summary>
    /// Every named operator reaches a dispatch, except the two inline-image payload markers. This is
    /// the completeness half: parity alone is satisfied by both renderers ignoring an operator.
    /// </summary>
    [RepoSourcesFact]
    public void EveryNamedOperatorIsDispatchedExceptThePayloadMarkers()
    {
        Dictionary<string, string> ops = OperatorConstants();
        HashSet<string> raster = DispatchedBy(Raster);

        var undispatched = ops.Where(o => !Handles(raster, o.Key, o.Value))
                              .Select(o => o.Key)
                              .Where(n => !PayloadMarkers.Contains(n, StringComparer.Ordinal))
                              .OrderBy(n => n, StringComparer.Ordinal)
                              .ToList();

        Assert.True(undispatched.Count == 0,
            "PdfOperators names these, and no renderer executes them — content using them is " +
            "silently dropped:\n  " + string.Join("\n  ", undispatched));
    }

    /// <summary>
    /// The exclusions are asserted rather than assumed: <c>ID</c> and <c>EI</c> must stay out of both
    /// switches. Dispatching either would mean <c>BI</c>'s payload scan had already lost its place —
    /// so seeing them here is evidence of a defect, not of coverage.
    /// </summary>
    [RepoSourcesFact]
    public void ThePayloadMarkersAreDispatchedByNeitherRenderer()
    {
        Dictionary<string, string> ops = OperatorConstants();
        HashSet<string> raster = DispatchedBy(Raster), svg = DispatchedBy(Svg);

        foreach (string field in PayloadMarkers)
        {
            Assert.True(ops.ContainsKey(field), $"PdfOperators no longer names {field}");
            Assert.False(Handles(raster, field, ops[field]), $"the raster dispatch has a case for {field}");
            Assert.False(Handles(svg, field, ops[field]), $"the SVG dispatch has a case for {field}");
        }
    }

    /// <summary>
    /// A floor for the scan itself. Both regexes returning nothing would make every assertion above
    /// pass while comparing two empty sets — banned pattern 3 wearing a source walk.
    /// </summary>
    [RepoSourcesFact]
    public void TheScanReadsRealDispatchSwitches()
    {
        Dictionary<string, string> ops = OperatorConstants();
        HashSet<string> raster = DispatchedBy(Raster), svg = DispatchedBy(Svg);

        Assert.True(ops.Count > 60, $"only {ops.Count} operator constants found");
        Assert.True(raster.Count > 60, $"only {raster.Count} raster cases found");
        Assert.True(svg.Count > 60, $"only {svg.Count} svg cases found");

        // …and a specific operator each side really does handle, so the sets are not coincidental.
        Assert.True(Handles(raster, "ShowText", ops["ShowText"]));
        Assert.True(Handles(svg, "ShowText", ops["ShowText"]));
    }
}

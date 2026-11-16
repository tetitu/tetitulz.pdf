// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Constants;

/// <summary>
/// Every DoS ceiling is the caller's to set, and setting one reaches the guard that reads it.
/// </summary>
/// <remarks>
/// <para><b>These were 70 <c>public const</c> values and a consumer could not move any of them.</b>
/// A library cannot pick a caller's threat model: only the caller knows whether the next file is a
/// hostile upload or a 400-page press proof. Worse, a <c>const</c> is <i>inlined into the consuming
/// assembly</i> at compile time, so even a consumer who could assign one would not affect the
/// library's own internal use of it.</para>
///
/// <para><b>The constants did not go away, and that is the design.</b> Roughly thirty-five sites in
/// this tree use a limit where the language requires a compile-time constant — another
/// <c>const</c>, a default parameter value, an <c>[InlineData]</c> argument — and a property cannot
/// go in any of them. So each limit is now a settable <c>X</c> plus a <c>DefaultX</c> constant, and
/// the rule is: <b>read <c>X</c> at runtime, use <c>DefaultX</c> only where a constant is
/// required.</b></para>
/// </remarks>
[Collection(PdfLimitsCollection.Name)]
public class PdfLimitsOverrideTests
{
    /// <summary>
    /// <b>Every limit starts at its own default, and every limit has one.</b>
    /// </summary>
    /// <remarks>
    /// Structural rather than behavioural, and it is the test that covers the other ninety-eight:
    /// a limit added later without a matching <c>Default</c>, or one whose property is initialised
    /// from the wrong constant, fails here rather than in whichever guard happens to read it.
    /// </remarks>
    [Fact]
    public void EveryLimitIsSettableAndStartsAtItsOwnDefault()
    {
        var settable = SettableLimits().ToList();

        // The scan must be finding limits; a reflection filter that silently stopped matching
        // would otherwise make this green forever.
        Assert.True(settable.Count >= 60,
            $"Only {settable.Count} settable limits were found; the reflection filter is wrong, not the class.");

        var missing = new List<string>();
        var mismatched = new List<string>();
        foreach (PropertyInfo p in settable)
        {
            FieldInfo d = typeof(PdfLimits).GetField("Default" + p.Name,
                BindingFlags.Public | BindingFlags.Static);
            if (d == null || !d.IsLiteral) { missing.Add(p.Name); continue; }
            if (!Equals(p.GetValue(null), d.GetRawConstantValue())) mismatched.Add(p.Name);
        }

        Assert.True(missing.Count == 0, "No Default constant for: " + string.Join(", ", missing));
        Assert.True(mismatched.Count == 0,
            "Does not start at its default: " + string.Join(", ", mismatched));
    }

    /// <summary>
    /// <b>Lowering a ceiling reaches the guard that reads it</b> — here the indirect-reference hop
    /// cap, which a chain of five links then trips.
    /// </summary>
    /// <remarks>
    /// The chain is well inside the built-in ceiling of
    /// <see cref="PdfLimits.DefaultMaxIndirectReferenceChain"/>, so the acceptance half below is
    /// what the same file does untouched — the refusal is caused by the setting and by nothing
    /// about the fixture.
    /// </remarks>
    [Fact]
    public void LoweringACeilingReachesTheGuardThatReadsIt()
    {
        byte[] pdf = ChainPdf(links: 5);

        using (var clean = PdfDocument.Open(new MemoryStream(pdf)))
            Assert.Equal(42, Assert.IsType<PdfNumber>(clean.ResolveObject(new PdfReference(4, 0))).IntValue);

        try
        {
            PdfLimits.MaxIndirectReferenceChain = 2;

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            Assert.IsType<PdfNull>(doc.ResolveObject(new PdfReference(4, 0)));
            Assert.Contains("hops", PdfParser.LastObjectParseError, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// <b>A limit read through an alias follows the override too.</b>
    /// </summary>
    /// <remarks>
    /// Thirty-three private aliases in this tree were <c>private const X = PdfLimits.Y;</c>, which
    /// baked the value in at compile time. They are expression-bodied properties now, so they see
    /// the current setting; <c>CcittFaxEncoder</c>'s are the public ones and stand in for the rest.
    /// </remarks>
    [Fact]
    public void ALimitReadThroughAnAliasFollowsTheOverride()
    {
        Assert.Equal(PdfLimits.MaxCcittColumns, Core.IO.Filters.CcittFaxEncoder.MaxEncodeColumns);

        try
        {
            PdfLimits.MaxCcittColumns = 4096;
            Assert.Equal(4096, Core.IO.Filters.CcittFaxEncoder.MaxEncodeColumns);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// <b><c>ResetToDefaults</c> puts every limit back</b> — the method a test suite needs more than
    /// a consumer does, because a setting one test leaves behind changes what every later test in
    /// the same process means.
    /// </summary>
    [Fact]
    public void ResetToDefaultsRestoresEveryLimit()
    {
        var settable = SettableLimits().ToList();

        foreach (PropertyInfo p in settable)
            p.SetValue(null, p.PropertyType == typeof(int) ? (object)7
                           : p.PropertyType == typeof(long) ? (object)7L
                           : (object)7d);

        // The fixture is potent: they really did move.
        Assert.All(settable, p => Assert.Equal(7d, Convert.ToDouble(p.GetValue(null))));

        PdfLimits.ResetToDefaults();

        var stillWrong = settable
            .Where(p => !Equals(p.GetValue(null),
                                typeof(PdfLimits).GetField("Default" + p.Name,
                                    BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue()))
            .Select(p => p.Name)
            .ToList();

        Assert.True(stillWrong.Count == 0,
            "Not restored by ResetToDefaults: " + string.Join(", ", stillWrong));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>Every public settable static limit on <see cref="PdfLimits"/>.</summary>
    private static IEnumerable<PropertyInfo> SettableLimits()
        => typeof(PdfLimits)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => p.PropertyType == typeof(int)
                     || p.PropertyType == typeof(long)
                     || p.PropertyType == typeof(double))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>A document holding a chain of <paramref name="links"/> references ending at 42.</summary>
    private static byte[] ChainPdf(int links)
    {
        var bodies = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n",
        };
        for (int i = 0; i < links; i++)
            bodies.Add($"{4 + i} 0 obj\n{4 + i + 1} 0 R\nendobj\n");
        bodies.Add($"{4 + links} 0 obj\n42\nendobj\n");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string b in bodies) { offsets.Add(sb.Length); sb.Append(b); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

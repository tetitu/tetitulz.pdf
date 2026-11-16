// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// An object the checkers cannot read is not an absent one (<see cref="UnreadableObjects"/>). The
/// PDF/UA-1 checker, the PDF/UA-2 checker and the accessibility fixer each resolved through a copy
/// that answered both with null and said nothing. Each now ends its report with one notice naming
/// how many objects could not be read.
/// </summary>
public sealed class UnreadableObjectsTests
{
    private const string Notice = "could not be read";

    [Fact]
    public void Adversarial_unreadable_struct_tree_root_is_reported_as_incomplete_by_the_ua1_checker()
    {
        var result = PdfUaChecker.Validate(new MemoryStream(Pdf(StructTreeRoot.Unreadable)));

        var notice = Assert.Single(result.Violations, v => v.Message.Contains(Notice, StringComparison.Ordinal));
        Assert.Equal("UA99", notice.Code);
        Assert.StartsWith("1 object(s)", notice.Message, StringComparison.Ordinal);
    }

    /// <summary>The controls: a readable root, and a reference to an object the file does not have.</summary>
    [Theory]
    [InlineData(StructTreeRoot.Readable)]
    [InlineData(StructTreeRoot.Absent)]
    public void A_readable_or_absent_struct_tree_root_is_not_reported_as_unreadable(StructTreeRoot root)
    {
        var result = PdfUaChecker.Validate(new MemoryStream(Pdf(root)));

        Assert.NotEmpty(result.Violations);
        Assert.DoesNotContain(result.Violations, v => v.Message.Contains(Notice, StringComparison.Ordinal));
    }

    [Fact]
    public void Adversarial_unreadable_metadata_is_reported_as_incomplete_by_the_ua2_checker()
    {
        var result = PdfUa2Checker.Validate(new MemoryStream(Pdf(StructTreeRoot.Readable, unreadableMetadata: true)));
        var control = PdfUa2Checker.Validate(new MemoryStream(Pdf(StructTreeRoot.Readable)));

        Assert.Contains(result.Violations, v => v.Code == "UA2-99" && v.Message.Contains(Notice, StringComparison.Ordinal));
        Assert.NotEmpty(control.Violations);
        Assert.DoesNotContain(control.Violations, v => v.Message.Contains(Notice, StringComparison.Ordinal));
    }

    /// <summary>
    /// The fixer sets /DisplayDocTitle in a /ViewerPreferences it cannot read as if there were none,
    /// which drops whatever else the dictionary held. It still does, and now says so.
    /// </summary>
    [Fact]
    public void Adversarial_unreadable_viewer_preferences_are_reported_by_the_fixer()
    {
        var result = PdfAccessibilityFixer.Fix(
            new MemoryStream(Pdf(StructTreeRoot.Readable, unreadableViewerPreferences: true)), new MemoryStream());
        var control = PdfAccessibilityFixer.Fix(new MemoryStream(Pdf(StructTreeRoot.Readable)), new MemoryStream());

        Assert.Contains(result.Warnings, w => w.Contains(Notice, StringComparison.Ordinal));
        Assert.Contains(result.FixesApplied, f => f.Contains("DisplayDocTitle", StringComparison.Ordinal));
        Assert.Contains(control.FixesApplied, f => f.Contains("DisplayDocTitle", StringComparison.Ordinal));
        Assert.DoesNotContain(control.Warnings, w => w.Contains(Notice, StringComparison.Ordinal));
    }

    /// <summary>The record is per run: a clean run after an unreadable one reports nothing.</summary>
    [Fact]
    public void A_clean_run_after_an_unreadable_one_carries_no_notice()
    {
        PdfUaChecker.Validate(new MemoryStream(Pdf(StructTreeRoot.Unreadable)));
        var clean = PdfUaChecker.Validate(new MemoryStream(Pdf(StructTreeRoot.Readable)));

        Assert.NotEmpty(clean.Violations);
        Assert.DoesNotContain(clean.Violations, v => v.Message.Contains(Notice, StringComparison.Ordinal));
        Assert.Equal(0, UnreadableObjects.Count);
    }

    public enum StructTreeRoot { Readable, Unreadable, Absent }

    /// <summary>
    /// A one-page tagged document whose catalog names object 4 as its structure tree root (object 9,
    /// which the file does not have, for <see cref="StructTreeRoot.Absent"/>) and object 5 as its
    /// metadata. An unreadable object is a body of a lone <c>]</c>; one inside a dictionary is
    /// passed over by the tolerant parser, and the dictionary reads.
    /// </summary>
    private static byte[] Pdf(StructTreeRoot root, bool unreadableMetadata = false, bool unreadableViewerPreferences = false)
    {
        string rootRef = root == StructTreeRoot.Absent ? "9 0 R" : "4 0 R";
        string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>";
        string[] bodies =
        {
            $"<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot {rootRef} /Metadata 5 0 R /Lang (en) /ViewerPreferences 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            root == StructTreeRoot.Unreadable ? "]" : "<< /Type /StructTreeRoot /K [] >>",
            unreadableMetadata
                ? "]"
                : $"<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream",
            unreadableViewerPreferences ? "]" : "<< /Direction /L2R >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

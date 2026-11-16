// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A field lock is about what a reader shows: the value, the appearance beside it, and every field of
/// the name, so a change the lock exists to report cannot be stood in front of.
/// </summary>
public sealed class FieldLockForgeryTests
{
    private const string Lock = "/Reference [<< /TransformMethod /FieldMDP /TransformParams << /Action /Include /Fields [{0}] >> >>]";

    private static byte[] Assemble(IReadOnlyList<string> bodies, int end)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i].Replace("@END", end.ToString("D10", CultureInfo.InvariantCulture))).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>A form of the fields given, objects 5 up, and a signature locking the names given, its range the whole file.</summary>
    private static byte[] Signed(string lockedNames, params string[] fields)
    {
        string list = string.Concat(fields.Select((_, i) => $"{5 + i} 0 R "));
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R {list}] >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Sig /ByteRange [0 @END @END 0] /Contents <00> " + string.Format(CultureInfo.InvariantCulture, Lock, lockedNames) + " >>",
            "<< /FT /Sig /T (signature) /V 3 0 R >>",
        };
        bodies.AddRange(fields);
        return Assemble(bodies, Assemble(bodies, 0).Length);
    }

    private static long StartXref(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.LastIndexOf("startxref", System.StringComparison.Ordinal);
        return long.Parse(text.Substring(at + 9, text.IndexOf("%%EOF", at, System.StringComparison.Ordinal) - at - 9).Trim(), CultureInfo.InvariantCulture);
    }

    private static byte[] Revise(byte[] pdf, IReadOnlyDictionary<int, string> objects)
    {
        var sb = new StringBuilder(Encoding.Latin1.GetString(pdf));
        var at = new SortedDictionary<int, long>();
        foreach (var o in objects)
        {
            at[o.Key] = sb.Length;
            sb.Append(o.Key).Append(" 0 obj\n").Append(o.Value).Append("\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append("xref\n");
        foreach (var e in at) sb.Append(e.Key).Append(" 1\n").Append(e.Value.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 100 /Root 1 0 R /Prev ").Append(StartXref(pdf)).Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static List<string> Violations(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        return FieldMdpAnalyzer.Analyze(doc, pdf.Length).Single().Violations;
    }

    /// <summary>The control: a change to a field the lock does not name leaves it clean.</summary>
    [Fact]
    public void AFieldTheLockDoesNotName_MayChange()
    {
        byte[] signed = Signed("(a)", "<< /FT /Tx /T (a) /V (1) >>", "<< /FT /Tx /T (b) /V (1) >>");

        Assert.Empty(Violations(Revise(signed, new Dictionary<int, string> { [6] = "<< /FT /Tx /T (b) /V (2) >>" })));
    }

    [Fact]
    public void ALockedFieldsValue_ChangingIsReported()
    {
        byte[] signed = Signed("(a)", "<< /FT /Tx /T (a) /V (1) >>");

        Assert.Single(Violations(Revise(signed, new Dictionary<int, string> { [5] = "<< /FT /Tx /T (a) /V (2) >>" })));
    }

    /// <summary>
    /// <b>A field added under the locked name, listed first, with the old value.</b> A reader shows the
    /// widget on the page, whose value did change; a lookup of the first of the name saw only the decoy.
    /// </summary>
    [Fact]
    public void AdversarialDecoyOfALockedName_ListedFirst_DoesNotHideTheChange()
    {
        byte[] signed = Signed("(a)", "<< /FT /Tx /T (a) /V (1) >>");

        byte[] forged = Revise(signed, new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 9 0 R 5 0 R] >> >>",
            [5] = "<< /FT /Tx /T (a) /V (2) >>",
            [9] = "<< /FT /Tx /T (a) /V (1) >>",
        });

        Assert.Single(Violations(forged));
    }

    /// <summary>
    /// <b>The value kept, what a reader draws replaced.</b> On the field and on its widget alike; a fill
    /// of a field the lock does not name changes neither.
    /// </summary>
    [Theory]
    [InlineData("<< /FT /Tx /T (a) /V (1) /AP << /N 9 0 R >> >>")]
    [InlineData("<< /FT /Tx /T (a) /V (1) /AS /Off >>")]
    public void AdversarialAppearance_OfALockedField_IsReported(string rewrite)
    {
        byte[] signed = Signed("(a)", "<< /FT /Tx /T (a) /V (1) >>");

        Assert.Single(Violations(Revise(signed, new Dictionary<int, string> { [5] = rewrite })));
    }

    [Fact]
    public void AdversarialAppearance_OfALockedFieldsWidget_IsReported()
    {
        byte[] signed = Signed("(a)", "<< /FT /Tx /T (a) /V (1) /Kids [6 0 R] >>", "<< /Subtype /Widget /Parent 5 0 R /Rect [0 0 1 1] /AP << /N 8 0 R >> >>");

        byte[] forged = Revise(signed, new Dictionary<int, string> { [6] = "<< /Subtype /Widget /Parent 5 0 R /Rect [0 0 1 1] /AP << /N 9 0 R >> >>" });

        Assert.Single(Violations(forged));
    }

    /// <summary>A lock naming a parent locks the fields below it, whose values are the ones that change.</summary>
    [Fact]
    public void AdversarialChild_OfALockedParent_ChangingIsReported()
    {
        byte[] signed = Signed("(p)", "<< /T (p) /Kids [6 0 R 7 0 R] >>", "<< /FT /Tx /T (a) /V (1) /Parent 5 0 R >>", "<< /FT /Tx /T (b) /V (1) /Parent 5 0 R >>");

        List<string> violations = Violations(Revise(signed, new Dictionary<int, string> { [6] = "<< /FT /Tx /T (a) /V (2) /Parent 5 0 R >>" }));

        Assert.Equal("p.a", Assert.Single(violations).Split('\'')[1]);
    }

    /// <summary>The control: a name only begins like the locked one; the periods are what make a parent.</summary>
    [Fact]
    public void AFieldWhoseNameOnlyBeginsWithALockedOne_IsNotLocked()
    {
        byte[] signed = Signed("(p)", "<< /FT /Tx /T (p) /V (1) >>", "<< /FT /Tx /T (pq) /V (1) >>");

        Assert.Empty(Violations(Revise(signed, new Dictionary<int, string> { [6] = "<< /FT /Tx /T (pq) /V (2) >>" })));
    }
}

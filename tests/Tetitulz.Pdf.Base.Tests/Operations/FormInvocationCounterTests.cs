// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="FormInvocationCounter"/> — "is this Form XObject drawn exactly once in the whole
/// document?", which is what lets redaction rewrite a form in place instead of cloning it.
/// </summary>
/// <remarks>
/// <para><b>Erring toward "shared" costs a refused edit; erring the other way corrupts a page
/// nobody looked at.</b> So every case here is about the <i>conservative</i> direction being taken:
/// a form invoked from inside another form that is itself drawn twice is not uniquely drawn, a
/// cycle answers false rather than recursing, and a form reached only through an unresolvable
/// reference is not claimed.</para>
/// <para>A mutation sweep found four of these decisions reached by nothing — the redaction suites
/// assert the <i>output</i>, and a clone where an in-place edit would have done is invisible in it.</para>
/// </remarks>
public class FormInvocationCounterTests
{
    private static Func<int, bool> UniquelyDrawn(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return FormInvocationCounter.BuildUniquelyDrawnTest(doc);
    }

    /// <summary>A form drawn once from page content is uniquely drawn.</summary>
    [Fact]
    public void AFormDrawnOnceFromPageContentIsUnique()
    {
        // 5 = the only form, invoked once by the page.
        Assert.True(UniquelyDrawn(Doc("/Fm5 Do", null, ("5", "BT ET", null)))(5));
    }

    /// <summary>Two invocations from the same page are not.</summary>
    [Fact]
    public void AFormDrawnTwiceIsNotUnique()
    {
        Assert.False(UniquelyDrawn(Doc("/Fm5 Do /Fm5 Do", null, ("5", "BT ET", null)))(5));
    }

    /// <summary>
    /// A form invoked once — but from inside a form that is itself drawn twice — is drawn twice.
    /// Counting sites alone scores it 1 and rewrites it, changing the other place it appears.
    /// </summary>
    [Fact]
    public void AFormInsideAFormDrawnTwiceIsNotUnique()
    {
        // The page draws Fm5 twice; Fm5 draws Fm6 once.
        byte[] pdf = Doc("/Fm5 Do /Fm5 Do", null, ("5", "/Fm6 Do", "6"), ("6", "BT ET", null));

        Assert.False(UniquelyDrawn(pdf)(6));
    }

    /// <summary>The control: the same nesting where the outer form is drawn once.</summary>
    [Fact]
    public void AFormInsideAFormDrawnOnceIsUnique()
    {
        byte[] pdf = Doc("/Fm5 Do", null, ("5", "/Fm6 Do", "6"), ("6", "BT ET", null));

        Assert.True(UniquelyDrawn(pdf)(6));
    }

    /// <summary>
    /// A form that invokes itself answers false rather than recursing forever. A form that
    /// transitively invokes itself is never safe to rewrite in place in any case.
    /// </summary>
    [Fact]
    public void AdversarialSelfInvokingFormIsNotUniqueAndDoesNotRecurse()
    {
        byte[] pdf = Doc("/Fm5 Do", null, ("5", "/Fm5 Do", "5"));

        Assert.False(UniquelyDrawn(pdf)(5));
    }

    /// <summary>
    /// A form whose own <c>/Resources</c> are absent inherits the enclosing scope's, which is how
    /// §7.8.3 lets a form rely on the page's resource dictionary. Without that, a nested invocation
    /// resolves to nothing and the inner form is never counted at all.
    /// </summary>
    [Fact]
    public void AFormWithNoResourcesInheritsTheEnclosingScope()
    {
        // Fm5 has no /Resources of its own, and draws Fm6 — which only the PAGE's resources name,
        // so the invocation resolves through inheritance or not at all.
        byte[] pdf = Doc("/Fm5 Do", alsoNamedByPage: "6",
                         forms: new[] { ("5", "/Fm6 Do", (string)null), ("6", "BT ET", (string)null) });

        Assert.True(UniquelyDrawn(pdf)(6));
    }

    /// <summary>An object number no form claims answers false rather than being assumed unique.</summary>
    [Fact]
    public void AnUnknownObjectNumberIsNotClaimed()
    {
        Assert.False(UniquelyDrawn(Doc("/Fm5 Do", null, ("5", "BT ET", null)))(99));
    }

    /// <summary>
    /// A chain of forms nested deeper than <see cref="PdfLimits.MaxFormXObjectDepth"/> stops being
    /// descended, and what lies past the cap is <b>not</b> claimed as uniquely drawn.
    /// </summary>
    /// <remarks>
    /// <para>The conservative direction is the whole point: a form the walk never reached has no
    /// recorded site, and "I did not see it invoked" must not read as "it is invoked once". Without
    /// the cap a hostile file nests as deep as it likes and the walk follows.</para>
    /// <para>The control is the same chain one level inside the cap, so the refusal is attributable
    /// to the depth and not to the shape.</para>
    /// </remarks>
    [Theory]
    [InlineData(PdfLimits.DefaultMaxFormXObjectDepth + 3, false)]
    [InlineData(4, true)]
    public void AdversarialFormNestingDepthIsNotFollowedPastTheCap(int depth, bool expected)
    {
        // Fm5 → Fm6 → … each form drawing the next, and only the first is named by the page.
        var forms = new List<(string, string, string)>();
        for (int i = 0; i < depth; i++)
        {
            string self = (5 + i).ToString(CultureInfo.InvariantCulture);
            string next = (6 + i).ToString(CultureInfo.InvariantCulture);
            bool last = i == depth - 1;
            forms.Add((self, last ? "BT ET" : "/Fm" + next + " Do", last ? null : next));
        }

        int deepest = 5 + depth - 1;

        Assert.Equal(expected, UniquelyDrawn(Doc("/Fm5 Do", null, forms.ToArray()))(deepest));
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// A one-page document whose page draws <paramref name="pageContent"/>. Each form is
    /// (object number, its content, the object number it names in its own <c>/Resources</c> — null
    /// for a form with no resource dictionary of its own).
    /// </summary>
    /// <remarks>
    /// <b>The page names only the forms it draws itself.</b> Naming every form there instead makes
    /// the page's scope reach all of them, so a form that fails to read its <i>own</i>
    /// <c>/Resources</c> still resolves everything through inheritance — which is exactly how the
    /// first version of this fixture hid the decision it was written for.
    /// </remarks>
    /// <param name="alsoNamedByPage">
    /// A form the page's resources name although the page does not draw it — the shape a nested
    /// form needs when it has no resource dictionary of its own and must inherit one.
    /// </param>
    private static byte[] Doc(string pageContent, string alsoNamedByPage = null,
                              params (string Obj, string Content, string Names)[] forms)
    {
        var xobjects = new StringBuilder();
        foreach (var f in forms)
            if (pageContent.Contains("/Fm" + f.Obj + " ", StringComparison.Ordinal) || f.Obj == alsoNamedByPage)
                xobjects.Append("/Fm").Append(f.Obj).Append(' ').Append(f.Obj).Append(" 0 R ");

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /XObject << " + xobjects + ">> >> >>",
            Stream(pageContent, string.Empty),
        };

        // Objects 5.. are the forms, in the order given; their numbers are their position.
        foreach (var f in forms)
        {
            string resources = f.Names == null
                ? string.Empty
                : " /Resources << /XObject << /Fm" + f.Names + " " + f.Names + " 0 R >> >>";
            bodies.Add(Stream(f.Content, " /Type /XObject /Subtype /Form /BBox [0 0 100 100]" + resources));
        }

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string content, string extraKeys) =>
        "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + extraKeys
        + " >>\nstream\n" + content + "\nendstream";
}

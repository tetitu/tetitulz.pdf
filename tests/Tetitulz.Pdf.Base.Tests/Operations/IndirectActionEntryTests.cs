// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An action's entries are read through the reference-following resolve, at every level.
/// </summary>
/// <remarks>
/// <para><b>The resolution had landed on one arm out of six.</b> <c>PdfActionRunner</c> resolved
/// <c>/URI</c>, with a comment giving the reason — an indirect string read raw yields null, which it
/// then reports as an action with no target — and the next five arms used the plain indexer.
/// <c>PdfDictionary</c>'s indexer returns the <i>stored</i> value, so <c>6 0 R</c> is
/// indistinguishable from an absent key. All six now go through one <c>Entry</c> helper, which is
/// what these tests pin.</para>
///
/// <para><b><c>/JS</c> was the worst of them and its own code proved it.</b> §12.6.4.16 allows a
/// text string <i>or a text stream</i>, and a PDF stream is <b>always</b> an indirect object — so
/// the stream-extracting fallback beside that read, which exists for nothing else, could never
/// receive a stream. A stream-valued JavaScript action ran an empty script and still reported
/// success. It is not asserted directly here because the script text only reaches a registered
/// JavaScript plugin, and hooking the plugin singleton from a test couples this file to every other
/// test in the process. It shares the one <c>Entry</c> helper the tests below exercise, and the
/// mutation that removes the resolve from that helper reddens them.</para>
///
/// <para>The dispatcher resolved all of these already — two implementations of one question, one
/// lagging. Its own remaining gap was a level deeper: a file specification dictionary's inner
/// <c>/F</c>, which §7.11.3 also allows to be indirect.</para>
/// </remarks>
public class IndirectActionEntryTests
{
    /// <summary>
    /// A <c>GoTo</c> whose <c>/D</c> is an indirect array reports that destination, not nothing.
    /// </summary>
    /// <remarks>
    /// The commonest indirect of the six: a destination array is routinely written as its own
    /// object, so this is ordinary content rather than an adversarial shape.
    /// </remarks>
    [Fact]
    public void AGoToWithAnIndirectDestinationReportsIt()
    {
        string dest = null;
        RunOpenAction(
            GoToPdf(),
            r => r.NavigationRequested += (_, e) => dest = e.Destination);

        // "[array:5]" is what the describer says about a five-element destination array, and it is
        // reachable ONLY once the reference has been followed. "Non-empty" was the first assertion
        // here and it could not fail: an unresolved reference falls through the describer's default
        // arm to PdfReference.ToString(), which is also non-empty. The mutation that reverted this
        // one arm survived until the assertion named the resolved form.
        Assert.Equal("[array:5]", dest);
    }

    /// <summary>
    /// A <c>Launch</c> whose <c>/F</c> is an indirect <b>string</b> reports the file name.
    /// </summary>
    /// <remarks>
    /// §7.11.2 lets <c>/F</c> be a file-name string directly, and that is the case this pins. A
    /// <i>file specification dictionary</i> is a different shape and the runner only describes it
    /// rather than reading its inner <c>/F</c> — which the dispatcher does, and which is a gap in
    /// the runner rather than part of the non-resolving read fixed here. Not conflated: the change
    /// under test is that the entry is resolved at all.
    /// </remarks>
    [Fact]
    public void ALaunchWithAnIndirectFileNameReportsIt()
    {
        string target = null;
        RunOpenAction(
            LaunchPdf(),
            r => r.LaunchRequested += (_, e) => target = e.Target);

        Assert.False(string.IsNullOrEmpty(target), "an indirect /F produced no launch target");
        Assert.Contains("report.doc", target);
    }

    /// <summary>
    /// The acceptance control: a <b>direct</b> entry still works, so the fix is a widening rather
    /// than a swap.
    /// </summary>
    /// <remarks>
    /// Resolving an object that is already direct must be the identity. Without this, replacing the
    /// read with something that only ever followed references would satisfy the tests above and
    /// break every ordinary file.
    /// </remarks>
    [Fact]
    public void ADirectDestinationStillReportsIt()
    {
        string dest = null;
        RunOpenAction(
            GoToDirectPdf(),
            r => r.NavigationRequested += (_, e) => dest = e.Destination);

        Assert.Equal("[array:5]", dest);
    }

    /// <summary>
    /// An indirect <c>/S</c> still selects the action, in the runner and in the dispatcher.
    /// </summary>
    /// <remarks>
    /// The subtype decides everything else, and it was the one entry still read raw after the other
    /// six were fixed: <c>/S 5 0 R</c> made this GoTo <c>Unknown</c>, so the runner raised nothing
    /// and the dispatcher reported an unknown action.
    /// </remarks>
    [Fact]
    public void AnActionWhoseSubtypeIsIndirectIsStillThatAction()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
            "<< /Type /Action /S 5 0 R /D [3 0 R /XYZ 0 100 null] >>",
            "/GoTo");

        string dest = null;
        RunOpenAction(pdf, r => r.NavigationRequested += (_, e) => dest = e.Destination);
        Assert.Equal("[array:5]", dest);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        PdfAction action = Assert.Single(PdfActionDispatcher.ExtractActions(doc));
        Assert.Equal(PdfActionType.GoTo, action.ActionType);
    }

    /// <summary>
    /// <c>RunFieldAction</c> finds a field by its fully-qualified name whichever way the names on
    /// its path are spelled.
    /// </summary>
    /// <remarks>
    /// The field search read <c>/T</c> raw, so a group written <c>/T 6 0 R</c> contributed no name
    /// and <c>group.kid</c> matched nothing: the field's action never ran. <c>PdfFormField.FieldName</c>
    /// already resolved, so the name the form reader listed was not a name the runner could find.
    /// </remarks>
    [Theory]
    [InlineData(false)]   // /T (group) and /T (kid)
    [InlineData(true)]    // /T 6 0 R and /T 8 0 R
    public void AFieldActionIsFoundByANameWhoseSegmentsAreIndirect(bool indirect)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
            "null",
            "<< /T " + (indirect ? "6 0 R" : "(group)") + " /Kids [7 0 R] >>",
            "(group)",
            "<< /T " + (indirect ? "8 0 R" : "(kid)") + " /Parent 5 0 R /FT /Btn "
                + "/A << /S /GoTo /D [3 0 R /XYZ 0 100 null] >> >>",
            "(kid)");

        string dest = null;
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);
        runner.NavigationRequested += (_, e) => dest = e.Destination;

        runner.RunFieldAction("group.kid", "MouseUp");

        Assert.Equal("[array:5]", dest);
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    private static void RunOpenAction(byte[] pdf, Action<PdfActionRunner> subscribe)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);
        subscribe(runner);

        PdfActionResult result = runner.RunCatalogOpenAction();
        Assert.NotNull(result);
    }

    /// <summary>Catalog <c>/OpenAction</c> is a GoTo whose <c>/D</c> is its own object.</summary>
    private static byte[] GoToPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
        "<< /Type /Action /S /GoTo /D 5 0 R >>",
        "[3 0 R /XYZ 0 100 null]");

    /// <summary>The same with the destination written inline.</summary>
    private static byte[] GoToDirectPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
        "<< /Type /Action /S /GoTo /D [3 0 R /XYZ 0 100 null] >>",
        "null");

    /// <summary>A Launch whose <c>/F</c> is an indirect file-name string (§7.11.2).</summary>
    private static byte[] LaunchPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
        "<< /Type /Action /S /Launch /F 5 0 R >>",
        "(report.doc)");

    private static byte[] Build(params string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append('\n')
           .Append("0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}

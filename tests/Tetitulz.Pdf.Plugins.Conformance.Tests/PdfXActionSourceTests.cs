// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfXValidator"/>'s X07 action-type check reads every action the document holds,
/// through <c>PdfActionDispatcher</c>.
/// </summary>
/// <remarks>
/// It read the head of the <c>/OpenAction</c>, each annotation's <c>/A</c> and each field's
/// <c>/A</c>, and nothing else: an action second on a <c>/Next</c> chain (§12.6.3), which the same
/// trigger runs, and an outline item's action were not looked at, so a <c>/Launch</c> behind a
/// harmless <c>/GoTo</c> passed.
/// </remarks>
public class PdfXActionSourceTests
{
    [Fact]
    public void ALaunchSecondOnTheOpenActionChain_ReportsX07()
    {
        byte[] pdf = Doc("/OpenAction << /S /GoTo /D [3 0 R /Fit] /Next << /S /Launch /F (app.exe) >> >>");

        Assert.Contains(Violations(pdf), v => v.Code == "X07" && v.Message.Contains("/Launch") && v.Message.Contains("/Next chain"));
    }

    [Fact]
    public void AnOutlineItemsJavaScript_ReportsX07()
    {
        byte[] pdf = Doc("/Outlines 4 0 R",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            "<< /Title (x) /Parent 4 0 R /A << /S /JavaScript /JS (x) >> >>");

        Assert.Contains(Violations(pdf), v => v.Code == "X07" && v.Message.Contains("/JavaScript") && v.Message.Contains("outline"));
    }

    /// <summary>
    /// The <c>/Names /JavaScript</c> key is the rule, not the scripts in it: an empty tree lists no
    /// action and is still reported. (A tree with a script is reported twice, once for the key and
    /// once for the script's type, so only an empty one shows the key check is alive.)
    /// </summary>
    [Fact]
    public void AnEmptyDocumentScriptTree_ReportsX07()
    {
        byte[] pdf = Doc("/Names << /JavaScript << /Names [] >> >>");

        Assert.Contains(Violations(pdf), v => v.Code == "X07" && v.Message.Contains("document-level JavaScript"));
    }

    /// <summary>The control: a chain of permitted actions is not an X07.</summary>
    [Fact]
    public void AChainOfNavigationActions_DoesNotReportX07()
    {
        byte[] pdf = Doc("/OpenAction << /S /GoTo /D [3 0 R /Fit] /Next << /S /URI /URI (https://example.test) >> >>");

        Assert.DoesNotContain(Violations(pdf), v => v.Code == "X07");
    }

    /// <summary>
    /// When the dispatcher could not list every action, X07 did not run in full: an X99 says so. A
    /// chain of 300 actions passes the per-chain bound of 256; a chain of 10 is the control.
    /// </summary>
    [Fact]
    public void AnActionListCutAtItsBound_ReportsX99()
    {
        var cut = PdfXValidator.Validate(new MemoryStream(Doc("/OpenAction 4 0 R", Chain(300))));
        var whole = PdfXValidator.Validate(new MemoryStream(Doc("/OpenAction 4 0 R", Chain(10))));

        Assert.Contains(cut.Informational, i => i.Code == "X99" && i.Message.Contains("X07"));
        Assert.DoesNotContain(whole.Informational, i => i.Code == "X99" && i.Message.Contains("X07"));
    }

    /// <summary>
    /// A field tree cut at the walk's depth cap was examined in part, and X07 said nothing: only
    /// the node budget was reported. A shallow tree is the control.
    /// </summary>
    [Fact]
    public void AFieldTreeCutAtItsDepthCap_ReportsX99()
    {
        var deep = PdfXValidator.Validate(new MemoryStream(NestedFields(AcroFormFieldWalk.MaxFieldDepth + 3)));
        var shallow = PdfXValidator.Validate(new MemoryStream(NestedFields(3)));

        Assert.Contains(deep.Informational, i => i.Code == "X99" && i.Message.Contains("depth cap"));
        Assert.DoesNotContain(shallow.Informational, i => i.Code == "X99" && i.Message.Contains("AcroForm field walk"));
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] NestedFields(int depth)
        => Doc("/AcroForm << /Fields [4 0 R] >>", Enumerable.Range(0, depth).Select(i =>
            i + 1 < depth
                ? $"<< /T (f{i}) /Kids [{i + 5} 0 R] >>"
                : $"<< /T (f{i}) /FT /Tx >>").ToArray());

    private static PdfXViolation[] Violations(byte[] pdf)
        => PdfXValidator.Validate(new MemoryStream(pdf)).Violations.ToArray();

    private static string[] Chain(int length)
        => Enumerable.Range(0, length).Select(i =>
            i + 1 < length
                ? $"<< /S /GoTo /D [3 0 R /Fit] /Next {i + 5} 0 R >>"
                : "<< /S /GoTo /D [3 0 R /Fit] >>").ToArray();

    /// <summary>Catalog 1, pages 2, page 3, then <paramref name="objects"/> from 4.</summary>
    private static byte[] Doc(string catalog, params string[] objects)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalog + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        }.Concat(objects).ToArray();

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The variable-text entries <c>/DA</c> and <c>/Q</c> are inheritable (§12.7.3.3, Table 222), and a
/// value on a nearer node that <b>cannot be used</b> must not end the climb: the ancestor holding
/// the usable one has to be reached.
/// </summary>
/// <remarks>
/// <para><b>The climb terminated on the raw stored value being non-null</b>, resolving nothing and
/// accepting anything. Its sibling one method up, <c>GetInheritedInteger</c>, already resolved and
/// type-tested — so <c>/Ff</c> was right and <c>/DA</c> and <c>/Q</c> were wrong, in the same
/// class, in the same file. The repository's documented "landed one level short" shape.</para>
///
/// <para>Three live losses, all of the form "the file said something unusable one level down" being
/// indistinguishable from "the file said nothing": an indirect reference to a free or missing
/// object, a direct <c>null</c>, and a value of the wrong type. In each the parent's real entry was
/// discarded and the caller fell back to its own default.</para>
///
/// <para>The assertions read the resolved <c>DefaultAppearance</c> and <c>Quadding</c> back out of
/// <c>PdfFormReader</c>, so a fix that merely resolved without changing WHICH node wins would still
/// fail them.</para>
/// </remarks>
public class IndirectVariableTextEntryTests
{
    private const string ParentDa = "/Helv 18 Tf 1 0 0 rg";

    /// <summary>An indirect <c>/DA</c> pointing at an object the file does not contain.</summary>
    [Theory]
    [InlineData("/DA 99 0 R")]      // dangling reference
    [InlineData("/DA null")]        // direct null
    [InlineData("/DA /Helv")]       // a name where a string belongs
    public void AnUnusableDefaultAppearanceOnTheChildDoesNotHideTheParents(string childEntry)
    {
        PdfFormFieldInfo field = ReadKid(childEntry, parentQ: null);

        Assert.Equal(ParentDa, field.DefaultAppearance);
    }

    /// <summary>The same rule for <c>/Q</c>, whose unusable value is a string where a number belongs.</summary>
    [Fact]
    public void AnUnusableQuaddingOnTheChildDoesNotHideTheParents()
    {
        PdfFormFieldInfo field = ReadKid("/Q (centre)", parentQ: "/Q 1");

        Assert.Equal(1, field.Quadding);
    }

    /// <summary>
    /// The control: a <b>usable</b> <c>/DA</c> on the child still wins over the parent's.
    /// </summary>
    /// <remarks>
    /// Without this, "always climb past the child" satisfies every case above — and would silently
    /// repaint every field in its group's appearance rather than its own.
    /// </remarks>
    [Fact]
    public void AUsableDefaultAppearanceOnTheChildStillWins()
    {
        PdfFormFieldInfo field = ReadKid("/DA (/Helv 9 Tf 0 1 0 rg)", parentQ: null);

        Assert.Equal("/Helv 9 Tf 0 1 0 rg", field.DefaultAppearance);
    }

    /// <summary>
    /// The other control: an <b>indirect but resolvable</b> <c>/DA</c> on the child wins too — the
    /// rule is about usability, not about directness.
    /// </summary>
    /// <remarks>
    /// This is what separates "resolve the value" from "skip anything written as a reference". A fix
    /// that treated every indirect entry as unusable would pass all the cases above and be wrong on
    /// the commonest way a large form shares one appearance string.
    /// </remarks>
    [Fact]
    public void AnIndirectButResolvableDefaultAppearanceOnTheChildWins()
    {
        PdfFormFieldInfo field = ReadKid("/DA 8 0 R", parentQ: null,
                                      extraObject: "(/Helv 9 Tf 0 1 0 rg)");

        Assert.Equal("/Helv 9 Tf 0 1 0 rg", field.DefaultAppearance);
    }

    // ------------------------------------------------------------------

    private static PdfFormFieldInfo ReadKid(string childEntry, string parentQ, string extraObject = null)
    {
        byte[] pdf = Build(childEntry, parentQ, extraObject);
        using var ms = new MemoryStream(pdf, writable: false);
        IReadOnlyList<PdfFormFieldInfo> fields = PdfFormReader.GetFields(ms);

        return Assert.Single(fields, f => f.PartialName == "kid");
    }

    /// <summary>
    /// Object 6 is the parent field carrying the usable entries; object 7 is its terminal kid,
    /// carrying whatever <paramref name="childEntry"/> says.
    /// </summary>
    private static byte[] Build(string childEntry, string parentQ, string extraObject)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] "
                + "/DR << /Font << /Helv 5 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /T (group) /FT /Tx /DA (" + ParentDa + ") " + (parentQ ?? string.Empty)
                + " /Kids [7 0 R] >>",
            "<< /T (kid) /Parent 6 0 R " + childEntry + " /Rect [10 10 200 30] >>",
        };
        if (extraObject != null) bodies.Add(extraObject);

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

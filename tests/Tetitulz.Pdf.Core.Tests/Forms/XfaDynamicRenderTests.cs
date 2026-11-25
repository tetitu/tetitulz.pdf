// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <see cref="XfaPacket.RequiresDynamicRender"/> — ISO 14289-1 7.15: a form is dynamic when the
/// <c>dynamicRender</c> element, a child of <c>acrobat7</c>, of <c>acrobat</c>, of <c>config</c>, of the
/// <c>xdp</c> root, has the value <c>required</c>.
/// </summary>
public class XfaDynamicRenderTests
{
    private const string ConfigNs = "http://www.xfa.org/schema/xci/3.0/";

    private static string Config(string acrobat7)
        => "<config xmlns=\"" + ConfigNs + "\"><present><pdf><version>1.7</version></pdf></present>" +
           "<acrobat><acrobat7>" + acrobat7 + "</acrobat7></acrobat></config>";

    private static bool? Packaged(string config, out string unreadable, string packetName = "config")
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(
            "[(preamble) 5 0 R (" + packetName + ") 6 0 R (postamble) 7 0 R]",
            Stream("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">"),
            Stream(config),
            Stream("</xdp:xdp>"))));
        return XfaPacket.Read(AcroForm(doc), doc)!.RequiresDynamicRender(out unreadable);
    }

    private static bool? Unpackaged(string xdp, out string unreadable)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf("5 0 R", Stream(xdp))));
        var packet = XfaPacket.Read(AcroForm(doc), doc)!;
        Assert.False(packet.IsPackaged);
        return packet.RequiresDynamicRender(out unreadable);
    }

    [Theory]
    [InlineData("<dynamicRender>required</dynamicRender>", true)]
    [InlineData("<dynamicRender>\n  required\n</dynamicRender>", true)]
    [InlineData("<dynamicRender>forbidden</dynamicRender>", false)]
    [InlineData("<dynamicRender>Required</dynamicRender>", false)]          // XFA keywords are case-sensitive
    [InlineData("<dynamicRender/>", false)]
    [InlineData("<scriptModel>XFA</scriptModel>", false)]                     // no dynamicRender at all
    public void ThePackagedConfigPacket_DecidesByTheElementsValue(string acrobat7, bool dynamic)
    {
        Assert.Equal(dynamic, Packaged(Config(acrobat7), out string unreadable));
        Assert.Null(unreadable);
    }

    /// <summary>
    /// The element is found by its place, not its name alone: a <c>dynamicRender</c> directly under
    /// <c>acrobat</c> is not the one the clause names, and the same value one level down is.
    /// </summary>
    [Theory]
    [InlineData("<config><acrobat><dynamicRender>required</dynamicRender></acrobat></config>")]
    [InlineData("<config><acrobat><acrobat6><dynamicRender>required</dynamicRender></acrobat6></acrobat></config>")]
    [InlineData("<config><present><acrobat7><dynamicRender>required</dynamicRender></acrobat7></present></config>")]
    [InlineData("<form><acrobat><acrobat7><dynamicRender>required</dynamicRender></acrobat7></acrobat></form>")]
    public void ADynamicRenderOutsideConfigAcrobatAcrobat7_IsNotTheOne(string misplaced)
    {
        Assert.Equal(false, Packaged(misplaced, out _));
        Assert.Equal(true, Packaged(Config("<dynamicRender>required</dynamicRender>"), out _));
    }

    /// <summary>In a whole XDP the config element is the root's child, and the root is <c>xdp</c>.</summary>
    [Theory]
    [InlineData("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><config><acrobat><acrobat7><dynamicRender>required</dynamicRender></acrobat7></acrobat></config></xdp:xdp>", true)]
    [InlineData("<form><config><acrobat><acrobat7><dynamicRender>required</dynamicRender></acrobat7></acrobat></config></form>", false)]
    public void AWholeXdpsConfig_IsTheXdpRootsChild(string xdp, bool dynamic)
        => Assert.Equal(dynamic, Unpackaged(xdp, out _));

    /// <summary>A packaged entry with no <c>config</c> packet names no dynamicRender; the same bytes named config do.</summary>
    [Fact]
    public void APackagedEntryWithoutAConfigPacket_IsNotDynamic()
    {
        string config = Config("<dynamicRender>required</dynamicRender>");
        Assert.Equal(false, Packaged(config, out _, packetName: "template"));
        Assert.Equal(true, Packaged(config, out _));
    }

    /// <summary>An unpackaged entry is the whole XDP, its <c>config</c> the root's child.</summary>
    [Theory]
    [InlineData("required", true)]
    [InlineData("forbidden", false)]
    public void TheSingleStreamXdp_IsReadFromItsRoot(string value, bool dynamic)
    {
        string xdp = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">" +
                     Config("<dynamicRender>" + value + "</dynamicRender>") + "<template/></xdp:xdp>";
        Assert.Equal(dynamic, Unpackaged(xdp, out _));
    }

    /// <summary>
    /// A packet that is not XML, or that carries a DTD (prohibited, as for every XML reader of a file's
    /// bytes), is not read: the answer is "unknown" with the reason, never "static".
    /// </summary>
    [Theory]
    [InlineData("<config><acrobat><acrobat7><dynamicRender>required</acrobat7></config>", "could not be read as XML")]
    [InlineData("<!DOCTYPE config [<!ENTITY r \"required\">]><config><acrobat><acrobat7><dynamicRender>&r;</dynamicRender></acrobat7></acrobat></config>", "DTD")]
    public void AnUnreadablePacket_IsUnknown_WithItsReason(string config, string reason)
    {
        Assert.Null(Packaged(config, out string unreadable));
        Assert.Contains(reason, unreadable, System.StringComparison.Ordinal);
        // Control: the same element, readable, is an answer.
        Assert.Equal(true, Packaged(Config("<dynamicRender>required</dynamicRender>"), out _));
    }

    private static PdfDictionary AcroForm(PdfDocument doc)
        => (PdfDictionary)doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.AcroForm]);

    private static string Stream(string text)
        => "<< /Length " + Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "\nendstream";

    private static byte[] Pdf(string xfa, params string[] packets)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Fields [] /XFA " + xfa + " >>",
        };
        objects.AddRange(packets);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.UTF8.GetByteCount(sb.ToString()));
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = Encoding.UTF8.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A value written as an indirect reference must read the same as one written inline.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PdfResolve"/> exists because <c>PdfDictionary</c>'s indexer does <b>not</b> follow a
/// reference, so <c>dict[PdfName.X] as PdfString</c> silently yields <c>null</c> — indistinguishable
/// from "the key is absent" — whenever a producer writes <c>X</c> as <c>9 0 R</c>. Both spellings
/// are legal for essentially every value in the spec, so each unresolved read is a latent
/// "this feature is missing on some files" defect.
/// </para>
/// <para>
/// These pin sites where one reader and its counterpart must agree: a link's <c>/URI</c> read by
/// <c>PdfLinkExtractor</c>, the SVG renderer and <c>PdfActionRunner</c>. The fixtures write the
/// value indirectly <b>and</b> assert the direct form works, so resolution cannot pass by breaking
/// the ordinary case.
/// </para>
/// </remarks>
public class IndirectValueResolutionTests
{
    private const string Uri = "https://example.com/indirect";

    [Theory]
    [InlineData(true)]   // /URI written as "9 0 R"
    [InlineData(false)]  // /URI written inline
    public void SvgExport_EmitsTheLinkUri_WhetherItIsIndirectOrInline(bool indirect)
    {
        byte[] pdf = BuildPdfWithLinkAnnotation(indirect);

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // Parse rather than substring-match: an export that is not well-formed XML has slipped
        // through this suite before.
        var doc = XDocument.Parse(svg);
        bool hasUri = doc.Descendants()
            .Any(e => e.Attributes().Any(a =>
                a.Name.LocalName == "data-uri" && a.Value == Uri));

        Assert.True(hasUri,
            indirect
                ? "The link URI was written as an indirect reference and vanished from the SVG."
                : "The inline case regressed.");
    }

    /// <summary>
    /// A catalog <c>/OpenAction</c> of type <c>/S /URI</c>, which is what
    /// <c>PdfActionDispatcher.CollectActions</c> actually enumerates (it covers the catalog
    /// <c>/OpenAction</c> and <c>/AA</c> and page-level <c>/AA</c> — <b>not</b> link annotations).
    /// </summary>
    /// <remarks>
    /// The divergence this pins sits inside a single <c>switch</c>: the <c>Uri</c> case read
    /// <c>actionDict[PdfName.URI]</c> raw while the adjacent <c>JavaScript</c> case resolved
    /// <c>actionDict[PdfName.JS]</c>. Same method, adjacent lines, opposite discipline.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtractActions_ReadsAnOpenActionUri_WhetherItIsIndirectOrInline(bool indirect)
    {
        byte[] pdf = BuildPdfWithOpenActionUri(indirect);

        using var ms = new MemoryStream(pdf);
        var actions = PdfOperations.ExtractActions(ms);

        Assert.Contains(actions, a => a.Target == Uri);
    }

    /// <summary>
    /// One page carrying a <c>/Link</c> annotation whose action is <c>/S /URI</c>. When
    /// <paramref name="indirect"/> the URI string is object 9 and the action holds <c>9 0 R</c>;
    /// otherwise it is written inline. Everything else is identical.
    /// </summary>
    private static byte[] BuildPdfWithLinkAnnotation(bool indirect)
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string uriValue = indirect ? "9 0 R" : $"({Uri})";

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
            "/Contents 4 0 R /Resources << >> /Annots [5 0 R] >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Annot /Subtype /Link /Rect [10 10 90 30] " +
            "/Border [0 0 1] /A 6 0 R >>\nendobj\n");
        Obj($"6 0 obj\n<< /Type /Action /S /URI /URI {uriValue} >>\nendobj\n");
        Obj("7 0 obj\n<< >>\nendobj\n");
        Obj("8 0 obj\n<< >>\nendobj\n");
        Obj($"9 0 obj\n({Uri})\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// One page whose <b>catalog</b> carries <c>/OpenAction</c> → <c>&lt;&lt; /S /URI /URI … &gt;&gt;</c>,
    /// written indirectly or inline.
    /// </summary>
    private static byte[] BuildPdfWithOpenActionUri(bool indirect)
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string uriValue = indirect ? "5 0 R" : $"({Uri})";

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Type /Action /S /URI /URI {uriValue} >>\nendobj\n");
        Obj($"5 0 obj\n({Uri})\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

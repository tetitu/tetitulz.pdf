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
/// What a sanitized document can still reach out with: a link that names a host with no scheme, one that
/// resolves against a base that does, and the annotations and actions that play a clip from anywhere.
/// </summary>
public sealed class SanitizerMediaAndHostRouteTests
{
    private const string Marker = "ROUTE-MARKER";

    private static byte[] Doc(string catalog, string page, params string[] extra)
    {
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] {page} >>",
        };
        bodies.AddRange(extra);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Sanitized(byte[] pdf, SanitizeOptions options = null)
    {
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(new MemoryStream(pdf), output, options);
        return Encoding.Latin1.GetString(output.ToArray());
    }

    private static byte[] Link(string uri) => Doc("", "/Annots [4 0 R]",
        "<< /Type /Annot /Subtype /Link /Rect [0 0 9 9] /A << /S /URI /URI (" + uri + ") >> >>");

    [Theory]
    [InlineData("\\\\\\\\" + Marker + "\\\\share")]
    [InlineData(" \\\\\\\\" + Marker + "\\\\share")]
    [InlineData("//" + Marker + "/share")]
    [InlineData("file:///" + Marker)]
    public void AdversarialLink_NamingAnotherHostWithoutAWebScheme_IsRemoved(string uri)
    {
        Assert.DoesNotContain(Marker, Sanitized(Link(uri)), System.StringComparison.Ordinal);
    }

    private static string Attached => "<< /Length " + Marker.Length + " >>\nstream\n" + Marker + "\nendstream";

    /// <summary>
    /// ISO 32000-2 §7.11.4.2: a file may be associated with a page, a form, an annotation or a
    /// structure element as well as the catalog, and a stream an <c>/EF</c> names need not say what it is.
    /// </summary>
    [Theory]
    [InlineData("page", "", "/AF [4 0 R]")]
    [InlineData("catalog, by reference", "/AF 6 0 R", "")]
    public void AdversarialAssociatedFile_WithAnUnmarkedStream_IsRemoved(string where, string catalog, string page)
    {
        Assert.NotNull(where);
        byte[] pdf = Doc(catalog, page,
            "<< /Type /Filespec /F (a) /UF (a) /AFRelationship /Data /EF << /F 5 0 R >> >>",
            Attached,
            "[4 0 R]");

        Assert.Contains(Marker, Encoding.Latin1.GetString(pdf), System.StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
    }

    private const string RemoteFile = "<< /FS /URL /F (http://" + Marker + "/x) >>";

    public static IEnumerable<object[]> RoutesOut() => new[]
    {
        new object[] { "a reference XObject", "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Ref << /F " + RemoteFile + " /Page 0 >> /Length 0 >>\nstream\n\nendstream" },
        new object[] { "a reference XObject naming a share", "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Ref << /F (\\\\\\\\" + Marker + "\\\\x.pdf) /Page 0 >> /Length 0 >>\nstream\n\nendstream" },
        new object[] { "an external stream","<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /F " + RemoteFile + " /Length 0 >>\nstream\n\nendstream" },
        new object[] { "an external stream's filter", "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /F (\\\\\\\\" + Marker + "\\\\x) /FFilter /FlateDecode /FDecodeParms << /Predictor 1 >> /Length 0 >>\nstream\n\nendstream" },
        new object[] { "a URL specification anywhere", "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Whatever " + RemoteFile + " /Length 0 >>\nstream\n\nendstream" },
    };

    [Theory]
    [MemberData(nameof(RoutesOut))]
    public void AdversarialRouteOut_InAKeptObject_IsRemoved(string name, string form)
    {
        Assert.NotNull(name);
        byte[] pdf = Doc("", "/Contents 5 0 R /Resources << /XObject << /X 4 0 R >> >>", form, "<< /Length 4 >>\nstream\n/X Do\nendstream");

        Assert.Contains(Marker, Encoding.Latin1.GetString(pdf), System.StringComparison.Ordinal);
        string output = Sanitized(pdf);
        Assert.DoesNotContain(Marker, output, System.StringComparison.Ordinal);
        Assert.Contains("/Form", output, System.StringComparison.Ordinal);   // the form itself stays
    }

    [Fact]
    public void A_form_submission_kept_on_request_keeps_its_address()
    {
        byte[] pdf = Doc("", "/Annots [4 0 R]",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 9 9] /A << /S /SubmitForm /F " + RemoteFile + " >> >>");

        Assert.Contains(Marker, Sanitized(pdf, new SanitizeOptions { RemoveFormActions = false }), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialRelatedFile_OfAFileSpecification_IsRemoved()
    {
        byte[] pdf = Doc("", "/AF [4 0 R]",
            "<< /Type /Filespec /F (a) /RF << /F [(part) 5 0 R] >> >>",
            Attached);

        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialFileSpecification_NestedDeepInDirectObjects_LosesItsPayload()
    {
        string nested = "<< /Type /Filespec /F (a) /EF << /F 5 0 R >> >>";
        for (int i = 0; i < 60; i++) nested = "[" + nested + "]";
        byte[] pdf = Doc("", "/Extra 4 0 R", nested, Attached);

        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_attachment_kept_on_request_keeps_its_payload()
    {
        byte[] pdf = Doc("", "/AF [4 0 R]",
            "<< /Type /Filespec /F (a) /EF << /F 5 0 R >> >>",
            Attached);

        Assert.Contains(Marker, Sanitized(pdf, new SanitizeOptions { RemoveEmbeddedFiles = false }), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialAssociatedFile_OnAForm_IsRemoved()
    {
        byte[] pdf = Doc("", "/Contents 7 0 R /Resources << /XObject << /X 6 0 R >> >>",
            "<< /Type /Filespec /F (a) /EF << /F 5 0 R >> >>",
            Attached,
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /AF [4 0 R] /Length 0 >>\nstream\n\nendstream",
            "<< /Length 5 >>\nstream\n/X Do\nendstream");

        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialAssociatedFile_OnAStructureElement_IsRemoved()
    {
        byte[] pdf = Doc("/StructTreeRoot 6 0 R", "",
            "<< /Type /Filespec /F (a) /EF << /F 5 0 R >> >>",
            Attached,
            "<< /Type /StructTreeRoot /K << /S /P /AF [4 0 R] >> >>");

        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialFileSpecification_ReachedFromAnAction_LosesItsPayload()
    {
        // A kept navigation action naming an embedded target carries a file specification too.
        byte[] pdf = Doc("/Dests 6 0 R", "",
            "<< /Type /Filespec /F (a) /EF << /F 5 0 R >> >>",
            Attached,
            "<< /x << /D [3 0 R /Fit] /FS 4 0 R >> >>");

        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
    }

    /// <summary>The controls: a web link, and a relative one, are the links this option keeps.</summary>
    [Theory]
    [InlineData("https://" + Marker + "/a")]
    [InlineData(Marker + "/a.html")]
    public void AWebOrRelativeLink_IsKept(string uri)
    {
        Assert.Contains(Marker, Sanitized(Link(uri)), System.StringComparison.Ordinal);
    }

    /// <summary>A relative link kept as harmless resolves against the base, so the base answers for it.</summary>
    [Theory]
    [InlineData("\\\\\\\\" + Marker + "\\\\", false)]
    [InlineData("file:///" + Marker + "/", false)]
    [InlineData("https://" + Marker + "/", true)]
    public void ACatalogUriBase_IsKeptOnlyIfItIsAWebAddress(string address, bool kept)
    {
        string output = Sanitized(Doc("/URI << /Base (" + address + ") >>", ""));

        Assert.Equal(kept, output.Contains(Marker, System.StringComparison.Ordinal));
    }

    /// <summary>All URI actions removed, the base has nothing to resolve.</summary>
    [Fact]
    public void ACatalogUriBase_GoesWithEveryLink()
    {
        string output = Sanitized(Doc("/URI << /Base (https://" + Marker + "/) >>", ""), new SanitizeOptions { RemoveURIActions = true });

        Assert.DoesNotContain(Marker, output, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Movie /Movie << /F (" + Marker + ".mov) >>")]
    [InlineData("/Sound /Sound << /F (" + Marker + ".wav) >>")]
    [InlineData("/Screen /T (" + Marker + ")")]
    public void AdversarialMediaAnnotation_IsRemoved(string subtypeAndEntries)
    {
        byte[] pdf = Doc("", "/Annots [4 0 R]", "<< /Type /Annot /Subtype " + subtypeAndEntries + " /Rect [0 0 9 9] >>");

        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
        Assert.Contains(Marker, Sanitized(pdf, new SanitizeOptions { RemoveRichMediaAnnotations = false }), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Rendition /OP 0 /X (" + Marker + ")")]
    [InlineData("/Movie /T (" + Marker + ")")]
    [InlineData("/Sound /Sound << /F (" + Marker + ") >>")]
    [InlineData("/RichMediaExecute /CMD << /C (" + Marker + ") >>")]
    [InlineData("/GoTo3DView /VA (" + Marker + ")")]
    public void AdversarialMediaAction_IsRemoved(string typeAndEntries)
    {
        byte[] pdf = Doc("", "/Annots [4 0 R]", "<< /Type /Annot /Subtype /Link /Rect [0 0 9 9] /A << /S " + typeAndEntries + " >> >>");

        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
        Assert.Contains(Marker, Sanitized(pdf, new SanitizeOptions { RemoveRichMediaAnnotations = false }), System.StringComparison.Ordinal);
    }

    [Fact]
    public void APortfoliosPresentation_GoesWithItsFiles()
    {
        byte[] pdf = Doc("/Collection << /D (" + Marker + ") >>", "");

        Assert.DoesNotContain(Marker, Sanitized(pdf), System.StringComparison.Ordinal);
        Assert.Contains(Marker, Sanitized(pdf, new SanitizeOptions { RemoveEmbeddedFiles = false }), System.StringComparison.Ordinal);
    }
}

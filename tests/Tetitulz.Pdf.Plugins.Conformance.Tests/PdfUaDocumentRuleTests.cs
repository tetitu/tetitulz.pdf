// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// PDF/UA-1's UA58 (optional content configurations, ISO 14289-1 7.10), UA59 (embedded files' /F and
/// /UF, 7.11 and 7.18.7), UA60 (dynamic XFA, 7.15) and UA61 (media clip data, 7.18.6.2).
/// </summary>
public class PdfUaDocumentRuleTests
{
    private const string StandardEncryptDict =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4"
      + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
      + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    /// <summary>
    /// Catalog 1, pages 2, one page 3 (with <paramref name="pageExtra"/>), then <paramref name="extra"/>
    /// from object 4.
    /// </summary>
    private static byte[] Pdf(string catalogExtra, string pageExtra = "", string trailerExtra = "", params string[] extra)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" + pageExtra + " >>",
        };
        objects.AddRange(extra);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(trailerExtra).Append(" >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string text)
        => "<< /Length " + text.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "\nendstream";

    private static List<PdfUaViolation> Coded(byte[] pdf, string code)
        => PdfUaChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code == code).ToList();

    // -------------------------------------------------------------- UA58

    /// <summary>
    /// Once /Configs holds a configuration, every configuration needs a name, the default included; a
    /// /Configs of no dictionary, or none at all, asks nothing of the default's name.
    /// </summary>
    [Theory]
    [InlineData("/D << >> /Configs [<< /Name (Print) >>]", "/D")]
    [InlineData("/D << /Name () >> /Configs [<< /Name (Print) >>]", "/D")]
    [InlineData("/D << /Name (Screen) >> /Configs [<< >>]", "/Configs [0]")]
    [InlineData("/D << /Name /Screen >> /Configs [<< /Name (Print) >>]", "/D")]            // a name object is no text string
    [InlineData("/D << /Name (Screen) >> /Configs [<< /Name (Print) >>]", null)]
    [InlineData("/D << >>", null)]
    [InlineData("/D << >> /Configs []", null)]
    [InlineData("/D << >> /Configs [5]", null)]
    public void UA58_AConfigurationsName_OnceConfigsHoldsOne(string ocProperties, string? unnamed)
    {
        var found = Coded(Pdf(" /OCProperties << /OCGs [] " + ocProperties + " >>"), "UA58");
        if (unnamed == null)
            Assert.Empty(found);
        else
            Assert.Contains(": " + unnamed + ".", Assert.Single(found).Message, StringComparison.Ordinal);
    }

    /// <summary>/AS is refused in every configuration, whatever /Configs holds; its absence is quiet.</summary>
    [Theory]
    [InlineData("/D << /AS [<< /Event /View /Category [/View] /OCGs [] >>] >>", "/D")]
    [InlineData("/D << /Name (Screen) >> /Configs [<< /Name (Print) /AS [] >>]", "/Configs [0]")]
    [InlineData("/D << /Name (Screen) >> /Configs [<< /Name (Print) >>]", null)]
    public void UA58_AnAsEntry_InAnyConfiguration(string ocProperties, string? where)
    {
        var found = Coded(Pdf(" /OCProperties << /OCGs [] " + ocProperties + " >>"), "UA58")
            .Where(v => v.Message.Contains("/AS entry", StringComparison.Ordinal)).ToList();
        if (where == null)
            Assert.Empty(found);
        else
            Assert.Contains(": " + where + ".", Assert.Single(found).Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- UA59

    private const string EmbeddedStream = "<< /Type /EmbeddedFile /Length 1 >>\nstream\nx\nendstream";

    [Theory]
    [InlineData("/F (a.txt) /UF (a.txt) /EF << /F 5 0 R >>", null)]
    [InlineData("/F (a.txt) /EF << /F 5 0 R >>", "(a.txt) has no /UF")]
    [InlineData("/UF (a.txt) /EF << /F 5 0 R >>", "(a.txt) has no /F")]
    [InlineData("/EF << /F 5 0 R >>", "(a.txt) has neither /F nor /UF")]
    [InlineData("/F (a.txt)", null)]                                                         // no /EF: no embedded file
    public void UA59_ANameTreeSpecification(string spec, string? expected)
    {
        byte[] pdf = Pdf(" /Names << /EmbeddedFiles << /Names [(a.txt) 4 0 R] >> >>", "", "",
            "<< /Type /Filespec " + spec + " >>", EmbeddedStream);
        var found = Coded(pdf, "UA59");
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 7.11 is no annotation rule, so a file attachment annotation a reader is not shown (hidden) is
    /// held to it as a shown one is — and a complete specification there is quiet.
    /// </summary>
    [Theory]
    [InlineData("/F 2", "/UF (a.txt) /EF << /F 5 0 R >>", true)]
    [InlineData("/F 4", "/UF (a.txt) /EF << /F 5 0 R >>", true)]
    [InlineData("/F 2", "/F (a.txt) /UF (a.txt) /EF << /F 5 0 R >>", false)]
    public void UA59_AFileAttachmentsSpecification_WhetherShownOrNot(string flags, string spec, bool reported)
    {
        byte[] pdf = Pdf("", " /Annots [6 0 R]", "",
            "<< /Type /Filespec " + spec + " >>", EmbeddedStream,
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 20 20] " + flags + " /Contents (File) /FS 4 0 R >>");
        var found = Coded(pdf, "UA59");
        Assert.Equal(reported ? 1 : 0, found.Count);
        if (reported)
            Assert.Contains("a /FileAttachment annotation's on page 1 has no /F", found[0].Message, StringComparison.Ordinal);
    }

    /// <summary>PDF/A-3's findings of <paramref name="code"/>, its violations and its partial-check notices alike.</summary>
    private static List<PdfAViolation> PdfA3(byte[] pdf, string code)
    {
        var result = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part3);
        return result.Violations.Concat(result.Informational).Where(v => v.Code == code).ToList();
    }

    /// <summary>
    /// A specification both named in the tree and attached is one specification, read once — by UA59 and
    /// by PDF/A's A67, which read the same walk — and named by its key.
    /// </summary>
    [Fact]
    public void UA59AndA67_ASpecificationNamedAndAttached_IsReadOnce()
    {
        byte[] pdf = Pdf(" /Names << /EmbeddedFiles << /Names [(a.txt) 4 0 R] >> >>", " /Annots [6 0 R]", "",
            "<< /Type /Filespec /F (a.txt) /AFRelationship /Data /EF << /F 5 0 R >> >>", EmbeddedStream,
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 20 20] /Contents (File) /FS 4 0 R >>");

        string ua = Assert.Single(Coded(pdf, "UA59")).Message;
        Assert.Contains("(a.txt) has no /UF", ua, StringComparison.Ordinal);
        Assert.DoesNotContain("/FileAttachment annotation's on page", ua, StringComparison.Ordinal);

        string a = Assert.Single(PdfA3(pdf, "A67")).Message;
        Assert.Contains("(a.txt) has no /UF", a, StringComparison.Ordinal);
        Assert.DoesNotContain("/FileAttachment annotation's on page", a, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name tree the walk cannot finish — a /Kids entry that does not parse — leaves a specification
    /// perhaps unread, and both readers say so: UA59 a UA99, A67 an A99. The entry it did read is judged.
    /// </summary>
    [Fact]
    public void UA59AndA67_ANameTreeReadInPart_IsSaid()
    {
        byte[] pdf = Pdf(" /Names << /EmbeddedFiles << /Kids [4 0 R 5 0 R] >> >>", "", "",
            "]",
            "<< /Names [(a.txt) 6 0 R] >>",
            "<< /Type /Filespec /F (a.txt) /EF << /F 7 0 R >> >>", EmbeddedStream);

        Assert.Contains(Coded(pdf, "UA99"), v => v.Message.StartsWith("UA59 (ISO 14289-1 7.11) did not read the whole /EmbeddedFiles name tree", StringComparison.Ordinal));
        Assert.Contains("(a.txt) has no /UF", Assert.Single(Coded(pdf, "UA59")).Message, StringComparison.Ordinal);
        Assert.Contains(PdfA3(pdf, "A99"), v => v.Message.Contains("did not read the whole /EmbeddedFiles name tree", StringComparison.Ordinal));

        byte[] whole = Pdf(" /Names << /EmbeddedFiles << /Kids [4 0 R] >> >>", "", "",
            "<< /Names [(a.txt) 5 0 R] >>",
            "<< /Type /Filespec /F (a.txt) /EF << /F 6 0 R >> >>", EmbeddedStream);
        Assert.DoesNotContain(Coded(whole, "UA99"), v => v.Message.StartsWith("UA59", StringComparison.Ordinal));
        Assert.DoesNotContain(PdfA3(whole, "A99"), v => v.Message.Contains("/EmbeddedFiles name tree", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------- UA60

    private static byte[] Xfa(string dynamicRender, string trailerExtra = "", string encryptObject = "")
    {
        string config = "<config xmlns=\"http://www.xfa.org/schema/xci/3.0/\"><acrobat><acrobat7>" + dynamicRender +
                        "</acrobat7></acrobat></config>";
        var extra = new List<string> { "<< /Fields [] /XFA [(config) 5 0 R] >>", Stream(config) };
        if (encryptObject.Length > 0) extra.Add(encryptObject);
        return Pdf(" /AcroForm 4 0 R", "", trailerExtra, extra.ToArray());
    }

    [Theory]
    [InlineData("<dynamicRender>required</dynamicRender>", 1)]
    [InlineData("<dynamicRender>forbidden</dynamicRender>", 0)]
    [InlineData("", 0)]
    public void UA60_ADynamicXfaForm_IsRefused_AStaticOneIsNot(string dynamicRender, int reported)
        => Assert.Equal(reported, Coded(Xfa(dynamicRender), "UA60").Count);

    /// <summary>A config packet that cannot be read is no verdict either way: a UA99 says UA60 was not verified.</summary>
    [Fact]
    public void UA60_AnUnreadableConfigPacket_IsAUa99NotAVerdict()
    {
        byte[] pdf = Xfa("<dynamicRender>required</acrobat7>");
        Assert.Empty(Coded(pdf, "UA60"));
        Assert.Contains(Coded(pdf, "UA99"), v => v.Message.StartsWith("UA60 (ISO 14289-1 7.15) could not be verified: " +
            "the XFA config packet could not be read", StringComparison.Ordinal));
        Assert.Single(Coded(Xfa("<dynamicRender>required</dynamicRender>"), "UA60"));
    }

    // -------------------------------------------------------------- UA61

    private const string CompleteClip = "<< /Type /MediaClip /S /MCD /CT (video/mp4) /Alt [() (A short clip)] /D << /F (a.mp4) >> >>";

    /// <summary>A Screen annotation's rendition action reaching <paramref name="clip"/> through <paramref name="rendition"/>.</summary>
    private static byte[] Media(string clip, string rendition = "<< /S /MR /C 7 0 R >>", string trailerExtra = "", string encryptObject = "")
    {
        var extra = new List<string>
        {
            "<< /Type /Annot /Subtype /Screen /Rect [10 10 50 50] /Contents (Clip) /A 5 0 R >>",
            "<< /S /Rendition /OP 0 /AN 4 0 R /R 6 0 R >>",
            rendition,
            clip,
        };
        if (encryptObject.Length > 0) extra.Add(encryptObject);
        return Pdf("", " /Annots [4 0 R]", trailerExtra, extra.ToArray());
    }

    [Theory]
    [InlineData(CompleteClip, null)]
    [InlineData("<< /Type /MediaClip /S /MCD /Alt [() (A short clip)] >>", "object 7 has no /CT")]
    [InlineData("<< /Type /MediaClip /S /MCD /CT () /Alt [() (A short clip)] >>", "object 7 has no /CT")]
    [InlineData("<< /Type /MediaClip /S /MCD /CT (video/mp4) >>", "object 7 has no /Alt that is a multi-language text array")]
    [InlineData("<< /Type /MediaClip /S /MCD /CT (video/mp4) /Alt [(A short clip)] >>", "object 7 has no /Alt that is a multi-language text array")]
    [InlineData("<< /Type /MediaClip /S /MCD /CT (video/mp4) /Alt [() /Clip] >>", "object 7 has no /Alt that is a multi-language text array")]
    [InlineData("<< /Type /MediaClip /S /MCD >>", "object 7 has neither /CT nor /Alt")]
    public void UA61_AMediaClipDataDictionary(string clip, string? expected)
    {
        var found = Coded(Media(clip), "UA61");
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence is about the dictionary, so a clip is judged wherever it is: one no action reaches,
    /// and one held directly inside another object, named by that object.
    /// </summary>
    [Fact]
    public void UA61_AClipNothingReaches_OrHeldInsideAnObject_IsJudged()
    {
        byte[] unreached = Pdf("", "", "", "<< /Type /MediaClip /S /MCD /CT (video/mp4) >>");
        Assert.Contains("object 4 has no /Alt", Assert.Single(Coded(unreached, "UA61")).Message, StringComparison.Ordinal);

        // The rendition (object 6) holds a clip of its own without /CT; the complete clip at 7 is quiet.
        byte[] held = Media(CompleteClip, "<< /S /MR /C << /Type /MediaClip /S /MCD /Alt [() (A clip)] >> >>");
        string message = Assert.Single(Coded(held, "UA61")).Message;
        Assert.Contains("a dictionary in object 6 has no /CT", message, StringComparison.Ordinal);
        Assert.DoesNotContain("object 7", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dictionaries are never ciphertext, so an encrypted document's clips are judged as a clear
    /// document's are, and no notice says otherwise.
    /// </summary>
    [Fact]
    public void UA61_OnAnEncryptedDocument_IsJudgedAsInTheClear()
    {
        const string bare = "<< /Type /MediaClip /S /MCD >>";
        byte[] encrypted = Media(bare, trailerExtra: " /Encrypt 8 0 R", encryptObject: StandardEncryptDict);
        Assert.Contains("object 7 has neither /CT nor /Alt", Assert.Single(Coded(encrypted, "UA61")).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Coded(encrypted, "UA99"), v => v.Message.StartsWith("UA61", StringComparison.Ordinal));
        Assert.Empty(Coded(Media(CompleteClip, trailerExtra: " /Encrypt 8 0 R", encryptObject: StandardEncryptDict), "UA61"));
    }

    /// <summary>
    /// An object that does not parse may be a clip, so the scan records it in the run's one
    /// unreadable-object notice — a UA99, not a verdict, and one notice however many checks meet it. The
    /// same file with the object readable carries no notice.
    /// </summary>
    [Fact]
    public void UA61_AnObjectThatDoesNotParse_JoinsTheRunsUnreadableNotice()
    {
        byte[] broken = Pdf("", "", "", "]");
        Assert.Contains("object(s) could not be read", Assert.Single(Coded(broken, "UA99")).Message, StringComparison.Ordinal);
        byte[] whole = Pdf("", "", "", "<< /Type /MediaClip /S /MCD /CT (video/mp4) /Alt [() (A clip)] >>");
        Assert.Empty(Coded(whole, "UA99"));
        Assert.Empty(Coded(whole, "UA61"));
    }

    /// <summary>
    /// The scan is bounded by its node ceiling: below it the scan stops and says so; under the production
    /// ceiling the same file is read whole.
    /// </summary>
    [Fact]
    public void UA61_TheScanStopsAtItsCeiling_HostileInput()
    {
        using var doc = Core.Document.PdfDocument.Open(new MemoryStream(Media("<< /Type /MediaClip /S /MCD >>")));
        var whole = PdfUaChecker.MediaClipData(doc, PdfUaChecker.MaxMediaClipScanNodes, out int unreadable, out bool cut);
        Assert.False(cut);
        Assert.Equal(0, unreadable);
        Assert.Equal("object 7", Assert.Single(whole).Where);

        var partial = PdfUaChecker.MediaClipData(doc, 3, out _, out cut);
        Assert.True(cut);
        Assert.Empty(partial);

        // The check forwards its ceiling to the scan, and a cut scan is said, not passed.
        var cutShort = new List<PdfUaViolation>();
        PdfUaChecker.CheckMediaClips(doc, cutShort, 3);
        Assert.Equal("UA99", Assert.Single(cutShort).Code);
        Assert.StartsWith("UA61 (ISO 14289-1 7.18.6.2) did not read the whole file — its scan stopped at 3 values",
            cutShort[0].Message, StringComparison.Ordinal);
        var whole2 = new List<PdfUaViolation>();
        PdfUaChecker.CheckMediaClips(doc, whole2);
        Assert.Equal("UA61", Assert.Single(whole2).Code);
    }
}

// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Metadata;

/// <summary>
/// <see cref="PdfIccEmbedder.WriteOutputIntent"/> — the one output-intent writer, which the
/// Conformance plugin's converters now go through instead of a copy of their own.
/// </summary>
public class OutputIntentWriterTests
{
    private static byte[] Profile(string dataColorSpace, string deviceClass = "prtr")
    {
        var profile = new byte[132];
        profile[3] = 132;
        profile[8] = 2;
        Encoding.ASCII.GetBytes(deviceClass).CopyTo(profile, 12);
        Encoding.ASCII.GetBytes(dataColorSpace).CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
        return profile;
    }

    /// <summary>
    /// One page; the catalog names <paramref name="catalogExtras"/>; objects 4 onward are
    /// <paramref name="extra"/>.
    /// </summary>
    private static byte[] Pdf(string catalogExtras = "", string trailerExtras = "", params string[] extra)
    {
        var builder = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R " + catalogExtras + " >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        for (int i = 0; i < extra.Length; i++) builder.Obj(4 + i, extra[i]);
        return builder.Finish(trailerExtras);
    }

    private static (OutputIntentWritten Written, PdfDocument Doc) Write(byte[] pdf, OutputIntentSpec spec)
    {
        using var output = new MemoryStream();
        OutputIntentWritten written = PdfIccEmbedder.WriteOutputIntent(new MemoryStream(pdf), output, spec);
        return (written, PdfDocument.Open(new MemoryStream(output.ToArray())));
    }

    private static List<PdfDictionary> Intents(PdfDocument doc)
        => ((PdfArray)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OutputIntents]))
            .Select(e => (PdfDictionary)doc.ResolveObject(e)).ToList();

    private static string Subtype(PdfDocument doc, PdfDictionary intent)
        => ((PdfName)doc.ResolveObject(intent[PdfName.S])).Value;

    /// <summary>
    /// A second intent of the same <c>/S</c> is what this library's own X11 (and, for PDF/A, A15)
    /// fails. The existing one is replaced; an intent of another subtype is kept beside it.
    /// </summary>
    [Fact]
    public void AnIntentOfTheSameSubtypeIsReplaced_AnotherSubtypeIsKept()
    {
        byte[] pdf = Pdf("/OutputIntents [4 0 R 5 0 R]", "",
            "<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (Old press) >>",
            "<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB) >>");

        var (written, doc) = Write(pdf, new OutputIntentSpec
        {
            Subtype = "GTS_PDFX", OutputConditionIdentifier = "FOGRA39", Profile = Profile("CMYK"),
        });
        using (doc)
        {
            Assert.Equal(1, written.ReplacedIntents);
            List<PdfDictionary> intents = Intents(doc);
            Assert.Equal(new[] { "GTS_PDFA1", "GTS_PDFX" }, intents.Select(i => Subtype(doc, i)).ToArray());
            Assert.Equal("FOGRA39", ((PdfString)intents[1][PdfName.OutputConditionIdentifier]).GetText());
        }
    }

    /// <summary>
    /// <c>/N</c> is the profile's own. The plugin's copy wrote its caller's, defaulting to 3 for
    /// PDF/A, so a CMYK profile's stream said 3 — the mismatch A20 fails.
    /// </summary>
    [Theory]
    [InlineData("CMYK", 4)]
    [InlineData("GRAY", 1)]
    [InlineData("RGB ", 3)]
    public void TheProfilesNIsItsOwnComponentCount(string dataColorSpace, int expected)
    {
        var (written, doc) = Write(Pdf(), new OutputIntentSpec { Profile = Profile(dataColorSpace) });
        using (doc)
        {
            Assert.True(written.EmbeddedProfile);
            Assert.Equal(expected, written.ProfileComponents);
            var profile = (PdfStream)doc.ResolveObject(Intents(doc).Single()[PdfName.DestOutputProfile]);
            Assert.Equal(expected, ((PdfNumber)profile[PdfName.N]).IntValue);
        }
    }

    /// <summary>
    /// An intent may name a registered characterisation without embedding one; the Base writer
    /// could not write that before, and the plugin's could.
    /// </summary>
    [Fact]
    public void AnIntentWithoutAProfileNamesNoDestinationProfile()
    {
        var (written, doc) = Write(Pdf(), new OutputIntentSpec { Subtype = "GTS_PDFX", OutputConditionIdentifier = "FOGRA39" });
        using (doc)
        {
            Assert.False(written.EmbeddedProfile);
            PdfDictionary intent = Intents(doc).Single();
            Assert.Null(intent[PdfName.DestOutputProfile]);
            Assert.Equal("FOGRA39", ((PdfString)intent[PdfName.OutputConditionIdentifier]).GetText());
        }
    }

    /// <summary>
    /// An existing <c>/Info</c> is superseded in place, and the trailer keeps naming it as the
    /// original did. At generation 1 that is the difference between <c>4 1 R</c> and a
    /// <c>4 0 R</c> naming nothing. The sweep found a rebuilt trailer indistinguishable at
    /// generation 0.
    /// </summary>
    [Fact]
    public void TrappedGoesIntoTheExistingInfo()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .ObjAtGeneration(4, 1, "<< /Title (Kept) >>")
            .Finish(" /Info 4 1 R");

        var (written, doc) = Write(pdf, new OutputIntentSpec { Profile = Profile("RGB "), Trapped = false });
        using (doc)
        {
            Assert.True(written.SetTrapped);
            var infoRef = (PdfReference)doc.Trailer[PdfName.Info];
            Assert.Equal((4, 1), (infoRef.ReferenceObjectNumber, infoRef.ReferenceGenerationNumber));
            var info = (PdfDictionary)doc.ResolveObject(doc.Trailer[PdfName.Info]);
            Assert.Equal("False", ((PdfName)info[PdfName.Trapped]).Value);
            Assert.Equal("Kept", ((PdfString)info[PdfName.Title]).GetText());
        }
    }

    /// <summary>A document with no <c>/Info</c> gets one, linked from the trailer.</summary>
    [Fact]
    public void TrappedCreatesAnInfoWhenThereIsNone()
    {
        var (written, doc) = Write(Pdf(), new OutputIntentSpec { Profile = Profile("RGB "), Trapped = true });
        using (doc)
        {
            Assert.True(written.SetTrapped);
            var info = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Trailer[PdfName.Info]));
            Assert.Equal("True", ((PdfName)info[PdfName.Trapped]).Value);
        }
    }

    /// <summary>
    /// With no <c>/Root</c> reference there is no catalog object to attach the intent to. The
    /// plugin's copy wrote its objects anyway, attached to nothing, and reported success.
    /// </summary>
    [Fact]
    public void ADirectRootIsRefused_AReferencedOneIsNot()
    {
        byte[] direct = new PdfFixtureBuilder().Header()
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .Finish(root: "<< /Type /Catalog /Pages 2 0 R >>");

        var ex = Assert.Throws<PdfException>(() =>
            PdfIccEmbedder.WriteOutputIntent(new MemoryStream(direct), new MemoryStream(),
                new OutputIntentSpec { Profile = Profile("RGB ") }));
        Assert.Contains("/Root is not an indirect reference", ex.Message, StringComparison.Ordinal);

        var (_, doc) = Write(Pdf(), new OutputIntentSpec { Profile = Profile("RGB ") });
        using (doc) Assert.Single(Intents(doc));
    }

    [Theory]
    [InlineData("", "Custom", "subtype")]
    [InlineData("GTS_PDFA1", "", "OutputConditionIdentifier")]
    public void AnIntentMissingARequiredEntryIsRefused(string subtype, string identifier, string named)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            PdfIccEmbedder.WriteOutputIntent(new MemoryStream(Pdf()), new MemoryStream(),
                new OutputIntentSpec { Subtype = subtype, OutputConditionIdentifier = identifier }));
        Assert.Contains(named, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// PDF/A and PDF/X both require the trailer's <c>/ID</c>: a file with none gets one derived from its
    /// bytes, the same on every run; a file with one keeps it.
    /// </summary>
    [Fact]
    public void TheRevisionCarriesTheFilesId_OrOneDerivedFromItsBytes()
    {
        var spec = new OutputIntentSpec { Subtype = "GTS_PDFA1", OutputConditionIdentifier = "sRGB", Profile = Profile("RGB ", "mntr") };
        byte[] bare = Pdf();

        var (_, first) = Write(bare, spec);
        var (_, second) = Write(bare, spec);
        using (first)
        using (second)
        {
            var id = Assert.IsType<PdfArray>(first.ResolveObject(first.Trailer[PdfName.ID]));
            Assert.Equal(2, id.Count);
            Assert.Equal(16, ((PdfString)id[0]).RawBytes.Length);
            Assert.Equal(((PdfString)id[0]).RawBytes, ((PdfString)((PdfArray)second.Trailer[PdfName.ID])[0]).RawBytes);
        }

        const string own = "/ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]";
        var (_, kept) = Write(Pdf(trailerExtras: own), spec);
        using (kept)
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
                ((PdfString)((PdfArray)kept.ResolveObject(kept.Trailer[PdfName.ID]))[0]).RawBytes);
    }
}

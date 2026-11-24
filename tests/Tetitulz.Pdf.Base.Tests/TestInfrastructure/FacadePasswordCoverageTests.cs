// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Every <see cref="PdfOperations"/> entry point that opens a document for <b>reading</b> must give
/// the caller a way to supply a password, or an encrypted document is unreadable through the facade.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Password support arrived one method at a time — eight of them, spread
/// across 46 <c>PdfOperations</c> partials — and nothing enumerated the rest. A reader added to a
/// new partial inherits the old behaviour (an encrypted document parses as an unreadable catalog and
/// answers <i>empty</i>, not <i>refused</i>) and fails nothing. The guard that was supposed to hold
/// the set complete was never written: <c>typeof(PdfOperations)</c> appeared nowhere under
/// <c>tests/</c> before this file.
/// </para>
/// <para>
/// <b>What counts as "a way to supply a password".</b> Two shapes are accepted, because the tree
/// genuinely uses both: a <c>string password</c> parameter on the method or on a sibling overload of
/// the same name taking the same kind of document input, and an options object carrying a
/// <c>string Password</c> property — which is how every rendering entry point does it
/// (<c>PdfRenderOptions.Password</c>). Anything else is a hole.
/// </para>
/// <para>
/// <b>Both lists below are self-cleaning, the way <see cref="ApiBaselineCoverageTests"/>'
/// burn-down is.</b> An entry that has since gained a password path fails
/// <see cref="Neither_list_has_a_stale_entry"/>, and an entry naming a method that no longer exists
/// fails <see cref="Every_listed_method_still_exists"/>. So the lists can only shrink, and a
/// "deliberate exclusion" cannot quietly become a description of a method that has moved on.
/// <b>Do not add to <see cref="KnownWithoutPassword"/>.</b> A new reader ships with a password
/// parameter or it does not ship.
/// </para>
/// </remarks>
public class FacadePasswordCoverageTests
{
    /// <summary>
    /// Readers that must <b>never</b> take a password, each with the reason. These are not a
    /// backlog — adding a password to one of them would be a defect, and the test that keeps this
    /// list honest fails if one ever gains a password path.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberateExclusions = new(StringComparer.Ordinal)
    {
        ["ValidateConformance(Stream input, String conformanceLevel)"] =
            "Obsolete, and superseded by Validate(Stream, string, string) which HAS a password " +
            "route. Adding an overload to a method already marked [Obsolete] and slated for " +
            "removal grows the surface that has to be deleted later, and gives a caller a new " +
            "reason to keep using it. The password path exists on the survivor.",

        ["GetRevisions(Stream input)"] =
            "Byte-level. A revision boundary is a %%EOF/startxref offset into the file as it lies " +
            "on disk, and that chain is not encrypted. Decrypting first re-serialises the document " +
            "and gives it entirely different offsets, turning a working operation into a broken " +
            "one — the rule stated on PdfOperations.WithDecrypted.",

        ["GetRevisionsWithSignatures(Stream input)"] =
            "Byte-level boundaries with object-graph attribution. It reports the encrypted case " +
            "through SignaturesExamined rather than decrypting, because decrypting would move " +
            "every offset it just measured.",

        ["InspectUnencryptedWrapper(Stream input)"] =
            "The subject IS the unencrypted wrapper (§7.6.7). It reads the outer, cleartext " +
            "document to report that an encrypted payload is attached; decrypting the wrapper is " +
            "not a thing that can happen, and the payload is an attachment rather than this " +
            "document.",

        ["OpenWithRepair(Stream input)"] =
            "Returns an OPEN PdfDocument that reads lazily from the stream it was handed. " +
            "WithDecrypted disposes the decrypted buffer when it returns, so a password overload " +
            "here would hand back a document backed by a dead stream. A caller needing this on an " +
            "encrypted file decrypts with Base.Encryption.PdfDecryptedInput.OpenDecrypted first.",

        ["InspectIncrementalSave(Stream input)"] =
            "Byte-level, and the same rule as GetRevisions. It reports where each incremental " +
            "update begins as an offset into the file as it lies on disk. Decrypting first " +
            "re-serialises the document and moves every offset it is about to report, so a " +
            "password parameter would turn a correct answer into a confidently wrong one.",

        ["InspectIncrementalSave(String inputPath)"] =
            "Path sibling of the stream overload; the same byte-offset reasoning applies.",

        ["VerifyMdp(Stream input)"] =
            "Byte-level. A DocMDP verdict is a digest over a /ByteRange of the file AS SIGNED. " +
            "Decrypting produces different bytes, so the digest would not match and every " +
            "document would report as modified. The encrypted case belongs to " +
            "Signing.PdfSignatureVerifier, which holds the file key handling. It REFUSES an " +
            "encrypted document rather than reporting one: with no readable catalog the " +
            "AcroForm walk found no signature fields and it answered 'No DocMDP certification " +
            "signature found' about a document that may well be certified.",

        ["VerifyMdp(String pdfPath)"] =
            "Path sibling of the stream overload; the same /ByteRange reasoning applies, and " +
            "it refuses through the same guard on the Verify(Stream) it delegates to.",

        ["ComparePdfText(Stream left, Stream right)"] =
            "Two documents, so one password is the wrong shape -- the two can have different " +
            "passwords, or one can be encrypted and the other not. Adding `password` would " +
            "silently apply it to both. A caller with encrypted input decrypts each side " +
            "through Base.Encryption.PdfDecryptedInput and compares the results, which is " +
            "two lines and unambiguous. It REFUSES an encrypted side rather than comparing " +
            "what it cannot read: declining the parameter was always right, but answering " +
            "anyway reported two different encrypted documents as identical, because both " +
            "measured zero pages. See EncryptedVerdictRefusalTests.",

        ["ComparePdfText(String leftPath, String rightPath)"] =
            "Path sibling; the same two-documents-one-password objection applies, and it " +
            "refuses through the same guard on the Stream overload it delegates to.",

        ["ComparePdfVisual(Stream left, Stream right, Int32 dpi, Single thresholdPct, Int32 pixelTolerance)"] =
            "Two documents (see ComparePdfText), and it renders -- so an encrypted pair is " +
            "served by PdfRenderOptions.Password on each side rather than by a second " +
            "password channel here. It REFUSES an encrypted side: rendering one produces no " +
            "pages at all, and no pages against no pages compared EQUAL.",

    };

    /// <summary>
    /// Readers that have no password path today. <b>This list may only shrink.</b>
    /// </summary>
    /// <remarks>
    /// <para>Each entry is an entry point that answers <i>empty</i> rather than <i>refused</i> for an
    /// encrypted document and gives the caller no way to fix that. The permanent exclusions above are
    /// not on it.</para>
    /// <para><b>Only the <c>Stream</c> shapes are real work.</b> A file-path overload comes off this
    /// list for free the moment its <c>Stream</c> sibling is converted — see
    /// <see cref="HasPasswordPath"/> — so the ones listed here with a <c>String</c> first parameter
    /// are either waiting on their sibling or, in the case of <c>GetPageCount</c> and
    /// <c>GetVersion</c>, have no <c>Stream</c> sibling at all and need one.</para>
    /// <para><b>Four groups in this list need a decision before a sweep, not just typing:</b></para>
    /// <list type="bullet">
    ///   <item><c>InspectIncrementalSave</c> and <c>VerifyMdp</c> are <b>hybrids</b> — byte offsets
    ///     <i>and</i> object graph. Decrypting fixes the graph half and invalidates the offsets, so
    ///     they need a shape (decrypt only the AcroForm walk / report that half unavailable / leave
    ///     them), not a parameter.</item>
    ///   <item><c>ComparePdfText</c> and <c>ComparePdfVisual</c> take <b>two</b> documents, so one
    ///     <c>password</c> parameter is the wrong signature.</item>
    ///   <item><c>Validate</c> / <c>ValidateConformance</c> hand the document to a plugin, and a
    ///     conformance verdict on a decrypted re-serialisation is a verdict about a file the caller
    ///     does not have.</item>
    ///   <item><c>RecognizeTextWithOcr</c>, <c>ExtractImages</c> and <c>ReadPageThumbnail</c> reach
    ///     the rendering stack, which already carries <c>PdfRenderOptions.Password</c>; the question
    ///     is which of the two mechanisms wins rather than whether one is missing.</item>
    /// </list>
    /// </remarks>
    private static readonly string[] KnownWithoutPassword =
    {
        // EMPTY, and that is the finished state rather than an oversight. Every read-only facade
        // entry point either takes a password or is on DeliberateExclusions with the reason it
        // cannot. The list stays so the guard below keeps working: a NEW reader that ships without
        // a password path fails the build naming itself, and the only way to silence it is to add
        // the overload or to justify a decline. Do not delete it, and do not add to it -- the
        // second guard fails on any entry that is not genuinely missing one.
    
    };

    [Fact]
    public void Every_read_only_facade_entry_point_accepts_a_password()
    {
        var missing = ReadersWithoutPassword()
            .Where(k => !KnownWithoutPassword.Contains(k, StringComparer.Ordinal))
            .Where(k => !DeliberateExclusions.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These PdfOperations entry points open a document for reading but give the caller no " +
            "way to pass a password, so an encrypted document answers empty instead of being " +
            "readable:\n  " + string.Join("\n  ", missing) +
            "\n\nAdd a `string password = null` parameter routed through WithDecrypted (or a " +
            "Password-carrying options object) rather than adding the name to KnownWithoutPassword " +
            "— that list may only shrink.");
    }

    /// <summary>
    /// A name on either list that has since gained a password path must come off it, or the list
    /// stops describing the facade and starts excusing it.
    /// </summary>
    [Fact]
    public void Neither_list_has_a_stale_entry()
    {
        var stillMissing = ReadersWithoutPassword().ToHashSet(StringComparer.Ordinal);

        var staleBurnDown = KnownWithoutPassword.Where(k => !stillMissing.Contains(k)).ToList();
        Assert.True(staleBurnDown.Count == 0,
            "These now accept a password and must be deleted from KnownWithoutPassword:\n  " +
            string.Join("\n  ", staleBurnDown.OrderBy(k => k, StringComparer.Ordinal)));

        var staleExclusions = DeliberateExclusions.Keys.Where(k => !stillMissing.Contains(k)).ToList();
        Assert.True(staleExclusions.Count == 0,
            "These are listed as deliberately password-free, but they now HAVE a password path, so " +
            "the documented reason is no longer true of the code. Delete the entry (and check the " +
            "reason was not a real constraint someone has just broken):\n  " +
            string.Join("\n  ", staleExclusions.OrderBy(k => k, StringComparer.Ordinal)));

        // "Deliberately never" and "not done yet" are opposite claims; a method on both lists would
        // be excused twice and burned down never.
        var onBoth = KnownWithoutPassword.Where(DeliberateExclusions.ContainsKey).ToList();
        Assert.True(onBoth.Count == 0,
            "These are on the burn-down AND listed as a deliberate exclusion, which are contradictory " +
            "claims. Pick one:\n  " + string.Join("\n  ", onBoth.OrderBy(k => k, StringComparer.Ordinal)));
    }

    /// <summary>
    /// An entry naming a method that no longer exists — renamed, re-signatured, deleted — silently
    /// stops excluding anything, and the guard would never say so.
    /// </summary>
    [Fact]
    public void Every_listed_method_still_exists()
    {
        var actual = FacadeMethods().Select(Key).ToHashSet(StringComparer.Ordinal);

        var ghosts = DeliberateExclusions.Keys.Concat(KnownWithoutPassword)
            .Where(k => !actual.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(ghosts.Count == 0,
            "These are listed in FacadePasswordCoverageTests but no PdfOperations method has that " +
            "signature any more, so the entry excludes nothing:\n  " + string.Join("\n  ", ghosts));
    }

    /// <summary>
    /// Entries in <see cref="NonDocumentFirstParameters"/> claim "this leading Stream/string is not
    /// a PDF". A stale claim removes a real reader from the demand set, which is the one failure
    /// this guard could not otherwise notice.
    /// </summary>
    [Fact]
    public void Every_non_document_input_claim_still_matches_a_method()
    {
        var actual = FacadeMethods()
            .Where(m => m.GetParameters().Length > 0)
            .Select(m => m.Name + "(" + m.GetParameters()[0].Name + ")")
            .ToHashSet(StringComparer.Ordinal);

        var ghosts = NonDocumentFirstParameters.Where(k => !actual.Contains(k))
                                               .OrderBy(k => k, StringComparer.Ordinal)
                                               .ToList();

        Assert.True(ghosts.Count == 0,
            "These NonDocumentFirstParameters entries match no PdfOperations method, so they are " +
            "excusing nothing:\n  " + string.Join("\n  ", ghosts));
    }

    /// <summary>
    /// A floor for the guard itself. Every test above passes trivially if the reflection scan finds
    /// no readers at all — banned pattern 3 wearing a reflection call — so pin that the classifier
    /// really sees the facade, really finds the eight methods that already take a password, and
    /// really rejects the writers.
    /// </summary>
    [Fact]
    public void The_classifier_is_reading_the_real_facade()
    {
        var all = FacadeMethods().ToList();
        Assert.True(all.Count >= 150, $"only {all.Count} public static PdfOperations methods found");

        var readers = Readers().Select(Key).ToHashSet(StringComparer.Ordinal);

        // Known reads, one per category the classifier has to get right.
        Assert.Contains("ExtractLinks(Stream input, String password)", readers);      // already converted
        Assert.Contains("GetRevisions(Stream input)", readers);                       // plain single-input read
        Assert.Contains("RenderPageToPng(Stream input, Int32 pageIndex, Int32 dpi, PdfRenderOptions renderOptions)",
                        readers);                                                     // password on the options object
        Assert.Contains("ExtractText(String pdfPath)", readers);                      // file-path shape

        // Writers are not reads: an encrypted write goes through Encryption.PdfEncryptedRoundTrip.
        Assert.DoesNotContain("FlattenForm(Stream input, Stream output)", readers);
        Assert.DoesNotContain("RotatePage(Stream input, Stream output, Int32 pageIndex, Int32 degrees)", readers);

        // Non-PDF leading parameters are not document inputs.
        Assert.DoesNotContain("ShapeText(String text)", readers);
        Assert.DoesNotContain("ConvertSvgToPdf(Stream svgStream, SvgConversionOptions options)", readers);

        // And the password detector distinguishes the two halves.
        var withPassword = Readers().Where(HasPasswordPath).Select(Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("GetBookmarks(Stream input, String password)", withPassword);
        // Cancellation on the SVG path is a SEPARATE, WIDER OVERLOAD, not a trailing optional
        // parameter — appending one to a shipped signature is a binary break, and these four were
        // widened in place before that was caught. Both halves of the pair carry a password (through
        // PdfRenderOptions.Password), so the classifier must find both; pinning only one would go
        // green again if the narrow overload were dropped a second time.
        //
        // Spelled out rather than trimmed to a prefix match, because the whole point of these pins
        // is that the classifier is reading the REAL signature — which is exactly why this assertion
        // reddened when the overloads were restored.
        Assert.Contains(
            "RenderPageToSvg(Stream input, Int32 pageIndex, PdfRenderOptions options)", withPassword);
        Assert.Contains(
            "RenderPageToSvg(Stream input, Int32 pageIndex, CancellationToken cancellationToken, "
          + "PdfRenderOptions options)", withPassword);
        Assert.Contains("ExtractLinks(String inputPath)", withPassword);   // via its Stream sibling
        // Deliberately password-free, permanently (see DeliberateExclusions), so this stays
        // falsifiable no matter how far the burn-down gets.
        Assert.DoesNotContain("GetRevisions(Stream input)", withPassword);
    }

    // ---------------------------------------------------------------- classification

    /// <summary>
    /// <c>Method(firstParameterName)</c> for every entry point whose leading <see cref="Stream"/> or
    /// <see cref="string"/> is <b>not</b> a PDF document, so no password could apply to it.
    /// </summary>
    private static readonly string[] NonDocumentFirstParameters =
    {
        "ConvertSvgToPdf(svgStream)",            // SVG source
        "ConvertSvgToPdf(svgPath)",              // SVG source
        "Import(source)",                        // the plugin's foreign format, not a PDF
        "ExecuteJavaScript(script)",             // JavaScript text
        "ShapeText(text)",                       // text to shape
        "GenerateBarcodePdf(data)",              // barcode payload
        "GenerateBarcodeContentStream(data)",    // barcode payload
        "TryResolveNamedColor(name)",            // colour name
    };

    /// <summary>Parameter names that make a method a document <b>writer</b>.</summary>
    /// <remarks>
    /// <c>outputDirectory</c> is deliberately absent: <c>ExtractImages(Stream, string
    /// outputDirectory)</c> writes image files but <i>reads</i> the PDF, so it needs a password like
    /// any other reader.
    /// </remarks>
    private static readonly string[] DocumentOutputParameters = { "output", "outputPath", "pdfOutput" };

    /// <summary>
    /// Writers by the rule above that in fact emit something other than a PDF, so they are reads of
    /// the input document after all.
    /// </summary>
    private static readonly string[] WritersThatEmitSomethingElse =
    {
        "RenderPageToStream",   // writes a rendered image to `output`; the PDF is read-only input
        "Export",               // writes the plugin's foreign format; the PDF is read-only input
    };

    private static IEnumerable<MethodInfo> FacadeMethods() =>
        typeof(PdfOperations)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);   // drop property getters

    private static IEnumerable<MethodInfo> Readers() => FacadeMethods().Where(IsDocumentRead);

    private static IEnumerable<string> ReadersWithoutPassword() =>
        Readers().Where(m => !HasPasswordPath(m)).Select(Key);

    private static bool IsDocumentRead(MethodInfo m)
    {
        var ps = m.GetParameters();
        if (ps.Length == 0) return false;

        Type first = ps[0].ParameterType;
        if (first != typeof(Stream) && first != typeof(string)) return false;

        if (NonDocumentFirstParameters.Contains(m.Name + "(" + ps[0].Name + ")", StringComparer.Ordinal))
            return false;

        bool writesADocument = ps.Skip(1).Any(p =>
            (p.ParameterType == typeof(Stream) || p.ParameterType == typeof(string)) &&
            DocumentOutputParameters.Contains(p.Name, StringComparer.Ordinal));

        if (writesADocument && !WritersThatEmitSomethingElse.Contains(m.Name, StringComparer.Ordinal))
            return false;

        return true;
    }

    private static bool HasPasswordPath(MethodInfo m)
    {
        if (TakesPassword(m)) return true;

        // An options object carrying a password — how every rendering entry point does it.
        if (m.GetParameters().Any(p => CarriesPassword(p.ParameterType))) return true;

        // A sibling overload of the same name that DOES take one. A file-path overload's whole body
        // is `using var fs = File.OpenRead(path); return <the Stream overload>(fs, …)`, so a
        // password-taking Stream sibling really is a way in for a caller holding a path — it is the
        // wrapper that is missing, not the capability. The converse is not true, which is why the
        // sibling must be Stream-shaped in BOTH directions: a path-only password overload would
        // leave a caller holding a Stream with nowhere to go.
        return FacadeMethods().Any(o =>
            !ReferenceEquals(o, m) &&
            string.Equals(o.Name, m.Name, StringComparison.Ordinal) &&
            o.GetParameters().Length > 0 &&
            o.GetParameters()[0].ParameterType == typeof(Stream) &&
            IsDocumentRead(o) &&
            (TakesPassword(o) || o.GetParameters().Any(p => CarriesPassword(p.ParameterType))));
    }

    private static bool TakesPassword(MethodInfo m) =>
        m.GetParameters().Any(p => p.ParameterType == typeof(string) &&
                                   string.Equals(p.Name, "password", StringComparison.Ordinal));

    private static bool CarriesPassword(Type t) =>
        t.GetProperty("Password", BindingFlags.Public | BindingFlags.Instance)?.PropertyType == typeof(string);

    private static string Key(MethodInfo m) =>
        m.Name + "(" + string.Join(", ",
            m.GetParameters().Select(p => Simple(p.ParameterType) + " " + p.Name)) + ")";

    private static string Simple(Type t)
    {
        if (t.IsByRef) return "out " + Simple(t.GetElementType()!);
        if (!t.IsGenericType) return t.Name;
        string bare = t.Name.Substring(0, t.Name.IndexOf('`'));
        return bare + "<" + string.Join(", ", t.GetGenericArguments().Select(Simple)) + ">";
    }
}

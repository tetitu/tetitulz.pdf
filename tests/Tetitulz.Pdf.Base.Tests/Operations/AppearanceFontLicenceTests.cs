// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The <c>OS/2</c> <c>fsType</c> gate, asserted through the two <b>callers</b> that draw text with
/// a substituted host font: AcroForm appearance generation and signature appearance generation.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists, and why the resolver's own licence tests were not enough.</b>
/// <c>SystemFontLicenceGateTests</c> has covered <c>SystemFontResolver</c>'s gate since it shipped,
/// and it was green the whole time these two paths were bypassing the resolver entirely: they ran
/// their own search through <c>Base.Operations.Forms.FormFontSource</c>, which never read
/// <c>fsType</c> at all. So <c>AllowSystemFontSubstitution</c> embedded host fonts whose own licence
/// bits forbid embedding, and every test of the gate passed while the gate was not on the path.
/// <b>Covering a function is not covering the decision that uses it</b> — the same finding the
/// 2026-08-29 liveness sweep recorded, reached from the other direction.</para>
///
/// <para>Host-independent by construction: <c>TETITULZ_PDF_FONTDIR</c> <i>replaces</i> the OS search
/// path, so the only font either path can find is the one each test writes. That matters more here
/// than usual — an installed Hebrew font on the machine would satisfy "an appearance was drawn" for
/// entirely the wrong reason, and the restricted case would then fail on a host that has one and
/// pass on a host that does not.</para>
/// </remarks>
[Collection(SystemFontCacheCollection.Name)]
public sealed class AppearanceFontLicenceTests : IDisposable
{
    /// <summary>Hebrew, so the value cannot be drawn by the field's WinAnsi Helvetica.</summary>
    private const string Hebrew = "שלום";

    private const int Installable = 0x0000;
    private const int RestrictedLicence = 0x0002;

    private readonly string _dir;
    private readonly string _previousEnv;

    public AppearanceFontLicenceTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(FontFileLocator.FontDirEnvVar);
        _dir = Path.Combine(Path.GetTempPath(), "tetitulz-aplicence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _previousEnv);
        FontFileLocator.ResetCache();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void GivenOnlyHostFont(int fsType)
    {
        File.WriteAllBytes(Path.Combine(_dir, "host.ttf"),
                           MinimalTrueType.BuildWithFsType(Hebrew, fsType));
        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _dir);
        FontFileLocator.ResetCache();
    }

    // ------------------------------------------------------------------
    //  AcroForm appearance generation
    // ------------------------------------------------------------------

    /// <summary>
    /// The claim: filling a field with text only a restricted host font can draw produces <b>no</b>
    /// appearance, rather than an appearance built from a font whose licence forbids embedding.
    /// </summary>
    [Fact]
    public void FormFilling_DoesNotEmbedARestrictedHostFont()
    {
        GivenOnlyHostFont(RestrictedLicence);

        var result = Fill();

        Assert.Null(AppearanceOf(result.Bytes));
        Assert.Contains(result.Warnings,
            w => w.Contains("no font on this machine covers", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control, without which the test above passes for any reason at all — including the
    /// fixture font being unreadable, or the substitution path never running. The <b>same font</b>
    /// with an installable licence <i>is</i> embedded and the appearance <i>is</i> drawn.
    /// </summary>
    [Fact]
    public void FormFilling_EmbedsAnInstallableHostFont()
    {
        GivenOnlyHostFont(Installable);

        var result = Fill();

        Assert.Equal(Hebrew.Length, DrawnGlyphCount(result.Bytes));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("no font on this machine covers", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Coverage decides which host font wins, not which one the scan reaches first.</b> The
    /// decoy covers one letter of the value; the other font covers all four.
    /// </summary>
    /// <remarks>
    /// <para>Written because a mutation survived: replacing the request's <c>RequiredCodePoints</c>
    /// with <c>null</c> left the whole Base assembly green. It is <i>nearly</i> an equivalent
    /// mutation — <c>TextRunComposer</c> runs with <c>MissingGlyphPolicy.Refuse</c>, so a font
    /// missing a glyph still ends in a refusal further down — and that near-equivalence is exactly
    /// what hid it. What the fallback cannot recover is the <b>choice</b>: with nothing required,
    /// the first parsable font on the host wins, which on a real machine means the right glyphs in
    /// the wrong typeface where a covering font was available two files later. That is the failure
    /// <c>SystemFontResolver</c>'s own step-3b comment describes.</para>
    ///
    /// <para><b>Both arrangements are run, and that is what makes the assertion mean anything.</b>
    /// With the coverage requirement deleted the winner is whichever font the scan reaches first,
    /// and <c>FontFileLocator.EnumerateFontFiles</c> takes <c>Directory.EnumerateFiles</c>' order
    /// — which is the filesystem's, not sorted. As a single case naming the decoy <c>aaa-</c> this
    /// killed the mutation on NTFS (name-ordered) and would have passed it on a filesystem that
    /// returns creation or inode order. Writing the decoy first in <i>both</i> cases and letting it
    /// sort first in one removes the dependency: whichever of the two orders a host uses, one of
    /// these cases has the decoy in front of the covering font.</para>
    /// </remarks>
    [Theory]
    [InlineData("aaa-partial.ttf", "zzz-complete.ttf")]   // decoy first by name, and written first
    [InlineData("zzz-partial.ttf", "aaa-complete.ttf")]   // decoy last by name, still written first
    public void FormFilling_PrefersTheHostFontThatCoversTheWholeValue(string decoy, string complete)
    {
        File.WriteAllBytes(Path.Combine(_dir, decoy),
                           MinimalTrueType.BuildWithFsType(Hebrew.Substring(0, 1), Installable));
        File.WriteAllBytes(Path.Combine(_dir, complete),
                           MinimalTrueType.BuildWithFsType(Hebrew, Installable));
        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _dir);
        FontFileLocator.ResetCache();

        var result = Fill();

        Assert.Equal(Hebrew.Length, DrawnGlyphCount(result.Bytes));
    }

    /// <summary>
    /// <b>The opt-in the refusal message names is reachable, and works.</b> With
    /// <c>AllowRestrictedSystemFonts</c> set, the same restricted host font that was skipped above
    /// is used and the appearance is drawn.
    /// </summary>
    /// <remarks>
    /// <para>Until 2026-09-04 the failure text that reached <c>FormFillResult.Warnings</c> ended
    /// "set <c>FontResolutionRequest.AllowRestrictedSystemFonts</c>…" — naming a property that
    /// existed on the resolver's request and on <b>neither</b> options type a caller of this path
    /// can construct. <b>A remedy the caller cannot act on is worse than no remedy</b>: it reads as
    /// a supported opt-in and sends them looking for an API that is not there.</para>
    ///
    /// <para>This is the pair to <c>FormFilling_DoesNotEmbedARestrictedHostFont</c> above. Without
    /// it "the gate refuses" is satisfied by a gate that is a wall, and a deployment that owns the
    /// fonts it installed has no way past.</para>
    /// </remarks>
    [Fact]
    public void FormFilling_AllowRestrictedSystemFonts_ReachesTheGate()
    {
        GivenOnlyHostFont(RestrictedLicence);

        var result = Fill(new FormFillOptions
        {
            AllowSystemFontSubstitution = true,
            AllowRestrictedSystemFonts = true,
        });

        Assert.Equal(Hebrew.Length, DrawnGlyphCount(result.Bytes));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("no font on this machine covers", StringComparison.Ordinal));
    }

    /// <summary>
    /// The opt-out reaches this path too. A deployment that owns the fonts it installed says so
    /// once, and the same restricted font is then used — otherwise the gate would be a wall.
    /// </summary>
    [Fact]
    public void FormFilling_HonoursTheCallersOwnFontWhateverItsLicenceSays()
    {
        GivenOnlyHostFont(RestrictedLicence);
        byte[] supplied = MinimalTrueType.BuildWithFsType(Hebrew, RestrictedLicence);

        var result = Fill(new FormFillOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => supplied,
        });

        // Not an oversight: `AllowRestrictedSystemFonts` applies to the SYSTEM search only. A
        // caller handing over a font program has made a licensing decision this library cannot see
        // and may hold a licence the bits know nothing about; overruling it would be this library
        // overruling its own user.
        //
        // Counting the glyphs rather than testing the stream for null, because an appearance that
        // exists and draws nothing is what a *refusal* would leave behind if the refusal were
        // moved one step later — which is precisely the mistake this asserts against.
        Assert.Equal(Hebrew.Length, DrawnGlyphCount(result.Bytes));

        // The acceptance control for the three substitution reports below. Without it each of them
        // could be satisfied by a warning this path emits unconditionally, and "the caller's font
        // was used" would be reported as a substitution.
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("not the one supplied", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    //  Falling through from the caller's own font to a host one
    // ------------------------------------------------------------------

    /// <summary>
    /// A supplied font that covers only part of the value is replaced by a host font — and the
    /// caller is <b>told</b>, naming the file that was embedded instead.
    /// </summary>
    /// <remarks>
    /// <para><b>The substitution itself is correct and stays.</b> That is what
    /// <c>AllowSystemFontSubstitution</c> is for, and refusing here would break a caller who
    /// supplies a brand font and allows substitution as a safety net for scripts it lacks. What
    /// was wrong is that it was silent: <c>SystemFontResolver</c> coverage-tests the provider's
    /// font like any other candidate and falls through with nothing said, so a caller who supplied
    /// a font <i>because they hold its licence</i> shipped a different one under the same call,
    /// with the only trace being <c>FontResolution.FromSystem</c> — which this path did not read.
    /// Before the resolver was on this path at all, the provider's font was used unconditionally,
    /// so nothing was ever substituted behind the caller's back.</para>
    ///
    /// <para>Both halves are asserted, because either alone passes for the wrong reason: the glyph
    /// count says a covering font really was embedded (a bare warning would also fit a refusal),
    /// and the warning says the caller can find out (four glyphs alone would also fit the silent
    /// fall-through this exists to end).</para>
    /// </remarks>
    [Fact]
    public void FormFilling_ReportsAHostFontEmbeddedInPlaceOfTheSuppliedOne()
    {
        GivenOnlyHostFont(Installable);
        byte[] partial = MinimalTrueType.BuildWithFsType(Hebrew.Substring(0, 1), Installable);

        var result = Fill(new FormFillOptions
        {
            AllowSystemFontSubstitution = true,
            FontProvider = _ => partial,
        });

        Assert.Equal(Hebrew.Length, DrawnGlyphCount(result.Bytes));
        Assert.Contains(result.Warnings,
            w => w.Contains("cannot draw every character of the value", StringComparison.Ordinal) &&
                 w.Contains("not the one supplied", StringComparison.Ordinal) &&
                 w.Contains("host.ttf", StringComparison.Ordinal));
    }

    /// <summary>
    /// A provider that <b>threw</b> is reported as having thrown, naming the exception — not as one
    /// that declined.
    /// </summary>
    /// <remarks>
    /// The two are different problems with different remedies (fix the provider; supply a font for
    /// this family), and the resolver swallows the exception, so without this the caller cannot
    /// tell them apart at all. Paired with the test below, which asserts the other sentence for the
    /// other cause — a single test naming only one of them would pass against a message that said
    /// the same thing in both cases.
    /// </remarks>
    [Fact]
    public void FormFilling_ReportsAFontProviderThatThrew_NamingTheException()
    {
        GivenOnlyHostFont(Installable);

        var result = Fill(new FormFillOptions
        {
            AllowSystemFontSubstitution = true,
            FontProvider = _ => throw new InvalidOperationException("provider is broken"),
        });

        Assert.Equal(Hebrew.Length, DrawnGlyphCount(result.Bytes));
        Assert.Contains(result.Warnings,
            w => w.Contains("threw InvalidOperationException: provider is broken",
                            StringComparison.Ordinal));
    }

    /// <summary>
    /// A provider that returned nothing is reported as having returned nothing, and is <b>not</b>
    /// described as having thrown.
    /// </summary>
    [Fact]
    public void FormFilling_ReportsAFontProviderThatReturnedNothing_WithoutClaimingItThrew()
    {
        GivenOnlyHostFont(Installable);

        var result = Fill(new FormFillOptions
        {
            AllowSystemFontSubstitution = true,
            FontProvider = _ => null,
        });

        Assert.Equal(Hebrew.Length, DrawnGlyphCount(result.Bytes));
        Assert.Contains(result.Warnings,
            w => w.Contains("returned no font for", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("threw", StringComparison.Ordinal));
    }

    /// <summary>
    /// With substitution off, the refusal names the provider's exception rather than reporting it
    /// as a provider that had nothing to give.
    /// </summary>
    /// <remarks>
    /// <para>This is the only reachable assertion on <c>SystemFontResolver</c>'s provider
    /// diagnostic. It used to be written into <c>_lastResolutionError</c> mid-call, and every exit
    /// from <c>Resolve</c> either cleared that slot (success) or overwrote it (failure), so no
    /// caller could ever read it through <c>LastResolutionError</c> — a diagnostic that was
    /// recorded and never reportable for its whole life. It is folded into the failure reason now,
    /// which is where a caller acts on it, and the failure reason is what this path appends to its
    /// own message.</para>
    ///
    /// <para>The assertion is on the <b>reason</b>, not on the refusal: three separate guards
    /// produce "no appearance was drawn" here, so a test asserting only that would pass with the
    /// provider's exception discarded again.</para>
    /// </remarks>
    [Fact]
    public void FormFilling_RefusalNamesTheProviderException_WhenSubstitutionIsOff()
    {
        GivenOnlyHostFont(Installable);

        var result = Fill(new FormFillOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => throw new InvalidOperationException("provider is broken"),
        });

        Assert.Null(AppearanceOf(result.Bytes));
        Assert.Contains(result.Warnings,
            w => w.Contains("the font provider threw InvalidOperationException: provider is broken",
                            StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    //  Signature appearance generation
    // ------------------------------------------------------------------

    /// <summary>Same gate, same reason, on the signature block.</summary>
    [Fact]
    public void SignatureAppearance_DoesNotEmbedARestrictedHostFont()
    {
        GivenOnlyHostFont(RestrictedLicence);
        int next = 10;

        var (ap, _) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions
            {
                SignerName = Hebrew,
                AllowSystemFontSubstitution = true,
            },
            ref next, out string error);

        Assert.Null(ap);
        Assert.Contains("no font on this machine covers", error, StringComparison.Ordinal);
        Assert.Equal(10, next);   // a refusal burns no object numbers
    }

    /// <summary>The control, for the same reason as the form one.</summary>
    [Fact]
    public void SignatureAppearance_EmbedsAnInstallableHostFont()
    {
        GivenOnlyHostFont(Installable);
        int next = 10;

        var (ap, side) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions
            {
                SignerName = Hebrew,
                AllowSystemFontSubstitution = true,
            },
            ref next, out string error);

        Assert.Null(error);
        Assert.Equal(Hebrew.Length, GlyphsShownIn(Encoding.Latin1.GetString(ap.GetDecodedData())));

        // …and the program really travelled with it. A composite appearance referencing a font
        // nobody embedded renders as nothing in every viewer, and the content stream above cannot
        // tell the two apart.
        Assert.Contains(side, o => o.obj is PdfDictionary d &&
                                   (d.ContainsKey(PdfName.FontFile2) || d.ContainsKey(PdfName.FontFile3)));
    }

    // ------------------------------------------------------------------

    private FillOutcome Fill(FormFillOptions options = null)
    {
        using var input = new MemoryStream(FormPdfFixtures.TextField());
        using var output = new MemoryStream();

        var result = PdfFormFiller.Fill(input, output,
            new Dictionary<string, string> { ["name"] = Hebrew },
            options ?? new FormFillOptions { AllowSystemFontSubstitution = true });

        return new FillOutcome(output.ToArray(), result.Warnings);
    }

    private sealed class FillOutcome
    {
        internal FillOutcome(byte[] bytes, IReadOnlyList<string> warnings)
        {
            Bytes = bytes;
            Warnings = warnings;
        }

        internal byte[] Bytes { get; }
        internal IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>
    /// The filled field's <c>/AP /N</c> stream, or null when none was written.
    /// </summary>
    /// <remarks>
    /// Reads the appearance rather than counting warnings, because the two answer different
    /// questions: a warning says the code noticed, an absent <c>/AP</c> says it did not embed. A
    /// path that reported the licence and drew anyway would satisfy the warning assertion alone.
    /// </remarks>
    private static PdfStream AppearanceOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var field = doc.GetObject(FormPdfFixtures.FirstFieldObj) as PdfDictionary;
        var ap = doc.ResolveObject(field?[PdfName.AP]) as PdfDictionary;
        return doc.ResolveObject(ap?[PdfName.N]) as PdfStream;
    }

    /// <summary>
    /// How many glyphs the field's appearance actually shows — 0 when there is no appearance.
    /// </summary>
    /// <remarks>
    /// <b>The count, not the stream's existence.</b> A form XObject that exists and paints nothing
    /// is a perfectly valid appearance, so "an <c>/AP /N</c> was written" passes with the drawing
    /// deleted; it is banned pattern 3, and the guard flagged this file for exactly that before it
    /// was written this way. Four glyphs for four Hebrew characters also pins the composite path
    /// specifically: the WinAnsi path would emit a literal string, not a hex glyph run.
    /// </remarks>
    private static int DrawnGlyphCount(byte[] pdf)
    {
        var stream = AppearanceOf(pdf);
        return stream == null ? 0 : GlyphsShownIn(Encoding.Latin1.GetString(stream.GetDecodedData()));
    }

    /// <summary>
    /// Glyphs in the first <c>&lt;…&gt; Tj</c> of a content stream. Composite text is shown as a
    /// hex string of 4-digit glyph ids, so the glyph count is its length over four.
    /// </summary>
    private static int GlyphsShownIn(string content)
    {
        int open = content.IndexOf('<');
        int close = open < 0 ? -1 : content.IndexOf('>', open + 1);
        if (open < 0 || close < 0) return 0;

        int digits = close - open - 1;
        return digits % 4 == 0 ? digits / 4 : 0;
    }
}

// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Where the managed engine gets a font for text the standard-14 WinAnsi faces cannot
    /// encode: <c>@font-face</c>, then <see cref="HtmlToPdfOptions.FontProvider"/>, then the
    /// host — and what it says when none of them can supply one.
    /// </summary>
    public class HtmlFontResolutionTests
    {
        private const string Shalom = "שלום";

        private static HtmlToPdfOptions NoHostFonts()
        {
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            return o;
        }

        // ------------------------------------------------------------------ @font-face

        [Fact]
        public void FontFace_WithALocalUrlInsideBasePath_IsEmbedded()
        {
            using var dir = NewScratchDir();
            dir.Write("hebrew.ttf", MinimalTrueType.Build(Shalom));

            var o = NoHostFonts();
            o.BasePath = dir.Path;

            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';src:url(hebrew.ttf) format('truetype')}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o));

            Assert.Contains("/Type0", raw);
            Assert.Contains("/FontFile2", raw);
        }

        [Fact]
        public void FontFace_WithADataUri_IsEmbedded()
        {
            string b64 = Convert.ToBase64String(MinimalTrueType.Build(Shalom));
            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';src:url(data:font/ttf;base64," + b64 + ")}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", NoHostFonts()));

            Assert.Contains("/Type0", raw);
            Assert.Contains("/FontFile2", raw);
        }

        [Fact]
        public void FontFace_WithARemoteUrl_IsNeverFetched_AndIsReported()
        {
            var seen = new List<BuilderDiagnostic>();
            var o = NoHostFonts();
            o.OnDiagnostic = seen.Add;

            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';src:url(https://fonts.example.com/h.ttf)}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o));

            Assert.DoesNotContain("/FontFile2", raw);
            Assert.Contains(seen, d => d.Detail.Contains("never fetches", StringComparison.Ordinal));
        }

        [Fact]
        public void FontFace_OutsideBasePath_IsRefused()
        {
            using var outer = NewScratchDir();
            outer.Write("secret.ttf", MinimalTrueType.Build(Shalom));
            string inner = Path.Combine(outer.Path, "sub");
            Directory.CreateDirectory(inner);

            var o = NoHostFonts();
            o.BasePath = inner;

            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';src:url(../secret.ttf)}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o));

            Assert.DoesNotContain("/FontFile2", raw);
        }

        /// <summary>
        /// A declared <c>format('woff2')</c> is not a reason to decline anything.
        /// </summary>
        /// <remarks>
        /// <para><b>The engine reads the container's signature, not the stylesheet's claim.</b>
        /// WOFF 2.0 is unwrapped by <c>Core.Fonts.Woff2Decoder</c> at <c>TrueTypeFontProgram.Parse</c>,
        /// so no <c>format()</c> test gates it — the weaker test, since <c>format()</c> is optional in CSS
        /// and stylesheets routinely omit it.</para>
        ///
        /// <para><b>The fixture is a TrueType file behind a <c>format('woff2')</c> declaration</b>,
        /// which is a stylesheet lying about its own asset. That is the case that isolates a decline
        /// by declared format: nothing but the declared format could refuse it, so the font loading
        /// is the assertion.</para>
        /// </remarks>
        [Fact]
        public void FontFace_DeclaringWoff2_NoLongerDeclinesOnTheDeclaredFormatAlone()
        {
            using var dir = NewScratchDir();
            dir.Write("hebrew.bin", MinimalTrueType.Build(Shalom));

            var seen = new List<BuilderDiagnostic>();
            var o = NoHostFonts();
            o.BasePath = dir.Path;
            o.OnDiagnostic = seen.Add;

            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';src:url(hebrew.bin) format('woff2')}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o));

            Assert.Contains("/FontFile2", raw);
            Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.FontFormatRejected);
        }

        /// <summary>
        /// The sibling the rename demands: WOFF 1.0 is <b>accepted</b>, unwrapped by
        /// <c>Core.Fonts.WoffDecoder</c> on the way into <c>TrueTypeFontProgram</c>.
        ///
        /// <para>Asserting both <c>/FontFile2</c> and drawn glyph IDs is what makes this fail if
        /// the unwrap produces a differently-shaped sfnt: a font whose recomputed directory is one
        /// entry wrong still embeds, and the page then draws nothing.</para>
        /// </summary>
        [Fact]
        public void FontFace_DeclaringWoff1_IsUnwrappedAndEmbedded()
        {
            byte[] sfnt = MinimalTrueType.Build(Shalom);
            using var dir = NewScratchDir();
            dir.Write("hebrew.woff", MinimalWoff.Wrap(sfnt));

            var o = NoHostFonts();
            o.BasePath = dir.Path;

            byte[] pdf = ToPdf(
                "<style>@font-face{font-family:'MyHebrew';src:url(hebrew.woff) format('woff')}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o);

            Assert.Contains("/FontFile2", Raw(pdf));

            // Every glyph drawn must map back to a Hebrew code point through the WRAPPED font's
            // own cmap. "A font was embedded" is not the claim — an unwrap that mangled the
            // directory still embeds something, and the page then draws .notdef or nothing.
            var gidToCodePoint = MinimalTrueType.ReadCmap(sfnt).ToDictionary(kv => kv.Value, kv => kv.Key);
            var drawn = DrawnGlyphRuns(DecodeStreams(pdf));
            Assert.Equal(Shalom.Length, drawn.Sum(run => run.Gids.Length));
            foreach (var run in drawn)
                foreach (ushort gid in run.Gids)
                {
                    Assert.True(gidToCodePoint.TryGetValue(gid, out int cp), $"glyph {gid} is not from the WOFF font");
                    Assert.InRange(cp, 0x0590, 0x05FF);
                }
        }

        /// <summary>
        /// The bytes decide, not the declaration: <c>format()</c> is optional in CSS and
        /// stylesheets routinely omit it, so a <c>.woff</c> with no format at all must still be
        /// unwrapped.
        /// </summary>
        [Fact]
        public void FontFace_Woff1WithNoDeclaredFormat_IsStillUnwrapped()
        {
            byte[] sfnt = MinimalTrueType.Build(Shalom);
            using var dir = NewScratchDir();
            dir.Write("hebrew.woff", MinimalWoff.Wrap(sfnt));

            var o = NoHostFonts();
            o.BasePath = dir.Path;

            byte[] pdf = ToPdf("<style>@font-face{font-family:'MyHebrew';src:url(hebrew.woff)}" +
                               "p{font-family:'MyHebrew'}</style>" +
                               $"<p>{Shalom}</p>", o);

            var gidToCodePoint = MinimalTrueType.ReadCmap(sfnt).ToDictionary(kv => kv.Value, kv => kv.Key);
            var drawn = DrawnGlyphRuns(DecodeStreams(pdf));
            Assert.Equal(Shalom.Length, drawn.Sum(run => run.Gids.Length));
            foreach (var run in drawn)
                foreach (ushort gid in run.Gids)
                    Assert.True(gidToCodePoint.ContainsKey(gid), $"glyph {gid} is not from the WOFF font");
        }

        /// <summary>
        /// A WOFF 1.0 payload inside a <c>data:</c> URI takes a different branch of the loader
        /// than a file does, and the two are one <c>return</c> apart.
        /// </summary>
        [Fact]
        public void FontFace_Woff1InADataUri_IsUnwrappedAndEmbedded()
        {
            byte[] sfnt = MinimalTrueType.Build(Shalom);
            string b64 = Convert.ToBase64String(MinimalWoff.Wrap(sfnt));

            byte[] pdf = ToPdf(
                "<style>@font-face{font-family:'MyHebrew';src:url(data:font/woff;base64," + b64 + ")}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", NoHostFonts());

            Assert.Contains("/FontFile2", Raw(pdf));

            var gidToCodePoint = MinimalTrueType.ReadCmap(sfnt).ToDictionary(kv => kv.Value, kv => kv.Key);
            var drawn = DrawnGlyphRuns(DecodeStreams(pdf));
            Assert.Equal(Shalom.Length, drawn.Sum(run => run.Gids.Length));
            foreach (var run in drawn)
                foreach (ushort gid in run.Gids)
                    Assert.True(gidToCodePoint.ContainsKey(gid), $"glyph {gid} is not from the WOFF font");
        }

        /// <summary>
        /// A payload that <i>claims</i> to be WOFF 2.0 and is not is still declined, and nothing is
        /// embedded.
        /// </summary>
        /// <remarks>
        /// <para>The <c>wOF2</c> signature routes the bytes to the decoder, which refuses them because
        /// they are not a WOFF 2.0 container. What the caller sees is a diagnostic, and no
        /// <c>/FontFile2</c>. A malformed web font that
        /// is silently ignored is indistinguishable from one that loaded — the caller sees a
        /// fallback face with nothing to explain it.</para>
        ///
        /// <para>The fixture is a TrueType file with the <c>wOF2</c> signature stamped over its
        /// sfnt version, so the bytes are exactly as hostile as the name says: a real container
        /// header over content that is not one.</para>
        /// </remarks>
        [Fact]
        public void FontFace_MalformedWoff2Payload_IsDeclinedAndNothingIsEmbedded()
        {
            using var dir = NewScratchDir();
            dir.Write("hebrew.bin", MinimalWoff.Woff2Signature(MinimalTrueType.Build(Shalom)));

            var seen = new List<BuilderDiagnostic>();
            var o = NoHostFonts();
            o.BasePath = dir.Path;
            o.OnDiagnostic = seen.Add;

            string raw = Raw(ToPdf("<style>@font-face{font-family:'MyHebrew';src:url(hebrew.bin)}" +
                                   "p{font-family:'MyHebrew'}</style>" +
                                   $"<p>{Shalom}</p>", o));

            Assert.DoesNotContain("/FontFile2", raw);
            Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.FontFormatRejected);
        }

        [Fact]
        public void FontFace_FallsThroughToTheNextSrcEntry()
        {
            using var dir = NewScratchDir();
            dir.Write("hebrew.ttf", MinimalTrueType.Build(Shalom));

            var o = NoHostFonts();
            o.BasePath = dir.Path;

            // The first entry names a file that does not exist, so it is declined and the
            // truetype entry after it must still be used. Its .woff2 spelling does not matter:
            // the missing file is the reason, and the fall-through is the same either way.
            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';" +
                "src:url(h.woff2) format('woff2'), url(hebrew.ttf) format('truetype')}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o));

            Assert.Contains("/FontFile2", raw);
        }

        /// <summary>
        /// A <c>src</c> entry that reads but is not a font this engine parses — an EOT, which
        /// generated <c>@font-face</c> lists put first — is passed over for the next one, as a
        /// browser passes over a format it does not support.
        /// </summary>
        /// <remarks>
        /// Taken because it can be <i>read</i>, it would hide the TTF after it, and every Bootstrap 3
        /// icon would fall back to a host font.
        /// </remarks>
        [Fact]
        public void FontFace_PassesOverASourceThatIsNotAFont()
        {
            using var dir = NewScratchDir();
            dir.Write("hebrew.eot", System.Text.Encoding.ASCII.GetBytes("this is an EOT, which is not an sfnt"));
            dir.Write("hebrew.ttf", MinimalTrueType.Build(Shalom));

            var seen = new List<BuilderDiagnostic>();
            var o = NoHostFonts();
            o.BasePath = dir.Path;
            o.OnDiagnostic = seen.Add;

            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';" +
                "src:url(hebrew.eot) format('embedded-opentype'), url(hebrew.ttf) format('truetype')}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o));

            Assert.Contains("/FontFile2", raw);
            // An unsupported format followed by one that loads is the list working as intended.
            Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.FontFormatRejected);
        }

        /// <summary>
        /// The same for a <c>data:</c> source: decoding it is not enough either, the bytes must be a
        /// font this engine parses.
        /// </summary>
        [Fact]
        public void FontFace_PassesOverADataUriThatIsNotAFont()
        {
            using var dir = NewScratchDir();
            dir.Write("hebrew.ttf", MinimalTrueType.Build(Shalom));
            string notAFont = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes("this is an EOT, which is not an sfnt"));

            var o = NoHostFonts();
            o.BasePath = dir.Path;

            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';" +
                $"src:url(data:application/vnd.ms-fontobject;base64,{notAFont}), url(hebrew.ttf)}}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o));

            Assert.Contains("/FontFile2", raw);
        }

        /// <summary>
        /// The control for the tests above: a list whose only entry is not a font embeds nothing and
        /// says so — and names the entry.
        /// </summary>
        [Fact]
        public void FontFace_WhoseOnlySourceIsNotAFont_IsReportedAndNothingIsEmbedded()
        {
            using var dir = NewScratchDir();
            dir.Write("hebrew.eot", System.Text.Encoding.ASCII.GetBytes("this is an EOT, which is not an sfnt"));

            var seen = new List<BuilderDiagnostic>();
            var o = NoHostFonts();
            o.BasePath = dir.Path;
            o.OnDiagnostic = seen.Add;

            string raw = Raw(ToPdf(
                "<style>@font-face{font-family:'MyHebrew';src:url(hebrew.eot)}" +
                "p{font-family:'MyHebrew'}</style>" +
                $"<p>{Shalom}</p>", o));

            Assert.DoesNotContain("/FontFile2", raw);
            Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.FontFormatRejected
                                       && d.Detail.Contains("hebrew.eot", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ linked stylesheets

        /// <summary>
        /// A relative <c>url()</c> in a linked stylesheet means a path next to the STYLESHEET, not
        /// next to the document — Bootstrap's <c>css/</c> + <c>fonts/</c> layout.
        /// </summary>
        /// <remarks>
        /// Only remote sheets were rebased onto their own URL; a local one's <c>../fonts/x.ttf</c>
        /// was resolved against <c>BasePath</c>, landed outside it, and was refused.
        /// </remarks>
        [Fact]
        public void FontFace_InALinkedStylesheet_ResolvesAgainstTheStylesheetsFolder()
        {
            using var dir = NewScratchDir();
            dir.Write(Path.Combine("fonts", "hebrew.ttf"), MinimalTrueType.Build(Shalom));
            dir.Write(Path.Combine("css", "site.css"),
                "@font-face{font-family:'MyHebrew';src:url('../fonts/hebrew.ttf')}" +
                "p{font-family:'MyHebrew'}");

            var o = NoHostFonts();
            o.BasePath = dir.Path;

            string raw = Raw(ToPdf(
                "<link rel=\"stylesheet\" href=\"css/site.css\">" + $"<p>{Shalom}</p>", o));

            Assert.Contains("/FontFile2", raw);
        }

        /// <summary>
        /// The same, with the <c>?#iefix</c> suffix generated lists carry. <c>System.Uri</c> escapes a
        /// <c>file:</c> URI's <c>?</c> and <c>#</c> into its path, so the rebased reference named
        /// <c>hebrew.ttf%3F%23iefix</c>, which does not exist.
        /// </summary>
        [Fact]
        public void FontFace_InALinkedStylesheet_KeepsAQueryOrFragmentOutOfThePath()
        {
            using var dir = NewScratchDir();
            dir.Write(Path.Combine("fonts", "hebrew.ttf"), MinimalTrueType.Build(Shalom));
            dir.Write(Path.Combine("css", "site.css"),
                "@font-face{font-family:'MyHebrew';src:url('../fonts/hebrew.ttf?#iefix') format('truetype')}" +
                "p{font-family:'MyHebrew'}");

            var o = NoHostFonts();
            o.BasePath = dir.Path;

            string raw = Raw(ToPdf(
                "<link rel=\"stylesheet\" href=\"css/site.css\">" + $"<p>{Shalom}</p>", o));

            Assert.Contains("/FontFile2", raw);
        }

        /// <summary>
        /// Rebasing does not move the trust boundary: a linked stylesheet naming a font above the
        /// base directory is refused, exactly as the document naming it would be.
        /// </summary>
        /// <remarks>
        /// The potency half is the file itself: it exists, it is a font, and the same sheet naming
        /// it from inside the base embeds it — so a refusal here is the containment check and not a
        /// missing or unreadable file.
        /// </remarks>
        [Fact]
        public void FontFace_InALinkedStylesheet_OutsideBasePath_IsRefused()
        {
            using var outer = NewScratchDir();
            outer.Write("secret.ttf", MinimalTrueType.Build(Shalom));
            string inner = Path.Combine(outer.Path, "site");
            outer.Write(Path.Combine("site", "fonts", "secret.ttf"), MinimalTrueType.Build(Shalom));
            outer.Write(Path.Combine("site", "css", "escape.css"),
                "@font-face{font-family:'MyHebrew';src:url('../../secret.ttf')}p{font-family:'MyHebrew'}");
            outer.Write(Path.Combine("site", "css", "inside.css"),
                "@font-face{font-family:'MyHebrew';src:url('../fonts/secret.ttf')}p{font-family:'MyHebrew'}");

            HtmlToPdfOptions Options()
            {
                var o = NoHostFonts();
                o.BasePath = inner;
                return o;
            }

            string escaped = Raw(ToPdf("<link rel=\"stylesheet\" href=\"css/escape.css\">" + $"<p>{Shalom}</p>", Options()));
            string contained = Raw(ToPdf("<link rel=\"stylesheet\" href=\"css/inside.css\">" + $"<p>{Shalom}</p>", Options()));

            Assert.DoesNotContain("/FontFile2", escaped);
            Assert.Contains("/FontFile2", contained);
        }

        // ------------------------------------------------------------------ options provider

        [Fact]
        public void OptionsFontProvider_SuppliesTheFont()
        {
            var o = NoHostFonts();
            o.FontProvider = _ => MinimalTrueType.Build(Shalom);

            string raw = Raw(ToPdf($"<p>{Shalom}</p>", o));

            Assert.Contains("/Type0", raw);
            Assert.Contains("/FontFile2", raw);
        }

        [Fact]
        public void OptionsFontProvider_ReceivesTheRequestedFamilyName()
        {
            // font-family does not collapse to a sans/serif/mono bucket: the name a document asks
            // for reaches something that can act on it.
            var families = new List<string>();
            var o = NoHostFonts();
            o.FontProvider = req => { families.Add(req.FamilyName); return MinimalTrueType.Build(Shalom); };

            ToPdf($"<p style=\"font-family:David\">{Shalom}</p>", o);

            Assert.Contains("David", families);
        }

        [Fact]
        public void FontFace_TakesPrecedenceOverTheOptionsProvider()
        {
            using var dir = NewScratchDir();
            dir.Write("hebrew.ttf", MinimalTrueType.Build(Shalom));

            bool providerAsked = false;
            var o = NoHostFonts();
            o.BasePath = dir.Path;
            o.FontProvider = _ => { providerAsked = true; return MinimalTrueType.Build(Shalom); };

            ToPdf("<style>@font-face{font-family:'MyHebrew';src:url(hebrew.ttf)}" +
                  "p{font-family:'MyHebrew'}</style>" +
                  $"<p>{Shalom}</p>", o);

            Assert.False(providerAsked, "@font-face should have satisfied the request first");
        }

        [Fact]
        public void GenericFamilyNames_DoNotBecomeAFontRequest()
        {
            // `font-family: sans-serif` names no real face; asking a provider for "sans-serif"
            // would be meaningless and would mask the standard-14 bucket that is the right answer.
            var families = new List<string>();
            var o = NoHostFonts();
            o.FontProvider = req => { families.Add(req.FamilyName); return MinimalTrueType.Build(Shalom); };

            ToPdf($"<p style=\"font-family:sans-serif\">{Shalom}</p>", o);

            Assert.DoesNotContain("sans-serif", families);
        }

        // ------------------------------------------------------------------ honest failure

        [Fact]
        public void NoFontAnywhere_IsReportedRatherThanSilentlyWrittenAsQuestionMarks()
        {
            var seen = new List<BuilderDiagnostic>();
            var o = NoHostFonts();
            o.OnDiagnostic = seen.Add;

            ToPdf($"<p>{Shalom}</p>", o);

            var d = seen.SingleOrDefault(x => x.Kind == BuilderDiagnosticKind.FontNotResolved);
            Assert.NotNull(d);
            Assert.Contains("U+05", d.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void UnrenderableTextPolicyThrow_PropagatesFromTheConverter()
        {
            var o = NoHostFonts();
            o.UnrenderableText = UnrenderableTextPolicy.Throw;

            Assert.Throws<UnrenderableTextException>(() => ToPdf($"<p>{Shalom}</p>", o));
        }
    }
}

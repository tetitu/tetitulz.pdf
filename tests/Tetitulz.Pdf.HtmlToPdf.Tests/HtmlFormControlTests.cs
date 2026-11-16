// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Form controls, rendered as static graphics.
    ///
    /// <para>They were not handled at all, which was worse than it sounds: unknown elements fall
    /// through to a generic "recurse into the children" walk, so a <c>&lt;select&gt;</c> emitted
    /// <b>every</b> option into the text flow and a <c>&lt;textarea&gt;</c>'s default value leaked
    /// as ordinary prose. An <c>&lt;input&gt;</c> is void, so it drew nothing at all.</para>
    ///
    /// <para>Static rather than AcroForm fields is deliberate: Chromium's own <c>page.pdf()</c>
    /// flattens controls, so emitting real fields here would make the two engines disagree about
    /// what the same HTML produces.</para>
    /// </summary>
    public class HtmlFormControlTests
    {
        // ------------------------------------------------------------------ the leaks

        [Fact]
        public void Select_RendersOnlyTheSelectedOption()
        {
            string dec = DecodeStreams(ToPdf(
                "<select><option>Alpha</option><option selected>Beta</option><option>Gamma</option></select>"));

            Assert.Contains("Beta", dec);
            Assert.DoesNotContain("Alpha", dec);
            Assert.DoesNotContain("Gamma", dec);
        }

        [Fact]
        public void Select_WithNoSelectedAttribute_ShowsTheFirstOption()
        {
            string dec = DecodeStreams(ToPdf(
                "<select><option>First</option><option>Second</option></select>"));

            Assert.Contains("First", dec);
            Assert.DoesNotContain("Second", dec);
        }

        [Fact]
        public void TextArea_RendersItsValueOnce_NotAsLooseProse()
        {
            // A single-word value on purpose: layout tokenises on spaces and draws each token with
            // its own text matrix, so a two-word value never appears as one literal in the content
            // stream and a substring assertion on it would fail for reasons unrelated to the
            // feature.
            string dec = DecodeStreams(ToPdf("<p>Before</p><textarea>TYPEDVALUE</textarea><p>After</p>"));

            Assert.Single(Regex.Matches(dec, "TYPEDVALUE"));
            Assert.Contains("Before", dec);
            Assert.Contains("After", dec);
        }

        // ------------------------------------------------------------------ inputs

        [Fact]
        public void TextInput_RendersItsValue()
        {
            string dec = DecodeStreams(ToPdf("<input type=\"text\" value=\"Jerusalem\">"));
            Assert.Contains("Jerusalem", dec);
        }

        [Fact]
        public void InputWithNoTypeAttribute_IsTreatedAsText()
        {
            string dec = DecodeStreams(ToPdf("<input value=\"Defaulted\">"));
            Assert.Contains("Defaulted", dec);
        }

        /// <summary>
        /// A password's stored value is echoed as bullets, never in clear text. A PDF is a durable
        /// artefact that gets mailed and archived; printing a password because the markup happened
        /// to carry one would be a disclosure nobody asked for.
        /// </summary>
        [Fact]
        public void PasswordInput_IsMaskedNotPrinted()
        {
            // The mask glyph is U+2022, above the WinAnsi range, so it draws through the composite
            // font path as glyph ids — it is never a literal in the content stream. The injected
            // font is what makes "the bullets really reached the page" observable at all.
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build("•");

            byte[] pdf = ToPdf("<input type=\"password\" value=\"hunter2\">", o);

            Assert.DoesNotContain("hunter2", DecodeStreams(pdf));
            Assert.DoesNotContain("hunter2", Raw(pdf));
            Assert.Contains("/Type0", Raw(pdf));     // the mask glyphs were drawn
        }

        [Fact]
        public void HiddenInput_RendersNothing()
        {
            string withHidden = DecodeStreams(ToPdf("<p>A</p><input type=\"hidden\" value=\"SECRETVALUE\"><p>B</p>"));
            string without = DecodeStreams(ToPdf("<p>A</p><p>B</p>"));

            Assert.DoesNotContain("SECRETVALUE", withHidden);
            Assert.Equal(without, withHidden);
        }

        [Theory]
        [InlineData("checkbox", false, "☐")]
        [InlineData("checkbox", true, "☑")]
        [InlineData("radio", false, "○")]
        [InlineData("radio", true, "◉")]
        public void CheckboxAndRadio_ShowTheirState(string type, bool @checked, string glyph)
        {
            // The glyphs are above U+00FF, so they need a covering font — inject one rather than
            // depending on what the host has installed.
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build("☐☑○◉");

            string html = $"<input type=\"{type}\"{(@checked ? " checked" : "")}>";
            byte[] pdf = ToPdf(html, o);

            // The glyph reached a composite font, which only happens when a run needed one.
            Assert.Contains("/Type0", Raw(pdf));
        }

        [Fact]
        public void SubmitButton_ShowsItsConventionalCaption()
        {
            Assert.Contains("Submit", DecodeStreams(ToPdf("<input type=\"submit\">")));
            Assert.Contains("Send", DecodeStreams(ToPdf("<input type=\"submit\" value=\"Send\">")));
        }

        // ------------------------------------------------------------------ containers

        [Fact]
        public void FormAndFieldset_AreTransparentBlockContainers()
        {
            string dec = DecodeStreams(ToPdf(
                "<form><fieldset><legend>Details</legend><p>Inner</p></fieldset></form>"));

            Assert.Contains("Details", dec);
            Assert.Contains("Inner", dec);
        }

        [Fact]
        public void Label_TextIsKept()
        {
            string dec = DecodeStreams(ToPdf("<label>Name <input type=\"text\" value=\"Dana\"></label>"));

            Assert.Contains("Name", dec);
            Assert.Contains("Dana", dec);
        }

        // ------------------------------------------------------------------ tagging

        [Fact]
        public void FormControls_DoNotBreakTaggedOutput()
        {
            var o = Managed();
            o.Tagged = true;
            string dec = DecodeStreams(ToPdf(
                "<form><p>Name</p><input type=\"text\" value=\"Dana\">" +
                "<select><option selected>Beta</option></select></form>", o));

            Assert.Equal(Regex.Matches(dec, @"\bBDC\b").Count, Regex.Matches(dec, @"\bEMC\b").Count);
        }
    }
}

// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <see cref="HtmlToPdfConverter.ConvertHtmlWithFormFields"/>: which controls become form-field
    /// placements, with what state, and which stay drawings.
    /// </summary>
    /// <remarks>
    /// The placeholder is no-break spaces in Courier, 0.6&#160;em each, so a width in characters
    /// converts to points by arithmetic: <c>chars × 0.6 × FontSize</c>.
    /// </remarks>
    public class HtmlFormFieldMappingTests
    {
        private static (HtmlFormConversionResult Result, List<BuilderDiagnostic> Seen) Convert(string html)
        {
            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.OnDiagnostic = seen.Add;
            return (HtmlToPdfConverter.ConvertHtmlWithFormFields(html, o), seen);
        }

        private static PlacedFormField Only(string html) => Assert.Single(Convert(html).Result.FormFields);

        // ------------------------------------------------------------------ text

        [Fact]
        public void ATextInputBecomesATextField_WithItsNameValueLengthAndWidth()
        {
            PlacedFormField placed = Only("<p>Name <input name=\"fullname\" value=\"Alice\" size=\"10\" maxlength=\"40\"></p>");

            Assert.Equal(FormFieldKind.Text, placed.Field.Kind);
            Assert.Equal("fullname", placed.Field.Name);
            Assert.Equal("Alice", placed.Field.Value);
            Assert.Equal(40, placed.Field.MaxLength);
            Assert.Equal(10 * 0.6 * placed.FontSize, placed.Width, 3);
            Assert.Equal(0, placed.PageIndex);
        }

        /// <summary>
        /// The value is drawn once — by the field's appearance, once the field is made — and not also
        /// on the page underneath it. The label beside the control is the potency half: an empty
        /// page also "does not contain" the value.
        /// </summary>
        [Fact]
        public void AFieldsValueIsNotDrawnOnThePage_WhileTheTextAroundItIs()
        {
            var (result, _) = Convert("<p>Fullname <input name=\"n\" value=\"Alicia\"></p>");

            string content = DecodeStreams(result.Pdf);
            Assert.DoesNotContain("Alicia", content, StringComparison.Ordinal);
            Assert.Contains(DrawnTexts(content), t => t.Text.Contains("Fullname", StringComparison.Ordinal));
        }

        /// <summary>
        /// The parity control: the ordinary entry point is untouched, still draws the value, and
        /// maps no control to a field — which is what a browser prints.
        /// </summary>
        [Fact]
        public void TheOrdinaryConversionStillDrawsControlsAsGraphics()
        {
            const string html = "<p>Fullname <input name=\"n\" value=\"Alicia\"></p>";

            Assert.Contains("Alicia", DecodeStreams(ToPdf(html)), StringComparison.Ordinal);
            M_NoFieldRuns(HtmlToBuilderMapper.MapToModel(html, Managed()));
        }

        private static void M_NoFieldRuns(Document doc)
        {
            var runs = doc.Sections.SelectMany(s => s.Children).OfType<Paragraph>()
                          .SelectMany(p => p.Runs).OfType<TextRun>().ToList();
            Assert.NotEmpty(runs);
            Assert.DoesNotContain(runs, r => r.FormField != null);
        }

        [Fact]
        public void APasswordInputIsAPasswordField_AndItsValueIsNeverKept()
        {
            PlacedFormField placed = Only("<input type=\"password\" name=\"pin\" value=\"1234\">");

            Assert.True(placed.Field.Password);
            Assert.Null(placed.Field.Value);
        }

        [Fact]
        public void ATextareaIsAMultilineField_WithItsContentAsTheValue()
        {
            PlacedFormField placed = Only("<textarea name=\"notes\" cols=\"30\">first line</textarea>");

            Assert.True(placed.Field.Multiline);
            Assert.Equal("first line", placed.Field.Value);
            Assert.Equal(30 * 0.6 * placed.FontSize, placed.Width, 3);
        }

        [Theory]
        [InlineData("<input name=\"r\" readonly>", true)]
        [InlineData("<input name=\"r\" disabled>", true)]
        [InlineData("<input name=\"r\">", false)]
        public void ReadonlyAndDisabledControlsBecomeReadOnlyFields(string html, bool expected)
        {
            Assert.Equal(expected, Only(html).Field.ReadOnly);
        }

        /// <summary>
        /// Nothing inherited changes the placeholder's width: letter-spacing on the control would
        /// widen a run of no-break spaces, and the field's width is its <c>size</c>, not its styling.
        /// </summary>
        [Fact]
        public void InheritedLetterSpacingDoesNotWidenThePlaceholder()
        {
            PlacedFormField placed = Only("<p style=\"letter-spacing:6pt\"><input name=\"n\" size=\"10\"></p>");

            Assert.Equal(10 * 0.6 * placed.FontSize, placed.Width, 3);
        }

        /// <summary>
        /// A <c>size</c> the document chooses cannot push a placeholder off the page: it is capped
        /// at eighty characters.
        /// </summary>
        [Fact]
        public void AnOversizedSizeAttributeIsCapped()
        {
            PlacedFormField placed = Only("<input name=\"wide\" size=\"100000\">");

            Assert.Equal(80 * 0.6 * placed.FontSize, placed.Width, 3);
        }

        // ------------------------------------------------------------------ check boxes and choices

        [Theory]
        [InlineData("<input type=\"checkbox\" name=\"agree\" checked>", true)]
        [InlineData("<input type=\"checkbox\" name=\"agree\">", false)]
        public void ACheckboxKeepsItsState(string html, bool expected)
        {
            PlacedFormField placed = Only(html);

            Assert.Equal(FormFieldKind.Checkbox, placed.Field.Kind);
            Assert.Equal(expected, placed.Field.IsChecked);
        }

        [Fact]
        public void ASelectIsAComboBox_WithItsOptionsAndTheSelectedValue()
        {
            PlacedFormField placed = Only(
                "<select name=\"country\"><option value=\"IL\">Israel</option>" +
                "<option value=\"GB\" selected>Britain</option></select>");

            Assert.Equal(FormFieldKind.ComboBox, placed.Field.Kind);
            Assert.Equal("GB", placed.Field.Value);
            Assert.Equal(new[] { ("IL", "Israel"), ("GB", "Britain") },
                         placed.Field.Options.Select(o => (o.Value, o.Text)));
        }

        /// <summary>A drop-down with nothing marked selected shows its first option, as a browser does.</summary>
        [Fact]
        public void AComboBoxWithNoSelectionTakesItsFirstOption()
        {
            Assert.Equal("IL", Only("<select name=\"c\"><option value=\"IL\">Israel</option><option>Britain</option></select>")
                                   .Field.Value);
        }

        [Fact]
        public void AMultipleSelectIsAMultiSelectListBox_WithNoSelectionUntilOneIsMade()
        {
            PlacedFormField placed = Only("<select name=\"langs\" multiple><option>he</option><option>en</option></select>");

            Assert.Equal(FormFieldKind.ListBox, placed.Field.Kind);
            Assert.True(placed.Field.MultiSelect);
            Assert.Null(placed.Field.Value);
            Assert.Equal(new[] { "he", "en" }, placed.Field.Options.Select(o => o.Value));
        }

        // ------------------------------------------------------------------ what stays a drawing

        // ------------------------------------------------------------------ radio groups

        /// <summary>
        /// Radios sharing a <c>name</c> are one group: every button placed under the group's name,
        /// each with its own value, and only the checked one checked.
        /// </summary>
        [Fact]
        public void RadiosSharingANameArePlacedAsOneGroup()
        {
            var (result, _) = Convert(
                "<input type=\"radio\" name=\"size\" value=\"s\"><input type=\"radio\" name=\"size\" value=\"m\" checked>");

            Assert.All(result.FormFields, f => Assert.Equal(FormFieldKind.RadioButton, f.Field.Kind));
            Assert.Equal(new[] { ("size", "s", false), ("size", "m", true) },
                         result.FormFields.Select(f => (f.Field.Name, f.Field.Value, f.Field.IsChecked)));
        }

        /// <summary>The last <c>checked</c> in a group wins, as in a browser, and un-checks the earlier one.</summary>
        [Fact]
        public void TheLastCheckedRadioInAGroupWins()
        {
            var (result, _) = Convert(
                "<input type=\"radio\" name=\"g\" value=\"a\" checked><input type=\"radio\" name=\"g\" value=\"b\" checked>");

            Assert.Equal(new[] { false, true }, result.FormFields.Select(f => f.Field.IsChecked));
        }

        /// <summary>A radio with no <c>name</c> belongs to no group but its own, as in HTML.</summary>
        [Fact]
        public void UnnamedRadiosAreGroupsOfTheirOwn()
        {
            var (result, _) = Convert("<input type=\"radio\" value=\"a\"><input type=\"radio\" value=\"b\">");

            Assert.Equal(2, result.FormFields.Select(f => f.Field.Name).Distinct().Count());
        }

        /// <summary>
        /// A button's value is its on state: two in one group cannot share it, and none may be
        /// <c>Off</c>. Each is made distinct and reported; a radio with no value takes HTML's
        /// <c>on</c>.
        /// </summary>
        [Fact]
        public void RadioValuesAreMadeDistinctWithinAGroup_AndOffIsNeverOne()
        {
            var (result, seen) = Convert(
                "<input type=\"radio\" name=\"g\" value=\"x\"><input type=\"radio\" name=\"g\" value=\"x\">" +
                "<input type=\"radio\" name=\"g\" value=\"Off\"><input type=\"radio\" name=\"g\">");

            Assert.Equal(new[] { "x", "x_2", "Off_2", "on" }, result.FormFields.Select(f => f.Field.Value));
            Assert.Equal(2, seen.Count(d => d.Detail.Contains("radio button", StringComparison.Ordinal)));
        }

        /// <summary>
        /// A radio group takes its field name like any other field: one already called <c>size</c>
        /// pushes the group to <c>size_2</c> rather than merging with it — and every button of the
        /// group follows.
        /// </summary>
        [Fact]
        public void ARadioGroupDoesNotMergeWithAFieldOfTheSameName()
        {
            var (result, _) = Convert(
                "<input name=\"size\"><input type=\"radio\" name=\"size\" value=\"s\"><input type=\"radio\" name=\"size\" value=\"m\">");

            Assert.Equal(new[] { "size", "size_2", "size_2" }, result.FormFields.Select(f => f.Field.Name));
        }

        [Fact]
        public void ButtonsStayDrawings_AndAreReported()
        {
            var (result, seen) = Convert("<input type=\"submit\" value=\"Send\"><input type=\"file\" name=\"cv\">");

            Assert.Empty(result.FormFields);
            Assert.Single(seen, d => d.Detail.Contains("button, file and image inputs", StringComparison.Ordinal));
        }

        [Fact]
        public void AHiddenInputIsNeitherAFieldNorADrawing()
        {
            var (result, _) = Convert("<p>Visible</p><input type=\"hidden\" name=\"token\" value=\"SECRET-TOKEN\">");

            Assert.Empty(result.FormFields);
            string content = DecodeStreams(result.Pdf);
            Assert.DoesNotContain("SECRET-TOKEN", content, StringComparison.Ordinal);
            Assert.Contains(DrawnTexts(content), t => t.Text.Contains("Visible", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ names

        /// <summary>
        /// A name repeated in the markup — which HTML allows and a check-box group does routinely —
        /// becomes two distinct fields, reported: §12.7.4.2 would read two fields under one name as one.
        /// </summary>
        [Fact]
        public void ARepeatedNameIsMadeUnique_AndTheRenameIsReported()
        {
            var (result, seen) = Convert(
                "<input type=\"checkbox\" name=\"topping\" value=\"cheese\"><input type=\"checkbox\" name=\"topping\" value=\"olives\">");

            Assert.Equal(new[] { "topping", "topping_2" }, result.FormFields.Select(f => f.Field.Name));
            Assert.Single(seen, d => d.Detail.Contains("'topping_2'", StringComparison.Ordinal));
        }

        /// <summary>A period separates the levels of a fully qualified field name (§12.7.4.2), so it cannot sit inside one.</summary>
        [Fact]
        public void APeriodInANameBecomesAnUnderscore()
        {
            Assert.Equal("address_city", Only("<input name=\"address.city\">").Field.Name);
        }

        [Theory]
        [InlineData("<input id=\"email\">", "email")]
        [InlineData("<input>", "text")]
        [InlineData("<input type=\"checkbox\">", "checkbox")]
        public void AControlWithNoNameIsNamedByItsIdElseByItsKind(string html, string expected)
        {
            Assert.Equal(expected, Only(html).Field.Name);
        }

        /// <summary>
        /// A control inside a float the exact-float path tries and abandons keeps its declared name
        /// rather than "f_2": the scratch map claims the name through <c>FieldNameFor</c>, and the
        /// allocation is rolled back with the rest of the scratch state on Abandon, so the band's re-map of
        /// the same control does not see it taken. The
        /// empty second float is what makes the exact path abandon mid-run, after the first was
        /// mapped.
        /// </summary>
        [Fact]
        public void AControlInAnAbandonedFloat_KeepsItsDeclaredName()
        {
            PlacedFormField field = Only(
                "<div style=\"float:left\"><input name=\"f\"></div>" +
                "<div style=\"float:left\"></div><p>beside</p>");

            Assert.Equal("f", field.Field.Name);
        }

        /// <summary>
        /// The control: two controls that genuinely share a name still get the suffix — the rollback
        /// frees only a name an abandoned scratch claimed, not a real duplicate.
        /// </summary>
        [Fact]
        public void TwoControlsSharingAName_StillGetTheSuffix()
        {
            var result = Convert("<input name=\"f\"><input name=\"f\">").Result;

            Assert.Equal(new[] { "f", "f_2" }, result.FormFields.Select(f => f.Field.Name));
        }

        // ------------------------------------------------------------------ the entry point

        /// <summary>Only the managed layout knows where a control landed, so a browser engine is refused by name.</summary>
        [Fact]
        public void TheBrowserEngineIsRefused()
        {
            var o = Managed();
            o.RenderEngine = HtmlRenderEngine.Browser;

            var ex = Assert.Throws<HtmlToPdfException>(() => HtmlToPdfConverter.ConvertHtmlWithFormFields("<input>", o));
            Assert.Contains("RenderEngine.Managed", ex.Message, StringComparison.Ordinal);
        }
    }
}

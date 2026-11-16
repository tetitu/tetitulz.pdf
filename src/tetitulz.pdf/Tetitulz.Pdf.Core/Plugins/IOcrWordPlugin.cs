// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally, so the setting is
// preserved per-file instead — see IOcrPlugin.cs.
#nullable enable annotations

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// One recognised word and where it sits in the image that was recognised.
    /// </summary>
    /// <remarks>
    /// <b>The rectangle is in IMAGE PIXELS, top-left origin, y increasing downward</b> — the space
    /// an OCR engine natively works in, not PDF user space. Converting to points is the caller's
    /// job, because only the caller knows the dpi the page was rasterised at and how tall the page
    /// is; a plugin that guessed would be wrong for every caller but one.
    /// </remarks>
    public sealed class OcrWordBox
    {
        /// <param name="text">The recognised word, in <b>logical</b> order.</param>
        /// <param name="x">Left edge, in pixels from the left of the image.</param>
        /// <param name="y">Top edge, in pixels from the <b>top</b> of the image.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="confidence">0–1, or a negative value when the engine reports none.</param>
        public OcrWordBox(string text, float x, float y, float width, float height,
                          float confidence = -1f)
        {
            Text = text ?? string.Empty;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Confidence = confidence;
        }

        /// <summary>
        /// The recognised word, in <b>logical</b> order — the order the characters are stored and
        /// read, not the order they appear on the page.
        /// </summary>
        /// <remarks>
        /// This matters for Hebrew and Arabic and nowhere else: an engine that returned visual
        /// order would produce a text layer that extracts backwards, and nothing downstream can
        /// tell the two apart from the string alone. Tesseract returns logical order.
        /// </remarks>
        public string Text { get; }

        /// <summary>Left edge, in pixels from the left of the recognised image.</summary>
        public float X { get; }

        /// <summary>Top edge, in pixels from the <b>top</b> of the recognised image.</summary>
        public float Y { get; }

        /// <summary>Width in pixels.</summary>
        public float Width { get; }

        /// <summary>Height in pixels.</summary>
        public float Height { get; }

        /// <summary>
        /// How sure the engine is, from 0 to 1 — or <b>negative</b> when it reports no confidence,
        /// which is not the same as being sure of nothing.
        /// </summary>
        public float Confidence { get; }

        /// <inheritdoc/>
        public override string ToString() =>
            $"'{Text}' at ({X},{Y}) {Width}x{Height}px";
    }

    /// <summary>
    /// An <see cref="IOcrPlugin"/> that can also report <b>where</b> each word is, which is what
    /// turns recognition into a searchable PDF.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a second interface rather than a method on <see cref="IOcrPlugin"/>.</b>
    /// Adding a member to a shipped public interface breaks every host that implements it, and Core
    /// targets <c>netstandard2.0</c>, where a default interface method — the language feature that
    /// would avoid the break — does not exist. A plugin opts in by implementing both; a plugin that
    /// only implements <see cref="IOcrPlugin"/> keeps working and is simply not offered the
    /// searchable-PDF path.</para>
    /// <para>Dispatch is explicit, like <see cref="IOcrPlugin"/>'s: nothing runs OCR unless the
    /// caller asks for it.</para>
    /// </remarks>
    public interface IOcrWordPlugin : IOcrPlugin
    {
        /// <summary>
        /// Recognise text in the pixel buffer and report each word with its rectangle.
        /// </summary>
        /// <param name="pixels">Interleaved 8-bit pixel data, row-major, top-to-bottom.</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <param name="components">Number of colour components (1=gray, 3=RGB).</param>
        /// <returns>
        /// The words, in the engine's <b>reading order</b> — which for a right-to-left line is
        /// rightmost first. <c>null</c> if recognition failed; an <b>empty list</b> if it ran and
        /// found nothing, and the two must not be conflated.
        /// </returns>
        IReadOnlyList<OcrWordBox>? RecognizeWords(byte[] pixels, int width, int height, int components);
    }
}

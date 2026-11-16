// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// HTML element names used by the managed (non-browser) mapper, as compile-time constants.
    ///
    /// <para>These are HTML concepts, not PDF ones, so they deliberately live in this assembly
    /// rather than in <c>Tetitulz.Pdf.Core.Constants</c>. Every name is lower-case, matching the
    /// <c>LocalName</c> AngleSharp reports for an HTML-namespace element (the mapper lower-cases
    /// before comparing where a comparison is not already ordinal-ignore-case).</para>
    /// </summary>
    internal static class HtmlTags
    {
        // ---- Block-level ----
        /// <summary><c>p</c></summary>
        public const string P = "p";
        /// <summary><c>div</c></summary>
        public const string Div = "div";
        /// <summary><c>section</c></summary>
        public const string Section = "section";
        /// <summary><c>article</c></summary>
        public const string Article = "article";
        /// <summary><c>header</c></summary>
        public const string Header = "header";
        /// <summary><c>footer</c></summary>
        public const string Footer = "footer";
        /// <summary><c>main</c></summary>
        public const string Main = "main";
        /// <summary><c>nav</c></summary>
        public const string Nav = "nav";
        /// <summary><c>aside</c></summary>
        public const string Aside = "aside";
        /// <summary><c>address</c></summary>
        public const string Address = "address";
        /// <summary><c>figure</c></summary>
        public const string Figure = "figure";
        /// <summary><c>figcaption</c></summary>
        public const string FigCaption = "figcaption";
        /// <summary><c>blockquote</c></summary>
        public const string Blockquote = "blockquote";
        /// <summary><c>pre</c></summary>
        public const string Pre = "pre";
        /// <summary><c>hr</c></summary>
        public const string Hr = "hr";

        // ---- Headings ----
        /// <summary><c>h1</c></summary>
        public const string H1 = "h1";
        /// <summary><c>h2</c></summary>
        public const string H2 = "h2";
        /// <summary><c>h3</c></summary>
        public const string H3 = "h3";
        /// <summary><c>h4</c></summary>
        public const string H4 = "h4";
        /// <summary><c>h5</c></summary>
        public const string H5 = "h5";
        /// <summary><c>h6</c></summary>
        public const string H6 = "h6";

        // ---- Lists ----
        /// <summary><c>ul</c></summary>
        public const string Ul = "ul";
        /// <summary><c>ol</c></summary>
        public const string Ol = "ol";
        /// <summary><c>li</c></summary>
        public const string Li = "li";
        /// <summary><c>dl</c></summary>
        public const string Dl = "dl";
        /// <summary><c>dt</c></summary>
        public const string Dt = "dt";
        /// <summary><c>dd</c></summary>
        public const string Dd = "dd";
        public const string Center = "center";

        // ---- Tables ----
        /// <summary><c>table</c></summary>
        public const string Table = "table";
        /// <summary><c>caption</c></summary>
        public const string Caption = "caption";
        /// <summary><c>tr</c></summary>
        public const string Tr = "tr";
        /// <summary><c>td</c></summary>
        public const string Td = "td";
        /// <summary><c>th</c></summary>
        public const string Th = "th";
        /// <summary><c>col</c></summary>
        public const string Col = "col";
        /// <summary><c>colgroup</c></summary>
        public const string ColGroup = "colgroup";
        /// <summary><c>thead</c> — its rows render first, whatever their document position.</summary>
        public const string THead = "thead";
        /// <summary><c>tbody</c></summary>
        public const string TBody = "tbody";
        /// <summary><c>tfoot</c> — its rows render last, whatever their document position.</summary>
        public const string TFoot = "tfoot";

        // ---- Inline formatting ----
        /// <summary><c>b</c></summary>
        public const string B = "b";
        /// <summary><c>strong</c></summary>
        public const string Strong = "strong";
        /// <summary><c>i</c></summary>
        public const string I = "i";
        /// <summary><c>em</c></summary>
        public const string Em = "em";
        /// <summary><c>cite</c></summary>
        public const string Cite = "cite";
        /// <summary><c>dfn</c></summary>
        public const string Dfn = "dfn";
        /// <summary><c>var</c></summary>
        public const string Var = "var";
        /// <summary><c>u</c></summary>
        public const string U = "u";
        /// <summary><c>ins</c></summary>
        public const string Ins = "ins";
        /// <summary><c>s</c></summary>
        public const string S = "s";
        /// <summary><c>strike</c></summary>
        public const string Strike = "strike";
        /// <summary><c>del</c></summary>
        public const string Del = "del";
        /// <summary><c>sup</c></summary>
        public const string Sup = "sup";
        /// <summary><c>sub</c></summary>
        public const string Sub = "sub";
        /// <summary><c>mark</c></summary>
        public const string Mark = "mark";
        /// <summary><c>small</c></summary>
        public const string Small = "small";
        /// <summary><c>big</c></summary>
        public const string Big = "big";
        /// <summary><c>code</c></summary>
        public const string Code = "code";
        /// <summary><c>kbd</c></summary>
        public const string Kbd = "kbd";
        /// <summary><c>samp</c></summary>
        public const string Samp = "samp";
        /// <summary><c>tt</c></summary>
        public const string Tt = "tt";
        /// <summary><c>span</c> — the generic inline container.</summary>
        public const string Span = "span";
        /// <summary><c>a</c></summary>
        public const string A = "a";
        /// <summary><c>font</c> — the legacy presentational tag.</summary>
        public const string Font = "font";
        /// <summary><c>br</c></summary>
        public const string Br = "br";
        /// <summary><c>bdo</c> — overrides the bidi algorithm for its content.</summary>
        public const string Bdo = "bdo";
        /// <summary><c>bdi</c> — isolates its content from the surrounding direction.</summary>
        public const string Bdi = "bdi";

        // ---- Embedded content and non-rendered heads ----
        /// <summary><c>img</c></summary>
        public const string Img = "img";
        /// <summary><c>svg</c></summary>
        public const string Svg = "svg";
        /// <summary><c>canvas</c> — drawn only by script; laid out as its blank box (HtmlToBuilderMapper.MapCanvas).</summary>
        public const string Canvas = "canvas";
        /// <summary><c>picture</c> — a container of <c>source</c> candidates plus a fallback <c>img</c>.</summary>
        public const string Picture = "picture";
        /// <summary><c>source</c></summary>
        public const string Source = "source";
        /// <summary><c>script</c></summary>
        public const string Script = "script";
        /// <summary><c>style</c></summary>
        public const string Style = "style";
        /// <summary><c>link</c></summary>
        public const string Link = "link";
        /// <summary><c>head</c></summary>
        public const string Head = "head";
        /// <summary><c>noscript</c> — rendered only when scripting is disabled, like a browser.</summary>
        public const string NoScript = "noscript";

        // ---- Form controls ----
        // Rendered as STATIC graphics, not AcroForm fields — see HtmlFormControls. That is what
        // Chromium's own page.pdf() does, so matching it keeps the two engines in agreement.
        /// <summary><c>form</c></summary>
        public const string Form = "form";
        /// <summary><c>input</c></summary>
        public const string Input = "input";
        /// <summary><c>textarea</c></summary>
        public const string TextArea = "textarea";
        /// <summary><c>select</c></summary>
        public const string Select = "select";
        /// <summary><c>option</c></summary>
        public const string Option = "option";
        /// <summary><c>optgroup</c></summary>
        public const string OptGroup = "optgroup";
        /// <summary><c>button</c></summary>
        public const string Button = "button";
        /// <summary><c>label</c></summary>
        public const string Label = "label";
        /// <summary><c>fieldset</c></summary>
        public const string FieldSet = "fieldset";
        /// <summary><c>legend</c></summary>
        public const string Legend = "legend";
    }
}

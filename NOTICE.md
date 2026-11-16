# Third-Party Notices

Tetitulz.Pdf itself is copyright (c) 2026 Tamir Mordo, under the Apache License 2.0 ([LICENSE](LICENSE), with the
credit in [NOTICE](NOTICE)), except `Plugins.PostScript`, `Plugins.Maintenance` and `Plugins.PageEdits`, which are
under the Tetitulz.Pdf Attribution License ([LICENSE-ATTRIBUTION.txt](LICENSE-ATTRIBUTION.txt)). It uses the
third-party components below, each under its own licence.

> **Versions are declared in one place: [`Directory.Packages.props`](Directory.Packages.props).**
> This file intentionally does **not** repeat them. To audit the live set:
>
> ```bash
> dotnet list tetitulz.pdf.sln package --include-transitive
> ```
>
> **The component list below is maintained by hand, and it has drifted in both directions.** Run
> that command against the table whenever you change a `PackageReference`: a licence notice that
> omits a component carries legal weight, and one that invents a component the product does not
> ship misstates what the user is receiving.
>
> Complex-script **shaping** is not a third-party component at all: Arabic joining is done by the
> pure-managed `Tetitulz.Pdf.Core.Text.OpenTypeShaper`, which is part of this library.

---

## Used by the library assemblies

| Component | License | Used for |
|---|---|---|
| **SkiaSharp** | MIT (© Microsoft Corporation) | Page rasterization to PNG/JPEG. <br>https://github.com/mono/SkiaSharp |
| **SkiaSharp.NativeAssets.Win32**, **SkiaSharp.NativeAssets.Linux**, **SkiaSharp.NativeAssets.macOS** | MIT (© Microsoft Corporation) | The native `libSkiaSharp` binaries. All three desktop platforms are referenced by `Tetitulz.Pdf.Base` (the Linux/macOS pair on the `net10.0` leg only — the `netstandard2.0` leg serves net462 hosts, which are Windows by definition), so consuming the packages does not force a Windows-only dependency graph. <br>https://github.com/mono/SkiaSharp |
| **BouncyCastle.Cryptography** | MIT (© The Legion of the Bouncy Castle Inc.) | PDF encryption (RC4/AES), decryption, digital signatures (PKCS#7/CMS), certificate handling, RFC 3161 TSA timestamps, OCSP/CRL revocation parsing. <br>https://github.com/bcgit/bc-csharp |
| **Portable.BouncyCastle** | MIT (© The Legion of the Bouncy Castle Inc.) | Compile-time only for the `netstandard2.0` target; assets are excluded so it is never shipped at runtime. |
| **Jint** | BSD 2-Clause (© Sebastien Ros) | Sandboxed JavaScript engine for PDF form actions and calculations, in `Tetitulz.Pdf.Plugins.JavaScript` — **not** a `Tetitulz.Pdf.Base` dependency. Runs with a 32 MiB memory cap, a 5-second timeout and a recursion guard. Also the engine behind `Tetitulz.Pdf.Plugins.HtmlScript`'s page scripts, under that plugin's own limits. <br>https://github.com/sebastienros/jint |
| **Acornima** | BSD 3-Clause (© Adam Simon) | The JavaScript parser Jint is built on; arrives transitively with it, so it ships wherever `Tetitulz.Pdf.Plugins.JavaScript` or `Tetitulz.Pdf.Plugins.HtmlScript` does. <br>https://github.com/adams85/acornima |
| **AngleSharp.Js** (vendored source) | MIT (© AngleSharp) | Binds AngleSharp's DOM to Jint for page JavaScript in the managed HTML engine. **Compiled into `Tetitulz.Pdf.Plugins.HtmlScript`**, not referenced as a package: version 1.1.0's source with one added hook (an engine-options callback, so the plugin can bound a page script's statements, memory and time), recorded in that plugin's `VENDORED.md`; its licence travels with it, at `Vendor/AngleSharp.Js/LICENSE` in the source and `licenses/AngleSharp.Js/LICENSE` in the package. <br>https://github.com/AngleSharp/AngleSharp.Js |
| **AngleSharp.Css** | MIT (© AngleSharp contributors) | The CSS object model (`element.style`) page scripts need, in `Tetitulz.Pdf.Plugins.HtmlScript` only. <br>https://github.com/AngleSharp/AngleSharp.Css |
| **DocumentFormat.OpenXml** | MIT (© Microsoft Corporation) | Reading DOCX for DOCX→PDF conversion. <br>https://github.com/dotnet/Open-XML-SDK |
| **AngleSharp** | MIT (© AngleSharp contributors) | HTML parsing for the managed HTML→PDF engine. <br>https://github.com/AngleSharp/AngleSharp |
| **Microsoft.Playwright** | Apache 2.0 (© Microsoft Corporation) | HTML→PDF via headless Chromium/Firefox/WebKit. Referenced only by `Tetitulz.Pdf.Plugins.HtmlBrowser`; `Tetitulz.Pdf.HtmlToPdf` and `Tetitulz.Pdf.Plugins.Html` ship without it (the managed engine needs no browser). The browser binaries are installed out of band by `playwright install`. <br>https://github.com/microsoft/playwright-dotnet |
| **Tesseract** | Apache 2.0 | Optional OCR in `Tetitulz.Pdf.Plugins.Ocr`. <br>https://github.com/charlesw/tesseract |
| **Nethereum.Web3** | MIT (© Nethereum Contributors) | Ethereum integration for the optional `Tetitulz.Pdf.Plugins.EthereumSign` plugin. <br>https://github.com/Nethereum/Nethereum |
| **Newtonsoft.Json** | MIT (© James Newton-King) | JSON serialization for blockchain signature proofs. Pinned (Directory.Packages.props) to avoid GHSA-5crp-9r3c-p9vr in older transitive versions. <br>https://github.com/JamesNK/Newtonsoft.Json |
| **Brotli static dictionary** | RFC 7932 Appendix A (© IETF Trust; code components under the Simplified BSD License), the same 122,784 bytes google/brotli publishes under MIT (© the Brotli Authors; the MIT text is packed as `licenses/LICENSE-Brotli.txt`) | The static dictionary of `Tetitulz.Pdf.Core`'s own Brotli decoder (`IO/Brotli/dictionary.bin`, embedded, checked against the RFC's CRC-32 when read), which reads WOFF 2.0 fonts and `/BrotliDecode` streams. The decoder is this library's, written from the RFC; BrotliSharpLib was removed on 2026-10-18. <br>https://www.rfc-editor.org/rfc/rfc7932 · https://github.com/google/brotli
| **System.Text.Encoding.CodePages** | MIT (© .NET Foundation) | Windows-1252 fallback codepage on .NET Core hosts. <br>https://github.com/dotnet/runtime |
| **System.Security.Cryptography.Pkcs** | MIT (© .NET Foundation) | `SignedCms` / `CmsSigner` for `Tetitulz.Pdf.Plugins.CertStoreSign` — the only way to build a CMS with a key that never leaves a smart card or HSM. Not in-box on any target framework. <br>https://github.com/dotnet/runtime |
| **Microsoft.Bcl.Cryptography** | MIT (© .NET Foundation) | Arrives transitively with `System.Security.Cryptography.Pkcs` on the `netstandard2.0` and `net462` legs. <br>https://github.com/dotnet/runtime |
| **System.Memory** | MIT (© .NET Foundation) | `Span<T>` / `MemoryExtensions` on the `netstandard2.0` and `net462` legs. **A .NET Framework host needs a binding redirect for it**, which an SDK-style executable generates itself — see the `HtmlToPdf` README. <br>https://github.com/dotnet/runtime |
| **Microsoft.CSharp** | MIT (© .NET Foundation) | The `dynamic` binder, on `Tetitulz.Pdf.DocxToPdf`'s `netstandard2.0` leg only, for `WordComConverter`'s late-bound COM calls. <br>https://github.com/dotnet/runtime |
| **System.Net.Http**, **System.Text.RegularExpressions** | MIT (© .NET Foundation) | **Not referenced and not distributed** as of 2026-08-27 — on `net10.0` both ship in the shared framework, so the four test projects that pinned them no longer reference them and neither package appears in any project's dependency graph. Their `PackageVersion` entries stay in `Directory.Packages.props` as a **dormant transitive pin** (central transitive pinning is on), so that if a future dependency ever drags one in it resolves to the safe version rather than the vulnerable one (GHSA-7jgj-8wvc-jh57, GHSA-cmhx-cq75-c4mj). Listed here for that reason, not as a shipped component. |
| **Microsoft.NETFramework.ReferenceAssemblies.net462** | MIT (© .NET Foundation) | Reference assemblies for the `net462` target of the WinForms viewer. |
| **Serilog** | Apache 2.0 (© Serilog Contributors) | The logging API of `Tetitulz.Pdf.Printing`; the host chooses the sinks. <br>https://github.com/serilog/serilog |

## Embedded data

| Component | License | Notes |
|---|---|---|
| **Liberation Sans, Serif and Mono 2.1.5** | SIL Open Font License 1.1 (© Red Hat, Inc.) | Packed unmodified in `Tetitulz.Pdf.Plugins.Fonts` as the stand-ins for the standard 14; the licence text is packed and written beside the fonts. No font ships in the core packages. <br>https://github.com/liberationfonts/liberation-fonts |
| **Noto Sans Hebrew 3.001, Noto Sans Arabic 2.013** | SIL Open Font License 1.1 (© The Noto Project Authors) | Packed unmodified in `Tetitulz.Pdf.Plugins.Fonts`, with the licence text, for hosts with no Hebrew or Arabic font installed. <br>https://github.com/notofonts |
| **Adobe CMap resources** | BSD 3-Clause (© 1990–2023 Adobe) | The four Adobe CMap collections are **embedded as assembly resources** in `Tetitulz.Pdf.Plugins.CMap`, so no runtime download is needed. The full notice is [`src/plugins/Tetitulz.Pdf.Plugins.CMap/LICENSE.md`](src/plugins/Tetitulz.Pdf.Plugins.CMap/LICENSE.md) and it is packed into that package as `LICENSE-Adobe-CMap.md` so it travels with the binary. |
| **Adobe Glyph List and ITC Zapf Dingbats Glyph List** (`glyphlist.txt`, `zapfdingbats.txt`) | BSD 3-Clause (© 2002–2019 Adobe) | The glyph-name to Unicode tables, compiled into `Tetitulz.Pdf.Core` (`Fonts/StandardEncodings.GlyphData.cs`) with a few aliases older producers write. The licence text is `licenses/LICENSE-Adobe-Glyph-List.txt`, packed with that package. <br>https://github.com/adobe-type-tools/agl-aglfn |
| **American English hyphenation patterns** (`hyph-en-us.tex`, 2005-05-30) | All-permissive: "Copying and distribution of this file, with or without modification, are permitted in any medium without royalty provided the copyright notice and this notice are preserved" (© 1990, 2004, 2005 Gerard D.C. Kuiken) | Embedded unmodified, notice included, as an assembly resource of `Tetitulz.Pdf.Core` (`Text/Hyphenation/hyph-en-us.tex`), read by its Liang hyphenator for the Builder's `Paragraph.Hyphenate` and HTML's `hyphens: auto`. From the hyph-utf8 package. <br>https://www.hyphenation.org/tex · https://github.com/hyphenation/tex-hyphen |
| **Unicode Character Database 18.0.0** — derived tables | Unicode License v3 (© 1991–2026 Unicode, Inc.): permission to use, copy, modify, merge, publish, distribute and sell, provided the copyright and permission notice appears with all copies or in associated documentation — it appears here, and whole in `tests/Tetitulz.Pdf.Core.Tests/Fixtures/Unicode/LICENSE-Unicode.txt` | Tables generated from the data files, compiled into `Tetitulz.Pdf.Core` — Bidi_Class (`Text/UnicodeBidi.Classes.cs`, from `extracted/DerivedBidiClass.txt`), Bidi_Paired_Bracket (`Text/UnicodeBidi.Brackets.cs`, from `BidiBrackets.txt` and `UnicodeData.txt`), Script (`Text/UnicodeScripts.Table.cs`) — and the ISO-8859 tables of `Tetitulz.Pdf.Base` (`Operations/Barcodes/Iso8859Tables.cs`, from the Consortium's mapping files), each by its script under `tools/`. <br>https://www.unicode.org/Public/UCD/ · https://www.unicode.org/license.txt |
| **Adobe Core14 AFM files** (Helvetica, Helvetica-Bold, Times-Roman, Times-Bold, Times-Italic, Times-BoldItalic) | "This file and the 14 PostScript(R) AFM files it accompanies may be used, copied, and distributed for any purpose and without charge, with or without modification, provided that all copyright notices are retained; that the AFM files are not distributed without this file; …" (© 1985–1997 Adobe Systems Incorporated) | Embedded as assembly resources of `Tetitulz.Pdf.Core` (`Fonts/Afm/`), gzipped and otherwise unmodified, copyright lines kept, with Adobe's `MustRead.html` beside them; their `KPX` pairs kern a standard-14 run (`Standard14Kerning`). Fetched by `tools/standard14-afm/pack.ps1`, which checks Apache PDFBox's copy against PDFKit's. |

The apps built on the library, and the components only they use, are in
[tetitulz.pdf-apps](https://github.com/tetitu/tetitulz.pdf-apps) with their own notice.

## Used by the test projects only

**xunit**, **xunit.runner.visualstudio**, **Microsoft.NET.Test.Sdk**, **coverlet.collector** —
all Apache 2.0 or MIT. Not distributed.

**veraPDF test corpus** — CC BY 4.0 (© the veraPDF Consortium). A subset of
github.com/veraPDF/veraPDF-corpus (commit `bb75f4f`), file names shortened, contents unmodified, under
`tests/Tetitulz.Pdf.Plugins.Conformance.Tests/Fixtures/veraPDF/` with the licence text and the selection in its `README.md`. Test
data, not distributed in any package.

**Unicode's UAX #9 conformance files** — Unicode License v3 (© 1991–2026 Unicode, Inc.). `BidiCharacterTest.txt` and `BidiTest.txt` of the
Unicode Character Database 18.0.0, gzipped and otherwise unmodified, under `tests/Tetitulz.Pdf.Core.Tests/Fixtures/Unicode/` with
the licence text. Test data, not distributed in any package.

## Used by the benchmark and fuzz harnesses only

**BenchmarkDotNet** (MIT) — the performance harness in `benchmarks/Tetitulz.Pdf.Benchmarks`, which
is outside `src/` and never packs. **SharpFuzz** (MIT) — the coverage-guided fuzz harness in
`tools/fuzz`, likewise outside `src/`. Neither is distributed.

## Build-time only

**MinVer** (Apache 2.0) — derives the package version from git tags. `PrivateAssets="all"`, so it
never becomes a dependency of any produced package.

---

All components above are MIT, BSD 2-Clause, BSD 3-Clause, or Apache 2.0 — free for commercial
use. JPEG 2000 is decoded and encoded in-house; no third-party JPEG 2000 code ships. Native binaries shipped via NuGet (SkiaSharp, Tesseract) carry their upstream
notices, including LGPL/FreeType terms where applicable, inside their own packages. The library's
own code makes no direct native call (guarded by `NoLibraryPInvokeTests`).

## Where the licence texts are

Every library package carries this file and `THIRD-PARTY.md`; a package embedding data also carries
that data's full licence under `licenses/` (`Tetitulz.Pdf.Core`: Unicode, Brotli, the Adobe Glyph List; `Tetitulz.Pdf.Base`:
Unicode), and the CMap and Fonts plugins and the vendored AngleSharp.Js carry theirs as listed above.
Each released app archive carries `LICENSE`, `NOTICE`, this file, `THIRD-PARTY.md` and every licence text.

## Test fixtures not listed above

**BFO PDF/A test suite** — CC BY 3.0 (© BFO), `tests/Tetitulz.Pdf.Plugins.Conformance.Tests/Fixtures/bfo/`,
unmodified, the licence linked from its `README.md`. **The PDF Association's PDF 2.0 examples** — CC BY-SA
4.0, `tests/Tetitulz.Pdf.Base.Tests/Fixtures/pdf20examples/`, unmodified, with the licence. **An HTJ2K
conformance codestream** (© 2022 Aous Naman) — BSD 2-Clause, `tests/Tetitulz.Pdf.Core.Tests/Fixtures/Htj2k/`,
with the licence. **Open Sans 1.10** — Apache 2.0 (© 2010–2011 Google), `tests/Tetitulz.Pdf.Base.Tests/Fixtures/OpenSans/`,
unmodified, with the licence. **jQuery 1.12.4 and 3.7.1** — MIT (© OpenJS Foundation), `tests/Tetitulz.Pdf.Plugins.HtmlScript.Tests/Fixtures/`,
unmodified minified builds with their headers. **Recorded independent answers** (`Fixtures/IndependentDecodes`,
`Fixtures/IndependentRenders`, `Fixtures/WriterCheck`) are the output of other programs run on this
repository's own test inputs. None of these is packed or distributed.

## Not shipped

No proprietary colour library: the built-in table of trade-named spot colours was removed on 2026-10-25
(see `THIRD-PARTY.md`).

# Tetitulz.Pdf — Contributor Guide (long form)

A pure-managed .NET PDF library: parse, render, extract, edit, sign, convert. No native PDF engine.

This is the **long form of [`CLAUDE.md`](CLAUDE.md)**: the same rules under the same headings, each
with its reasoning and the measurement behind it. `CLAUDE.md` is the digest an agent loads on every
turn, so it states the rule; this file says **why**, which is what stops a reader "fixing" the thing a
rule protects. **A rule changes in both files in the same commit.** Where they disagree on the rule,
`CLAUDE.md` wins; where they disagree on the reasoning, this file does.

Open gaps live in `GAPS.md`, a developer-local register not in the published repository; the user-facing documentation in [`README.md`](README.md),
and history in git.

---

## Layout

| Path | Contents |
|---|---|
| `src/tetitulz.pdf/` | `Core`, `Base`, `Encryption`, `Signing` |
| `src/features/` | `HtmlToPdf`, `DocxToPdf`, `Builder`, `Accessibility` |
| `src/plugins/` | the plugin ring, enumerated in `CLAUDE.md` |
| `tests/` | One test project per production project. No cross-project test dependencies. |
| — | The Windows-only packages (the WinForms viewer control, `Printing`), the apps and the demos are a separate repository, `tetitulz.pdf-apps`, that takes this one as a submodule and builds against its sources. |
| `benchmarks/`, `tools/` | Developer-local, not in the published repository: the BenchmarkDotNet harness; fuzzing, the corpus scorecard and fetcher, the package-consumption check, the audit harnesses, the Unicode table generators. |
| `docs/` | Index only — [`docs/README.md`](docs/README.md). |

One library `.nupkg` per project under `src/`. `IsPackable` defaults to false repo-wide and
`src/Directory.Build.props` flips it to true, so nothing outside `src/` can pack.

> **The package count and the plugin list are stated in `CLAUDE.md` only**, because they are the two
> enumerable claims and so the two that are guarded:
> `tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/ProjectRegisterFreshnessTests.cs` reads them back
> off the tree, from `CLAUDE.md`. A second copy here would be the unguarded one, and an
> unguarded count drifts: a new plugin ships while the file still lists the old number.

### Target frameworks

| Project group | TFM |
|---|---|
| Every `src/` project | `net10.0;netstandard2.0` |
| Test projects | `net10.0` |

**A .NET Framework host can reach every package**, so three constructs missing on the legacy leg each
have an in-repo answer: `Core.Text.Latin1Text` for `Encoding.Latin1` and `Encoding.GetEncoding("iso-8859-1")`,
`!double.IsNaN(x) && !double.IsInfinity(x)` for `double.IsFinite`, and `Core.Objects.PdfReferenceEquality`
for `ReferenceEqualityComparer`.

**A `float` or `double` becomes an integer only through `Core.Util.SaturatingCast`.** An explicit
cast out of range is saturated by .NET 9 and later and is the type's *minimum* on .NET Framework, so
`Math.Min(width, (int)Math.Ceiling(x))` clamps on one runtime and lets `int.MinValue` through on the
other — a redaction footprint or a flattened region of that shape goes wrong only on .NET Framework.
And .NET 10 saturates only the 32- and 64-bit targets: `(byte)300.0` is 44, so a colour channel past its
range comes out dark. Every conversion in `src/` goes through the helper, which answers what .NET 10's
`int`, `long` and `uint` casts answer (`SaturatingCastTests` compares them over twenty thousand values)
and clamps the small types. `SaturatingCastGuardTests` (Base.Tests) compiles `src/` under both target
frameworks' symbols with Roslyn and fails on any other float-to-integer cast — by type, because
`(int)(a * b)` does not say what `a * b` is; it also fails when it cannot type a cast's operand, so a
missing reference cannot hide a cast from it.

*Latin-1.* `Encoding.GetEncoding("iso-8859-1")` is the quieter half of the hazard: it compiles on
netstandard2.0, unlike `Encoding.Latin1`, and then throws on a .NET Core host that consumes the
netstandard2.0 asset without the CodePages provider (a .NET 8 app, say) — in the standard security
handler, while opening an RC4 document under a non-empty password. Every such transcode goes through
`Latin1Text`. The one `Encoding` *object*, `PdfFont`'s CP-1252 fallback held for repeated `GetString`,
is `Core.Text.Latin1Encoding` (§21 of GAPS.md), so the fallback-of-a-fallback works on the host it
exists for.

*The comparer.* `PdfReferenceEquality` is the **only** reference-identity comparer in the tree. An
unchecked rule of this kind gathers copies, so `SingleReferenceIdentityComparerTests` fails on any class
implementing `IEqualityComparer` over the identity hash outside `PdfReferenceEquality.cs`.

**A key a file chooses hashes through `Core.Util.KeyedHash`** — SipHash-2-4 under a key drawn once a
process. A hash table compares a key with every key in its bucket, so a file that can put n keys in one
bucket makes filling the table cost n² comparisons, and a budget that charges an insert for its key
never sees the walk. `double.GetHashCode` XORs a value's two 32-bit halves on every runtime, so any reals
whose halves XOR to one value share a bucket: 131 072 of them, a 3.5 MB PostScript program, take 112 s
to define in one dictionary under it and 0.24 s under the keyed hash. .NET Framework's
`string.GetHashCode` is one fixed function (two DJB-style chains over the even and the odd characters),
so names collide offline the same way; there `KeyedHash.Of(string)` is SipHash over the UTF-16 code
units, and on .NET Core and later it is `string.GetHashCode`, which the runtime already keys. `PdfName`,
the PostScript plugin's `PsObject` (a string key hashing as the name it equals) and the JBIG2 encoder's
`SymbolKey` (a symbol's bitmap, which an image chooses) hash through it. `Dictionary<string, …>` and
`HashSet<string>` guard themselves on .NET Framework by switching to a randomized comparer when a bucket
grows long — 16 384 colliding names cost them 2–27 ms, against 2.1 s for a key type hashing the string
itself and 5.2 s for a `(string, string)` tuple. Neither a tuple nor LINQ's `Distinct`, `GroupBy` and
`ToLookup` (1–2 s, `StringComparer.Ordinal` included, since LINQ's tables use the comparer's hash as it
is) switches, so **a tuple key holding a string takes `KeyedHash.Comparer`** — a comparer that compares
as the tuple does and hashes through the function it is given, built over `KeyedHash.Of(string)` — and a
LINQ operator over strings a file chose takes `KeyedHash.Strings`. `TupleKeyComparerGuardTests` holds
the first by the compiler's types: every `Dictionary`, `HashSet` and `ConcurrentDictionary` creation and
every hashing LINQ call in `src/` whose key is a tuple (or an anonymous type) holding a string must call
an overload with a comparer, whether or not the string is a file's — a rule that needs no judgement is
one a guard can hold. The framework's XML name tables (`XName`, `NameTable`) are not affected: colliding
element names parse as fast as random ones there. A comparer field that a static field's initializer
reads is declared before it — static fields are initialised in order, and a null comparer is the
default one. `KeyedHashTests` checks SipHash against the reference implementation's vectors and makes the
collisions — reals, names under a model of .NET Framework's hash pinned to that runtime's own answers,
bitmaps under FNV-1 — and asserts their spread; the PostScript plugin's `DictionaryKeyHashTests` does so
for real keys.

Two behaviours differ on the legacy leg: `Plugins.Maintenance`'s Lossless uses deflate Optimal (no
`SmallestSize`), and **`HtmlToPdf` needs a `System.Memory` binding redirect on .NET Framework**
(AngleSharp binds `System.Memory` 4.0.5.0 against a graph compiled for 4.0.1.2). An SDK-style exe
generates it: `AutoGenerateBindingRedirects` is on by default, and the package-consumption check's
net462 leg, a default project with no `app.config`, passes on it. A host that generates no redirects,
such as a `web.config` or a project with the setting off, writes it by hand. **A consumer test keeps
its restore folder out of its items** (`DefaultItemExcludes`): `RestorePackagesPath` puts the folder
inside the project directory, the default globs would take every restored DLL as an item, and
reference resolution would print MSB3277 conflicts that no real consumer sees.

`DocxToPdf`'s LibreOffice engine builds its command line through `Engines/ProcessArgumentEscaper.cs` on
the legacy leg only, because `ProcessStartInfo.ArgumentList` does not exist there; net10.0 keeps
`ArgumentList`, since replacing a framework guarantee with our own code is a downgrade. The escaper is
security-sensitive on a caller-supplied path; its tests round-trip through `CommandLineToArgvW`. **If you
touch it, re-run the three escaping-rule mutations.**

**`Plugins.CertStoreSign`'s `KeyPin` is on the net10.0 leg only**, and on Windows. A PIN reaches a token
on the key's own handle (a key storage provider's key takes it as its PIN property, an older provider's
key as the password it is opened with), and the CMS is then signed through that handle —
`CmsSigner.PrivateKey`, which the netstandard2.0 build of the package does not have; without it the CMS
signer opens a handle of its own, which has no PIN. The legacy leg therefore has no such property
rather than one that does nothing. The tests need no token: Windows' software provider keeps a key under
a password when it is made with one, and takes the password through the same property, so a silent
signature with no PIN is refused, a wrong PIN is refused before anything is signed, and the right one
signs. **The test's key is made and deleted with the provider's silent flag, and no test uses it
without its PIN unless `Silent` is set** — otherwise the provider's password window opens on the
machine running the tests.

**Word and LibreOffice are handed a copy of the package with what leads out of it pointing nowhere**
(`Engines/ExternalEngineInput.cs`). With macros off and `UpdateLinksAtOpen` false, Word opened a document and
asked a listening socket for its attached template, its external image and its subdocument, and an HTML
`altChunk`'s image and stylesheet: on a server, a request to an address the document's author chooses, and on
Windows a UNC target hands over the account's credentials. Every relationship leading out is pointed at
`about:blank`, its type kept so that what names it still reads, a hyperlink excepted since nothing follows one
until it is clicked — but only one that hyperlink elements alone name: an engine looks a relationship up by its
id whatever type it declares, so a "hyperlink" an image's `r:link` names is fetched as the picture. A file that is not a package is refused because Word goes by the bytes and reads a `.docx`
holding HTML or RTF as that, and a ZIP with no Open Packaging Conventions frame around a main document of its
own parts — an OpenDocument file named `.docx` — because an engine tells the format by what the archive holds
and such a file's references are no relationships; an `aFChunk` because it is a document of its own. Field codes stay: nothing asked
for an `INCLUDEPICTURE`, an `INCLUDETEXT` or an updated field, with this Word. The test reads the package written,
since no test runs a program outside the repository; the socket was read by hand, and LibreOffice has not been
run against it.

**`Accessibility` and `Conformance` must stay in step**: dropping Conformance's legacy leg breaks
Accessibility's with `NU1201`, not a compile error.

### Platforms

**Windows and Linux are supported; macOS is unverified.** "Pure-managed" means no native PDF engine, not
that rendering is free of native code: `Core` has no native dependency; `Base` pulls SkiaSharp's native
assets for all three desktop platforms.

- **Zero P/Invokes in library code**, none to gdi32, user32, gdiplus, dwrite or windowscodecs, no WIC,
  no registry. `System.Drawing` appears only in the viewer, which blits PNG bytes. **Do not "port away
  from Win32"; there is nothing to port.** `NoLibraryPInvokeTests` scans `src/` for `DllImport`,
  `LibraryImport`, `extern`, `NativeLibrary` and unmanaged function pointers.

  *Why.* A P/Invoke into a host's codec runs only where the library loads, so no test sees it: a
  struct allocated short of the native size (512 bytes for an `opj_dparameters_t` of about 8 KB, which
  the native default-parameters call zeroes whole) overruns the heap on every decode before reading a
  byte. The in-house codec covers JPEG 2000. **A native codec comes only as a plugin that isolates it.**
- Use `SkiaSharp.NativeAssets.Linux`, not `.Linux.NoDependencies`: the latter has no fontconfig, so no
  system font can ever be resolved.
- **SkiaSharp is on the stable line** (a preview pin would force prerelease resolution on consumers). Text state lives on `SKFont`. Deprecated families are
  migrated except **five deliberate `CS0618` sites**, the `PdfRenderContext._currentPath` mutators: that
  path is read between mutations (`Bounds`, `VerbCount`, `SegmentMasks`, `SKPathMeasure`) and
  `SKPathBuilder` offers none of that, so converting means a `Snapshot()` per read on a path reset
  thousands of times a page. **Do not silence them with `NoWarn` and do not "finish" them without a
  measurement.**

  > **`SKSamplingOptions.Default` is byte-identical to the obsolete draw overloads; `Linear` is not.**
  > Only a 1:1 blit converts mechanically; a draw that scales takes its sampling from `/Interpolate`
  > (Mitchell cubic when true, nearest when false).
- **`SkiaSharp.HarfBuzz` and `HarfBuzzSharp.NativeAssets.*` must not come back** (see *Shaping*).

**Font resolution off Windows.** `Core.Fonts.FontFileLocator` matches font files by name as a second
pass at every resolution site, mapping the standard 14 onto Arial, Times New Roman and Courier New where
present, then Liberation (metric-compatible, so `/Widths` stays meaningful), then DejaVu, Noto and Free.
**No fonts ship in the core packages** (licensing). `Tetitulz.Pdf.Plugins.Fonts` carries Liberation and
Noto Sans Hebrew and Arabic under the OFL, unmodified, the licence texts packed and written beside
them, and registers the directory it extracts them to through the one public door,
`Core.Fonts.FontSearchPath.Add`: searched first, ahead of the OS directories and of
`TETITULZ_PDF_FONTDIR`, which still *replaces* the OS search path when set.

Two rules for a bundled font: **a family asked for
by its own name comes before its substitutes** (`CandidateStems` promotes the stem that is the family's
own name, so "Liberation Sans" is not answered with Arial merely because "arial" leads the shared sans
list), and **a registered directory's file wins a same-name, same-style tie with the host's**
(an ordinal tie-break on the path would let `C:\Windows\Fonts\…` beat a bundled copy under `D:\`).
Style rank still comes first.

> **`SKTypeface.FromFamilyName` lies on both platforms**: DirectWrite returns Segoe UI for an unknown
> family, fontconfig returns DejaVu Sans. Every resolution site asks `FontFileLocator.IsFamilyMatch`,
> never `!= null`, **and it is one implementation**, which `FontCache`, `SystemFontResolver`,
> the Accessibility font remediator and the test suite's probe all call.

> **A `FontCache` typeface is never valid outside the scope that disposes the cache.** `Dispose` disposes
> every typeface it hands out; only the resolved path is memoised. Read inside the scope
> (`WithResolvedTypeface`).

**A font program's own counts are hostile input**. A reader that checks every range against the
buffer and nothing against each other is open three ways. INDEX offsets that need not ascend let a
sawtooth declare half the items as copies of the whole data, 32 GB from a 1 MB `/FontFile3`, so
`CffParser.TryLocateIndex` refuses them. Table records that may overlap let a directory of 512 records
each spanning the font make every re-emitter copy the font 512 times, so `SfntTableDirectory.TryRead`
refuses a directory declaring more than twice the font. A `/Subrs` block's closing `def` sought afresh
from every `dup` reads the section *n* times for *n* one-byte subroutines (180 MB for a 60 KB program),
so `Type1Parser.ParseSubrs` seeks it once, and `LastScanBytesExamined` is the evidence the test
asserts, never a clock. **A reader that bounds each item by the buffer has bounded the largest item,
not the sum.**

### Shaping — a reader must not shape

**Neither renderer shapes text, no shaping package is referenced, and it must stay that way.** §9.4
defines showing text as character codes → glyphs with advances from `/Widths`; the producer already
chose them, and a non-embedded Arabic PDF stores presentation forms (U+FE70–FEFF), which shaping again
would double-shape.

**Producing a PDF is the opposite case and does shape.** Builder, DocxToPdf, the AcroForm appearance
generator and the signature appearance generator reach `Core.Text.TextRunComposer` → `OpenTypeShaper`,
pure-managed and unreachable from any Base-resident shaper, since Builder may reference only Core.

**Marks are placed by the font's own anchors, and the placement has to reach the page.** The composer
applies `GPOS` `mark`, `mkmk` and `curs` (`OpenTypeMarkPositioning`: anchors, single adjustments,
mark-to-ligature on the last component, context and chained context in all three formats through one
rule matcher, over the glyphs the lookup does not skip — the ignore flags, a mark filtering set or a
mark-attachment type, decided in one place, `SkipsAt`) and returns each glyph's offset and advance
change on `ShapedGlyphRun.Placements`; a point is attached in logical order and placed in visual order.
Every producer site goes through `GlyphRunShowText`,
which writes the placements as `TJ` adjustments and a text rise. **A new producer that builds its own
hex string draws the point at the letter's left edge and no extraction test sees it.**

**Kerning rides the same placements, and each producer decides whether to ask** (`TextRunOptions.Kerning`:
the `kern` feature's pair adjustments, else the legacy `kern` table). HTML asks as a browser does, DOCX
where `w:kern` does, the Builder through its options; form and signature appearances do not. **An
advance change belongs to the logically first glyph and widens the gap after it in reading order**, so
right to left it goes to the letter drawn on the left, past any mark.

**Some points are not positioned; the letter is replaced.** Arial, Segoe UI and Times anchor a dagesh
or a shin dot on no base, because their `GSUB` draws the pair as a presentation form (U+FB31, U+FB2A).
The composer draws the form where the font has its glyph (`HebrewPresentationForms`), and
`ToUnicodeCMapWriter` writes it back as the letter and point. The lam-alef ligature works the same way
(`ArabicLigatures`): a character of its own, so no cluster tracking is needed — **unless the font's
own required rules redraw the pair**, in which case the two forms are left for the substitution pass
(`TextRunComposer.FontRedrawsLamAlef`). The font's mark anchors are on the glyphs its
`GSUB` produces: Arial's and Times's on a ligature glyph their `cmap` also maps U+FEFB to, Segoe UI's on
two contextual pieces its `rclt` substitutes for the lam and the alef, with the `cmap`'s U+FEFB glyph
carrying none — so a fatha on the encoded ligature would stay at its pen position in Segoe UI. **Probe
the font's own tables before writing the fixture**: a fixture that assumes a ligature glyph passes
while Segoe UI fails.

**A font's own ligatures carry their letters as text, and every producer must record it**.
The composer forms `GSUB` type 4 ligatures (`rlig` always, `liga` when asked); the ligature glyph has
no code point, so `GlyphMapping.Text` carries the letters it joined, recorded through
`ShapedGlyphRun.RecordUsedGlyphs` (the Builder through `EmbeddedFont.Note`) and written by the one
`/ToUnicode` writer. **A producer that registers glyph ids and code points alone writes a ligature back
as its first letter**, and a search for "fi" misses the word.

**Which script a run's lookups are taken under is Unicode's answer, generated, with a shaper's
fallbacks**. `Core.Text.UnicodeScripts` is generated from the Character Database by
`tools/unicode-scripts/Generate-UnicodeScripts.ps1`; **a run is cut into segments of one script**
(`TextRunComposer.ScriptSegments`, in logical order; a glyph of no script — a space, a digit, a point —
takes its neighbour's, a run with none is `latn`), each looked up under its own tag, and `Fallbacks`
is the usual shaper's chain (the tag, an Indic first-generation tag, `DFLT`, `dflt`, `latn`). One tag
for a whole run would look a Latin word beside Hebrew up under `hebr` and lose its `latn` kerning (a
Latin pair unkerned at 2300 where 2220 is right). **Every by-script lookup goes through the chain** (`LookupsFor`, `JoiningLookups`); a
site that falls back to `DFLT` alone drops the Latin step that lets a `latn`-only font kern the Cyrillic
it covers. Never type a range into `ScriptOf`.

**A placed or composed mark has to read back as typed**, which takes three extractor rules: a mark's
`TJ` move is not a word space (the space waits for the glyph after it); a right-to-left line is not
reversed code unit by code unit (`UnicodeBidi.InVisualOrder` pre-reverses a two-character glyph); and
a zero width is a real width and a mark is not sorted as a letter (`GlyphWidth.Units`, `AttachMarks`).

**A drawn line does not say its paragraph's direction.** P2/P3 asked of the visual line finds its
leftmost strong character, which is a right-to-left line's *end*: a Hebrew line ending in a Latin code
would read back left to right, code first. `UnicodeBidi.VisualLineDirection` is the one guess — the
ends when they agree, the more strong characters when they do not, the left end on a tie; a caller
that knows the paragraph's direction has the answer and should pass it.

> **Extraction cannot see a shaping defect.** `/ToUnicode` maps an isolated presentation form back to
> its base letter as happily as a medial one. Only a test asserting *which presentation form was chosen*
> detects wrong shaping.

### XML and regular expressions over a file's text

**Every XML reader in the tree sets `DtdProcessing.Prohibit` (or `Ignore`) and `XmlResolver = null`.**
Thirteen readers do: XMP, XFDF, the XFA `datasets` and `template` packets, SVG, the embedded e-invoice
reader, the PDF/A, PDF/X and PDF/UA checkers. The XXE fixture (a `SYSTEM` entity naming a local file)
is refused as not well-formed, with a control that parses; a mutation to `Parse` is killed.

**A regular expression over a field the file controls is unambiguous, length-capped before it runs,
and has a timeout behind both.** Three shapes are quadratic: `\d+\.?\d*` (with the dot optional, a
long digit string that then fails to match its unit splits every way between the two digit runs),
`\d*\.?\d+` (the same shape; a `style` attribute is hostile input and a megabyte of digits costs a
million-squared steps), and `\s*[,\s]\s*` separators (a run of *n* spaces splits *n* ways, each split
backtracked). The number is `\d+(?:\.\d*)?|\.\d+`, the text is trimmed so no two whitespace runs are
adjacent, a value longer than any legal one is refused before the pattern runs
(`XfaMeasure.MaxTextLength`, `CssColor.MaxLength`, `HtmlToBuilderMapper.MaxLengthChars`, 64 each), and
each `Regex` carries a one-second timeout **and is interpreted, never `RegexOptions.Compiled`**. The
timeout is a belt behind the cap, and a timeout reads as "not found" — so the pattern must not be able
to spend the window on anything but the field: a compiled pattern's first match pays its JIT inside
that window, which a loaded host exceeds (the invoice date finder, compiled under a quarter of a
second, answers "no date" for `15 Oct 2026` in a whole-solution run whose test hosts share the
machine, and finds it alone a moment later — a wrong answer, not a flake). A capped field is a few
kilobytes, which the interpreter scans in microseconds, so compiling buys nothing the cap has not
bought; `NoCompiledRegexTests` scans `src/` for the option. **The cap is what a test asserts** (the same value one character
over and under it); a pattern's linearity has no answer a test can see, so review is the guard. The
auto-linker's URL pattern is bounded the same way (unbounded, it takes minutes on four million
characters). A new `Regex` over file text is read against this rule before it ships.

**Or it is a scan, held by a test to the pattern's answers**. An ambiguous media-range pattern takes
minutes on runs of spaces between three operators, so `TryMediaRange` is a hand scan, as are the HTML
engine's other untimed patterns, the XFA base64 strip, the spreadsheet `_xHHHH_` escape and the SVG
export's id references. Each is held to the answers of the pattern it stands for
(`HandScannedPatternTests`, `XfaWhiteSpaceStripTests`,
`SvgExportSingleDocumentTests.IdReferencePrefixing_AnswersAsTheReplacedPatternDid`). A search for
patterns greps `new Regex(` and target-typed `new(` both.

**A file's grammar comes from a specification or from two independent implementations, never from
recollection.** §12b of GAPS.md forbids a PDF/X rule from recall; a standard transcribed from memory
is wrong in ways its own round trip cannot show. The XFA readers' grammar comes from an open-source reader's element tables and the XFA form
designer's own output in a public test corpus, because the specification's public address refuses scripted
downloads, and GAPS.md §19d names the download that settles a disagreement between the two.

### Encryption

**A `ProjectReference` to `Tetitulz.Pdf.Encryption` decrypts nothing.** `PdfEncryptionOperations.Register()`
is the only writer of `PdfDecryptorHolder.Current`. The failure is silent: `PdfPageRenderer.EnsureDecryptedStream`
returns the still-encrypted bytes, the catalog never loads, `GetPageCount` answers 0, and a password
prompt built on that rejects the correct password forever. Only a host relying on the ambient holder
needs the call (the WPF viewer and `Demo` call `PdfDecryptor` directly); ask which mechanism
a surface uses.

**Read-only Base operations take a password directly** (`PdfFormReader.GetFields(Stream, string)`,
`PdfImageReplacer.FindImages(Stream, string)`) over `Base.Encryption.PdfDecryptedInput`, which decrypts
through the registered holder and needs no reference to Encryption.

**A reader that reaches the catalog through the trailer's `/Root` sees ciphertext and must refuse.**
`PdfDocument` loads no catalog for an encrypted file, so a reader going through `doc.Catalog` answers
empty; one that resolves `/Root` itself gets the dictionaries intact and every string and stream still
encrypted — an attachment lister would list ciphertext names and extract encrypted bytes as the file.
`PdfAttachmentManager` throws `PdfEncryptedDocumentException` without a password.

**A door that takes no password refuses as well**. Going through `doc.Catalog`, a reader answers
"none" for an encrypted document — even a permissions-only file anyone can open — and an export that
writes a fresh `/ID` makes its empty output look genuine. The form reader, XFA, form export, the
structure tree, bookmarks, page labels, layers, the initial view, the link, annotation and tagged-text
extractors and the usage-rights reader each refuse through `Base.Operations.EncryptedRead.Refuse`, the reader twin of
`EncryptedRevision`, naming the overload that takes a password where one exists; the usage-rights
reader says it in `LastError`, the channel all its failures use. **A converter is the same question with an empty output for its false answer**: the text, SVG and HTML export plugins, the HTML converter and the Office converters each refuse through `EncryptedRead.RefuseConversion` (public, since Base names no plugin in its `InternalsVisibleTo`) rather than write a valid .txt, page or .docx with nothing in it.

**Writers are a different question.** Whole-file rewrites (sanitize, optimize, linearize, redact,
template-fill) fail closed on an encrypted document: they cannot decrypt, and `Encryption → Base` cannot
be reversed. Two routes: `PdfEncryptedIncrementalUpdate` through each writer's `Plan` (keeps the
signature and the owner password; one appended revision per step) and `PdfEncryptedRoundTrip.Run`
(keeps neither; rewrites every byte). The incremental writers with no `Plan` yet refuse the same way,
naming the round trip, through one check, `Base.Operations.EncryptedRevision.Refuse`, which the shared
tail `PdfPageContentAppender.AppendRevision` also applies. Without the refusal each would write clear
text into an encrypted file. **A refusal is proved against a real encrypted file**: a writer that
loads pages first fails on an exception about pages an encrypted document cannot load, which looks
like a refusal and is one for the wrong reason.

**The round trip keeps the layout the operation wrote**. Writing every object back individually
under a classic table would undo the optimizer's object streams on the way out (`PDF32000_2008`:
23.5 MB from a 22.5 MB input). Both whole-file encryptors keep the input's layout, through `PdfCompressedWriteOptions.ObjectEncryptor`: each object stream encrypted whole under its
own number with its members in the clear inside (§7.5.7), the object `/Encrypt` names and the
cross-reference stream unencrypted (§7.6.2, §7.5.8.2), a trailer's `/AuthCode` carried into the
cross-reference stream's dictionary for the PDF MAC seal. One rule the seam imposes: **`PdfWriter`
tells an embedded file by its `/Type`**, so the compressed path declares an `/EF`-named stream
`/EmbeddedFile` before the writer sees it, or an attachments-only document leaves it in the clear.

**Both routes open with a `PdfDecryptionCredential`, and neither infers the output's handler.**
`ReuseOpenPassword` writes `/Standard`, `Recipients` writes `/Adobe.PubSec` to certificates the caller
supplies (a public-key document identifies its recipients without embedding them). **`ReuseOpenPassword`
is refused for a document with no password**: it would re-encrypt a certificate-protected file under an
*empty* password, and since both handlers write `/V 5 /CFM AESV3`, the scheme change would be invisible.
A policy that re-protects a document must never change which key opens it. **`KeepOriginal`** (opt-in,
the owner's decision) is the one policy that satisfies that by construction: the output is
written under the file key the credential resolved, with the original `/Encrypt` dictionary and first
`/ID` (Algorithm 2 mixes the identifier into RC4 and AES-128 keys, so an ID-less RC4 document is refused),
the encryptor being the decryptor's inverse (`PdfIncrementalEncryption.ForResolved`); both passwords, every
recipient and a PDF MAC salt survive.

**Signing an encrypted document travels to the crypto.** `PdfEncryptionOperations.Sign` reads the graph
from a decrypted copy and appends the signature revision encrypted under the document's own key; what
crosses is the signer's *plan* (`PdfSigner.Plan` stages the revision with fixed-width placeholders,
`Complete` fills them). `PdfSigner.Sign` refusing an encrypted document is correct. `Encryption → Signing`
is the one allowed edge: both core-ring, Signing references Core only, no new package enters either graph.

> **Verification needs two views, and one is the original file.** A decrypted copy is a different
> serialisation. `PdfSignatureVerifier` takes the graph from one document and every byte, offset and
> length from another; handing the copy as both reports a page rewritten after signing as intact.

> **§7.6.2: a signature's `/Contents` is not encrypted, and nothing else is exempt.** Running the
> cipher over it would decrypt every signature in an encrypted document to noise. The one
> classification is `Core.Document.SignatureDictionaryShape`.

**A PDF MAC token that is absent is "unprotected" only when none was required.** The trailer's
`/AuthCode` and the encryption dictionary are plain text, so a token is removed by deleting its entry,
and a verifier that reads "no entry" as "no PDF MAC" answers with the tamperer's own edit.
`PdfMacVerifier.WhyATokenIsRequired` asks what the rest of the document says. Under V≥5 the
requirement is permission bit 13 (ISO/TS 32004 Table 3), and `/Perms` is what ties `/P` to the file
key: the bit is read only when `PdfDecryptor.LastPermsValidation` is `OK`, and any other answer is
itself reported, since flipping the bit — or removing `/Perms` with it — is what removing a token
unseen needs. Under the public-key handler the bit is in the recipient's permissions (Table 4), the
four bytes after the seed in the envelope: every recipient blob is hashed into the file key, so the
envelope that opened is the one the document was encrypted with, and its word
(`ResolvedEncryption.EnvelopePermissions`) decides whatever the dictionary now says. Below `/V 5`
nothing authenticates `/P`, the bit had no meaning and producers cleared it freely, so there a token
is held required only where the dictionary also carries a 32-byte `/KDFSalt` (Table 2); removing the
token and the salt from such a document is not detected, and the README says so. **That is why no
token is written there.** Table 5: "If present, the value of the V entry in the document's Encrypt
dictionary shall be at least 5." `PdfEncryptor.CanCarryPdfMac` reads the rule off the one algorithm
table; the password encryptor, the public-key one and `PdfEncryptedRoundTrip.TryDeriveParameters`
ask it, and `EnablePdfMac` with RC4 or AES-128 — the default algorithm — is an `ArgumentException`
naming both. A document another producer wrote that way is still handled as what it is: its token
verifies, an appended revision reseals it (a stale token reads as tampering), `KeepOriginal` keeps
it; a re-encryption under a new key would have to write the token again or drop it, so it is
refused with the reason. The fixtures for those documents come through an internal door with the
refusal off, as the signer's non-conforming fixtures do. An `/AuthCode` that is not a direct dictionary
(Table 5) is a token placed where no reader looks, so it is invalid, not absent.

**What a PDF MAC token's gap holds is read, not sized** — the rule the signature verifier already
keeps. A token rewritten as a literal string takes half the room its hex form was given; the rest,
which no digest covers, can close `/AuthCode` and add trailer keys the parser obeys, with the digest,
the coverage and the HMAC unchanged. So a standalone token's `/ByteRange` starts at the file's first
byte and leaves out exactly `<`, the token's hex digits and `>` (ISO/TS 32004 Table 6,
`StandalonePdfMacSeal.GapHoldsOnlyToken`), and a token attached to a signature leaves out only that
signature's `/Contents` (§12.8.1, `SignatureGapHoldsOnlyContents` over
`SignedByteRanges.GapHoldsOnlyContents`) — `PdfMacVerificationTests`.

**An encrypted file keeps nothing in clear that the standard does not exempt.** The writer encrypts
strings and streams inside indirect objects only, so a direct dictionary, array or string in the
trailer — a direct `/Info` most often — would be written as plain text, in the compressed layout into
the cross-reference stream's dictionary; both encryptors make each such value (but `/ID`, which §7.6.2
exempts) an object of its own first (`DirectTrailerValueEncryptionTests`). **`/EncryptMetadata false`
exempts the document-level metadata stream alone** (ISO 32000-2 Table 20): an encryptor names it to
the writer through `IPdfDocumentMetadataEncryptor`, and every other `/Type /Metadata` stream — a
page's, an image's, a font's — is encrypted; the decryptor, which in a compressed file cannot see the
catalog until the object streams are open, holds back each candidate metadata stream until it can,
then leaves only the catalog's `/Metadata` as it is (`EncryptMetadataFalseScopeTests`). Below AES-128
the entry means nothing (Table 21: it is read only under `/V` 4 and 5) and readers decrypt the
metadata anyway, so both encryptors refuse `EncryptMetadata = false` there rather than write a file
whose XMP no reader can read. **An embedded
file need not say `/Type /EmbeddedFile`** (the entry is optional): `EmbeddedFileStreams.ReferencedBy`
reads a file specification's `/EF` through a reference and the related files its `/RF` lists, for the
encryptor, the decryptor, `KeepOriginal` and the linearizer alike (`EncryptedAttachmentsTests`).

**Do not "fix" layering with a `ProjectReference`**: it would drag BouncyCastle into two more package
graphs, the design GAPS.md §5a rejected. Encryption is core-ring, so nothing in the ring model forbids
the edge; this rule is the only thing that does.

**A guard that answers a `bool` about a hostname has not protected the connection.** `IsSafeHost`
resolves, classifies, discards the addresses and answers; a caller that then fetches by hostname
resolves again, and a short TTL answers public to the check and `169.254.169.254` to the connection.
Use `TryResolveSafeAddresses` and connect to what it returns: `GuardedHttpConnect.ConnectAsync` as a
`SocketsHttpHandler.ConnectCallback` on net10.0. The netstandard2.0 build is what a .NET 5–9 host
loads (it cannot load net10.0 assets), and that runtime has the hook: `GuardedHttpFetch.CreateHandler`
finds `SocketsHttpHandler` by reflection and installs the same vetted connection as a callback compiled
from an expression (`GuardedHttpConnect.TryCreateGuardedSocketsHandler`), so the residual is .NET
Framework's alone, not every netstandard2.0 host's. .NET Framework has
no such hook (`ServicePoint.BindIPEndPointDelegate` binds the *local* endpoint), so
the handler there is the library's own transport, `GuardedHttpTransport`: the vetted connection,
`SslStream` authenticated with the URL's host name (SNI cannot be an address, RFC 6066), one HTTP/1.1
request with `Connection: close`, the response under ceilings and RFC 9112's refusals, every read and
write cancellable by closing the socket, which a Framework socket read needs. What it does not carry —
credentials, an explicit proxy, a URL the system proxy applies to, where the proxy resolves the host —
goes to `HttpClientHandler` with the pre-check alone, and GAPS.md §17a says so. **Its framing tests
run in memory, not over loopback**: an HTTP-aware filter on the loopback interface (an antivirus web
shield) rewrites a malformed chunked response in flight, and the tests would assert about the filter.
The choice of the transport is the netstandard2.0 leg's, which no net10.0 test reaches; a probe on
.NET Framework 4.8 checks it. **A security comment is read as a
promise; write the limit, not the hope.**

**A browser has more doors than its request route, so it is guarded at its proxy.** Playwright's
`RouteAsync` sees what a page's renderer requests. Run against Chromium with a loopback listener, a
route that aborts every private host still let two things through: `navigator.sendBeacon`, which the
browser process sends, and a WebSocket opened inside a worker, which `RouteWebSocketAsync` does not
reach either; and WebRTC sent a STUN request over UDP to whatever address the page named as an ICE
server. The one door all of a browser's TCP passes is its proxy setting, so the browser is launched
behind `GuardedSocksProxy`: a SOCKS5 listener on loopback for the one conversion, which is handed
the host *name* (RFC 1928 address type 3), resolves it once through
`PrivateAddressGuard.TryResolveSafeAddresses`, and dials one of the addresses that came back —
CONNECT only, at most `MaxConnections` at once, the handshake under a timeout. `<-loopback>` in the
bypass list is what sends `127.0.0.1` to the proxy instead of around it. Chromium is launched with
`--force-webrtc-ip-handling-policy=disable_non_proxied_udp`.
The route stays for what a proxy cannot decide (`file:`, other schemes) and to refuse a private host
early by name; a WebSocket keeps its own interception (held to the same host rule by
`PlaywrightRenderer.IsWebSocketAllowed`), which needs `Microsoft.Playwright` 1.64.1 — up to 1.62.0
the .NET client reads a close code the browser may omit, and one routed socket fails every later
call. A service worker's fetches pass no page route (the context is opened with
`ServiceWorkerPolicy.Block`), and a download writes a file nobody asked for
(`AcceptDownloads = false`). **The proxy and the route bound what an intact renderer does**; a renderer a
page compromised acts with this process's file and network access unless Chromium's operating-system sandbox
holds it, so Chromium is launched sandboxed (`HtmlToPdfOptions.ChromiumSandbox`, on by default;
`Chromium_IsLaunchedInItsSandbox_UnlessTurnedOff`), off only where it cannot start so — a Linux host running
as root, a container without user namespaces. **The proof is the probe, not the rule**: a page that tries every
channel at a loopback listener, rendered with the guard off (each channel must arrive — the control)
and on (none may). The unit tests speak SOCKS5 to the proxy and need no browser; the probe needs
one, so it is `tools/browser-guard-probe`, run by hand when the guard changes (GAPS.md §17d has
what it covers).
A `file:` URL is admitted by `Core.IO.DirectoryContainment.IsInside`,
the managed engine's own rule, so the two engines admit the same files: both paths made absolute, the
directory's trailing separator (`C:\base-evil` starts with `C:\base`), and case as the file system's —
ignored everywhere, it admits on Linux a sibling directory whose name differs only in case.

**A credential for a fetch to an attacker-chosen URL rides it only when host-scoped**. AIA, CRL
and OCSP URLs come out of the chain being verified; a plain `NetworkCredential` (or
`DefaultNetworkCredentials`, the process identity) on their client would answer any host's 401, and a
redirect walk would carry it across hosts.
`RevocationCredentials` passes a credential to the revocation client only when it is a `CredentialCache`,
which binds each credential to a host, scheme and authentication type. The TSA request keeps the
caller's credentials: its URL is the one trusted host. A seed value's `/TimeStamp /URL` is not — it names a
host of the document author's choosing, asked from the signing host — so it is asked only when the caller
sets `UseDocumentTimestampAuthority`, and then as an OCSP request is (`DssBuilder.PostGuardedCore`): the
connection to an address the guard passed, host-scoped credentials, no redirect. Without the switch a seed
that requires a timestamp is refused unless the caller names a server. A seed's `/DigestMethod` is held to
the verifier's weak-digest rule the same way: SHA-1 offered first is passed over, and a required list of
SHA-1 alone is refused rather than signed under a collision-broken hash over the author's own prefix.

**SASLprep belongs to the V≥5 key derivation, on both sides, and nowhere else**.
§7.6.4.3.3 normalises a revision-6 password (RFC 4013) before hashing. Normalising on one side only
writes an AES-256 document, for a password with a no-break space, that its own password cannot open;
normalising at a public door, unconditionally, refuses a revision 2–4 password with the same character
(raw Latin-1 bytes under Algorithm 2). So it happens at the one derivation site that knows the
revision, on each side: `PdfEncryptor.ComputeAes256Keys` and `PdfDecryptor.ValidateAndDeriveAes256Key`.
ASCII passwords hide both mistakes. **A property that must hold on both
sides of a boundary lives at the boundary's one crossing.**

**The public-key handler is checked against the standard, not against itself**.
Table 27 has the enveloped content as the 20-byte seed then 4 bytes of permissions, and §7.6.5.3 step c
adds `FF FF FF FF` to the key digest when the metadata is left plaintext. An encryptor and decryptor
that agree with each other on the opposite order, or both leave step c out, pass every round trip and
write documents nothing else opens. The layout is one set of constants in the decryptor (`SeedOffset`, `PermissionsOffset`) and the digest
one method (`DeriveEncryptionKey`), read by the encryptor; `/EncryptMetadata` is read from the crypt
filter first, then `/Encrypt`, because an independent implementation writes it where the standard handler's goes. The test opens
the envelope with the platform's CMS and derives the key from the standard's text. **When a format has
two sides in one tree, a round trip is a consistency check, not a conformance check.**

**A recipient key that stays on a device goes through `IExternalDecryptor`**, the decrypt-side mirror of
`IExternalSigner`. The seam is the CMS envelope, not the RSA primitive, so only the seed crosses it; and
**the certificate match happens before the device is touched**, because `/Recipients` is
attacker-controlled and a card asked to open envelopes not addressed to it prompts for a PIN each time.
`GetRecipientCertificate` is on the interface for that.

**The accessibility writers are not in the fail-closed list.** Every structure-tree operation, the
tagger, the artifact marker, the reading-order fixer, the font remediator and the XMP writer have a
`Plan`, and all nine remediation steps run against an encrypted document through
`RemediationOptions.EncryptedRevisionWriter`. The dependency is inverted, not added: `Accessibility`
references no crypto and contains no occurrence of "password".

### Editing under a signature

`PdfImageReplacer` supersedes an image XObject in place (same object number, new samples) through
`IncrementalUpdateWriter`, so the original bytes stay byte-identical and a signature over them still
verifies. `PdfRedactor` is the opposite trade. **Preserve the signature or remove the data, never both**:
an incremental update cannot destroy the old pixels, and `PdfRevisions` hands them back.

`PdfRevisions` finds boundaries physically: a `%%EOF` preceded within 64 bytes by a `startxref` that
parses and points strictly backwards. **A linearized file's first-page terminator is recognised by
structure, not by its integer** (Annex F.3.4 says the integer "shall be ignored"): the `/Linearized`
first object within `LinearizationDictionaryWindowBytes` followed by a cross-reference section. Rejecting
offset 0 alone would let a nonzero value mint a phantom revision whose extract is a truncated file.

**Redaction and shared Form XObjects.** A redacted clone is substituted at the invocation on the named
page; a shared form's original survives for the pages still drawing it, so the content stays in the
file. `PdfRedactionEngine.LastSharedFormWarning` names them; a field nothing carries out would tell the
caller nothing.

**LTV data is only ever added, and only in its own shapes**. An allow-list of every object
reachable from `/DSS` would let a revision after a P=1 certification rewrite the page's content
stream, list it under `/Certs`, and pass as compliant. `DocMdpAnalyzer`'s allow-list is by position (Table 261's keys, digest-keyed `/VRI`, Table 263's keys,
a stream's own keys), and a changed object is LTV only when its old version had the same shape.

**A CRL vouches only within its scope**. Reading a certificate's absence from an issuer-signed,
current CRL as `Good` skips three of RFC 5280 §6.3.3's steps: not a delta, not a
partition the certificate is outside (CA-only, user-only, a distribution point it does not name, a
reasons subset), not indirect. A CA publishing a CA-only list beside its end-entity list has a genuine
document that names no end-entity certificate ever. `Cms.CrlScope.WhyItCannotVouchFor` is asked by the
verifier (absence is `Unknown`) and by the LTV embedder (refused unless it lists the subject). The chain walk does not check a self-signed root against the
leaf's CRL: RFC 5280 §6.1 validates the path below a trust anchor, not the anchor.

**An OCSP response is evidence for the certificate its `SingleResponse` names**. A verified
responder and fresh entries say nothing until one entry is shown to be about the certificate the
response was fetched for. `Cms.OcspSingleResponse.Find`
is asked on both sides, and `WhyADelegatedResponderCannotSign` checks the delegated responder's validity
period at the time each relies on the response. **The embedder must never write what the verifier will
not read.**

**One policy on both sides: freshness, the responder, the timestamp's algorithms**. Each copy
of a policy on one side lets the embedder write what default verification refuses: an embedder that
never ages a CRL with no `nextUpdate` (the verifier caps one at 365 days) or never reads `producedAt`;
one that takes the first embedded certificate whose key verifies as the responder, or judges every
entry's freshness, so a stale entry about another certificate refuses current evidence about this one;
one that applies no weak-algorithm policy to the timestamp, so a SHA-1 token is embedded in silence and
then rejected. `Cms.RevocationFreshness.WhyNotCurrent`, `OcspSingleResponse.WhyTheResponderCannotVouch`
and `WeakAlgorithmPolicy.DetectWeakTimestamp` are each asked on both sides. The embedder passes the
verifier's defaults and may be **stricter** (seven days for an OCSP response with no `nextUpdate`),
never looser. A weak token is still embedded and reported (`TsaClient.LastTokenWeakAlgorithm`):
refusing it would stop legacy TSAs from signing. A broken one (MD5, MD2) is refused, since
verification refuses it under every policy. The test that holds the line judges each fixture through
both sides in one test (`RevocationPolicyParityTests`); a test of either side alone could not see the
two drift apart. The age cap compares differences, so a `MaxRevocationDataAge` of `TimeSpan.MaxValue`
does not overflow `DateTime.Subtract`.

**The signer is told what default verification refuses, in the verifier's words.** A signature made
with a TLS certificate, an expired one, a 1024-bit key or a SHA-1 digest is written — a verifier
configured otherwise accepts it, and a refusal at signing would be this library deciding what a
relying party requires — and the verifier's defaults then report it Invalid, so the first to hear of
it is whoever receives the document. `PdfSigner.LastSignerWarning` says it where the CMS is made
(`BuildSignatureValue`, which `Sign` and `Plan`/`Complete` both reach). The reasons are not a second
copy of the policy: the chain walk's leaf checks are one function,
`CertificateChainValidator.WhyTheLeafIsRefused`, which `Resolve` calls for its first step and the
signer calls for its own certificate under `new SignatureVerificationOptions()`, and SHA-1 is
`WeakAlgorithmPolicy.DetectWeakDigest` for a SignerInfo read and a digest to sign with alike. The
tests sign, verify the result, and hold the two texts equal. Trust is not judged — the signer has no
anchors — and a deferred or counter signature is not judged at all (GAPS.md §17d).

**A chain is held to more than signatures and dates**. `Cms.CertificateChainValidator` walks from
the leaf to an anchor and asks four things a chain that only verifies link by link does not answer.
*Where the issuer is*: among the CMS's certificates and then the anchors — RFC 5652 lets a signer
leave the root out, and sought in the bag alone such a chain never anchored (an anchor the bag also
carries is one candidate, since tried twice its second try outranks the refusal the first earned).
*What the signer's certificate is for*: an extended key usage that names purposes, none of them one
a document is signed under (`Core.Constants.PdfEkuOids.PermitsDocumentSigning`: any, e-mail
protection, code signing, RFC 9336's document signing, Adobe's and Microsoft's), is `Invalid` —
otherwise any certificate issued for a web server signs a document under a root that issues both;
`RequireSignerKeyPurpose` is the opt-out, and a TSA's leaf is held to its own purpose by its
verifier. The certificate picker asks the same function (`CertStoreLookup.PermitsDocumentSigning`),
so it offers no certificate the verifier refuses — a personal store on a developer's machine held
three hundred TLS certificates with private keys, left by a debugging proxy. *Whether the certificate may
be used by a validator that does not understand it*: a critical extension outside
`UnderstoodCriticalExtensions` refuses the certificate (RFC 5280 §4.2), an anchor excepted. *Whose
names a CA may issue*: a CA's name constraints are held to every certificate on the path below it,
by BouncyCastle's `PkixNameConstraintValidator`, a self-issued intermediate passed over and the leaf
never.

**Revocation is checked along the path the walk anchored.** A chain built apart for the revocation
check follows whichever same-named certificate's key verifies first, and the CMS bag decides that order:
a certificate carrying the intermediate's name and public key under the attacker's own root, placed first,
verifies the leaf as well as the real one. The walk anchored through the real intermediate while the
separate chain followed the copy and asked the attacker's root, so the anchor's CRL listing the
intermediate was never consulted and a revoked chain read as unknown. `Resolve(…, out trustedPath)` hands
back the anchored path, continued above an anchor that is not a self-signed root by `RevocationPath` (a
relying party may anchor a leaf or an intermediate, whose status is still its issuer's to state); signers
and timestamp authorities alike. `BuildChain`, kept for an untrusted signer and the LTV embedder, follows no
issuer whose key did not verify: revocation data checked under a decoy says nothing about the certificate
(`RevokedCertificateChainStatusTests`, `TimestampAuthorityCurrencyTests`, `CostlyPublicKeyTests`).

**A signature's `/VRI` entry is read beside the document-wide lists, never instead of them**
(`RevocationChecker.Sources`). Read alone, it let a later revision add `/VRI << /<hash> << >> >>` and turn a
revoked signer into "not checked", which passes `IsFullyTrusted`. Evidence is matched to its certificate
already — an OCSP response by its `SingleResponse`, a CRL by issuer, signature, scope and serial — so a list
kept for another signature cannot vouch for this one, and a listing anywhere is decisive
(`VriBesideDocumentWideListsTests`).

**A timestamp's authority is judged now as well as then**. A token's GenTime is the TSA's own
assertion, so a chain judged only at GenTime is judged where the token says to look: the key of a
TSA certificate that has since expired — no longer guarded, on no revocation list — signs a token
with any time in its old validity period and back-dates a signature made today. A token moves the
assessment time only when the TSA certificate is not revoked (`AuthorityIsRevoked`: the `/DSS`, and
online when asked) and its chain is valid at the current time
(`TsaVerificationResult.IsAuthorityCurrent`) or a later document timestamp that may itself vouch
covers the token (`KeepRenewed`, `VerificationContext.AuthorityMayVouch`) — the renewal chain of
PAdES-LTA. `RequireCurrentTimestampAuthority` is the opt-out for an archive never renewed. A test's
token carries its own signing-time attribute at its GenTime, as a TSA's does; left to the generator
it is the moment the test runs, and a token under an expired certificate fails as a CMS for the
fixture's reason.

**A hash of a prefix says nothing of what follows it, in the revision carrying the proof too.** The
blockchain verifier re-hashed the notarized prefix and required nothing after the proof's revision, so a
forger kept the prefix and replaced the genuine tail with one holding the same proof and a rewritten page:
hash true, nothing following. `DocMdpAnalyzer.ChangesBesidesCatalogEntry` holds that revision to the one
entry it exists to write. **A permitted change is no error, and is said.** The published evil-annotation
and sneaky-signature attacks use what Table 257 allows: an annotation at level 3, and at level 2 a new
signature field's appearance, standing over the signed text. They are not violations and the signature stays
valid, so the analysis keeps them in `PermittedChanges` and the verifier names them in a warning; a
relying party cannot ask about what it was never told.

**What a `/ByteRange` gap holds is read, not sized.** Signature wrapping leaves the two signed blocks
byte for byte and moves the second along the file: the CMS verifies, the range reaches the end, no later
revision exists to classify. Its room comes from the gap, which no digest covers, and the check on the
gap compared lengths against `2 × raw + 2` — yet a padded `/Contents` written as a literal string takes
one byte a byte, so half the allowance is free for a new signature dictionary (naming the moved range),
new objects and the cross-reference table the moved block's `startxref` now lands on.
`SignedByteRanges.GapHoldsOnlyContents` reads the gap and requires the hex form of this signature's own
`/Contents`, delimiters optional; a signed revision's gap is otherwise what §12.8.1 says it is.

**A redaction removes what a page says about its text as well as the glyphs.** It returned success with
the secret still in the file for sixteen of thirty-six places a page can hold it, found by reading a
marker back out of the whole output rather than by extracting text: `/ActualText` (which a text extractor
prefers to the glyphs, so it hands the removed text straight back), a structure element's alternatives,
a thumbnail (a picture of the page), an image "removed whole" whose `/SMask` kept its shape, a string
operand no operator reads. The rules bias toward removal, as the glyph test does, because which alternative
described the removed text cannot be told from the file: every one on a redacted page goes. A tiling
pattern or soft mask that draws text is refused, not cleaned: it is one stream for every tile, no region
can be placed on it, and cleaning it would change the page outside the region. **An image there refuses
only a paint a region meets** (`RefuseCoveredImageSource`): fills and masks built of images are ordinary, so
one away from every region is left, but a cover box over one hid pixels the file kept, the one leak a cover
box made without saying so. Which pattern each colour goes through and which mask is in force is graphics
state, kept across `q`/`Q` and handed to a form; a cell or group is read once, nested patterns, masks and
forms included. The limits are stated in
the class summary so a caller can read them; `RedactionLeftBehindTests` pins one as a limit, not a promise.
XFA packets go whole because nothing in them says where a page draws a value. **What other routes still reach
goes too**: the structure tree keeps an element of a redacted page, and through it the element's `/AF` (a
formula's or a table's source — an associated file is the source, as the page's own is), and an object reference
to a dropped annotation, so a dropped file attachment's `/FS` and any dropped annotation's `/AF` are scrubbed
with its words. A parent field whose every widget fell in a region leaves `/Fields`, but the calculation order
and a reset or submit action's `/Fields` still name it, so it is scrubbed as a dropped widget is (value,
appearance, words, name) and leaves `/CO`.

**A region takes a glyph where the page draws it.** The engine keeps its own walk of the text state, and
each thing that walk did not know left text beside a region it was named in, found by redacting small cells
of a rendered page and rendering the result with the cover boxes taken out: `Q` restored the text matrices
(they are not graphics state, so the text after it was looked for where the text before it started); a form
started from a blank state, though it inherits the font, size, spacing and rise of the stream drawing it; a
font set through `gs` was not seen; a vertical run advanced along x. A Type 3 glyph is a program: its advance
goes through the font matrix and prefers the procedure's `d0`, and it paints wherever the procedure says, so
it is scanned (`RedactionType3Font`) and what a scan cannot place makes it go with any region. An embedded
font's glyph can also be drawn far from its advance box; reading every outline for that costs more than the
hostile file it answers, so it is a stated limit, with the cover box over it. **The cover boxes were drawn in
whatever state the page ended in**: a matrix never restored moved them off what they cover, a clip hid them,
`ca 0` made them clear, an unclosed hidden layer took them with it, all with vector art beneath that only they
cover. The page is now bracketed, what it leaves open is closed, and a stray `Q` is dropped so it cannot take
the bracket; this renderer forgives the stray `Q` by itself, so the test reads the operators as well as the pixels.

**A sanitized document keeps no attachment and no route out, wherever it was put.** The catalog's name tree and
`/AF` were cleaned, and otherwise a stream was blanked only if it named itself `/Type /EmbeddedFile`, which is
optional: a file associated with a page, a form or a structure element, with that key left off, went through whole,
to a gateway that had sanitized and then trusted the result. So every `/EF` and `/RF` in any object goes, found by a
walk of everything left. The same walk takes what makes a reader fetch on opening: a stream with its data elsewhere,
a reference XObject, a URL file specification. A form submission's address is passed over, being its own option's.

**A search-and-redact reads its result back.** The search runs over the layout, whose lines are rows of
baselines, so rotated or mirrored text comes apart and two runs overlapping interleave: nothing matches, the
call returned "0 matches", and the word stayed on the page. A caller told that would believe it. The plain
extractor reads in drawing order whatever the direction, so it reads each page of the result, and a match
within one of its lines refuses with nothing written. Locating such text would be a second layout; a refusal
that says to redact by region is the honest answer until there is one.

**A rewrite that removes annotations removes what they said.** The optimizer's `RemoveAnnotations` promises to
discard field values, comments and link targets; taking the pages' `/Annots` away discards none of them, since
the catalog's `/AcroForm` keeps every field and widget with its value and appearance, and a structure element's
object reference keeps a comment's words. So every annotation a page listed and every node of the field tree
is scrubbed where it stands (value, appearance, words, author, file, action, destination), and `/AcroForm`
leaves the catalog (`Optimize_RemoveAnnotations_DiscardsFormValuesAndCommentsReachedOtherwise`).

**A search-and-redact cleans the document's own text as well.** A caller asking for every occurrence of a
word expects it gone from the title, the bookmarks and the metadata, where no region can be placed. The text
redactor hands the engine a function that removes each match from the information dictionary's strings, the
outline's titles and the XMP packet's text and attribute values, before the one full rewrite, so no old copy
is written. It searches again until nothing matches, since a removal can join two halves into another match,
and empties a string still matching after `MaxCleaningPasses`: a file can nest a word in itself so that each
pass removes one level. A packet that cannot be read, parsed or bounded goes whole, an outline too deep to walk
refuses the call, and a matching comment goes whole, since what is left of one may not be a comment. A
document whose pages lose nothing is still rewritten when its own text loses something.

**A later revision is judged by what the signed revision held.** The classifier's first reading of a
changed object was the new dictionary's keys, and those are the forger's to choose: a page's content
stream rewritten with `/Type /DocTimeStamp` in its dictionary passed as a permitted timestamp at P=1,
`/Subtype /X /Rect [0 0 1 1]` passed as an annotation at P=3 — the level every approval signature is
asked at, so a self-signed decoy reaching the end of the file then left the genuine signature valid —
and no key changes what a reader draws from a stream's bytes. So a stream that existed changes at no
level (nothing a level permits rewrites one: filling a field adds its appearance), and an object that
existed is an annotation only if a page listed it in the signed revision. **A change need write no
object.** The later objects are found by their offsets, so a revision of nothing but a cross-reference
entry pointing at an earlier version still in the signed bytes, or into a signed object stream, or a
trailer naming a catalog the signed bytes hold unused, gave the classifier nothing to look at; each
current entry is now compared with the signed revision's own table. That table is read from the raw
prefix, which an encrypted file's is too: a decrypted copy's offsets are its own. A deletion also
needs no free entry: below P=3 `/Annots` keeps every entry, and a new one must be an object written
later, so its classification runs; `/Fields` only grows. A signature field is admitted on its `/FT`,
so an action on it, or a subtype that makes it another annotation, is refused. Choosing the
certification by the first field listed let a later signature, placed first in `/Fields` and naming P=3,
stand in for a certification of P=1 when no `/Perms` named it; the one that covers least is the first.

**What a redraw reads does not change, and a comment does not run.** At level 3 an annotation may be added, and one carrying `/AA` runs a script when its page opens, which can rewrite what every signed field shows; so an added annotation only links. A form's `/DR` may gain a font a filled field needs, but a name rebound to another font draws a signed value in glyphs that say something else once `/NeedAppearances` asks for a redraw, so it only gains names; `/DA` and a choice field's `/Opt` are what that redraw reads, and are structural changes. `/NeedAppearances` itself is permitted and named in a warning.
A signed field's `/AP` was a fill-in key too, so a later revision could redraw the visible signature under another
name and date with every digest still good: a field holding a signature, or a widget below one, now takes no change.

**A signature's dictionary is among the bytes it signs.** The verifier read `/ByteRange` and `/Contents` from the
dictionary in force and proved the digest, and nothing said that dictionary was the one signed: its gap check reads
the gap, which holds the same hex whichever dictionary names it. So a revision adding a field whose value copies a
genuine signature's two entries, with a `/Reason`, `/Name`, `/M` and field name of its own, got `IsValid` under the real
signer's name, and added signature fields are what level 3 permits. The object in force must now start ahead of
the gap and not have ended by it. A second field naming the signature object itself is refused by the classifier, and
where two certifications cover the same bytes the stricter counts, so a copy listed first cannot name a kinder level.

**A field lock covers what a reader shows.** Only `/V` was compared, and only for the first field of a
name: a field added under the locked name and listed first, holding the old value, hid a real change,
and a new `/AP` replaced what the reader draws under an unchanged value. The comparison now takes the
value and the appearance it and its widgets name, every field of the name joined, and a name in
`/Include` locks the fields below it too, whose values are the ones that change.

**A proof that travels with the document names its own signer.** The blockchain proof is stored in
the PDF, so whoever writes the file writes the proof: anyone can anchor a hash from an address of
their own and name that address. `BlockchainSignatureProvider.Judge` holds the transaction's sender
to the proof's address, which shows the proof is consistent, and to
`BlockchainParameters.ExpectedSignerAddress` when the caller sets one, which is the only statement
about *who*; without it the result carries a warning saying that the document was anchored, not by
whom. A transaction the endpoint knows but that is in no block has recorded nothing and is not found,
and an empty document hash matches nothing. The judgement is a pure function of what the endpoint
returned, so its tests need no chain.

**A writer that supersedes the catalog asks for its number**. An inline catalog has no object
number to supersede, so a writer that does not ask emits `0 0 obj`; the revision writer skips object 0,
so the new tree is appended with nothing pointing to it and the call returns as if it worked. The
signing writers ask `PdfSigner.CatalogObjectNumberOrRefuse`; Base's — the page labels, the portfolio
converter, the field builder, the initial-view writer, both named-destination writers, the attachment
manager, the bookmarks manager and the flattener — ask `Core.Writing.CatalogObject.NumberOrRefuse`,
so each refuses in the same words.
**A fix to one writer is a finding about every writer of the same shape.** Likewise a Table
237/238 `/Ff` is a flag word, read as `PdfNumber.FlagWord`, not through `(int)DoubleValue`.

**A rewrite cleans the catalog a reader takes.** A sanitizer that looks for the catalog only through a
`/Root` referring to a dictionary misses an inline one, which pdf.js and MuPDF read: every catalog-level
removal is skipped and the catalog is copied through whole, its open action's script, the document's scripts
and its attachment with it; a `/Root` naming a reference to a reference keeps its XFA scripts and a `file:`
base. So before anything is cleaned, `PdfSanitizer` points the trailer's `/Root` and `/Info` at the
dictionary a reader takes: an inline one becomes an object of its own, numbered past every other, and a
`/Root` naming no dictionary is pointed at the catalog the parser settled on (`SanitizerTrailerCatalogTests`).
**Every object the sanitizer writes has been cleaned.** Two walks decide its output: the cleaning walk
resolves a reference through the parser, which stops after `PdfLimits.MaxIndirectReferenceChain` hops,
and the walk deciding what is written follows object numbers to the end of any chain — so an action one
hop past the limit, or an `/Info` named through a reference to a reference, would be kept without being
cleaned. The cleaning walk's visited set is shared, and once the kept set is known, whatever it holds that
the cleaning walk missed is cleaned and the kept set recomputed; cleaning only removes edges, so one pass
suffices (`SanitizerReferenceChainTests`).

---

## Architecture

### The reference invariant is acyclicity, not direction

The project-reference graph is a DAG with `Core` at the bottom: Core (object model, parsing, filters,
writing primitives, signing crypto; no SkiaSharp, no UI), Base (operations and rendering; may use
SkiaSharp), features (each depending only on Core and Base, with two sanctioned exceptions).

1. **`Builder` never references another feature.** It owns the code-first document model, so `DocxToPdf`
   and `HtmlToPdf` may reference it.
2. **`Conformance` never references a feature.** `Accessibility → Plugins.Conformance` is sanctioned.
3. **`PostScript` never references a feature.** `Printing`, in `tetitulz.pdf-apps`, references it: a print
   job is PostScript, and the virtual printer converts it through the plugin rather than duplicating it.
4. **`Core`, `Base`, `Signing` and `Encryption` reference no plugin.**
5. **Plugin → plugin is banned outright.** There is always a downward home for shared code:
   `Plugins.PostScript` and `Plugins.CMap` share `Core.Fonts.PredefinedCMapRegistry`.

Plugin → feature is not banned: `Plugins.Docx → DocxToPdf` and `Plugins.Html → HtmlToPdf` are the
adapters that expose the converters through `IImportPlugin`/`IExportPlugin`, and neither converter
references a plugin. The exceptions are the targets of rules 2 and 3 — `Conformance` and `PostScript`
are referenced by a feature, so a reference back would close a cycle. Enforced by
`AssemblyLayeringTests`, which parses every `src/**/*.csproj`.

### What makes something a plugin rather than part of Base

**A plugin earns its place when it keeps a dependency out of the core package graph**: Tesseract,
Nethereum, Jint, a 10 600-line interpreter referencing Core only. The best shape is `Plugins.CMap`,
mechanism in Core and payload in the plugin. A plugin that isolates nothing is an adapter
(`Renderers`, `Text`, `Svg`, `SignatureVerify` give hosts string-keyed discovery), not evidence a
capability belongs in the plugin ring. **Size is not the criterion**: `Jbig2Decoder` stays in Core.
**`Base` must not reference `Jint`**; the contract is `Base/Scripting/IJavaScriptPlugin.cs`
and the one dispatch point `PluginJavaScriptRunner`. A plugin contract lives where the types it mentions
allow: most in `Core/Plugins/`, the signing ones in `Tetitulz.Pdf.Signing.Plugins`, `IJavaScriptPlugin`
in `Base.Scripting`.

**A document's own JavaScript runs only when the host registers the engine by name.**
`JintJavaScriptPlugin` carries `PdfPluginExplicitRegistrationAttribute`, and `RegisterFromAssembly`,
`RegisterFromLoadedAssemblies` and `RegisterFromDirectory` pass such a type over: a host that
references the package for one feature, or discovers plugins for OCR or rendering, must not thereby
run what every document it opens brings. The failure is silent — no script runs, as without the
package — which is the safe side; `ScriptExposureTests` holds both halves. **A web page's own
JavaScript is the same case**: `JintHtmlScriptPlugin` carries the attribute too, so discovering
plugins for HTML conversion does not run the scripts a fetched page brings. **The object model is held
to Acrobat's own answers, never to recollection**: ISO 32000 defines the JavaScript action and leaves
the objects to Adobe's reference, and the `AF*` functions are in no published one, so
`AcrobatObjectModel.js` is matched against probe rounds run by hand in Acrobat
([`Fixtures/Acrobat/README.md`](tests/Tetitulz.Pdf.Plugins.JavaScript.Tests/Fixtures/Acrobat/README.md)):
an answers file is the saved probe document's `results` field byte for byte, and every difference the
engine keeps is a listed choice with its reason (GAPS.md §25a).

### Which side owns which PDF/UA check

- `Plugins.Conformance`'s `PdfUaChecker` owns UA00–UA09, UA25–UA29, UA50+ and its own UA99;
  `Accessibility`'s `PdfDeepAccessibilityChecker` owns UA10–UA24 and UA30, plus its own UA00, UA06 and
  UA99. New deep codes from UA30 up (UA30–UA49), new plugin codes from UA50 up. PDF/UA-2's checkers use
  `UA2-<clause>` codes and never reuse the range.
- The font pair is numbered oppositely (deep UA20 Unicode / UA21 embedding; plugin UA25 embedding /
  UA26 Unicode) and is not de-duplicated. UA16 (leaf) and UA30 (subtree) stay two checks.
- `PdfAccessibility.Audit` drops exactly UA28, UA29 and UA50 (superseded by deep checks); the plugin's
  UA99 maps to `Info`, since it says a check did not run.
- **Both halves read `/P` through `PdfPermissionValue.FromNumber`**: UA06 is computed on both sides and
  merged, so the one step that can disagree is shared, not asserted to agree in a comment.
- **All three checkers read `/ViewerPreferences /DisplayDocTitle` through `PdfViewerPreferences.Read`**,
  with a resolve delegate rather than a `PdfDocument`, because an encrypted document loads no catalog.
- **`PdfXValidator.ScanPage` is shared by four standards, and only PDF/X is scoped to what prints.**
  `printElementsOnly` is a parameter, false by default, passed by X04, X06, X08 and X09 alone. The
  print-element test inside the walk would drop non-printing annotations from PDF/UA-2, where a screen
  reader is shown exactly the annotation a press never sees.
- **Conformance holds two content walks** (`ScanPage`, `PdfUaChecker.DefinedMcids`) **and must not grow
  a third.**
- **One validation scans and parses a page once** (`ScanReuse`). A check that walks every page
  through `ScanPage` and tokenizes what it collects multiplies the cost by the number of checks: PDF/A
  on a thousand-page text document measured 4.8–6.0× the tokenize floor that way, with 22 of 25 stack
  samples in `ContentStreamParser.Parse`, against 1.6–1.7× with one walk. The PDF/A and PDF/X validators
  open a scope; `ScanPage` hands back a complete scan of the same page and parameters only where
  making it again would have completed within the caller's document ceiling, and charges the total as
  the walk would have. A check parses a scan's stream through `ScanReuse.Parse`, which keys on the
  decoded array's identity. That identity is why scans are shared first: a scan made again decodes
  new arrays. A new check that calls `ContentStreamParser.Parse` on a scan's stream adds the walk back.
  The PDF/UA-1 checker opens a scope too, since UA21's painted fonts and UA57's painted codes
  each walk every page. Its marked-content walk reads a page's own content outside the scans: UA02 on
  every page, UA50 on a tagged one. That walk goes through `ScanReuse.ParseAndKeep`, which keeps the
  parse under the same size bound, and `Keep` does not charge that array a second time. One content
  stream decodes to the stream's own cached array, so the font walks' scans reuse that parse; an
  array-valued `/Contents` is concatenated afresh and is not kept. On a thousand-page text document a
  validation allocates 42.1 MB with the scope and 57.0 MB without.
- **Which annotations are reader content is one predicate**, `PdfUaAnnotationScope.IsPresentedToReader`,
  read by UA27, the deep checker and the auto-tagger; three copies under "must stay identical" comments
  would diverge, making a press-ready file with registration marks non-conforming to one side and
  conforming to the other, and the tagger skip what the checker reports. A check narrows the scope
  at its own site and says why (UA27 leaves `/Widget` to UA08). Its four-argument form adds §7.18.1's
  CropBox exemption (an annotation wholly outside its page's CropBox; an edge touching it is inside),
  which the conformance corpus's conforming files need, and UA08 reads the same form for a widget.
- **Which fonts are used for rendering is one answer**, `PdfUaFontUse.PaintedFonts`, for UA25 and UA21
  (ISO 14289-1 7.21.4.1: a glyph referenced from a content stream; mode 3 exempt) — not every font in
  the resources, and not a text-state scan that skips patterns, Type 3 procedures or appearances. **`null` means "treat every font as used", never "none is".** PDF/A-1's CharSet needs
  the codes each font paints, not only whether it paints: `PaintedCodes` is the same loop
  (`VisitPaintedText`) with a different collector, not a third content walk.

**One incremental writer, in Core** (`Core.Writing.IncrementalUpdateWriter`), because `Accessibility →
Conformance` is live and the shared code goes down. **It keeps a superseded object's generation**
(§7.5.6; only a 0 is corrected, by `KeptGeneration` reading the original's table), and **a reference to
an existing object is the caller's**: the writer cannot tell a caller-built reference from one copied out
of the original, so a site that builds one asks `ExistingObjectGeneration.ReferenceTo`. The
table is the authority, never a clone: `CloneShallow` carries no identity and answers 0.

**One structure-tree walk, in Base** (`Operations.Structure.StructureTreeWalk`). Four readers of
`/StructTreeRoot` read it through the walk — `Accessibility.Internal.StructTreeWalker`, Base's
`PdfTaggedTextExtractor` and `PdfStructureTreeReader`, and the Conformance plugin's `PdfUaChecker` —
because separate walks each lack a bound another carries: a depth cap and a visited set with no edge
budget; a cycle guard on the array spelling of `/K` and not the single reference, so two elements
naming each other recurse to the depth cap and throw; a node budget that cannot fire on a shared-array
graph. Two walks can be compared; a walk in one place is compared with nothing. The one walk
gives `Enter` in document order, a `Kid` per `/K` slot in array order *before* the element it names is
entered (so a consumer interleaving leaves and child elements sees the file's order), and `Leave`
after an element's kids (so one accumulating a subtree finishes it there); a visited set by object
number or reference identity; the depth, node and edge ceilings from `PdfLimits`, each reported once
through one delegate with the first revisit and every unresolvable reference, and the evidence
(`EdgesExamined`, `NodesVisited`, `FirstCeiling`) on the walk. What a consumer makes of a ceiling is
its own: the accessibility walker maps the events to `AccessibilityDiagnostics` and a truncation
reason, the extractor to `LastError`, the reader to a refusal naming the ceiling, the checker to a
partial scan (UA99) — **a depth-capped PDF/UA scan never stops in silence**, or a tree deeper than
the cap and a tree that ends there would produce the same clean result. **A new reader of the tree does not
walk it itself.** A consumer keeps its per-node state on `StructureTreeNode.Tag`, not in a dictionary
keyed by node.

**A text object's first byte does not say whose it is**. tagpdf writes `BDC` after `BT`, so a
tagger that skips a text object only when an existing sequence encloses its `BT` would wrap such an
object whole or wrap its marked line a second time, one run under two MCIDs (§14.7.5.1).
A sequence opening inside the object forbids the wrap and leaves every line sharing its bytes to the
producer. **A balance check is not a nesting check.**

**A colour-space classifier descends every base**. `[/Pattern /DeviceRGB]` is DeviceRGB
on the page (§8.7.3.3), and a classifier that stops at `/Pattern` validates an RGB pattern X-1a clean. `/P` is
read as a flag word (`PdfNumber.FlagWord`) by every checker, `Pdf20DeprecationChecker` included.
**A rule fixed in two readers of a value is fixed in every reader**: grep the key.

**The PostScript interpreter's rules**:

- **Work and memory are charged where they happen, by size.** One operator is not one unit of work
  when its operand is 4 KB of name or 200 MB of decoded block: a 31 KB program looped a reusable
  file's block to 3 GB in twelve seconds with none of it charged, and 118 bytes of `/ASCIIHexDecode
  filter` in a loop made every read a walk of 300 000 stages. So every block a file hands over goes
  through `PsFile.Charge`, each decode's input and output tick the work budget and a failed one at
  the most its input could have made (a filter that throws says nothing of how far it got), each key
  and name ticks by its length (`TickKey`), and each filter kept is charged. A number array a shading
  carries is charged at what a `PdfNumber` costs, not what the operand cost.
- **What a program can nest is walked iteratively or under a depth, and what a program runs may reach
  the builder again.** A shading's `/Function` naming itself took 152 bytes to overflow the stack, an
  `/Alternate` naming its own space and a CIE decode procedure setting a CIE space likewise: an
  overflow is caught by nothing. So `DiscardFileSources` is a loop with a visited set (a list shared
  sixty-four ways is not walked sixty-four times), the alternate takes the resolver's depth, and a
  build running sees a nested one declined.
- **A pen moves through the font matrix** (PLRM §5.3): multiplying the width by the matrix's A term and
  moving along x piles a rotated line's strings up and makes `stringwidth` answer `(w, 0)`. One
  `AdvancePen` through `PsMatrix.ApplyDelta`. **A geometry read from one matrix term is a
  geometry for one orientation**; test the rotated case.
- **A show that continues the line joins the text object before it** (`ContentStreamEmitter.ContinueText`):
  the Microsoft PS Class Driver shows one glyph at a time, flipping the transform around each, and a
  text object a glyph is a line a reader selects and edits letter by letter. The join is one more
  string of a `TJ`, the gap from the pen its number — so it needs the advance a *reader* moves by,
  which only a caller whose font's `/W` is the advance the pen moved can pass (the glyph-indexed
  fonts); a show without one starts its own object. Nothing but `cm`s that leave the transform as
  it was may stand between; another baseline, a gap past an em (a column) or another font, size or
  spacing is a new object. The pen is tracked with the rounded numbers, so a long line does not drift.
- **A DSC data section is skipped only when binary** (DSC 3.0 §5.7): skipping an ASCII section skips
  the `image` that reads its data; a `Lines` count is lines, not bytes. A skip with no test is a
  skip whose extent nobody measured.
- **Symbol and ZapfDingbats draw through their own encoding and have widths**: without them a
  `/Symbol` font with no `/Widths` renders every glyph on one point. The AFMs live in
  `Standard14Metrics.Symbolic.cs`, and `HasBuiltInEncoding` is the one answer for the reader and the
  plugin.
- **A comment promising a report is not the report**: a CalRGB image carried as DeviceRGB is reported
  through a `Report` call. Grep for the `Report` call before believing a comment.
- **A path is in device space** (PLRM §4.4): kept in user space, the Blue Book's ellipse idiom would
  draw a unit circle at the origin. Every CTM change re-expresses the open path by
  `old × new⁻¹`, charged by `Path.Count`. **A translation layer's invariant is a claim about the source's
  semantics**; read the clause, not the comment.
- **A font dictionary names its own embedded program, and `findfont` reaches a captured program by its
  header**: a real Type 1 program calls `definefont` inside `eexec`, which the scanner skips whole, so
  without the header every real embedded font would draw as Helvetica, and a thread-static hand-off
  between `findfont` and `setfont` would swap two fonts a prologue looks up together. The association is a literal name in the
  dictionary. **A fixture written from the code's idea of the input proves the code agrees with itself.**
- **Every text operator dispatches on the font's kind** — `show`, `xshow`, `glyphshow`, `stringwidth`
  and `charpath` alike, none taking the standard-14 path for an embedded font. When one operator in a family learns a
  distinction, grep the family.
- **A driver's font is read when it is used, not when it is defined**: the Windows driver writes a
  Type 42 font's `/sfnts` without glyphs and fills `/GlyphDirectory`, `/Metrics`, `/CharStrings` and
  `/Encoding` after `definefont` — after `setfont` too, and often inside a page's `save`, whose
  `restore` rolls them back. So each glyph and its width are copied out of the dictionary in use at
  their first show, measure or outline (`PsType42Font.Glyph`), and the program a reader gets is
  assembled from what was taken when the resources are built. A snapshot at `definefont` holds no
  glyphs; a read at the end finds what `restore` left.
- **A binary inline-image block ends where its data says**: RunLength's EOD byte and a JPEG's EOI
  without decoding, LZW's EOD code and a zlib stream's final block by decoding
  (`FlateDecodeFilter.DecodeEmbedded` feeds `DeflateStream` in chunks and reads where it stops asking). CCITT stays refused by name because its end
  is a bit pattern inside the coded rows. **A refusal by name is right exactly as long as the data really
  does not say.**
- **A domain error is a `rangecheck`, never a clamp**: `{ -1 sqrt } stopped` answers true in every
  interpreter, and a clamped `0 ln` would push −744 into the next coordinate. "Harmless" and "not
  observable" are claims to test (a weak reference observes the Type 3 registry reset).

**A fit measured by a whole-chunk trial is found by doubling and bisection**.
Growing every chunk a page at a time, measuring each prefix by extracting it, is quadratic in the
extracts' size. `PdfSplitPlanner.PlanUnderByteCeiling`'s size is monotone in the pages, so the ceiling is crossed once; the test asserts `LastTrialExtractions`, since
both searches find the same chunks.

**A slot the script fills is counted**: appended on every call, `PdfJsEngine.LastUnsupportedRequest`
would let a loop setting `hidden` five thousand times leave a report of megabytes. The first 32
entries are kept, the rest counted.

### Folder taxonomy — the namespace follows the folder

| `Base.Operations.` | What belongs there |
|---|---|
| `Forms` | AcroForm filling, flattening, reading, appearance generation, field extraction, FDF/XFDF, invoices |
| `Documents` | Whole-document operations: redaction, templating, page manipulation, comparison, repair, revisions, n-up, content filtering, transparency flattening |
| `Extraction` | Reading content out: text, tables, links, graphics, layout, 3-D, rich media |
| `Structure` | Catalog-level structure: bookmarks, destinations, optional content, page labels, tag tree, OCR layer, the one rewrite of a catalog name tree |
| `Metadata` | Document info, XMP, ICC, colour-space conversion, usage rights, DocMDP |
| `Images` | The document-edit side of images (`Base/Images/` is the decode side) |
| `Barcodes`, `Fonts` | Barcode generation and reading; font embedding and subsetting |

Cross-cutting types stay at `Operations` root: `VerifiedWrite`, `PdfObjectIndex`, `WalkBudget`,
`DocumentEditDiagnostics`, `DocumentRevisionWriter`, `RevisionObjects`, `PdfDestinationPage`,
`StructKidClassifier`, `TextDirection`, `PdfActionDispatcher`, `PdfActionRunner`,
`PdfAnnotationWriter`, `PdfAnnotationEditor`, `AnnotationAppearanceBuilder`, `AnnotationAppearanceStamp`,
`PdfAutoLinker`, `PdfAttachmentManager`, `SvgToPdfConverter` and `SvgFilterEffects`. The list carries no
count, which would go stale. Signing keeps its entry points at the root and groups the
rest under `Cms`, `Timestamping`, `Models` and `Plugins`.

**The sanitizer is the one whole-document operation in Core** (`Core.Document.PdfSanitizer`, with
`SanitizeOptions` and `PdfSignatureDetection` beside it). A host that signs references `Core` and
`Signing` alone, and sanitizing before signing must not cost it Base's SkiaSharp native assets; the
sanitizer reads, cleans and writes objects and draws nothing, so nothing holds it in Base. It is not
a precedent for moving other operations down: an operation goes to Core only when a host without
Base has to reach it and it needs nothing Base has.

### KISS / SOLID

Implement only what the gap needs; a working 80% beats an untestable 100%; no abstraction layer with
one implementation. Parsers parse, renderers render, writers write. Depend on `PdfDictionary`,
`PdfObject` and `Stream`, not on concrete parsers or file paths.

---

## DRY — the shared decision points

**A comment asserting that two implementations agree is a duplication report, not documentation.**
Extract the helper instead. Before writing any of these, use the one implementation:

| Concern | The one implementation |
|---|---|
| Colour conversion, device **and** CIE (`ComponentsToColor`, `CmykToRgb`, `CalGrayToSRgb`, `CalRgbToSRgb`, both `LabToSRgb` overloads, `ToCssRgb`, `Channel`) | [`Base/Rendering/PdfColor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfColor.cs) |
| BT.601 luminance weights | `Core.Constants.PdfColorSpaceNames` |
| PDF literal-string escaping (§7.3.4.2) | [`Core/Objects/PdfStringEscape.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Objects/PdfStringEscape.cs) |
| PDF name escaping **and decoding** (§7.3.5) - `Escape` from UTF-8 bytes, `Decode` back to UTF-8 with a Latin-1 fallback for bytes that are not valid UTF-8. **Two readers call it** (`PdfTokenizer`, `ContentStreamScanner`) and they must agree | [`Core/Objects/PdfNameEscape.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Objects/PdfNameEscape.cs) |
| Dictionary cloning | `PdfDictionary.CloneShallow` / `DeepClone` |
| Operand coercion | [`Core/Text/ContentOperand.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/ContentOperand.cs) |
| Stream filter decoding | `Core.IO.Filters` only |
| Stream seek-normalisation | `Base.PdfHelpers.EnsureSeekable` (Signing has its own internal copy — it cannot reference Base) |
| Object-number map → ordered `IndirectObject` list | [`Base/Operations/RevisionObjects.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/RevisionObjects.cs) |
| Trailer + write tail for an appended revision | [`PdfPageContentAppender.AppendRevision`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfPageContentAppender.cs) |
| **Adding content to an existing page**: its resource names must not replace the page's, and it must start in the page's initial graphics state | `PdfContentStreamBuilder.ReserveResourceNames` (fed by `PdfPageContentAppender.ResourceNames`), and `PdfPageContentAppender.StateIsolation`, which brackets the page's own streams in a shared `q`/`Q` pair (§7.8.2 concatenates `/Contents`). The builder mints `F1`, `Im1`, `GS1`, the commonest names in real PDFs, and a merge that overwrote a page's own entry of the same name would re-render the page's text in a watermark's font and at its opacity. A page whose content leaves a `cm` in force would move every stamp, flattened annotation and flattened widget with it without the bracket. Both flatteners bracket through the same pair; the annotation editor also names each placement from the page's taken names |
| A resource name a page does not use — for the font or image a writer adds to an existing page's resources: the template engine's replacement font (one an edit), the OCR layer's, the page text writer's; a fixed name replaced an earlier edit's | `PdfPageContentAppender.FreeResourceName` |
| **Content added to many pages in one revision** — the stamp's objects written once, the pages that start from one `/Resources` naming one merged copy; chosen pages as all | `PdfPageContentAppender.AppendContentToAllPages` and `AppendContentToPages`, over `ResourceSharing` (`Merged`, one per revision). A direct copy of each page's resources with the stamp's merged in, its font, state and image minted again, would write 40 MB from 455 KB for 800 pages inheriting one dictionary of 800 entries. An object a page mints that names no other and repeats one already written is dropped for it. Headers, footers, Bates numbers, watermarks, the OCR text layer, the template engine and `CoverRegions` go through it; `CoverRegions` writes one revision, not one a page (51 ms at 800 pages, against 40 s) |
| **A page's stamped forms** — a flattened field's or annotation's appearance — registered in the `/XObject` dictionary the page already reads | [`Operations/StampedXObjects.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/StampedXObjects.cs), one per revision: the indirect `/Resources` a page names or inherits, or the `/XObject` object that names, is superseded once for every page sharing it, and a page whose resources are written directly gets its own copy. Names are taken against what the revision has added there, so two pages never mint one name into a shared dictionary. A direct copy a page would write 10 MB from 371 KB at 800 × 800, against 628 KB; a minted name that could take one the resources already hold would replace the image under it |
| **What one built-in call of an untrusted script may build** | [`Core/Scripting/ScriptBuiltinBounds.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Scripting/ScriptBuiltinBounds.cs) `Prelude`: JavaScript that both script plugins run before any script — the form engine in `PdfJsEngine.Execute`, the page engine through the vendored `JsScriptingOptions.PrepareEngine` (VENDORED.md item 6). Jint asks its memory limit when a statement starts, so one call would build what it is asked to: under 32 MiB, `'x'.repeat(1e8)` would build 190 MiB and a `join` of two hundred references to one megabyte string 1.4 GB, bounded only by .NET's largest string. `repeat`, `padStart`, `padEnd`, `concat`, `replace`, `replaceAll`, `join`, `toLocaleString` and `JSON.stringify` convert their operands once, as the specification does, count what they would build, and throw a `RangeError` past the memory limit in characters; a string replacement goes through a callback that applies the specification's substitution, so `$&` cannot multiply unseen. `ScriptBuiltinBoundsTests` holds the bounded built-ins to the native ones' answers. A regular expression runs inside one call too, so its timeout is the script's, and a prepared, cached page script is prepared under it (VENDORED.md item 7): its literals keep the timeout they were prepared with |
| **A list a file fills, kept distinct**, and a message that joins it | [`Core/Util/DistinctStrings.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Util/DistinctStrings.cs): a set beside the list, `Joined` naming the first twenty and counting the rest. The colour converter's and content filter's notes, the redactor's shared forms, the form filler's button states and a Type 4 function's unknown operators go through it; a search of the list before every addition is quadratic in what the file names (four thousand unknown operators would spend 6 s of one function's budget), and a `Last*` slot joining every entry lets a file choose its length |
| **Which pages share a content stream**, and a document written by what a root reaches | [`Operations/Documents/PdfFullRewriter.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfFullRewriter.cs) `ContentUsage`: how many page listings use each content stream, counted once an operation and updated when a substitution points a page at a private clone (its old streams taken out), compared by dictionary identity so a page listed twice is not another user of its own streams. A walk of every page for each page edited would be pages squared for a term on every page. `WriteReachable` writes what an explicit root reaches, with an `/ID`; `TryFullRewrite` calls it, and N-up does, so its sheets carry their pages' `/Resources` renumbered, with every font program and image they reach |
| **The decoded length of each stream a listing measures** | [`Operations/Extraction/DecodedLengths.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Extraction/DecodedLengths.cs): once a stream, under the listing's ceiling, a refusal's reason kept. A decode refused at the ceiling is not cached by the stream, so without the listing's own record one flate bomb named by N 3-D or rich-media annotation entries would be inflated to the ceiling N times; every annotation still reports its own |
| **An element's attribute objects** | [`Core/Document/PdfStructureAttributes.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfStructureAttributes.cs) `SourcesOf` — the objects `/A` and its `/C` classes name, in the order `ReadAll` reads them, each once (a repeat adds nothing under first writer wins) — and `ReadAll` over them; `PdfStructureTreeReader` keeps one reading per set of sources. A map per element, copying each entry of its attribute objects would make half a million elements naming one shared object of a few thousand keys hold billions of entries, from a few hundred kilobytes |
| **A code point of decoded text** | [`Rendering/GlyphIdResolver.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/GlyphIdResolver.cs) `CodePointAt`: a surrogate pair's scalar, else the unit, so a lone surrogate is one slot no font maps. `char.ConvertToUtf32` throws on one, and a `/ToUnicode` destination longer than four digits keeps it (`CMapParser.HexToUnicodeString`): in the glyph lookup and the GID-0 fallback scan alike the throw would drop the whole run and leave the text matrix where the run had moved it |
| **What every extraction of a split reads of the whole document** | [`Plugins.PageEdits/PdfSplitter.cs`](src/plugins/Tetitulz.Pdf.Plugins.PageEdits/PdfSplitter.cs) `SplitWalks`: the flattened `/Names` with `/Dests` indexed by the page each lands on (each extraction its own copy of every list, since it sorts in place), the outline in preorder with each item's parent, own page and page-independent action, and the field tree as [`AcroFormFieldIndex`](src/plugins/Tetitulz.Pdf.Plugins.PageEdits/AcroFormPageFilter.cs) — read once a split. Read a page, they would be pages × entries (800 pages: 1.0 s). `AcroFormPageFilter.Filter` given the index visits only the placed fields and those listing them; the plan is the whole walk's because a skipped node reaches no kept page, so the index exists only for a tree with no `/Kids` cycle and no path past `MaxFieldDepth` (a long path through a node read first under another parent included), and is used only when the whole walk's spend fits its budget — only there could skipped nodes change the bookkeeping. The field arrays are written from the plan's kept entries (`WriteFieldArray`, `DeepClone`'s `rebuiltKeys`), never copied whole: an indirect array would come out of the copy as null and take every field. |
| **What a split's object walk refuses to enter** | `PdfSplitter.CollectionGuard`: a page the extraction does not keep, by [`PdfObjectHelper.IsPageObject`](src/plugins/Tetitulz.Pdf.Plugins.PageEdits/PdfObjectHelper.cs) — `/Type /Page` or a page Core's page list holds (so an untyped leaf is not followed, page and content); and a field node the form plan dropped unless a kept page lists it in `/Annots` — otherwise a kept button's SubmitForm `/Fields` would carry another page's SSN and its appearance into the extract. Past the field listing's budget a dictionary that reads as a field is refused unless kept. [`SplitResourceUse`](src/plugins/Tetitulz.Pdf.Plugins.PageEdits/SplitResourceUse.cs) reads what the kept pages draw — their content and annotation appearances, then every form, Type 3 glyph, tiling pattern and soft-mask group drawn from them, each through its own resources or the ones it was drawn from — and the guard leaves out every entry of a resource category no kept page names: a producer that writes one `/Resources` for all pages otherwise hands a one-page extract the other pages' images and forms. A colour space keeps the entries its value names (a Pattern space's base). A resource dictionary whose drawing cannot all be read (a stream that will not decode, the read's byte or stream ceiling), and a category dictionary that something carried names unread (a form's `/DR`, a widget's icon), travel whole, said in `LastResourceWarning`. `InheritedCopies` writes a direct value several pages inherit once (a rectangle of four numbers is still copied); `Stamp` numbers a copied object only when `DeepClone` made it — numbering a shared singleton such as `PdfBoolean.True` or `PdfNull.Instance` would renumber it process-wide. |
| Standard-14 → system family | [`Core/Fonts/Standard14Names.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/Standard14Names.cs) (`SystemFamily`; `Canonical` is the reverse) |
| Simple-font `/BaseFont` + `/Widths` read | [`Core/Fonts/PdfSimpleFont.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/PdfSimpleFont.cs) |
| Annotation walk by subtype | [`Base/Operations/Extraction/AnnotationSubtypeWalk.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Extraction/AnnotationSubtypeWalk.cs) |
| Whether a link sends the pointer's position — a URI action with `/IsMap` true at its `/A` or along its `/Next` chain, each object read once, the chain bounded by `MaxActionChainDepth` and its cut said — PDF/UA-1's UA35 (a Warning) and PDF/UA-2's §8.13.3 (an advisory). Both parts condition the requirement on equivalent functionality elsewhere, which no reading of the file establishes | `PdfDeepAccessibilityChecker.SendsPointerPosition` |
| The plain text of a rich text string — a markup annotation's `/RC`, a field's `/RV`. ISO 32000-2 defines rich text by the XFA 3.3 specification, whose chapter 27 names the elements: `p` and `br` break the text (read as a space), `a`, `b`, `i`, `span`, `sub` and `sup` style it in place, an element it does not name is suppressed to its text. **A boundary of a paragraph is a space**: a reader that drops it reads `<p>a</p><p>b</p>` as `ab`, and an equivalence check then reports a `/Contents` of `a b` as different. The FreeText display and PDF/UA-2's §8.9.2.3 and §8.10.3.3 read one implementation | `Core.Text.RichText` |
| CRL scheme whitelist + SSRF guard | `Signing.Timestamping.DssBuilder.SafeCrlDistributionUrls` |
| Address classification (private / reserved / rebinding) | [`Core/Net/PrivateAddressGuard.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Net/PrivateAddressGuard.cs) — `TryResolveSafeAddresses` is the form to use; `IsSafeHost` is written in terms of it |
| Connecting to the address that was checked | [`Core/Net/GuardedHttpConnect.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Net/GuardedHttpConnect.cs) — `net10.0` only, by transport |
| Fetching a URL untrusted input chose — the handler (no automatic redirects, the connect callback above), redirects followed by hand with the guard re-run per hop, a success status, and a body read that stops at a ceiling. **Every send reads headers only, and the body read carries the fetch's budget token.** `HttpClient`'s default `ResponseContentRead` buffers the whole body inside `SendAsync` before returning — up to two gigabytes — so a size cap checked afterwards bounds nothing. The second half follows from the first: once the send returns at the headers, its cancellation no longer covers the body, and a blocking read lets a server trickling a byte a minute hold the caller indefinitely — so there is no synchronous capped read at all | [`Core/Net/GuardedHttpFetch.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Net/GuardedHttpFetch.cs) — `DssBuilder`'s revocation fetches and HtmlToPdf's `GuardedRemoteResourceLoader`. Signing and HtmlToPdf may not reference each other, which is why it is in Core |
| Name-tree (§7.9.6) **and** number-tree (§7.9.7) walk, with its `/Kids` cycle guard and both budgets | [`Core/Document/PdfTreeWalk.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfTreeWalk.cs) — the two public faces are `PdfNameTree` and `PdfNumberTree`, which differ only in the leaf array's key. **The limits are the caller's and the stop reason is reported** ([`PdfTreeTruncation`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfTreeTruncation.cs)): answering only "incomplete" would leave callers needing their own copies — `PdfPageLabels`, and the deep accessibility checker, which writes the reason into a PDF/UA finding and holds deliberately tighter caps than a reader does; a `bool` would cost the diagnostic or the stricter limits |
| Name-tree **key order** (§7.9.6: by bytes, a shorter prefix first) and **rewriting a catalog name tree** in an appended revision | [`PdfNameTree.CompareKeys`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfNameTree.cs) — in Core, so merge, split and the Base writers all sort by bytes, never by decoded text. [`Operations/Structure/CatalogNameTree.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Structure/CatalogNameTree.cs) for the rewrite: every other category of the name dictionary is kept, each key keeps its stored bytes, a read cut short by a limit refuses, an indirect root or name dictionary is superseded where it lives, and an emptied category is removed. Replacing the whole `/Names` dictionary would delete every named destination and document script, and re-encoding existing keys from their text would break `/GoTo`s that name a destination by its stored bytes |
| A TrueType composite glyph's component records (`glyf` §5.3.4.3) — where each glyph index sits, to read or renumber it, **bounded by the glyph's own end** | [`Core/Fonts/CompositeGlyph.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/CompositeGlyph.cs) — public, because `Plugins.Maintenance` needs it. Both subsetters and the pruner read through it; a walk that does not stop at the glyph's end reads a malformed last record's `MORE_COMPONENTS` into the next glyph and puts whatever it names into the subset. `Woff2GlyfTransform` is not a caller: it reconstructs records from WOFF2's split streams, a different job |
| sfnt assembly (header, tag-sorted directory, checksums) | [`Core/Fonts/SfntBuilder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/SfntBuilder.cs) — public, because `Plugins.Maintenance` needs it. **A consolidation is finished when the old helpers are gone too**: an uncalled copy is the one the next edit calls |
| Reference-identity comparison | `Core.Objects.PdfReferenceEquality` |
| A conformance check reading each page's resource dictionaries: **a dictionary the pages share is read once a validation** — the walk below a page's own content shared by every page that names the same `/Resources` and shows no annotation, and each check's loop skipping a dictionary it has read. A file states a dictionary once and names it from every page, so a per-page read is pages × entries: PDF/A validation of 500 pages over 20 000 shared entries takes 1.3 s with the skip and 278 s without. Each check dedupes its own findings, so the skip changes no answer; the scope counts the skips as the evidence | `Plugins.Conformance.ScanReuse` (`SharedWalk`, `FirstVisit`, `ClosedResourceRevisitsSkipped`) |
| A simple TrueType font against the TrueType clauses — PDF/UA-1 7.21.6, PDF/A-1 6.3.7, PDF/A-2/3 6.2.11.6, PDF/A-4 6.2.10.6, PDF/UA-2 §8.4.5.7. PDF/UA-1, PDF/A and PDF/UA-2 ask the same questions, so the readings are in Conformance, public for the sanctioned edge (Accessibility's PDF/UA-2 checker reads them); the `cmap` rule takes the standard's form as a `TrueTypeCmapRule` (PDF/A-1's symbolic-only count, ISO 32000-1's count with (3,0), ISO 32000-2's named subtables) | `Plugins.Conformance.TrueTypeFontRules` |
| Which streams are embedded files — a stream naming itself `/EmbeddedFile`, or one a file specification's `/EF` names. The `/EFF` crypt filter asks it (the encryptor and the decryptor alike), and so does the linearizer's embedded file stream hint table (F.4.7), from a plugin that must not reference Encryption — so it is in Core rather than a second copy or a `ProjectReference` | `Core.Document.EmbeddedFileStreams` |
| A font subset's `/CharSet` and `/CIDSet` against its program — PDF/A-1 6.3.5, PDF/A-2/3 6.2.11.4.2, PDF/UA-1 7.21.4.2. **The standards read the CharSet differently**, and the corpus shows it: PDF/A-1's lists the glyphs used for rendering (the published profile's 6-3-5-t02-pass-a lists extra names, -pass-b leaves an unused glyph out, both pass), while ISO 32000-1's wording, which PDF/A-2/3 and PDF/UA-1 take, lists every glyph the program defines "regardless of whether" it is used, and 7.21.4.2-t01-fail-b fails a name the program lacks — so the rule is a `SubsetSetRule`, and PDF/A-1's reads the painted codes: each named through the font's encoding or, where the dictionary leaves a code to the implicit base, the embedded program's own encoding — Table 114 makes that the base once a program is embedded, and the published profile's t02 files list `/space`, which their `/Differences` never name. **"Present" in a TrueType CIDFont is a glyph with outline data**: 7.21.4.2-t02-fail-a's CIDSet names eight CIDs under an Identity map whose eight outlined glyphs are elsewhere, in a program of 1 532 glyph slots. **PDF/A-1 asks for the sets of every subset, embedded or not**: a gate that skips unembedded ones would hide a tagged Type 1 subset with no `/CharSet` from an embed-then-convert pipeline. The plugin reaches Core's `internal` Type 1, CFF and sfnt readers through one public door | `Plugins.Conformance.FontSubsetSets` over `Core.Fonts.EmbeddedGlyphSets` |
| A composite font's CMap — PDF/UA-1 7.21.3.3 and PDF/UA-2 §8.4.5.4, the same three sentences over ISO 32000-1 Table 118 and ISO 32000-2 Table 116. **The two tables list the same 61 names**: read from both texts (a text extraction runs four long names into their descriptions, so the comparison was made against the PDF), and each but the two Identity CMaps among Adobe's CMap resources, which the CMap plugin carries. A CMap program is read by one token pass that steps over comments and strings, so a `usecmap` in either is not the operator. **A predefined CMap's character collection is its program's**, since ISO 32000-2 leaves Table 117 empty and points to the CMap programs — so 7.21.3.1 and §8.4.5.3.1 read the program registered under the name (`PredefinedCMapRegistry.GetProgram`, filled by the CMap plugin; mechanism in Core, payload in the plugin), and a name no one registered is not judged and is said | `Plugins.Conformance.CMapRules` |
| Whether an embedded program defines the glyph a painted code selects — PDF/A's A84, PDF/UA-1's UA57 and PDF/UA-2's §8.4.5.5.1, whose sentence is the same (the published profile's isGlyphPresent). **An empty TrueType glyph is not a defined one** — the published profile's 6-2-11-4-1-t02-fail-c maps Cambria's `a` to a glyph slot the subset kept with no outline — **unless its character is whitespace**, whose glyph is empty by nature (the same file paints a space); where the character is not known (a symbolic font, a TrueType CIDFont), an empty glyph is not judged. **A font this library cannot read is not judged, and is said in `LastValidationError` — never an A99**: `PdfAConverter` refuses on any A99, so a program over this library's readers' limits would refuse a file the published profile passes (its `isGlyphPresent == null`). **A composite font's strings go through its own CMap**, `EmbeddedGlyphSets.CidReader` over `CMapCodec` (the one splitter): `CMapCodec.FromFontEncoding` falls back to Identity for a CMap it cannot honour and says so in `LastEncodingError`, and that fallback is a guess, so the reader answers null there rather than CIDs. Checked against the full corpus: every public PDF/A suite 6-3-5-t01 file and four of five t02 files fail as named, and none of 1 027 pass files is flagged | `Plugins.Conformance.FontGlyphPresence` over `Core.Fonts.EmbeddedGlyphSets.GlyphPresence` |
| Which shown codes reference the `.notdef` glyph — ISO 14289-2 §8.4.5.9 and ISO 19005-2/3 6.2.11.8 / 19005-4 6.2.10.9 ask one question in the same words ("shall not contain a reference to the .notdef glyph from any of the text showing operators, regardless of text rendering mode"). The decision is in Core, where PDF/A's Conformance checker and Accessibility's PDF/UA-2 checker both reach it. **A valid code no character mapping covers is a reference** (§9.7.6.3: "If the CMap does not contain either a character mapping or a notdef mapping for the code, … the glyph for CID 0 shall be substituted"), but the codec keeps the code's own value as its CID, because subset fonts that omit their `cidrange` table render through it — so the codec says which it is (`CMapCodec.Code.CidMapped`) and the renderer's fallback stays. **A simple font's code its encoding names nothing for is a reference** where the encoding is known in full (a named base, a Type 3 encoding dictionary, an embedded Type 1 program's built-in encoding): the published profile's 6-2-11-8-t01-fail-a and -b show WinAnsi's code 0, which a `/Differences`-only reading would pass. A symbolic TrueType font's codes reach its `cmap` directly (§9.6.6.4) and are not judged by name. **Annex D's WinAnsi note makes six codes the bullet**, without which this reading flags them — the table carries the note, not the rule. Checked against the published profile's full corpus: all nine fail files flagged, none of 551 PDF/A-2/3/4 pass files | `Core.Fonts.NotdefReferences` (`For`, `In`); `Plugins.Conformance.PdfUaFontUse.ShownStrings` |
| The glyph name a simple font's code selects through its `/Encoding` — the WinAnsi, MacRoman, MacExpert and Standard code-to-name tables, the name-to-table switch, and a font's own code-to-name map with `/Differences` applied. The tables are in Core, so the font remediator reaches them to write a subset's `/CharSet`; `FontCache`'s names are aliases, so the renderer and its tests read the same arrays. A code the dictionary leaves to the program's built-in encoding is absent from `ByCode`, never guessed | `Core.Fonts.SimpleFontEncodingNames` |
| Whether a font maps its character codes to Unicode — ISO 14289-1 7.21.7 and ISO 19005's level A and U clauses ask it in the same words. UA26 and PDF/A's A77 both read it. One tightening the PDF/A-2u corpus asks for: a Type 3 font's `/Differences` names are its own procedure keys, so they map only when the Adobe Glyph List knows them | `Plugins.Conformance.FontUnicodeMapping.Maps` |
| Whether a structure type is standard — ISO 32000-1 Table 333 for PDF/A-2 and -3's level A rules and for Accessibility's checker and tagger (which add ISO 32000-2's types), the PDF Reference 1.4's §9.7.4 for PDF/A-1's. Conformance may not reference a feature, so the list is in Core, as two `switch` methods with no static state | `Core.Constants.PdfStructureTypes.IsStandardInIso32000Part1` / `IsStandardInPdf14` |
| Reading and writing a bitmap's pixels as **straight** colour through its own buffer — byte order, premultiply-on-store, the `a == 0` ⇒ transparent-**black** collapse | [`Base/Images/StraightPixelLayout.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Images/StraightPixelLayout.cs), and [`BitmapPixels.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Images/BitmapPixels.cs) for the walk — remap **and** positional `Fill` — with one counted `SKBitmap.Pixels` fallback per entry point. A hand-written row loop for a huge, trivial pass carries its own slow branch, and should count `PropertyFallbacks` in it |
| The form-wide `/AcroForm /DA` and `/Q` default that follows a variable-text climb (ISO 32000-2 Table 224). **The climb itself has two walks on purpose** — a widget's `/Parent` chain, a field's arrival chain — and only this step is shared | `AnnotationFallbackModel.DefaultAppearanceOrFormDefault` / `QuaddingOrFormDefault`, reached by the filler, the reader, the flattener and both renderers |
| **Reading a `/DA` string** (§12.7.3.3): the font resource, the size, and the `g`/`rg`/`k` fill as numbers | [`Base/Operations/Forms/DefaultAppearanceString.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/DefaultAppearanceString.cs), behind the form generator, the renderers' fallback and the annotation appearance builder. Separate copies disagree: one ignoring `k` draws a CMYK `/DA` black; one pasting colour operands into a content stream as found writes what the file wrote; defaults of 10 and 12 under a comment saying they agree. A colour with a non-numeric or non-finite operand is ignored, and the one set before it stands |
| **The annotation appearance builder draws what the no-`/AP` fallback draws** | The builder takes `AnnotationFallbackModel`'s `FreeTextBaseline`, `StampName`, `StampColor`, `StampMinLineWidth` and `StampCaption`, and `AnnotationColor` for `/C`. Numbers of its own would draw an "Approved" stamp red where the fallback makes it green, read a stamp with no `/Name` from its `/Contents` rather than Table 181's "Draft", and set free text at a different inset and baseline |
| Whether a one-byte simple font can carry a string (above U+00FF, or in WinAnsi's 0x80–0x9F, needs a composite font) | `Core.Text.TextRunComposer.NeedsCompositeFont`, reached by `FormAppearance.IsSingleByteEncodable` and the watermarker, neither carrying the two ranges itself |
| **Where a paragraph may break between its lines** (CSS 2.1 §13.3.3): a fragment before a break keeps the paragraph's orphans, one after a break its widows, one between two breaks both — it is at the top of its page or column and at the bottom — and a forced break exempts the sides it makes | [`PdfExporter.LinesBeforeBreak`](src/features/Tetitulz.Pdf.Builder/Export/PdfExporter.cs) over `MinFragmentLines`; the page breaker's `PlanLineBreak`, `SplitAtHeight`, `MeasureParagraphLead` and the HTML column balance's `Slice`. A rule asking only the orphans of the part it plans would let a page in the middle of a paragraph begin with fewer lines than its widows. One rule, with `afterBreak` saying whether the part begins after an unforced break |
| **Where an English word may break at a line's end**: Frank Liang's algorithm over Gerard Kuiken's American English patterns from hyph-utf8 (`hyph-en-us.tex`, embedded unmodified — its notice permits copying with the notice kept, `NOTICE.md`; `.gitattributes` keeps its bytes), the file's own exceptions first (`ta-ble`, `present`), at least 2 letters before a break and 3 after (its `hyphenmins`), words of ASCII letters up to 64 long; a token's leading and trailing punctuation set aside, punctuation inside the letters leaving it whole, as TeX does. A line takes the **last** point whose head, with a drawn hyphen, still fits | [`Core.Text.Hyphenation.EnglishHyphenation`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/Hyphenation/EnglishHyphenation.cs) (`BreakPoints`, `BreakPointsWithin`); the Builder's `PdfExporter.TryHyphenate` (`Paragraph.Hyphenate`, which HTML's `hyphens: auto` sets under an English `lang`) and `ReflowLineBreaker` (`TemplateReplaceOptions.Hyphenate`). The expected breaks in the tests come from pyphen 0.18.1 loaded with the same patterns — an independent implementation, libhyphen's; a hyphenator checked against its own output shares whatever it believes. Hebrew is not hyphenated in typesetting, and no other language's patterns ship |
| A file specification's name — `/UF` before `/F`, which Table 43 makes a *shall* | `PdfAttachmentManager.FileSpecName` |
| Reading a CMap section as **tokens, not lines** — several entries may share a line, one may span several — for both `/ToUnicode` (`CMapParser`) and a Type 0 encoding (`CMapCodec`) | `CMapParser.SectionTokens` (`bfrange`'s array form keeps its own scan, with the same hex-digit rule) |
| Filling a buffer from a stream until it is full or the stream ends | `Core.IO.StreamBytes.Fill` — public, because two plugins read this way too |
| A gap between words in plain extraction, measured from where the last appended glyph ended: a `TJ` kern below −0.2 em, a same-line `Tm` jump wider than 0.2 em of the text as drawn, and a `Td` — which moves from the **start of the line**, so its operand counts every glyph the line has shown. A browser writes each kerning pair as such a move (`<P> Tj 7.43 0 Td <AYROLL> Tj`), and read by the operand alone the word is `P AYROLL`: no search finds it, and the redactor's read-back, which is this extractor, cannot see a kerned word it failed to remove. A `Td` landing within an em before and a fifth of an em after the last glyph's end, on its upright line, is no gap; where that cannot be measured the operand decides | `PdfTextExtractor.WordGapEmFraction`, `ContinuesWhereTheLastGlyphEnded`, `TextState.LastShownEnd` |
| Whether a path is strictly inside a directory — both made absolute, the directory's trailing separator, case as the platform's file system has it. A local file an HTML page names, in the managed engine (`ResolveLocalPath`) and the browser engine's request guard, and the path an extracted attachment is written to: a prefix test over an unnormalised root refuses every name under a target written with a trailing separator | `Core.IO.DirectoryContainment.IsInside` — public, because a feature and a plugin ask it |
| Walking a document's pages for text — the newline between them, and which page's extraction error is reported. **The facade needs each page's text separately to notify its hooks**; a loop of its own, with no separator and no first-error aggregation, would make `PdfOperations.ExtractText` return `"one\ntwo"` or `"onetwo"` for the same file depending on whether *any* plugin happened to be registered. A per-page callback is what lets one walk serve both: **observing the pages must not be able to change the answer** | `Core.Text.PdfTextExtractor.ExtractText(PdfDocument, Action<int, string>)`, which the no-callback overload forwards to |
| A signature's two signed ranges — reading them into memory, streaming them into a digest, and whether a range ending before the file's last bytes still covers the document (§12.8.1's optional EOL after `%%EOF`) | [`Core/Document/SignedByteRanges.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/SignedByteRanges.cs) — `ReadSignedBytes`, `DigestSignedBytes`, `ReachesEndOfFile`; the verifier, the DocMDP analysis, the incremental-save report, the timestamper and the PDF MAC seal all go through it |
| Writing a CMS value into a `/Contents` hex placeholder | `Signing.SigningHelpers.WriteContents` — the signer, the deferred signer, the counter-signer and the timestamper |
| **Encoding** that CMS value — **DER**, which BouncyCastle's `GetEncoded()` is not: it writes indefinite lengths (`30 80 … 00 00`), valid BER and not what ISO 32000-2 §12.8.3.3.1 and the CAdES/PAdES profiles require. Both forms parse to the same `SignedData`, so a site encoding its own would be invisible to every assertion that parses | `Signing.Cms.CmsSignatureGenerator.EncodeForContents` — the signer, its second generator, the deferred builder, the counter-signer, the signature timestamper and the PDF MAC attachment |
| Adding a signature field to `/AcroForm` — every existing field kept, `/SigFlags` 3, a name no field already has (a period becomes an underscore, §12.7.4.2) | [`Signing/SignatureFieldRegistration.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/SignatureFieldRegistration.cs) — `FormWithSignature`, `UniqueName`; the signer, the timestamper and the visible-signature builder |
| **A visible signature's image** — drawn through an inner form whose own `/Resources` names it; the appearance's `/Resources /XObject` names that form alone | [`Core/Writing/SignatureAppearanceImage.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Writing/SignatureAppearanceImage.cs) — `InnerForm`, `AppearanceXObjects`, `Invocation`; `VisualSignatureBuilder`'s image stamp and `SignatureAppearanceGenerator`'s logo (so its `/R` and `/D` variants too). Acrobat Reader does not validate a signature whose appearance names an image under `/XObject`: clicking it reports "Expected a dict object" and the document stays "requires validating"; the same image inside a form carrying its own `/Resources` validates, as an approval and as a DocMDP certification. The specification permits both shapes — the rule is Acrobat's, found by testing files that differ in this alone, and Adobe does not document it. The inner form takes the appearance's box and the identity matrix, so the page renders as with the image drawn directly. A text-only appearance, naming a font and no XObject, validates as it is. `SignatureAppearanceXObjectRule` (Base tests) asserts the shape for both paths |
| Listing a document's signatures once each, across fields that share one `/V` | [`Core/Forms/SignatureFieldWalk.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Forms/SignatureFieldWalk.cs) — `DistinctSignatures`, for Base and Signing alike |
| Appending a `/DSS` revision, merged with the store the document already carries and with the trailer carried forward (§7.5.6) | `Signing.PdfLtvRefresher.DssRevision` — `PdfSigner.EmbedDss` and the refresher |
| The object number and generation of an object a revision supersedes — the cross-reference table first, the reference second, never a clone | `Signing.SigningHelpers.ObjectNumberOf` / `GenerationOfExistingObject` |
| Which signatures a document already has and what they let the next one be — one certification at most, first, `/P 1` permits nothing (§12.8.1, §12.8.2.2) | [`Signing/SignatureOrdering.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/SignatureOrdering.cs), over `Core.Document.DocMdpAnalyzer`'s classification of a signature dictionary |
| A signature timestamp's imprint (RFC 3161 Appendix A — the SignerInfo's signature value), attached without signing again | [`Signing/Timestamping/SignatureTimestamp.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Timestamping/SignatureTimestamp.cs) |
| **A CMS's signing-time attribute** — none for a PAdES signature (ETSI EN 319 142-1 §6.3, Table 1: `/M` shall be present, the attribute shall not; ISO 32000-2 §12.8.3.4.2: one of the two), the instant `/M` claims for any other (Table 255 makes `/M` the fallback when the signature holds no time) | [`Signing/Cms/CmsSignatureGenerator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Cms/CmsSignatureGenerator.cs) `SigningTimeAttributeFor` and `SignedAttributeGenerator`; `PdfSigner.Sign`, `Plan`/`Complete` (the plan carries the instant), the hand-built Ed448 CMS, and `CmsDeferredSignatureBuilder.GetSignedAttributes(PreparedSignature, …)` (the prepared dictionary's `/SubFilter` and `/M`). BouncyCastle's default generator adds a signing time of *now* to a table without one, so a signature would claim two times and a PAdES one break the profile; BouncyCastle also checks the signer's certificate at that attribute, so a test whose CMS never verifies could pass on its other assertions (`DocumentTimestampScopeTests` verifies it). An external signer's CMS is its own |
| **Whether a public key is affordable to verify with** — a key a file or a server supplied: an RSA exponent of at most 256 bits, 64 above a 3072-bit modulus (FIPS 186-4, OpenSSL's ceiling); DSA's q 256 and p 10 000 bits; an EC field of 661 | [`Signing/Cms/PublicKeyCost.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Cms/PublicKeyCost.cs) `WhyTooCostly`, `Affordable` (an `InvalidKeyException`, counted), asked before every verification: the chain validator, the CMS verifier, the OCSP response's signer, the revocation checker, the `/DSS` builder and both timestamp paths. An exponent of 16 384 bits takes 8.7 s a verification against 18 ms; BouncyCastle caps the modulus itself. `CertificateChainValidator.MaxIssuerVerifications` (64) is one allowance a chain walk spends, so no hop asks every candidate again |
| The certification path a revocation walk follows — each certificate under the one whose key signed it, never the list's order | `Signing.Timestamping.DssBuilder.RevocationPath`, built on `CertificateChainValidator.BuildChain`; both the synchronous and the asynchronous walk |
| Which field a signature name addresses — the fully qualified name (§12.7.4.2), or a partial name only one field carries — and the name a report gives a field | `Core.Forms.PdfAcroForm.FindSignatureField` and `PdfFormField.ReportedName`; the verifier, the counter-signer, the DocMDP analysis, the signature listing and the incremental-save report |
| **Joining partial field names** into a fully qualified name (§12.7.4.2) — an empty partial name adds no segment, as `AcroFieldClassification` reads a node with `/T ()` as a widget | [`Core/Forms/FieldNames.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Forms/FieldNames.cs) `Qualify`; the field model, the action dispatcher and runner, and the FDF and XFDF readers. A join that keeps the empty segment names a field `a..b` or `a.`, names no field has. The signer's two walks (adoption, and the names a new field must avoid) take the join together with `AcroFieldClassification`'s "is this a named field" test — a non-empty `/T` — since a `/T`-present test beside the join would let the signer adopt a widget. **A name past `PdfLimits.MaxQualifiedFieldNameLength` (1024) is answered as its stand-in** — its first 64 characters, `#`, and the SHA-256 of the parent's name with the partial name's own digest — because a name is its ancestors' joined, and one parent named by a megabyte over ten thousand fields is otherwise 20 GB of names: two names that differ anywhere stay two, every join agrees because every walker joins here, a long partial name is hashed once, and each field wrapper keeps its joined name while its `/T` and its parent's name are the same objects. A caller addresses such a field by the name the readers list |
| **A font's own streams decoded under their kind's ceiling** — a CMap (`/ToUnicode`, an embedded `/Encoding`) under `PdfLimits.MaxCMapStreamBytes` (16 MiB), a font program (`/FontFile`, `/FontFile2`, `/FontFile3`) under `MaxFontProgramBytes` (64 MiB) | [`Core/Fonts/FontStreamBytes.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/FontStreamBytes.cs); the font model, the renderers' cache, the SVG embedder, the font exporter, the conformance and accessibility checkers. Under the general half-gigabyte a CMap bomb of half a megabyte costs some 3 GB in passing (its bytes, then its text). A refusal throws `PdfFilterException` as a failed decode does, so each reader's own failure path is the one it takes; the font model reads a refused `/ToUnicode` as absent (`PdfFont.LastToUnicodeError`, the renderers' `FontToUnicodeUnread`), since one malformed CMap must not abort a document's fonts. `FontStreamDecodeGuardTests` holds every reader to it: in a file naming those keys, a parameterless `GetDecodedData()` is on another kind of stream, listed with what it reads |
| **A CID of an Adobe collection as text** (Japan1, GB1, CNS1, Korea1) — through the collection's registered Unicode maps, the first that names the CID | [`Core/Fonts/CidCollectionText.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/CidCollectionText.cs) `TextOf` and the public `Decode`; the font model's extraction for a CID font with no `/ToUnicode`, and the PostScript converter's `charpath`, which outlines the character in a host face — one lookup, so an outline is the character the text says it is. The maps `Plugins.CMap` registers are the Unicode encoding CMaps, read backwards (`CMapParser`'s reversed `cidchar`/`cidrange`); several characters encode one CID there, and the reversed map keeps the canonical character over its compatibility form (`AddReversed`: U+4E00 over the Kangxi radical U+2F00, CID 1200), never displacing a producer's own `bfchar` |
| **A glyphs-only TrueType program's text** — a print driver's download (the Microsoft PS Class Driver's Type 2 CIDFont under a made-up collection, no `cmap`, no `post`, no glyph names) keeps the installed font's glyph numbers, its `head` and each glyph's bytes | [`Core/Fonts/InstalledFontGlyphs.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/InstalledFontGlyphs.cs) `TryIdentify`, `TextOf`, `GlyphOf`; the PostScript converter's Type 42 fonts, for a glyph nothing else names. Found by evidence, never by name: a candidate face's `head` agrees in every field a copy keeps (all but `checkSumAdjustment`, `indexToLocFormat`, `glyphDataFormat`, which a re-assembled program recomputes), and a glyph's text is taken only where that face's glyph of the same number has the same bytes, a few zero bytes of padding aside. The installed fonts are indexed by `head` once a process (each file's directory and `head` read, nothing more) and again when the search path changes; a candidate is read whole only when asked, under `MaxFontProgramBytes`. The reverse `cmap` keeps the lowest code point and never a private-use or control one. A glyph no installed font matches has no text, and the converter says so once a font |
| **A CFF Type 2 charstring's path, width and stems** (Adobe TN #5177) — and the five path operators (`rlineto`, `hlineto`/`vlineto`, `rrcurveto`, `vhcurveto`/`hvcurveto`) Type 1 and Type 2 share | [`Core/Fonts/Cff/Type2Charstrings.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/Cff/Type2Charstrings.cs) `Evaluate`, drawing into an `IGlyphPathSink`, and [`Core/Fonts/CharstringPath.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/CharstringPath.cs). Base's `Type2CharstringEvaluator` hands it a SkiaSharp path builder (`SkiaGlyphPathSink`); `CffFontSet.Outline` and `CidOutline` collect a `GlyphOutline` for the PostScript converter's `charpath`, each glyph run once and spending the font's own step budget (`MaxCharstringStepsPerFont`). In Core because the plugin references Core alone: an outline a converter computed with a second evaluator would be a glyph the renderer does not draw |
| **What an action says, and which `/AA` key is which trigger** — the target of each action type (`/UF` before `/F` through `PdfAttachmentManager.FileSpecName`; a `/Win` launch; a `SubmitForm` URL; `[array:n]` for an explicit destination; a `/JS` text stream decoded as a text stream, §7.9.3) and Tables 197–200's key ↔ name | [`Operations/PdfActionTypes.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/PdfActionTypes.cs) `DescribeTarget`, `Triggers`, `AdditionalActionKey`; `PdfActionDispatcher` describes through it and `PdfActionRunner` raises through it. A parse that "mirrors" another disagrees with it: on action types, on decoding a script stream as a text stream rather than UTF-8, on the catalog's `/WC` and the annotation triggers `/PO`, `/PC`, `/PV`, `/PI` |
| **Every action a document holds** — the `/Names /JavaScript` scripts first (§12.6.4.17), the `/OpenAction`, the catalog's and pages' `/AA`, the fields', the annotations' the field walk did not reach, the outline items'; each one's `/Next` chain; an `/AA` entry under a key its table does not define, under that key; one document-wide ceiling beside the per-chain budget | [`Operations/PdfActionDispatcher.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/PdfActionDispatcher.cs) `ExtractActions`, each action carrying its `Source` and `PageIndex`. A walk that stops at the fields would leave out the places a document usually keeps a `/Launch` or a script, and a caller would read a partial list as complete. `ExecuteDocumentScripts` runs the `Document`, `Page` and `Field` scripts of this list and no annotation's or outline item's, which run when the user activates them |
| **Which dictionaries hold `/A` or `/AA`** — the catalog, each page, each annotation with a widget told apart from the rest, and every node of the field tree — for PDF/A's A04 and A23 and PDF/X's X07; and **one entry per violation message**, counted, naming the first | [`Plugins.Conformance/ActionOwnerWalk.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/ActionOwnerWalk.cs) and [`ActionFindings.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/ActionFindings.cs). Not the dispatcher: these rules are about the keys (the published profile's `containsAA == false`), so an `/AA` that holds no action still breaks them. The action **types** both validators read from `PdfActionDispatcher`, every chain followed, not a `/Names /JavaScript` key or the head of an `/OpenAction` or `/A`. The rules per part are the published profiles', because ISO 19005 is not in the repository — each message cites the clause and the rule id |
| **Which XMP properties a PDF/A packet may hold, and in what shape** — PDF/A-1 against the XMP Specification of January 2004 (ISO 19005-1 6.7.9), PDF/A-2 and -3 against June 2005's (19005-2/3 6.6.2.3.1), ISO 19005's own `pdfaid` and `pdfaExtension`, and whatever the packet's extension schema container describes; each value's RDF shape (simple, `rdf:resource`, `parseType="Resource"`, an `rdf:Description`, property attributes, an array, a qualified `rdf:value`) against its declared type; the container, Schema, Property, ValueType and Field descriptions' required fields and prefixes (6.7.8, 6.6.2.3.3) | [`Plugins.Conformance/XmpPropertyRules.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/XmpPropertyRules.cs), [`XmpSchemas.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/XmpSchemas.cs), the table generated into `XmpPredefinedSchemas.Generated.cs` by [`tools/xmp-schemas`](tools/xmp-schemas/README.md). The tables come from the specification's text, never from recollection: the two editions differ (`xmp:Rating` is 2005's), and the conformance corpus is the oracle — it shows two later-edition definitions the published profiles accept (`xmpMM:InstanceID`; `xmp:Rating` as a Real), added by hand with the files that show them. A predefined structure's fields are not checked, since the field tables do not extract cleanly; a value of a type the packet describes uses only the fields its `pdfaType:field` declares, under the type's namespace, each of its declared type. **A writer that adds a property to a PDF/A packet describes its schema**: `pdfuaid:part` written bare would be (rightly) refused by these rules, so remediating a PDF/A file for PDF/UA would break its PDF/A conformance; `PdfUaXmpWriter` writes the description |
| **A `JPXDecode` image's own header against the conformance rules** — every image XObject the pages reach (through `ScanPage`, once per stream), and for each its colour channels (the header's count, a `cdef` opacity channel left out), its colour specifications (how many carry `APPROX` 1, a `METH` outside 1–3, CIEJab — all ignored when the dictionary names a `/ColorSpace`, ISO 32000-2 §7.4.9), its depths (a `bpcc` box, or unequal SIZ depths) and HT code-blocks — for PDF/A's A24–A29 and PDF/X-4's X20–X23 | [`Plugins.Conformance/JpxImageRules.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/JpxImageRules.cs) `EachImage`, `TryRead`. The PDF/X rule asks four of PDF/A-2's five questions — the four a preflight tool's PDF/X check names — so the walk and the reading are shared rather than copied beside each validator. Each validator keeps its own codes, messages and citations, and its own early stop once every code it holds is reported |
| **A graphics state's halftones** — the `/HT` halftone and a type 5's components, each under the colorant its key names, two levels deep — and whether ISO 32000-1 Table 130 requires a `/TransferFunction` there (a non-primary colorant's, as the published profile's rule 6.2.5-6 reads the primaries) — for PDF/A's A57 and PDF/X's X26 | [`Plugins.Conformance/Halftones.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/Halftones.cs) `Visit`, `TransferFunctionRequired`. PDF/A judges; PDF/X names without judging, since what ISO 15930 says of a halftone's transfer function is not available |
| **Optional content configurations** — `/OCProperties /D` and each of `/Configs`, a dictionary listed twice read once; the `/Name` text, the names two share, and how many of `/OCGs`' groups an `/Order` leaves out (nested arrays read, labels skipped) — for PDF/A's A70–A72 and PDF/X's X27; and `UnnamedUnderPdfUa`, the configurations PDF/UA asks a non-empty `/Name` of once `/Configs` holds a configuration dictionary — ISO 14289-1 7.10 and ISO 14289-2 §8.7 state it in the same sentence, so PDF/UA-1's UA58 and the PDF/UA-2 checker read one rule; PDF/A's A70, which asks a name of every configuration, does not | [`Plugins.Conformance/OptionalContentConfigurations.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/OptionalContentConfigurations.cs). The `/Order` walk spends **one document budget** (`MaxOrderWork`) on each entry read and each group compared, because both counts are the file's and they multiply across configurations; a walk cut short is a partial report, not a verdict |
| **Embedded-file specifications** — the `/EmbeddedFiles` name tree's entries, then each file attachment annotation's `/FS`, page by page, a specification both named and attached read once — for PDF/A's A67 and PDF/UA-1's UA59 | [`Plugins.Conformance/EmbeddedFileSpecifications.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/EmbeddedFileSpecifications.cs). The catalog, the pages and the resolver are the caller's, because the PDF/UA-1 checker reads an encrypted document, whose catalog `PdfDocument` does not load. An attachment a reader is not shown is read too: ISO 14289-1 7.11 is no annotation rule, so 7.18.1's scope does not narrow it |
| A signature field's `/Lock` (Table 236) — read when the field is signed, written as the FieldMDP reference (§12.8.2.4), its `/P` taken as the DocMDP level — and whether a locked field changed afterwards | [`Signing/SignatureFieldLock.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/SignatureFieldLock.cs) on the way in, [`Core/Document/FieldMdpAnalyzer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/FieldMdpAnalyzer.cs) on the way out, beside `DocMdpAnalyzer`, whose revision reading, canonical value comparison **and `/Reference` reading** it shares. Table 252 says that entry is an array and producers also write a lone dictionary: two readers taking the two spellings differently would make such a file **certified but not locked** — the lenient reading governing what *grants* and the strict one discarding what *enforces*. `DocMdpAnalyzer.SignatureReferences` is the one reader |
| A signature field's `/SV` (Tables 237–238) — what the signature's sub-filter, digest, reason, level and timestamping server are, and whether the certificate qualifies — settled once before a byte is written | [`Signing/SeedValueConstraints.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/SeedValueConstraints.cs); `PdfSigner` reads every one of those five from its `Choices` and nowhere else |
| Finding indirect objects in raw bytes, with no cross-reference table — where each one ends, read from its tokens — for `PdfRepair` and FDF import. The `N G obj` recognition beneath it is the parser's, `Core.Parsing.ObjectHeaderScanner` | `Base.Operations.Documents.RawObjectScanner` |
| One character code's text, for a caller that has split a shown string into codes: decoding each code by copying its bytes into a fresh array and building a `StringBuilder` and a string for one character would be, on a hundred-thousand-show document, a third of plain extraction's allocation (§13b's measurement ranks the extractors' per-show cost above everything else). A font answers from a per-code cache (a simple font's 256 slots, filled once — its decode tables are set in the constructors and never after; a composite font's dictionary keyed on value *and* length, since a one-byte 0x41 and a two-byte 0x0041 are different codes under one codespace, up to `MaxCachedCodes` so a hostile font showing two billion codes holds no string for each); the base rebuilds the bytes big-endian in the code's length | `PdfFont.Decode(CMapCodec.Code)` (over `Decode(byte[])`); `FontCache.DecodeCode` |
| Text drawn again at nearly the same place — an independent implementation's duplicate-overlap rule — **per glyph on both sides**, structured and plain alike. Indexing whole show operators would return a layer drawn word by word over lines drawn whole twice; the index needs each glyph's own device-space position, which means the advance must compose through `Tm` (§9.4.4) rather than being added to `Tm.E` | `Core.Text.OverlappingTextIndex` |
| Whether a whole-file rewrite would invalidate a signature. The optimizer, sanitizer, linearizer, both conformance converters and the encrypted round trip ask it and refuse unless the caller allows it; a rewriter that answers differently, or not at all, is the defect this exists to prevent | `Core.Document.PdfSignatureDetection` |
| What `SigningNetworkOptions.Timeout` means — null keeps the transport's built-in, non-positive disables, overlong is clamped — for the TSA's `HttpWebRequest` and the revocation fetches' `HttpClient` alike, and the one per-URL budget every OCSP/CRL fetch runs under | `SigningNetworkOptions.EffectiveTimeout`; `DssBuilder.RevocationBudget` / `RevocationBudgetSource`. **The built-in constant has one reader**, so a caller's longer timeout cannot be capped by a fetch site's own cancellation again |
| Which signature certifies a document, by either spelling (`/Perms /DocMDP`, or a field's `/Reference`) — for the full DocMDP analysis and for a caller that needs only *is this certified and at what `/P`* (the image replacer), which must not run a modification verdict against a length that, for a decrypted copy, describes another file | `DocMdpAnalyzer.FindCertification` behind `Analyze` and `TryReadCertification` |
| A widget's `/MK /R` (Table 192): the reading, the upright layout box, and the turn — written as a form's `/Matrix` by the form filler and the flattener, applied to their own placements by both renderers' no-`/AP` fallbacks. **The renderers apply no turn of their own** to an `/AP`: §12.5.5's fit has no rotation step, and a producer's `/Matrix` already carries it | [`Base/Operations/Forms/WidgetRotation.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/WidgetRotation.cs) |
| Where a file's `%PDF-` header is — the origin every offset counts from (§7.5.2), and its 1024-byte search window — for the parser and for the incremental writer, which counts an appended revision's offsets from the original's header **where it sits in the output** | [`Core/Parsing/PdfHeaderLocator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Parsing/PdfHeaderLocator.cs); a missing header folds to origin 0 on both sides |
| Whether a byte array is an ICC profile at all — the 132-byte floor, the size cap, the `'acsp'` signature — and its header fields (device class, data colour space, PCS, major version), for the colour evaluator, its B2A destination reader and the PDF/A-1 profile check (A21). `ParseB2A` asks it too | `IccProfile.TryReadHeader`; public face `IccProfileComponents.TryGetHeaderFields` |
| Which cross-reference sections a document's chain reaches, at their stream positions — every section the walk read (`CrossReferenceSectionPositions`), and one per revision with a hybrid file's `/XRefStm` stream left out (`RevisionSectionPositions`, §7.5.8.4). **The incremental-save report reads the second and re-parses nothing**: following `/Prev` by hand would re-parse the whole tail per hop | `PdfParser`, recorded once per section read by `RecordSection` |
| A document's declared PDF/UA part and revision — `pdfuaid:part` / `pdfuaid:rev`, in **either** the element or the attribute form — for UA03, UA2-5 and `PdfAccessibility.Audit`'s `Auto` target. Three readers, one implementation | [`Plugins.Conformance/PdfUaXmpIdentifier.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/PdfUaXmpIdentifier.cs) |
| Whether a dictionary is a signature dictionary — `/Type /Sig`, `/Type /DocTimeStamp`, or a `/ByteRange` beside a `/Contents`, since `/Type` is optional — and the §7.6.2 rule drawn from it, that its `/Contents` alone is written and read unencrypted. **Public**, because Encryption cannot see Core's internals and the writer, the decryptor and the whole-file encryptor must answer it identically; `DocMdpAnalyzer.IsSignatureValue` forwards to it | [`Core/Document/SignatureDictionaryShape.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/SignatureDictionaryShape.cs) |
| OOXML's five page-number format names (`w:pgNumType /@w:fmt`) → the alphabet each one writes, for the Builder's PDF exporter and DocxToPdf's legacy renderer. The **vocabulary** is the shared part; the arithmetic is two primitives both already reach. **The match is exact and case-sensitive**, because ISO 32000-1 Table 159's page-label styles are a different vocabulary over a *repeated-letter* alphabet and a loose match would honour its `A` as `upperLetter` | `Core.Util.OrdinalNumbering.FormatOoxmlNumber`, beside `ToRoman` / `ToLetters` |
| A CLUT's own answer for a set of components — the input curves, then multilinear interpolation over the 2ⁿ corners of the enclosing cell — stopping **before** the output curves, which is the first step where A2B and B2A stop agreeing. A remark calling a second copy "a split rather than a second implementation" is a duplication report | `IccLut.TryInterpolateClut`, the shape `IccLutPipeline` already used for its own two directions. The v4 pipeline keeps its own because its grid is **per-axis** where this one is a cube |
| One period of a tiling pattern (ISO 32000-2 §8.7.3.1): `/XStep` by `/YStep`, anchored at the cell's `/BBox` bottom-left corner, holding every cell copy that reaches into it, in lattice order, at most `MaxCopies` (past that, the cell once and a `PatternApproximated` report). A `/BBox` larger than its step overlaps its neighbours: clipping each cell to its tile loses the overlap, and repeating the cell at its own size is the wrong period — with a 0.8 % drift wherever the step is not a whole number of device pixels, when the tile is mapped at the device scale rather than its own. Both renderers build the tile from one lattice, so they agree on the copies and on which overlapping cell is on top | `Rendering.TilingLattice`; `ContentStreamInterpreter.RenderTilingPattern`; `SvgContentWriter.ResolvePatternFill` |
| A combined fill and stroke is one object to ISO 32000-2 §11.7.4.4 (`B`, `B*`, `b`, `b*`, text modes 2 and 6): a non-isolated knockout group unless both overprint parameters are set with equal alphas, which is a non-knockout group painted at alpha 1 and composited at the alpha and blend mode. A stroke painted over the fill would show the fill through a translucent stroke's inner half; the raster renderer clips the fill out of the stroke's outline and the SVG writer masks it with the path filled white and stroked black. The decision is one leaf, reached from both renderers' state. Text tests use `/TK false`: the default text-knockout layer knocks the fill out by itself and would hide the rule's absence | `Rendering.FillStrokeCompositing.Of`; `PdfRenderContext.FillThenStroke` (paths, pattern halves included) and `InOverprintFillStrokeGroup`; `SvgContentWriter.EmitFillKnockedOutByStroke` (paths) and `EmitTextPaint` (both text routes) |
| Overprint simulation in the raster renderer is the page's separation plates recombined (the owner's choice): a separation plate already composites every case per colorant, so recombining covers shadings, patterns, colorant-set images, spot inks, soft masks, groups and clips at once, where a per-paint compositor reading back sRGB pixels covers solid DeviceCMYK fills only. Only a page that overprints takes the route (`PageOverprints` walks every way an ExtGState is reached, soft-mask groups and annotation appearances included, and answers yes past a bound); tiles take it too, so a mosaic agrees with its page. Spot inks are recorded with the colour space that named them, so the recombination can evaluate each one's appearance; inks mix by multiplying. An ink's appearance is the ink alone: one named only in a DeviceN space is read through the Separation its attributes' `/Colorants` define where they define one (§8.6.6.5 — a DeviceN's tint transform describes the inks in combination; the reference renderer slices the DeviceN instead; the specification's sentence decides). Inside a plate pass no overprint report is raised — a plate is the exact answer. The SVG writer, with no plates, keeps its per-paint approximation and declines; ask before "re-aligning" the two | `Rendering.OverprintPreview`; `PdfSeparationRenderer.RenderPlates`; `SpotColorantList.SpaceOf` |
| The sample buffer an OCR plugin is handed — the component count and bit depth it is *selected* by, the size refusal, and the packing its bytes are *written* under, which must be the same constants | [`Base/Images/OcrSampleBuffer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Images/OcrSampleBuffer.cs) |
| **A real past `float`'s range** — finite as a `double`, infinite as the `float` both renderers draw with. Unguarded, an SVG writer would write `stroke-width="Infinity"` and `transform="matrix(NaN,…)"`, which a viewer drops for its default — a one-pixel stroke, an untransformed glyph. An operator with such an operand is skipped at each dispatcher's door, a graphics-state entry (`/LW`, `/ML`, `/D`, `/Font` size, a border width) reads as absent, a form, appearance stream, pattern or annotation `/Rect` holding one is not drawn and is reported, and a page box holding one reads as none | `MatrixGuard` (`HasNonFiniteOperand`, `TryFinite`, `HasNonFiniteNumber`, `PatternGeometryNotFinite`); `PageBoxMetrics.Of` |
| The half-em fallback for a **non-finite glyph advance** — a corrupt charstring, or an overflowing `Tf` reaching the same place by multiplication. Composing NaN or Infinity into `Tm` poisons the run, so every later glyph draws at non-finite coordinates and is silently invisible. Copies could drift on the predicate **and** on the fallback | `MatrixGuard.SanitizeAdvance`, beside `IsFinite` / `AllFinite` in the same leaf |
| What the encryption dictionary's `/P` denotes — the **low 32 bits**, because §7.6.3 Table 22 defines it signed while producers write the unsigned spelling of the same pattern, and a `double`→`int` cast **saturates** rather than truncating. Key derivation, `EncryptionInfo.PValue`, `/Perms` authentication, the PDF MAC seal and **both halves of the PDF/UA audit** read it here; a saturating read would pass a document that *denies* accessibility extraction | [`Core/Document/PdfPermissionValue.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfPermissionValue.cs) — `Read` for a dictionary, `FromNumber` for a value the caller already resolved |
| **A PDF flag word, of which `/P` is one** — annotation `/F` (§12.5.3) is the other: a 32-bit field producers write in either spelling. The low 32 bits of an **integer**, and `null` for anything else. Readers that read it differently **disagree on visibility, not only on the number**: `IntValue` saturates, so `/F 2147483648` — bit 32 alone — would become every named flag and hide the annotation, while a reader over `LongValue` draws it; without the integer test `/P 1e30` would saturate to all ones — every permission granted — and pass a document whose permissions nobody can read. A non-integer is `null` in both, which each caller turns into its own default | `PdfNumber.FlagWord`, with `PdfPermissionValue.FromNumber` and `PdfAnnotationFlags.FromNumber` as its two faces |
| **The hash of a key a file chose** — a number, a name, a bitmap — under a key drawn once a process. A hash table compares a key with every key in its bucket, so keys a file can put in one bucket make filling the table quadratic, and no budget that charges an insert for its key sees the walk. `double.GetHashCode` (the value's halves XORed) and an unkeyed content hash (FNV-1) are such hashes on every runtime, a string's on .NET Framework, a tuple key's and a LINQ grouping's included; see Target frameworks | `Core.Util.KeyedHash` (SipHash-2-4): `PdfName`, the PostScript plugin's `PsObject`, `Jbig2SymbolEncoder.SymbolKey`; `Comparer` for a tuple key holding a string, `Strings` for a LINQ operator |
| Which nodes of a `Plugins.Maintenance` object map are leaf pages, with §7.7.3.4's inheritable `/Resources` and the object number that named each one (`-1` when written directly). **The walk visits objects, not object numbers** — a walk pushing numbers would end at a directly-written `/Pages` or kid, and page-scoped options would report success over a document they had not touched | [`Plugins.Maintenance/PageTreeWalk.cs`](src/plugins/Tetitulz.Pdf.Plugins.Maintenance/PageTreeWalk.cs), over `PageTreeShape` → `Core.Document.PdfPageTreeShape` for the leaf predicate |
| Following an indirect reference through a loose `Dictionary<int, PdfObject>` object map, bounded at `MaxReferenceHops` — the form `PdfDocument.ResolveObject` cannot serve, because these scanners hold no `PdfDocument` | `Plugins.Maintenance.ContentScanGraph.Deref`; `PdfOptimizer.Deref` is a forwarder, which keeps the "every dictionary read in this file goes through here" reasoning at its call sites |
| §9.6.6's encoding ladder for a **simple** font — Symbol and ZapfDingbats overriding whatever `/Encoding` says, a name selecting a standard table, a dictionary taking `/BaseEncoding` (resolving) then its `/Differences` (resolving), and §9.6.6.4's implicit base for everything else, **including an `/Encoding` that is neither a name nor a dictionary**. Two copies would drift at all three implicit-base sites, and byte `0x27` would extract as U+0027 through one subtype and U+2019 through the other | `Core.Fonts.PdfSimpleFont.ReadEncoding`, over `ImplicitBaseEncoding` and `ReadFontDescriptor`. It also answers whether the dictionary named an `/Encoding` at all, which is the one thing `PdfTrueTypeFont.UsesBuiltInEncoding` needs |
| **Which code points a font must cover to draw a run** — the *shaped* run's, not the source text's, because Arabic joining substitutes the U+FE70 presentation forms before a glyph is looked up. A producer that asks for the letters and then draws the forms would choose a face covering بيت and not U+FE91/U+FEF4/U+FE96, which can draw none of them — a refused run, or a space per glyph. **Not the same question as "does this need an embedded font at all"**, which is about what the text *is* — `UnicodeText.CodePoints` and `RtlText.NeedsUnicode` keep the source reading | `Core.Text.TextRunComposer.CodePointsToCover`, over `OpenTypeShaper.Shape` and `FontResolutionRequest.CodePointsOf` |
| Which font file a family and style resolve to — the filename→score rule **and its tie-break**, so the ranked list's head *is* the single answer. Two files tie whenever one filename sits under two roots, which is exactly a per-user font directory shadowing a system one; without one tie-break, which face a page draws would depend on the order the directories are listed in. Within a style rank a file under a `FontSearchPath` directory comes first, then the shorter suffix, then the ordinal path; and the family asked for by its own name is tried before its substitutes (`CandidateStems`) | `Core.Fonts.FontFileLocator.MatchStems`; `MatchStem` is its head |
| The composite-font object graph a `TrueTypeFontProgram` is embedded as: Table 122's descriptor, Table 117's descendant with its Adobe-Identity-0 `/CIDSystemInfo`, Table 114's Type 0 dictionary, §9.6.4's subset tag on **all three** names, and the Flate'd stream each points at. The `glyf` and CFF branches differ in exactly three things — `/FontFileN`, the descendant `/Subtype`, and `/CIDToGIDMap`, which Table 117 gives only to CIDFontType2 | `Core.Writing.Type0FontBuilder` — `Descriptor`, `DescendantCidFont`, `Type0Dictionary`, `FlateStream`, `Tagged`, `Add` |
| The dictionaries **every** composite-font writer shares, whatever it embeds or names: Table 116's `/CIDSystemInfo`, Table 117's common descendant entries (`/Type`, `/Subtype`, `/BaseFont`, `/CIDSystemInfo`, `/FontDescriptor`) and Table 114's Type 0 font over one descendant. Each writer adds what is its own after them — `/DW` and `/W`, `/CIDToGIDMap` for a CIDFontType2, `/ToUnicode` — so a key a strict reader requires of every composite font is written in one place. Public because the PostScript converter, outside Core's `InternalsVisibleTo`, writes three kinds of composite font and a CMap stream's collection | `Core.Writing.CompositeFontDictionaries` — `CidSystemInfo`, `IdentityCidSystemInfo`, `DescendantCidFont`, `Type0`; used by `Type0FontBuilder`, Base's `OcrTextLayerFont` and the PostScript `PdfResourceCollector` |
| The PDF version a feature needs, and a header raised to what the file uses. ISO 32000-2 marks each feature "(PDF 1.x)" beside its entry, filter or operator; the table quotes those marks and nothing else, so a floor is the specification's, not recollection's. **The objects decide**: `PdfDocumentBuilder` builds every object first, scans them (`Scan` walks the direct objects iteratively and follows no reference, since every indirect object is scanned where it is written), adds what its content streams do that no object shows (marked content, optional content, an artifact's `/Subtype` — `RequireContentFeatures`, each transparency group's content included), and writes the header last: the version `WithVersion` asked for, raised to that floor and never lowered. A header below its content is read by a reader of that version as a file it can open, and it cannot. The producers' `Version` option (`PdfExportOptions`, `HtmlToPdfOptions`, `DocxToPdfOptions`) is null by default — the lowest the content needs, at least 1.4 — and a raise is reported (`BuilderDiagnosticKind.VersionRaised`, `DocxToPdfConverter.DiagnosticReported`) only when the caller asked for a version: unasked, nothing deviates. The external engines write their own header. Two writers keep a literal that stands above anything they can write: `PdfTaggedDocumentBuilder`'s 1.7 (PDF/UA-1's base, over at most an OpenType font's 1.6) and `PdfLayoutTable`'s 1.4 (uncompressed standard-14 text) | `Core.Writing.PdfFeatureVersions` (`Scan`, `Floor.HeaderFor`, `RaiseReport`), for `PdfDocumentBuilder`, the legacy DOCX renderer's `AssemblePdf` and the PostScript converter's `PostScriptDocumentWriter` (each builds every object before its header for the same reason), `SinglePageDocumentWriter` (its built objects; its text objects are the generators' own, nothing past 1.4), `PdfCompressedFileWriter`, the encryptor's `DetermineMinimumVersion` and the optimizer alike; `PdfContentStreamBuilder.RequireContentFeatures`. Public because Encryption and a plugin read its floors; the scan is internal |
| §7.5.8's cross-reference **stream** field layout — the widths, the `/W` that declares them, the `/Index` run-length compaction and the record packing. **`/W` declares what the packer writes**, so a half-changed pair is not corrupt in any way a writer can notice: it parses, and every row after the first names the wrong object | `Core.Writing.XrefStreamFields`, for the appended-revision writer and the whole-file one alike. `PdfXrefFormat` is §7.5.4's **table** counterpart and stays separate: a table's fields are fixed-width decimal text, a stream's are sized to the values they carry |
| §7.10.2's `/Decode` step for a Type 0 sampled function — one implementation for all three input arities, since nothing about `/Decode` depends on how many *inputs* a function takes, so three copies could only agree or be a defect | `Core.Functions.PdfFunctionEvaluator.ApplyDecode` |
| The ambient context an SVG writer runs under — hidden OCGs, the shared `<defs>` registry, the render options and the cancellation token — set once at the page and **inherited**, with the depth step, by every writer running nested content (form XObject, tiling pattern, soft mask, Type 3 glyph). **Forgetting a field is a defect**: a writer missing the token honours cancellation for content and ignores it for an enormous `/AP` — and `Shared` owns the id counter, so a writer that misses it mints colliding element ids | `SvgContentWriter.SetAmbient` at the root, `InheritFrom` for a child. The **graphics state is deliberately not in it**: three sites clone the parent's and the tiling pattern's depends on whether the pattern is uncoloured |
| What a widget **shows** for its value — a password text field's characters as asterisks, one per text element (ISO 32000-2 Table 229 bit 14: they "shall instead be echoed in some unreadable form"). Both renderers' no-`/AP` fallbacks and the flattener's §12.7.3.3 regeneration draw through it; the staleness test deliberately does not, because whether a value *exists* is not a question about how it looks. **The filler and the field builder do not mask — they refuse**, since the same table says a processor must never store that value | `AnnotationFallbackModel.DisplayedWidgetValue`, over `ReadWidgetValue` |
| §12.7.4.3's field layout — multiline and comb are **text-field** flags, and comb also needs a single line, no password and no file-select. The filler, the flattener and `FormAppearance.Compose` read it; a copy that drops the text-field test lays a choice widget carrying bit 13 or 25 out one way when flattened and another when filled | `FieldLayout.For` |
| Writing AcroForm fields with appearances. **There is one field author, in Base, and a producer that cannot reach Base reports where fields go instead of writing them.** The managed HTML layout is the case: Builder may reference only Core, so its exporter reports `PlacedFormFields` (page and highlight box, in default user space) and `Plugins.Html` hands them to the builder. An exporter writing widgets whose `/AP` is the control's own static drawing would be a second appearance generator, however sourced. **A batch is one revision**: through single-field calls a fifty-field form would be fifty cross-reference sections, and the appearance-font memo would never be asked for a font twice | [`PdfFormFieldBuilder.AddFields`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/PdfFormFieldBuilder.cs); Builder's `PdfExporter.PlacedFormFields` |
| A text-markup annotation's /QuadPoints quad (§12.5.6.10) and the ordered /Rect (§12.5.2) a caller's two corners normalise to — three writers build the quad, five build the rectangle, and the appearance path's own Math.Min is **not** in the cluster because it derives a box origin from a /Rect already built. **The quad is deliberately not derived from the rectangle**: the rectangle's order is required, the quad's order is the caller's statement about the span | PdfAnnotationWriter.QuadPointsFor / NormalisedRect |
| **A CSS absolute length** (CSS Values §6.2: `pt`, `px`, `pc`, `in`, `cm`, `mm`, `q`) in points or in CSS pixels; a relative unit is refused, so each caller resolves it against its own context | [`Core/Text/CssLength.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/CssLength.cs) `TryToPoints` (HtmlToPdf's `ParseLength`) and `TryToPixels` (its `ParseMediaLength`, and `SvgToPdfConverter`, whose user unit is a CSS pixel). A converter trimming unit letters and keeping the number would read `10mm` as 10 user units. **Each form keeps its caller's arithmetic operation for operation**, bit-identical to the arithmetic it stands for. The pixels per pica is its own constant (16) because the media parser's `n * 16` rounds once, and the chained `n * 96 / 72 * 12` does not always agree (511.139 pc) |
| **A PDF affine matrix** `[a b c d e f]` (§8.3.4): composition on row vectors (the left factor is applied first, so an inner `cm`'s matrix is the left factor of the CTM it makes with the outer), a point under it, the matrix taken about a point (CSS `transform-origin`: the point moved to the origin, the matrix, the point moved back — a translation is unchanged by any origin), and the axis-aligned bounds of a rectangle's four turned corners | [`Core/Writing/TransformMatrix.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Writing/TransformMatrix.cs). HtmlToPdf's mapper composes a `transform` list with it; the Builder exporter writes a `Paragraph.Transform` or `BoxStart.Transform` about the block's origin with `About` and remaps the link and form-field rectangles the block registered through `BoundsOf` — a `/Rect` is in default user space, which a content-stream `cm` does not reach, so the content-space matrix is taken back through the export scale first. A matrix written as composed, about the page's origin, would pass every one-function test, because an origin leaves `a`–`d` and a translation's `e`/`f` alone; a block box writes its matrix when its fragment closes and the height is known, moved back to the mark taken at its opening |
| **A CSS colour value** — hex in its four lengths, `rgb()`/`rgba()` with numbers or percentages, `hsl()`/`hsla()`, the 148 named colours, ASCII digits only; `false` for anything else, so each caller decides what absent means | [`Core/Text/CssColor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/CssColor.cs) `TryParse`; HtmlToPdf's `TryNormalizeColor` (drops the declaration) and `SvgToPdfConverter` (reports, draws black). SVG 1.1 §4.4 takes its keywords from CSS, so the SVG converter reads all 148 names through it. Reached from HtmlToPdf through Core's `InternalsVisibleTo`, as DocxToPdf reaches `FontFileLocator`. **`transparent` is a colour here** (alpha 0), and `IsFullyTransparent` is the one answer to "does this paint nothing": `rgba(…, 0)` composited over white would paint white boxes and borders. A caller treats alpha 0 as absent; never composite it |
| Axis-aligned box intersection, for the spatial region queries **and** `PdfPageTextLayout.Crop`. Two copies under a comment calling them "the same predicate" — a duplication report — drift on the **contract** rather than the arithmetic: a query that does not refuse a negative extent, where a negative width inverts the interval and still matches a word that *spans* the gap, so the caller is handed words from a region it never described | `PdfSpatialQuery.RectIntersects`, with `RequireNonNegativeExtent` beside it |
| A bounded walk of the structure tree, and how much of it a walk may read — the node and **edge** ceilings. Four readers walk the same tree from the same untrusted input (`Accessibility.Internal.StructTreeWalker`, `Base`'s `PdfTaggedTextExtractor` and `PdfStructureTreeReader`, the PDF/UA checker), and separate walks let one bound total work while another does not: a depth cap bounds a branch and a visited set bounds expansions, so N elements sharing one N-entry `/K` array cost N each with **both** guards satisfied and the depth at three. O(N²) out of O(N) bytes — and in the reader, where each MCID slot becomes a result element, O(N²) memory too. The walk is one, and the ceilings are its | `Operations.Structure.StructureTreeWalk` (`Enter`, `Kid`, `Leave`, `Report`), reading `PdfLimits.MaxStructTreeDepth` / `MaxStructTreeNodes` / `MaxStructTreeEdges` |
| An `/XYZ` explicit destination (§12.3.2.2) and **how it names a page**. Core's document builder, the named-destination, initial-view and outline writers, and DocxToPdf's headings build the array, `null` meaning "keep the viewer's value". A page with no object number (a page dictionary written inline in `/Kids`) is the case copies disagree on — a writer writing no destination makes a bookmark that points nowhere. `PageOf` is the one answer — the index, which every destination reader resolves | `Core.Writing.XyzDestination` — `Build`, `PageOf` |
| A `/ToUnicode` CMap for two-byte codes (§9.10.3) — preamble, the full two-byte codespace, 100-entry `bfchar` sections, trailer. The composite-font writer and the OCR text layer both write through it. Pinned byte-for-byte and read back by `CMapParser`, because a relative assertion cannot see a change both consumers make together | `Core.Writing.ToUnicodeCMapWriter` |
| A destination array's page (§12.3.2.2): a page reference, or an integer index as a remote destination writes it, with object 0 and a non-integer refused. A private copy would match object 0 against a page written inline and miss an index. **A document part's `/Start` and `/End` are the same question** (§14.12, Table 409: "an indirect reference to the page object"): a reader that resolves the reference and then reads it as a number gives no conforming document a page range. **A page reference is found through `PdfDocument.PageIndexOfObject`**: a scan of the pages for every destination would make links, outline items, named destinations and document parts cost destinations × pages; the tagged text extractor asks the same index | `Base.Operations.PdfDestinationPage` — `IndexFromDestArray`, `IndexOfPage`; `Core.Document.PdfDocument.PageIndexOfObject` |
| Every image XObject the pages reach — their own resources and form XObjects nested under them, cycle-guarded, once per object. A walk of the pages' own `/XObject` dictionaries alone would miss every image inside a form and convert, and count, a shared image once per page; the image replacer and the colour converter both read this index. It walks every container that paints with resources of its own: form XObjects, tiling patterns (`/PatternType 1`), Type 3 fonts' `/Resources`, and each annotation appearance stream, every state of `/N`, `/R` and `/D`. Without the last three an RGB logo on a stamp would survive a grayscale conversion that reports the document converted. Inline images have no object to list and are not here | `Operations.Images.DocumentImageIndex` (with an `onResources` callback for a caller reading another category over the same dictionaries, and an `onContainer` one for what a container declares of its own — the transparency scan reads a form's or appearance stream's `/Group` through it rather than walking a third time) |
| Which pages use transparency, and those pages flattened. **The scan is the broader of the two validators' readings on purpose** — PDF/A-1's every-`/BM`-entry rule and PDF/X's any-page-`/Group` rule — and **PDF/A-2 to -4's A41 asks it too** ("does this page contain transparency?", a page group alone not counted), through the open-document overload of `FindTransparency`; an annotation's own `/BM` counts (PDF/A-4 6.2.9 reads it) and, like its `/CA`, is left to the viewer rather than flattened; because a flattener that misses a page either rejects hands a converter a document still failing its validator, and flattening a page that does not need it costs sharpness, never correctness. The flattener replaces each such page's content with a raster of it (the renderer composites every construct already) and lays the page's text back as invisible text; **annotations are kept as annotations, and the raster is drawn without them** (`PdfRenderOptions.ContentOnly`), or a half-opaque annotation would be drawn twice. What it leaves is found by scanning the output, not assumed. **Its words stay spaced only because plain extraction reads a same-line `Tm` jump as a word gap** — an OCR-style layer places each word by its own text matrix, and without that reading the extractor would run them together. **Region flattening (`KeepOpaqueContentAsVector`) takes a transparent operation out in the way that keeps every other operation's meaning** — a path's paint becomes `n`, so a clip it set still clips; text is shown in mode 3 (or 7 where it clips), so the text position still advances and the text still extracts — and the image covers the bounds the renderer measured for exactly those operations, so inside them it shows everything and outside them nothing changed. A pattern or Type 3 font reaching transparency paints it from an operation that looks opaque, so such a page is flattened whole rather than guessed at. The rewritten content is closed (`ET`, `Q`) and isolated in `q`/`Q`, because a page's own `cm` outside any `q` would otherwise move the image | `Operations.Documents.TransparencyScan`, `PdfTransparencyFlattener`, `TransparencyRegions`, `ContentOperationWriter` |
| A class of content — text, images, vector graphics — taken out of every page (a filter by content class). **The way an operation leaves a stream is one rule, `ContentOperationRemoval`, shared with the region flattener above**: a path's paint becomes `n`, clipping text stays in mode 7 with the mode put back, an image, inline image or shading is dropped — a second copy of that rule drifts (the flattener's text is kept invisible, the filter's dropped, which is a parameter of the one rule, not two rules). Every container the pages reach is rewritten once (`DocumentImageIndex`'s walk: forms, tiling patterns, appearance streams), a form stays invoked, and a Type 3 glyph procedure is text — the text class takes it out whole through `Tj`, the others leave it. Written as an appended revision superseding each stream under its own number; nothing to take out copies the input through; encrypted input is refused; a stream that will not decode is left and **named** (`Warnings`, `LastFilterWarning`), never silently skipped | `Operations.Documents.PdfContentFilter`, `ContentOperationRemoval`; `PdfOperations.RemoveContent` |
| A page's elements — each painting operation of the page's own content (a text-showing operation, a path's paint, `sh`, an inline image, a `Do`: a picture, or a form as one element) with the box the raster renderer painted it in, an operation that paints nothing left out, and so is what the document's default configuration hides (`MeasureOperations(…, hideOptionalContent: true)`: nobody sees it to select it); a stroked path's box is its own points and half the line width in force round them, not the renderer's bound with a miter join's room — and the page written again without some, or with some moved (`Transform`: a picture, a form or a whole path inside a `q … cm … Q` of its own, a run through a text matrix of its own with the line and the pen put back after it; the matrix in default user space, acting as `Ctm × M × Ctm⁻¹`; refused for a shading, a path that sets the clip or has another operator among its points — `PathSpans`, one forward pass — and text that adds to the clip). The removal is `ContentOperationRemoval`'s rule with one addition: **a text run that only paints is dropped and the pen moved as the run moved it** — a `TJ` holding one number, from the advance the measuring render recorded (`ShownTextAdvance`: every glyph's width, the spacing, a `TJ`'s own numbers; the text line matrix untouched, a `'` or `"`'s line move and spacing written in its place). **The advance is the file's own (§9.4.4: the font's widths), not the renderer's pen**: for a font that is not embedded the raster renderer moves its pen by the substituted face's measure, which differs from host to host, so what it moved beyond the widths is kept apart (`ContentStreamInterpreter.TakeAdvanceBeyondWidths`) and taken off — the same for where a moved run starts and where the pen is left after it (`OperationPlacement.BeyondWidthsAtStart` / `BeyondWidthsAfter`) — because, unlike the content-class filter, the run after it on the line stays and has no position of its own. The page gets a content stream of its own (the streams it had may be other pages'), and the file is written anew by reachability, so the removed text is not in the output; what the page's resources name stays, and so do metadata, bookmarks and the structure tree — an edit, not a redaction. A signature does not survive; encrypted input is refused; an id that names no element is the caller's error and nothing is written. An id is the operation's index in the page's content and names the same element only in the same bytes. **A listing gives each run its text** (`PdfTemplateEngine.RunTexts`: the template engine's own walk through the fonts, by the operation's index — the same parse of the same bytes as the measuring render, so the indices agree), **and the text of runs a reader chose is replaced in their slot by the template engine** (`ReplaceRun`, `MatchMode.Element`: the glyphs of the operations named, which must stand together in the order drawn — a glyph of another operation among them is text drawn between the runs, refused by name — their own text the key, so `TryBuildMatch`'s refusals, the slot's font size, colour and shrink-to-fit are the literal path's; the erasure is not re-read by text, since the same words elsewhere on the page would be taken for a miss). A second erase-and-draw outside the engine would place the new text by a box, not by the slot's own matrix | `Operations.Documents.PdfPageElements`, `Rendering.ShownTextAdvance`, `ContentStreamInterpreter.OperationExecuted`, `PdfTemplateEngine.RunTexts` / `ReplaceRun`; `PdfOperations.ListPageElements` / `RemovePageElements` / `TransformPageElements` / `ReplacePageElementText` |
| Text drawn on an existing page as its own content (`PdfOperations.AddPageText`): the lines of a caller's text, each shaped once through `TextRunComposer` and written through `GlyphRunShowText`, in one text object of a content stream appended to the page in the page's initial state, bracketed by `q`/`Q`. **The point given is where the pen goes down**: a left-to-right line runs right from it and a right-to-left one left, since that is where a writer starts either; a direction said is the line's whatever its letters. **Upright as the page is shown**: the text matrix carries the page's `/Rotate`, so a line added to a page shown turned reads across the page the user sees, and the next line is below it as seen. **The font is always embedded**, a subset of the glyphs drawn, found by the one resolver (the caller's provider, then an installed font only where the caller allows it) and registered under a resource name the page does not use, so the page's own font of that name stays the page's. **Refused whole**: with no font to embed, or a character the font cannot draw — a space included, which the font search does not ask for and the shaping does — nothing is written, because a line with a letter missing is worse than no line. A revision is appended, so the file as it was is the prefix of the file written; an encrypted document is refused before a font is sought. Each line is afterwards an element `PdfPageElements` lists, moves, edits and removes. | `Operations.Documents.PdfPageTextWriter` |
| Merging into an existing XMP packet, and the revision that makes a packet the document's `/Metadata`. `PdfDeclarationsWriter`, Accessibility's `PdfUaXmpWriter` and `PdfMetadataWriter.WriteXmpMetadata` all go through it; each give-up is reported through a callback, the catalog and stream keep their own generations, and the cross-reference section keeps the document's format. **The padding is the caller's** (`Serialize(doc, padding)`): the PDF/UA writer's byte-identical-rerun contract rests on its fixed 2048 bytes. **An existing `/Metadata` is superseded only when it resolves to a stream the index names**: taking the reference's number on trust, `/Metadata 2 0 R` naming the page tree would have the page tree overwritten. **`Append` refuses a revision that wrote nothing**: `DocumentRevisionWriter` reports a failed write and returns the original bytes, which a caller must not return as success. **The two writers that answer `false` and pass the input through share one pipeline, `Write`** (open, refuse an empty, encrypted or unparsable input, ask the writer's plan, append): a plan that finds the packet already current returns no packet, and the output is the input's own bytes. **A packet's bytes are decoded no further than `MaxExistingPacketBytes` (2 MB) by every reader** (`TryReadPacketBytes`): a length compared after a whole decode, or a decode under the 512 MB per-stream ceiling sized for images loaded into a DOM, lets a few kilobytes of flate cost hundreds of megabytes. The conformance plugin, outside Core's internals, asks the same ceiling through `XmpStreamBytes` over the public `TryGetDecodedData` and `PdfDeclarationsWriter.MaxExistingPacketBytes` | `Core.Metadata.XmpPacketDom` (internal — Base and Accessibility both see Core's internals); `Base.Operations.Metadata.XmpMetadataRevision` (public, because Accessibility cannot see Base's); `Plugins.Conformance.XmpStreamBytes` |
| An existing object's generation — for a revision that supersedes it, and for a reference to it. Signing's `SigningHelpers.GenerationOfExistingObject` forwards to it; a writer that writes superseded objects at 0, or builds `N 0 R` to an existing object, breaks a file whose generations are not 0. **The table is the authority**, not a reference to the object (a damaged file's `N G R` can disagree), and **never a clone**, which carries no identity. The incremental writer applies it to every superseded object it is handed at 0 (`KeptGeneration`); references stay the caller's, because the writer cannot tell a built reference from a copied stale one | `Core.Writing.ExistingObjectGeneration` (`Of`, `ReferenceTo`); `IncrementalUpdateWriter.KeptGeneration` |
| An output intent: the profile stream, the intent dictionary, the catalog's `/OutputIntents`, and `/Trapped` in `/Info`. Every decision a validator checks is made here once: `/N` from the profile's header (a CMYK profile under a caller's default of 3 fails A20), the profile gated (arbitrary bytes are not "cannot verify"), encrypted documents refused, a direct `/Root` refused, an intent without a profile and `/Trapped` written. **An intent of the same `/S` is replaced**, since X11 and A15 reject two, and other subtypes kept (§14.11.5 NOTE 1). The Conformance plugin's `OutputIntentWriter` is a thin adapter holding the converters' fixed entries | `Base.Operations.Metadata.PdfIccEmbedder.WriteOutputIntent` (`OutputIntentSpec`, `OutputIntentWritten`) |
| The size an image this library **writes** may have: `PdfLimits.MaxImageDimension` per side and `MaxImagePixelArea` in all. Every reader enforces them; a bomb authored into output is missed because every other check is on a read path — a 20 000 × 20 000 JPEG frame header written into a page verbatim, a PNG making the same claim decoded at that size, a 65 535 × 65 535 frame header carried from a DOCX. The rule is Core's, so the Builder's exporter and DocxToPdf reach it, and `PdfContentStreamBuilder.AddImage`/`AddJpxImage` ask it on every image, so a producer cannot write past it; one that can skip and report asks first | `Core.Writing.AuthoredImageSize` |
| How much of a DOCX part the converter will inflate. A DOCX is a ZIP the Open XML SDK reads a part at a time into memory; a part with no cap — a zip-bombed `document.xml` — would exhaust the process before a paragraph is laid out. Every entry's declared length is checked before the package is opened (ZIP enforces the declaration as the entry is read, so a lie fails the read), and the image readers read the same limit | `PdfLimits.MaxDocxPartBytes` |
| Deflating raw image samples. `AddImage(filter: null)` deflates them itself, the soft mask with them, when the page is built, so no caller has to remember to compress first; and a document writes an image once for every page that registers the same arrays under the same dictionary — the arrays by identity, never a content hash, so a content-equal copy from another decode stays its own. A DOCX naming one picture two hundred times would otherwise write it two hundred times (2.6 MB from 3 KB); the producers decode each image once, so they hand over the same arrays, and every `AddImage` is still judged by the size rule | `Core.Writing.PdfContentStreamBuilder.AddImage`; `ImageObjectShare` |
| Whether a barcode payload character is a digit. `char.IsDigit` is Unicode's Nd category: an Arabic-Indic or full-width digit passes, and `c - '0'` on it indexes EAN-13's tables in the thousands or makes a Data Matrix codeword far outside 0–255. And a QR version's byte capacity, derived from the codeword table that determines it rather than carried as literals beside it | `Operations.Barcodes.AsciiDigits`; `QrLayout.ByteCapacity` |
| An ECI's character set, and QR's layout. The Data Matrix and QR readers need the same sets, so they are one class rather than a copy. **A writer and its reader sharing a mistake pass every round trip**: format positions transposed from ISO/IEC 18004's, a Data Matrix clock edge in the opposite phase, Reed–Solomon over QR's roots from α⁰ where ISO/IEC 16022's start at α¹ (`GaloisField256.GeneratorBase`) — each written where the reader looks, so only an independent reader sees it. The blocks for every version and level come from Table 9 as Nayuki's generator and an independent reader's `Version.java` give it (all 160 entries agree); the reader is held to an independent encoder's symbols (`QrThirdPartySymbolTests`, an independent encoder's output checked in as module rows) | `Operations.Barcodes.EciCharacterSets`; `QrLayout` (`BlockSizes`, `AlignmentPositions`, `FormatPositionA`/`B`) |
| Brotli decoding (RFC 7932), the WOFF 2.0 reader's and `/BrotliDecode`'s, on both legs. It is in-house rather than an external package, the owner's decision: the context lookups and the 121 transforms are generated from the RFC's text and checked against the CRC-32 values the RFC prints, the dictionary against Appendix A's; the tests' oracle is the runtime's `BrotliEncoder` (Google's), and hand-built streams pin what an encoder does not emit for short inputs (block-type wraps, LSB6/MSB6 contexts, metadata blocks) | `Core.IO.Brotli.BrotliDecoder` |
| Zlib (Flate) encoding — every caller, the colour converter, the font embedder and the OCR plugin among them; a hand-rolled framer is where an unflushed `DeflateStream` writes nothing | `Core.IO.Filters.FlateEncodeFilter.Encode` |
| The UR transform parameters (Table 258) both ways — `UsageRights` flags to names for the signer, names to flags for the reader — and which methods are the usage-rights transform (`/UR`, as Table 256 names it, and `/UR3`, as a common viewer writes it). **A usage-rights signature lives in `/Perms /UR3`** (§12.8.1, "not from a signature field"); a reader walking fields alone would read no Reader-extended document | `Core.Document.UsageRightsTransform`; the reader is `PdfUsageRightsReader` |
| Which text-showing operators paint: the selected font (`Tf`) and render mode (`Tr`), both graphics state and so **restored at `Q`**, with modes 3 and 7 painting nothing (§9.3.6). Tracked without `Q`, body text after an OCR layer's `q … 3 Tr … Q` would read as invisible; an operand that is not an integer from 0 to 7 is not a mode | `Core.Text.TextPaintTracker` |
| MQ arithmetic decoding, for JBIG2 (T.88 Annex E) and JPEG 2000 (T.800 Annex C) alike. JPEG 2000 needs three things JBIG2 does not — a codeword segment's end (bytes past it read as `0xFF`, never the next segment's), a restart that keeps the context states, and non-zero initial states — and they are parameters of the one decoder, because a copy per standard agrees until one of them is corrected | `Core.IO.Filters.MqDecoder`; `MqEncoder` reads its tables |
| MQ arithmetic **encoding**, for JBIG2 and JPEG 2000 alike, with the length a codeword-segment prefix must have for a decoder padding with `0xFF` (T.800 D.4.1) to reproduce the decisions through a coding pass. The intervals nest, so a prefix decodes a pass exactly when the prefix followed by ones lies below the interval's upper end at that pass; the check is made against the *finished* segment (a later carry raises the byte the coder was on), in a frame where that byte's least significant bit weighs 2^32, and a byte after `0xFF` carries seven bits one bit higher. The reference decoder estimates this with "bytes so far + 3"; here it is computed, and the tests hold it to the behavioural minimum on nine passes in ten (`JpxEncoderTruncationTests`). **The padded value must lie inside the interval at both ends, (C, C + A]**: checking only the top, where the byte after an `0xFF` holds a carry, passes a prefix too short, which decodes another symbol — off by one in eight samples on one ISO codestream's round trip. `MqEncoder_EveryPrefixDecodes_EvenWhereACarryFollowsAnFF` walks every prefix to the behavioural minimum with that case seeded | `Core.IO.Filters.MqEncoder` (`Mark`, `PrefixLengths`) |
| JPEG 2000 rate allocation: every block is coded whole, each pass's prefix length and distortion removed recorded (`JpxBlockCode`); the feasible truncation points are the upper convex hull of a block's (bytes, weighted distortion) points; each layer takes, from every block, the hull points above one threshold for the tile, found by binary search over the tile's distinct slopes with the layer's packets written and measured from the saved state of the layers before it — a precinct's packets are in layer order whatever the progression, so a layer's bytes do not depend on the order. **The distortion weights are measured**: a unit impulse through the decoder's own inverse wavelet on a one-row image gives each level's low- and high-pass energy gain (T.800's units — half the reference decoder's tabled high-pass norm, since that encoder scales its high-pass coefficients by two; the level-2 low-pass is 4.122, not the 4.177 the reference decoder tables, confirmed by an independent lifting), and a unit through the inverse ICT, or a large one through the inverse RCT, gives each component's. Writing every layer per trial would be quadratic in the layer count: 65535 layers (Table A.14's most) take half a second this way, four minutes that way | `Core.Imaging.Jpx.JpxRateControl` (`HullOf`, `Allocate`); `JpxSynthesisGain`; `JpxPacketEncoder` (any layer; `JpxEncodedBlock.LayerEnd`); `JpxTagTreeEncoder.Clone` |
| The dictionary a JPEG 2000 file embedded as it is wants (ISO 32000-2 §7.4.9), for every embedder — `PdfImageToPdf`, `ReplacementImage.Jpeg2000`, the Builder's image decoder — as `Core.Imaging.JpegColorSpace` is for a JPEG. A dictionary that names `/ColorSpace` overrides the file's colour specification, so naming `DeviceRGB` for an sYCC or ICC-profiled JP2 would repaint it: the space is named only when the file names sRGB, greyscale or CMYK (or is a raw codestream, which has no box, of 1, 3 or 4 components), and left out otherwise, as the standard provides. `/BitsPerComponent` is stated only when every component has one depth of 1 to 16. A `cdef` or Opacity box that declares an opacity channel is written as `/SMaskInData 1`, the only key that lets a viewer use it (Table 87). The page builder's writer leaves out the keys it is told to; the replacer never inherits the original's | `Core.Imaging.Jpx.JpxImageDictionary` (`TryDescribe`); `Jp2ColourDescription.DeclaresOpacity`; `PdfContentStreamBuilder.AddJpxImage`; `RasterImageInspector` (`Jpeg2000`) |
| Raw samples → JPEG 2000, the in-house codec's public door: interleaved 8-bit, or 16-bit big-endian as a PDF stores them, grey, RGB or CMYK — the counts a PDF colour space and a JP2 `colr` box both name; 2 (grey + alpha) is refused, since no `cdef` is written. **A quality's step is 1 at 100 and doubles every 16⅔ points down**: a step below the integers' own resolution spends bytes on precision the 9-7 coefficients have and the picture does not, so quality 90 would come out *larger* than lossless. A size target is for the file, so the JP2 container's 85 bytes (`JpxEncoder.Jp2ContainerBytes`, asserted against the raw boxes) come off the codestream's share; without that a 787-byte target would give 814. The optimiser under `ImageFormat.Original` keeps a `JPXDecode` image JPEG 2000 | `Core.Imaging.Jpx.JpxImageEncoding` (`Encode`, `StepForQuality`); `JpxEncodingOptions`; `PdfOperations.EncodeJpeg2000`; `Plugins.Maintenance.ImageFormat.Jpeg2000` |
| The wide path: samples of 17 to 38 bits without widening the common decode. Tier 1's workspace is `long` (61 planes; T.800's M_b tops at 37 and an ROI shift adds to it), but a band is decoded into a `long` array only when its M_b plus ROI shift can exceed the 30 planes an `int` band holds, and a component is stored in a `long` plane only past 31 bits — every conformance codestream stays on the `int` arrays, and p0_07 measures the same as a narrow-only decoder (1.9 s tier 1, 2.08 s one thread, 0.36 s on twelve). The steps after the wavelet — rounding, the RCT, the level shift and clip, the non-linearity — run on the `double` planes the wavelet produced, exact for integers below 2^53, and `Store` writes the samples once at the depth they end with; the `int[]` forms of those steps are wrappers over the `double` ones for their callers, never second implementations. **The codestream's ceiling is 37 magnitude bit-planes** (five exponent bits, three guard bits): lossless coding is possible only while the 5-3 coefficients stay within it — 35 bits for any content, since the HH band grows two planes — and the encoder refuses deeper input with the reason rather than write a QCD exponent past 31, which would overflow its byte; lossy coding reaches 38 bits because the step clamps at 2^(R_b − 31). An independent tool's JPEG 2000 plugin returns `I;16` above 16 bits, so the wide fixtures' oracles are a hand-computed 35-bit sample and the decoder's own narrow path | `Core.Imaging.Jpx.JpxBlockDecoder` (`MaxBitPlanes`, `MaxNarrowBitPlanes`, the `long[]` `Decode`); `JpxTier1Decoder` (`JpxBandCoefficients.WideValues`); `JpxComponentTransforms` (`RoundInPlace`, `Store`, `IsWide`, `MaxSamplePrecision` 38); `JpxDecodedImage.WideSamples`; `JpxEncoder.MaxCodedBitPlanes`; `JpxImageEncoding.EncodePlanes` |
| What a JPEG 2000 image says about itself: which framing (JP2 or raw codestream), the JP2 box walk, the SIZ grid and each component's precision and sign, and how many channels are colour (`colr`, else `cdef`). Two walkers would disagree on a 64-bit box length past the end of the data, one refusing it and one clamping it. One public reader clamps, and a test pins that | `Core.Imaging.Jpx.JpxHeader` (public: the plugin is a caller) |
| JPEG 2000 codestream syntax: one SIZ reader (`JpxHeader` and the codestream parser both go through it), the Annex B geometry — equations B-1 to B-18, whose ⌈·⌉ of a negative numerator a truncating division gets wrong — and T.800's precedence for a tile-component's coding and quantisation (tile COC > tile COD > main COC > main COD). The conformance set's reference decodes are the geometry's oracle: p1_07's two components differ in sub-sampling, p0_08's reference is one resolution level down, and each is asserted by size, depth and sign | `Core.Imaging.Jpx.JpxImageSize` (`TryRead`, `TryValidateGrid`); `JpxGeometry`; `JpxCodestream.ComponentCodingFor` / `QuantizationFor` |
| JPEG 2000 tier 2: the packet order (T.800 §B.12 — the standard prints the position-driven loops over every grid sample; the sequence steps by the gcd of the precinct sizes and admits a precinct where §B.12.1.3 says, including the off-lattice first row and column, which a tile origin of (40, 40) with 32x32 precincts exercises: ⌊try0 / 2^PPy⌋ is 1 there, and B-20 counts from it), the packet-header syntax (§B.10: bit stuffing, tag trees, Table B.4, equation B-19), and where a codeword segment ends for each code-block style (Tables D.8 and D.9 — `IsTerminated`, which tier 1 reads rather than re-deriving). No packet states its own length anywhere but its header, so the oracle is that a tile's packets end exactly where its data ends, in the count its COD and precincts declare; PLT, where present, is the second opinion, and p0_07's 24 576 lengths agree | `Core.Imaging.Jpx.JpxPacketSequence`; `JpxBitReader`, `JpxTagTree`, `JpxPacketDecoder`; `JpxTileLayout` (the structures, bounded by `PdfLimits.MaxJpxCodeBlocks`) |
| JPEG 2000 tier 1 (T.800 Annex D): the stripe scan, the three passes, the context rules of Tables D.1–D.4 with Table D.7's initial states, run-length coding, and each code-block style flag; the Maxshift ROI rule (§H.1). **The output convention is fixed and every consumer relies on it**: a value is twice the reconstructed magnitude with the half of E-6 (r = ½) at the last decoded bit-plane, negated when negative — so an irreversible band multiplies by Δ_b/2, and a reversible band takes `Truncate` (E-7 exactly when fully decoded; E-8 otherwise). An arithmetic `>> 1` on the signed value floors negatives one too low. The oracle is the reference decoder's own encoder — lossless fixtures with no decomposition level put each sample, less the DC shift, straight into a coefficient — beside hand-coded blocks whose every decision and context is written in the test (Table D.6's column among them) and coded by the shared MQ coder. M_b per band, from QCD/QCC (E-2, E-5), lives with the quantisation steps. The decoder keeps a flag word per stripe column (the reference decoder's shape) and reads Tables D.1–D.3 from lookup tables — **built from the rule functions `ZeroCodingContext(orientation, h, v, d)` and `SignContext(hc, vc, out xorBit)`, which stay the one statement of the tables**; the encoder's per-sample state (`JpxBlockContexts`) computes its sums and asks the same functions, so a context defect cannot be on one side only | `Core.Imaging.Jpx.JpxBlockDecoder` (`Decode`, `Truncate`, `ResetContexts`, `ZeroCodingContext`, `SignContext`); `JpxRawReader` (§D.6); `JpxQuantizationSteps`; `JpxBlockContexts` (the encoder's state over the same rules) |
| HT code-blocks (ITU-T T.814 clause 7): the five bit-stream readers of §7.1 (MagSgn and SigProp forward, VLC and MagRef backward, MEL big-endian, each with its own stuffing rule; MagSgn and VLC gather bytes into a 64-bit accumulator — a stuffing bit set or a byte past the segment noted at the stream bit where it begins and raised once a read passes it, which is where the bit-at-a-time procedure raised it, `JpxHtReaderTests` holding them to §7.1.2 and §7.1.4 as printed — and a codeword is read from a seven-bit peek, `JpxHtCodeTables.Peek`; the workspace is cleared only where it is read before it is written), MEL symbols (§7.3.3), the quad-pair VLC interleave (§7.3.4–7.3.6, the first line-pair's shared MEL symbol and its one-bit residual among them), predictors and bounds (§7.3.7) and the MagSgn unpacking (§7.3.8), then SigProp and MagRef (§7.4, §7.5). **The cleanup pass's least significant plane is where Part 1's first cleanup pass would be** (§7.6, §B.3: S_blk = P + P0 + S_skip takes the zero-bit-plane count's place), so the output is `JpxBlockDecoder`'s convention and the ROI rule the same, and nothing after tier 1 has an HT branch. **One HT set is decoded: the last whose cleanup segment has bytes** — each set re-codes the block a plane further down. **Annex C's tables are extracted from the text by script, never typed**: `JpxHtCodeTablesTests` asserts each context a complete prefix code (Kraft sum exactly 1, none a prefix of another) and every entry consistent with §7.3.2 — which is also why the codeword loop needs no exit but a match. Tier 2's set T (§B.2) is the one place an HT segment ends: every pass after the 3·P0 placeholders but a SigProp pass, the Part 1 termination flags ignored. **Before a block's first cleanup, its contributions are read by Annex B's trial** (`TryReadUndecided`): a contribution may be placeholder passes alone (§B.3 NOTE 4) — one segment, Lblock + ⌊log2 passes⌋ bits, 0 — so when it could hold the first cleanup, the cleanup segment's field is read first and a zero there is finished as the placeholder field. Taking P0 from the first contribution's pass count would misread 27 of the ISO suite's 37 HTJ2K files (T.803 (02/2024), `JpxIsoHtConformanceTests`), and neither the reference HT implementation's nor the reference decoder's files have placeholders to show it. **The oracles are two other implementations and the ISO suite**: the reference HT implementation's conformance set (its encoder wrote the files, its decoder measured the per-component MSE and peak error against the source images) and the reference decoder through an independent tool, sample by sample — where the two disagree (one the reference HT implementation file's one-column irreversible tile), the reference decoder's numbers stand and the reason is at the row. The paths no file reaches (vertically causal SigProp, a second HT set, placeholder passes, the exponent and magnitude guards, ROI) are hand-built segments, written by a model of the §7.1 writers and checked against its readers before their bytes go into the test. **MIXED (code-block style bits 6 and 7 under a MIXED CAP) is decoded**: a block's coder (`JpxCodeBlock.Coder`) is settled by its first non-zero length under §A.4 — an HT block's begins with 0, with Lblock above 3 and more than one byte — and tier 1 dispatches per block; the ISO suite's DS0_HM files are the oracle (a commercial encoder's demo licence forbids its use in a project). **The encoder writes one cleanup pass per block, down to plane 0, without a size target** — a block's zero bit-planes are M_b − 1 and its pass count one — taking U_q = max(E_max, κ_q) and, of the Annex C entries whose known bits are true of the quad, the one whose codeword costs least net of the MagSgn bits it saves. Its segments are the fixture model's byte for byte (the three `JpxHtBlockDecoderTests` hard-codes), and the reference decoder decodes its codestreams sample for sample. **Rate control is by set, not truncation** (`JpxHtRateControl`): a set re-codes a block rather than refining it, so each block's candidates are a cleanup at every plane and, the same cleanup with its SigProp and with SigProp and MagRef passes (`JpxHtRefinementEncoder`, packed and terminated by T.814 Annex F's emitSPBit/emitMRBit/termSPandMRPackers — the standard's informative encoder, not a re-derivation); a target takes the set minimising error + λ·bytes, then spends what λ's step leaves on each block's best upgrade per extra byte (λ's step alone can leave a tenth of a target unspent); and layers decide when a block arrives (most error removed per byte first) — one set per block, since the reference decoder decodes a block of several sets wrongly; HTONLY with Rsiz bit 14 and CAP's Ccap15 (the bound P the smallest covering every band's M_b); in a container a JPH file (brand `jph `, T.814 Annex D), which enumerates no CMYK | `Core.Imaging.Jpx.JpxHtBlockDecoder`; `JpxHtBlockEncoder` (one cleanup pass), `JpxEncoder.CapSegment`; `JpxHtCodeTables` (Annex C, MEL_E); `JpxPacketDecoder.IsTerminated` (the set T), `TryReadUndecided` (before the first cleanup), `JpxCodeBlock.HtPlaceholderPasses`, `JpxCodeBlock.Coder`; `JpxCodestream.HtCapabilities` (Ccap15) |
| Lossless transcoding between JPEG 2000's two block coders, with T.803 (02/2024) §C.1.10–C.1.12's own test methods as the tests. **The values come from the decoders**: a block decoded with no ROI descaling holds (2μ + 1)·2^k for the plane k a sample is known to, so its trailing zeros give k and the bits above are what the other coder codes (`KnownBits`) — no coder's state is translated. **A block keeps its pass count**: a Part 1 block of N passes is one HT set of its last cleanup and (N − 1) mod 3 refinement passes after 3·⌊(N − 1)/3⌋ placeholder passes, so each layer brings the passes it did and the way back recovers the layers (§C.1.11); the way back codes Part 1 from the values' top, the placeholders becoming its passes again. **Placeholders are folded into the zero bit-planes where the block's first layer brings its last cleanup** (S_blk = P + P0 decodes the same) — they mark no boundary there, and the reference decoder refuses an HT block of more than three passes; `keepQualityLayers: false` folds every block. A selective transcode (`KeepPart1`) is MIXED (§C.1.12), and refuses selective bypass and termination on each pass, which §A.4 forbids its Part 1 blocks. **Headers**: Rsiz bit 14 with a CPF carrying the Part 1 profile (Pcpf = CPFnum + 1), and back; CAP's Ccap15 (HTONLY or MIXED, RGN, heterogeneity, bit 5, the magnitude bound) set and removed, the other parts kept; COD/COC style bytes; TLM, PLM, PLT, PPM, PPT dropped and each tile one tile-part, its parts' POCs merged (§A.6.6 allows one per header); a marker of 0xFF30–0xFF3F has no length. The PDF door supersedes each `JPXDecode`-only HTJ2K image in place, dictionary kept — it describes the same samples — and refuses encrypted and certified documents as `PdfImageReplacer` does | `Core.Imaging.Jpx.JpxTranscoder` (`TryTranscode`, `KnownBits`, `StyleByte`); `JpxTranscoding` (public; JP2 ↔ JPH, Annex D); `Base.Operations.Images.PdfJpxTranscoder`, `PdfOperations.TranscodeHtj2kImagesToPart1` |
| JPEG 2000 reconstruction. The inverse wavelet follows T.800 (2019) Annex F as printed — parity by absolute coordinate, F-7's STEP1 scaling even samples by K and STEP2 odd ones by 1/K — and the dequantiser's Table E.1 gains (LL 0, HL 1, LH 1, HH 2) are paired with exactly that: the reference decoder and another implementation take the 9-7 gain as 0 and scale their high-pass synthesis by 2/K instead, the same numbers distributed differently, so **neither side may be changed without the other**; the 9-7 conformance files (p0_04, p0_06, p1_02–p1_06 within Table C.1) and the 9-7 fixtures (within 1 of the reference decoder's decode) are what hold the pair. The order after the wavelet is fixed: the Part 2 transformation, whose offsets replace the DC level shift, or Annex G's RCT (on integers, exact) / ICT (on the reconstructed values); then rounding, the level shift for unsigned components, clipping, the non-linearity, and placement on the grid with sub-sampled components replicated. Tier 1's value convention is the contract every stage reads (twice the magnitude, the half at the last decoded plane). A band reaches the wavelet still quantised and is dequantised as it is interleaved — `JpxDequantizer.ReconstructStrided`, the one rule, which `Reconstruct` also goes through — and the decode's stages run under **one `WorkBudget`**: shared (atomic ticks) once `JpxParallel` puts tiles, or one tile's code-blocks and wavelet lines, across workers. A private lock around one stage's ticks beside unlocked ticks from another stage would be two answers to "who may tick", so the budget itself has the shared form; a second budget or a second lock is that defect. `JpxParallel` is the only worker loop, and a worker's exception is rethrown as itself. **Speed.** §F.3.7's extension is not built: every lifting step of either filter is symmetric, so after each step a position outside the signal holds what its mirror inside holds, and a step reads a neighbour one past an end from the position one inside it — the operands 1D_FILTR uses over the extended signal, in the same order, bit for bit (`JpxWaveletTests` holds the product to Annex F as printed, Tables F.2/F.3 and §F.3.8's ranges included, on every parity of origin and on signals of one to three samples, on one worker and four). HOR_SR lifts each row in place, VER_SR lifts the columns a row at a time over ranges of eight-column groups (a strided column copy costs more than its lifting), and a level under about 2^17 samples stays on the calling thread — `JpxParallel.For` starts a thread per worker, and a level starts one set. The forward transform (`JpxForwardWavelet`) is the same mirror and the same row-wise columns, held to F.4 as printed by `JpxEncoderWaveletTests`. The encoder shares `JpxParallel` too — the tiles, or one tile's components, through the wavelet, then every code-block of every tile in one pass — each item writing its own slot, so the bytes are the same at any degree (`JpxEncoderParallelTests`; `JpxParallel.MostWorkers` is the evidence a pass was shared, where the output cannot show it). Measured on a loaded machine, so as ratios: a 1024x768 RGB encode 2.5–8x faster, the wavelet stage of a decode about 2x, a single-tile decode on nine threads about 2x; the Part 1 MQ decoder is still most of a single-threaded decode, and two micro-changes to it (a Qe table by packed state; a neighbour pre-test in the encoder's passes) measure as noise | `Core.Imaging.Jpx.JpxWavelet`; `JpxForwardWavelet`; `JpxDequantizer` (`ReconstructStrided`); `JpxComponentTransforms`; `JpxDecoder` (the order); `JpxParallel`; `Core.Util.WorkBudget(budget, what, shared: true)` |
| JP2 colour for a PDF consumer: the boxes (T.800 I.5.3, T.801 M.11.7) read once, and ISO 32000-2 §7.4.9 encoded rule by rule with the clause at each site — /ColorSpace overrides the file and its component count must be supplied, the colour specification is chosen by PREC then APPROX then file order with unsupported ICC falling to the next, an Indexed /ColorSpace bypasses the palette, /SMaskInData 1 or 2 needs exactly one whole-image opacity channel, /Decode applies only with /ColorSpace or /ImageMask. The ISO JP2 files: file9 is the palette file, none carries alpha, only file2 has a cdef | `Core.Imaging.Jpx.Jp2ColourDescription` (`TryRead`, `TryPlanForPdf`); `Core.Imaging.IccDataColourSpaces` (one table) |
| A `JPXDecode` image's pixels for any consumer: the plan applied to the decoded samples — an `/ImageMask` is a stencil, an `/Indexed` image keeps its indices, `/Decode` applies only where §7.4.9 allows, `/SMaskInData` splits the opacity channel, the palette and `cdef` order are resolved, sYCC becomes RGB by the ICT's own constants (T.800 Annex I), CIELab and an embedded ICC profile are handed on with their parameters — and every sample narrowed to 8 bits by one rule. Without the plan, a colour space chosen by channel count alone renders `/DeviceCMYK` over three channels as RGB, a JPX `/ImageMask` paints grey, an `/Indexed` JPX paints grey indices, `/Decode` applies without a `/ColorSpace`, and a 12-bit sample narrowed by a shift disagrees by a level with the same samples Flate-encoded through `PdfColor.Channel`. The renderers, the exporter, the thumbnail reader, the colour converter, the optimiser and the plugin's `To8Bit` all read the one facade; `JpxImageDecoder` alone decides the order (the decoder, then a registered plugin only when it declines), so no site asks the plugin first | `Core.Imaging.Jpx.JpxImageDecoding` (public: `TryDecode`, `NarrowTo8Bit`) over `JpxPdfImage`; `Base.Images.JpxImageDecoder` (the order) |
| How far below its native size an image is decoded for a device target under `DownsampleImagesToTarget`: the 2x headroom kept above the target, the 256-px floor below which nothing is reduced, and — for a codec that reduces by halving, as JPEG 2000 does by resolution level — how many halvings (each ⌈· / 2⌉ a side, T.800 B-14) keep the wanted extent on both sides. Skia's codec size (`ImageDownsampling.CodecSize`: the renderer's encoded-image decode and, the optimizer's JPEG decode through `JpegSamples`), the raw sample decimation and `JpxImageDecoder`'s reduction all read it; a JPEG 2000 decoder never asked for a reduction would decode a JPX image drawn as a thumbnail in full and shrink it | `Base.Images.ImageDownsampling` (`Wanted`, `IsReducible`, `HalvingsFor`) |
| A shading on a separation plate is sampled into the plate's tints from its own components where `ShadingSpace.ToColor` would give sRGB — a spot or DeviceN shading's named tints (one `ColorantTints`, shared with the solid paint's snapshot), DeviceCMYK's four components, DeviceGray on black, anything else through sRGB or the output profile — and the shader is drawn plate-ready; §8.6.7 keeps the overprint mode at 0, overprint itself applies (the reference renderer's `separation output` within a level). An sRGB filter would collapse a rich black ramp onto black alone | `ShadingSpace.OnPlate`; `SeparationPlate.ColorantTints` |
| A paint's ink on a separation plate: `/All` marks every plate and `/None` none (§8.6.6.4), a named colorant takes its tint and every other plate is knocked out or, overprinting, left alone (§11.7.4.5), DeviceCMYK puts its components on the process plates with OPM 1's zero rule (§11.7.4.3), a solid DeviceRGB paint goes through the graphics state's `/BG` and `/UCR` when it sets them (§10.3.4, `BlackGeneration` — the plates convert RGB to CMYK; the reference renderer's `separation output -dUseFastColor` agrees on the inks and carries the functions past `Q`, which Table 52 restores), any other space reaches them by the inverse of `PdfColor.CmykToRgb`. An image's route is decided once per colour space, and without the overprint mode — §8.6.7 keeps it off images. The reference renderer's `separation output` checks both, and the one rule both renderers ask about an overprinting image on a composite (no OPM 1 on an image) | `Rendering.SeparationPlate` (`TryTint`, `RouteImage`); `OverprintCompositor.ImageLeavesColorantsAlone` |
| An inline image may name its colour space — or an `/Indexed` base — from the current resources (ISO 32000-2 §8.9.7). Expanding the device abbreviations alone would send `BI /CS /CS0` to the decode as an unknown family, and a spot image would paint nothing. The resolved space goes on a **copy** of the inline image: parsed operations are cached, and one content stream can be drawn under two pages' resources. The raster `BI` path decodes and draws through the image XObject's `DecodeSamplesWithMasks` and `DrawDecodedImage`, and both renderers report an overprinting inline image as they report an XObject | `Rendering.InlineImageColorSpace`; `ContentStreamInterpreter.DrawDecodedImage` |
| A `DCTDecode` image whose `/ColorSpace` is not the device space its codec assumes — `/ICCBased`, `/Separation`, `/DeviceN`, `/Lab`, `/Cal*`, `/Indexed` with the frame's arity — is coloured from its samples, down the raw path; the codec's picture would paint a spot JPEG at full tint white where the reference renderer paints the spot. Skia's codec gives a one- or three-component frame's samples when no destination colour space is asked for. A four-component frame it converts to RGB — naively, unless the JPEG embeds an ICC profile, which Skia then converts through; so the inks come out by splicing in a synthetic CMYK profile whose table copies three inks onto the PCS axes, decoded into a linear identity space, twice (C, M, Y; then K). Bit-exact against the reference JPEG library (an independent tool), chroma-subsampled or not; the file's own ICC segments are removed first; full scale is `0xFFFF`, which Skia reads as 1.0 (the ICC encoding's `0x8000` comes back as half). Skia undoes the Adobe inversion, so a four-component image's `/Decode` is dropped, as on the codec path. On a plate every JPEG takes the samples route. A device space, or a frame whose arity contradicts the space, keeps the codec. Decoded samples are never sniffed again: a row can open `FF D8 FF` or `89 'P' 'N' 'G'` | `Base.Images.JpegSamples` |
| Absolute colorimetric is the relative table times the media white over D50 (the reference colour engine's `ComputeAbsoluteIntent` at full adaptation); the media white is the `wtpt` tag, except D50 for a version 2 display profile (its `wtpt` is the illuminant) or when the tag is absent. That one rule is what the ICC v2/v4 difference reduces to, and the reference renderer is its oracle — RSWOP's paper, a D65 printer profile, sRGB unmoved, all within a level. A white at D50 to the tag's four decimals counts as D50 (the PDF constant's Z is 0.8251, profiles write 0.8249) or every neutral would get a one-count cast. Never black-point compensated. `/Sa gs` is the once-per-stream signal in the cancellation fixtures | `IccProfile.MediaWhiteScale`; `IccOutputTransform` |
| sRGB into an output profile: the `B2A` table for the paint's intent, not `B2A0` alone, and Adobe's black-point compensation as the reference colour engine implements it (destination black point: a perceptual round-trip estimate, then for relative colorimetric a neutral L* ramp round-tripped, a straight-midrange test, and a least-squares quadratic's root; compensation a per-channel XYZ scale fixing D50 white), with the reference colour engine's white fix-up — the plates' conversion and `PdfColorSpaceConverter`'s alike. The reference renderer through RSWOP checks it within one level with compensation on and off; the bare relative table differs from its default output by up to 17 levels. The source side (an ICC colour into the render) compensates too, by the reference colour engine's `cmsDetectBlackPoint` — whose round trip comes back through the **relative** table (another table is 18 levels off at K-only black). PDF 2.0's `/UseBlackPtComp /OFF` switches both sides off: it rides in the intent as the `WithoutBlackPointCompensation` modifier bit, because the intent already reaches every conversion (fills, images, shadings, palettes, plates, both renderers) and a second parameter would have to be threaded through all of them; anything that indexes or compares an intent reads it through `Table`, and an image's own `/Intent` replaces the table, never the switch. The reference renderer reads the key as a boolean, so its `/OFF` is on — its `-dBlackPtComp=0` values are the oracle. **A device link from RGB is a destination too** (ICC.1 §8.6): its `A2B0` maps the colour's components straight to the output the header's PCS field names, the link's own rendering — no intent, compensation or white fix-up is added, since the caller who supplies a link supplies the whole conversion; a v4 link's `lutAToBType` writes every output (`IccLutPipeline.TryEvaluateAToB`, which the three-value PCS evaluation calls). A link is no colour space (no PCS side), so `IccProfile.Parse` and the soft proof refuse it. A second independent CMM's `xicclu` evaluates the synthetic link the tests build (`TestHelpers.DeviceLinkProfile` — an affine CLUT between nonlinear curves, so every CMM's interpolation agrees inside a cell); that CMM reads no version 4, which is held to the same numbers | `Rendering.IccBlackPoint` (`Source`, `TryDetectDestination`); `IccOutputTransform`; `IccProfile.TryEvaluate`; `IccProfile.ParseB2A(…, intent)`; `IccRenderingIntents` (`Of`, `Table`, `Compensates`, `UseBlackPtComp`) |
| A label a caller typed, compared with a page's text. Hebrew abbreviations are set three ways — `סה"כ` with an ASCII quote from a Hebrew keyboard, with a curly quote from a word processor's autocorrect, with the gershayim (U+05F4) from a typographer — and the apostrophe of `מס'` likewise, so the look-alikes fold to one form, with NFKC (a presentation-form letter or a ligature becomes what it draws) and the Hebrew points removed — by Core's `HebrewSearchNormalizer.IsHebrewMark`, the one test of which characters are Hebrew marks. Text search and redaction fold apart and opt-in, through `HebrewSearchNormalizer`'s own fold: geresh, gershayim and maqaf only, no curly quotes or case. `PdfFieldExtractor` keys each line through the same fold, a text element at a time (an astral letter is folded whole), **in logical order taken from the bidi map** (`UnicodeBidi.ReorderWithMap`), not by joining the words: an English label inside a Hebrew line is stored word by word right to left, and a joined key would read "No Invoice". **A right-to-left line's map is taken under its own direction** (any other line's by its first strong character): a right-to-left line's visual string opens with its value, and resolved from its first strong character — `INV1001` — a number being weak and deciding nothing, the geresh of `חשבונית מס'` would move to the far end of the label, before `חשבונית`, and every label ending in it would stop matching. The fold remembers which word and which glyph each key character came from, which is how a label occurrence names its words, and how the rest of a word a label ends inside (`Invoice #1001`, `Total:118.00`) becomes the value. Whitespace and colons are dropped after the fold, so a thin space or a full-width colon goes as the plain one does; a character `String.Normalize` refuses (U+FFFE, a lone surrogate) is kept as written, so one such glyph cannot abort a document's extraction | `Forms.InvoiceValueParser.NormalizeForMatching` (`LabelKey`, `AppendLabelKey`); `PdfFieldExtractor.Prepare` |
| The invisible layout marks a number laid out in right-to-left text carries — LRM, RLM, ALM, the BOM, the embeddings, overrides and isolates. The value parser's digit fold, its punctuation fold and `IsraeliIdentifiers.TryNormalize` all ask it | `Core.Text.UnicodeBidi.IsInvisibleLayoutMark` |
| Where text may hold an Israeli identifier: eight or nine ASCII digits in groups joined by single hyphens — and single spaces, where the caller allows — adjoining no other digit, and no part of a number written with a decimal or thousands separator. A hyphenated run is one number (an account number offers no nine-digit windows); spaced numbers are joined from each in turn as far as nine digits allow. The redactor and the invoice extractor both ask it; a greedy space join would swallow the phone number after an ID (`514286558 03-1234567` → 11 digits) and throw the whole away. The redactor keeps hyphen joins only — a redaction is destructive, and two numbers a space apart are two numbers there | `Core.Text.IsraeliIdentifiers.Candidates` |
| An amount drawn with its currency sign inside it — Word's `3 ₪,539.43` for `₪ 3,539.43`: digits, a word space, the sign, a separator and digits; the glyphs read in visual order, the sign put before the amount where nothing else is, the rest closed up on the integer part — taken apart once, at the invoice extractor's entry, for the fields and the item table alike | `Forms.CurrencySignsInsideAmounts.MovedOut` |
| What a word contributes to a visual line: its glyphs left to right, or — a word a caller built without glyphs — one glyph standing for its text at its box. Keying such a word by its text and reading its value from glyphs it does not have would find the label and return its value empty at confidence 1 | `TextDirection.VisualGlyphs` (`HasOwnGlyphs`) |
| A character's bidi class (UAX #9). A hand-written list lets what it does not name fall through to strong L — `₪` among them, so a Hebrew item row whose amount and shekel sign Chromium draws at the row's visual left would read amount first. The classes are generated from DerivedBidiClass.txt, its `@missing` defaults first, at the Unicode version `UnicodeScripts` uses, and a string is classified by code point, never each half of a surrogate pair. The resolution follows the algorithm's own structure for X1–X10 and is held to Unicode's own conformance file, every case of `BidiCharacterTest.txt` — a resolution that passes its own tests can still split a sequence at isolates alone, keep X9's removals, skip the overflow counts or know only some bracket pairs | `Core.Text.UnicodeBidi` (`UnicodeBidi.Classes.cs` and `UnicodeBidi.Brackets.cs`, written by `tools/unicode-bidi/Generate-BidiClasses.ps1` and `Generate-BidiBrackets.ps1`; `BidiCharacterConformanceTests`) |
| A glyph of a `/ReversedChars` run as drawn (ISO 32000-2 §14.8.2.3.3). Chromium draws every right-to-left run inside the tag, left to right, each glyph decoding to its logical character — so the bracket at the run's visual left decodes to `)` though it is drawn `(` — and the bidi algorithm's L4 would mirror it back a second time: `(לפני מע"מ)` would read `)לפני מע"מ(`. The glyph's mirrored characters take the shape drawn before any reordering: in Core's plain extractor at `EMC`, and in the one builder of a visual line's text for the structured line text and the field extractor's keys | `TextDirection.VisualText` over `UnicodeBidi.AsDrawnInReversedRun`; `PdfTextChar.InReversedRun`; `MarkedContentInfo.IsReversedChars` |
| What a text's bidi controls leave open at a point — embeddings, overrides, isolates (UAX #9 X1–X8) — and so whether a piece lies inside an override and in which direction. A level does not say it: a Latin word inside an RLO and inside an RLE share a level, and only the override reverses it. Shaping every token in its paragraph's direction would keep Hebrew under an LTR override in its right-to-left order; the Builder and the legacy DOCX lines read the one stack | [`Core/Text/BidiControlStack.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/BidiControlStack.cs) |
| A legacy DOCX line in visual order, for a body line and a table cell alike. Shaping (and so reordering) each run on its own and drawing the runs left to right in logical order would read a Hebrew paragraph of several runs backwards and put a list marker on the item's left. The paragraph's text, before wrapping, resolved once in its direction (`LineBidi.Paragraph`) and each wrapped line located in it after the spaces its break took, L1 applied per line, each segment cut where its level changes, odd-level pieces drawn right to left, L2 over the pieces; a `w:dir`/`w:bdo` is its controls. Resolved a line at a time, a bracket pair a break splits would pair on neither line and an open control would have to be carried by hand; a line the paragraph does not hold is resolved alone and reported, never misplaced. The three wraps share one tokenizer, which keeps a run's spaces but those a break takes (so "Hello" + " World" does not draw "HelloWorld"); a tab stop's alignment is one function in both directions, matched to Word 16's output | DocxToPdf [`Rendering/LineBidi.cs`](src/features/Tetitulz.Pdf.DocxToPdf/Rendering/LineBidi.cs); `UnicodeText.SpacedWords`; `ParagraphRenderer.AlignedOffset` |
| Which glyphs share a structured line. Chaining each glyph to the one before it, top to bottom, within half the tallest glyph the line holds so far, with no horizontal test, would let a 19.5 pt title lend its band to the 8 pt lines beside and below it, and make a Chromium invoice header's six blocks one interleaved line. A row is the glyphs within a tenth of the smaller height of its first; a row joins the nearest line holding a row within the smaller glyph's band (`YTolerance`, never under 2 pt) unless two overlapping runs would sit one above the other further apart than theirs — without that guard staggered columns chain — and a drawn space is a line's within that band of one of its rows **and among that row's glyphs**, or the next column's spaces break its words. Bounded per row (`MaxStepsPerRow`; `LastLineGroupingRowSteps` for the test, since a bound on the total cannot see one search left unbounded). Rotated glyphs chain to the glyph before them; `OcrTextLayout` (within half the taller word's height) and `PdfTableExtractor.ClusterByY` (within `RowTolerance` of a row's first word) form rows of their own | `PdfStructuredTextExtractor` (`GroupByBaseline`, `BlankCentresOnLine`, `GroupByBand`) |
| A column gap, read once: the text strategy's separator candidates, whether a prose row crosses the grid, and where a phrase of a row's words ends (a word space, under 0.4 em, is a phrase's; a gap of `ColumnGap` never is). Each word placed on its own, a word crossing a separator would make a cell over two columns beside a cell in one, and the line-item reader would take one fragment of each description. A separator a header's words cross — a header set right over numbers set left — moves into the space every gap that voted for it shares, beside the header, where the header's row keeps a word on each side | `PdfTableExtractor.SeparatesColumns`; `PlaceInFreeSpace` |
| A line's segments, and a title that stands. A title phrase deciding the document type wherever it stands on its line would make a tax-deduction certificate's sentence `רשאי להוציא חשבונית מס בתיק מספר …` a tax invoice numbered with a tax file number. A line's segments part at the column-wide gap that ends a value (`TooFar`, `ValueGapEm` word heights); a title stands when, in its first word's segment, nothing with letters comes before it but a copy's marks, and in its last word's at most two words with letters follow (number words and copy marks aside) and no word with letters and a full stop ends it — one predicate for the type, the receipt word and the number after a title (through `Prepared.Best`'s `where`). Its own words may stand apart, as a spaced-out title's do | `PdfFieldExtractor.Segments`; `IsraeliInvoiceExtractor.StandsAsTitle` |
| An amount that is a field's value. `TryParseAmount` reads a number out of any string by contract — the line-item reader relies on it for cells — so prose beside or under a total's label would give totals and VATs: `… בתוך 30 ימים` a total of 30, a section number 17, `PCN874` a VAT of 874, `4,250 ש"ח ומעלה`. A value is an amount only where the number the parser chooses (`TryChooseAmount`, shared, so the two never pick different numbers) touches no letter and has no letter after it but a currency's. The currencies are one table of sign, Latin and Hebrew forms (`שקלים`, `דולר`, `אירו`, `GBP`, `CHF` …), read by the value rule's allowance and the currency detection alike; a code apart from the number names its currency, whatever it is | `Forms.InvoiceValueParser.TryParseAmountValue` (over `TryChooseAmount`; the currency table) |
| Which lines and words a caller-built layout holds. `PdfPageTextLayout`, `PdfTextLine` and `PdfTextWord` are public and constructible, so a layout can hold a null page, line or word, or a line with null `Words`; each walk of one would otherwise answer the null question its own way. One rule, which every walk goes through: the spatial queries and the table extractor at each walk (the table extractor keeping a null page's index, so the pages after it keep theirs), the field and invoice extractors once, through a cleaned copy at their entry | `Extraction.TextLayouts` (`LinesOf`, `WordsOf`; `WithoutNulls`) |
| Israel's dated tax figures: the VAT rate (16.5%, 15.5%, 16.5%, 16%, 17%, 18%, 17%, 18%) and the amount above which a tax invoice needs the Tax Authority's allocation number — before VAT, 25,000, 20,000 from 2025, 10,000 from 2026, 5,000 (the accelerated schedule; the 2024 plan stretched to 2028); for an invoice mixing taxable and exempt or zero-rated items, its VAT: 4,250, 3,600, 1,800 and 900 on the same dates. The rule covers the tax invoice (305), the consolidated one (310), the invoice-receipt (320) and the agent's (345) — not a credit invoice, and not a proforma (332), whose advance approval is no allocation number (the Tax Authority's API specification, 2024). An invoice is judged on its own date: checked against today's figures, every invoice before the last change would fail. Within a month after a rate change the old rate is still plausible, since the rate follows the transaction's tax-liability date, which the invoice may follow by fourteen days. The rates the VAT has had are also the phrases the taxed-at-rate subtotal labels are built from (`סה"כ סכומים ב-17% מע"מ`), so a rate the table never held matches no label | `Forms.IsraeliTaxRules` (`VatRateOn`, `AllocationThresholdOn`, `AllocationVatThresholdOn`, `NeedsAllocationNumber`, `PlausibleVatRates`, `StandardVatRates`) |
| Invoices as tables for analysis: one typed row per invoice, per line item and per finding, joined by source and number. Cells stay typed so a spreadsheet sums and sorts without conversion. The CSV carries a UTF-8 byte-order mark — without one Excel reads the file in the system code page and the Hebrew turns to question marks — and text starting with `=`, `+`, `-` or `@` is written behind an apostrophe, because the text came from a PDF and its author chose it. The workbook writes text as inline strings, never formulas, and a date Excel's serials cannot show (before 1900-03-01) as text | `Forms.InvoiceDataTable`, `IsraeliInvoiceTables`; `Plugins.Docx.InvoiceWorkbook` |
| A spreadsheet cell's text (ECMA-376 `ST_Xstring`): XML-legal, and the underscore opening a `_xHHHH_` sequence written `_x005F_`, since a spreadsheet decodes the sequence as the character it names — a catalogue number extracted as `AB_x0031_` would show as `AB1`. The invoice workbook and the PDF-to-XLSX converter alike; not a word-processing or slide run, which is no `ST_Xstring` | `Plugins.Docx.XmlTextSanitizer.ForSpreadsheet` |
| What a font's name says about weight and slant — the standard-14 mapping (which picks the widths), the SVG face, the raster typeface lookup and the accessibility remediator alike. Two readings would measure `Arial,Black` as Helvetica-Bold and draw, and embed, it as regular Arial | `Core.Fonts.FontNameStyle`; `FontCache.StyleNamedBy` |
| A `/DSS` or `/VRI` revocation list read, and each stream's parse. Decoding every entry under the 512 MB image ceiling, a failure swallowed, with every certificate of every signature's chain reading the list again, would make one CRL listed five thousand times five thousand parses and signature checks a certificate. A list is read once a stream, under the 20 MB the embedder accepts from a responder (the verifier reads what the embedder may write, and nothing larger), at most 1000 streams, what is skipped said; a CRL or OCSP stream is parsed once a decoded buffer — a weak table keyed by the buffer the stream hands back each time — and a CRL's signature checked once an issuer; a check's warnings are folded and counted; the inventory reads the same memo | `Signing.Cms.RevocationChecker` (`ReadStreams`, `CachedCrl`, `CachedOcsp`); `DssBuilder.MaxResponseBytes` |
| The LTV revocation walk, sync and async. Two copies of the walk drift — warnings naming less, a POST outside the revocation budget. A plain sync-over-async wrapper would move the `[ThreadStatic]` diagnostics (`LastBlockedRedirect`, `LastCrl*`) off the caller's thread, so the one implementation takes a `sync` switch that blocks each step in place | `DssBuilder.WalkRevocationPath`, `FetchOcspCore`, `FetchCrlCore`, `PostOcspRequestCore` |
| CSS text cut at top-level separators. Declarations, tokens, `box-shadow`, a media query list and `src`/`srcset` all cut through it; separate copies drift — one ignoring quotes, one whose depth goes below zero at a stray `)`, after which no comma splits the list. And the `)` that closes a `(` | `HtmlToBuilderMapper.SplitTopLevel`, `ClosingParen` |
| Big-endian integers, for the sfnt, CFF, WOFF, ICC, JPEG 2000 and JBIG2 readers and writers; the local names stay as forwarders so each site still reads as its format. A PNG image's scanlines and the `/Predictor` unfilter share one Sub/Up/Average/Paeth row | `Core.IO.BigEndian`; `PredictorHelper.UndoPngRow` |
| An OOXML attribute by local name, for every site in the DOCX renderer | DocxToPdf `Rendering.OoxmlAttribute.Value` |

Two documented exceptions: `Plugins.PostScript` implements the subtractive colour model itself
(`PdfColor` is internal to Base; the copy is in `double` and never quantised), and `DocxToPdf`'s
`EncodeWinAnsiLiteral` escapes *and* transcodes to CP1252 (routing it through `PdfStringEscape` would
mojibake every smart quote).

> **Consolidating part of a cluster is more dangerous than consolidating none of it.** The unit of work
> is every site that answers the same question: every quantisation in both renderers, every nested ICC
> position, every `/Indexed` palette builder.

### Constants — no bare literals for named PDF concepts

`Tetitulz.Pdf.Core.Constants` is the single home for values the spec names. Check it before writing a
literal; add to it rather than start a local copy.

| Use | Type |
|---|---|
| Dictionary keys and name values | `Core.Objects.PdfName` (about 300 `static readonly` fields) |
| `switch` case labels, string compares | `PdfOperators`, `PdfColorSpaceNames`, `PdfAnnotationSubtypes`, `PdfBlendModes`, `StandardFonts` |
| Unit conversion | `PdfUnits` |
| Page geometry | `PdfPageSizes` |
| DoS and recursion caps | `PdfLimits` |
| Annotation `/F` bits | `PdfAnnotationFlags` (with `IsHiddenOnScreen`, `IsHiddenInPrint`) |
| Text render mode, line cap and join | `TextRenderMode`, `PdfLineCap`, `PdfLineJoin` |
| Page-label numbering styles (Table 159) | `PdfPageLabelStyles` (with `IsDefined`) |
| Digest and EKU OIDs | `PdfDigestOids`, `PdfEkuOids` |

1. **`PdfName` cannot be a `case` label**, so dispatch uses the `const string` classes and lookups the
   `PdfName` cache. `NoRedundantPdfNameAllocationTests` fails the build on `new PdfName("X")` where
   `PdfName.X` exists. Never cite an example of an uncached name: the guard reads the live field list and
   a documented example goes stale.
2. **Centralising a value is not unifying it.** Where two values differ on purpose, give each its own
   name (`BezierFlattenSteps` 16 for the raster path, `SvgGradientStopCount` 32 for the vector one).
3. **A cap whose exponent the file chooses needs a constant** (`MaxSampledFunctionInterpolationInputs`:
   Type 0 interpolation visits 2^m corners).
4. **`/All` deliberately does not go through the tint transform** (§8.6.6.5).

`PdfLimits` supplies values only; `Core.Util.WorkBudget` bounds aggregate work across nested loops.

> **Every limit in `PdfLimits` and `PsLimits` is settable, and the pair of spellings is load-bearing.**
> `X` is a property a consumer may assign; `DefaultX` a `const`. Read `X` at runtime and use `DefaultX`
> only where the language demands a compile-time constant (a default parameter takes a `-1` sentinel);
> an alias of the form `private const M = PdfLimits.N;` would pin the built-in number so an override
> never arrives. They are process-global and deliberately not scoped (they are read in the
> hottest paths); where a ceiling must differ per call the entry point takes it as a parameter, **and a
> ceiling taken as a parameter needs its forwarding mutated too**. A test that writes a limit joins
> `PdfLimitsCollection` and restores with `ResetToDefaults()` in a `finally`. Values are not validated
> on assignment: the guards fail closed, and clamping would be the library choosing the threat model.

**Counts a file chooses multiply: bound the product**. Script records, language systems, feature
indices and lookup indices are each the font's count, and with nothing on their product a 320 KB font
would ask for 4 × 10¹³ reads from every producer that embeds it. `PdfLimits.MaxOpenTypeScriptSteps` is
charged once per script, feature index and lookup index, in the shape of `MaxStructTreeEdges` and
`MaxLinearizationSharedReferences`: a cap on one factor bounds nothing. Likewise:
- A format-12 `cmap`'s groups share one budget of code-point visits (four times the map's entries, as
  format 4): overlapping groups never raise the map's size, so a hundred thousand copies of one wide
  group would be 1.3 × 10¹⁰ writes (`TrueTypeFontProgram.ParseCmap12`).
- A DOCX chart's value, x and bubble caches draw on one chart-wide point budget (four times
  `MaxChartDataPoints`), and a list level's `w:lvlText` is cut at `MaxListMarkerChars` and the cut said:
  it is laid out for every numbered paragraph.
- N-up refuses more than `PdfNup.MaxCellsPerSheet` cells, the product taken in `long` — in `int`,
  65 536 × 65 536 is 0 and the sheet count divides by it.
- `MaxAnnotationsPerDocument`: pages may name one `/Annots` array, so a whole-document read of
  annotations reads pages × entries (800 pages over one array of 800 links: 1.8 s
  uncapped). The link and annotation extractors, the annotation editor and the
  accessibility deep checker count every page's entries together, and say from which page they
  stopped. A ceiling, not attributing a shared annotation to its first page (§12.5.2), which would
  change what a viewer shows.
- `MaxSignatureCoverageMultiple`: signatures each digesting one long revision cost S × N bytes.
  Verification stops at 64 times the document's length, and `MaxSignaturesPerDocument` (500, about
  16 ms each) bounds the count.
- `OpenTypeMarkPositioning.MaxExaminationsPerGlyph` (1024): `MaxSteps` counts subtable trials, and a
  position a lookup's flags skip makes none, so a font whose lookups all skip every glyph of a run would
  cost its lookups (up to the 16-bit count) × the run's length — reachable from an HTML `@font-face` data
  font. Per glyph of the run, so the work is linear in the text.

Either way, a signature past the budget is reported, not verified.

**A decode sized by what a budget has left is asked before it runs, and refused at nothing.** A check
behind the decode has already paid: a form of 456 bytes decoded to the 512 MB ceiling to be told it
was past a 32 MB budget. Two things follow when the decode moves under the budget. A budget spent to
exactly zero must refuse, and `FilterPipeline` reads a ceiling below one as its own default, so
`PdfStream.TryGetDecodedData` refuses there rather than running the largest decode there is, once a
stream, for every caller past its budget. And a refused decode must spend the budget, since neither
the bytes it made nor a typed reason come back: half of what is left, so refusals together cost no
more than twice the budget and a few corrupt streams in an ordinary file leave it room
(`FormXObjectInventory.Measure`, `ScanFormXObject`, `ScanNestedStream`).

**Every recursion over untrusted nesting carries a depth** — `PdfParser.ParseArray`'s `_parseDepth` and
`ContentStreamParser.ParseInlineValue`'s alike: two hundred thousand `[` (a few hundred bytes deflated)
would overflow the stack of whichever process renders the page, and a `StackOverflowException` is
caught by nothing. Past `MaxObjectGraphDepth` a `[` or `<<`
reads as `PdfNull` without recursing.

**A resource that can name its own kind is charged to the aggregate budget, whatever its kind.** A
Type 3 glyph procedure that shows four glyphs of its own font, with a depth cap and nothing else, stops
each branch at 24 levels and starts 4²⁴ branches, so both renderers charge a glyph procedure to the
page's invocation budget. A form of two thousand `Do` operators drawing itself stays inside an
invocation count: 100 000 invocations of a 16 KB form is 1.6 GB parsed whole. **A budget on a count is
a budget on one factor**; every invocation is charged its decoded length too
(`MaxFormContentBytesPerPage`), and in the raster renderer every offscreen bitmap its area
(`MaxOffscreenPixelsPerPage`). **A byte is not an operation's price either**: a tiling cell of 4 556
two-byte `q` operators, painting itself, runs 39 million state clones inside every one of those
ceilings — 21 s raster, 57 s as SVG, from 8 KB — and the content ceiling alone lets a page run eighteen
seconds of fills. Measured inside a form, a fill or a stroke is 1.2–1.8 µs an operation in either
renderer; so each run is also charged the operations it executes after its parse
(`MaxFormOperationsPerPage`, two million, `Execute` in each renderer at a depth above the page's).

**And an operation's price is not fixed: a paint costs the area it covers.** The 1.2–1.8 µs is a
fill of a few pixels. The page box chooses the bitmap up to `MaxImagePixelArea` whatever resolution
the caller asks for, so a page a million points square renders to a hundred megapixels even as a
thumbnail, and a page-sized fill on it costs 42 ms: two thousand of them are 85 s from a 50 KB page
with no form in it, and the coverage-guided fuzzer's 6 KB form drawing itself with one fill rendered
for 198 s inside every form ceiling. Measured there, a pixel of a stroke costs 2.4 fill pixels, of a
shading 15, of an image 13 with nearest sampling and 88 with Mitchell (`/Interpolate`). So the raster
renderer charges every paint — fill, stroke, glyph, text run, image, shading, pattern — the device
area it covers inside the clip times its kind's weight (`RenderBudget`'s, each rounded up), against
`MaxPaintWorkPerPage` (2³⁵ fill pixels, about fourteen seconds), at the site that already reports the
paint's bound. A stroke pays the smaller of its bounds and its length times the pen's width, so a
hairline across the page costs its ink: two thousand page-long diagonals stay inside the ceiling. The
ceiling is absolute rather than a multiple of the page's area, so a thumbnail of a dense page is never
refused and a 1200 dpi render still admits three page-sized interpolated images. The SVG writer paints
no pixels and never spends it. **A refusal a branch can reach is reported once a page**: the same file
reached the depth cap a thousand times a form on every branch the form ceilings admit, two million
reports, so `RenderBudget.FirstDepthRefusal` gates every depth-cap report in both renderers. A tiling
cell a thousand points square at a 1e-300 step that fills with itself reported its overlap
approximation 200 001 times: the report precedes the build's charge, so the attempts the invocation
ceiling refuses reach it as well as those it admits; `FirstTilingApproximation` gates it in both. The
same 1.8 KB file still renders in about six seconds: a hundred thousand offscreen cell renders at some
55 µs each, the invocation ceiling's own price. **And behind every site, a per-kind allowance**: a report
site reached once an operator or a colour sample reports as often as the file repeats it, and the site
need not know it is in a loop — `PdfColor`'s Pattern branch is static and is reached once a sample by a
Separation or DeviceN space whose alternate is `/Pattern`. Such a shading clipped to one unit and drawn
a million times through three levels of forms is 2 KB and was 22 million reports; counting each kind
across the structure fuzzer's render corpus found pages of 111 000 pattern and 25 000 image reports from
other sites. So `RenderDiagnostics.Report` itself counts: both renderers' operator loops open
`RenderDiagnostics.ForPage(budget)` (a thread-static scope, opened and closed around the loop on the
thread that runs it, as the function and charstring budgets are), and inside it each kind is delivered
at most `PdfLimits.MaxRenderReportsPerKindPerPage` (100) times a page, the last saying the rest are
suppressed. A once-a-page gate at a site is still right where the report is one refusal; the allowance
is what bounds the sites nobody has measured. Reports outside a page's operators are not counted.

**A per-stream ceiling a few bytes can reach whole is a per-page cost the page must also cap.** A JBIG2
stream of a few dozen bytes may spend its whole composition budget (three seconds); a page of a hundred
such images would cost a hundred budgets. A `WorkBudget` may have a parent it also spends (clamped to what
the child could pay), `FilterPipeline.Decode` carries the page's budget to every stage as
`StreamFilter.Work`, and both renderers hand `RenderBudget.ImageWork` (`MaxImageDecodeWorkPerPage`) to
their one image-decode path. The codecs' own units are the currency, because a pixel's price differs
several-fold between codecs. **The parser's own streams have no page, so they spend the document's
budget**: an object stream or cross-reference stream is decoded under `PdfParser.SyntaxStreamWork`
(`MaxSyntaxStreamDecodeWork`, an eighth of one JBIG2 stream). An object stream filtered through
JBIG2 and reached through four damaged headers in each of two recovery scans is eight distinct
`PdfStream`s, which a memo of failures by object number would not see as one stream: eight decodes,
27 s at open uncapped, 0.7 s under the document's budget. **A syntax stream whose `/Filter` names an
image codec is refused by name before any decode** (`Parsing.SyntaxStreamFilters`): a codec's output
is samples — a bitmap, a JPEG or JPEG 2000 codestream passed through — and no reading of samples is
object syntax or cross-reference entries, so decoding is wasted whatever it answers; and the budget
is per document, so the 0.7 s came back on every open — the scorecard battery's forty opens of the
fuzzer's 1.5 KB file took 30 s. Refused unread, the stream costs nothing and the section is skipped
with the codec's name; the budget bounds page content filtered through a codec, which is still decoded.

**A function a page evaluates once a pixel spends the page's budget.** One Type 4 evaluation has its own
ceiling (two million operations, its `repeat` and `for` loops included, which ISO 32000's Table 42 does
not define and the evaluator reads leniently), and that bounded nothing a page does with it: a tint
transform runs once a pixel, a shading once a sample, a halftone's spot function once a cell pixel —
a fifty-byte looping tint transform over a thousand-pixel-square DeviceN image is 2 × 10¹² operations. `PdfFunctionEvaluator.SpendFrom` opens a scope in which every evaluation on the
thread also spends one budget as its parent (`MaxFunctionWorkPerPage`, 2³⁰): both renderers' operator
loops open it with `RenderBudget.FunctionWork`, a halftone screen and a colour conversion each with one of
their own. Past it each evaluation is refused at its first operation, so the rest of the page costs
nothing, and the image, screen or space that could not be drawn is reported. A new caller that evaluates
in bulk outside a render opens a scope; one inside a render inherits the page's.

**A ceiling's unit has a price, and the price is measured.** Prices no ceiling counts: a JBIG2 MMR
region sized by its header before any check, beside an arithmetic path that charges the budget (**a
guard on one path is owed on its twin**); a 98-byte symbol dictionary spending the whole pixel budget
at 61 ns a pixel (the context is formed from sliding windows, 16 ns, and a region is charged its
setup); a tiling cell filling with itself twice, a sixteen-megapixel bitmap at each of a hundred
thousand invocations (the third ceiling); a cell drawn at the page's scale into a tile sized at its own
(**a fuzzer's control is a test of the renderer**); and a text-region instance naming a missing symbol,
which draws nothing and must still be charged — billions from 121 bytes.

---

## Coding conventions

- Language version `latest`; namespaces match folders; `internal` by default; no `using static`; no
  global usings beyond the csproj; `float` for user-space coordinates, `double` where the spec mandates;
  pattern matching freely.

**Comments and docs describe the current state.** A comment says what the code does and why, in the
present tense, so a reader learns the rule without the story of how it was found. A reason that needs a
contrast names the alternative and what it would do ("a cast would let `int.MinValue` through"), never
what an older version did. History — the defect that motivated a rule, the finding id, the date — lives
in git, where it can be dated and searched without misleading a
reader of the code about what the code does now.

**Every public exception derives from `Core.PdfException`.** `catch (PdfException)` is the contract a
host writes around a parse, and a type outside the hierarchy escapes it — a filter bomb or a work-budget refusal would
reach a host's top level. The API baselines cannot see this (they
record members, not base types; a type can change base class without moving a baseline), so the guard
is reflection: `ExceptionHierarchyTests`, whose `OutsideTheHierarchy` list is empty and anchors one type
per package, since a sweep that loads nothing reports a clean sheet.

**A refusal of the file's own content is a `PdfException`, not an `InvalidOperationException`**. The fuzzers count a `PdfException` or an argument error as a typed refusal and
anything else as a finding, and a host's `catch (PdfException)` is written on the same assumption.
Sites outside the fuzzers' reach are held to it too: in Core, `PdfArray`'s number getters; in the
plugins, the optimizer and linearizer over a page tree that yields no page, the optimizer's object
ceiling, the hint tables' 32-bit fields, and the PDF/A converters' throwing overloads; in Base, a
catalog that is missing or not a reference, an unreadable `/AcroForm`, `/Fields`, `/Names`,
`/OutputIntents` or optional-content entry, a name tree read in part, the structure-tree reader's
ceilings, the font subsetter's malformed tables; in Signing, the counter-signer's checks of the
signature it counter-signs, the `/Contents` delimiter search, the timestamper's token reserve. A domain
subclass is used where one exists (`PdfSigningException` in Signing). `InvalidOperationException`
stays for a caller's misuse, such as a builder used out of order, and for the option-gated
signature-invalidation refusals of the whole-file rewriters, which are one consistent family.

**A public enum member is appended, never inserted.** A member inserted after `RC4_128` would hand a
caller built earlier, asking for `AES128` (2), 40-bit RC4 — a silent downgrade with no error. An API
snapshot accepts the change because it shows what changed, not whether it is safe;
`EncryptionAlgorithm_HasExpectedValues` pins every member.

**A public `float` or `double` argument is refused for NaN and ±∞, not by `<= 0` alone**. NaN passes every comparison, `Math.Max(NaN, 1)` is NaN, and `PdfRealFormat` writes a
non-finite value as `0`, so unchecked, a NaN tile extent paints a blank image, an ink-coverage dpi of
0, −72 or NaN answers with an empty map, and a redaction area with a NaN side draws its cover at
`[0 0 0 0]` and returns with the data in place. One check, `Core.Util.NumericArguments`, at every entry
(a private copy is a copy nothing tests); a public constructor sizing a buffer from two `int`s multiplies in `long`; a pad a caller sets
has a cap. **A model every exporter reads is validated once, before any of them reads it, by one walk
that names the path and the property** (`Builder.Model.ModelMeasurements.Validate`): a size cannot be
negative, a margin or offset can and is only required finite.

**A file-supplied field is judged before any arithmetic uses it.** A BMP depth of 65 535 read into the
row stride at the pixel ceiling wraps `w * bpp`, a truncation check compares a wrapped stride, and the
decoder would allocate 120 MB before its per-pixel switch refuses the depth. The depth is refused first (`BmpDepthGuardTests`, whose assertion is the reason's name).
Likewise a TIFF field of a type TIFF 6.0 does not define has no size, so any count fits any file — an 86-byte
file asks for an array of 268 million entries — and it is dropped before its count sizes anything; a page's
strips, which may share bytes, read at most twice the file and the page's raster between them
(`TiffSharedDataTests`). A BMP channel mask of all 32 bits is scaled in 64: in 32, `1 << 32` is `1 << 0`, the
maximum is 0 and the division throws (`BmpBitfieldChannelTests`).

> **A file-supplied number is not a `long` until something says it is.** `PdfNumber.DoubleValue` can
> hold a value outside `long`'s range, and `(long)` then yields `long.MinValue`, so a check against
> negative offsets manufactures one. Test `PdfNumber.IsInteger`, then read `LongValue`. Only a literal
> carrying a period reaches it: the parser builds an integer literal through the token's `LongValue`,
> which clamps first.

> **An unreadable object is not an absent one.** `GetObject` and `ResolveObject` answer an object the
> file does not define and an object they could not parse with the same null object (§7.3.9 makes the
> first the file's own null), and only `PdfParser.LastObjectParseError` tells them apart — read
> *immediately*, since both calls clear it at their start and the value belongs to the most recent one.
> A reader that takes the second for the first answers wrongly: a name or number tree with an
> unparseable node read *whole* gives "no such key" where the answer is "the tree could not be read"
> (`PdfTreeTruncation.UnreadableNode`, and UA14 says so); DocMDP reading an unreadable rewrite as a
> *deletion* and an unreadable certified value as *never defined* would pass, at `/P 3`, an annotation
> rewritten into garbage as a permitted deletion (it is a violation, in either revision); a rewriter or
> splitter leaving the object out of its output leaves references dangling and must say so
> (`DocumentEditDiagnostics`, `PdfSplitter.LastObjectWarning`). A reader handed the null object refuses
> or reports; one that grants nothing by reading absence (the image replacer's "not an image", the OCG
> manager's "not a dictionary") may leave it. **A test's unreadable object is a stray `]`.** The parser
> is tolerant: an unterminated dictionary reads on into the next object and comes back as a dictionary
> with that object's entries, so a "garbage" object of that shape is one the parser reads.

> **The doc comments are usually right and the declarations wrong.** Read them first.

**A documentation comment holds one `<summary>`**. A member written in
directly under another member's comment carries both summaries, and the member the first one described
is left with none. The compiler is silent, because duplicate tags are well-formed XML, and a private
member is asked for no comment at all — `ListBlock`'s description ends up on `TocBlock`, or an old
summary above its own rewrite.

`OneSummaryPerDocCommentTests` reads every comment in `src/` (a floor on the count) and names each one
that holds two. A comment that puts a member's parameters or remarks before its summary is one comment
and is not counted.

**A partial type is documented on one part.** The compiler merges every part's documentation comment
into the type's one XML doc entry, so a type documented on several parts publishes each summary, and
copies of the type's summary on the parts go stale one at a time with nothing pointing at the
contradiction. The type's summary stands on its main part; what a part says about its own file is an
ordinary `//` comment there. The same test names every partial type (namespace-qualified) documented
on more than one part.

**A class of defect found by reading is then probed by running**. A read of one component for
the number-reaches-a-file class can come back clean while the class is present one layer down: a
Builder document with every measurement NaN would export if Core's `PdfDocumentBuilder.AddPage` tested
only `<= 0`; a JPEG frame header claiming 65 535² would reach the output on both DOCX render paths if
the writer did not ask the authored-image size rule; a package part declaring 257 MB would be read
whole without the part cap. **When a review finds a class, write the probe
before reading the next component for it.**

### Nullable reference types — per-project, and enforced

**`<Nullable>` is deliberately not set repo-wide.** With no annotations written, `annotations` mode
marks every reference type non-nullable in metadata, which would tell consumers the documented
"returns null on failure" methods never return null. Opted in and at zero: `Builder`, `HtmlToPdf`,
`Signing`, `Plugins.{Text,Svg,Docx,Html,Ocr}` (`EthereumSign` is `annotations`);
`Core`, `Base` and `Encryption` remain oblivious.

`src/Directory.Build.targets` makes thirteen nullable codes errors in any project that sets
`<Nullable>`, plus the doc-tag codes everywhere; `src/Directory.Build.props` makes the broken-doc-tag
codes errors. A new project starts with `<NullableWarningsAreErrors>false</NullableWarningsAreErrors>`
and a comment naming the tracking item, never a shared `NoWarn`.

Four shapes: the netstandard2.0 leg analyses differently (no `[NotNullWhen(false)]` on
`string.IsNullOrEmpty`; `required` does not compile); `CS8618`'s two fixes are opposite statements
(`= ""` says there is always a value, `?` says absence is meaningful; read the consumers); a `bool`
standing in for a null check cannot carry it; widening a shared helper moves the obligation to its other
callers.

---

## Diagnostics Pattern

Failure-tolerant code must not swallow exceptions silently. Choose one: render-time anomalies go to
`RenderDiagnostics.Report(kind, detail, ex)`; parsing, extraction and signing to a `static string
Last*Error` slot; a feature tolerating many failures and continuing to its own sink
(`Accessibility.Internal.AccessibilityDiagnostics` is the model).

> **A slot per concern is often better than a sink.** `PdfMerger` carries six named slots and
> `PdfSplitter` five; a caller reads a property instead of filtering an event. Many assignment sites are
> not evidence of a slot that loses information: `PngImageDecoder` writes its slot at sixteen mutually
> exclusive early returns. **Count what one call can write, not what the file can**, and **a slot a
> file or a script can write repeatedly has a cap, and the cap has a test**.

> **A slot can be clear-only.** When the condition a slot reported is removed, the reset outlives it and
> every `Assert.Null` on it passes with the feature deleted, and a guard asking only whether a slot is
> cleared counts a mutation deleting the reset as killed. `DiagnosticSlotResetTests` asks both
> questions. Revive the slot on the give-up paths
> that still exist, or delete the member.

> **Adding a `RenderDiagnosticKind` is often the whole fix**: a parameter parsed and dropped is
> indistinguishable at the pixel level from one never set. Keep `PatternFillUnresolved` (nothing painted)
> apart from `PatternApproximated` (painted, not as described); `RenderParameterIgnored` is once per
> content stream, **and only when `IgnoredParameterReporting.Deviates`**: `/HT /Default` and `/SA false`
> name the state we are already in, and a channel that fires on conformant input is one a caller
> learns to ignore.

> **A verification that cannot distinguish its two outcomes says nothing.** Verifying a template
> erasure by searching the page for the key, which the replacement may contain by design, would report
> a miss every time.

### Concurrency — `Last*` slots are `[ThreadStatic]`

Every `Last*` slot is backed by a `[ThreadStatic]` field; `BaseDiagnosticSurfaceTests` reflects over
the public static `Last*` properties and fails on any that lacks it. Three caveats thread-local storage
does not fix: read on the calling thread; a value does not flow across `await` (`DssBuilder.TryBuildDssAsync`
writes from `ConfigureAwait(false)` continuations; report on a result object instead); per-thread is not
per-document, so a slot is reset at the start of the operation on every path (`DiagnosticSlotResetTests`).
**A thread-static hand-off between two operators is a claim that nothing runs between them**, which no
language makes.

---

## Rendering

### There are two renderers, and a rule about that

`ContentStreamInterpreter` (raster) and `SvgContentWriter` (vector; `PdfSvgRenderer` is the entry
point) read the same files and must agree. **A silently wrong output is, as a rule, one of them lagging the
other.**

- Their partials share suffixes (`.Path`, `.Text`, `.Color`, `.Shading`, `.Images`, `.XObject`,
  `.InlineImage`, `.GraphicsState`, `.MarkedContent`); the SVG side has no `.OptionalContent` or
  `.Type3`, because those live inside marked content and text. When you add a file to one, ask whether
  the other needs the same name.
- **A spec rule gets one implementation, reached from both**, whenever the answer is a value:

  | Rule | Implementation |
  |---|---|
  | Optional-content visibility | `ContentStreamInterpreter.IsOcEntryHidden` / `IsAnnotationOcHidden` |
  | Colour-key `/Mask` (§8.9.6.4) | [`Rendering/ImageSamples.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/ImageSamples.cs), a leaf: `TryComputeColorKeyMask` compares **raw integer samples in the source space**, discriminated on buffer size because `DCTDecode`/`JPXDecode` pass bytes through, and `TryReadColorKeyRanges` is the one read of the ranges — **resolving**, never casting a bound straight to a number. The colour converter carries a key across a conversion with it, so it is a leaf rather than part of `ContentStreamInterpreter`, reached sideways; the sample-arity answer (`ComponentCount`) is there too |
  | Image decode + CCITT `/Columns` repair | `DecodeImageStream` (`internal`) |
  | Per-vertex `/Function` for shading types 4–7 | `MeshVertexColor` |
  | Which page box to draw (§14.11.2) | `PdfPageRenderer.ResolveRenderBox` |
  | §7.9.5's normalisation of that box into an origin and an extent, and the "no usable page box" fallback | [`Rendering/PageBoxMetrics.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PageBoxMetrics.cs) — a leaf, read by `MeasurePage`, `RenderPageTileToBitmap` and the SVG renderer alike, as the box *choice* and the rotation are |
  | §9.6.6.2 Type 3 char code → glyph name | [`Rendering/Type3Encoding.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/Type3Encoding.cs) — a leaf with no static fields |
  | §12.7.3.3 stale-widget appearance | `AnnotationFallbackModel.{FormNeedsAppearances, WidgetAppearanceIsStale}` — handed the `/N` stream each renderer already selected, so `/AS` choice stays in exactly two places |
  | All colour conversion and quantisation | `PdfColor` |
  | `[/ICCBased …]` in a nested position | `IccColorTransform.ResolveAlternateColor` — **twelve** call sites, six per renderer. Count it, don't quote it. |
  | An `/ICCBased` space's arity — its profile's `/N` | `PdfColor.IccComponentCount`, which falls back to 3; `PdfImageExporter` decodes through the raster renderer, so it falls back as a page does |
  | An image's `/Decode` (§8.9.5.2) — read with every element resolved, then applied per component | `PdfColor.DecodeArray` / `PdfColor.ApplyDecode`. Casting the elements raw after a check that resolves them would find `/Decode [9 0 R 10 0 R]` inverting and then apply it as the identity; the colour converter reads both too, and applies the mapping **before** converting — dropping `/Decode` would turn an inverted scan into a negative |
  | What a `/BM` value denotes — a name, or §11.6.3's "first blend mode in the array that it recognizes (or Normal if it recognizes none)" — for the `gs` operator and the annotation compositor | `PdfBlendModeEntry.FirstRecognised`, a leaf; what "recognises" means is `Core.Constants.PdfBlendModes.IsStandard`, shared down into Core so PDF/A's A40 asks the same list |
  | Whether an annotation is drawn at all: the §12.5.3 flag block, read once for both loops, plus §12.7.5.5's rule that a signature widget flagged NoView is not printed either | `AnnotationVisibility.IsHidden` (`/FT` inherited through the existing climb) |
  | A transparency group's blending space (§11.6.6 Table 147 `/CS`, §11.3.4) — grey, RGB, CMYK, inherit, or a space the standard does not admit; the page group's (honoured whatever its `/I`) and a `Do`'s alike | `PdfColor.TryGetGroupBlendSpace` over `PdfColor.TransparencyGroupColorSpace`. **The two renderers answer it differently by design, and the diagnostic says which**: the raster path composites a grey or CMYK group in its space — [`Rendering/GroupBlendSpace.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/GroupBlendSpace.cs), one SkSL blender per (space, mode) writing §11.3.6's formula per component in that space (blend function on the complements for CMYK, §11.3.5.3's K rule for the non-separable modes, BT.601 grey), set on a paint by `PdfRenderContext.ApplyElementBlend`, the one place a paint's blend is decided; the layer's pixels stay sRGB because the device conversions are exact inverses. An opaque Normal paint keeps Skia's fixed mode (same pixel in every space — the shortcut, whose coverage edge interpolates in RGB). The SVG export composites in sRGB and reports every non-RGB group (`SvgContentWriter.ReportGroupSpaceApproximation`) |
  | An annotation's composite (§12.5.5): its `/BM`, and `/ca`, else `/CA`, else 1 | `AnnotationAppearance.BlendMode` / `CompositeAlpha` — raster through `BeginTransparencyGroup`, SVG through `opacity` + `mix-blend-mode` on the annotation group |
  | The frame a widget without `/AP` gets — Table 192's `/MK /BC` at the `/BS` width, nothing for width 0 or no `/BC`; never `/C`, never a floor of 1 pt | `AnnotationFallbackModel.WidgetBorderColor` |

- **`PdfResolve` for every dictionary read.** The indexer does not follow an indirect reference, so
  `dict[PdfName.X] as PdfArray` yields `null` whenever a producer writes `X` as `6 0 R`. **After fixing a
  non-resolving read, grep the whole family**, one level and one *array* down: a reader that follows
  `/W`'s every element is no help if `/DW2` and `/W2` are read as stored, and a resolved `/Rect` whose
  elements are read raw makes an indirect element 0, stamping an appearance in a box the file never
  gave. The family is every reader of the same kind of value: every element of an array a dictionary
  holds is read through `PdfResolve.ArrayDouble` or `ArrayFloat` — a `/Rect`, `/BBox`, `/Matrix`,
  `/QuadPoints`, a border width and dash, an annotation colour, a CIE space's `/Gamma`, `/WhitePoint`,
  `/Matrix` and `/Range`, an `/Indexed` Lab range, a soft mask's `/BC`, a JPEG 2000 `/Decode`. Each of
  those was read through `ContentOperand`'s element readers, which are right for a content-stream
  operand — a content stream holds no references — and read a reference as 0: a referenced gamma fell
  back to 1, a referenced dash drew solid, a referenced link border drew at width 1.
- **A `Q` leaves the text matrices** (§9.3.1): the raster renderer always did, and the SVG writer, both extractors
  and the redaction and template engines each restored them, which put what follows a `q … Q` inside a text
  object on top of what came before it: a letter lost to the duplicate rule, a region off the text it was made from.
- **One quantisation rule, `PdfColor.Channel`** (clamp and round). Never `(byte)(v * 255)`.
- **`SKBitmap.Pixels` is not the way to touch pixels**: the getter allocates and un-premultiplies the
  whole image (33 MB and 264 ms for one soft mask at 300 dpi). Use `BitmapPixels`, which walks the buffer
  and keeps the one fallback for an untrustworthy colour type (`BufferWalks`/`PropertyFallbacks` are the
  probe). **Straight is not raw**: on a `Premul` surface the stored bytes are colour·α, and almost every
  pixel of almost every fixture is opaque, so the error is invisible. Both renderers decode images
  through `ContentStreamInterpreter.DecodeImageXObject` (a second decode in the SVG writer, claiming to
  mirror the raster one, would drift from it);
  what the writer still rasterises is a stencil for an SVG `<mask>`, the one `Gray8`/`Rgb888x` bitmap
  left, so check `CanStore` before writing one.
- **Share downward into a leaf, never sideways.** `ContentStreamInterpreter` has a static initializer;
  pointing `PdfSvgRenderer` at it creates a type-initialization edge.
- **A comment asserting a safety property is not the property**, and can justify the bug it sits on. A depth cap bounds one branch, never the number of branches.
- **Assert the two against each other**: `SvgRasterParityTests` compares pixels,
  `RenderGeometryParityTests` geometry (a wrong-sized export is internally consistent). SVG assertions
  parse an `XDocument`, never a substring. A defect in an advance is invisible to a pixel comparison;
  assert the position of a later glyph.
- **The SVG side owns what is SVG-shaped**: `Shared.IdCounter` for every minted id (a child writer's own
  counter restarts at 0), the JPEG pass-through, the stencil `<mask>`, and `SvgSyntax` (a leaf with no
  static fields: `XmlEsc`, the `G6` number format, `HairlineStrokeWidth`). **The page's font cache and
  its font-program reads are on `Shared` too** (`Fonts`, `FontPrograms`): a cache per child writer would
  parse a form's fonts once per invocation, a hundred thousand times for a form invoked that often, and
  read the program for its `@font-face` before the registry — keyed by the program's object number,
  which only the read knows — could answer. The cache keys a font by its resources dictionary or its
  object number, so writers with different `/Resources` share it, as the raster interpreters do.
- **A font program the SVG embedder synthesises is glyph-indexed.** `SvgFontEmbedder` re-emits an
  embedded program as an `@font-face` for the runs outlining declines (vertical writing, a program
  SkiaSharp would not load). A browser loads sfnt containers and nothing else, so a bare CFF is wrapped
  by `CffOtfWrapper` and a Type 1 program converted through `Type1ToCffConverter` and wrapped — and the
  font's own encoding is not the document's (the PDF's `/Encoding` or CMap chooses the glyph; a
  CID-keyed CFF has no encoding), so the synthesised container's `cmap` maps U+F0000 + *g* to glyph *g*
  (`TryBuild(…, glyphIndexedCmap: true)`, one format-12 group) and the writer hands the viewer those
  code points for the visible run with the document's text in the zero-opacity overlay the outline
  path already writes. The default stub `cmap` stays for the raster path, which draws by glyph index.
  **The test's decoder is SkiaSharp's `SKTypeface.FromData`**, asked which glyph each code point
  selects — a round trip through our own CFF reader would share whatever both halves believe. A
  test's CMap says `bfchar` where a `/ToUnicode` needs it (`cidchar` leaves the overlay empty), a fact
  only the decoded text shows.
- **A tiling cell is drawn at the tile's scale, never `DpiScale`**: the tile is one step in whole pixels,
  so at the page's scale a one-point step would paint nothing and a fine hatch gap by a tenth of its
  period — invisible to a parity suite whose every fixture's step is a whole number of pixels.

### ContentStreamInterpreter

Operator handlers live in `ContentStreamInterpreter`, registered in `Dispatch` and implemented as a
private `Op<Name>`. **Never modify `PdfRenderContext` for a concern that belongs to the interpreter**,
with one exception: the parsed-operation cache hangs off the context, because nested content builds a
new interpreter each time while sharing one context.

### Performance — measure before you rank

**PNG encoding is about 97% of a blank raster render and about 51% of a real one; opening a document is
about 420 µs and negligible; tokenizing is 25% of a vector page's allocation** (1.66 MB of 6.73 MB) and
7% of a text page's (0.55 MB of 8.02 MB), with §13b's struct lexer. Those are three different costs
the word "parsing" hides, and a ratio is re-measured before it is ranked by. Rank by allocation: the render timings carry
standard deviations of 16–30 ms, and the allocation column is byte-identical run to run. The object
parser's own `PdfToken` per token is not the next target: opening and walking a fifty-page document
allocates 330 KB in all (`ParseBenchmarks.OpenAndWalk_ClassicXref`).

- `PdfRenderOptions.PngCompression` is `Fast` by default (zlib 3 + Sub); lossless at every setting, so it
  cannot change a pixel; `Smallest` trades time for bytes.
- `RenderPageToPixels` returns a `RenderedPage` with no SkiaSharp type, so consumers are not pinned to a
  SkiaSharp version.
- **`PdfRenderSession` is a tile optimisation and a page loop's** (4×4 tiling 363 → 160 ms); ten
  whole-page renders of a small document measure 354 ms one-shot against 383 ms through a session. A
  one-shot render opens the whole document, though, and its page tree is walked each time: a loop of
  them over a document's pages is quadratic. 800 pages take 35 s and 5 GiB one-shot against 0.8 s and
  20 MiB through one session; the HTML converter and the OCR plugin's document door use a session. It
  is not thread-safe.
- Batch rendering is parallel behind `RenderAllPagesCore`, each worker with its own document and shared
  state; `MaxRenderThreads = 1` restores the calling-thread profile a host reading `[ThreadStatic]`
  diagnostics needs.
- **One encoder, `PdfImageEncoder`**, for PNG, JPEG, WebP and the separated TIFF of plates (planes, not a
  bitmap, since N ink planes have no Skia surface); raw samples from outside Base go through the public
  `PdfRawImageEncoder.EncodePng`, so no plugin writes its own PNG.
  JPEG 2000 is the codec's own door, `Core.Imaging.Jpx.JpxImageEncoding`. `WebpLossless` measures 32 KB
  against PNG's 280 KB on a rendered page; JPEG is the largest of the three for text pages and lossy.

> `benchmarks/Tetitulz.Pdf.Benchmarks` is the authority. **Run `-- verify` first**: a benchmark whose
> fixture never reaches the feature produces a plausible number, banned pattern 3 with a stopwatch. Say
> which regime a ratio is from (cold, the DocxToPdf font scan is I/O over parsing 14:1; warm, parsing
> is the larger half). Read the allocation column even when you came for the time (an unbounded static
> retaining 210 MiB shows on no stopwatch). A per-call cost is not a ranking until multiplied by a call
> count. Stub the function and measure the delta for an upper bound. **A new entry point is a new
> chance to reintroduce a divergence the shared decision points closed**: check it against the existing
> path on encryption, colour and clipping.

**A fit measured by a whole-chunk trial is found by doubling and bisection**. **A
per-item search that may run to the end of the buffer is quadratic in the item count**: a search to
the end of the buffer for `endobj` once per header turns a million eight-byte headers into an
eight-terabyte scan, and a test host that reports every test green and then spins one core is running it. Stop at the next item's boundary and
remember a search that found nothing (`ResumeCursor.NoEndstreamFrom`); the test asserts the bytes read
against a multiple of the file.

---

## Testing

- Every new method needs a test. Tests build a minimal in-memory PDF rather than reading a file; xUnit,
  no mocks unless unavoidable; `tests/Tetitulz.Pdf.<Xxx>.Tests/<Feature>Tests.cs`; `dotnet test` after
  every change.
- Every DoS or recursion guard gets an adversarial-input test that hangs or overflows without the
  guard, asserting the guard's own evidence.
- **Two corpora are committed**, each CC BY with its licence beside it; the first a subset under
  `tests/Tetitulz.Pdf.Plugins.Conformance.Tests/Fixtures/veraPDF/` (README, LICENSE, `.gitattributes`). `ConformanceCorpusTests` checks every
  file's verdict against its name on every run, not as a `[CorpusFact]`. A disagreement is
  either listed in `known-disagreements.tsv` with its reason (`miss` — a rule not yet implemented,
  `stricter` — deliberate, `deep` — a PDF/UA-1 rule the deep checker owns, so the plugin's checker
  alone passes the file, with the code `PdfAccessibility.Audit` fails it with, which Accessibility's
  `UaConformanceCorpusTests` asserts) or fixed; an entry whose file starts agreeing fails the test until it is
  removed, so the list is the burn-down of the conformance checkers.
- Developer-local PDFs resolve through `TestHelpers.TestCorpus` (`TETITULZ_PDF_CORPUS`, `TETITULZ_PDF_RAW`,
  else `tests/pdf` and `raw`), never committed.
- **Corpus tests carry `Category=Corpus`, which is what makes a local run fast**: Base's few dozen
  corpus tests cost more than all the others together (90 s against 43 s), which the per-test durations
  of a parallel run hide (a 365 ms test reports 18.5 s under contention). Use
  `--filter "Category!=Corpus"` for the inner loop; `mutate.ps1` leaves the corpus out of every row,
  since CI has no corpus and a corpus-only kill is a survivor there.
- **RSA keys come from `TestHelpers.RsaTestKeys`, never a fresh generator**: BouncyCastle's generator
  costs a quarter of a second per key, most of a signing suite's time. The pool gives each test its
  own cursor over shared keys, so every key a test draws is distinct from the others it draws; a `static`
  fixture builds its certificate inside `RsaTestKeys.Shared(...)`, or a later test can be handed the
  same key.
- **Do not record a test count anywhere.** CI is the single source of truth.

### Three banned patterns — all fail the build

Patterns 1 and 2 in `NoHardcodedPathsTests`, pattern 3 in `NoUnfalsifiableAssertionsTests`, both
reading the tree through `TestSourceScanner`.

1. **No absolute path into a local checkout.** An unrelated absolute literal is fine.
2. **No `if (!File.Exists(path)) return;`**: an early return reports Passed while executing nothing.
   Gate with `[CorpusFact]`/`[CorpusTheory]`, which report Skipped.
3. **No assertion that cannot fail.** `Assert.NotNull(png); Assert.NotEmpty(png);` passes on a blank
   page; `Record.Exception` with `Assert.Null` is the same test in a `try`. Assert on decoded pixels or
   the parsed object. There is no allow-list. A test whose only assertions live inside a helper that
   returns a value is flagged too: assert on what the helper returned.

A no-throw test on hostile input is not pattern 3: the method name must carry a token from
`HostileInputNameTokens` (the source is the authority; `DoesNotThrow` and `NoException` are deliberately
absent, since they describe the assertion, not the input). Two blind spots: the token test is a substring
match (`Cap` exempts "Caption"), and `Assert.True(png.Length > 100)` counts as strong.

> **A negative assertion needs a potency assertion beside it.** `DoesNotContain` passes when the
> operation threw and something upstream caught it into an empty output — an extractor indexing past
> the end into an empty layout passes an absence-only test.

> **Two methods documented as answering one question are asserted in one test**, with a control that
> agrees on a matching input — `PdfTextSearch.Contains` and `Search` for a query that folds away to
> nothing, for instance.

**A test whose name and body disagree is a failure mode no guard can catch.** When a behaviour changes,
rename the test or restore the behaviour; never edit the assertion and leave the name.

### Three blind spots no assertion about the *answer* can reach

1. **A parser normalises away what a raw writer can get wrong.** `PdfSigner` writes the signature
   dictionary as raw bytes, so a key could be emitted twice (§7.3.7 leaves that undefined):
   `SignatureDictionaryKeyUniquenessTests` reads raw bytes. An ASN.1 object model normalises too:
   IMPLICIT versus EXPLICIT tagging lives only in the identifier octet (`0x80` against `0xA0`), so a
   mutation flipping ISO/TS 32004 §6.2's tagging survives every assertion on the parsed object.
2. **An optimisation whose fast and slow paths agree cannot be pinned by the answer.** Deleting the
   `Sort` in `CMapCodec` abandons the CID index library-wide and leaves the answers identical; assert
   liveness (`UsesCidRangeIndex`). A DoS ceiling is the same shape: deleting the `return` at an edge
   budget leaves the truncation flag set and the output identical; probe the work
   (`LastEdgesExamined`).
3. **A relative assertion cannot see a change applied to both sides.** Deleting Algorithm 2.B's 64-round
   minimum alters six R6 hashes in seven and survives every round trip; pin a characterisation value.
   BER where DER is required parses identically on both sides of a round trip: hand the output to an
   independent implementation (`System.Security.Cryptography.Pkcs`), or assert the declared length
   against what a reader walks.

### Mutation

**Mutate the code to prove a new or rewritten test can fail, and read a surviving mutation as a finding
about the test.** Four explanations: the fixture never reached the feature; the assertion was weaker
than the claim; the mutation was equivalent; **the line does nothing** ("I deleted it and everything
stayed green" is identical evidence for no coverage and for dead code; only reading the callee tells
them apart). A fifth when mutating an authoring side against a checker: **the standard permits it**
(ISO 14289-2 §8.2.4 lets an element carry no `/NS`; a producer may be stricter than required). Read the
clause before "fixing" the checker, and leave an acceptance control.

- Re-run a mutation against the test written for it, and against every consumer's suite: a filtered
  run can only say nothing in the filter kills it, and unasserted call sites show only this way.
- A guard can be redundant at one end of its input range and load-bearing at the other (reflow refuses
  rotated text; a 45° fixture never reaches the check, under a degree the drift stays inside the
  baseline tolerance and the check is the only thing left).
- A merge exposes the branch nothing tested: mutate the survivor.
- Before removing a write, grep every read of what it wrote: a stamp that looks unused can be the
  number an identity check compares. A stage that also removes
  what an earlier stage removes can un-kill the earlier stage's mutations; re-run them.
- A rename that breaks the build proves nothing; report build failures as such.
- Quote the run's total and skip count beside every verdict: a short total is a run that died, and a
  skipped killer is indistinguishable from a missing one (a mutation surviving against skips whose
  names name the path being mutated is not exercised, neither a kill nor a survival).
- A ceiling only production scale reaches is tested as a parameter (`Woff2Decoder.Decode(byte[], long)`),
  **and the forwarding is mutated too**, or every small-ceiling test passes over the production path.

**Harness hygiene** (`.claude/mutate.ps1`), each rule against a false clean sheet:

- A missed anchor is not a kill: line endings are mixed, so derive the separator per file by majority
  and assert the anchor's occurrence count.
- A driver row is a PowerShell array literal, and `'a' + [char]10 + 'b'` inside one is several
  elements; write a multi-line anchor as one double-quoted string.
- A kill against a red baseline is not a kill: run the suite green in the harness's own process first.
- A kill can be for the wrong reason: replacing a brace-less `if` body with a comment hands the `if`
  the next statement; replace it with `;` and read which tests
  killed.
- A kill against a corrupted baseline is not a kill: a harness killed mid-run skips the `finally` that
  restores the file, and a restore that compares against its own in-memory copy proves only that it put
  back what it found. After killing a harness, re-run the suite.
- Kill the `testhost`, not just `dotnet test`: the host survives, holds the DLL, and every later build
  fails with `MSB3027`.
- Never two runs at once: the backup is named after the file, so the second run backs up the first's
  mutant. `mutate.ps1` takes a lock; stopping a background job is not enough, kill the shell by pid.
- A stale DLL reproduces a mutation you removed: when a failure's numbers do not match the source,
  rebuild.
- A mutation that makes a DoS fixture potent does not return, and that is the strongest kill; so is one
  that eats memory (`-MaxTestMemoryGB` polls the host's private bytes, since a sweep can stall the machine on its page
  file with no timeout firing). After a reset, restore the tree
  from git, not from the backup.
- A scripted edit must not change a file's encoding or BOM (`utf-8-sig` writes one).
- **A sweep mutates the shared checkout.** While one runs, do not edit a source file it names, do not
  trust a read of one, and do not run `dotnet` beside it (a file read while its row is in shows the mutant).

> **A refusal test must refuse for the reason in its name.** Give it an input only the named guard can
> reject, and pair a file of refusals with an acceptance control. An `mBA` refusal test can pass for the wrong
> reason: `IccLut.Parse`'s default arm rejects the same signature.

> **The overnight sweep (`.claude/overnight.ps1`) drafts rows with a local model; its report is a list
> of hypotheses.** The model's rows find holes hand-written rows miss and miss coordinate, index and
> rotation rows. Hand-write those before calling a file done.

### Diagnosing a red whole-solution run

1. **`dotnet test --blame` first.** It runs sequentially; green there means concurrency, and the
   "which assertion is wrong" search is ruled out.
2. **An assertion that reads shared state asserts about the whole process.** Filter the handler to the
   calling thread; do not serialise the collection. Three routes into it: an unfiltered `RenderDiagnostics.OnEvent`
   handler, a hook on the singleton `PdfPluginHost`, and a publisher invoking its multicast delegate in
   one call so the first throwing subscriber aborts the rest. A correct collector that misses an event means the publisher lost it.
3. **A count that disagrees with the assembly's total is a stale binary or an aborted run.** A green run
   is not evidence; a green run with the same count is.
4. **A DoS-guard test asserts the guard's own evidence, never a wall clock.** Under a full-solution run
   elapsed time measures the machine's backlog; ceilings under 30 s flake. Remedies in order: delete the
   clock; run on a dedicated `Thread` with a generous `Join` (`BoundedWalkTimeout`, five minutes, shared
   by both callers); measure the thread's CPU time.
5. **A merge whose survivor no test reaches is a deletion.** Mutate the survivor and count what reddens.
6. **Run `git status` before calling a repository-wide guard flaky**: a second session editing the tree
   is the cheaper explanation.
7. **A test host that lingers after `Passed!` is a test still running.** `dotnet-stack report -p <pid>`
   names the thread and the test; `--blame-hang` names nothing once the summary printed.

Other shapes of the same class: a table assigned empty inside a lock and populated after it, which two
parallel workers can read half-built (build it in the type initialiser); a fixture counting a request
after flushing its reply; a worker pool waiting on thread-pool threads under contention (use
`LongRunning` threads); a dedicated thread's ceiling too short for a full-solution run.

### Parallel work

- **Partition by assembly.** A conflict is the cheap case; the auto-merge that silently takes one side is
  the expensive one. After a parallel merge, diff each parent against the result and account for every
  hunk that did not survive: absences (an undisposed buffer, a dropped CRL) are invisible to every
  assertion that does not name them.
- **A finding list and a register row are hypotheses; the code is the authority**, and checking costs
  one or two greps.
- **"Nothing implements it" is not "nothing calls it".** `IRenderHook` and `ITextExtractionHook` have
  zero implementers and are dispatched on every render and extraction; `PluginHookDispatchTests` asserts
  what the hook was told.
- **Corpus smoke tests correlate against a second opinion**: a page that shows text must extract text;
  "it returned something" passes with the feature deleted.
- **Every writer's output passes an independent structure check, in both file layouts**
  (`WriterOutputIndependentCheckTests`: each writer over a fixture written classic and, recorded once,
  by an independent producer with object streams). `IndependentPdfCheck` re-derives the file from its
  bytes — xref offsets, `/Prev` chains, stream framing, object streams, the page tree, every Flate
  stream, decrypted first under either password — and reports what it had to forgive, so anything it
  reports is a finding. Its own tests (`IndependentPdfCheckTests`) make each fault once beside a clean
  control and assert how many streams it decrypted and inflated: a checker that passes everything
  makes every writer test vacuous. **A new writer joins the table.**
- **An encoder's stream is read by a decoder this repository did not write.** The answer an
  independent decoder gave is recorded beside the test, keyed by a SHA-256 of the bytes and the
  decode's options (`IndependentJpxDecode`, `IndependentImageRender`). An encoder whose output changes
  fails with the key it looked for, until the new bytes are decoded independently and recorded
  (`TETITULZ_RECORD_INDEPENDENT=1`, with a live decoder plugged into the seam from outside the
  repository). **No test runs a program outside the repository; a record proves what the decoder said
  about those bytes and nothing about any others.**
- **The public-corpus scorecard** (`tools/corpus-scorecard`) runs every PDF in a directory through open,
  render, text, an incremental save and a full rewrite, a child process per file; a structure check reads the input
  as well as each output, so the "made worse" rows must stay at zero. **The structure fuzzer is the same
  harness fed a generated corpus** (`--generate`, `HostileGraphs.cs`; `--generate-html`,
  `HostileHtml.cs`): seeded, twenty families each, run after a reader changes and before a release.
  **What the HTML mapper hands a dependency it cannot interrupt is bounded before the hand-over, and the
  bound is measured**: `RefuseDeepMarkup` (25 000, since AngleSharp's tree builder is quadratic in open
  list items), `SelectorComplexity` (32 compounds, since its matcher is exponential in descendant
  combinators), `ParseSpan` (the HTML Standard's clamps), `MaxTableCells` (100 000, priced at about 1 KB
  of model and 3.5 KB through the export per tagged cell).
- **Coverage-guided fuzzing is `tools/fuzz`** (SharpFuzz under libFuzzer): twenty targets over the public
  doors, each swallowing only the declared refusals; `run.ps1` builds, instruments Core, Base and
  Builder, downloads the pinned driver and keeps the corpus between runs. **A timeout is a finding about
  cost**: instrumented code runs about six times slower, so replay the artifact on the shipped build or
  probe it before judging. A target is run before a release and after its reader changes; a slice of
  each runs nightly.
- **The READMEs' samples compile** (`tools/readme-samples/check.ps1`). No build compiles
  them otherwise, and they are what a consumer copies first. The check builds a throwaway project over every
  library with every library namespace imported. A block that does not parse is a listing and is
  checked by reflection instead. An undefined local name is a fragment's own variable. Any other
  error is a sample the code does not support. A parser-error range would hide such errors: CS1729
  and CS1061 sit among the parser's numbers, so the syntax codes are listed by name. Run it before a release and after changing a public API a README shows.
- **The nightly digest** (`.claude/nightly.ps1`) runs every suite and the scorecard against master in
  its own worktree and writes a dated digest: per-suite counts, failing tests, what is new or fixed. Read
  it before re-running suites.

---

## CI

Three jobs: `build-test` on `windows-latest` (the full solution with coverage, plus the packing
gate), `linux-verify` on `ubuntu-latest` and `macos-verify` on
`macos-latest`, the last two on `tetitulz.pdf.portable.slnf`. A `v*` tag adds a fourth, `release`: once
the three are green it pushes the tag's `.nupkg`s to nuget.org, then attaches them to a GitHub release
through the runner's GitHub CLI. The apps are built, tested and released by `tetitulz.pdf-apps`' own
workflow. It is the one job whose token writes (`contents: write`, and `id-token: write` for
nuget.org). **No nuget.org key is stored**: `NuGet/login` trades the run's OIDC token for a
short-lived key under the account's Trusted Publishing policy (this repository, `ci.yml`, no
environment), the account named by the repository variable `NUGET_USER`; the job stops before
pushing when it is unset. nuget.org goes first because a version there cannot be deleted, only
unlisted, and `--skip-duplicate` lets a re-run finish what a partial push left. All in
[`.github/workflows/ci.yml`](.github/workflows/ci.yml).

**The workflow's own settings**: a read-only token outside `release`, a timeout on every job, every action pinned by
commit SHA with its tag beside it, `global.json` pinning the SDK's feature band, Dependabot for NuGet and
the actions, and no test oracle to install. The workflow triggers on `main`, on pull requests to it
and on `v*` tags.
**A shipped package's dependency version is every consumer's floor.** NuGet resolves the lowest version
a package allows, and a consumer that pins the dependency below it (central package management with
transitive pinning) fails to restore until it raises its pin, so the library asks for the lowest version that
carries what it needs: an API, or a fix it depends on (`System.Security.Cryptography.Pkcs` stays at the
first version with its CVE fixes). A newer version is the consumer's to take. Dependabot skips the majors
of the BCL packages the libraries ship with (`.github/dependabot.yml`); a patch or an advisory still comes.
**The owner's:** the remote and the branch filter, the nuget.org account and its Trusted Publishing
policy (no `.snupkg`; author signing needs a certificate), an SBOM step on the release.

**Six guard steps, none relaxable to go green:**

| Guard | What it checks |
|---|---|
| every `src/` project is in `tetitulz.pdf.sln` | otherwise `dotnet pack` never produces the library |
| every `src/` library has a `tests/<name>.Tests/` project | its `KnownGaps` list is self-cleaning and empty; do not re-populate it |
| every portable `src/` project is in `tetitulz.pdf.portable.slnf` | the filter is hand-maintained |
| only `src/` libraries were packed | an allow-list derived from the tree, not a denylist of app names |
| no placeholder `TODO_` metadata reached a `.nuspec` | the next placeholder is one careless `<PackageTags>` away |
| the test run actually completed | `dotnet test` prints `Passed!` for the tests that finished and says nothing about ones that never ran, so the run is compared against itself |

Two gates beside them: the dependency vulnerability audit and the coverage ratchet
(`tests/coverage-baseline.json`, a floor that only moves up). Two more are run by the developer, their
scripts being local: restoring the packed `.nupkg`s into a throwaway consumer
(`tools/package-consumption`) and the benchmark fixtures' `-- verify`. Two guards are compiler settings
(*Nullable reference types*); never `NoWarn` to go green.

---

## Build & test

```bash
# Run from the repository root — never hardcode a checkout path.
dotnet build tetitulz.pdf.sln
dotnet test  tetitulz.pdf.sln
dotnet pack  tetitulz.pdf.sln -c Release -o artifacts              # every library package
TETITULZ_PDF_CORPUS=/path/to/pdfs dotnet test tetitulz.pdf.sln    # the corpus-gated tests
dotnet test tetitulz.pdf.sln --filter "Category!=Corpus"           # the inner loop
```

Versions come from git tags through MinVer ([`Directory.Build.props`](Directory.Build.props)); an
untagged commit gets a prerelease version.

### Gap-closure checklist

1. Write the implementation.
2. Write tests, including an adversarial-input test for any DoS or recursion guard, and mutation-prove
   them.
3. Expose it via `PdfOperations` if it belongs to the public API.
4. Update `GAPS.md`: the closed bullet deleted, or moved to §8 with its reason. The
   commit message records the closure.

---

## Key file locations

Each row carries the contract its component keeps. Read a row before editing the file it names.

| Concern | File |
|---|---|
| PDF parsing entry point | [`Core/Parsing/PdfParser.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Parsing/PdfParser.cs) |
| Raster interpreter — 11 partials beside `ContentStreamInterpreter.cs` (fields, `Execute`, the 72-case `Dispatch`) | [`Base/Rendering/ContentStreamInterpreter.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/ContentStreamInterpreter.cs) |
| SVG renderer — `PdfSvgRenderer` is the entry point; `SvgContentWriter` is the interpreter, in 9 partials mirroring the raster names | [`Base/Rendering/PdfSvgRenderer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfSvgRenderer.cs), [`SvgSyntax.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/SvgSyntax.cs) |
| Colour conversion, shared by both renderers | [`Base/Rendering/PdfColor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfColor.cs) |
| Reference-following dictionary read — use for **every** `dict[key]` in the render path | [`Base/Rendering/PdfResolve.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfResolve.cs) |
| Blend-mode mapping + knockout substitution, open-group stack | [`Base/Rendering/PdfRenderContext.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfRenderContext.cs) |
| Clip culling + the single CTM scale (the cull margin must use the **CTM-scaled** width, §8.4.3.2) | [`Base/Rendering/PdfRenderContext.Path.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfRenderContext.Path.cs) |
| PDF function evaluation (types 0/2/3/4) | [`Core/Functions/PdfFunctionEvaluator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Functions/PdfFunctionEvaluator.cs) |
| Image-decode plugin dispatch (both renderers) | [`Base/Images/PluginImageDecoder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Images/PluginImageDecoder.cs) |
| ICC.1 profile reader + tone curves | [`Base/Rendering/IccProfile.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/IccProfile.cs) |
| ICC evaluation + `[/ICCBased …]` routing (both renderers) | [`Base/Rendering/IccColorTransform.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/IccColorTransform.cs) |
| PostScript plugin dispatch (both renderers) | [`Base/Rendering/PluginPostScriptTranslator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PluginPostScriptTranslator.cs) |
| PostScript interpreter + its DoS table | [`src/plugins/Tetitulz.Pdf.Plugins.PostScript/`](src/plugins/Tetitulz.Pdf.Plugins.PostScript/README.md), [`PsLimits.cs`](src/plugins/Tetitulz.Pdf.Plugins.PostScript/PsLimits.cs) |
| JPEG 2000 codec chain + JP2 header parsing | [`Base/Images/JpxImageDecoder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Images/JpxImageDecoder.cs) |
| Sample-channel meaning (colour arity, `/SMaskInData` alpha) → `SKBitmap` | [`Base/Images/ImageSampleLayout.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Images/ImageSampleLayout.cs) |
| JPEG frame header (SOF dimensions, Adobe APP14) — the one marker walk — and the colour space and `/Decode` an embedded frame takes | [`Core/Imaging/RasterImageInspector.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Imaging/RasterImageInspector.cs) (`TryInspectJpeg`); [`Core/Imaging/JpegColorSpace.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Imaging/JpegColorSpace.cs) |
| One PNG decoder, in Core | [`Core/Imaging/PngImageDecoder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Imaging/PngImageDecoder.cs) |
| Render diagnostics sink | [`Base/Rendering/RenderDiagnostics.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/RenderDiagnostics.cs) |
| The one stream prologue — seek-normalise → decrypt → open → dispose what the call owns | [`Base/Rendering/PdfPageRenderer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfPageRenderer.cs) |
| Pre-render geometry (`GetRenderSize`, `MeasurePage`, `ResolveRenderBox`) | [`Base/Rendering/PageRenderSize.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PageRenderSize.cs) |
| The single image encoder — PNG / JPEG / WebP and the separated TIFF of plates, grayscale, alpha reconciliation, provenance stamp; its public door for raw 8-bit grey or RGB samples. JPEG 2000 is not its fourth format: that is the in-house codec's own door, `Core.Imaging.Jpx.JpxImageEncoding` | [`Base/Rendering/PdfImageEncoder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfImageEncoder.cs); [`PdfRawImageEncoder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfRawImageEncoder.cs) |
| Colour separations: a pass per plate, the paint colour read as the plate's tint (`PdfRenderContext.PaintColor`), images from their samples, a tiling cell on the plate and a soft mask's group off it; plates out as one separated TIFF. Checked against the reference renderer's `separation output` (`PdfSeparationRendererTests`) | [`Base/Rendering/PdfSeparationRenderer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfSeparationRenderer.cs); [`SeparationPlate.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/SeparationPlate.cs); [`ContentStreamInterpreter.Separation.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/ContentStreamInterpreter.Separation.cs) |
| Render options / unencoded pixel output | [`PdfRenderOptions.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfRenderOptions.cs), [`RenderedPage.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/RenderedPage.cs) |
| Parsed-operation cache (bounded, lifetime chosen by its owner) | [`Base/Rendering/ContentOperationCache.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/ContentOperationCache.cs) |
| Reuse handle for tiles + repeated renders | [`Base/Rendering/PdfRenderSession.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/PdfRenderSession.cs) |
| §12.5.3 annotation visibility — `IsHiddenOnScreen` **and** `IsHiddenInPrint`; the one place both renderers ask them, with §12.7.5.5's NoView-signature rule | [`Core/Constants/PdfAnnotationSubtypes.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Constants/PdfAnnotationSubtypes.cs), [`Base/Rendering/AnnotationVisibility.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/AnnotationVisibility.cs) |
| §12.7.3.3 stale-widget appearance model | [`Base/Rendering/AnnotationFallbackModel.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/AnnotationFallbackModel.cs) |
| §9.6.6.2 Type 3 char code → glyph name (leaf, no type initializer) | [`Base/Rendering/Type3Encoding.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/Type3Encoding.cs) |
| Shape → bidi → glyph pipeline, shared by form appearances, signature appearances and templating — **do not start another copy**. `NeedsCompositeFont` is the entry question, and `CodePointsToCover` is the one that picks the font: what the run will be **drawn** as | [`Core/Text/TextRunComposer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/TextRunComposer.cs) |
| Bidi, incl. the `BaseDirection` overload a form/template field needs (first-strong auto-detect is wrong for `"1,234 ₪"`) | [`Core/Text/UnicodeBidi.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/UnicodeBidi.cs) |
| Hebrew search folding — niqqud, punctuation, presentation and final forms, with a `sourceIndex` map so a folded match is reportable in the caller's coordinates. **Three callers, three different defaults, each stating its reasoning**: the viewer's find box folds, `PdfTextSearch` does not, `PdfTextRedactor` does not — a spurious hit costs a keystroke, a wrong match, or the document | [`Core/Text/HebrewSearchNormalizer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/HebrewSearchNormalizer.cs) |
| The nine-digit check digit shared by ת״ז, ח״פ and עמותה — **one algorithm, three identifiers, so nothing can tell them apart**, and one decimal digit means ~1 in 10 arbitrary numbers passes | [`Core/Text/IsraeliIdentifiers.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/IsraeliIdentifiers.cs) |
| Font search that checks real cmap **coverage**, not filenames; opt-in system substitution + the `fsType` licence gate | [`Core/Fonts/SystemFontResolver.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/SystemFontResolver.cs) |
| OS font-directory list + standard-14 → Liberation/DejaVu/Noto mapping | [`Core/Fonts/FontFileLocator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/FontFileLocator.cs) |
| Cheap "does this file cover these code points / what family is it?" — reads the sfnt directory plus **one** table, answers through the same `TrueTypeFontProgram.Parse`. **May only ever reject** | [`Core/Fonts/FontCoverageProbe.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/FontCoverageProbe.cs) |
| `OS/2` `fsType` — **bits 0–3 are one mutually-exclusive value, not four flags** (`0x0006` is Preview & Print, not "restricted") | [`Core/Fonts/FontEmbeddingLicense.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/FontEmbeddingLicense.cs) |
| The single sfnt assembler — sfnt **version is a parameter**, so it emits `OTTO` too. Public, because `Plugins.Maintenance` needs it | [`Core/Fonts/SfntBuilder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/SfntBuilder.cs) |
| Table pruning for embedding (drops `GSUB/GPOS/GDEF/DSIG` — a reader must not shape). Removes **tables, not glyphs**, so its output must never carry a subset tag | [`Core/Fonts/SfntTablePruner.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/SfntTablePruner.cs) |
| TrueType `glyf` subsetting (really rebuilds `glyf`/`loca`) | [`Core/Fonts/TrueTypeSubsetter.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/TrueTypeSubsetter.cs) |
| CFF subsetting — **blanks unused charstrings in place**, so the program length is unchanged and the win is post-Flate only. Owns the `seac` component scan; **a CID is not a glyph index** | [`Core/Fonts/Cff/CffSubsetter.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/Cff/CffSubsetter.cs), [`CffCharstringScanner.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/Cff/CffCharstringScanner.cs) |
| CID lookup index + its **liveness** assertion (`UsesCidRangeIndex`) | [`Core/Fonts/CMapCodec.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Fonts/CMapCodec.cs) |
| Font cache | [`Base/Rendering/FontCache.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Rendering/FontCache.cs) |
| §12.7.3.1 widget-vs-field — the **one** predicate. **`/FT` is not a clause**: Table 220 makes it inheritable. `PdfFormField.IsAddressableField` composes it with `IsTerminal` for "is this the node a name addresses" — **`IsTerminal` alone is not that question**, since a bare widget has no kids and so is terminal without being a field. **`WidgetFieldType` is the downward `/FT` step** — the type a bare widget kid declares, for a node whose chain declares none — shared by `PdfFormField` and `PdfSigner`'s adoption walk. It is in Core because the signer reads through a parser; a signer taking `/FT` from a node and its ancestors only would see a field whose type sits on its nameless widget as no signature field, and write a second field beside it | [`Core/Forms/AcroFieldClassification.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Forms/AcroFieldClassification.cs) |
| Annotation `/AP` generation + the **one** §12.5.5 placement algorithm. **Do not test this by rendering** — the renderer synthesises a fallback | [`AnnotationAppearanceBuilder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/AnnotationAppearanceBuilder.cs), [`AnnotationAppearanceStamp.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/AnnotationAppearanceStamp.cs), [`PdfAnnotationEditor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/PdfAnnotationEditor.cs) |
| OCR → searchable PDF. The layer is **invisible**, so its font is Type0/Identity-H with UTF-16 code units, an identity `/ToUnicode` and **no embedded program** — right here, wrong for any visible writer. Emission is **visual** order. **Invisible text is text**: structured extraction drops a mode 3/7 glyph only where a visible glyph of similar size covers it, because dropping them all would leave search-driven redaction nothing to find on a scanned page | [`PdfOcrLayerWriter.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Structure/PdfOcrLayerWriter.cs), [`OcrTextLayerFont.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Structure/OcrTextLayerFont.cs), [`PdfOperations.SearchablePdf.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/PdfOperations.SearchablePdf.cs) |
| FDF / XFDF — both formats, both directions, one model. Written **through `PdfWriter`**; read by **scanning for `N G obj`** rather than trusting the xref | [`PdfFormDataExchange.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/PdfFormDataExchange.cs), [`FormData.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/FormData.cs), [`XfdfSerializer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/XfdfSerializer.cs), [`FdfSerializer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/FdfSerializer.cs) |
| XFA — the packets (`/XFA`, both §12.7.8 shapes) and the `datasets` values. **In Core, not Base**, because the static-XFA filler and the dynamic-XFA content importer both read them and a second XML walk is not an option. The **template** packet is deliberately unread: its grammar is specified outside ISO 32000-2 and is not in `docs/iso/` | [`Core/Forms/XfaPacket.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Forms/XfaPacket.cs), [`XfaDatasets.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Forms/XfaDatasets.cs) |
| A **dynamic** XFA form's content → a `Document` every exporter can write. Annex K makes materialising the generated pages "implementation dependent", so this is the form's content, not the designer's layout — no captions, boxes, page geometry or widgets | [`Builder/Import/XfaContent.cs`](src/features/Tetitulz.Pdf.Builder/Import/XfaContent.cs) |
| Template token **and literal** location + erasure + insertion. **Two locators, one match step**: `Replace` scans for `{{token}}`s, `ReplaceText` for literal text, and every judgement about whether a located span can actually be replaced — one content stream, one text object, one baseline — is `TryBuildMatch`'s and is shared; `ReplaceRun`'s runs a reader named may stand in several text objects (a print driver writes a line a word to an object), measured on the page as one line by `TrySpanOnThePage` and drawn after the first. Both keep the guarantee that **everything the call did not replace is where it was** | [`Base/Operations/Documents/PdfTemplateEngine.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfTemplateEngine.cs) |
| **Reflow — the same engine, a different contract.** `PdfTextReflow` re-breaks the containing paragraph, so text the caller did not name **does** move; that is why it is a separate class and not an option, and why `MatchMode` has three values rather than two booleans. What it still guarantees is that **nothing outside the paragraph moves** — a PDF says where text was drawn, not what it belongs to, so the content below a paragraph is related to it by nothing in the file and cannot be pushed down. A replacement needing more lines than the paragraph had is therefore **refused**, and so are centred, right-aligned, justified, rotated, mixed-size and standalone lines, each **by name** through `NotReflowedKeys`. Shrink-to-fit is deliberately **not** applied here: reflow's answer to "too wide" is another line | [`PdfTemplateEngine.Reflow.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfTemplateEngine.Reflow.cs), [`PdfTextReflow.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfTextReflow.cs), [`ReflowLineBreaker.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/ReflowLineBreaker.cs) |
| Single-revision rewrite + page-content substitution, shared by redaction and templating — the two operations that must **destroy** content | [`Base/Operations/Documents/PdfFullRewriter.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfFullRewriter.cs) |
| Search-driven redaction | [`Base/Operations/Documents/PdfTextRedactor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfTextRedactor.cs) |
| In-place image replacement (same object number, signature-preserving) | [`Base/Operations/Images/PdfImageReplacer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Images/PdfImageReplacer.cs) |
| A document's HTJ2K images rewritten as Part 1 JPEG 2000 in place (same object, same dictionary, incremental) | [`Base/Operations/Images/PdfJpxTranscoder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Images/PdfJpxTranscoder.cs) |
| Image → PDF | [`Base/Operations/Images/PdfImageToPdf.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Images/PdfImageToPdf.cs) |
| Revision enumeration / recovery of a superseded state | [`Base/Operations/Documents/PdfRevisions.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfRevisions.cs) |
| The **awaited** form of that read. **An asynchronous seam needs no "not yet" answer** — that state exists only where a call cannot wait, so it becomes an `await` and the interface is just "fetch this range". `ReadPageAsync` **drives the synchronous protocol** (ask, await exactly the ranges `Missing` names, ask again) rather than mirroring it, so there is one decoder and one set of ceilings. It reports on `PdfProgressivePage.Error`, **not** a `Last*` slot, because those are `[ThreadStatic]` and a continuation may resume elsewhere | [`Core/IO/IAsyncByteRangeSource.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/IO/IAsyncByteRangeSource.cs), `PdfProgressivePageReader.ReadPageAsync` |
| Reading **one page** from a file held only in part — the head, the hint stream, the page's run, and the shared-object groups Table F.3 names for the page when its content stream or one of its resources lives there; no cross-reference table. The third answer is the return value and it carries `Missing`, the ranges to fetch before asking again: a refusal a client cannot act on sends it back to downloading the document. **It refuses a file not written for progressive access rather than falling back**, because a silent fallback downloads the file and never says why. Table F.3's content offset decides what to *fetch* and `/Contents` what was *got* — confusing the two returns the file's opening bytes as every page's content, and a test written from the same belief agrees. **The resource check follows four hops from `/Resources`** (the dictionary, a category's entry, its descriptor, its program) under `PdfLimits.MaxProgressiveResourceObjects`; what no range holds after the groups is `UnresolvedResources`, reported, never answered as absent — by Annex F a page's objects are its own or shared, so it is the file's defect. **A resolver that can say *not yet* never caches a miss**: the check asks before the groups arrive and again after, and a remembered null would answer the second ask. Objects come from the fetched bytes via `RawObjectScanner` with a null parser, parsed on demand behind `PdfProgressivePage.Resolve` | [`Base/Operations/Documents/PdfProgressivePageReader.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfProgressivePageReader.cs), [`PdfProgressivePage.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfProgressivePage.cs) |
| Annex F's primary hint stream, **decoded** — the per-page byte range a client fetches, with F.4 NOTE 3's "as if the hint stream were not present" correction applied once at the boundary (**at** the stream's offset, not merely past it). Item groups are byte-aligned, which the specification never says and an independent checker both writes and requires. **Each table must end exactly where the next one's offset declares, and the reader refuses otherwise** — the tables carry no checksum and no length per field, so that is the only way a misread becomes loud rather than plausible. Every width and count is chosen by the file, so `PdfLimits.MaxLinearizationHintFieldBits` / `MaxLinearizationSharedReferences` bound them and a table past either is refused, never clamped | [`Base/Operations/Documents/PdfLinearizationHintReader.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfLinearizationHintReader.cs), [`PdfLinearizationHints.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfLinearizationHints.cs) |
| A file a reader may hold only **part** of — the seam for progressive / HTTP `Range` access. Its whole design is the predicate a `Stream` cannot express: **`TryRead` answering `false` means *not yet*, never end of file; a short read means end of file, never *not yet*; and it must not block**, because a source that fetches inside the call can always answer `true` and has thereby destroyed the distinction its caller implements the interface to get. `PdfRevisions.ReadLinearizationState` is its consumer, and `PdfLinearizationState.NotYetAvailable` states the rule any further three-state answer must follow: it means **more bytes could change the answer**, and is never returned where the answer is settled | [`Core/IO/IByteRangeSource.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/IO/IByteRangeSource.cs), [`StreamByteRangeSource.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/IO/StreamByteRangeSource.cs) |
| Sanitizer, in Core so a signing host reaches it without Base's SkiaSharp and `Plugins.Conformance` without a plugin → plugin edge. **A reachability rewrite**: clean every dictionary reachable from the cleaned catalog + `/Info` in one iterative walk with one document-wide visited set (no recursion, so a deep chain neither overflows the stack nor is silently clipped at a depth cap; arrays in the set, so the walk is linear, not cubic), then write only the objects the cleaned graph still reaches, trailer rebuilt from `/Root` `/Info` `/ID`. Stripping a reference drops its target — a `/JS` stream, an embedded file, and the `/ObjStm` / `/XRef` containers a compressed input is built from all leave the file instead of shipping as untouched orphans (an object-stream leak an independent structure check alone would not show). `IsRemovableAction` is the one action-type decision; new default-on options cover remote GoTo/ImportData/Thread, non-web URI and `/3D` annotations, and `RemoveAllAdditionalActions` (off by default; the conformance converters set it) drops the `/AA` key itself for A04/X07 | [`Core/Document/PdfSanitizer.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfSanitizer.cs), wrapper [`SanitizerPlugin.cs`](src/plugins/Tetitulz.Pdf.Plugins.Maintenance/SanitizerPlugin.cs) |
| Verified write + object index — **public, in Base**, because the Conformance plugin needs them and cannot reference `Accessibility` | [`VerifiedWrite.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/VerifiedWrite.cs), [`PdfObjectIndex.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/PdfObjectIndex.cs) |
| Incremental writer (Base + plugins + Accessibility) | [`Core/Writing/IncrementalUpdateWriter.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Writing/IncrementalUpdateWriter.cs) |
| Name-tree (§7.9.6) and number-tree (§7.9.7) walk — **one** traversal behind two faces, shared by Base and two plugins. §7.9.7 defines a number tree by reference to §7.9.6, so a second copy would differ in one string — and that is where a guard goes missing | [`Core/Document/PdfTreeWalk.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfTreeWalk.cs), [`PdfNameTree.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfNameTree.cs), [`PdfNumberTree.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/PdfNumberTree.cs) |
| Stream filter pipeline | [`Core/IO/Filters/FilterPipeline.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/IO/Filters/FilterPipeline.cs) |
| PDF name cache | [`Core/Objects/PdfName.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Objects/PdfName.cs) |
| Operand coercion | [`Core/Text/ContentOperand.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Text/ContentOperand.cs) |
| Shared spec constants | [`Core/Constants/`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Constants) |
| Document JavaScript — contract + single dispatch point in Base, engine in the plugin (`Jint` is **not** a Base dependency); **enabled only by name**; the object model held to Acrobat's answers | [`IJavaScriptPlugin.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Scripting/IJavaScriptPlugin.cs), [`PluginJavaScriptRunner.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Scripting/PluginJavaScriptRunner.cs), [`PdfJsExecutionContext.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Scripting/PdfJsExecutionContext.cs), [`PdfJsDocumentScope.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Scripting/PdfJsDocumentScope.cs) (one environment for a document's scripts) and [`PdfJsDocumentFacts.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Scripting/PdfJsDocumentFacts.cs) (what the reader hands them: fields, captions, `/Info`, and attachments only when the caller asks, each under a bound); [`src/plugins/Tetitulz.Pdf.Plugins.JavaScript/`](src/plugins/Tetitulz.Pdf.Plugins.JavaScript) — `AcrobatObjectModel.js` is the object model and the `AF*` functions, loaded into every engine over the one bridge `JsHost.cs`, which takes and returns strings, numbers and booleans. `JintJavaScriptPlugin` carries [`PdfPluginExplicitRegistrationAttribute`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Plugins/PdfPluginExplicitRegistrationAttribute.cs): every discovery door passes it over, so referencing the package or discovering plugins for another reason runs no document script; only `PdfPluginHost.Register` does, and the failure is silent. The model is not ISO's — ISO 32000 leaves the objects to Adobe's reference and the `AF*` functions are in no published one — so it is held to Acrobat's own answers, recorded by probe rounds run by hand in Acrobat ([`tests/Tetitulz.Pdf.Plugins.JavaScript.Tests/Fixtures/Acrobat/`](tests/Tetitulz.Pdf.Plugins.JavaScript.Tests/Fixtures/Acrobat/README.md), `AcrobatParityTests`); an answers file is the saved probe document's `results` field byte for byte (`read-results.py` checks it), and every difference the engine keeps is a row of the round's `known-disagreements*.tsv` with its reason, a list that only shrinks (GAPS.md §25a) |
| Public API facade | [`Base/PdfOperations.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/PdfOperations.cs) |
| Conformance validation from the facade — **dispatches via `PdfPluginHost`**, because Base may not reference the plugin that references it | [`PdfOperations.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/PdfOperations.cs) + [`PdfXValidationPlugin.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/PdfXValidationPlugin.cs) |
| Link-annotation extractor | [`Base/Operations/Extraction/PdfLinkExtractor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Extraction/PdfLinkExtractor.cs) |
| Bates numbering — a sequence that crosses documents (`Apply` returns the next number, so the state is the caller's) and a stamp that comes off again. Marked as a **pagination artifact**, unlike `PdfHeaderFooterWriter` | [`Base/Operations/Documents/PdfBatesNumbering.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Documents/PdfBatesNumbering.cs) |
| Merge — page tree, `/AcroForm`, `/Names`, `/Outlines`, the **tag tree**, `/OCProperties`, page labels and output intents. A per-document key (`/ParentTree`, `/StructParents`, a label index) must be shifted and whatever carries it shifted with it | [`Plugins.PageEdits/PdfMerger.cs`](src/plugins/Tetitulz.Pdf.Plugins.PageEdits/PdfMerger.cs) |
| Decryptor | [`Encryption/PdfDecryptor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/PdfDecryptor.cs) |
| Encrypted in-place revision writer — the `EncryptedRevision` overload carries the trailer `/Info`, without which a minted `/Info` is written and unreachable | [`Encryption/PdfEncryptedIncrementalUpdate.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/PdfEncryptedIncrementalUpdate.cs) |
| Encrypted round trip (decrypt → operate → re-encrypt) — opened by a `PdfDecryptionCredential`, re-protected by a policy that **names the output's handler**. `Recipients` takes the certificates from the caller, because a public-key document identifies its recipients without embedding them; `ReuseOpenPassword` is refused for a document that has none, since it would re-encrypt under an empty password and `/V 5 /CFM AESV3` is what both handlers write | [`Encryption/PdfEncryptedRoundTrip.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/PdfEncryptedRoundTrip.cs) |
| Signing an encrypted document in place — the graph from a decrypted copy, the revision appended encrypted, the signer's **plan** crossing between them. `Sign` refuses a wrong password before writing a byte. A **PDF MAC-protected** document is signed through ISO/TS 32004 §6.5.2: the token goes inside the CMS as an unsigned attribute and the standalone seal is suppressed for that revision, because a standalone token would cover the signature's placeholders and the signature's `/ByteRange` would cover the token. `SignaturePlan.CmsPostProcessor` is the seam — `Complete` is the only point holding the byte range and the finished CMS at once, and it is `internal` because a caller who can replace the CMS can replace the signature | [`Encryption/PdfEncryptedSigner.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/PdfEncryptedSigner.cs), [`Signing/SignaturePlan.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/SignaturePlan.cs) |
| What opens an encrypted document — a password, a PKCS#12 store, or a device holding the recipient's key. **A credential says what the caller holds, not which handler the document uses**; the document decides that, so the wrong kind is refused by name. Every write path needs the same two things from it (the file key, and a decrypted copy), once per handler, in the same shapes — three overloads across four entry points would be twelve methods differing in one line | [`Encryption/PdfDecryptionCredential.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/PdfDecryptionCredential.cs) |
| **A PDF MAC token that verifies is not one that covers.** §6.5.1 requires a standalone token's range to reach the end of the file, §6.5.2 the same of a signed revision's signature; Annex B.2.2.2 reads a short range as tampering *or* as a token left stale by an append. **A digest comparison structurally cannot see it** — appending leaves every byte the range names unchanged, so the digest matches and only the extent is wrong. Forwarded to the signature rule rather than restated, down to §12.8.1's optional EOL after `%%EOF` | `StandalonePdfMacSeal.CoversToEndOfFile` → `SignedByteRanges.ReachesEndOfFile` |
| Checking a PDF MAC token, **both placements and both handlers** — the reader beside the writer, so no digest is asserted only against itself. The MAC authenticates the *authenticated attributes*; the payload is tied to them only through `messageDigest` (RFC 5652 §9), so verifying the MAC and then reading `dataDigest` out of the content accepts digests an attacker chose. Three answers, not two: **unprotected** is not **invalid**, and collapsing them reports every pre-2.0 encrypted document as tampered with. **§6.4 keys a token on the file encryption key whichever handler produced it**, so the password, PKCS#12 and `IExternalDecryptor` overloads differ in one resolve delegate and nothing else | [`Encryption/PdfMacVerifier.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/PdfMacVerifier.cs), `PdfMacToken.Verify` |
| Is this a signature dictionary, and which entry §7.6.2 leaves unencrypted | [`Core/Document/SignatureDictionaryShape.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Document/SignatureDictionaryShape.cs) |
| `/Adobe.PubSec`, both directions — and the **only** two files allowed to name BouncyCastle in `Encryption`. The hardware seam beside them names none, which is what makes it implementable by a device | [`Encryption/PdfPublicKeyEncryptor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/PdfPublicKeyEncryptor.cs), [`PdfPublicKeyDecryptor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/PdfPublicKeyDecryptor.cs), [`IExternalDecryptor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Encryption/IExternalDecryptor.cs) |
| CMS signature generate/verify | [`Signing/Cms/CmsSignatureGenerator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Cms/CmsSignatureGenerator.cs) |
| Signature verifier / validator (one engine, one thin adapter) | [`Signing/PdfSignatureVerifier.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/PdfSignatureVerifier.cs), [`PdfSignatureValidator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/PdfSignatureValidator.cs) |
| Verification policy (per-call) | [`Signing/Models/SignatureVerificationOptions.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Models/SignatureVerificationOptions.cs) |
| X.509 chain walking / anchoring | [`Signing/Cms/CertificateChainValidator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Cms/CertificateChainValidator.cs) |
| Signing façade — unsigned-field enumeration, `/DSS` inventory | [`Signing/PdfSigningOperations.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/PdfSigningOperations.cs) |
| Post-hoc LTV refresh | [`Signing/PdfLtvRefresher.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/PdfLtvRefresher.cs) |
| Deferred / two-phase signing — `IExternalSigner` is called *inside* `Sign`, so it cannot reach a signer behind a request boundary. What crosses is **data** (`PreparedSignature`), and `Complete` overwrites the hex digits and **nothing else** | [`Signing/PdfDeferredSigner.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/PdfDeferredSigner.cs), [`PreparedSignature.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/PreparedSignature.cs) |
| CMS around a **raw signature value** (CSC/eIDAS, smart card). A CAdES signature covers the DER of the signed *attributes*, not the document digest — so those bytes cross the wire and **come back**, because they carry a `signingTime` that a second build would not reproduce | [`Signing/Cms/CmsDeferredSignatureBuilder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Cms/CmsDeferredSignatureBuilder.cs) |
| TSA client | [`Signing/Timestamping/TsaClient.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Timestamping/TsaClient.cs) |
| DSS / OCSP / CRL builder | [`Signing/Timestamping/DssBuilder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/Timestamping/DssBuilder.cs) |
| **Visible signature appearance — the text-drawing one.** Lives in **Core**, not beside the signer: `Signing → Base` would put SkiaSharp in every Signing consumer's graph and `Base → Signing` would put BouncyCastle in every Base consumer's, so the shared code goes **down**. `VisualSignatureBuilder` has two appearance shapes and **neither is a special case of the other** | [`Core/Writing/SignatureAppearanceGenerator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Core/Writing/SignatureAppearanceGenerator.cs), [`Signing/VisualSignatureBuilder.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Signing/VisualSignatureBuilder.cs) |
| Accessibility facade (Audit / AutoTag / Remediate) | [`Accessibility/PdfAccessibility.cs`](src/features/Tetitulz.Pdf.Accessibility/PdfAccessibility.cs) + [README](src/features/Tetitulz.Pdf.Accessibility/README.md) |
| Encrypted remediation — the inverted dependency: the delegate a caller wires to `Encryption`, and the revision type it carries | [`EncryptedRevisionWriter.cs`](src/features/Tetitulz.Pdf.Accessibility/EncryptedRevisionWriter.cs), [`PlannedRevision.cs`](src/features/Tetitulz.Pdf.Accessibility/PlannedRevision.cs) |
| Deep PDF/UA checks (UA10–UA24, UA30–UA32) | [`Accessibility/Checking/PdfDeepAccessibilityChecker.cs`](src/features/Tetitulz.Pdf.Accessibility/Checking/PdfDeepAccessibilityChecker.cs) |
| Mostly-dictionary PDF/UA checks (UA00–UA09, UA25–UA29, UA50) | [`Plugins.Conformance/PdfUaChecker.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/PdfUaChecker.cs) |
| AcroForm terminal-field walk shared by checker and fixer | [`Plugins.Conformance/AcroFormFieldWalk.cs`](src/plugins/Tetitulz.Pdf.Plugins.Conformance/AcroFormFieldWalk.cs) |
| Auto-tagger (marked content + struct tree) | [`Accessibility/Tagging/PdfAutoTagger.cs`](src/features/Tetitulz.Pdf.Accessibility/Tagging/PdfAutoTagger.cs) |
| Content-stream scan / splice / page rewrite (owns the 4 MiB per-page tagging ceiling) | [`ContentStreamScanner.cs`](src/features/Tetitulz.Pdf.Accessibility/Internal/ContentStreamScanner.cs), [`ContentStreamTagger.cs`](src/features/Tetitulz.Pdf.Accessibility/Internal/ContentStreamTagger.cs), [`PageContentRewriter.cs`](src/features/Tetitulz.Pdf.Accessibility/Internal/PageContentRewriter.cs) |
| Cycle-safe structure-tree walk — the one traversal, in Base; this feature's view of it as `StructNode`s | [`Base/Operations/Structure/StructureTreeWalk.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Structure/StructureTreeWalk.cs), [`Accessibility/Internal/StructTreeWalker.cs`](src/features/Tetitulz.Pdf.Accessibility/Internal/StructTreeWalker.cs) |
| Accessibility diagnostics sink | [`Accessibility/Internal/AccessibilityDiagnostics.cs`](src/features/Tetitulz.Pdf.Accessibility/Internal/AccessibilityDiagnostics.cs) |
| Structure-tree editors — **seven operations**, each with a `Stream`→`Stream` writer and a `Plan*` for encrypted input | [`Accessibility/Editing/PdfStructureTreeEditor.cs`](src/features/Tetitulz.Pdf.Accessibility/Editing/PdfStructureTreeEditor.cs) |
| Reading-order analysis + single-column-proof repair | [`Accessibility/ReadingOrder/PdfReadingOrderAnalyzer.cs`](src/features/Tetitulz.Pdf.Accessibility/ReadingOrder/PdfReadingOrderAnalyzer.cs) |
| Header/footer → `/Artifact` marker | [`Accessibility/Artifacts/PdfArtifactMarker.cs`](src/features/Tetitulz.Pdf.Accessibility/Artifacts/PdfArtifactMarker.cs) |
| Retroactive font embedding (UA21/UA25) | [`Accessibility/Fonts/PdfFontEmbeddingRemediator.cs`](src/features/Tetitulz.Pdf.Accessibility/Fonts/PdfFontEmbeddingRemediator.cs) |
| PDF/UA XMP identifier writer | [`Accessibility/Metadata/PdfUaXmpWriter.cs`](src/features/Tetitulz.Pdf.Accessibility/Metadata/PdfUaXmpWriter.cs) |
| Markdown → HTML → PDF (inside `HtmlToPdf`, because a `MarkdownToPdf` feature would need a banned feature→feature edge). **No pass reads another's markup**: each image and link is rendered into a placeholder restored after the later passes — a link pass reading the image pass's output finds a `[` inside a generated `alt` and writes its own quotes there, closing the attribute and opening one of the source's (`onerror`). A target holding a placeholder, or a scheme no document link uses (`Core.Writing.LinkTargets.IsSafe`: `javascript:`, `data:`), renders its label alone, and the placeholder mark in the source is replaced (`MarkdownHostileInlineTests`) | [`HtmlToPdf/Markdown/MarkdownToHtml.cs`](src/features/Tetitulz.Pdf.HtmlToPdf/Markdown/MarkdownToHtml.cs) |
| HtmlToPdf's fillable forms — **opt-in, and only through `ConvertHtmlWithFormFields`**: the ordinary entry points cannot turn it on, because a placeholder nothing makes into a field is an empty box. A control maps to a run of **no-break spaces in Courier** — one token, so it cannot wrap, and 0.6 em per character, so its width is exact without the mapper restating the exporter's missing-glyph rule. Nothing inherited may change that width (letter-spacing, caps, super/subscript) or draw on it (underline, a link). Names: `name`, else `id`, else the kind; a period becomes an underscore and a repeat gets a suffix, both reported. **Radios sharing a `name` are one group** — the first claims the field name (so a text field already called `size` pushes the group to `size_2`), a value that repeats or is `Off` gets a suffix, and the last `checked` wins; the plugin gathers a group's placements into one `FormRadioGroupField`, locked only when *every* button is disabled | [`HtmlToBuilderMapper.FormControls.cs`](src/features/Tetitulz.Pdf.HtmlToPdf/HtmlToBuilderMapper.FormControls.cs), [`HtmlFillableForms.cs`](src/plugins/Tetitulz.Pdf.Plugins.Html/HtmlFillableForms.cs) |
| HtmlToPdf's remote subresources — **one path, `LoadRemote`, for all four kinds** (image, background, stylesheet, `@font-face`), carrying the per-conversion cache, the `MaxRemoteResources` budget and the reason text; a stylesheet's relative `url()`s — fetched **or local** — are rebased onto **its own location** before its rules are extracted, because nothing downstream knows which sheet a declaration came from (a local sheet's become `file:` URIs, which still reach the disk only through `ResolveLocalPath`'s containment check). **A fetched sheet names no local file by any route**: a `url()` of its that is not `http(s)` or `data:` — a `file:` URL, a drive path `Uri` reads as one — becomes one that names nothing, and its `@import` of anything not remote is declined, since either would be read from inside `BasePath` (`AdversarialRemoteStylesheet_NamingALocalFile_ReadsNothing`). **The `url()` values are found by a scan, as the readers find them** (`RebaseRemoteCssUrls`: comments stripped, the closing parenthesis by `ClosingParen`, unquoted and trimmed) — a pattern missed a space inside the quotes, a comment before the parenthesis and a `url()` held in a custom property, and a sheet it timed out on came back whole; a `var()`, `env()` or `attr()` inside `url()` names nothing. No loader means no network I/O, and the long-standing "declined" report | [`HtmlToBuilderMapper.Remote.cs`](src/features/Tetitulz.Pdf.HtmlToPdf/HtmlToBuilderMapper.Remote.cs), [`IRemoteResourceLoader.cs`](src/features/Tetitulz.Pdf.HtmlToPdf/IRemoteResourceLoader.cs), [`GuardedRemoteResourceLoader.cs`](src/features/Tetitulz.Pdf.HtmlToPdf/GuardedRemoteResourceLoader.cs) |
| Build-failing test guards (banned patterns 1–3, `PdfName`, CMYK, layering, doc references, API baselines, **exception hierarchy**) + their shared scanner | [`tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/`](tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/TestSourceScanner.cs) |
| Visible-skip attributes — corpus, host fonts, repo sources (never an early `return`) | [`CorpusFactAttribute.cs`](tests/Tetitulz.Pdf.TestHelpers/CorpusFactAttribute.cs) (shared by every test project; adds `Category=Corpus`), [`SystemFontFactAttribute.cs`](tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/SystemFontFactAttribute.cs), [`RepoSourceFactAttribute.cs`](tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/RepoSourceFactAttribute.cs) |
| Corpus smoke tests — each correlates output against an independent second opinion | [`Rendering/CorpusRenderSmokeTests.cs`](tests/Tetitulz.Pdf.Base.Tests/Rendering/CorpusRenderSmokeTests.cs), [`CorpusExtractionSmokeTests.cs`](tests/Tetitulz.Pdf.Base.Tests/CorpusExtractionSmokeTests.cs), [`CorpusFormSmokeTests.cs`](tests/Tetitulz.Pdf.Base.Tests/CorpusFormSmokeTests.cs) |
| Raster/SVG parity suites | [`Rendering/SvgRasterParityTests.cs`](tests/Tetitulz.Pdf.Base.Tests/Rendering/SvgRasterParityTests.cs), [`Rendering/RenderGeometryParityTests.cs`](tests/Tetitulz.Pdf.Base.Tests/Rendering/RenderGeometryParityTests.cs) |
| API baselines (one per packable project, compared by that project's own `PublicApiSurfaceTests`) | [`tests/ApiSnapshots/`](tests/ApiSnapshots) |
| Performance harness + its fixture verification | the developer-local BenchmarkDotNet project (`Tetitulz.Pdf.Benchmarks`) |
| Invoice extraction — label → value fields (`PdfFieldExtractor`: labels matched in logical order — a right-to-left line under its own direction, any other by most of its strong characters, a tie by its first (the extractor's flag counts digits: `עוסק מורשה / ח.פ.: 514351287` is left to right to it), and that direction (`KeyedLine.Rtl`) decides the value's side and order; a value on the label's line (its value side first), then a whole-word match, then the most specific line wins, then reading order; a value ends at the next label or a column gap; the rest of a word the label ends inside is the value; every occurrence, exclusion, run step, word rest and neighbour search charged to `PdfLimits.MaxFieldExtractionWork`), the Israeli preset and its validation, invoices as tables. **The preset's label lists come from vendors' published samples and the Tax Authority's specification, checked against fixtures, not against a corpus of real invoices** — an outside producer's synthetic invoices show gaps (GAPS.md §4a); change them through `IsraeliInvoiceExtractor.CreateQueries`, never by teaching `PdfFieldExtractor` a schema; the number labels are built from the title table, so add a title there, not a label beside it | [`Base/Operations/Forms/PdfFieldExtractor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/PdfFieldExtractor.cs), [`IsraeliInvoiceExtractor.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/IsraeliInvoiceExtractor.cs), [`IsraeliInvoiceValidator.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/IsraeliInvoiceValidator.cs), [`InvoiceDataTable.cs`](src/tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/InvoiceDataTable.cs) |
| Open gaps | `GAPS.md` (developer-local) |

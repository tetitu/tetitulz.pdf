# Tetitulz.Pdf.Plugins.PostScript

A pure-managed PostScript interpreter. It converts embedded PostScript to a **PDF content stream**,
which the library's renderers then execute through their form-XObject path — so the same output
reaches both the raster renderer and the SVG exporter, and stays vector.

No external interpreter, no native dependency, no `Tetitulz.Pdf.Base` reference.

## What it serves

| Surface | Contract | Registration |
|---|---|---|
| `/Subtype /PS` XObjects (ISO 32000-1 §8.10.7) | `IPostScriptPlugin` | `PostScriptXObjectPlugin` |
| The legacy inline `(…) PS` operator | `IPostScriptPlugin` | `PostScriptXObjectPlugin` |
| `.ps` files → PDF | `IImportPlugin` (`"ps"`) | `PostScriptImportPlugin` |
| `.eps` files → PDF | `IImportPlugin` (`"eps"`) | `EpsImportPlugin` |

```csharp
PdfPluginHost.Instance.Register(new PostScriptXObjectPlugin());
PdfPluginHost.Instance.Register(new EpsImportPlugin());

// Now a /Subtype /PS XObject draws instead of showing a placeholder:
byte[] png = PdfOperations.RenderPageToPng(path, pageIndex: 0, dpi: 150);

// …and an EPS converts to PDF:
PdfOperations.Import(epsStream, pdfStream, "eps");
```

**Registration is the opt-in.** With no plugin registered the library's behaviour is exactly what it
was: the XObject draws a hatched placeholder and the inline operator is a no-op.

## What converts

- **Paths** — `moveto`/`lineto`/`curveto` and their relative forms, `arc`/`arcn`/`arct`/`arcto`
  (flattened to cubic Béziers, resolution-independent), `closepath`, `pathbbox`, `reversepath`,
  `clippath`, `strokepath`.
- **Painting** — `fill`, `eofill`, `stroke`, `clip`, `eoclip`, `initclip`, and the Level 2
  `rectfill` / `rectstroke` / `rectclip` (four numbers or an array of 4n; `rectstroke`'s optional
  matrix transforms the pen).
- **VM** — `restore` rolls local VM back to its `save` (PLRM §3.7.3): an array or dictionary written
  since is put back as it was, copied on its first change at that level (as interpreters
  does), one made since the `save` not copied at all; strings are not restored, as the PLRM says, and
  global VM — `systemdict`, `globaldict` — is untouched. The copies are charged to the allocation
  budget at the price of the objects they copy.
- **Resources** (PLRM §3.9) — `defineresource`, `undefineresource`, `findresource` (`undefinedresource`
  on a miss), `resourcestatus` and `resourceforall` over every category, implicit ones included; the
  `Font` category is `definefont` and `findfont` under another name.
- **Graphics state** — `gsave`/`grestore`/`grestoreall`, `save`/`restore`, `initgraphics`. What
  `gsave` keeps includes the **current path, its current point and the font** (PLRM §4.2), so a
  `grestore` puts back the path and the font the bracket began with. `gstate`, `currentgstate` and
  `setgstate` carry a graphics state as an object (`gstatetype`), path, clip and clipping path stack
  included.
- **Clipping that widens** — `clipsave`/`cliprestore` (with an empty stack, the region the innermost
  `gsave` saved), `initclip` and `setgstate`. PDF can narrow a
  clip and never widen one — the only way back is a `Q` past the `q` it was taken under — so each state
  carries its region as the list of clips it took, each path in device space, and the emitter tracks the
  region and the transform its open levels hold. A widening closes levels until the region in force is
  one the wanted region extends (each `Q` rolling back colour, line style, transform and clip, all
  tracked), writes the transform's difference as one `cm`, and takes the rest of the wanted clips again
  under a bracket of their own, charged by their segments; a `grestore` to a level a widening closed
  does the same. `gsave` levels survive `initclip`.
- **Forms** — `execform` becomes a PDF form XObject drawn with `Do`, its `/Matrix` and `/BBox` carried;
  the `/PaintProc` runs once, at the form's first use, and its stream inherits the state it is drawn in.
- **The page device** — `setpagedevice`'s `/PageSize` is the page's medium (its `/MediaBox`, ahead of
  a `%%BoundingBox` and the drawn extent, and what `clippath` answers), and `currentpagedevice`
  answers it; its other requests describe a printer and are ignored.
- **Transformations** — the whole family, including `setmatrix`, `concatmatrix`, `invertmatrix`,
  `transform`/`dtransform`/`itransform`/`idtransform`.
- **Colour** — `setgray`, `setrgbcolor`, `setcmykcolor`, `sethsbcolor` and the `current*` forms.
  CMYK stays CMYK; it is not converted through sRGB. In Level 2 spaces (`setcolorspace`, `setcolor`):
  `/ICCBased` is written as an ICC stream, its component count read from the profile's header (data
  that is no profile, or an `/N` the profile disagrees with, is refused); `/CalGray`, `/CalRGB` and `/Lab` travel as PDF's;
  and `/CIEBasedA`, `ABC`, `DEF` and `DEFG` — which PDF has no form of — are run through their decode
  procedures (each sampled at 256 points, a `DEF`/`DEFG` table looked up multilinearly) to XYZ and
  carried as `/Lab` against the space's white point, colours, images and indexed palettes alike. At most
  `PsLimits.MaxColorSpaceResources` such spaces a conversion.
- **Transfer functions** — `settransfer` and `setcolortransfer` (and their `current*` forms) become an
  ExtGState `/TR`, each procedure sampled at 256 points into a FunctionType 0 and cached; an empty
  procedure is the identity, written as `/Identity` where another transfer was in force. At most
  `PsLimits.MaxTransferFunctions` a conversion.
- **Device parameters** — `setoverprint` becomes `/OP` and `/op` (PostScript has one parameter for
  both), `setblackgeneration` and `setundercolorremoval` a sampled `/BG` and `/UCR` (the latter over
  −1 to 1, ISO 32000-1 §10.3.4; an empty procedure is the identity, a function, not the device's own),
  and `setflat`, `setsmoothness` and `setstrokeadjust` `/FL`, `/SM` and `/SA`. Each is written with
  `gs` as it is set, a state holding only what changed (`/BG2` or `/UCR2 /Default` where a
  `setgstate` returns to the device's own), rolled back by `grestore`; every `current*` form answers
  what was set. A press-faithful reader — this library's raster renderer, simulating overprint or
  separating the page — then sees an overprinted black as the press would. At most
  `PsLimits.MaxDeviceParameterStates` such states a stream.
- **Colour readbacks** — `currentcolorspace` answers the array `setcolorspace` was given (a family
  name alone in an array of one, the `[/Pattern …]` `setpattern` made, or the device family after
  `setgray` and its kin), and `currentcolor` the operands the colour was set with: a tint, an index,
  an uncoloured pattern's components and then the pattern — not the alternate colour painted.
- **Line style** — width, cap, join, miter limit, dash.
- **Text** — `findfont`/`scalefont`/`makefont`/`setfont`, `show`/`ashow`/`widthshow`/`awidthshow`,
  and `stringwidth` with **real AFM metrics**. Fonts map onto the PDF standard 14, unless the document
  ships its own Type 1 or Type 42 program (see **Embedded fonts**).
- **Type 3 (user-defined) fonts** — the glyph procedures are **run**, and what they draw becomes the
  emitted font's `/CharProcs`. `setcachedevice` and `setcharwidth` become PDF's `d1` and `d0`, which
  is a real distinction: `d1` means the glyph describes shape only and paints in the text fill
  colour, `d0` that it carries its own. See **Type 3 fonts** below for the constraint that shapes
  the implementation.
- **Images** — `image`, `imagemask`, `colorimage` with a string data source, **and the Level 2
  dictionary form** `<< /ImageType 1 … >> image` (`/Width` `/Height` `/BitsPerComponent` `/Decode`
  `/ImageMatrix` `/DataSource` `/ImageMask`). All four operand shapes converge on one draw path, so
  they cannot drift. `imagemask`'s polarity operand becomes the PDF `/Decode` array: `true` means
  sample 0 paints, which is PDF's default, and `false` needs `/Decode [1 0]`. Dropping the operand
  does not "default" — it inverts every mask written the second way round.
  **The current colour space is honoured** (Level 2): `/DeviceGray`, `/DeviceRGB`, `/DeviceCMYK` and
  `[/Indexed base hival lookup]` — the palette included — reach the emitted image's `/ColorSpace`.
  A one-ink `/Separation` image of at most 8 bits a sample travels as an `/Indexed` image over its
  device alternate, each tint (its `/Decode` folded in) run through the tint transform once, reported —
  exact at every sample value. Any other separation image (16-bit, or a JPEG, whose lossy samples are
  no palette indices) and every `/DeviceN` image travels **in its own space**,
  `[/Separation name alternate f]` or `[/DeviceN [names] alternate f]`, its samples and `/Decode` as
  they are and `f` the tint transform sampled into a FunctionType 0 — 256 tints for one ink, a grid
  within `PsLimits.MaxTintTransformSamples` for several (64 × 64 for two) — so the plates are written,
  reported once. `/ICCBased`, `/Lab` and the Cal spaces reach the image's `/ColorSpace`, and a
  `/CIEBased*` image's samples are converted to `/Lab` (a JPEG or a `/MaskColor` image in one is
  refused, their samples not being the image's). A space that cannot be carried (`/Pattern`, a
  separation over a non-device alternate, a transform that gives no answer) makes the
  image **refused and reported**, its inline data consumed, not drawn as grey: emitting DeviceGray for a Separation image
  paints tint values as grey levels *and* sizes the sample buffer to a quarter of the payload, so
  it would then report only "the remainder was padded" — the wrong picture behind a diagnostic
  naming the wrong cause.
  > **`setgray`, `setrgbcolor` and `setcmykcolor` set the colour space (PLRM §4.8.2), and the
  > dictionary form of `image` paints in it** — a print driver's `0 0 0 setrgbcolor` before its
  > RGB picture's image dictionary. **The operand forms ignore them**: PLRM §8.2 has the five-operand
  > `image` render DeviceGray "regardless of the current color space", so `1 0 0 setrgbcolor … <16
  > bytes> image` stays a 16-byte grey image rather than demanding 48 — and so does a space
  > `setcolorspace` named. Only the dictionary form paints in the current space.
  **Masked images** (PLRM §4.10.6). `/ImageType 4`'s `/MaskColor` — a value or a range a component,
  each held to the samples' range — becomes the image's colour-key `/Mask` array, both sides comparing
  samples before `/Decode`. `/ImageType 3`'s mask becomes the image's stencil `/Mask` image, its
  `/Decode` kept (`imagemask`'s reading is PDF's stencil reading: the image is painted where the stencil
  would paint) and mapped onto the data's rectangle, its own `/ImageMatrix` unread, as other
  interpreters do. `/InterleaveType 1` takes each pixel's first sample as the mask's (any value but 0 sets
  the bit), `2` takes whole rows in the usual plane order (the
  data never ahead of the mask), and `3` gathers the mask's own source before the data's — procedures
  in turn, mask first; a JPEG is allowed for the data's one source alone. The checks
  (`gx_begin_image3`: under 1 the mask is the data's size and depth, under 2 and 3 one bit deep, under 2
  one height a multiple of the other) are `rangecheck`s, raised after every inline block is consumed.
  `imagemask` takes `/ImageType 1` alone; any other type is reported.
- **Smooth shading** — `dict shfill` (Level 3) becomes PDF's `/ShN sh` under the same transform,
  painting the current clip. The two shading dictionaries agree key for key, and so do their function
  dictionaries: all seven `/ShadingType`s, FunctionTypes 0 (its `/DataSource` string becoming the
  stream), 2 and 3 (at most `PsLimits.MaxShadingFunctions` dictionaries a shading, nesting counted).
  A mesh given as an array of numbers is packed — 8-bit flags, 32-bit coordinates, 16-bit colour, a
  `/Decode` spanning the values — and one given as a string travels as it is. A `/Separation` or
  `/DeviceN` shading is **written in its own space**, its tint transform sampled as an image's is, so the
  plates exist. A mesh's or a sampled function's `/DataSource` may be a file `currentfile` made: its
  inline block is read, ending where an image's does, so the program resumes after it; a JPEG is no
  such data. A reusable stream is read whole from position 0 each time, wherever the program left
  it (PLRM §3.10.1, §4.9.3). Skipped and reported: a space the image path cannot carry, a procedure data source, and
  an entry of the wrong shape, every file block the shading and its functions name consumed; a
  non-dictionary operand is a `typecheck`. At most `PsLimits.MaxShadings` `shfill`s a conversion, the refused counted.
  `/Background` is not carried — `shfill` ignores it, and so does `sh`.
- **Patterns** — `makepattern` and `setpattern` (and `setcolor` in a `/Pattern` or `[/Pattern base]`
  space) become PDF patterns painted with `/Pattern cs /Pn scn`. A tiling pattern's `/PaintProc` runs
  once, at `makepattern`, with the pattern on the operand stack, in a child interpreter over its own
  emitter and resources, and its marks become the pattern cell; the cell starts in the line style and
  colour at `makepattern`, and an uncoloured one (`/PaintType 2`) writes no colour — it is painted in the
  colour `setpattern` names over the current space, through a `[/Pattern base]` resource. A shading
  pattern carries its `/Shading` as `shfill` does. The pattern matrix is the program's under the CTM at
  `makepattern`. A malformed dictionary raises PLRM's error; a pattern this converter cannot build
  (a refused shading, a singular matrix, past `PsLimits.MaxPatterns` or
  `MaxPatternNestingDepth`) leaves the colour as it was, said once. `setpattern` leaves the colour
  space a Level 2 `image` is read in, so an image after it and a `setgray` is drawn, not refused.
- **The language** — the full operand/dictionary/execution machinery: procedures, `def`/`load`/
  `where`/`known`, arrays, strings, dictionaries, `for`/`repeat`/`loop`/`forall`/`exit`,
  `stopped`/`stop`, the arithmetic, relational, bitwise, stack and type operators.
- **The syntax** — literal strings with the full escape set, octal escapes, line continuations and
  nested parentheses; hex strings `<…>`; **ASCII base-85 strings `<~…~>`**; radix numbers (`16#FF`);
  exponents; comments and DSC; `//name`, the value the name has when it is scanned (PLRM §3.12.2) —
  what `/moveto { … //moveto } def` relies on to redefine an operator through itself.
- **`systemdict` names**, not just operators — `StandardEncoding` and `ISOLatin1Encoding` (the
  Appendix E vectors, as real arrays), `FontDirectory` / `GlobalFontDirectory` (kept current by
  `definefont`; one dictionary, because fonts are not split between local and global VM here), `systemdict` / `userdict` / `globaldict`,
  `errordict` and `$error`.
- **User objects** — `defineuserobject` / `undefineuserobject` / `execuserobject`, the last running
  a stored procedure through the same entry `exec` uses, so it pushes a frame rather than recursing.
- **Procedures per item** — `pathforall` (each segment of the path as it stands, in the current user
  space), `kshow` (each character shown, the procedure run between each pair with their two codes —
  a composite font's string is shown whole, said) and `cshow` (each character's code, a composite
  font's split by its CMap, and its advance; nothing shown — in a composite font the code's last byte,
  and a `show` or `charpath` of that one byte in the same font shows the code the CMap selected, any
  other string a `rangecheck`, as PLRM §8.2 says; a print driver places each glyph so). Each is a loop context for `exit`, and
  the procedure sees and leaves the one operand stack (see **Type 3 fonts run in a child
  interpreter** for how).

### `strokepath` is flagged, not built

`strokepath` marks the path as standing for the outline of its own stroke and never constructs that
outline. PDF already has an operator that paints exactly that region from exactly this path — `S` —
so `strokepath fill` emits a stroke, which is not an approximation of what it means but a
byte-for-byte match, dash pattern and line style included. Where the flag buys nothing it says so:
`strokepath clip` has no PDF expression and is skipped (clipping to the un-outlined path would hide
everything after it), `pathbbox` pads by half the line width, and extending the path afterwards
drops the flag. All four are reported.

### `charpath` appends outlines

`charpath` appends the glyphs' outlines where this side has them, laid through the font matrix at the
pen, the pen advancing as `show`'s does and the current point left after the last glyph:

- **An embedded Type 1 program** — its own outlines and widths (see **Embedded fonts**).
- **A CFF font a font set carried** — each glyph's charstring run by the renderers' Type 2 evaluator,
  which is Core's (`CffFontSet.Outline`), the glyph named as `show` names it; a CID-keyed one's glyph
  for each code's CID under its CMap (`CidOutline`), CID 0's for a CID it lacks. Each glyph is run once,
  on the font's own charstring budget; a glyph that budget stops short, like one the font lacks, takes
  the `.notdef`'s (CID 0's) outline, or its metric box when that has none.
- **A standard face** — the outlines of the face its text is drawn with: the host's Arial, Times New
  Roman or Courier New, or their metric-compatible Liberation substitutes, found by Core's
  `SystemFontResolver`, or the program `PostScriptConversionOptions.OutlineFontProvider` supplies for
  the standard-14 name (a host that wants the same answer on every machine supplies Liberation, which
  `Plugins.Fonts` carries). Core's `TrueTypeOutlines` reads the face's `glyf` — quadratics raised to the
  equivalent cubics, composites placed and transformed, bounded by `PdfLimits.MaxCompositeGlyphDepth` and
  `MaxGlyphOutlinePoints` — each code's glyph found by its character through the face's `cmap`. The pen
  advances by the AFM width, which is what `show` advances by and a reader draws with; the face is
  metric-compatible, so the outline fits it. A code the face lacks keeps its own metric box.
  Symbol and ZapfDingbats draw a code through their built-in encoding (§9.6.6.4), so the face is asked
  for the character the code's glyph name stands for — Symbol's `alpha` is U+03B1, ZapfDingbats' `a1`
  U+2701 — or a re-encoding's name, and failing that a symbol font's (3,0) `cmap` entry at U+F000 + code,
  which is how Windows' `symbol.ttf` maps them.
- **A Type 3 font** — the paths each glyph's procedure paints (PLRM §5.7), read back from the glyph's
  built content stream: what it fills or strokes, not what it clips, through the glyph matrix and the
  font matrix, the pen advancing by its width as `show`'s does.
- **A composite font** through a predefined CMap (horizontal) — each code's CID through the CMap, the
  CID's character through the character collection's Unicode map (Core's `CidCollectionText`, read off
  the Unicode CMaps `Plugins.CMap` registers; the one text extraction reads), and that character's
  outline in the face the provider supplies for the composite font's face name, else the host face of
  that name or one that covers the string's characters (`SystemFontResolver`). The pen advances an em a
  code, as `show`'s does (`/DW 1000`).
- **Anything else** — vertical composite text, a code no registered map or face carries, a Type 3 glyph
  that paints only an image or text, a code the face has no glyph for, a standard face no outline font
  can be had for — appends the text's *metric box*: the advance along the baseline, the engine's ascender and descender across it. Close enough for
  a clip, which is what programs mostly use `charpath` for, and reported once per conversion with how
  many characters it covered.

A font matrix's translation (PLRM §5.3) moves the outlines and the box as it moves `show`'s text — every
text operator's `Tm` takes it — and the advance, a difference, takes none. Every glyph is charged by
its segments as well as by the string, so `charpath` of a long string in an outline font spends the
budget its path costs.

### Type 3 fonts run in a child interpreter, and must

An operator here **cannot** run a procedure and observe its result within its own invocation:
`DrainExecStack` has exactly one call site, inside `Run`. An operator that calls a procedure *per
item* and needs nothing back — `pathforall`, `kshow`, `cshow` — does it through the step loop
instead: one `PsFrameKind.Callback` frame whose `Advance` does the operator's part of each item
(pushing a segment, showing or measuring a character) and names the procedure to run, as `forall`
names its body (`PsCallbackOps`). A glyph needs its procedure's *result* — the marks it drew, the
width it declared — before `show` can go on, so a glyph procedure runs in a second `PsInterpreter`
with its own operand and exec stacks — which is what stops a glyph's `stop` unwinding into the outer
program's `stopped` — **sharing** the parent's `WorkBudget`, allocation budget and dictionary stack.

> **Sharing the budget is load-bearing, not tidiness.** A child built through the ordinary
> constructor gets a *fresh* `WorkBudget`: twenty million steps per glyph, times a thousand glyphs,
> is a straight bypass of `MaxTotalOps`. One test exists solely to detect that, by setting a low
> `MaxTotalOps` and asserting a drawing *after* a looping glyph never reaches the stream.

Three things about it are worth knowing before changing it:

- **The font's `/FontMatrix` collides with the interpreter's size matrix** — both are keyed literally
  `"FontMatrix"`. `definefont` parks the glyph matrix under a private key *before* resetting the size
  slot; resetting first loses it, and the value read back later is the point size. That produced a
  document at 1/1000 scale, and then, once half-fixed, a `/FontMatrix` of 12. A Type 1 program's own
  font dictionary has the same collision, and `definefont` takes its glyph matrix into the size
  matrix's thousandths once, where the program is attached — a re-encoded or scaled copy already names
  its program and is not taken twice (see **Embedded fonts**).
- **The font state travels to the child.** PLRM keeps the font in the graphics state, and without it
  `MaxType3NestingDepth` is *unreachable* — a glyph showing text in its own font could not recurse,
  so the guard could never fire. A guard that cannot fire reads as protection that was never tested.
- **The emitted encoding carries no `/BaseEncoding`.** Both renderers fall back to it for a code the
  `/Differences` omits, so declaring WinAnsi would draw a notdef box where nothing belongs.

`/Widths` are in **glyph space** — the raw `wx`, not thousandths; both renderers multiply by
`/FontMatrix`[0] themselves. Page-level operators (`showpage`, `initclip`, `erasepage`) are refused
inside a glyph build and reported: the child engine has no page sink, so `showpage` would take the
fragment-mode reset branch and discard the glyph mid-build. Bounded by `MaxType3NestingDepth`,
`MaxType3Glyphs`, `MaxType3GlyphContentBytes` and `MaxType3TotalGlyphBytes`, the last aggregate
because per-item caps do not compose.

### `currentfile … filter` reads the program's own bytes

This is how an EPS embeds image data:

```postscript
4 4 8 [4 0 0 -4 0 4] currentfile /ASCIIHexDecode filter image
00112233445566778899AABBCCDDEEFF>
```

`currentfile` pushes a real file object (PLRM `filetype`), `filter` wraps one, and `image` reads the
inline block through it. **It works only because execution is streamed** — the facade pulls one
top-level object at a time from the parser, so the block begins exactly where the scanner is when
the *consumer* pulls. Pre-tokenising the program would put the scanner at
end of input before the first object runs, with the image data already shredded into garbage names.

> **This is why the `eexec` pattern does not transfer.** `eexec`'s bytes begin immediately after its
> own token, so the scanner can capture them on the way past. A filter's begin after the **`image`**
> operator, and a procedure-invoked source interleaves in a way no scan-time rule gets right.

The other half of the EPS idiom is the **procedure data source**:
`{ currentfile picstr readhexstring pop } image`. A procedure source — and one source per component,
`colorimage`'s `multi` or `/MultipleDataSources` — is gathered before the image is drawn: each
procedure run through the step loop round the sources in turn until its share is full or it answers an
empty string, every answer copied as it comes (a read answers the same buffer each time), the planes
interleaved sample by sample. `read`, `readstring`, `readhexstring` and `readline` read a file
`currentfile` made — the bytes after the operator's one delimiter (CR LF counting as one), hex digit
pairs skipping everything else, a line without its end; a filtered file decodes its block at the first
read and serves the rest from it. `token` scans one object from a string — answering the rest after
the object and its one delimiter — or from such a file, as the program itself is scanned: a procedure
whole, a `//name` looked up; anything else is a `typecheck`.

**A filter's data source may be a string or a procedure** (PLRM §3.13.1), not only a file: a string's
bytes decoded once; a procedure called for each piece, an empty string its end — how the Microsoft PS
Class Driver feeds a picture, `{ gImgDict /data get … get } /FlateDecode filter`, its procedure
cycling through the strings it was given and never answering empty. An image gathers such a filter
through the step loop like a procedure source, decoding the chain each time the raw bytes have
doubled (linear in them) until the samples are all there; the allocation budget ends a procedure that
never fills them, and a stream's own truncation is said only when the image comes up short. A
`/DCTDecode` chain over a procedure — the driver's pictures, `{ … } /DCTDecode filter` — ends at the
JPEG's EOI marker, its codestream carried as the image's `/Filter` as an inline JPEG is; data that does
not begin with the SOI marker ends at its first two bytes, and a JPEG that ends before its EOI draws
nothing — both said. Any reader but an image is refused by name.

**The innermost filter decides where the block ends**: `>` for `ASCIIHexDecode`, `~>` for
`ASCII85Decode`. `FlateDecode`, `LZWDecode`, `RunLengthDecode` and `DCTDecode` have no marker in
the byte stream — how a binary EPS embeds its images — so their block ends where the data itself
says: RunLength's EOD byte, LZW's EOD code, a zlib stream's final block and Adler-32 trailer, a
JPEG's EOI. For the compressed ones that end is known only by decoding, so Core's filters decode
the block and report where it ended in one pass (`RunLengthDecodeFilter.DecodeEmbedded`,
`LzwDecodeFilter.DecodeEmbedded`, `FlateDecodeFilter.DecodeEmbedded`, `DctDecodeFilter.FindEnd`),
and `PsScanner.TakeInlineBlock` lands the cursor **exactly** on the byte after it — never an
estimate, because a wrong extent feeds the rest of the program into the image and loses it from the
program at once. A chain ending in `DCTDecode` is **carried**, not decoded: the image is the JPEG
codestream under `/Filter /DCTDecode`, as a PDF writer embeds a JPEG — Photoshop's
`currentfile /ASCII85Decode filter /DCTDecode filter` included. `CCITTFaxDecode`'s end is a bit pattern
inside the coded rows — past the last of `/Rows` rows, or Group 4's EOFB or Group 3's RTC — which Core's
decoder reports as it decodes (`CcittFaxDecodeFilter.DecodeEmbedded`, the filter's parameter
dictionary carried from `filter`'s operands); where the data does not say — no `/Rows` and no end code
in reach — the block is refused, as an unterminated Flate stream is. Decoding goes through Core's own
filter classes, never a copy.

**`SubFileDecode` ends where its count or its string says** (PLRM §3.8.4):
`source EODCount EODString /SubFileDecode filter`, or the two in a
dictionary below the name. With a string, the data is everything before its occurrence after
`EODCount` others — those passed through as data — and the string itself is consumed, so the program
resumes after it; with none, `EODCount` is a byte count, 0 meaning the rest of the source. The string
is found by Knuth–Morris–Pratt, linear in the data whatever the string. Over another filter's output a
sub-file passes what its count or string allows of that output.

**`ReusableStreamDecode` reads its source whole when it is made** — the program's bytes through the
dictionary's `/Filter` and `/DecodeParms` first, which is how InDesign and Illustrator delimit an
image's or a pattern's data (`currentfile << /Filter /SubFileDecode … >> /ReusableStreamDecode filter`,
the data, then `def`) — or a string as it is. The data is held, charged to the allocation budget,
and read from where the last read left it: `resetfile` and `setfileposition` move about in it and
`fileposition` answers where it stands. A second `image` of the same stream without a `resetfile`
finds its end: PLRM §4.10.2 has `image` read a file "as necessary", from where it stands. A mesh's
or a sampled function's data source is the exception PLRM makes — read from position 0 (above).

Every path that *declines* an image still consumes the block — otherwise one option flag, or the
257th image, leaves image data to be tokenised as PostScript. A block the data never ends (a Flate
stream cut before its final block), or one that cannot be decoded, is consumed as far as a binary
block may reach (`PsLimits.MaxBinarySkipBytes`) and reported, as an unterminated `~>` block is: the
image is skipped rather than drawn from whatever was read. A filter whose end is not found —
`JBIG2Decode`, whose segments do not say where the block ends — is refused by name: it has no end to
consume up to, so its data scans as names after the refusal, each a typed `undefined`, deduplicated and
capped in the diagnostics.

### `clippath` is exact for a rectangle, a bounding box otherwise

The engine tracks the clip as four corner points in device space, so a single straight-edged
four-point clip comes back exactly — rotation included, since a quadrilateral survives any affine
map — and in whichever user space is current when it is read. A curved clip, a many-sided one, or
the intersection of two degrades to the device-space bounding box, which is an over-estimate and is
reported. **`clippath pathbbox`, which is what the operator is nearly always for, is exact either
way**: a bounding box is what it asks for. With nothing clipped the answer is the document's
declared `%%BoundingBox`, or US Letter when it declared none — reported, because there is no device
page here to have a real one.

## What does not — and how you find out

Nothing is skipped quietly. Every unimplemented operator is recorded in
`PostScriptConversionResult.UnimplementedOperators`, and every tolerated failure in `LastError` /
`DiagnosticCount`. `IsClean` is true only when nothing was recovered from.

> **That list carries two kinds of entry.**
> Most entries are operators `PsSystemDict`'s arity table recognises and declines — their operands
> are consumed, so the rest of the object runs. The rest are **undefined names**, which is the worse
> case: the operands were *not* consumed, so the object was abandoned. Until that date an undefined
> name recorded **nothing at all**, so a name nobody had thought of yet — `clippath`, `strokepath`,
> `ISOLatin1Encoding` — left the list empty while `IsClean` went false, and the documented channel
> could not say what was missing. Names probed with `where` / `known`, and names raised inside
> `{ … } stopped`, are still never listed: a probe answering "no" is the mechanism working.

- **`charpath` outlines for vertical composite text** — its pen runs down the column, and the box stays,
  said once per conversion. Horizontal composite text gives real outlines through the host's face (see
  **`charpath` appends outlines**).
- **Halftones** — `setscreen`, `setcolorscreen`, `sethalftone` and friends are parsed, their operands
  consumed, and ignored: a halftone is a device's, and PDF's `/HT` is one most readers ignore.
- **Some font sets, and what a PDF CMap cannot hold** — a CMap the program builds is read, but its
  mappings for a font other than font 0, its base-font mappings (`beginbfchar`, `beginbfrange`), a
  font matrix and a rearranged font are consumed and said: a PDF Type 0 font has one descendant
  (ISO 32000-2 §9.7.5.4). A Chameleon font set, or a synthetic font in a CFF set (`SyntheticBase`),
  is consumed and refused by name; such a font draws in the face its name maps to, reported.
- **Colour.** As a fill or stroke colour, `setcolor` in `/Indexed` paints its palette entry, and in
  `/Separation` and `/DeviceN` the alternate-space colour the program's tint transform gives (a
  procedure run in a child interpreter under the conversion's budget, or a FunctionType 2 dictionary)
  — reported once, because the separation itself is not written; `setcolorspace` makes the space's
  initial colour current. Separation and DeviceN *images* and *shadings* are carried in their own
  spaces, plates and all (above), and ICC, CIE, Lab and Cal colour as **Colour** above says; a CIE
  shading is refused, reported.
- **File I/O** — `file`, `run`, and writing of any kind. A deliberate sandbox boundary: the one file
  a program reaches is the program itself, through `currentfile` (above); a read of anything else is
  empty and false, said, so a reading loop ends.
- **The dictionary stack at `restore`** is left as it stands, which is what the PLRM specifies
  (`restore` never pops it; it only forbids dictionaries newer than the save on it); VM itself is
  rolled back (above).
- **`statusdict` and `internaldict`** — left *undefined* rather than stubbed, deliberately. Both are
  printer-private and everything reached through them is a device operator this interpreter does not
  have, so defining them only moves the failure one token later, and leaving them undefined lets
  `/statusdict where { … } if` take its own fallback branch. Pinned by a test so the next
  completeness sweep does not "finish the set".

## Scripts and languages

**RTL needs no special handling, and that is not a gap.** PostScript has no bidi algorithm and no
concept of writing direction: `show` paints glyphs along the current point and advances by each
glyph's width. The *producer* — Illustrator, a print driver, a typesetter — has already run bidi
resolution and emitted the glyphs in **visual order**, often with an explicit `moveto` per run. So
Hebrew and Arabic PostScript arrives pre-shaped and pre-ordered, and an interpreter that faithfully
replays the positions is correct. This is the opposite of the HTML and DOCX converters, which
receive logical-order text and must shape it — they do, through the pure-managed
`Core.Text.OpenTypeShaper`. The *renderer* never shapes, in any path.

**Non-Latin text works through `definefont`, which is implemented.** A legacy Greek, Hebrew,
Cyrillic or Central-European document encodes text in a single-byte codepage and re-encodes a base
font to say which glyph each byte means:

```postscript
/Helvetica findfont dup length dict begin
  {1 index /FID ne {def} {pop pop} ifelse} forall
  /Encoding MyEncodingArray def
currentdict end /MyFont exch definefont pop
```

…or, far more often, names one of the two built-in vectors instead of building its own:

```postscript
/Helvetica findfont dup length dict begin
  {1 index /FID ne {def} {pop pop} ifelse} forall
  /Encoding ISOLatin1Encoding def
currentdict end /Helvetica-ISO exch definefont pop
```

**That second form is the one nearly every real accented-Latin document uses.** It once failed
*in the middle of the idiom*, leaving `currentdict` pushed
and the copied dictionary on the operand stack, so the `definefont` that followed ran against a
desynchronised stack and the document drew with the wrong font rather than with none. Every
re-encoding test in the suite supplied the array by hand — the one form real files do not use.

That `/Encoding` array is carried into a PDF **`/Differences`** encoding on the emitted font, and
`stringwidth` measures through it rather than through WinAnsi — so a re-encoded document gets both
the right glyph names and the right metrics. Consecutive codes are run-length encoded, and an
all-`.notdef` array is ignored rather than emitted (it would make the font worse than plain WinAnsi).

**What still limits it is the glyph repertoire, not the encoding:**

| Script | Status |
|---|---|
| **Latin, incl. accented** | Works. WinAnsi covers it; no re-encoding needed. |
| **Greek** | Works when the base font is `Symbol`, which contains the Greek glyphs. |
| **Hebrew, Cyrillic, Thai, Vietnamese, Indic** | Works **when the document embeds its font**, which such documents almost always do — the program is captured and emitted verbatim as a `/FontFile`. Without one, `/Differences` is still emitted with the right glyph names but no standard-14 face has the glyphs, which is **reported** and makes `IsClean` false. |
| **CJK** | Works, via composite fonts — see below. |

### CJK

`findfont` on a composite name (`KozMinPro-Regular-90ms-RKSJ-H`) and `composefont` both produce a
PDF **Type0** font naming the same Adobe predefined CMap, with a `CIDFontType0` descendant declaring
the right `/CIDSystemInfo`.

**The bytes are never decoded, and that is the whole trick.** A PostScript composite font turns
bytes into CIDs with a CMap; a PDF Type0 font naming the *same* CMap makes the reader do the
identical decoding. So `show` operands pass into `Tj` unchanged and this converter needs no
Shift-JIS, Big5, EUC or GBK knowledge at all.

It depends on **your CMap plugin** being registered:

```csharp
PdfPluginHost.Instance.Register(new AdobeCMapPlugin());       // populates the Core registry
PdfPluginHost.Instance.Register(new PostScriptXObjectPlugin());
```

`Core.Fonts.PredefinedCMapRegistry` is what the two share — it lives in **Core**, so this plugin
consults it without referencing the CMap plugin, which would be a plugin-to-plugin dependency the
architecture forbids. Without it nothing is recognised as CJK and the conversion says exactly which
registration is missing rather than guessing.

Two limits worth knowing. Per-CID widths need the font program, so the descendant declares
`/DW 1000` and no `/W` array — correct for the full-width glyphs that dominate CJK, wrong for a
proportional Latin run inside the same font, and reported. And the reader supplies the actual CJK
face: this emits a `CIDFontType0` naming e.g. `KozMinPro-Regular`, the same "predefined CMap +
predefined CIDFont" route viewers have always supported.

### Embedded fonts

A document that ships its own Type 1 font has that program **captured and embedded verbatim** as a
`/FontFile`, with the `/Length1` `/Length2` `/Length3` section lengths a reader requires. A PDF
`/FontFile` stream *is* a Type 1 program, so passing the bytes through gives the reader the
document's own glyphs — the same "don't interpret, pass through" move as the CMap path. (The program
is read as well, for what this side needs of it; below.)

The capture happens **while scanning**, not when the `eexec` operator executes: the whole input is
tokenised before any of it runs, so an operator acting at execution time would find the bytes already
behind it. The section is consumed as a unit either way — tokenising ciphertext produces a storm of
garbage names and eventually a byte that looks like `{`.

**A program is reached by the name its header declares.** A real Type 1 program calls `definefont`
*inside* its encrypted section — the Type 1 format's own example ends the `eexec` portion with
`dup /FontName get exch definefont pop` — so the clear-text program never defines the font, and
the document's `/CMR10 findfont` is the only request for it. `findfont` therefore looks the name up
among the captured programs by their headers' `/FontName` before it falls back to the standard 14,
and the dictionary it returns names its program under a private key, so the association survives
`scalefont`, `makefont`, a `def` and the re-encoding idiom's copy. Consulting only fonts defined in
clear text, every real embedded font would draw as the Helvetica substitute. A header that declares no `/FontName` can only be claimed by the next `definefont`, once.

**The program is also read, for its glyphs** — the embedding is still verbatim, and the reading is
Core's `Type1Outlines`: each charstring run (subroutines, flex and `seac` accents included, hints
dropped) into its outline and its `hsbw` width. `charpath` appends those outlines, `stringwidth` and
the pen after `show` advance by those widths, and the emitted font carries `/FirstChar 0 /LastChar 255
/Widths` read from them under the encoding the dictionary declares — ISO 32000-1 Table 111 requires
them, and §9.6.2.1 that they agree with the program. A code's glyph is named by the font dictionary's
`/Encoding`, else the program's own array, else StandardEncoding; one the program lacks is its
`.notdef`. A program Core cannot read keeps the standard face's metrics and reports the `/Widths`
omission, as before.

**A program's own font dictionary is drawn at its size.** A real program builds its dictionary
itself — `12 dict begin … /FontMatrix [0.001 0 0 0.001 0 0] def … currentdict end` — so its glyph
matrix sat in the slot `scalefont` accumulates into, and `100 scalefont` drew at 0.1. `definefont`
takes it into thousandths once, where the program is attached; a dictionary copied from a `findfont`
result already carries this interpreter's matrix and is left alone. The plugin's own fixtures had
defined an empty `12 dict`, which is why nothing saw it until a real program did.

This is what makes Hebrew and Cyrillic work, and such documents nearly always embed. Where no program
is supplied the standard-14 path still applies, and says what is missing.

### Type 42 fonts

A **Type 42** font (Adobe TN 5012) — what the Windows PostScript driver, LibreOffice and Cairo
emit for a TrueType face — is a dictionary whose `/sfnts` strings hold a TrueType program
and whose `/CharStrings` map glyph names to glyph indices. `definefont` joins the strings (an
odd-length string's last byte is padding, dropped as TN 5012 §4.2 has it), reads the
table directory through Core's `SfntTableDirectory`, and attaches the program; one the converter
cannot carry — no `head`, `hhea` or `maxp`, no outlines anywhere, a refused directory — leaves the font
on the standard face its name maps to, reported.

**It is emitted as a Type0 font over a `CIDFontType2` whose CIDs are glyph indices**
(`/Identity-H`, `/CIDToGIDMap /Identity`), not as a simple TrueType font. A simple font reaches a
glyph through its program's `cmap` by code, so every encoding a document shows the font through —
and every `glyphshow` of a name outside it — would need a program of its own. Showing two-byte glyph
indices names the glyph directly: each code's name comes from the `/Encoding` of the font in use *as
it stands when the text is shown* (a driver fills it after `setfont`), its index from `/CharStrings`.
`Tw` spaces only a one-byte code 32 (§9.3.3), so where word spacing applies the run is split after
each space and each piece placed at its own pen.

**A driver's glyphs are taken when they are first used.** Incremental download writes `/sfnts`
without `glyf`, `loca` or `hmtx` and adds each glyph later — its raw `glyf` bytes under
`/GlyphDirectory`, its `[lsb wx]` under `/Metrics` — often inside a page's `save`/`restore`, which
rolls those dictionaries back. So the glyph and its width are copied out the first time a show,
measure or outline uses them (a composite glyph's components with it), and the program a reader gets
is assembled from what was taken when the resources are built: a long `loca` and a `glyf` over the
glyphs taken, an `hmtx` from the widths used, through Core's `SfntBuilder`. The descendant's `/W`
carries the advance the interpreter moved the pen by — `/Metrics` first (PLRM §5.9.2), else the
program's `hmtx` — and the `/ToUnicode` each glyph's name (Core's `ToUnicodeCMapWriter`). A Type 2
CIDFont's `/MetricsCount` (TN 5012 §5.7: 0, 2 or 4 sixteen-bit values before each directory string's
glyph, the vertical pair first at 4) is taken off the glyph, its horizontal advance the glyph's after
`/Metrics`, its side bearing the `hmtx` record's; any other value, or a prefix on a Type 42 base font
(the note defines it for Type 2 CIDFonts alone), is refused by name. `charpath` reads the outlines
through Core's `TrueTypeOutlines` over the same assembled program, `stringwidth` the same widths.

### Type 2 CIDFonts

A **Type 2 CIDFont** (PLRM §5.11.3, Table 5.17) is a Type 42 program keyed by CID — how the Windows
driver sends a CJK or Unicode TrueType face. `/CIDFont defineresource` (or `definefont`) reads its
`/sfnts` as a Type 42 font's and its `/CIDMap` — `/CIDCount` entries of `/GDBytes` bytes, high byte
first — and makes its `/FontType` 11, as PLRM says. `composefont` (`key cmap [cidfont] composefont`,
the CIDFont looked up in the CIDFont category first) keeps it as the composite font's `/FDepVector`
and defines the result under its key. Each shown code goes through the CMap to its CID — decoded by
Core's `CidCollectionText`, as a reader decodes it — and through `/CIDMap` to a glyph index (glyph 0
past `/CIDCount` or the map), its width from `/Metrics` keyed by the CID, else `hmtx`; the glyphs go out
through the same Type0 font a Type 42 font uses, and the `/ToUnicode` carries each CID's text in the
CIDFont's own collection. A CMap Core cannot read keeps the predefined-CMap path below.

**A font that says nothing of its text** — the Microsoft PS Class Driver's, a Type 2 CIDFont under a
made-up collection (`cpsFontConverter-Synthetic-Registry`), its program with no `cmap` or `post`, its
glyphs shown by index — keeps the installed font's glyph numbers, `head` and glyph bytes. Each glyph
nothing else names is matched against the installed font it came from (Core's `InstalledFontGlyphs`:
the same `head` but for the fields a re-assembly recomputes, then the same bytes for that glyph), and
takes its text from that font's `cmap` — a presentation form, the driver's shaped Arabic or a pointed
Hebrew letter, as its letters — and a glyph no installed font matches is drawn and said, once a font,
because its text cannot be extracted.

**A driver's landscape page** is rotated content on portrait paper, said only in a feature comment
(`%%BeginFeature: *PageOrientation Landscape`, its code empty). `ConvertDocument`'s
`AutoRotatePages` (Acrobat Distiller's parameter: the dominant text orientation on the page)
with `PageByPage` turns each page — its box and its content, not a
`/Rotate` that turns only the view — so its predominant text reads left to right; a page with no text
is left as drawn. The default, `None`, leaves every page as drawn.

**A print driver's job** wrapped in HP's Printer Job Language is taken out of the wrapper by
`ConvertDocument`: the program is what `@PJL ENTER LANGUAGE = POSTSCRIPT` introduces, to the next
Universal Exit Language (`ESC%-12345X`), as the PJL Technical Reference has a printer read it; a job
in another language (PCL) is refused by name, and a second language section is said.

### Type 0 CIDFonts

A **Type 0 CIDFont** (PLRM §5.11.3) is Adobe's CID-keyed Type 1 format: `/CIDInit /ProcSet findresource
begin`, the CIDFont dictionary, and `(Binary) n StartData` (or `(Hex)`) with the binary data — an
offset table by CID at `/CIDMapOffset`, an FD index of `/FDBytes` and a charstring offset of
`/GDBytes` each, a charstring ending where the next CID's begins; and the subroutines of each FD,
a `/Subrs` array or a table at `/SubrMapOffset`. `StartData` sets `/GlyphData`, defines the CIDFont and
ends both dictionaries. A CID's charstring — from the table, or from `/GlyphDirectory` (an FD index then
the charstring) — and its subroutines are decrypted under the FD's `/lenIV` (4 by default), as two other
implementations both read them, and run by Core's Type 1 interpreter the first time the CID is used.

PDF holds no Type 1 CIDFont program, so each glyph is added to a CFF (Core's
`Type1CharstringFontBuilder`, CID 0 first): a CFF without CIDFont operators under a `CIDFontType0`
takes its CIDs as glyph indices (ISO 32000-2 §9.7.4.2), and the text is shown by those indices
through the same Type0 font a Type 42 font uses — `/W` from the `hsbw` widths or `/Metrics` by CID,
`charpath` from the same outlines. Hints do not travel; the outlines are exact. One CFF has one font
matrix, so FDs whose matrices scale the glyph space differently are written in the finest FD's
space: a glyph run under a coarser FD is scaled into it, each point rounded to its units, and its
`/Metrics` width read in its own FD's space. A glyph scaled past what a CFF operand holds
(`Type1CharstringFontBuilder.MaxCoordinate`), like a `seac` composite, draws as an empty glyph, said —
a CIDFont names no glyphs for `seac`'s components, and two other implementations refuse it too.

### CFF font sets

`/FontSetInit /ProcSet findresource begin /Key n StartData` and `n` bytes of CFF (PLRM §5.8.1) is how
Adobe's applications send an OpenType-CFF face. `StartData` reads the bytes after its delimiter —
always, whatever becomes of them, so binary data is never scanned as PostScript — defines a
`/FontSet` instance under the key and ends the procedure set's dictionary. Each font of the set is a
Font `findfont` reaches by the name its Name INDEX gives (PLRM leaves the mechanism open; other
interpreters define every font of a set), read through Core's `CffFontSet`: embedded as a `/FontFile3` of
`/Type1C`, through its built-in encoding, with `/Widths` and the pen's advances from its own
charstrings. An embedded CFF holds exactly one font (ISO 32000-2 §9.9), so a set holding several is
split — each font a CFF of its own, its Name and Top DICT INDEXes the one font's and the rest of the
set at the offsets it had (`CffFontSet.TryRead(data, index, …)`); a set of one is embedded as it is.
A deleted Name INDEX entry is skipped, and a synthetic font (`SyntheticBase`, drawn through another
font of the set) refused by name.

A **CID-keyed** font in a set is a CIDFont resource under its name instead — the CFF specification
puts CID-keyed fonts in font sets, PLRM §5.11 reaches a CIDFont through that category, and
other interpreters define it there — for `composefont` to join to a CMap. Its text passes through unchanged
under the CMap, as the predefined-CMap path's does, but in a Type0 font over a `CIDFontType0` whose
program is the font (a `/FontFile3` of `/CIDFontType0C`, its `/CIDSystemInfo` the font's `ROS`): each
code's CID is decoded by Core's `CidCollectionText` only to move the pen by the font's width for it,
and those widths are the descendant's `/W`.

### CMaps the program builds

A CMap resource sent with the job — `/CIDInit /ProcSet findresource begin`, a dictionary,
`begincmap`, the CMap's entries, `endcmap`, `CMapName currentdict /CMap defineresource` (PLRM §5.11.4)
— is read by the `CIDInit` operators: each `n begincodespacerange`, `begincidrange`, `begincidchar`,
`beginnotdefrange` or `beginnotdefchar` counts the `n` entries its `end…` pops (a code one to four
bytes, a range's bounds of one length, a CID a non-negative integer, else a `rangecheck`; past
`PsLimits.MaxCMapEntries` a `limitcheck`), and `usecmap` names the CMap it takes the rest from.
`defineresource` makes the dictionary a CMap, its `/CIDSystemInfo` (a dictionary, or PLRM's array whose
first entry is font 0's) and `/WMode` read then. `composefont` composes with it by its key or the
dictionary itself — before a predefined CMap of the same name, as the program's definition is the one
it uses.

The CMap is written back as a CMap program in Adobe Technical Note #5014's form, ISO 32000-2
§9.7.5.4's example (at most 100 entries a block), and read by Core's `EncodingCMap`: every text site —
`show` and the positioned shows, `stringwidth`, `cshow`, `charpath` — splits and maps the codes as the
reader will. The PDF carries it as the Type0 font's `/Encoding` stream — `/CMapName`,
`/CIDSystemInfo`, `/WMode` — whichever font it composes: a host face's descendant declares the CMap's
collection. A `usecmap` of a CMap the program defined is that CMap's own stream under `/UseCMap`; of a
predefined one, its name. A chain deeper than the reader follows (`PdfLimits.MaxCMapUsecmapDepth`) is
a `limitcheck`. What §9.7.5.4 keeps out of an encoding CMap — mappings for a font other than font 0,
base-font mappings, `beginusematrix`, `beginrearrangedfont` — is consumed and said.

**A document with no re-encoding cannot be detected.** ISO-8859-8 for "shalom" is the bytes
`E9 EC E5 ED`, byte-identical to Latin-1 accented text. Nothing distinguishes them except the
`/Encoding` the document did not supply, so such a file is converted as what it looks like. There is
no heuristic here that is not a guess.

## Design notes worth knowing before changing it

**The step loop is iterative.** Executing a procedure pushes a heap frame rather than recursing, so
`/f { f } def f` raises a catchable `execstackoverflow` instead of an uncatchable
`StackOverflowException`, and `exit`/`stop` unwind by popping frames.

**Nothing is emitted eagerly.** Paths accumulate in *user space* and colours in the graphics state;
both flush only at a painting or clipping operator. That single rule is what makes PostScript's
immediate `clip` become PDF's deferred `W n` — with the path re-emitted for a following `fill` — and
PostScript's single colour become PDF's separate fill and stroke colours, with no special cases.

**The CTM is never baked into coordinates.** Every transform change is emitted as a `cm`. PDF derives
line width and dash phase from the CTM exactly as PostScript does, so baking would silently destroy
both.

**`Core.Functions.PdfFunctionEvaluator` was not extended, deliberately.** Its float-only value model
is correct for the closed Type 4 subset and wrong for the language: `5 2 idiv` is 2 while
`5.0 2 idiv` is a `typecheck`, `and` is bitwise on integers, and `stackunderflow` must be catchable
by `stopped` rather than silently yielding 0. Its *bounding strategy* was copied wholesale.

**Conversions are reproducible.** `rand` is seeded from a constant and `usertime` does not read the
clock, so the same PostScript yields the same content stream every run — without which no test could
assert on output and no caller could diff two conversions. (A converted *document* still gets a fresh
trailer `/ID` per build, because §14.4 defines that as a unique file identifier.)

## Bounds

PostScript is Turing-complete over untrusted input, so every axis it can exhaust is budgeted in
`PsLimits`, and every entry there has an adversarial regression test:

| | |
|---|---|
| Operations | `MaxTotalOps` (20 M), through `Core.Util.WorkBudget` |
| Stacks | exec 1 000 · operand 10 000 · dict 64 · parse nesting 100 |
| Allocation | 1 M per array/string, **64 MiB aggregate** across the conversion |
| Output | 8 MiB per fragment, 32 MiB per document |
| Structure | 5 000 pages, 256 images, 64 dash entries, 8 arc segments |
| Binary skip | 16 MiB, and at most 64 captured font programs |

Two of those are the ones that matter most. **`MaxTotalOps`** is what terminates `{ } loop`. And
**`MaxCompositeAllocationBytes`** is aggregate because per-allocation caps do not compose:
`{ 1000000 string pop } loop` stays under the per-string cap on every iteration and still exhausts
memory.

**Capturing a font program is the one scanning step that copies input rather than pointing into
it**, and it is bounded three ways for one reason worth knowing. A capture runs from the clear-text
`%!PS-AdobeFont` header the DSC scanner last saw to the end of the encrypted section — so a file
carrying *one* header and many `eexec` sections would capture each of them from that same offset,
every copy longer than the last. That is quadratic in the input and all of it retained: 1 MiB of
such a file would allocate 5.3 GiB. The header is forgotten once used (one header, one program), the copy is charged to the allocation budget like any other composite, and the count is
capped at `MaxCapturedFontPrograms`.

Graphics-state depth deliberately reuses `PdfLimits.MaxGraphicsStateStackDepth` rather than
redefining it — emitting deeper than the renderer's own guard accepts could only produce a stream
`Base` refuses to draw.

All of them are overridable per conversion through `PostScriptConversionOptions`.

## Diagnostics

`PostScriptDiagnostics` follows the repo's feature-sink pattern: `[ThreadStatic]` `LastError`,
`UnimplementedOperators` and `ReportCount`, plus an `OnEvent` for telemetry. `Clear()` runs at the
start of every public entry point — per-thread storage is not per-document scope, and confusing the
two is a bug this repo has seen before.

The same three values are copied onto the result object, so a concurrent host never has to read
thread state to learn about a value it already holds.

## Licence and provenance

Tetitulz.Pdf Attribution License 1.0 (LICENSE.txt in the package): the MIT License plus conditions on every use of this plugin, of the whole or any part — credit "Uses Tetitulz.Pdf by Tamir Mordo", report the use on https://github.com/tetitu/tetitulz.pdf, and report any security problem privately with the fix if there is one. Copyright (c) 2026 Tamir Mordo. See the repository for the full guide, the open-gap register and the third-party notices.

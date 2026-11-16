# Tetitulz.Pdf.Plugins.Templating

Fills a PDF template from a `name → value` map. It covers **both** kinds of placeholder a real
template uses — named AcroForm fields, and literal `{{token}}` text drawn on the page, including
text inside a Form XObject — in one call, and Hebrew, Arabic and other RTL scripts work throughout.

The plugin is a façade. The AcroForm half is `Base.Operations.Forms.PdfFormFiller`; the
content-stream surgery is `Base.Operations.Documents.PdfTemplateEngine`, which lives in Base because
it needs `Base.Rendering.FontCache` (`internal`) to delimit character codes and Core grants
`InternalsVisibleTo` to no plugin. Same split, same reason, as `PdfRedactor` → `PdfRedactionEngine`.

## Calling it

```csharp
using var input  = File.OpenRead("contract-template.pdf");
using var output = File.Create("contract.pdf");

var report = PdfTemplateFiller.Fill(input, output,
    new Dictionary<string, string>
    {
        ["customer_name"] = "דנה כהן",
        ["amount"]        = "1,234 ₪",
        ["signed_on"]     = "2026-08-11",
    },
    new TemplateOptions
    {
        BaseDirection = BaseDirection.RightToLeft,          // Core.Text.BaseDirection
        FontProvider  = _ => File.ReadAllBytes("/srv/fonts/DavidCLM-Medium.ttf"),
    });

if (report.UnmatchedKeys.Count > 0)
    throw new InvalidOperationException("template is missing: " + string.Join(", ", report.UnmatchedKeys));
```

`TemplatingPlugin` exists only so a host holding Core can *see* that templating is available through
`PdfPluginHost.RegisterFromLoadedAssemblies()`. **There is deliberately no `ITemplatePlugin`.** Core
defines a plugin contract per capability, and every one of them exists because *Base* needs to call
out to a plugin without referencing it — conformance validation is the worked example. Nothing in
Base calls templating, so an interface here would be an abstraction with one implementation and no
caller. Reference the assembly and call `PdfTemplateFiller` directly, exactly as you would
`PdfMerger.Merge`.

## The trade that is on the API, not only in the docs

**Filling a field preserves a signature. Replacing a page token invalidates every signature.** That
is not an implementation shortcoming and it will not change:

| Path | How it writes | Existing approval signature |
|---|---|---|
| `TemplateSources.FormFields` | incremental update — the original bytes are untouched | **still verifies** |
| `TemplateSources.PageTokens` | full single-revision rewrite | **invalidated** |

Token replacement *erases* the placeholder, and an incremental update cannot erase anything: the old
glyphs stay in the revision the first signature's digest covers, which is precisely what lets
`PdfRevisions` recover them. Preserve the signature **or** remove the data, never both. It is the
same trade GAPS.md §6/§6a records for `PdfRedactor` against `PdfImageReplacer`, and if you need the
signature intact, use form fields.

Both passes run by default (`TemplateSources.All`), **fields first, tokens over that result** — so
the rewrite carries the field values it just wrote. One consequence worth knowing: **a key that
matches a form field is not then looked for as a page token.** It is removed from the pending set
when the field is filled, so a template carrying both a `name` field and a `{{name}}` on the page
fills the field and leaves `{{name}}` printed, with nothing in the report to say so. Split the names
or narrow `Sources`.

## Read the report — it is the point of the API

`Fill` never throws for a placeholder it could not handle. It writes a document and tells you what it
did, which is a different thing from what you asked for.

| | |
|---|---|
| `FilledFields` | fully-qualified AcroForm names whose `/V` was written |
| `FilledTokens` | page tokens located and erased, **keyed by name** — three occurrences of one key is one entry |
| `UnmatchedKeys` | matched nothing at all. **Check this**: silence here is how a contract ships with `{{signature_date}}` still printed on it |
| `NotEditableKeys` | located, but inside a Form XObject drawn more than once. The document is untouched for these |
| `NotDrawn` | the value was written / the placeholder erased, but nothing was drawn — normally no font covering the text |
| `UnverifiedKeys` | reported replaced, yet the token is still extractable from the output |
| `Warnings` | one line per tolerated condition, naming the placeholder |
| `NeedAppearances` | what was written to the AcroForm's `/NeedAppearances` |
| `ChangedAnything` | at least one field or token was filled |

**Invariant: every supplied key lands in exactly one of `FilledFields`, `FilledTokens`,
`UnmatchedKeys` and `NotEditableKeys`, exactly once.** A key cannot go missing from the report.
`NotDrawn` and `UnverifiedKeys` are orthogonal — a key is on one of those *as well as* on its bucket.

**`NotEditableKeys` is kept out of `UnmatchedKeys` on purpose, and the distinction is the whole
reason it exists.** "Unmatched" tells a caller to check the key for a typo; "not editable" tells them
the key is right and the *template* needs restructuring. They lead to opposite investigations, and
collapsing them sends the caller to the wrong one. A key in a shared form comes back as not
editable, not unmatched.

When nothing matches, the input is copied through **byte for byte**; `Fill` always writes a document
to the output stream.

## Page tokens

### Finding one

The matcher builds **one text buffer across show operators** and maps each match back to the glyphs
that produced it. That is not a nicety for exotic files: producers break runs at kerning pairs, and
`{`/`}` kerns in most Latin fonts, so the default delimiters are the ones *most* likely to be split.
`(Dear {) Tj ({name) Tj (}}, welcome.) Tj` is an ordinary template, and it matches.

Show strings are split into character codes through the font's own encoding CMap
(`GlyphIdResolver.DecodeCodes`), **not** by a fixed stride, so a CMap mixing 1-byte ASCII with 2-byte
CJK is delimited correctly — see the defects section.

If you control the template, a delimiter that does not kern (`[[`, `<<`) still gives the matcher an
easier job. `TokenOpen`/`TokenClose` take any non-empty pair.

### Erasing it without moving anything else

The placeholder's character codes are **dropped from their show operator** and their total advance is
re-emitted as one `TJ` adjustment, so every surviving glyph keeps its exact position. The guarantee is
structural rather than heuristic, and there is no fourth case to worry about: within the operator the
adjustment compensates exactly; later operators in the same text object are unaffected because a show
operator advances `Tm` by exactly what was compensated; and `Td`/`TD`/`Tm`/`T*` set position
absolutely or relative to `Tlm`, which show operators never touch.

The replacement is then drawn as a self-contained `q BT … ET Q` block inserted **after** the enclosing
`ET`, carrying an absolute `Tm` and its own font — never inside the text object, where a nested `BT`
would be malformed and the leftover text state would apply.

`TemplateFit.KeepSlot` (the default) shrinks the replacement's font size so it occupies exactly the
placeholder's advance and nothing on the line moves — a template's placeholder normally sits in a
designed slot, and reflowing the line around it is the more surprising outcome. It stops at **4 pt**;
past that the text is drawn at 4 pt, overflows, and says so in `Warnings`. `TemplateFit.Natural`
draws at the natural width and lets the line reflow.

### Form XObjects

Real templates put body content in a form more often than not — design tools emit one, and so does
anything reusing a letterhead. The walk descends into them (to `PdfLimits.MaxFormXObjectDepth`, 32),
composing the form's `/Matrix` (§8.10.1) with the CTM in force at the `Do`, so **a glyph position
recorded inside a form is already in page space**.

That buys the asymmetry that makes this work: **erasure happens in whichever stream holds the
placeholder, but the replacement is always drawn from the page's own content.** Splicing it into the
form instead would draw it once per invocation of that form.

**A form drawn more than once anywhere in the document is never edited.** Erasing the placeholder
would erase it from every other place that form appears. Such a form is still descended into — for
*detection* — and its keys are reported on `NotEditableKeys` with the reason in `Warnings`, because
skipping it silently would leave the caller's key in `UnmatchedKeys`. Uniqueness is transitive
(`Base.Operations.Forms.FormInvocationCounter`, shared with redaction so the two cannot drift on what
"shared" means): a form invoked once from a page and once from *inside another form* is correctly
refused.

## Hebrew and other RTL scripts

Four separate things have to be right, and each of them was a place this could silently produce a
blank or reversed field:

- **`Core.Text.TextRunComposer`** is the one shape → bidi-reorder → glyph pipeline, shared with
  generated AcroForm appearances. Shaping happens **before** reordering, because Arabic joining is
  decided by a letter's *logical* neighbours; getting that order backwards produces plausible-looking
  wrong glyphs.
- **Partial coverage refuses.** The composer runs under `MissingGlyphPolicy.Refuse`, so a font missing
  one letter draws nothing and names the code point, rather than dropping a letter out of a name.
- **`/ToUnicode` maps back to the base code point**, not the presentation form that was drawn, so the
  output is searchable and copy-pasteable as the author typed it even though the glyphs are laid down
  right-to-left.
- **`UnicodeBidi.Reorder(string, BaseDirection)`** — an overload rather than an optional parameter,
  because this ships as a package. Set `TemplateOptions.BaseDirection` to `RightToLeft` for a Hebrew
  or Arabic template. **First-strong auto-detection is wrong for exactly the values a template
  holds:** `"Tel Aviv 12"` and `"1,234 ₪"` in a Hebrew field begin with a Latin word or a digit, so
  the paragraph resolves LTR and the currency sign and trailing punctuation land on the wrong side.
  Direction is a property of the template, and no amount of inspecting the value recovers it.

**The value is bidi-resolved in isolation**, as a Unicode isolate. A neutral at the token boundary —
a trailing comma in `שלום,` inside an LTR line — therefore resolves against the value rather than the
surrounding line. Real, small, and unfixable without full line relayout, which would defeat a
slot-preserving edit.

## Fonts

**This library ships no fonts and licenses none.** Anything outside Latin-1 — so every Hebrew or
Arabic value — needs a font *program*, and embedding one redistributes it, which is why the decision
is the caller's:

- `TemplateOptions.FontProvider` is the primary path: called with the name being substituted for,
  return TrueType/OpenType bytes or `null` to decline.
- `AllowSystemFontSubstitution` defaults to **false**. With no font source the placeholder is still
  erased (or the field value still written), the key appears in `NotDrawn`, and a warning says so —
  never a silently mis-drawn value.
- `TETITULZ_PDF_FONTDIR` **replaces** the OS search path. Pointing it at fonts the deployment owns and
  then setting `AllowSystemFontSubstitution` true is the configuration that carries no licensing risk,
  and it is the recommended server setup.

**`Core.Fonts.SystemFontResolver` matches real cmap *coverage*, never a filename**, and on Linux that
is the difference between working and shipping blank pages: Liberation is what a minimal container
installs, it resolves happily under any Latin family name, and it has **no Hebrew glyphs at all**. The
resolver's Hebrew stems put Culmus (`fonts-culmus`), Noto Hebrew and the Windows families ahead of the
generic fallbacks for that reason. The templating tests probe through the same resolver and report
**Skipped** on a host with no Hebrew coverage (`HebrewFontFactAttribute`) rather than passing
vacuously.

One font is embedded per call, covering the union of every replacement's code points, and
`Type0FontBuilder` subsets it to **exactly the glyphs that are drawn** — which is why the runs are
emitted before the font is built rather than after.

## What it refuses, and how you find out

Nothing is skipped quietly. Each of these names the key.

- **A token must lie within one text object, on one baseline, and in one content stream.** Two halves
  in different text objects have independent text matrices, so there is no single slot to erase; two
  halves in different streams (page content and a form, or two forms) are edited independently and
  drawn at unrelated times. Baseline agreement is checked to 0.5 in text space. A wrongly-placed
  replacement is worse than an untouched placeholder — the placeholder is visible, and the
  misplacement looks deliberate.
- **An encrypted document is refused** with `PdfTemplateException`, naming
  `Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip`. It is checked before anything else, because an
  encrypted document exposes no catalog and "no tokens were found" would be a true statement about a
  file that was never read.
- **No page breaks, and there cannot be.** Overflow shrinks (`KeepSlot`) or reflows the line
  (`Natural`). Pushing it onto a new page would mean re-running pagination for the whole document and
  fixing every page reference, annotation, link destination and structure-tree entry — that is
  document *generation*, which is `Tetitulz.Pdf.Builder`.
- **Only `g` / `rg` / `k` fill colours are carried onto the replacement.** The run adopts the colour
  in force at the placeholder; a token painted through a colour space (`cs` + `scn`) falls back to
  black.
- **The document is buffered in memory**, up to three copies across the two passes, and
  `Core.Text.ContentStreamParser.Parse` is documented as unbounded (GAPS.md §12e). Templating is a
  first-party-template operation; it is not hardened for hostile input the way the PostScript
  interpreter is.

The complete list of deliberate limits, with their reasoning, is **GAPS.md §14**.

## Verification — three layers, and what each cannot prove

1. **Geometry, inside the operation, before anything is written.**
   `PdfTemplateEngine.VerifySurvivingGlyphs` re-walks every rewritten stream from its *post-edit
   bytes* and requires every glyph that was **not** part of a token to still be drawn at the same
   device position, to 0.01 pt. Otherwise `Replace` throws `PdfTemplateException` and writes nothing.
   It is stated as "every surviving original glyph is present at its position" rather than as sequence
   equality, because the output legitimately *gains* glyphs — the replacement runs — and a sequence
   check would have to know which those are. Presence-at-position tolerates insertion by construction
   while still catching removal and movement, the only two ways this operation goes wrong.
   **It cannot prove the page renders identically:** identical glyph positions are not identical
   pixels.
2. **Erasure, by re-reading the produced bytes** (`VerifyErasure`, default on). Any key reported
   replaced whose token is still extractable from an edited page lands on `UnverifiedKeys`.
   **This can only falsify.** Extraction is lossy — a token surviving in a font that reads back as
   something else stays invisible to it — so finding one is proof of a miss, and not finding one is
   the absence of that proof rather than its converse. Nothing is ever added to a "verified" list,
   because there is no such thing to say. The common cause of a hit is a token that also appears in a
   *shared* form, and such a key is usually on `NotEditableKeys` too. Turning it off skips the check
   and the buffering it needs; the produced bytes are identical either way.
3. **Pixels, in the test suite.** `TokenPixelIsolationTests` renders before and after and requires
   every row outside the edited line to be byte-identical, with a control asserting the page is not
   blank (a blank render makes every comparison vacuously true) and another asserting the edited line
   *did* change (without which a no-op engine passes). **No mutation was found where these fail and
   layer 1 passes** — the obvious candidate, dropping the `Q` that closes the inserted run so its fill
   colour leaks, is equivalent by construction, because the run adopts the colour already in force.
   So they are a stronger statement of the same guarantee, end to end through the renderer, rather
   than a demonstrated superset. Do not delete them as redundant; do not claim they catch strictly
   more.

Still genuinely unverified at every layer: interaction with a text render mode, a clip path or a
pattern fill around the inserted block. No fixture exercises those.

## What this plugin shares with the engines it uses

The template engine walks the same content streams as redaction and extraction, through the same
rules, so the three cannot disagree:

1. **Whether a Form XObject is shared** is `FormInvocationCounter`'s answer — transitive and
   cycle-guarded, so a form invoked once from a page and once from inside another form counts as
   shared, and neither engine rewrites it in place.
2. **Character codes** are split through `GlyphIdResolver.DecodeCodes`, by the font's codespace, so a
   CMap mixing 1-byte ASCII with 2-byte CJK is never stepped through a byte at a time; `Tw` applies to
   the **single-byte** code 32 alone (§9.3.3). `PdfStructuredTextExtractor`, which the position tests
   measure through, splits the same way.
3. **`FormFillOptions.BaseDirection`** forces a Hebrew *form field* RTL as
   `TemplateOptions.BaseDirection` does a token.
4. **A value too long for its widget is reported**: `FormAppearance.AppendLines` returns the
   dropped-line count past the 4 pt floor and `PdfFormFiller` surfaces it — **without** setting
   `/NeedAppearances`, because the drawing is correct as far as it goes and the widget will not get
   any taller.

## Design notes worth knowing before changing it

**The replacement font is built last, and that ordering is load-bearing.** Runs are emitted before
`FinishFont`, because building the font needs the set of glyphs actually drawn: `Type0FontBuilder`
subsets to exactly those and emits `/W` and `/ToUnicode` for them alone. Building first would either
embed the whole face or describe glyphs the page never shows.

**New objects are numbered above `max(/Size, CrossReferenceTable.GetMaxObjectNumber())`.** The
trailer's `/Size` is a declaration, not a measurement, and a file with a stale incremental-update
trailer can declare fewer objects than it contains. An object minted inside that gap collides with a
real one — and because `PdfFullRewriter` resolves caller-supplied objects *ahead* of the parser, the
collision does not fail: the real object is silently replaced by the font, and everything referencing
it now points at a font.

**The font resource is cloned onto the page, never set on the inherited dictionary.**
`PdfPage.Resources` falls back to a dictionary shared with every sibling page, so setting a key on it
would add the font to pages that never use it and, worse, let a name collision repaint another page's
text in it. The minted resource name is `TplF` and the first number the page does not use — `TplF0` on a
page edited once, `TplF1` on the next edit — so a page edited again keeps each edit's own font.

**Text-object ids are numbered per stream, from zero, and do not need to be globally unique.** The
matcher compares them only after establishing that both glyphs came from the same stream, so
`(StreamId, TextObjectId)` is what identifies a text object. Offsetting them by
`streamId * 100000` to force global uniqueness would buy nothing and overflow `int` past ~21,500
forms.

**`PdfFullRewriter` is shared with redaction** — the two operations that must *destroy* content —
which is why it is shared rather than copied. If you change how a single-revision rewrite is
produced, you have changed redaction as well.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file). See the repository for the full guide, the open-gap register and the third-party notices.

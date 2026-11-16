# Test fixtures — third-party

`jquery-1.12.4.min.js` and `jquery-3.7.1.min.js` are the unmodified minified builds from
<https://code.jquery.com/>, **MIT-licensed** (© jQuery Foundation / OpenJS Foundation and other
contributors — <https://jquery.org/license>); each file's own header carries the notice.

They are test inputs only: never packed, never shipped. 1.12.4 is the release Bootstrap 3 pages
still load (the owner's forms among them); 3.7.1 is current. Together they cover
the two feature-detection styles the plugin has to satisfy — 1.x touches `element.style` without a
guard, 3.x checks first — which is what `AngleSharp.Css` is in the plugin for.

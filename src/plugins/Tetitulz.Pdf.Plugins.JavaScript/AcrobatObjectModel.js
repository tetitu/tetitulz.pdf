// The form JavaScript object model (Adobe's "JavaScript for Acrobat API Reference"), as Acrobat
// answers it: tests/Tetitulz.Pdf.Plugins.JavaScript.Tests/Fixtures/Acrobat holds the recorded
// answers this file is held to (GAPS.md §25a). Where Acrobat's answers are silent the behaviour
// follows pdf.js's src/scripting_api (Apache-2.0).
//
// The global object is the document, as in Acrobat: its members are globals, and `this` in a plain
// function is the document. Everything here is plain script, bounded by the engine's limits; the
// one way out is `host`, the engine's bridge, which takes and returns strings, numbers and booleans
// and caps what it keeps. The bridge is captured here and its global deleted, so no document
// script reaches it except through the members below.
(function (host, doc) {
    "use strict";

    // ---------------------------------------------------------------- shared helpers

    var MAX_DECIMALS = 100;            // AFNumber_Format's and printf's precision (GAPS.md §25c)
    var MAX_FORMAT_DIGITS = 3;         // a printf width or precision past three digits is copied as written
    var MAX_CHOICE_ITEMS = 10000;      // a choice field's local item list
    var MAX_DATE_TEXT = 256;           // the date text and format a parse reads
    var MAX_URL = 2048;                // crackURL's input
    var MAX_SHOWN_NAME = 200;          // the characters of a name a message carries

    // What joins a multi-select list box's selections in a value the host hands over or takes:
    // the filler's own separator, read once.
    var MULTI_SEPARATOR = String(host.MultiSelectSeparator());

    function record(request) { host.Record(String(request)); }

    // A name as a message carries it: a script's or a file's name can be megabytes, and a message
    // is built before the host would cut it.
    function shortName(name) { return name.length > MAX_SHOWN_NAME ? name.substring(0, MAX_SHOWN_NAME) + "…" : name; }

    function apiError(name, message) {
        var e = new Error(message);
        e.name = name;
        return e;
    }

    function show(v) {
        if (typeof v === "string") return v;
        if (v && typeof v === "object" && typeof v.length === "number") return "[" + Array.prototype.join.call(v, ",") + "]";
        return String(v);
    }

    function own(o, k) { return Object.prototype.hasOwnProperty.call(o, k); }

    function defineValue(target, name, value) {
        Object.defineProperty(target, name, { value: value, writable: true, configurable: true, enumerable: true });
    }

    function defineGetter(target, name, get, set) {
        Object.defineProperty(target, name, {
            get: get,
            set: set || function (v) { record(name + " = " + show(v)); },
            configurable: true, enumerable: true
        });
    }

    // A recorded no-op for a member that would reach outside the process or the viewer's UI.
    function notRun(path, result) {
        return function () {
            var args = [];
            for (var i = 0; i < arguments.length && i < 3; i++) args.push(show(arguments[i]));
            record(path + "(" + args.join(", ") + ") not run");
            return result;
        };
    }

    // ---------------------------------------------------------------- constants (Acrobat's values)

    function frozen(o) { return Object.freeze(o); }

    var border = frozen({ s: "solid", d: "dashed", b: "beveled", i: "inset", u: "underline" });
    var display = frozen({ visible: 0, hidden: 1, noPrint: 2, noView: 3 });
    var position = frozen({ textOnly: 0, iconOnly: 1, iconTextV: 2, textIconV: 3, iconTextH: 4, textIconH: 5, overlay: 6 });
    var scaleHow = frozen({ proportional: 0, anamorphic: 1 });
    var scaleWhen = frozen({ always: 0, never: 1, tooBig: 2, tooSmall: 3 });
    var style = frozen({ ch: "check", cr: "cross", di: "diamond", ci: "circle", st: "star", sq: "square" });
    var highlight = frozen({ n: "none", i: "invert", p: "push", o: "outline" });

    // The font resource names an AcroForm's /DA usually gives (Acrobat's own /DR entries), as the
    // font names textFont answers; another resource name is answered as it is.
    var DA_FONTS = frozen({
        Helv: "Helvetica", HeBo: "Helvetica-Bold", HeOb: "Helvetica-Oblique", HeBO: "Helvetica-BoldOblique",
        TiRo: "Times-Roman", TiBo: "Times-Bold", TiIt: "Times-Italic", TiBI: "Times-BoldItalic",
        Cour: "Courier", CoBo: "Courier-Bold", CoOb: "Courier-Oblique", CoBO: "Courier-BoldOblique",
        Symb: "Symbol", ZaDb: "ZapfDingbats"
    });

    // The ZapfDingbats character a check box's /MK /CA draws, as the style it is.
    var CHECK_STYLES = frozen({ "4": "check", "8": "cross", "u": "diamond", "l": "circle", "H": "star", "n": "square" });
    var font = frozen({
        Times: "Times-Roman", TimesB: "Times-Bold", TimesI: "Times-Italic", TimesBI: "Times-BoldItalic",
        Helv: "Helvetica", HelvB: "Helvetica-Bold", HelvI: "Helvetica-Oblique", HelvBI: "Helvetica-BoldOblique",
        Cour: "Courier", CourB: "Courier-Bold", CourI: "Courier-Oblique", CourBI: "Courier-BoldOblique",
        Symbol: "Symbol", ZapfD: "ZapfDingbats", KaGo: "HeiseiKakuGo-W5-UniJIS-UCS2-H", KaMi: "HeiseiMin-W3-UniJIS-UCS2-H"
    });
    var zoomtype = frozen({ none: "NoVary", fitP: "FitPage", fitW: "FitWidth", fitH: "FitHeight", fitV: "FitVisibleWidth", pref: "Preferred", refW: "ReflowWidth" });
    var cursor = frozen({ visible: 0, hidden: 1, delay: 2 });
    var trans = frozen({
        blindsH: "BlindsHorizontal", blindsV: "BlindsVertical", boxI: "BoxIn", boxO: "BoxOut", dissolve: "Dissolve",
        glitterD: "GlitterDown", glitterR: "GlitterRight", glitterRD: "GlitterRightDown", random: "Random", replace: "Replace",
        splitHI: "SplitHorizontalIn", splitHO: "SplitHorizontalOut", splitVI: "SplitVerticalIn", splitVO: "SplitVerticalOut",
        wipeD: "WipeDown", wipeL: "WipeLeft", wipeR: "WipeRight", wipeU: "WipeUp"
    });

    var IDS_GREATER_THAN = "Invalid value: must be greater than or equal to %s.";
    var IDS_GT_AND_LT = "Invalid value: must be greater than or equal to %s and less than or equal to %s.";
    var IDS_LESS_THAN = "Invalid value: must be less than or equal to %s.";
    var IDS_INVALID_DATE = "Invalid date/time: please ensure that the date/time exists. Field";
    var IDS_INVALID_DATE2 = " should match format ";
    var IDS_INVALID_VALUE = "The value entered does not match the format of the field";

    // ---------------------------------------------------------------- color

    var color = {
        transparent: ["T"], black: ["G", 0], white: ["G", 1],
        red: ["RGB", 1, 0, 0], green: ["RGB", 0, 1, 0], blue: ["RGB", 0, 0, 1],
        cyan: ["CMYK", 1, 0, 0, 0], magenta: ["CMYK", 0, 1, 0, 0], yellow: ["CMYK", 0, 0, 1, 0],
        dkGray: ["G", 0.25], gray: ["G", 0.5], ltGray: ["G", 0.75],
        convert: function (colorArray, cColorSpace) { return convertColor(colorArray, cColorSpace); },
        equal: function (a, b) { return colorsEqual(a, b); }
    };

    function colorComponents(a) {
        var out = [];
        for (var i = 1; i < a.length; i++) out.push(Number(a[i]));
        return out;
    }

    // Acrobat: a transparent colour stays transparent; a target that is T, unknown or the colour's own
    // space returns the colour as it is.
    function convertColor(a, space) {
        if (!a || typeof a.length !== "number" || a.length < 1) return color.black;
        var from = String(a[0]);
        var copy = Array.prototype.slice.call(a, 0, 5);
        if (from === "T") return ["T"];
        if (space !== "G" && space !== "RGB" && space !== "CMYK") return copy;
        if (from === space) return copy;
        var c = colorComponents(a);
        if (from === "G") {
            if (space === "RGB") return ["RGB", c[0], c[0], c[0]];
            return ["CMYK", 0, 0, 0, 1 - c[0]];
        }
        if (from === "RGB") {
            if (space === "G") return ["G", 0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2]];
            var cy = 1 - c[0], mg = 1 - c[1], ye = 1 - c[2];
            return ["CMYK", cy, mg, ye, Math.min(cy, mg, ye)];
        }
        if (from === "CMYK") {
            if (space === "G") return ["G", 1 - Math.min(1, 0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2] + c[3])];
            return ["RGB", 1 - Math.min(1, c[0] + c[3]), 1 - Math.min(1, c[1] + c[3]), 1 - Math.min(1, c[2] + c[3])];
        }
        return copy;
    }

    function colorsEqual(a, b) {
        if (!a || !b) return false;
        if (a[0] === "T" || b[0] === "T") return a[0] === "T" && b[0] === "T";
        if (a[0] !== b[0]) b = convertColor(b, a[0]);
        if (a.length !== b.length) return false;
        for (var i = 1; i < a.length; i++) if (Number(a[i]) !== Number(b[i])) return false;
        return true;
    }

    // ---------------------------------------------------------------- util.printf

    function padStart(s, width, ch) {
        while (s.length < width) s = ch + s;
        return s;
    }

    function groupDigits(digits, sep) {
        if (!sep || digits.length <= 3) return digits;
        var parts = [], end = digits.length;
        while (end > 3) { parts.unshift(digits.substring(end - 3, end)); end -= 3; }
        parts.unshift(digits.substring(0, end));
        return parts.join(sep);
    }

    // The integer digits of a non-negative finite number, and its fraction to `places` places.
    function fixedParts(abs, places) {
        if (abs < 1e21) {
            var s = abs.toFixed(places);
            var dot = s.indexOf(".");
            return dot < 0 ? [s, ""] : [s.substring(0, dot), s.substring(dot + 1)];
        }
        // toFixed writes 1e21 and above in exponent form; the integer is exact as a BigInt.
        return [BigInt(Math.round(abs)).toString(), padStart("", places, "0")];
    }

    var SEPARATORS = [[",", "."], ["", "."], [".", ","], ["", ","], ["'", "."]];

    // C-style, as Acrobat answers: %[,n][flags][width][.precision]{d,f,s,x}; digits are grouped only
    // under ,n; %f has six places by default; %s takes no width; %#x has no 0x; an unknown conversion
    // is copied as written. Where Acrobat raises an error, this is tolerant: an item with no argument writes
    // nothing (Acrobat throws), a value that is not a number is 0 (Acrobat writes -2147483648 or
    // 1.#QNAN0), and a negative %x is its 32-bit two's complement (Acrobat writes "!"). A hand scan,
    // not a regular expression: flags and width both take a 0, and the format is the script's.
    function printf(format) {
        var fmt = String(format);
        var args = arguments, next = 1, out = "";
        for (var i = 0; i < fmt.length; ) {
            var ch = fmt.charAt(i);
            if (ch !== "%") { out += ch; i++; continue; }
            var start = i++;
            var sep = -1;
            if (fmt.charAt(i) === "," && fmt.charAt(i + 1) >= "0" && fmt.charAt(i + 1) <= "4") {
                sep = Number(fmt.charAt(i + 1)); i += 2;
            }
            var plus = false, space = false, zero = false, hash = false;
            for (; i < fmt.length; i++) {
                var f = fmt.charAt(i);
                if (f === "+") plus = true; else if (f === " ") space = true;
                else if (f === "0") zero = true; else if (f === "#") hash = true;
                else break;
            }
            var widthText = "";
            while (i < fmt.length && fmt.charAt(i) >= "0" && fmt.charAt(i) <= "9") widthText += fmt.charAt(i++);
            var precisionText = null;
            if (fmt.charAt(i) === ".") {
                precisionText = ""; i++;
                while (i < fmt.length && fmt.charAt(i) >= "0" && fmt.charAt(i) <= "9") precisionText += fmt.charAt(i++);
            }
            var conv = fmt.charAt(i++);
            var written = fmt.substring(start, i);
            if (conv !== "d" && conv !== "f" && conv !== "s" && conv !== "x") { out += written; continue; }
            // A width or precision the script chose is bounded before anything is padded to it.
            if (widthText.length > MAX_FORMAT_DIGITS || (precisionText !== null && precisionText.length > MAX_FORMAT_DIGITS)) { out += written; continue; }
            var width = widthText === "" ? 0 : Number(widthText);
            var precision = precisionText === null ? (conv === "f" ? 6 : 0) : Number(precisionText || "0");
            if (precision > MAX_DECIMALS) { out += written; continue; }
            if (next >= args.length) continue;
            var arg = args[next++];
            if (conv === "s") { out += String(arg); continue; }

            var n = Number(arg);
            if (isNaN(n)) n = 0;
            var text, negative;
            if (conv === "x") {
                var whole16 = Math.trunc(n);
                text = (whole16 < 0 ? (whole16 >>> 0) : Math.abs(whole16)).toString(16).toUpperCase();
                out += padStart(text, width, zero ? "0" : " ");
                continue;
            }
            var seps = sep < 0 ? ["", "."] : SEPARATORS[sep];
            if (conv === "d") {
                var whole = Math.trunc(n);
                negative = whole < 0;
                text = groupDigits(fixedParts(Math.abs(whole), 0)[0], seps[0]);
            } else {
                var parts = fixedParts(Math.abs(n), precision);
                negative = n < 0 && /[1-9]/.test(parts[0] + parts[1]);
                text = groupDigits(parts[0], seps[0]) + (parts[1] !== "" || hash ? seps[1] + parts[1] : "");
            }
            var sign = negative ? "-" : plus ? "+" : space ? " " : "";
            if (zero) text = padStart(text, width - sign.length, "0");
            out += padStart(sign + text, width, " ");
        }
        return out;
    }

    // ---------------------------------------------------------------- util.printx

    function printx(cFormat, cSource) {
        var fmt = String(cFormat), src = cSource === undefined || cSource === null ? "" : String(cSource);
        var out = "", i = 0, caseMode = 0, escaped = false;
        function cased(c) { return caseMode === 1 ? c.toUpperCase() : caseMode === 2 ? c.toLowerCase() : c; }
        function isAlpha(c) { return (c >= "a" && c <= "z") || (c >= "A" && c <= "Z"); }
        function isDigit(c) { return c >= "0" && c <= "9"; }
        for (var k = 0; k < fmt.length; k++) {
            var command = fmt.charAt(k);
            if (escaped) { out += command; escaped = false; continue; }
            if (i >= src.length) break;
            var c;
            switch (command) {
                case "?": out += cased(src.charAt(i++)); break;
                case "X": while (i < src.length) { c = src.charAt(i++); if (isAlpha(c) || isDigit(c)) { out += cased(c); break; } } break;
                case "A": while (i < src.length) { c = src.charAt(i++); if (isAlpha(c)) { out += cased(c); break; } } break;
                case "9": while (i < src.length) { c = src.charAt(i++); if (isDigit(c)) { out += c; break; } } break;
                case "*": while (i < src.length) out += cased(src.charAt(i++)); break;
                case "\\": escaped = true; break;
                case ">": caseMode = 1; break;
                case "<": caseMode = 2; break;
                case "=": caseMode = 0; break;
                default: out += command;
            }
        }
        return out;
    }

    // ---------------------------------------------------------------- dates

    var MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    var DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    var DATE_FORMATS = ["m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd", "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM"];
    var TIME_FORMATS = ["HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt"];

    function two(n) { return n < 10 ? "0" + n : String(n); }

    function startsAt(s, i, token) { return s.substring(i, i + token.length) === token; }

    // The date tokens of a format, longest first within each letter: [token, field].
    var TOKENS = [
        ["yyyy", "y"], ["yy", "y"], ["mmmm", "m"], ["mmm", "m"], ["mm", "m"], ["m", "m"],
        ["dddd", "w"], ["ddd", "w"], ["dd", "d"], ["d", "d"], ["HH", "H"], ["H", "H"], ["hh", "h"], ["h", "h"],
        ["MM", "M"], ["M", "M"], ["ss", "s"], ["s", "s"], ["tt", "t"], ["t", "t"]
    ];

    function tokenAt(fmt, i) {
        for (var k = 0; k < TOKENS.length; k++) if (startsAt(fmt, i, TOKENS[k][0])) return TOKENS[k];
        return null;
    }

    function tokenText(token, d) {
        var h12 = d.getHours() % 12 === 0 ? 12 : d.getHours() % 12;
        switch (token) {
            case "yyyy": return padStart(String(d.getFullYear()), 4, "0");
            case "yy": return two(d.getFullYear() % 100);
            case "mmmm": return MONTHS[d.getMonth()];
            case "mmm": return MONTHS[d.getMonth()].substring(0, 3);
            case "mm": return two(d.getMonth() + 1);
            case "m": return String(d.getMonth() + 1);
            case "dddd": return DAYS[d.getDay()];
            case "ddd": return DAYS[d.getDay()].substring(0, 3);
            case "dd": return two(d.getDate());
            case "d": return String(d.getDate());
            case "HH": return two(d.getHours());
            case "H": return String(d.getHours());
            case "hh": return two(h12);
            case "h": return String(h12);
            case "MM": return two(d.getMinutes());
            case "M": return String(d.getMinutes());
            case "ss": return two(d.getSeconds());
            case "s": return String(d.getSeconds());
            case "tt": return d.getHours() < 12 ? "am" : "pm";
            case "t": return d.getHours() < 12 ? "a" : "p";
        }
        return token;
    }

    function asDate(v) {
        if (v instanceof Date) return v;
        if (typeof v === "number") return new Date(v);
        if (typeof v === "string") {
            // Only the ISO form goes to Date.parse, which reads any other text in the host's culture.
            if (/^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2})?)?$/.test(v)) return new Date(Date.parse(v));
            return parseDate(v, "m/d/yyyy H:MM:ss");
        }
        return new Date();
    }

    // util.printd: Acrobat's tokens; a quote is an ordinary character, the tokens between quotes
    // still written (Acrobat: 'd' is '4'); a backslash copies the character after it, a backslash
    // too (Acrobat writes nothing for \\); a letter that starts no token is copied (Acrobat drops a
    // lone y); formats 0, 1 and 2 are Acrobat's fixed forms.
    function printd(cFormat, oDate) {
        var d = asDate(oDate);
        if (!d || isNaN(d.getTime())) return "";
        if (typeof cFormat === "number") {
            var stamp = d.getFullYear() + two(d.getMonth() + 1) + two(d.getDate()) + two(d.getHours()) + two(d.getMinutes()) + two(d.getSeconds());
            var clock = two(d.getHours()) + ":" + two(d.getMinutes()) + ":" + two(d.getSeconds());
            if (cFormat === 0) return "D:" + stamp;
            if (cFormat === 1) return d.getFullYear() + "." + two(d.getMonth() + 1) + "." + two(d.getDate()) + " " + clock + " ";
            if (cFormat === 2) return two(d.getDate()) + "/" + two(d.getMonth() + 1) + "/" + d.getFullYear() + " " + clock + " ";
        }
        var fmt = String(cFormat === undefined || cFormat === null ? "yyyy-mm-dd" : cFormat);
        var out = "";
        for (var i = 0; i < fmt.length; ) {
            var c = fmt.charAt(i);
            if (c === "\\" && i + 1 < fmt.length) { out += fmt.charAt(i + 1); i += 2; continue; }
            var t = tokenAt(fmt, i);
            if (t) { out += tokenText(t[0], d); i += t[0].length; }
            else { out += c; i++; }
        }
        return out;
    }

    function monthByName(word) {
        var w = word.toLowerCase();
        if (w.length < 3) return -1;
        if (w === "sept") return 8;
        for (var k = 0; k < 12; k++) {
            var name = MONTHS[k].toLowerCase();
            if (name === w || name.substring(0, 3) === w) return k;
        }
        return -1;
    }

    // Reads a date the way the AF functions and util.scand do, as Acrobat answers: the format fixes
    // the order of its fields, and the text's numbers fill them in that order — a month by name where
    // the text spells one, the current year when the text has none, a time's fields 0 when the text
    // stops short, the year 2000 + yy below 50 and 1900 + yy from 50; a text with numbers left over,
    // or without its month or day, is no date. A format with no date field reads today at the time
    // the text gives (Acrobat's util.scand answers null).
    function parseDate(cDate, cFormat) {
        if (cDate === null || cDate === undefined) return null;
        var text = String(cDate), fmt = String(cFormat === undefined || cFormat === null ? "" : cFormat);
        if (text.length === 0 || text.length > MAX_DATE_TEXT || fmt.length > MAX_DATE_TEXT) return null;

        var fields = [];
        for (var i = 0; i < fmt.length; ) {
            var c = fmt.charAt(i);
            if (c === "\\") { i += 2; continue; }
            var t = tokenAt(fmt, i);
            if (t) { if (t[1] !== "w") fields.push(t[1]); i += t[0].length; } else i++;
        }

        var numbers = [], month = -1, pm = null;
        for (var j = 0; j < text.length; ) {
            var ch = text.charAt(j);
            if (ch >= "0" && ch <= "9") {
                var k = j;
                while (k < text.length && text.charAt(k) >= "0" && text.charAt(k) <= "9") k++;
                numbers.push(Number(text.substring(j, Math.min(k, j + 9))));
                j = k;
            } else if ((ch >= "a" && ch <= "z") || (ch >= "A" && ch <= "Z")) {
                var e = j;
                while (e < text.length && /[A-Za-z]/.test(text.charAt(e))) e++;
                var word = text.substring(j, e).toLowerCase();
                if (word === "am" || word === "a") pm = false;
                else if (word === "pm" || word === "p") pm = true;
                else if (month < 0) month = monthByName(word);
                j = e;
            } else j++;
        }

        var now = new Date();
        var y = null, m = null, d = null, H = 0, M = 0, s = 0, twoDigitYear = false, n = 0;
        var hasDate = false;
        for (var f = 0; f < fields.length; f++) {
            var field = fields[f];
            if (field === "t") continue;
            if (field === "m" && month >= 0) { m = month + 1; hasDate = true; continue; }
            var isDate = field === "y" || field === "m" || field === "d";
            if (isDate) hasDate = true;
            if (n >= numbers.length) { if (field === "m" || field === "d") return null; continue; }
            var v = numbers[n++];
            if (field === "y") { y = v; twoDigitYear = v < 100; }
            else if (field === "m") m = v;
            else if (field === "d") d = v;
            else if (field === "H" || field === "h") H = v;
            else if (field === "M") M = v;
            else if (field === "s") s = v;
        }
        if (!hasDate && fields.length === 0) return null;
        if (n < numbers.length) return null;
        if (y === null) y = now.getFullYear(); else if (twoDigitYear) y += y < 50 ? 2000 : 1900;
        if (m === null) m = hasDate ? 1 : now.getMonth() + 1;
        if (d === null) d = hasDate ? 1 : now.getDate();
        if (pm === true && H < 12) H += 12;
        if (pm === false && H === 12) H = 0;
        if (m < 1 || m > 12 || d < 1 || d > new Date(y, m, 0).getDate() || H > 23 || M > 59 || s > 59) return null;
        var date = new Date(y, m - 1, d, H, M, s);
        date.setFullYear(y);
        return date;
    }

    // ---------------------------------------------------------------- Matrix2D

    // Acrobat's 2-D transform object (its bundled scripts define it; the reference does not): the
    // fields a b c d h v of [a b 0; c d 0; h v 1], each a property of the matrix with its methods
    // beside them. concat and invert answer a new matrix; invert of a singular one is the identity;
    // transform maps a point [x, y], a list of points, or a flat list of coordinates. fromRotated is
    // the page's rotated user space into default user space (rotatedToDefault); where Acrobat
    // throws (a page the document lacks) the identity is answered. rotate, scale and translate
    // compose a rotation (radians), a scale and a translation after the matrix.
    function matrixConcat(m) {
        var o = m || {};
        var a2 = Number(o.a), b2 = Number(o.b), c2 = Number(o.c), d2 = Number(o.d), h2 = Number(o.h) || 0, v2 = Number(o.v) || 0;
        return new Matrix2D(
            this.a * a2 + this.b * c2, this.a * b2 + this.b * d2,
            this.c * a2 + this.d * c2, this.c * b2 + this.d * d2,
            this.h * a2 + this.v * c2 + h2, this.h * b2 + this.v * d2 + v2);
    }

    function matrixInvert() {
        var det = this.a * this.d - this.b * this.c;
        if (det === 0 || !isFinite(det)) return new Matrix2D();
        return new Matrix2D(this.d / det, -this.b / det, -this.c / det, this.a / det,
            (this.c * this.v - this.d * this.h) / det, (this.b * this.h - this.a * this.v) / det);
    }

    function matrixTransform(pts) {
        var m = this;
        function point(x, y) { x = Number(x); y = Number(y); return [m.a * x + m.c * y + m.h, m.b * x + m.d * y + m.v]; }
        if (!pts || typeof pts.length !== "number") return [];
        if (pts.length > 0 && pts[0] && typeof pts[0] === "object") {
            var list = [];
            for (var i = 0; i < pts.length; i++) list.push(point(pts[i][0], pts[i][1]));
            return list;
        }
        var flat = [];
        for (var j = 0; j + 1 < pts.length; j += 2) { var p = point(pts[j], pts[j + 1]); flat.push(p[0], p[1]); }
        return flat;
    }

    // A page's rotated user space — what the viewer shows, the space of getPageBox, addLink and a
    // field's rect — into default user space. Acrobat's addLink example inverts this matrix to take
    // default-space quads into rotated space, and Thom Parker's published figures apply it the other
    // way; both place the origin at the displayed crop box's lower-left corner. For a crop box
    // [x0 y0 x1 y1] under /Rotate 0, 90, 180 and 270 that is [1 0 0 1 x0 y0], [0 1 -1 0 x1 y0],
    // [-1 0 0 -1 x1 y1] and [0 -1 1 0 x0 y1]. A page the document lacks answers the identity.
    function rotatedToDefault(page) {
        var json = host.PageBox(page, "Crop");
        if (json === null || json === undefined) return new Matrix2D();
        var b = JSON.parse(json), x0 = b[0], y0 = b[1], x1 = b[2], y1 = b[3];
        switch (host.PageRotation(page)) {
            case 90: return new Matrix2D(0, 1, -1, 0, x1, y0);
            case 180: return new Matrix2D(-1, 0, 0, -1, x1, y1);
            case 270: return new Matrix2D(0, -1, 1, 0, x0, y1);
            default: return new Matrix2D(1, 0, 0, 1, x0, y0);
        }
    }

    function matrixFromRotated(oDoc, nPage) { return rotatedToDefault(pageArgument(nPage)); }
    function matrixRotate(theta) { var t = Number(theta) || 0; return this.concat(new Matrix2D(Math.cos(t), Math.sin(t), -Math.sin(t), Math.cos(t), 0, 0)); }
    function matrixScale(sx, sy) { var x = Number(sx); return this.concat(new Matrix2D(x, 0, 0, sy === undefined ? x : Number(sy), 0, 0)); }
    function matrixTranslate(dx, dy) { return this.concat(new Matrix2D(1, 0, 0, 1, Number(dx) || 0, Number(dy) || 0)); }

    function Matrix2D(a, b, c, d, h, v) {
        this.a = a === undefined ? 1 : Number(a);
        this.b = b === undefined ? 0 : Number(b);
        this.c = c === undefined ? 0 : Number(c);
        this.d = d === undefined ? 1 : Number(d);
        this.h = h === undefined ? 0 : Number(h);
        this.v = v === undefined ? 0 : Number(v);
        this.concat = matrixConcat;
        this.invert = matrixInvert;
        this.transform = matrixTransform;
        this.fromRotated = matrixFromRotated;
        this.rotate = matrixRotate;
        this.scale = matrixScale;
        this.translate = matrixTranslate;
    }

    // ---------------------------------------------------------------- streams

    function charSetOf(cCharSet) {
        var c = String(cCharSet === undefined || cCharSet === null ? "utf-8" : cCharSet).toLowerCase();
        if (c === "utf-8" || c === "utf8") return "utf-8";
        if (c === "utf-16" || c === "utf-16be" || c === "ucs-2") return "utf-16";
        if (c === "iso-8859-1" || c === "latin1" || c === "latin-1") return "latin-1";
        record("character set " + show(cCharSet).substring(0, 40) + " read as utf-8");
        return "utf-8";
    }

    // A stream's bytes, as text in cCharSet (utf-8 when none is named), as Acrobat's util.stringFromStream.
    function stringFromStream(oStream, cCharSet) {
        if (!oStream || typeof oStream.read !== "function") throw apiError("TypeError", "Not a stream.");
        var hex = String(oStream.read());
        var set = charSetOf(cCharSet), out = "", i;
        if (set === "latin-1") {
            for (i = 0; i + 1 < hex.length; i += 2) out += String.fromCharCode(parseInt(hex.substring(i, i + 2), 16));
            return out;
        }
        if (set === "utf-16") {
            for (i = hex.substring(0, 4).toLowerCase() === "feff" ? 4 : 0; i + 3 < hex.length; i += 4)
                out += String.fromCharCode(parseInt(hex.substring(i, i + 4), 16));
            return out;
        }
        var escaped = "";
        for (i = 0; i + 1 < hex.length; i += 2) escaped += "%" + hex.substring(i, i + 2);
        try { return decodeURIComponent(escaped); }
        catch (e) {
            // Not well-formed UTF-8: each byte as itself.
            for (i = 0; i + 1 < hex.length; i += 2) out += String.fromCharCode(parseInt(hex.substring(i, i + 2), 16));
            return out;
        }
    }

    function streamFromString(cString, cCharSet) {
        var s = String(cString), set = charSetOf(cCharSet), hex = "", i, c;
        if (set === "latin-1") {
            for (i = 0; i < s.length; i++) { c = s.charCodeAt(i) & 0xFF; hex += (c < 16 ? "0" : "") + c.toString(16); }
        } else if (set === "utf-16") {
            for (i = 0; i < s.length; i++) { c = s.charCodeAt(i).toString(16); hex += "0000".substring(c.length) + c; }
        } else {
            var bytes;
            try { bytes = unescape(encodeURIComponent(s)); }
            catch (e) { bytes = s.replace(/[\uD800-\uDFFF]/g, "�"); bytes = unescape(encodeURIComponent(bytes)); }   // a lone surrogate
            for (i = 0; i < bytes.length; i++) { c = bytes.charCodeAt(i); hex += (c < 16 ? "0" : "") + c.toString(16); }
        }
        return readStream(hex);
    }

    // ---------------------------------------------------------------- util

    var util = {
        printf: printf,
        printx: printx,
        printd: printd,
        scand: function (cFormat, cDate) { return parseDate(cDate, cFormat); },
        crackURL: function (cURL) {
            var url = String(cURL);
            if (url.length > MAX_URL) throw apiError("RangeError", "The URL is longer than " + MAX_URL + " characters.");
            var r = { cScheme: "", cUser: "", cPassword: "", cHost: "", nPort: 0, cPath: "", cQuery: "", cFragment: "" };
            var rest = url;
            var hashAt = rest.indexOf("#");
            if (hashAt >= 0) { r.cFragment = rest.substring(hashAt + 1); rest = rest.substring(0, hashAt); }
            var queryAt = rest.indexOf("?");
            if (queryAt >= 0) { r.cQuery = rest.substring(queryAt + 1); rest = rest.substring(0, queryAt); }
            var schemeAt = rest.indexOf("://");
            if (schemeAt < 0) throw apiError("TypeError", "Not a URL: " + url.substring(0, 80));
            r.cScheme = rest.substring(0, schemeAt).toLowerCase();
            rest = rest.substring(schemeAt + 3);
            var slash = rest.indexOf("/");
            var authority = slash < 0 ? rest : rest.substring(0, slash);
            r.cPath = slash < 0 ? "" : rest.substring(slash);
            var at = authority.lastIndexOf("@");
            if (at >= 0) {
                var user = authority.substring(0, at), colonAt = user.indexOf(":");
                r.cUser = colonAt < 0 ? user : user.substring(0, colonAt);
                r.cPassword = colonAt < 0 ? "" : user.substring(colonAt + 1);
                authority = authority.substring(at + 1);
            }
            var portAt = authority.lastIndexOf(":");
            if (portAt >= 0 && /^\d{1,5}$/.test(authority.substring(portAt + 1))) {
                r.nPort = Number(authority.substring(portAt + 1));
                authority = authority.substring(0, portAt);
            } else r.nPort = r.cScheme === "https" ? 443 : r.cScheme === "http" ? 80 : 0;
            r.cHost = authority;
            return r;
        },
        byteToChar: function (n) { return String.fromCharCode(Number(n) & 0xFF); },
        stringFromStream: function (oStream, cCharSet) { return stringFromStream(oStream, cCharSet); },
        streamFromString: function (cString, cCharSet) { return streamFromString(cString, cCharSet); },
        readFileIntoStream: notRun("util.readFileIntoStream", null),
        iconStreamFromIcon: notRun("util.iconStreamFromIcon", null),
        spansToXML: notRun("util.spansToXML", null),
        xmlToSpans: notRun("util.xmlToSpans", null)
    };

    // ---------------------------------------------------------------- fields

    var FLAG_READONLY = 1, FLAG_REQUIRED = 2;
    var TEXT_MULTILINE = 1 << 12, TEXT_PASSWORD = 1 << 13, TEXT_FILESELECT = 1 << 20, DO_NOT_SPELLCHECK = 1 << 22,
        DO_NOT_SCROLL = 1 << 23, TEXT_COMB = 1 << 24, RICH_TEXT = 1 << 25, RADIOS_IN_UNISON = 1 << 25,
        COMBO_EDIT = 1 << 18, LIST_MULTISELECT = 1 << 21, COMMIT_ON_SEL_CHANGE = 1 << 26;

    var overrides = {};   // per field: what the document's scripts set, kept for the scripts to read back
    var describeCache = {};

    function describe(name) {
        if (own(describeCache, name)) return describeCache[name];
        var json = host.Describe(name);
        var d = json ? JSON.parse(json) : null;
        describeCache[name] = d;
        return d;
    }

    function overridesOf(name) {
        if (!own(overrides, name)) overrides[name] = {};
        return overrides[name];
    }

    // Acrobat's field value: a number when the stored text is one ('12.5', '007'), the text otherwise.
    // The pattern is unambiguous (the fraction is one optional group after the point, never a second
    // digit run beside the first) and runs only under the length cap.
    function typedValue(text) {
        if (text === null || text === undefined) return "";
        if (text.length > 0 && text.length <= 400 && /^[+-]?(\d+(\.\d*)?|\.\d+)$/.test(text)) return Number(text);
        return text;
    }

    function valueText(v) {
        if (v === null || v === undefined) return "";
        if (typeof v === "object" && typeof v.length === "number") return Array.prototype.join.call(v, ",");
        return String(v);
    }

    function typeOf(d) { return d ? d.type : "text"; }
    function flagsOf(d) { return d ? d.flags : 0; }
    function isButtonLike(t) { return t === "button" || t === "checkbox" || t === "radiobutton"; }

    function itemsOf(name, d) {
        var o = overridesOf(name);
        if (own(o, "__items")) return o.__items;
        return d && d.items ? d.items : [];
    }

    // The field's own item list, copied from the document once and changed in place after that.
    function localItems(name, d) {
        var o = overridesOf(name);
        if (!own(o, "__items")) o.__items = (d && d.items ? d.items : []).slice(0);
        return o.__items;
    }

    // Every field object shares one prototype; an object carries only its state — a getField in a
    // loop costs an object, not ninety closures.
    var FieldProto = {};

    function stateOf(f) {
        var s = f && f.__tetitulzState;
        if (!s) throw apiError("TypeError", "Not a field object.");
        return s;
    }

    // A property of every field. Acrobat throws InvalidGetError for one the field's type lacks (a
    // text property on a button, a button's on a text field); here it answers its default, so a
    // script written loosely keeps running. Setting a property this engine does not carry into the
    // document is recorded; setting a read-only one (type, page, numItems) is recorded and ignored.
    function fieldProp(key, read, settable) {
        Object.defineProperty(FieldProto, key, {
            get: function () {
                var s = stateOf(this);
                return own(s.o, key) ? s.o[key] : read(s);
            },
            set: function (v) {
                var s = stateOf(this);
                if (settable === false) { record(s.shown + "." + key + " = " + show(v) + " not applied (read-only)"); return; }
                s.o[key] = v;
                // A state the host may apply to its own handling of the field (a viewer's editor,
                // its hit testing) is handed over as well as recorded; the document is not changed.
                if (key === "readonly" || key === "required" || key === "hidden" || key === "display")
                    host.FieldState(String(s.name), key, String(v));
                record(s.shown + "." + key + " = " + show(v));
            },
            configurable: true, enumerable: true
        });
    }

    function fieldMethod(key, f) { defineValue(FieldProto, key, f); }

    // A method that would reach outside the document, or that this engine does not carry into it:
    // notRun, named after the field it is called on.
    function fieldNotRun(key, result) {
        fieldMethod(key, function () { return notRun(stateOf(this).shown + "." + key, result).apply(this, arguments); });
    }

    Object.defineProperty(FieldProto, "name", { get: function () { return stateOf(this).name; }, enumerable: true });

    // A multi-select list box's selections travel to the host joined by MULTI_SEPARATOR; Acrobat
    // answers several as an array and one as itself.
    function isMultiSelect(s) { return s.d !== null && s.d.type === "listbox" && (s.d.flags & LIST_MULTISELECT) !== 0; }

    Object.defineProperty(FieldProto, "value", {
        get: function () {
            var s = stateOf(this), v = host.GetValue(s.name);
            // A check box or radio with nothing selected is Off.
            if ((v === null || v === "") && s.d && (s.d.type === "checkbox" || s.d.type === "radiobutton")) return "Off";
            if (typeof v === "string" && v.indexOf(MULTI_SEPARATOR) >= 0 && isMultiSelect(s)) {
                var parts = v.split(MULTI_SEPARATOR), out = [];
                for (var i = 0; i < parts.length; i++) if (parts[i] !== "") out.push(typedValue(parts[i]));
                return out;
            }
            return typedValue(v);
        },
        set: function (v) {
            var s = stateOf(this);
            var text = v !== null && typeof v === "object" && typeof v.length === "number" && isMultiSelect(s)
                ? Array.prototype.join.call(v, MULTI_SEPARATOR) : valueText(v);
            host.SetValue(s.name, text);
        },
        enumerable: true
    });
    Object.defineProperty(FieldProto, "valueAsString", {
        get: function () { var v = host.GetValue(stateOf(this).name); return v === null || v === undefined ? "" : v; },
        set: function (v) { record(stateOf(this).shown + ".valueAsString = " + show(v) + " not applied (read-only)"); },
        enumerable: true
    });
    Object.defineProperty(FieldProto, "doc", { get: function () { return doc; }, enumerable: true });

    // A field name that only parents others (grp of grp.a) has no type of its own (Acrobat: "").
    fieldProp("type", function (s) { return s.d ? s.d.type : s.parent ? "" : "text"; }, false);
    fieldProp("rect", function (s) { return s.w ? s.w.slice(0, 4) : s.d ? s.d.rect.slice(0) : [0, 0, 0, 0]; });
    fieldProp("page", function (s) {
        if (s.w) return s.w[4];
        if (!s.d) return 0;
        return s.d.pages.length === 1 ? s.d.pages[0] : s.d.pages.length === 0 ? -1 : s.d.pages.slice(0);
    }, false);
    fieldProp("readonly", function (s) { return (flagsOf(s.d) & FLAG_READONLY) !== 0; });
    fieldProp("required", function (s) { return (flagsOf(s.d) & FLAG_REQUIRED) !== 0; });
    fieldProp("multiline", function (s) { return (flagsOf(s.d) & TEXT_MULTILINE) !== 0; });
    fieldProp("password", function (s) { return (flagsOf(s.d) & TEXT_PASSWORD) !== 0; });
    fieldProp("fileSelect", function (s) { return (flagsOf(s.d) & TEXT_FILESELECT) !== 0; });
    fieldProp("doNotSpellCheck", function (s) { return (flagsOf(s.d) & DO_NOT_SPELLCHECK) !== 0; });
    fieldProp("doNotScroll", function (s) { return (flagsOf(s.d) & DO_NOT_SCROLL) !== 0; });
    fieldProp("comb", function (s) { return (flagsOf(s.d) & TEXT_COMB) !== 0; });
    fieldProp("richText", function (s) { return typeOf(s.d) === "text" && (flagsOf(s.d) & RICH_TEXT) !== 0; });
    fieldProp("radiosInUnison", function (s) { return (flagsOf(s.d) & RADIOS_IN_UNISON) !== 0; });
    fieldProp("editable", function (s) { return (flagsOf(s.d) & COMBO_EDIT) !== 0; });
    fieldProp("multipleSelection", function (s) { return (flagsOf(s.d) & LIST_MULTISELECT) !== 0; });
    fieldProp("commitOnSelChange", function (s) { return (flagsOf(s.d) & COMMIT_ON_SEL_CHANGE) !== 0; });
    fieldProp("charLimit", function (s) { return s.d ? s.d.maxLength : 0; });
    // A check box or radio with no /DV defaults to Off, as Acrobat answers.
    fieldProp("defaultValue", function (s) {
        if (!s.d) return "";
        if (s.d.defaultValue === "" && (s.d.type === "checkbox" || s.d.type === "radiobutton")) return "Off";
        return typedValue(s.d.defaultValue);
    });
    fieldProp("exportValues", function (s) { return s.d ? s.d.onStates.slice(0) : []; });
    fieldProp("numItems", function (s) { return itemsOf(s.name, s.d).length; }, false);
    fieldProp("currentValueIndices", function (s) {
        var items = itemsOf(s.name, s.d), v = host.GetValue(s.name);
        var wanted = typeof v === "string" && isMultiSelect(s) ? v.split(MULTI_SEPARATOR) : [v], found = [];
        for (var w = 0; w < wanted.length; w++)
            for (var i = 0; i < items.length; i++)
                if (items[i][1] === wanted[w] || items[i][0] === wanted[w]) { found.push(i); break; }
        // Acrobat: one index for one selection, an array for several, -1 for none.
        return found.length === 0 ? -1 : found.length === 1 ? found[0] : found.sort(function (a, b) { return a - b; });
    });
    fieldProp("userName", function (s) { return s.d ? s.d.userName : ""; });
    fieldProp("submitName", function () { return ""; });
    fieldProp("calcOrderIndex", function () { return -1; });
    fieldProp("display", function (s) { return own(s.o, "hidden") && s.o.hidden ? display.hidden : display.visible; });
    fieldProp("hidden", function (s) { return own(s.o, "display") ? s.o.display === display.hidden : false; });
    fieldProp("print", function () { return true; });
    fieldProp("textColor", function () { return color.black; });
    fieldProp("fillColor", function () { return color.transparent; });
    fieldProp("strokeColor", function () { return color.transparent; });
    // The font and size the field's /DA sets (Helvetica, at 0 — auto — when it sets none); a check
    // box or radio draws its mark in ZapfDingbats whatever the form's default, as Acrobat answers.
    fieldProp("textFont", function (s) {
        if (s.d && (s.d.type === "checkbox" || s.d.type === "radiobutton")) return font.ZapfD;
        var r = s.d ? s.d.fontResource : "";
        if (r && own(DA_FONTS, r)) return DA_FONTS[r];
        return r || font.Helv;
    });
    fieldProp("textSize", function (s) { return s.d ? s.d.fontSize : 0; });
    fieldProp("lineWidth", function () { return 1; });
    fieldProp("borderStyle", function () { return border.s; });
    fieldProp("alignment", function () { return "left"; });
    fieldProp("rotation", function () { return 0; });
    fieldProp("delay", function () { return false; });
    // The check style its /MK /CA character draws (ZapfDingbats 4 is the check, l the circle); a
    // radio without one is a circle, a check box a check, as Acrobat answers.
    fieldProp("style", function (s) {
        var w = s.w || (s.d && s.d.widgets.length ? s.d.widgets[0] : null);
        var ca = w && w[5] ? w[5][0] : "";
        if (ca && own(CHECK_STYLES, ca)) return CHECK_STYLES[ca];
        return s.d && s.d.type === "radiobutton" ? style.ci : style.ch;
    });
    fieldProp("highlight", function () { return highlight.i; });
    fieldProp("buttonPosition", function () { return position.textOnly; });
    fieldProp("buttonAlignX", function () { return 50; });
    fieldProp("buttonAlignY", function () { return 50; });
    fieldProp("buttonFitBounds", function () { return false; });
    fieldProp("buttonScaleHow", function () { return scaleHow.proportional; });
    fieldProp("buttonScaleWhen", function () { return scaleWhen.always; });
    fieldProp("defaultStyle", function () { return {}; });
    fieldProp("richValue", function () { return []; });

    fieldMethod("getArray", function () {
        var s = stateOf(this);
        if (s.d) return [this];
        var kids = [], names = fieldNames(), prefix = s.name + ".";
        for (var i = 0; i < names.length; i++)
            if (names[i].length > prefix.length && names[i].substring(0, prefix.length) === prefix) kids.push(getFieldByName(names[i]));
        return kids.length ? kids : [this];
    });
    fieldMethod("getItemAt", function (nIdx, bExportValue) {
        var s = stateOf(this), items = itemsOf(s.name, s.d), i = Number(nIdx);
        if (i < 0) i = items.length - 1;
        if (items.length === 0 || isNaN(i)) return undefined;
        if (i >= items.length) i = items.length - 1;   // Acrobat: past the end is the last item
        return bExportValue === false ? items[i][0] : items[i][1];
    });
    fieldMethod("setItems", function (oArray) {
        var s = stateOf(this);
        var n = oArray && typeof oArray.length === "number" ? oArray.length : 0;
        if (n > MAX_CHOICE_ITEMS) throw apiError("RangeError", "A choice field holds at most " + MAX_CHOICE_ITEMS + " items.");
        var list = [];
        for (var i = 0; i < n; i++) {
            var it = oArray[i];
            list.push(it && typeof it === "object" && typeof it.length === "number" ? [String(it[0]), String(it.length > 1 ? it[1] : it[0])] : [String(it), String(it)]);
        }
        s.o.__items = list;
        record(s.shown + ".setItems(" + n + " items) not applied to the document");
    });
    fieldMethod("clearItems", function () { var s = stateOf(this); s.o.__items = []; record(s.shown + ".clearItems() not applied to the document"); });
    fieldMethod("insertItemAt", function (cName, cExport, nIdx) {
        var s = stateOf(this), items = localItems(s.name, s.d);
        if (items.length >= MAX_CHOICE_ITEMS) throw apiError("RangeError", "A choice field holds at most " + MAX_CHOICE_ITEMS + " items.");
        var at = nIdx === undefined ? 0 : Math.max(0, Math.min(items.length, Number(nIdx) || 0));
        items.splice(at, 0, [String(cName), String(cExport === undefined ? cName : cExport)]);
        record(s.shown + ".insertItemAt(" + show(cName) + ") not applied to the document");
    });
    fieldMethod("deleteItemAt", function (nIdx) {
        var s = stateOf(this), items = localItems(s.name, s.d);
        var at = nIdx === undefined ? 0 : Number(nIdx) || 0;
        if (at >= 0 && at < items.length) items.splice(at, 1);
        record(s.shown + ".deleteItemAt(" + show(nIdx) + ") not applied to the document");
    });
    fieldMethod("isBoxChecked", function (nWidget) {
        var s = stateOf(this), states = s.d ? s.d.onStates : [], v = host.GetValue(s.name), i = Number(nWidget) || 0;
        // Acrobat answers 1 or 0.
        return (states.length > i ? v === states[i] : v !== null && v !== "" && v !== "Off") ? 1 : 0;
    });
    fieldMethod("isDefaultChecked", function (nWidget) {
        var s = stateOf(this), states = s.d ? s.d.onStates : [], i = Number(nWidget) || 0;
        return s.d !== null && states.length > i && s.d.defaultValue === states[i];
    });
    fieldMethod("checkThisBox", function (nWidget, bCheckIt) {
        var s = stateOf(this), states = s.d ? s.d.onStates : [], i = Number(nWidget) || 0;
        host.SetValue(s.name, bCheckIt === false ? "Off" : states.length > i ? states[i] : "Yes");
    });
    fieldMethod("defaultIsChecked", function (nWidget) { record(stateOf(this).shown + ".defaultIsChecked(" + show(nWidget) + ") not applied to the document"); return true; });
    // nFace: 0 the normal caption (/MK /CA), 1 the down caption (/AC), 2 the rollover caption (/RC).
    fieldMethod("buttonGetCaption", function (nFace) {
        var s = stateOf(this), face = Number(nFace) || 0;
        if (own(s.o, "__caption" + face)) return s.o["__caption" + face];
        var w = s.w || (s.d && s.d.widgets.length ? s.d.widgets[0] : null);
        return w && w[5] ? w[5][face] || "" : "";
    });
    fieldMethod("buttonSetCaption", function (cCaption, nFace) {
        var s = stateOf(this);
        s.o["__caption" + (Number(nFace) || 0)] = String(cCaption);
        record(s.shown + ".buttonSetCaption(" + show(cCaption) + ") not applied to the document");
    });
    fieldMethod("setFocus", function () { record(stateOf(this).shown + ".setFocus()"); });
    fieldMethod("setAction", function (cTrigger) { record(stateOf(this).shown + ".setAction(" + show(cTrigger) + ") not applied to the document"); });
    fieldNotRun("buttonGetIcon", null);
    fieldNotRun("buttonSetIcon");
    fieldNotRun("buttonImportIcon", -1);
    fieldNotRun("browseForFileToSubmit");
    fieldNotRun("getLock", null);
    fieldNotRun("setLock", false);
    fieldNotRun("signatureGetModifications", null);
    fieldNotRun("signatureGetSeedValue", null);
    fieldNotRun("signatureInfo", null);
    fieldNotRun("signatureSetSeedValue");
    fieldNotRun("signatureSign", false);
    fieldNotRun("signatureValidate", -1);

    function makeField(name, d, widget, parent) {
        var field = Object.create(FieldProto);
        Object.defineProperty(field, "__tetitulzState", {
            value: Object.freeze({ name: name, d: d, w: d && widget !== undefined ? d.widgets[widget] : null, o: overridesOf(name), shown: shortName(name), parent: !!parent }),
            enumerable: false
        });
        return field;
    }

    var namesCache = null;
    function fieldNames() {
        if (namesCache === null) namesCache = JSON.parse(host.FieldNames());
        return namesCache;
    }

    // Acrobat answers null for a field the document lacks. When the host gave no fields at all, a
    // script still gets the field it names, so a context built from values alone keeps working.
    // The field objects of one execution, by name: a field's description does not change within
    // one, so a getField in a loop answers the object it answered before.
    var fieldCache = {};

    function getFieldByName(cName) {
        var name = String(cName);
        if (own(fieldCache, name)) return fieldCache[name];
        var field = findField(name);
        fieldCache[name] = field;
        return field;
    }

    function findField(name) {
        var d = describe(name);
        if (d === null && host.KnowsFields() && !host.HasField(name)) {
            // "name.N" names widget N of the field name (a scan: the name is the file's).
            var dot = name.lastIndexOf(".");
            if (dot > 0 && dot < name.length - 1 && name.length - dot <= 5) {
                var digits = name.substring(dot + 1), allDigits = true;
                for (var c = 0; c < digits.length; c++) if (digits.charAt(c) < "0" || digits.charAt(c) > "9") allDigits = false;
                var base = name.substring(0, dot), bd = allDigits ? describe(base) : null;
                if (bd !== null && Number(digits) < bd.widgets.length) return makeField(base, bd, Number(digits));
            }
            var names = fieldNames(), prefix = name + ".";
            for (var i = 0; i < names.length; i++) if (names[i].substring(0, prefix.length) === prefix) return makeField(name, null, undefined, true);
            return null;
        }
        return makeField(name, d);
    }

    // ---------------------------------------------------------------- the AF functions

    var AF = {};

    AF.AFExtractNums = function (str) {
        if (typeof str === "number") return [str];
        if (!str || typeof str !== "string") return null;
        var first = str.charAt(0);
        if (first === "." || first === ",") str = "0" + str;
        var numbers = str.match(/\d+/g);
        return numbers === null || numbers.length === 0 ? null : numbers;
    };

    // Acrobat: the digit groups joined with a point, negated when the text holds a minus anywhere.
    AF.AFMakeNumber = function (str) {
        if (typeof str === "number") return str;
        if (typeof str !== "string") return null;
        var nums = AF.AFExtractNums(str);
        if (nums === null) return null;
        var value = Number(nums.join("."));
        return str.indexOf("-") >= 0 ? -value : value;
    };

    AF.AFMakeArrayFromList = function (string) {
        return typeof string === "string" ? string.split(/, ?/) : string;
    };

    AF.AFMergeChange = function (ev) {
        var e = ev || event;
        var value = e.value === undefined || e.value === null ? "" : String(e.value);
        var change = e.change === undefined || e.change === null ? "" : String(e.change);
        if (e.willCommit) return value;
        var selStart = typeof e.selStart === "number" ? e.selStart : value.length;
        var selEnd = typeof e.selEnd === "number" ? e.selEnd : value.length;
        var start = Math.max(0, Math.min(selStart, value.length));
        var end = Math.max(start, Math.min(selEnd, value.length));
        return value.substring(0, start) + change + value.substring(end);
    };

    AF.AFParseDateEx = function (cString, cOrder) { return parseDate(cString, cOrder); };

    function checkDecimals(nDec, what) {
        var n = Number(nDec);
        if (!(n >= 0 && n <= MAX_DECIMALS))
            throw apiError("RangeError", what + " formats to between 0 and " + MAX_DECIMALS + " decimal places.");
        return Math.floor(n);
    }

    function sepStyleOf(sepStyle) { var s = Math.floor(Number(sepStyle) || 0); return s < 0 ? 0 : s > 4 ? 4 : s; }

    function targetField() { return event.target && event.target.__tetitulzState ? event.target : null; }

    AF.AFNumber_Format = function (nDec, sepStyle, negStyle, currStyle, strCurrency, bCurrencyPrepend) {
        var places = checkDecimals(nDec, "AFNumber_Format");
        var value = AF.AFMakeNumber(event.value);
        if (value === null || isNaN(value)) { event.value = ""; return; }
        var currency = strCurrency === undefined || strCurrency === null ? "" : String(strCurrency);
        var neg = Number(negStyle) || 0, prepend = !!bCurrencyPrepend;
        var sign = value < 0 ? -1 : value > 0 ? 1 : 0;
        var out = "";
        if (sign === -1 && prepend && neg === 0) out += "-";
        var paren = (neg === 2 || neg === 3) && sign === -1;
        if (paren) out += "(";
        if (prepend) out += currency;
        if ((neg !== 0 || prepend) && sign === -1) value = -value;
        out += printf("%," + sepStyleOf(sepStyle) + "." + places + "f", value);
        if (!prepend) out += currency;
        if (paren) out += ")";
        var field = targetField();
        if (field && (neg === 1 || neg === 3)) field.textColor = sign === -1 ? color.red : color.black;
        event.value = out;
    };

    function isNumberAsTyped(text, decimalSeparator, complete) {
        var digit = false, separator = false;
        for (var i = 0; i < text.length; i++) {
            var c = text.charAt(i);
            if (c >= "0" && c <= "9") digit = true;
            else if (c === "-" && i === 0) { }
            else if (c === decimalSeparator && !separator) separator = true;
            else return false;
        }
        return !complete || digit || text.length === 0;
    }

    AF.AFNumber_Keystroke = function (nDec, sepStyle, negStyle, currStyle, strCurrency, bCurrencyPrepend) {
        var s = sepStyleOf(sepStyle);
        if (!isNumberAsTyped(AF.AFMergeChange(event), s === 2 || s === 3 ? "," : ".", !!event.willCommit))
            event.rc = false;
    };

    AF.AFPercent_Format = function (nDec, sepStyle, bPercentPrepend) {
        var places = checkDecimals(nDec, "AFPercent_Format");
        var value = AF.AFMakeNumber(event.value);
        if (value === null || isNaN(value)) value = 0;
        var text = printf("%," + sepStyleOf(sepStyle) + "." + places + "f", value * 100);
        event.value = bPercentPrepend ? "%" + text : text + "%";
    };

    AF.AFPercent_Keystroke = function (nDec, sepStyle) { AF.AFNumber_Keystroke(nDec, sepStyle, 0, 0, "", true); };

    function specialMask(psf, value) {
        switch (AF.AFMakeNumber(psf)) {
            case 0: return "99999";
            case 1: return "99999-9999";
            case 2: return printx("9999999999", value).length >= 10 ? "(999) 999-9999" : "999-9999";
            case 3: return "999-99-9999";
        }
        throw apiError("RangeError", "Invalid psf in AFSpecial_Format");
    }

    AF.AFSpecial_Format = function (psf) {
        if (!event.value) return;
        var value = String(event.value);
        event.value = printx(specialMask(psf, value), value);
    };

    var MASK_CHECKS = {
        "9": function (c) { return c >= "0" && c <= "9"; },
        "A": function (c) { return (c >= "a" && c <= "z") || (c >= "A" && c <= "Z"); },
        "O": function (c) { return (c >= "a" && c <= "z") || (c >= "A" && c <= "Z") || (c >= "0" && c <= "9"); },
        "X": function () { return true; }
    };

    function fitsMask(value, mask) {
        for (var i = 0; i < value.length; i++) {
            var m = mask.charAt(i), c = value.charAt(i);
            if (own(MASK_CHECKS, m)) { if (!MASK_CHECKS[m](c)) return false; }
            else if (m !== c) return false;
        }
        return true;
    }

    function keystrokeMask(mask, value, warn) {
        if (!mask) return;
        if (value === null || value === undefined || value === "") value = AF.AFMergeChange(event);
        if (!value) return;
        var err = IDS_INVALID_VALUE + " = \"" + mask + "\"";
        function refuse() { if (warn) app.alert(err); event.rc = false; }
        if (value.length > mask.length) { refuse(); return; }
        if (event.willCommit) {
            if (value.length < mask.length || !fitsMask(value, mask)) { refuse(); return; }
            event.value = String(event.value) + mask.substring(value.length);
            return;
        }
        if (!fitsMask(value, mask.substring(0, value.length))) refuse();
    }

    AF.AFSpecial_KeystrokeEx = function (cMask) {
        var mask = String(cMask);
        keystrokeMask(mask.replace(/[^9AOX]/g, ""), null, false);
        if (event.rc) return;
        event.rc = true;
        keystrokeMask(mask, null, true);
    };

    AF.AFSpecial_Keystroke = function (psf) {
        var formats;
        switch (AF.AFMakeNumber(psf)) {
            case 0: formats = ["99999"]; break;
            case 1: formats = ["99999-9999"]; break;
            case 2: formats = ["999-9999", "(999) 999-9999"]; break;
            case 3: formats = ["999-99-9999"]; break;
            default: throw apiError("RangeError", "Invalid psf in AFSpecial_Keystroke");
        }
        var value = AF.AFMergeChange(event), i;
        for (i = 0; i < formats.length; i++) { keystrokeMask(formats[i], value, false); if (event.rc) return; event.rc = true; }
        var bare = value.replace(/[-()\s]+/g, "");
        for (i = 0; i < formats.length; i++) { keystrokeMask(formats[i].replace(/[-()\s]+/g, ""), bare, false); if (event.rc) return; event.rc = true; }
        var digits = bare.match(/\d/g);
        AF.AFSpecial_KeystrokeEx(formats.length > 1 && digits && digits.length > 7 ? formats[1] : formats[0]);
    };

    function dateFormatOf(f, table) { return typeof f === "number" ? table[f] : f; }

    // Acrobat: a date its format does not read formats as nothing.
    AF.AFDate_FormatEx = function (cFormat) {
        if (!event.value) return;
        var date = parseDate(String(event.value), cFormat);
        event.value = date === null ? "" : printd(cFormat, date);
    };
    AF.AFDate_Format = function (pdf) { AF.AFDate_FormatEx(dateFormatOf(pdf, DATE_FORMATS)); };

    AF.AFDate_KeystrokeEx = function (cFormat) {
        if (!event.willCommit) return;
        var value = AF.AFMergeChange(event);
        if (!value) return;
        if (parseDate(value, cFormat) === null) {
            var target = event.target && event.target.name ? "[ " + event.target.name + " ]" : "";
            app.alert(IDS_INVALID_DATE + " " + target + IDS_INVALID_DATE2 + cFormat);
            event.rc = false;
        }
    };
    AF.AFDate_Keystroke = function (pdf) { AF.AFDate_KeystrokeEx(dateFormatOf(pdf, DATE_FORMATS)); };

    AF.AFTime_FormatEx = function (cFormat) { AF.AFDate_FormatEx(cFormat); };
    AF.AFTime_Format = function (ptf) { AF.AFDate_FormatEx(dateFormatOf(ptf, TIME_FORMATS)); };
    AF.AFTime_Keystroke = function (ptf) { AF.AFDate_KeystrokeEx(dateFormatOf(ptf, TIME_FORMATS)); };

    AF.AFRange_Validate = function (bGreaterThan, nGreaterThan, bLessThan, nLessThan) {
        if (!event.value) return;
        var value = AF.AFMakeNumber(typeof event.value === "number" ? event.value : String(event.value));
        if (value === null || isNaN(value)) return;
        var gt = !!bGreaterThan, lt = !!bLessThan;
        var low = gt ? AF.AFMakeNumber(nGreaterThan) : null, high = lt ? AF.AFMakeNumber(nLessThan) : null;
        if ((gt && low === null) || (lt && high === null)) return;
        var err = "";
        if (gt && lt) { if (value < low || value > high) err = printf(IDS_GT_AND_LT, low, high); }
        else if (gt) { if (value < low) err = printf(IDS_GREATER_THAN, low); }
        else if (lt) { if (value > high) err = printf(IDS_LESS_THAN, high); }
        if (err) {
            app.alert(err);
            host.Rejected(err);
            event.rc = false;
        }
    };

    AF.AFSimple = function (cFunction, nValue1, nValue2) {
        var a = AF.AFMakeNumber(nValue1), b = AF.AFMakeNumber(nValue2);
        if (a === null) a = NaN;
        if (b === null) b = NaN;
        switch (cFunction) {
            case "AVG": return (a + b) / 2;
            case "SUM": return a + b;
            case "PRD": return a * b;
            case "MIN": return Math.min(a, b);
            case "MAX": return Math.max(a, b);
        }
        throw apiError("RangeError", "Invalid cFunction in AFSimple");
    };

    // As Acrobat answers: every field named counts, an empty or unreadable value as 0, and the result
    // is kept to six places. A name the document lacks is passed over (Acrobat stops the script).
    AF.AFSimple_Calculate = function (cFunction, cFields) {
        var names = AF.AFMakeArrayFromList(cFields);
        if (names === null || names === undefined) names = [];
        if (typeof names === "string" || typeof names.length !== "number") names = [names];
        var values = [];
        for (var i = 0; i < names.length; i++) {
            var f = getFieldByName(String(names[i]));
            if (f === null) continue;
            var kids = f.getArray();
            for (var k = 0; k < kids.length; k++) {
                var v = AF.AFMakeNumber(kids[k].value);
                values.push(v === null || isNaN(v) ? 0 : v);
            }
        }
        var result = 0, j;
        switch (String(cFunction).toUpperCase()) {
            case "SUM": for (j = 0; j < values.length; j++) result += values[j]; break;
            case "AVG": for (j = 0; j < values.length; j++) result += values[j]; result = values.length ? result / values.length : 0; break;
            case "PRD": result = 1; for (j = 0; j < values.length; j++) result *= values[j]; break;
            case "MIN": result = values.length ? Math.min.apply(null, values) : 0; break;
            case "MAX": result = values.length ? Math.max.apply(null, values) : 0; break;
        }
        event.value = isFinite(result) && Math.abs(result) < 1e21 ? Number(result.toFixed(6)) : result;
    };

    AF.AFExactMatch = function (rePatterns, str) {
        var s = String(str), m;
        if (rePatterns instanceof RegExp) { m = s.match(rePatterns); return m !== null && m[0] === s ? true : 0; }
        for (var i = 0; i < rePatterns.length; i++) { m = s.match(rePatterns[i]); if (m !== null && m[0] === s) return i + 1; }
        return 0;
    };

    // ---------------------------------------------------------------- app

    var timerHandle = function () { return {}; };
    var app = {
        alert: function (cMsg) {
            var msg = cMsg && typeof cMsg === "object" && own(cMsg, "cMsg") ? cMsg.cMsg : cMsg;
            host.Alert(msg === undefined || msg === null ? "" : String(msg));
            return 1;
        },
        response: function (cQuestion, cTitle, cDefault) {
            var q = cQuestion && typeof cQuestion === "object" ? cQuestion.cQuestion : cQuestion;
            var def = cQuestion && typeof cQuestion === "object" ? cQuestion.cDefault : cDefault;
            host.Alert("[response] " + (q === undefined || q === null ? "" : String(q)));
            return def === undefined || def === null ? "" : String(def);
        },
        beep: function () { },
        execDialog: function () { record("app.execDialog() not run"); return "cancel"; },
        setTimeOut: function (cExpr, nMilliseconds) { record("app.setTimeOut(" + show(cExpr).substring(0, 80) + ") not run"); return timerHandle(); },
        setInterval: function (cExpr, nMilliseconds) { record("app.setInterval(" + show(cExpr).substring(0, 80) + ") not run"); return timerHandle(); },
        clearTimeOut: function () { },
        clearInterval: function () { },
        launchURL: notRun("app.launchURL"),
        openDoc: notRun("app.openDoc", null),
        newDoc: notRun("app.newDoc", null),
        mailMsg: notRun("app.mailMsg"),
        mailGetAddrs: notRun("app.mailGetAddrs", null),
        browseForDoc: notRun("app.browseForDoc", null),
        newCollection: notRun("app.newCollection", null),
        newFDF: notRun("app.newFDF", null),
        openFDF: notRun("app.openFDF", null),
        execMenuItem: notRun("app.execMenuItem"),
        listMenuItems: function () { return []; },
        listToolbarButtons: function () { return []; },
        addMenuItem: notRun("app.addMenuItem"),
        addSubMenu: notRun("app.addSubMenu"),
        addToolButton: notRun("app.addToolButton"),
        removeToolButton: notRun("app.removeToolButton"),
        hideMenuItem: notRun("app.hideMenuItem"),
        hideToolbarButton: notRun("app.hideToolbarButton"),
        popUpMenu: notRun("app.popUpMenu", null),
        popUpMenuEx: notRun("app.popUpMenuEx", null),
        goBack: notRun("app.goBack"),
        goForward: notRun("app.goForward"),
        getPath: notRun("app.getPath", null),
        getNthPlugInName: function () { return undefined; },
        loadPolicyFile: notRun("app.loadPolicyFile"),
        endPriv: function () { },
        // A trusted function is the function it was given; nothing here is privileged.
        trustedFunction: function (oFunc) { return oFunc; },
        trustPropagatorFunction: function (oFunc) { return oFunc; },
        activeDocs: [doc],
        calculate: true,
        constants: { align: { left: 0, center: 1, right: 2, top: 3, bottom: 4 } },
        focusRect: true,
        formsVersion: 11,
        fromPDFConverters: [],
        fs: { isFullScreen: false },
        fullscreen: false,
        language: "ENU",
        media: {},
        monitors: [],
        numPlugIns: 0,
        openInPlace: false,
        platform: "WIN",
        plugIns: [],
        printerNames: [],
        runtimeHighlight: false,
        runtimeHighlightColor: color.ltGray,
        thermometer: { cancelled: false, duration: 100, value: 0, text: "", begin: function () { }, end: function () { } },
        toolbar: true,
        toolbarHorizontal: true,
        toolbarVertical: true,
        viewerType: "Reader",
        viewerVariation: "Reader",
        viewerVersion: 11
    };
    Object.defineProperty(app, "beginPriv", {
        get: function () { throw apiError("NotAllowedError", "Security settings prevent access to this property or method."); },
        enumerable: true
    });

    // ---------------------------------------------------------------- console and global

    var consoleObject = {
        println: function (cMessage) { host.Console(String(cMessage)); },
        clear: function () { host.ConsoleClear(); },
        show: function () { },
        hide: function () { }
    };

    var globalObject = {};
    Object.defineProperty(globalObject, "setPersistent", {
        value: function (cVariable) { record("global.setPersistent(" + show(cVariable) + ") not persisted"); },
        enumerable: false
    });
    Object.defineProperty(globalObject, "subscribe", {
        value: function (cVariable) { record("global.subscribe(" + show(cVariable) + ") not run"); },
        enumerable: false
    });

    // ---------------------------------------------------------------- the document, as the global object

    var docState = { calculate: true, dirty: false, pageNum: 0, zoom: 100, zoomType: zoomtype.none, layout: "SinglePage", delay: false };

    var infoCache = null, dataObjectsCache;
    function documentInfo() { if (infoCache === null) infoCache = JSON.parse(host.Info()); return infoCache; }
    function infoText(key) { var info = documentInfo(); return own(info, key) ? info[key] : ""; }

    // An /Info date as a Date, the entry's text when it does not read as one, "" when it is absent.
    function infoDate(key) {
        var text = infoText(key);
        if (text === "") return "";
        var d = parsePdfDate(text);
        return d === null ? text : d;
    }

    // A PDF date (§7.9.4), read by the host through Core's one parser (host.ParseDate answers
    // "Y,M,D,h,m,s,offset"); null when it does not read. A date that states its zone is that
    // instant; one that states none is the local time it names, as a viewer shows it. The year is
    // set apart, since Date maps a year below 100 into the twentieth century.
    function parsePdfDate(text) {
        if (!text) return null;
        var parsed = host.ParseDate(String(text));
        if (parsed === null || parsed === undefined) return null;
        var p = String(parsed).split(","), n = [];
        for (var i = 0; i < 6; i++) n.push(Number(p[i]));
        var d;
        if (p[6] === "") {
            d = new Date(2000, n[1] - 1, n[2], n[3], n[4], n[5]);
            d.setFullYear(n[0]);
            return d;
        }
        d = new Date(Date.UTC(2000, n[1] - 1, n[2], n[3], n[4], n[5]));
        d.setUTCFullYear(n[0]);
        return new Date(d.getTime() - Number(p[6]) * 60000);
    }

    function pdfDate(text) {
        if (!text) return null;
        var d = parsePdfDate(text);
        return d instanceof Date ? d : null;
    }

    // The attachments as Data objects, as Acrobat answers them (name, path, size, MIMEType,
    // description, creationDate, modDate, and its methods); null when there are none — or when
    // the host did not hand the attachments over (PdfJsExecutionContext.ExposeAttachments).
    function dataObjects() {
        if (dataObjectsCache !== undefined) return dataObjectsCache;
        var list = JSON.parse(host.DataObjects()), out = [];
        for (var i = 0; i < list.length; i++) {
            var o = {
                name: list[i][0], path: list[i][0], size: list[i][1], MIMEType: list[i][2], description: list[i][3],
                creationDate: pdfDate(list[i][4]), modDate: pdfDate(list[i][5])
            };
            o.getFieldValue = notRun("dataObject.getFieldValue", null);
            o.setFieldValue = notRun("dataObject.setFieldValue");
            o.toString = (function (n) { return function () { return n; }; })(o.name);
            o.valueOf = o.toString;
            out.push(o);
        }
        dataObjectsCache = out.length ? out : null;
        return dataObjectsCache;
    }

    // A ReadStream over hex: read(n) answers the next n bytes as hex, as Acrobat's does.
    function readStream(hex) {
        var at = 0;
        return {
            read: function (nBytes) {
                var n = nBytes === undefined ? (hex.length - at) / 2 : Math.max(0, Math.floor(Number(nBytes) || 0));
                var part = hex.substring(at, at + n * 2);
                at += part.length;
                return part;
            }
        };
    }

    // ---------------------------------------------------------------- annotations, optional content, page words

    var ANNOT_HIDDEN = 2, ANNOT_PRINT = 4, ANNOT_NOVIEW = 32, ANNOT_READONLY = 64, ANNOT_LOCKED = 128;

    // Annotation objects share one prototype, as field objects do; each carries its row — what the
    // document's inventory read — and the writes the scripts made, kept for them to read back and
    // recorded, since nothing here applies them to the document.
    var AnnotProto = {};

    function annotState(a) {
        var s = a && a.__tetitulzAnnot;
        if (!s) throw apiError("TypeError", "Not an annotation object.");
        return s;
    }

    function annotProp(key, read) {
        Object.defineProperty(AnnotProto, key, {
            get: function () { var s = annotState(this); return own(s.o, key) ? s.o[key] : read(s.r); },
            set: function (v) { var s = annotState(this); s.o[key] = v; record("annotation " + s.shown + "." + key + " = " + show(v) + " not applied to the document"); },
            configurable: true, enumerable: true
        });
    }

    // The row: [page, type, name, author, contents, modification date, flags, left, bottom, right, top].
    annotProp("page", function (r) { return r[0]; });
    annotProp("type", function (r) { return r[1]; });
    annotProp("name", function (r) { return r[2]; });
    annotProp("author", function (r) { return r[3]; });
    annotProp("contents", function (r) { return r[4]; });
    annotProp("modDate", function (r) { return pdfDate(r[5]); });
    annotProp("creationDate", function () { return null; });
    annotProp("hidden", function (r) { return (r[6] & ANNOT_HIDDEN) !== 0; });
    annotProp("print", function (r) { return (r[6] & ANNOT_PRINT) !== 0; });
    annotProp("noView", function (r) { return (r[6] & ANNOT_NOVIEW) !== 0; });
    annotProp("readOnly", function (r) { return (r[6] & ANNOT_READONLY) !== 0; });
    annotProp("lock", function (r) { return (r[6] & ANNOT_LOCKED) !== 0; });
    annotProp("rect", function (r) { return [r[7], r[8], r[9], r[10]]; });
    annotProp("subject", function () { return ""; });
    annotProp("inReplyTo", function () { return ""; });
    annotProp("intent", function () { return ""; });
    annotProp("opacity", function () { return 1; });
    annotProp("popupOpen", function () { return false; });
    annotProp("richContents", function () { return []; });
    annotProp("strokeColor", function () { return color.black; });
    annotProp("width", function () { return 1; });
    annotProp("delay", function () { return false; });
    Object.defineProperty(AnnotProto, "doc", { get: function () { return doc; }, enumerable: true });
    defineValue(AnnotProto, "getProps", function () {
        var out = {}, keys = Object.keys(AnnotProto);
        for (var i = 0; i < keys.length; i++)
            if (keys[i] !== "doc" && typeof this[keys[i]] !== "function") out[keys[i]] = this[keys[i]];
        return out;
    });
    defineValue(AnnotProto, "setProps", function (oProps) {
        var s = annotState(this);
        if (oProps && typeof oProps === "object") for (var k in oProps) if (own(oProps, k)) s.o[k] = oProps[k];
        record("annotation " + s.shown + ".setProps() not applied to the document");
    });
    defineValue(AnnotProto, "destroy", function () { record("annotation " + annotState(this).shown + ".destroy() not applied to the document"); });
    defineValue(AnnotProto, "getStateInModel", notRun("annotation.getStateInModel", []));
    defineValue(AnnotProto, "transitionToState", notRun("annotation.transitionToState"));

    function makeAnnot(r) {
        var a = Object.create(AnnotProto);
        Object.defineProperty(a, "__tetitulzAnnot", { value: Object.freeze({ r: r, o: {}, shown: shortName(r[2]) }), enumerable: false });
        return a;
    }

    var annotsCache = null;
    function annotations() {
        if (annotsCache === null) {
            var rows = JSON.parse(host.Annotations());
            annotsCache = [];
            for (var i = 0; i < rows.length; i++) annotsCache.push(makeAnnot(rows[i]));
        }
        return annotsCache;
    }

    // A method's index argument — a number, or the named member of Acrobat's object form — floored; -1 for none.
    function indexArgument(arg, key) {
        if (arg !== null && typeof arg === "object") arg = arg[key];
        if (arg === undefined || arg === null || arg === "") return -1;
        var n = Math.floor(Number(arg));
        return isNaN(n) ? -1 : n;
    }

    var ANNOT_SORT = { 1: "page", 2: "author", 3: "modDate", 4: "type" };

    // Acrobat's getAnnots: the annotations of the page, or of the whole document when no page is
    // named, sorted by nSortBy (page, author, date, type) and reversed when asked; null when none.
    function getAnnots(nPage) {
        var page = indexArgument(nPage, "nPage");
        var all = annotations(), out = [];
        for (var i = 0; i < all.length; i++) if (page < 0 || all[i].page === page) out.push(all[i]);
        if (!out.length) return null;
        var named = nPage !== null && typeof nPage === "object";
        var sortBy = named ? ANNOT_SORT[Number(nPage.nSortBy)] : undefined;
        if (sortBy) out.sort(function (a, b) { var x = a[sortBy], y = b[sortBy]; return x < y ? -1 : x > y ? 1 : 0; });
        if (named && nPage.bReverse) out.reverse();
        return out;
    }

    function getAnnot(nPage, cName) {
        var page = indexArgument(nPage, "nPage");
        var name = nPage !== null && typeof nPage === "object" ? nPage.cName : cName;
        if (name === undefined || name === null) return null;
        name = String(name);
        var all = annotations();
        for (var i = 0; i < all.length; i++) if (all[i].page === page && all[i].name === name) return all[i];
        return null;
    }

    // OCG objects: the document's groups, as Acrobat's getOCGs answers them — for any page, since
    // which page draws a group is not read. A write is kept for the scripts and recorded.
    var OcgProto = {};

    function ocgState(o) {
        var s = o && o.__tetitulzOcg;
        if (!s) throw apiError("TypeError", "Not an OCG object.");
        return s;
    }

    function ocgProp(key, read) {
        Object.defineProperty(OcgProto, key, {
            get: function () { var s = ocgState(this); return own(s.o, key) ? s.o[key] : read(s.r); },
            set: function (v) { var s = ocgState(this); s.o[key] = v; record("OCG " + s.shown + "." + key + " = " + show(v) + " not applied to the document"); },
            configurable: true, enumerable: true
        });
    }

    ocgProp("name", function (r) { return r[0]; });
    ocgProp("state", function (r) { return r[1]; });
    ocgProp("initState", function (r) { return r[1]; });
    ocgProp("locked", function () { return false; });
    defineValue(OcgProto, "getIntent", function () { return ["View"]; });
    defineValue(OcgProto, "setIntent", function (aIntent) { record("OCG " + ocgState(this).shown + ".setIntent(" + show(aIntent) + ") not applied to the document"); });
    defineValue(OcgProto, "toString", function () { return String(this.name); });

    var ocgsCache = null;
    function getOCGs() {
        if (ocgsCache === null) {
            var rows = JSON.parse(host.Ocgs());
            ocgsCache = [];
            for (var i = 0; i < rows.length; i++) {
                var g = Object.create(OcgProto);
                Object.defineProperty(g, "__tetitulzOcg", { value: Object.freeze({ r: rows[i], o: {}, shown: shortName(rows[i][0]) }), enumerable: false });
                ocgsCache.push(g);
            }
        }
        return ocgsCache.length ? ocgsCache.slice(0) : null;
    }

    // getPageNthWord strips the word's punctuation and white space unless bStrip is false.
    function isWordChar(c) { return !/[\s!-\/:-@\[-`{-~ -⁯]/.test(c); }
    function stripWord(w) {
        var s = 0, e = w.length;
        while (s < e && !isWordChar(w.charAt(s))) s++;
        while (e > s && !isWordChar(w.charAt(e - 1))) e--;
        return w.substring(s, e);
    }

    // A page argument the method may leave out means the current page, which is the first at open.
    function pageArgument(arg) { var p = indexArgument(arg, "nPage"); return p < 0 ? 0 : p; }

    function getPageRotation(nPage) { return host.PageRotation(pageArgument(nPage)); }

    // Acrobat's getPageBox answers the named box in rotated user space as [left, top, right,
    // bottom] — the origin at the displayed crop box's lower-left corner, so the crop box itself is
    // always [0, height, width, 0] and a media box larger than it reaches below zero: the box's
    // corners taken through the inverse of the page's rotatedToDefault matrix.
    function getPageBox(cBox, nPage) {
        var box = cBox, page = nPage;
        if (cBox !== null && typeof cBox === "object") { box = cBox.cBox; page = cBox.nPage; }
        var p = pageArgument(page);
        var json = host.PageBox(p, box === undefined || box === null ? "Crop" : String(box));
        if (json === null || json === undefined) return [0, 0, 0, 0];
        var b = JSON.parse(json);
        var q = rotatedToDefault(p).invert().transform([b[0], b[1], b[2], b[1], b[2], b[3], b[0], b[3]]);
        var xs = [q[0], q[2], q[4], q[6]], ys = [q[1], q[3], q[5], q[7]];
        return [Math.min.apply(null, xs), Math.max.apply(null, ys), Math.max.apply(null, xs), Math.min.apply(null, ys)];
    }

    function getPageNumWords(nPage) { return host.PageWordCount(indexArgument(nPage, "nPage")); }

    // Acrobat raises RangeError for a word the page lacks.
    function getPageNthWord(nPage, nWord, bStrip) {
        var page = nPage, word = nWord, strip = bStrip;
        if (nPage !== null && typeof nPage === "object") { page = nPage.nPage; word = nPage.nWord; strip = nPage.bStrip; }
        var w = host.PageWord(indexArgument(page, "nPage"), indexArgument(word, "nWord"));
        if (w === null || w === undefined) throw apiError("RangeError", "Invalid argument value.");
        return strip === false ? w : stripWord(w);
    }

    var docProps = {
        numFields: function () { return fieldNames().length; },
        numPages: function () { return host.NumPages(); },
        author: function () { return infoText("Author"); }, creator: function () { return infoText("Creator"); },
        producer: function () { return infoText("Producer"); }, subject: function () { return infoText("Subject"); },
        title: function () { return infoText("Title"); }, keywords: function () { return infoText("Keywords"); },
        creationDate: function () { return infoDate("CreationDate"); }, modDate: function () { return infoDate("ModDate"); },
        documentFileName: function () { return ""; }, path: function () { return ""; }, URL: function () { return ""; },
        baseURL: function () { return ""; }, filesize: function () { return 0; }, external: function () { return false; },
        hidden: function () { return false; }, mouseX: function () { return 0; }, mouseY: function () { return 0; },
        numTemplates: function () { return 0; }, templates: function () { return []; }, icons: function () { return null; },
        info: function () {
            var info = documentInfo(), copy = {};
            for (var k in info) if (own(info, k)) copy[k] = k === "CreationDate" || k === "ModDate" ? infoDate(k) : info[k];
            // Acrobat's info carries ContactEmail — its own identity preference, which no document
            // holds — beside the Authors the packet names: empty here unless the document says.
            if (!own(copy, "ContactEmail")) copy.ContactEmail = "";
            // Acrobat also answers each key in lower case (info.author); the aliases are not listed.
            for (var key in copy) if (own(copy, key) && key.toLowerCase() !== key && !own(copy, key.toLowerCase()))
                Object.defineProperty(copy, key.toLowerCase(), { value: copy[key], writable: true, configurable: true, enumerable: false });
            return copy;
        },
        metadata: function () { var m = host.Metadata(); return m === null || m === undefined ? "" : m; }, docID: function () { return []; },
        dataObjects: function () { return dataObjects(); }, bookmarkRoot: function () { return null; }, media: function () { return {}; },
        permStatusReady: function () { return true; }, requiresFullSave: function () { return false; },
        securityHandler: function () { return null; }, sounds: function () { return null; }, viewState: function () { return {}; },
        disclosed: function () { return false; }, dynamicXFAForm: function () { return false; }
    };
    for (var p in docProps) if (own(docProps, p)) defineGetter(doc, p, docProps[p], (function (key) {
        return function (v) { record("this." + key + " = " + show(v) + " not applied"); };
    })(p));
    for (var s2 in docState) if (own(docState, s2)) defineGetter(doc, s2, (function (key) {
        return function () { return docState[key]; };
    })(s2), (function (key) {
        return function (v) { docState[key] = v; if (key !== "dirty" && key !== "calculate" && key !== "delay") record("this." + key + " = " + show(v) + " not applied"); };
    })(s2));

    var docMethods = {
        getField: function (cName) { return getFieldByName(cName); },
        getNthFieldName: function (nIndex) { var names = fieldNames(), i = Number(nIndex); return i >= 0 && i < names.length ? names[i] : undefined; },
        calculateNow: function () { record("this.calculateNow() not run"); },
        resetForm: function (aFields) {
            var names = aFields && typeof aFields.length === "number" && typeof aFields !== "string" ? aFields : fieldNames();
            for (var i = 0; i < names.length; i++) {
                var d = describe(String(names[i]));
                host.SetValue(String(names[i]), d ? d.defaultValue : "");
            }
        },
        submitForm: notRun("this.submitForm"), mailDoc: notRun("this.mailDoc"), mailForm: notRun("this.mailForm"),
        print: notRun("this.print"), saveAs: notRun("this.saveAs"), closeDoc: notRun("this.closeDoc", false),
        getURL: notRun("this.getURL"), importDataObject: notRun("this.importDataObject", false),
        exportDataObject: notRun("this.exportDataObject"), createDataObject: notRun("this.createDataObject"),
        getDataObject: function (cName) {
            var all = dataObjects() || [];
            for (var i = 0; i < all.length; i++) if (all[i].name === String(cName)) return all[i];
            return null;
        },
        // The attachment's contents as a ReadStream; null when the document has none by that name,
        // it is larger than a script is handed, or the host did not hand attachments over.
        getDataObjectContents: function (cName) {
            var hex = host.DataObjectContents(String(cName));
            return hex === null || hex === undefined ? null : readStream(hex);
        },
        removeDataObject: notRun("this.removeDataObject"),
        importAnFDF: notRun("this.importAnFDF"), importAnXFDF: notRun("this.importAnXFDF"), importTextData: notRun("this.importTextData", -1),
        exportAsFDF: notRun("this.exportAsFDF"), exportAsXFDF: notRun("this.exportAsXFDF"), exportAsText: notRun("this.exportAsText"),
        addField: notRun("this.addField", null), removeField: notRun("this.removeField"),
        addAnnot: notRun("this.addAnnot", null),
        getAnnot: function (nPage, cName) { return getAnnot(nPage, cName); },
        getAnnots: function (nPage) { return getAnnots(nPage); },
        addLink: notRun("this.addLink", null), getLinks: notRun("this.getLinks", null),
        addIcon: notRun("this.addIcon"), getIcon: notRun("this.getIcon", null), importIcon: notRun("this.importIcon", -1), removeIcon: notRun("this.removeIcon"),
        createIcon: notRun("this.createIcon", null),
        addScript: notRun("this.addScript"), removeScript: notRun("this.removeScript"),
        setAction: notRun("this.setAction"), setPageAction: notRun("this.setPageAction"),
        deletePages: notRun("this.deletePages"), extractPages: notRun("this.extractPages", null), insertPages: notRun("this.insertPages"),
        replacePages: notRun("this.replacePages"), movePage: notRun("this.movePage"), newPage: notRun("this.newPage"),
        flattenPages: notRun("this.flattenPages"), setPageBoxes: notRun("this.setPageBoxes"), setPageLabels: notRun("this.setPageLabels"),
        setPageRotations: notRun("this.setPageRotations"), spawnPageFromTemplate: notRun("this.spawnPageFromTemplate", null),
        createTemplate: notRun("this.createTemplate", null), getTemplate: notRun("this.getTemplate", null), getNthTemplate: notRun("this.getNthTemplate", null),
        getOCGs: function () { return getOCGs(); },
        getPageBox: function (cBox, nPage) { return getPageBox(cBox, nPage); },
        getPageLabel: function (nPage) { return String((Number(nPage) || 0) + 1); },
        getPageRotation: function (nPage) { return getPageRotation(nPage); },
        getPageNthWord: function (nPage, nWord, bStrip) { return getPageNthWord(nPage, nWord, bStrip); },
        getPageNumWords: function (nPage) { return getPageNumWords(nPage); },
        selectPageNthWord: notRun("this.selectPageNthWord"),
        getPrintParams: notRun("this.getPrintParams", null),
        gotoNamedDest: notRun("this.gotoNamedDest"), scroll: notRun("this.scroll"), bringToFront: notRun("this.bringToFront"),
        syncAnnotScan: function () { }
    };
    for (var dm in docMethods) if (own(docMethods, dm)) defineValue(doc, dm, docMethods[dm]);

    // ---------------------------------------------------------------- globals

    var globals = {
        app: app, util: util, color: color, console: consoleObject, global: globalObject,
        border: border, display: display, position: position, scaleHow: scaleHow, scaleWhen: scaleWhen,
        style: style, highlight: highlight, font: font, zoomtype: zoomtype, cursor: cursor, trans: trans,
        Matrix2D: Matrix2D,
        // This library's alias for the document, which earlier scripts written for it name.
        doc: doc
    };
    for (var g in globals) if (own(globals, g)) defineValue(doc, g, globals[g]);
    for (var a in AF) if (own(AF, a)) defineValue(doc, a, AF[a]);
    // Acrobat: the last label has two characters at least (a@b.c is refused).
    defineValue(doc, "eMailValidate", function (str) {
        var s = String(str);
        return s.length <= 320 && /^[A-Za-z0-9_.+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/.test(s)
            && s.length - s.lastIndexOf(".") - 1 >= 2;
    });

    // ---------------------------------------------------------------- one execution's event

    var event;
    defineGetter(doc, "event", function () { return event; }, function (v) { event = v; });

    // Called by the engine before each script with the event it runs for; the engine keeps these
    // two functions and deletes their globals, so no document script calls them.
    function begin(json) {
        var e = JSON.parse(json);
        describeCache = {};
        fieldCache = {};
        namesCache = null;
        infoCache = null;
        dataObjectsCache = undefined;
        annotsCache = null;
        ocgsCache = null;
        var target = e.fieldName ? getFieldByName(e.fieldName) : doc;
        if (target === null) target = makeField(e.fieldName, null);
        if (e.type === "Doc" || e.type === "Page") {
            // A document or page event has no value, change or selection (Acrobat: undefined).
            event = { name: e.name, rc: true, source: target, target: target, type: e.type };
            return;
        }
        event = {
            change: e.change, changeEx: "", commitKey: 0, fieldFull: false, keyDown: false, modifier: false,
            name: e.name, rc: true, richChange: [], richChangeEx: [], richValue: [],
            selEnd: e.selEnd, selStart: e.selStart, shift: false,
            source: target, target: target, targetName: e.fieldName, type: e.type,
            value: e.value, willCommit: e.willCommit
        };
    }

    // Hands the event's outcome to the host: its value (null when it has none), change and rc.
    function end() {
        var e = event || {};
        var v = e.value;
        host.Finish(v === undefined || v === null ? null : valueText(v),
            e.change === undefined || e.change === null ? "" : String(e.change), !!e.rc);
    }

    defineValue(doc, "__tetitulzBegin", begin);
    defineValue(doc, "__tetitulzEnd", end);
})(__tetitulzHost, this);
delete this.__tetitulzHost;

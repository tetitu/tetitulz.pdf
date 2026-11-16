// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;

namespace Tetitulz.Pdf.Core.Scripting
{
    /// <summary>
    /// A JavaScript prelude that bounds the strings one built-in call may build, for the two
    /// plugins that run untrusted scripts under Jint — the form engine and the page-script engine.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a prelude.</b> Jint checks a script's memory limit when a statement starts, so
    /// one call of a built-in allocates whatever it builds before the limit is asked. Measured under a
    /// 32 MiB limit: <c>'x'.repeat(1e8)</c> 190 MiB, <c>'x'.padEnd(2e8)</c> 381 MiB, a <c>join</c>
    /// of two hundred references to one megabyte string 1.4 GB, <c>JSON.stringify</c> of the same
    /// array 383 MiB — each succeeding, and each bounded only by .NET's largest string, about two
    /// gigabytes. Array storage is not among them: Jint charges a large <c>fill</c> as it goes.</para>
    /// <para><b>What it does.</b> Each built-in whose result can be larger than its operands —
    /// <c>String.prototype.repeat</c>, <c>padStart</c>, <c>padEnd</c>, <c>concat</c>,
    /// <c>replace</c> and <c>replaceAll</c>; <c>Array.prototype.join</c> and
    /// <c>toLocaleString</c>; <c>JSON.stringify</c> — is replaced by a function that converts its
    /// operands once, as the specification does, counts what the result will hold, throws a
    /// <c>RangeError</c> past <c>maxCharacters</c>, and otherwise calls the original. A string
    /// replacement is applied through a callback that performs the specification's substitution
    /// (<c>$$</c>, <c>$&amp;</c>, <c>$`</c>, <c>$'</c>, <c>$n</c>, <c>$nn</c>, <c>$&lt;name&gt;</c>), so
    /// that <c>$&amp;</c> cannot multiply a match unseen; <c>join</c> keeps the engines' rule that an
    /// array met again while it is being joined contributes nothing.</para>
    /// <para><b>What it does not.</b> <c>JSON.stringify</c> counts a string's characters, not their
    /// escapes (at most six characters each), and indentation as a constant per value; with an
    /// allow-list replacer (an array) it is not counted. Other built-ins build at most a constant
    /// multiple of their operands, which the memory limit has already charged — except the ones that
    /// allocate from a number: <c>ArrayBuffer</c>, <c>SharedArrayBuffer</c> and each typed array's
    /// constructor, and <c>ArrayBuffer</c>'s <c>resize</c> and <c>transfer</c>, each refused past the
    /// memory limit in bytes; and the joins that do not go through the array's (a typed array's,
    /// <c>String.raw</c>, a regular expression's <c>[Symbol.replace]</c>), counted as the others.</para>
    /// <para><b>Public because two plugins need it</b> and Core's <c>InternalsVisibleTo</c> names
    /// no plugin; it references no engine — it is text a host runs before the page's scripts.</para>
    /// </remarks>
    public static class ScriptBuiltinBounds
    {
        /// <summary>
        /// The prelude for scripts that may build at most <paramref name="maxCharacters"/> characters
        /// in one call. Run it once in an engine, before any script of the document or page.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCharacters"/> is not positive.</exception>
        public static string Prelude(long maxCharacters)
        {
            if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
            return "(" + Body + ")(" + maxCharacters.ToString(CultureInfo.InvariantCulture) + ");";
        }

        /// <summary>The characters a script may build in one call, from its memory limit in bytes: two bytes a character.</summary>
        public static long CharactersFor(long memoryLimitBytes) => Math.Max(1, memoryLimitBytes / 2);

        private const string Body = @"function (max) {
  'use strict';
  var apply = Reflect.apply, defineProperty = Object.defineProperty, toStr = String, isArray = Array.isArray;
  var floor = Math.floor, min = Math.min, SP = String.prototype, AP = Array.prototype, nativeJoin = AP.join;
  function fail(what, n) {
    throw new RangeError(what + ' would build ' + n + ' characters; a script may build ' + max + ' in one call');
  }
  function text(v) {
    if (typeof v === 'symbol') throw new TypeError('Cannot convert a Symbol value to a string');
    return toStr(v);
  }
  function self(v, name) {
    if (v === null || v === undefined) throw new TypeError(name + ' called on null or undefined');
    return text(v);
  }
  function number(v) { var n = +v; return n !== n ? 0 : n; }
  function guard(target, name, make) {
    var original = target[name];
    if (typeof original !== 'function') return;
    var bounded = make(original);
    defineProperty(bounded, 'name', { value: name, configurable: true });
    defineProperty(bounded, 'length', { value: original.length, configurable: true });
    defineProperty(target, name, { value: bounded, writable: true, configurable: true, enumerable: false });
  }

  guard(SP, 'repeat', function (original) {
    return function (count) {
      var s = self(this, 'String.prototype.repeat'), n = number(count);
      var size = s.length * (n > 0 ? n : 0);
      if (size > max) fail('repeat', size);
      return apply(original, s, [n]);
    };
  });

  function pad(name) {
    guard(SP, name, function (original) {
      return function (maxLength, fillString) {
        var s = self(this, 'String.prototype.' + name), n = number(maxLength);
        var fill = fillString === undefined ? ' ' : text(fillString);
        if (fill.length > 0 && n > max) fail(name, n);
        return apply(original, s, [n, fill]);
      };
    });
  }
  pad('padStart');
  pad('padEnd');

  guard(SP, 'concat', function (original) {
    return function () {
      var s = self(this, 'String.prototype.concat'), parts = [], total = s.length;
      for (var i = 0; i < arguments.length; i++) {
        var p = text(arguments[i]);
        total += p.length;
        if (total > max) fail('concat', total);
        parts.push(p);
      }
      return apply(original, s, parts);
    };
  });

  function substitute(rep, a) {
    var n = a.length, groups = a[n - 1], str, position, m;
    if (typeof groups === 'string') { str = groups; position = a[n - 2]; groups = undefined; m = n - 3; }
    else { str = a[n - 2]; position = a[n - 3]; m = n - 4; }
    var matched = a[0], out = '', i = 0, len = rep.length;
    while (i < len) {
      var c = rep.charAt(i);
      if (c !== '$' || i + 1 >= len) { out += c; i++; continue; }
      var d = rep.charAt(i + 1);
      if (d === '$') { out += '$'; i += 2; }
      else if (d === '&') { out += matched; i += 2; }
      else if (d === '`') { out += str.slice(0, position); i += 2; }
      else if (d === ""'"") { out += str.slice(min(position + matched.length, str.length)); i += 2; }
      else if (d >= '0' && d <= '9') {
        var d2 = i + 2 < len ? rep.charAt(i + 2) : '';
        var two = d2 >= '0' && d2 <= '9' ? +(d + d2) : -1;
        if (two >= 1 && two <= m) { out += a[two] === undefined ? '' : a[two]; i += 3; }
        else if (+d >= 1 && +d <= m) { out += a[+d] === undefined ? '' : a[+d]; i += 2; }
        else { out += '$'; i++; }
      }
      else if (d === '<' && groups !== undefined) {
        var close = rep.indexOf('>', i + 2);
        if (close < 0) { out += '$<'; i += 2; }
        else { var g = groups[rep.slice(i + 2, close)]; out += g === undefined ? '' : text(g); i = close + 1; }
      }
      else { out += '$'; i++; }
    }
    return out;
  }
  function replacing(name) {
    guard(SP, name, function (original) {
      return function (searchValue, replaceValue) {
        var s = self(this, 'String.prototype.' + name), total = s.length, replacer;
        if (typeof replaceValue === 'function') {
          var f = replaceValue;
          replacer = function () {
            var r = text(apply(f, undefined, arguments));
            total += r.length;
            if (total > max) fail(name, total);
            return r;
          };
        } else {
          var rep = text(replaceValue), plain = rep.indexOf('$') < 0;
          replacer = function () {
            var r = plain ? rep : substitute(rep, arguments);
            total += r.length;
            if (total > max) fail(name, total);
            return r;
          };
        }
        return apply(original, s, [searchValue, replacer]);
      };
    });
  }
  replacing('replace');
  replacing('replaceAll');

  var joining = [];
  function joinWith(o, sep, element) {
    var len = number(o.length);
    len = len <= 0 ? 0 : min(floor(len), 9007199254740991);
    var total = len > 1 ? (len - 1) * sep.length : 0;
    if (total > max || len > max) fail('join', total > len ? total : len);
    if (joining.indexOf(o) >= 0) return null;
    joining.push(o);
    try {
      var parts = new Array(len);
      for (var i = 0; i < len; i++) {
        var p = element(o[i]);
        total += p.length;
        if (total > max) fail('join', total);
        parts[i] = p;
      }
      return parts;
    } finally {
      joining.pop();
    }
  }
  guard(AP, 'join', function (original) {
    return function (separator) {
      if (this === null || this === undefined) throw new TypeError('Array.prototype.join called on null or undefined');
      var o = Object(this), sep = separator === undefined ? ',' : text(separator);
      var parts = joinWith(o, sep, function (e) { return e === undefined || e === null ? '' : text(e); });
      return parts === null ? '' : apply(original, parts, [sep]);
    };
  });
  guard(AP, 'toLocaleString', function () {
    return function () {
      if (this === null || this === undefined) throw new TypeError('Array.prototype.toLocaleString called on null or undefined');
      var o = Object(this);
      var parts = joinWith(o, ',', function (e) { return e === undefined || e === null ? '' : text(e.toLocaleString()); });
      return parts === null ? '' : apply(nativeJoin, parts, [',']);
    };
  });

  // A typed array's join is an array's join over its elements.
  var typedProto = typeof Uint8Array === 'function' ? Object.getPrototypeOf(Uint8Array.prototype) : null;
  if (typedProto) guard(typedProto, 'join', function () {
    return function (separator) {
      var o = this, sep = separator === undefined ? ',' : text(separator);
      var parts = joinWith(o, sep, function (e) { return text(e); });
      return parts === null ? '' : apply(nativeJoin, parts, [sep]);
    };
  });

  // String.raw joins its template's strings and substitutions: counted as a join is.
  guard(String, 'raw', function (original) {
    return function (template) {
      if (template === null || template === undefined) throw new TypeError('String.raw called on null or undefined');
      var raw = Object(Object(template).raw), len = number(raw.length);
      len = len <= 0 ? 0 : min(floor(len), 9007199254740991);
      if (len > max) fail('String.raw', len);
      var total = 0;
      for (var i = 0; i < len; i++) {
        total += text(raw[i]).length;
        if (i + 1 < len && i + 1 < arguments.length) total += text(arguments[i + 1]).length;
        if (total > max) fail('String.raw', total);
      }
      return apply(original, String, arguments);
    };
  });

  // A regular expression's replace, reached as /x/g[Symbol.replace](s, '$&$&'), not through
  // String.prototype.replace: the same counting replacer.
  if (typeof Symbol === 'function' && Symbol.replace) {
    var RP = RegExp.prototype, nativeRegExpReplace = RP[Symbol.replace];
    if (typeof nativeRegExpReplace === 'function') {
      var boundedRegExpReplace = function (string, replaceValue) {
        var s = text(string), total = s.length, replacer;
        if (typeof replaceValue === 'function') {
          var f = replaceValue;
          replacer = function () {
            var r = text(apply(f, undefined, arguments));
            total += r.length;
            if (total > max) fail('RegExp replace', total);
            return r;
          };
        } else {
          var rep = text(replaceValue), plain = rep.indexOf('$') < 0;
          replacer = function () {
            var r = plain ? rep : substitute(rep, arguments);
            total += r.length;
            if (total > max) fail('RegExp replace', total);
            return r;
          };
        }
        return apply(nativeRegExpReplace, this, [s, replacer]);
      };
      defineProperty(RP, Symbol.replace, { value: boundedRegExpReplace, writable: true, configurable: true, enumerable: false });
    }
  }

  // One constructor call of ArrayBuffer or a typed array allocates its byte length at once, from a
  // number: refused past what the script may hold. A typed array built from an object (an array, a
  // buffer, an iterable) is bounded by that object, which the memory limit has charged.
  var bytes = max * 2, G = typeof globalThis === 'object' ? globalThis : this;
  function byteLength(v) { var n = number(v); return n > 0 ? floor(n) : 0; }
  function failBytes(what, n) {
    throw new RangeError(what + ' would allocate ' + n + ' bytes; a script may hold ' + bytes);
  }
  function boundConstructor(name, perElement) {
    var C = G[name];
    if (typeof C !== 'function' || typeof Proxy !== 'function') return;
    var each = perElement || C.BYTES_PER_ELEMENT || 1;
    var P = new Proxy(C, {
      construct: function (target, args, newTarget) {
        var a = args[0];
        if (args.length > 0 && (a === null || typeof a !== 'object')) {
          var n = byteLength(a) * each;
          if (n > bytes) failBytes(name, n);
        }
        var o = args[1];
        if (name === 'ArrayBuffer' && o !== null && typeof o === 'object' && byteLength(o.maxByteLength) > bytes)
          failBytes(name, byteLength(o.maxByteLength));
        return Reflect.construct(target, args, newTarget === P ? target : newTarget);
      }
    });
    defineProperty(C.prototype, 'constructor', { value: P, writable: true, configurable: true, enumerable: false });
    defineProperty(G, name, { value: P, writable: true, configurable: true, enumerable: false });
  }
  boundConstructor('ArrayBuffer', 1);
  boundConstructor('SharedArrayBuffer', 1);
  ['Int8Array', 'Uint8Array', 'Uint8ClampedArray', 'Int16Array', 'Uint16Array', 'Int32Array', 'Uint32Array',
   'Float16Array', 'Float32Array', 'Float64Array', 'BigInt64Array', 'BigUint64Array'].forEach(function (n) { boundConstructor(n, 0); });
  if (typeof ArrayBuffer === 'function') ['resize', 'transfer', 'transferToFixedLength'].forEach(function (name) {
    guard(ArrayBuffer.prototype, name, function (original) {
      return function (newLength) {
        if (newLength !== undefined && byteLength(newLength) > bytes) failBytes('ArrayBuffer.prototype.' + name, byteLength(newLength));
        return apply(original, this, arguments);
      };
    });
  });

  guard(JSON, 'stringify', function (original) {
    return function boundedStringify(value, replacer, space) {
      if (isArray(replacer)) {
        // An allow-list filters an object's keys and never an array's elements, so it bounds
        // nothing: counted by a first pass that keeps what the list keeps (in any order — only the
        // size is wanted), then the call as asked.
        var allow = [];
        for (var a = 0; a < replacer.length; a++) {
          var item = replacer[a];
          if (typeof item === 'string' || typeof item === 'number' || item instanceof String || item instanceof Number) {
            var name = text(item);
            if (allow.indexOf(name) < 0) allow.push(name);
          }
        }
        var keep = function (key, v) { return key === '' || isArray(this) || allow.indexOf(key) >= 0 ? v : undefined; };
        apply(boundedStringify, JSON, [value, keep, space]);
        return apply(original, JSON, arguments);
      }
      var f = typeof replacer === 'function' ? replacer : undefined, total = 0;
      var gap = typeof space === 'number' ? min(10, floor(space)) : typeof space === 'string' ? min(10, space.length) : 0;
      if (gap < 0) gap = 0;
      var counting = function (key, v) {
        var r = f ? apply(f, this, [key, v]) : v;
        total += key.length + 4 + gap;
        if (typeof r === 'string') total += r.length + 2;
        else if (r !== null && typeof r !== 'object' && r !== undefined) total += 24;
        if (total > max) fail('JSON.stringify', total);
        return r;
      };
      return apply(original, JSON, [value, counting, space]);
    };
  });
}";
    }
}

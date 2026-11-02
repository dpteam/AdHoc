/* =========================================================================
   DptCrypto - SHA-256 / HMAC-SHA256 / PBKDF2 / HMAC-CTR sealing in plain ES5.
   Mirrors src/AdHocServer/Core/ApiCrypto.cs bit for bit. No dependencies,
   no WebCrypto (which is unavailable on plain http:// LAN pages anyway).
   ========================================================================= */
var DptCrypto = (function () {
  'use strict';

  var K = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
  ];

  function rotr(x, n) { return (x >>> n) | (x << (32 - n)); }

  function sha256(bytes) {
    var H = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
             0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
    var len = bytes.length;
    var blocks = ((len + 9 + 63) >> 6);
    var padded = new Uint8Array(blocks * 64);
    padded.set(bytes);
    padded[len] = 0x80;

    var bitsHi = Math.floor(len / 536870912) >>> 0;
    var bitsLo = (len << 3) >>> 0;
    var end = blocks * 64;
    padded[end - 8] = (bitsHi >>> 24) & 0xff;
    padded[end - 7] = (bitsHi >>> 16) & 0xff;
    padded[end - 6] = (bitsHi >>> 8) & 0xff;
    padded[end - 5] = bitsHi & 0xff;
    padded[end - 4] = (bitsLo >>> 24) & 0xff;
    padded[end - 3] = (bitsLo >>> 16) & 0xff;
    padded[end - 2] = (bitsLo >>> 8) & 0xff;
    padded[end - 1] = bitsLo & 0xff;

    var w = new Array(64);
    for (var b = 0; b < blocks; b++) {
      var off = b * 64;
      for (var i = 0; i < 16; i++) {
        w[i] = ((padded[off + i * 4] << 24) | (padded[off + i * 4 + 1] << 16) |
                (padded[off + i * 4 + 2] << 8) | padded[off + i * 4 + 3]) | 0;
      }
      for (i = 16; i < 64; i++) {
        var w15 = w[i - 15], w2 = w[i - 2];
        var s0 = (rotr(w15, 7) ^ rotr(w15, 18) ^ (w15 >>> 3)) | 0;
        var s1 = (rotr(w2, 17) ^ rotr(w2, 19) ^ (w2 >>> 10)) | 0;
        w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
      }

      var a = H[0], b2 = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];
      for (i = 0; i < 64; i++) {
        var S1 = (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) | 0;
        var ch = ((e & f) ^ (~e & g)) | 0;
        var t1 = (h + S1 + ch + K[i] + w[i]) | 0;
        var S0 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) | 0;
        var maj = ((a & b2) ^ (a & c) ^ (b2 & c)) | 0;
        var t2 = (S0 + maj) | 0;
        h = g; g = f; f = e; e = (d + t1) | 0;
        d = c; c = b2; b2 = a; a = (t1 + t2) | 0;
      }
      H[0] = (H[0] + a) | 0; H[1] = (H[1] + b2) | 0; H[2] = (H[2] + c) | 0; H[3] = (H[3] + d) | 0;
      H[4] = (H[4] + e) | 0; H[5] = (H[5] + f) | 0; H[6] = (H[6] + g) | 0; H[7] = (H[7] + h) | 0;
    }

    var out = new Uint8Array(32);
    for (var j = 0; j < 8; j++) {
      out[j * 4] = (H[j] >>> 24) & 0xff;
      out[j * 4 + 1] = (H[j] >>> 16) & 0xff;
      out[j * 4 + 2] = (H[j] >>> 8) & 0xff;
      out[j * 4 + 3] = H[j] & 0xff;
    }
    return out;
  }

  function hmacSha256(keyBytes, dataBytes) {
    var key = keyBytes;
    if (key.length > 64) key = sha256(key);
    var block = new Uint8Array(64);
    block.set(key);

    var ipad = new Uint8Array(64 + dataBytes.length);
    var opad = new Uint8Array(64 + 32);
    for (var i = 0; i < 64; i++) {
      ipad[i] = block[i] ^ 0x36;
      opad[i] = block[i] ^ 0x5c;
    }
    ipad.set(dataBytes, 64);

    var inner = sha256(ipad);
    opad.set(inner, 64);
    return sha256(opad);
  }

  function pbkdf2(passwordBytes, saltBytes, iterations, length) {
    var out = new Uint8Array(length);
    var hashLen = 32;
    var blocks = Math.ceil(length / hashLen);
    var offset = 0;
    for (var b = 1; b <= blocks; b++) {
      var input = new Uint8Array(saltBytes.length + 4);
      input.set(saltBytes, 0);
      input[saltBytes.length] = (b >>> 24) & 0xff;
      input[saltBytes.length + 1] = (b >>> 16) & 0xff;
      input[saltBytes.length + 2] = (b >>> 8) & 0xff;
      input[saltBytes.length + 3] = b & 0xff;

      var u = hmacSha256(passwordBytes, input);
      var t = new Uint8Array(u);
      for (var i = 1; i < iterations; i++) {
        u = hmacSha256(passwordBytes, u);
        for (var j = 0; j < hashLen; j++) t[j] ^= u[j];
      }
      var take = Math.min(hashLen, length - offset);
      for (var k = 0; k < take; k++) out[offset + k] = t[k];
      offset += take;
    }
    return out;
  }

  // ------------------------------------------------------------- utilities

  function utf8(str) {
    if (window.TextEncoder) return new TextEncoder().encode(str);
    var bytes = [];
    for (var i = 0; i < str.length; i++) {
      var code = str.charCodeAt(i);
      if (code < 0x80) bytes.push(code);
      else if (code < 0x800) {
        bytes.push(0xc0 | (code >> 6), 0x80 | (code & 63));
      } else {
        bytes.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 63), 0x80 | (code & 63));
      }
    }
    return new Uint8Array(bytes);
  }

  function hexToBytes(hex) {
    if (!hex || hex.length % 2) return new Uint8Array(0);
    var out = new Uint8Array(hex.length / 2);
    for (var i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
    return out;
  }

  function bytesToHex(bytes) {
    var s = '';
    for (var i = 0; i < bytes.length; i++) {
      var h = bytes[i].toString(16);
      s += h.length === 1 ? '0' + h : h;
    }
    return s;
  }

  function bytesToBase64(bytes) {
    var binary = '';
    var chunk = 0x8000;
    for (var i = 0; i < bytes.length; i += chunk) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, Math.min(i + chunk, bytes.length)));
    }
    return btoa(binary);
  }

  function base64ToBytes(text) {
    var binary = atob(text);
    var out = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
    return out;
  }

  function randomBytes(count) {
    var out = new Uint8Array(count);
    if (window.crypto && window.crypto.getRandomValues) {
      window.crypto.getRandomValues(out);
      return out;
    }
    for (var i = 0; i < count; i++) out[i] = Math.floor(Math.random() * 256);
    return out;
  }

  function concat(a, b, c) {
    var len = a.length + b.length + (c ? c.length : 0);
    var out = new Uint8Array(len);
    out.set(a, 0);
    out.set(b, a.length);
    if (c) out.set(c, a.length + b.length);
    return out;
  }

  // -------------------------------------------------------------- sealing

  var MAC_LABEL = utf8('dpt.mac.v1');
  var ENC_LABEL = utf8('dpt.enc.v1');

  function subKeys(master) {
    return {
      mac: hmacSha256(master, MAC_LABEL),
      enc: hmacSha256(master, ENC_LABEL)
    };
  }

  function xorKeystream(encKey, iv, data) {
    var out = new Uint8Array(data.length);
    var counter = 0;
    for (var off = 0; off < data.length; off += 32) {
      var input = new Uint8Array(20);
      input.set(iv, 0);
      input[16] = (counter >>> 24) & 0xff;
      input[17] = (counter >>> 16) & 0xff;
      input[18] = (counter >>> 8) & 0xff;
      input[19] = counter & 0xff;
      var ks = hmacSha256(encKey, input);
      for (var i = 0; i < 32 && off + i < data.length; i++) out[off + i] = data[off + i] ^ ks[i];
      counter++;
    }
    return out;
  }

  function seal(master, plainBytes) {
    var keys = subKeys(master);
    var iv = randomBytes(16);
    var ct = xorKeystream(keys.enc, iv, plainBytes);
    var tag = hmacSha256(keys.mac, concat(iv, ct));
    return bytesToBase64(concat(iv, ct, tag.subarray(0, 16)));
  }

  function open(master, envelopeBase64) {
    var envelope;
    try { envelope = base64ToBytes(envelopeBase64); }
    catch (e) { return null; }
    if (envelope.length < 32) return null;

    var iv = envelope.subarray(0, 16);
    var ct = envelope.subarray(16, envelope.length - 16);
    var tag = envelope.subarray(envelope.length - 16);

    var keys = subKeys(master);
    var expect = hmacSha256(keys.mac, concat(iv, ct));
    var diff = 0;
    for (var i = 0; i < 16; i++) diff |= tag[i] ^ expect[i];
    if (diff !== 0) return null;

    return xorKeystream(keys.enc, iv, ct);
  }

  function deriveKey(token, saltHex, iterations) {
    return pbkdf2(utf8(token || ''), hexToBytes(saltHex || ''), iterations || 12000, 32);
  }

  function signRequest(master, method, target, timestamp, nonce, bodyBytes) {
    var macKey = hmacSha256(master, MAC_LABEL);
    var bodyHash = sha256(bodyBytes || new Uint8Array(0));
    var canonical = String(method || 'GET').toUpperCase() + '\n' +
                    String(target || '/') + '\n' +
                    String(timestamp || '') + '\n' +
                    String(nonce || '') + '\n' +
                    bytesToHex(bodyHash);
    return bytesToHex(hmacSha256(macKey, utf8(canonical)));
  }

  function utf8Decode(bytes) {
    if (window.TextDecoder) return new TextDecoder('utf-8').decode(bytes);
    var s = '';
    for (var i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
    try { return decodeURIComponent(escape(s)); } catch (e) { return s; }
  }

  return {
    sha256: sha256,
    hmacSha256: hmacSha256,
    pbkdf2: pbkdf2,
    deriveKey: deriveKey,
    signRequest: signRequest,
    seal: seal,
    open: open,
    utf8: utf8,
    utf8Decode: utf8Decode,
    hexToBytes: hexToBytes,
    bytesToHex: bytesToHex,
    bytesToBase64: bytesToBase64,
    base64ToBytes: base64ToBytes,
    randomBytes: randomBytes,
    randomHex: function (n) { return bytesToHex(randomBytes(n)); }
  };
})();
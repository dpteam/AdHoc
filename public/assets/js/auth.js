/* =========================================================================
   API client.
   Public board  : plain GET requests to the read-only endpoints.
   Admin board   : every call is HMAC-SHA256 signed (timestamp + nonce) and,
                   when allowed, the JSON body is sealed with HMAC-CTR.
                   The admin token itself never travels over the wire.
   ========================================================================= */
var API = (function () {
  'use strict';

  var ROLE = (document.documentElement.getAttribute('data-role') || 'public').toLowerCase();
  var IS_ADMIN = ROLE === 'admin';

  var token = '';
  var key = null;
  var info = null;
  var timeOffset = 0;
  var ready = false;

  function base() {
    var p = window.location.pathname || '/';
    if (p.charAt(p.length - 1) !== '/') p = p.substring(0, p.lastIndexOf('/') + 1);
    return p;
  }

  function url(target) {
    return base() + String(target || '/').replace(/^\/+/, '');
  }

  function xhr(method, address, body, timeout, headers, cb) {
    var done = false;
    var x = new XMLHttpRequest();

    function finish(err, data, raw) {
      if (done) return;
      done = true;
      cb(err, data, raw, x);
    }

    try {
      x.open(method, address, true);
      x.timeout = timeout || 12000;
      if (headers) {
        for (var k in headers) {
          if (headers.hasOwnProperty(k)) x.setRequestHeader(k, headers[k]);
        }
      }
      x.onreadystatechange = function () {
        if (x.readyState !== 4) return;
        var raw = x.responseText;
        var parsed = null;
        try { parsed = raw ? JSON.parse(raw) : null; } catch (e) { parsed = null; }
        if (x.status >= 200 && x.status < 300) {
          finish(null, parsed, raw);
        } else {
          finish({
            status: x.status,
            message: (parsed && parsed.error) || ('HTTP ' + x.status),
            auth: x.status === 401,
            blocked: x.status === 429
          }, parsed, raw);
        }
      };
      x.ontimeout = function () { finish({ status: 0, message: I18N.t('net.timeout') }); };
      x.onerror = function () { finish({ status: 0, message: I18N.t('net.offline') }); };
      x.send(body === null || body === undefined ? null : body);
    } catch (err) {
      finish({ status: 0, message: err.message || 'request failed' });
    }
  }

  function init(cb, explicitToken) {
    if (!IS_ADMIN) {
      ready = true;
      cb(null);
      return;
    }
    var t = explicitToken !== undefined ? explicitToken : (UI.read('dpt.token', '') || '');
    if (!t) {
      ready = false;
      cb({ auth: true, message: I18N.t('auth.required') });
      return;
    }
    xhr('GET', url('/api/auth'), null, 8000, null, function (err, data) {
      if (err) { cb(err); return; }
      if (!data || !data.salt) { cb({ status: 0, message: I18N.t('auth.badSalt') }); return; }
      try {
        var started = new Date().getTime();
        token = t;
        info = data;
        key = DptCrypto.deriveKey(token, data.salt, data.iterations);
        var localSec = Math.floor(new Date().getTime() / 1000);
        timeOffset = (Number(data.ts) || localSec) - localSec;
        ready = true;
        UI.store('dpt.token', token);
        if (window.console && console.debug) {
          console.debug('[auth] key derived in ' + (new Date().getTime() - started) + 'ms, clock skew ' + timeOffset + 's');
        }
        cb(null);
      } catch (e) {
        ready = false;
        cb({ status: 0, message: I18N.t('auth.cryptoError') + ': ' + e.message });
      }
    });
  }

  function logout() {
    token = '';
    key = null;
    info = null;
    ready = false;
    UI.store('dpt.token', null);
  }

  function request(method, target, payload, cb, opts) {
    opts = opts || {};
    var hasBody = payload !== undefined && payload !== null;

    if (!IS_ADMIN) {
      var plain = hasBody ? JSON.stringify(payload) : null;
      xhr(method, url(target), plain, opts.timeout,
        plain ? { 'Content-Type': 'application/json; charset=utf-8' } : null,
        function (err, data) { cb(err, data); });
      return;
    }

    if (!ready || !key) {
      cb({ auth: true, message: I18N.t('auth.expired') });
      return;
    }

    var wire = null;
    var encrypted = false;
    var headers = {};

    if (hasBody) {
      var json = JSON.stringify(payload);
      if (info && info.encrypt !== false) {
        wire = DptCrypto.seal(key, DptCrypto.utf8(json));
        encrypted = true;
        headers['X-Auth-Enc'] = '1';
        headers['Content-Type'] = 'text/plain; charset=utf-8';
      } else {
        wire = json;
        headers['Content-Type'] = 'application/json; charset=utf-8';
      }
    }

    var ts = String(Math.floor(new Date().getTime() / 1000) + timeOffset);
    var nonce = DptCrypto.randomHex(16);
    var wireBytes = wire === null ? new Uint8Array(0) : DptCrypto.utf8(wire);
    headers['X-Auth-Ts'] = ts;
    headers['X-Auth-Nonce'] = nonce;
    headers['X-Auth-Sig'] = DptCrypto.signRequest(key, method, target, ts, nonce, wireBytes);

    xhr(method, url(target), wire, opts.timeout, headers, function (err, data, raw, x) {
      if (!err && x && x.getResponseHeader && x.getResponseHeader('X-Enc-Resp') === '1') {
        if (!raw) { cb({ status: 0, message: I18N.t('auth.emptyResponse') }); return; }
        var bytes = DptCrypto.open(key, raw.replace(/\s+/g, ''));
        if (!bytes) { cb({ status: 0, message: I18N.t('auth.decryptFailed') }); return; }
        try {
          data = JSON.parse(DptCrypto.utf8Decode(bytes));
        } catch (e) {
          cb({ status: 0, message: I18N.t('auth.badResponse') });
          return;
        }
      }
      cb(err, data);
    });
  }

  function get(target, cb, timeout) {
    request('GET', target, null, cb, { timeout: timeout });
  }

  function post(target, payload, cb, timeout) {
    request('POST', target, payload || {}, cb, { timeout: timeout });
  }

  return {
    isAdmin: IS_ADMIN,
    role: ROLE,
    init: init,
    logout: logout,
    get: get,
    post: post,
    url: url,
    hasKey: function () { return ready; },
    info: function () { return info; }
  };
})();
/* =========================================================================
   UI helpers: DOM, formatting, toasts, modals, theme, tiny XHR client.
   Plain ES5 on purpose - the board should also survive old browsers.
   ========================================================================= */
var UI = (function () {
  'use strict';

  function $(sel, root) { return (root || document).querySelector(sel); }
  function $$(sel, root) {
    var list = (root || document).querySelectorAll(sel);
    var out = [];
    for (var i = 0; i < list.length; i++) out.push(list[i]);
    return out;
  }

  function esc(value) {
    if (value === null || value === undefined) return '';
    return String(value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function pad2(n) { return n < 10 ? '0' + n : '' + n; }

  function fmtInt(n) {
    n = Number(n) || 0;
    return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  }

  function fmtBytes(bytes) {
    bytes = Number(bytes) || 0;
    if (bytes < 1024) return bytes + ' B';
    var units = ['KB', 'MB', 'GB', 'TB'];
    var v = bytes;
    var u = -1;
    do { v /= 1024; u++; } while (v >= 1024 && u < units.length - 1);
    return (v >= 100 ? v.toFixed(0) : v.toFixed(v >= 10 ? 1 : 2)) + ' ' + units[u];
  }

  function fmtDuration(sec) {
    sec = Math.max(0, Math.floor(Number(sec) || 0));
    if (sec < 60) return sec + 's';
    var m = Math.floor(sec / 60), s = sec % 60;
    if (m < 60) return m + 'm ' + pad2(s) + 's';
    var h = Math.floor(m / 60), mm = m % 60;
    if (h < 24) return h + 'h ' + pad2(mm) + 'm';
    var d = Math.floor(h / 24), hh = h % 24;
    return d + 'd ' + pad2(hh) + 'h';
  }

  function parseIso(iso) {
    if (!iso) return null;
    var t = Date.parse(iso);
    return isNaN(t) ? null : t;
  }

  function ago(iso) {
    var t = parseIso(iso);
    if (t === null) return '–';
    var diff = Math.floor((Date.now() - t) / 1000);
    if (diff < 0) diff = 0;
    if (diff < 5) return 'сейчас';
    if (diff < 60) return diff + 'с назад';
    if (diff < 3600) return Math.floor(diff / 60) + 'м назад';
    if (diff < 86400) return Math.floor(diff / 3600) + 'ч назад';
    return Math.floor(diff / 86400) + 'д назад';
  }

  function clock(iso) {
    var t = parseIso(iso);
    if (t === null) return '–';
    var d = new Date(t);
    return pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
  }

  function dateTime(iso) {
    var t = parseIso(iso);
    if (t === null) return '–';
    var d = new Date(t);
    return pad2(d.getDate()) + '.' + pad2(d.getMonth() + 1) + '.' + d.getFullYear() + ' ' +
      pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  }

  function initials(name) {
    if (!name) return '?';
    var s = String(name).replace(/[^0-9a-zA-Zа-яА-ЯёЁ]/g, '');
    if (!s) return '?';
    return s.substr(0, 2).toUpperCase();
  }

  /* ------------------------------------------------------------------ storage */

  function store(key, value) {
    try {
      if (value === undefined) return null;
      if (value === null) { localStorage.removeItem(key); return null; }
      localStorage.setItem(key, JSON.stringify(value));
      return value;
    } catch (e) { return null; }
  }

  function read(key, fallback) {
    try {
      var raw = localStorage.getItem(key);
      if (raw === null) return fallback;
      return JSON.parse(raw);
    } catch (e) { return fallback; }
  }

  /* --------------------------------------------------------------------- http */

  function request(method, url, body, opts, cb) {
    opts = opts || {};
    var done = false;
    var xhr = new XMLHttpRequest();

    function finish(err, data) {
      if (done) return;
      done = true;
      cb(err, data);
    }

    try {
      xhr.open(method, url, true);
      xhr.setRequestHeader('Accept', 'application/json');
      if (body !== null && body !== undefined) xhr.setRequestHeader('Content-Type', 'application/json; charset=utf-8');
      if (opts.token) xhr.setRequestHeader('X-Admin-Token', opts.token);
      xhr.timeout = opts.timeout || 12000;
      xhr.onreadystatechange = function () {
        if (xhr.readyState !== 4) return;
        var data = null;
        try { data = xhr.responseText ? JSON.parse(xhr.responseText) : null; } catch (e) { data = null; }
        if (xhr.status >= 200 && xhr.status < 300) finish(null, data);
        else finish({ status: xhr.status, message: (data && data.error) || ('HTTP ' + xhr.status) }, data);
      };
      xhr.ontimeout = function () { finish({ status: 0, message: 'таймаут запроса' }); };
      xhr.onerror = function () { finish({ status: 0, message: 'нет связи с сервером' }); };
      xhr.send(body === null || body === undefined ? null : JSON.stringify(body));
    } catch (e) {
      finish({ status: 0, message: e.message || 'request failed' });
    }
  }

  function getJson(url, cb, timeout) {
    request('GET', url, null, { timeout: timeout }, cb);
  }

  function postJson(url, payload, cb, token) {
    request('POST', url, payload || {}, { token: token }, cb);
  }

  /* ------------------------------------------------------------------- toasts */

  var toastSeq = 0;

  function toast(type, title, text, ttl) {
    var host = document.getElementById('toasts');
    if (!host) return;
    var kinds = { ok: 'fa-circle-check', join: 'fa-user-plus', leave: 'fa-user-minus',
                  game: 'fa-gamepad', warn: 'fa-triangle-exclamation', err: 'fa-circle-exclamation',
                  info: 'fa-circle-info', chat: 'fa-comment' };
    var cls = type === 'join' || type === 'game' ? 'ok' : (type === 'leave' ? 'warn' : (type === 'chat' ? '' : type));

    var node = document.createElement('div');
    node.className = 'toast ' + (cls || '');
    node.setAttribute('data-id', ++toastSeq);
    node.innerHTML = '<i class="fas ' + (kinds[type] || 'fa-circle-info') + '"></i>' +
      '<div><b>' + esc(title) + '</b>' + (text ? '<small>' + esc(text) + '</small>' : '') + '</div>';
    host.appendChild(node);

    while (host.children.length > 5) host.removeChild(host.firstChild);

    var life = ttl || 5200;
    var timer = setTimeout(function () {
      node.className += ' out';
      setTimeout(function () { if (node.parentNode) node.parentNode.removeChild(node); }, 240);
    }, life);

    node.addEventListener('click', function () {
      clearTimeout(timer);
      if (node.parentNode) node.parentNode.removeChild(node);
    });
  }

  /* ------------------------------------------------------------------- modals */

  var activeModal = null;

  function closeModal() {
    if (!activeModal) return;
    var api = activeModal;
    activeModal = null;
    api.close();
  }

  function modal(opts) {
    opts = opts || {};
    var root = document.getElementById('modal-root');
    if (!root) return null;
    closeModal();

    root.hidden = false;
    root.innerHTML = '';

    var box = document.createElement('div');
    box.className = 'modal' + (opts.narrow ? ' narrow' : '');
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-modal', 'true');
    box.innerHTML =
      '<div class="modal-head"><h3>' + (opts.icon ? '<i class="fas ' + esc(opts.icon) + '"></i>' : '') +
      esc(opts.title || '') + '</h3><button class="x" title="Закрыть (Esc)"><i class="fas fa-xmark"></i></button></div>' +
      '<div class="modal-body"></div><div class="modal-foot"></div>';

    root.appendChild(box);

    var body = box.querySelector('.modal-body');
    var foot = box.querySelector('.modal-foot');
    var xBtn = box.querySelector('.x');

    var api = {
      close: close,
      box: box,
      body: body,
      foot: foot
    };

    function close() {
      root.hidden = true;
      root.innerHTML = '';
      document.removeEventListener('keydown', onKey, true);
      if (activeModal === api) activeModal = null;
      if (opts.onClose) opts.onClose();
    }

    function onKey(e) {
      if (e.key === 'Escape') {
        e.stopPropagation();
        close();
      }
    }

    xBtn.addEventListener('click', close);
    root.addEventListener('click', function (e) {
      if (e.target === root) close();
    });
    document.addEventListener('keydown', onKey, true);

    var actions = opts.actions || [];
    for (var i = 0; i < actions.length; i++) {
      (function (a) {
        var b = document.createElement('button');
        b.className = 'btn ' + (a.cls || 'ghost');
        b.innerHTML = (a.icon ? '<i class="fas ' + a.icon + '"></i> ' : '') + esc(a.label);
        if (a.align === 'right') b.className += ' right';
        b.addEventListener('click', function () {
          if (a.onClick) a.onClick(close, api);
          else close();
        });
        foot.appendChild(b);
      })(actions[i]);
    }
    if (!actions.length) foot.parentNode.removeChild(foot);

    if (opts.html) body.innerHTML = opts.html;
    if (opts.onReady) opts.onReady(api);

    activeModal = api;
    return api;
  }

  function confirm(title, text, okLabel, onOk, danger) {
    return modal({
      title: title,
      icon: danger ? 'fa-triangle-exclamation' : 'fa-circle-question',
      narrow: true,
      html: '<p style="margin:2px 0 4px;font-size:12.5px">' + esc(text) + '</p>',
      actions: [
        { label: 'Отмена', cls: 'ghost', onClick: function (close) { close(); } },
        { label: okLabel || 'OK', cls: danger ? 'danger right' : 'right', onClick: function (close) { close(); if (onOk) onOk(); } }
      ]
    });
  }

  /* -------------------------------------------------------------------- theme */

  var THEMES = ['light', 'dark', 'oled'];

  function setTheme(theme) {
    if (THEMES.indexOf(theme) < 0) theme = 'light';
    document.documentElement.setAttribute('data-theme', theme);
    var meta = document.querySelector('meta[name="color-scheme"]');
    if (meta) meta.setAttribute('content', theme === 'light' ? 'light dark' : 'dark');
    store('dpt.theme', theme);
    return theme;
  }

  function currentTheme() {
    return document.documentElement.getAttribute('data-theme') || 'light';
  }

  function nextTheme() {
    var i = THEMES.indexOf(currentTheme());
    return THEMES[(i + 1) % THEMES.length];
  }

  return {
    $: $, $$: $$,
    esc: esc,
    fmtInt: fmtInt,
    fmtBytes: fmtBytes,
    fmtDuration: fmtDuration,
    ago: ago,
    clock: clock,
    dateTime: dateTime,
    initials: initials,
    store: store,
    read: read,
    getJson: getJson,
    postJson: postJson,
    toast: toast,
    modal: modal,
    closeModal: closeModal,
    confirm: confirm,
    setTheme: setTheme,
    currentTheme: currentTheme,
    nextTheme: nextTheme,
    themes: THEMES
  };
})();
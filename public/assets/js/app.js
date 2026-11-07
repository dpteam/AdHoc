/* =========================================================================
   DPT.PSP board - application logic (ES5, no frameworks).

   One code base for both faces:
     data-role="public"  read-only board, redacted API, no moderation controls
     data-role="admin"   full board, signed + sealed API, auth gate
   ========================================================================= */
(function () {
  'use strict';

  var IS_ADMIN = !!(window.API && API.isAdmin);

  var App = {
    status: null,
    settings: null,
    openGames: {},
    gamesSig: '',
    lastEventSeq: 0,
    lastLogSeq: 0,
    logLines: [],
    logPaused: false,
    conn: 'init',
    failures: 0,
    pollTimer: null,
    logTimer: null,
    started: false,
    search: '',
    onlyActive: true,
    sort: 'players',
    notify: true
  };

  var DEFAULTS = {
    theme: 'light',
    sound: true,
    volume: 0.55,
    events: { join: true, leave: true, game: true, chat: true, error: true },
    refresh: 2000,
    logLevel: 'all',
    onlyActive: true
  };

  /* ------------------------------------------------------------- settings */

  function loadSettings() {
    var s = UI.read('dpt.settings', null);
    var out = {
      theme: DEFAULTS.theme,
      sound: DEFAULTS.sound,
      volume: DEFAULTS.volume,
      events: { join: true, leave: true, game: true, chat: true, error: true },
      refresh: DEFAULTS.refresh,
      logLevel: DEFAULTS.logLevel,
      onlyActive: DEFAULTS.onlyActive
    };
    if (s && typeof s === 'object') {
      if (s.theme) out.theme = s.theme;
      if (typeof s.sound === 'boolean') out.sound = s.sound;
      if (typeof s.volume === 'number') out.volume = s.volume;
      if (s.events) {
        for (var k in out.events) {
          if (out.events.hasOwnProperty(k) && typeof s.events[k] === 'boolean') out.events[k] = s.events[k];
        }
      }
      if (s.refresh !== undefined && s.refresh !== null) out.refresh = Number(s.refresh) || 0;
      if (s.logLevel) out.logLevel = s.logLevel;
      if (typeof s.onlyActive === 'boolean') out.onlyActive = s.onlyActive;
    }
    return out;
  }

  function saveSettings() {
    UI.store('dpt.settings', App.settings);
  }

  function applySettings() {
    var s = App.settings;
    UI.setTheme(s.theme);
    SoundFX.setEnabled(s.sound);
    SoundFX.setVolume(s.volume);
    SoundFX.setEvents(s.events);
    updateSoundButton();
    schedulePoll(true);
  }

  function updateSoundButton() {
    var btn = UI.$('#sound-btn');
    if (!btn) return;
    var on = App.settings.sound;
    btn.innerHTML = '<i class="fas ' + (on ? 'fa-volume-high' : 'fa-volume-mute') + '"></i>';
    btn.title = on ? I18N.t('sound.on') : I18N.t('sound.off');
    btn.className = 'icon-btn' + (on ? '' : ' muted');
  }

  function updateThemeButton() {
    var btn = UI.$('#theme-btn');
    if (!btn) return;
    var theme = UI.currentTheme();
    var icon = theme === 'light' ? 'fa-moon' : (theme === 'dark' ? 'fa-circle-half-stroke' : 'fa-sun');
    btn.innerHTML = '<i class="fas ' + icon + '"></i>';
    btn.title = I18N.t('theme.set', theme === 'light' ? I18N.t('theme.light') : theme === 'dark' ? I18N.t('theme.dark') : 'OLED');
  }

  /* ------------------------------------------------------------- auth gate */

  function showGate(on, message) {
    var gate = UI.$('#auth-gate');
    if (!gate) return;
    gate.hidden = !on;
    if (on) {
      var err = UI.$('#auth-error');
      if (err) err.textContent = message || '';
      var input = UI.$('#auth-token');
      if (input) {
        input.value = '';
        setTimeout(function () { try { input.focus(); } catch (e) { } }, 60);
      }
    }
  }

  function bindGate() {
    var form = UI.$('#auth-form');
    if (!form) return;
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var input = UI.$('#auth-token');
      var token = (input && input.value || '').trim();
      if (!token) return;
      var btn = UI.$('#auth-go');
      if (btn) btn.disabled = true;
      var errBox = UI.$('#auth-error');
      if (errBox) errBox.textContent = I18N.t('auth.checking');
      API.init(function (err) {
        if (btn) btn.disabled = false;
        if (err) {
          if (errBox) errBox.textContent = err.message || I18N.t('auth.error');
          SoundFX.play('error');
          return;
        }
        showGate(false);
        SoundFX.play('online');
        start();
        poll();
        logTick();
      }, token);
    });
  }

  /* ---------------------------------------------------------------- polling */

  function schedulePoll(immediate) {
    if (App.pollTimer) {
      clearTimeout(App.pollTimer);
      App.pollTimer = null;
    }
    App.pollTimer = setTimeout(poll, immediate ? 30 : nextDelay());
  }

  function nextDelay() {
    var base = Number(App.settings.refresh) || 0;
    if (base <= 0) base = 2000;
    if (App.failures > 0) return Math.min(15000, 1200 * App.failures);
    if (document.hidden) return Math.max(4000, base * 3);
    return base;
  }

  function poll() {
    API.get('/api/status', function (err, data) {
      if (err) {
        onPollFailed(err);
        return;
      }
      onPollOk(data);
      schedulePoll(false);
    }, 9000);
  }

  function onPollFailed(err) {
    if (err && err.auth && IS_ADMIN) {
      App.conn = 'bad';
      setConnPill('bad', I18N.t('auth.required'));
      showGate(true, I18N.t('auth.required'));
      return;
    }
    App.failures++;
    if (App.conn !== 'bad') {
      App.conn = 'bad';
      setConnPill('bad', I18N.t('conn.bad'));
      SoundFX.play('offline');
      UI.toast('err', I18N.t('conn.lost'), (err && err.message) || I18N.t('conn.retry'), 6000);
    }
    schedulePoll(false);
  }

  function onPollOk(data) {
    var wasBad = App.conn === 'bad';
    App.failures = 0;
    App.conn = 'ok';
    App.status = data;

    if (!App.initialised) {
      applyServerDefaults(data);
      App.initialised = true;
    }

    setConnPill('ok', I18N.t('conn.ok'));
    if (wasBad) {
      SoundFX.play('online');
      UI.toast('ok', I18N.t('conn.restored'), I18N.t('conn.back'), 3500);
    }

    handleEvents(data.events || []);
    renderTop(data);
    renderStats(data);
    renderGames(data.games || []);
    renderRecent(data.recent || []);
    renderEvents(data.events || []);
    renderFooter(data);
    renderAbout(data);
  }

  var serverDefaultsApplied = false;

  function applyServerDefaults(data) {
    if (serverDefaultsApplied) return;
    serverDefaultsApplied = true;
    var cfg = data.config || {};
    var stored = UI.read('dpt.settings', null);
    if (!stored) {
      if (cfg.theme) App.settings.theme = cfg.theme;
      if (typeof cfg.sound === 'boolean') App.settings.sound = cfg.sound;
      if (cfg.refreshMs) App.settings.refresh = cfg.refreshMs;
      saveSettings();
      applySettings();
    }
  }

  function setConnPill(state, text) {
    var pill = UI.$('#conn-pill');
    if (!pill) return;
    pill.className = 'conn-pill ' + (state === 'ok' ? 'ok' : state === 'bad' ? 'bad' : '');
    var txt = pill.querySelector('.txt');
    if (txt) txt.textContent = text;
  }

  /* ------------------------------------------------------------------ events */

  function handleEvents(events) {
    var maxSeq = App.lastEventSeq;
    var fresh = [];
    for (var i = 0; i < events.length; i++) {
      var e = events[i];
      if (e.seq > App.lastEventSeq) {
        fresh.push(e);
        if (e.seq > maxSeq) maxSeq = e.seq;
      }
    }
    if (App.lastEventSeq === 0) {
      App.lastEventSeq = maxSeq;
      return;
    }
    App.lastEventSeq = maxSeq;
    if (fresh.length > 8) fresh = fresh.slice(fresh.length - 8);
    for (var j = 0; j < fresh.length; j++) notifyEvent(fresh[j]);
  }

  function notifyEvent(e) {
    if (!App.notify) return;
    switch (e.type) {
      case 'join':
        SoundFX.play('join');
        UI.toast('join', e.player || I18N.t('toast.joined'), e.text, 4500);
        break;
      case 'leave':
        SoundFX.play('leave');
        UI.toast('leave', e.player || I18N.t('toast.left'), e.text, 4200);
        break;
      case 'game.new':
        SoundFX.play('game');
        UI.toast('game', I18N.t('toast.newGame'), e.text, 5200);
        break;
      case 'chat':
        SoundFX.play('chat');
        break;
      case 'error':
      case 'ban':
        SoundFX.play('error');
        UI.toast('err', I18N.t('toast.warn'), e.text, 6000);
        break;
      case 'kick':
        SoundFX.play('error');
        UI.toast('warn', I18N.t('chip.kick'), e.text, 5000);
        break;
      case 'hint':
        // server advice (e.g. NAT hairpin) - feed only, no noise
        break;
      default:
        SoundFX.play('chip');
        break;
    }
  }

  /* ------------------------------------------------------------------- render */

  function renderTop(data) {
    var s = data.stats || {};
    var server = data.server || {};
    setText('#t-online', UI.fmtInt(s.online));
    setText('#t-games', UI.fmtInt(s.games));
    setText('#t-peak', UI.fmtInt(s.peak));
    setText('#t-groups', UI.fmtInt(s.groups));
    setText('#t-uptime', UI.fmtDuration(server.uptimeSec));
    setText('#t-port-adhoc', server.adhocPort || '–');
    setText('#t-port-web', server.webPort || '–');

    var relay = data.relay || {};
    setText('#t-relay', relay.enabled ? ('on ' + UI.fmtInt(relay.sessions || 0)) : 'off');
  }

  function setText(sel, value) {
    var el = UI.$(sel);
    if (el) el.textContent = value;
  }

  function renderStats(data) {
    var s = data.stats || {};
    var cfg = data.config || {};
    var life = data.lifetime || {};
    setText('#s-online', UI.fmtInt(s.online));
    setText('#s-online-sub', I18N.t('stats.onlineSub', UI.fmtInt(cfg.maxUsers || 0)));
    setText('#s-games', UI.fmtInt(s.games));
    setText('#s-games-sub', I18N.t('stats.gamesSub', UI.fmtInt(s.groups)));
    // peak and connection totals come from data/stats.json, so a restart does not reset them
    setText('#s-peak', UI.fmtInt(life.peakOnline || s.peak));
    setText('#s-peak-sub', IS_ADMIN ? I18N.t('stats.peakSubAdmin', (cfg.timeoutSec || 0)) : I18N.t('stats.peakSub'));
    setText('#s-conn', UI.fmtInt(life.connections || s.connections));
    setText('#s-conn-sub', I18N.t('stats.connSub', UI.fmtInt(life.sessions || 0), UI.fmtDuration(life.playSeconds || 0)));

    if (IS_ADMIN) {
      var total = (s.bytesIn || 0) + (s.bytesOut || 0);
      setText('#s-traffic', UI.fmtBytes(total));
      setText('#s-traffic-sub', '↓ ' + UI.fmtBytes(s.bytesIn) + ' ↑ ' + UI.fmtBytes(s.bytesOut));
      setText('#s-health', (s.protocolErrors || 0) === 0 ? 'ok' : I18N.t('stats.healthWarn'));
      setText('#s-health-sub', I18N.t('stats.healthSub', UI.fmtInt(s.protocolErrors), UI.fmtInt(s.bans)));
    } else {
      setText('#s-db', UI.fmtInt(cfg.dbGames));
      setText('#s-db-sub', I18N.t('stats.dbSub', UI.fmtInt(cfg.dbCrosslinks)));
      setText('#s-health', 'ok');
      setText('#s-health-sub', I18N.t('stats.healthSubPublic'));
    }
  }

  function renderFooter(data) {
    var server = data.server || {};
    setText('#f-name', server.name || 'DPT.PSP AdHoc Server');
    setText('#f-version', 'v' + (server.version || '?'));
    setText('#f-build', server.build || '–');
    setText('#f-uptime', 'uptime ' + UI.fmtDuration(server.uptimeSec));
    if (IS_ADMIN) {
      setText('#f-host', (server.host || '') + ' · ' + (server.os || '') + ' · mem ' + (server.memoryMB || 0) + 'MB');
    } else {
      setText('#f-host', 'public board · adhoc ' + (server.adhocPort || '?'));
    }
  }

  function renderAbout(data) {
    var server = data.server || {};
    var relay = data.relay || {};
    var life = data.lifetime || {};
    setText('#a-adhoc', server.adhocPort || '–');
    setText('#a-version', 'v' + (server.version || '?'));
    setText('#a-uptime', UI.fmtDuration(server.uptimeSec));
    setText('#a-relay', relay.enabled ? I18N.t('about.relayOn', UI.fmtInt(relay.sessions || 0)) : I18N.t('about.relayOff'));

    setText('#a-peak', I18N.t('about.peakValue', UI.fmtInt(life.peakOnline || 0)));
    setText('#a-sessions', I18N.t('about.sessionsValue', UI.fmtInt(life.sessions || 0), UI.fmtInt(life.starts || 0)));
    setText('#a-playtime', I18N.t('about.playtimeValue', UI.fmtDuration(life.playSeconds || 0), UI.fmtInt(life.games || 0)));
    setText('#a-first', life.firstStartUtc ? UI.dateTime(life.firstStartUtc) : '–');

    var top = data.topGames || [];
    var host = UI.$('#a-top');
    if (host) {
      if (!top.length) { host.textContent = ''; }
      else {
        var parts = [];
        for (var i = 0; i < top.length && i < 5; i++) {
          parts.push(I18N.t('about.topItem', UI.esc(top[i].name), UI.fmtInt(top[i].peak), UI.fmtDuration(top[i].playSeconds || 0)));
        }
        host.innerHTML = '<b>' + I18N.t('about.top') + ':</b> ' + parts.join(' · ');
      }
    }
  }

  function gamesSig(games) {
    var parts = [];
    for (var i = 0; i < games.length; i++) {
      var g = games[i];
      var groups = [];
      for (var j = 0; j < (g.groups || []).length; j++) {
        var gr = g.groups[j];
        var mem = [];
        for (var k = 0; k < (gr.members || []).length; k++) mem.push((gr.members[k].id || '') + ':' + gr.members[k].name);
        groups.push(gr.name + '|' + (gr.host || '') + '|' + mem.join(','));
      }
      var idle = [];
      for (var z = 0; z < (g.idle || []).length; z++) idle.push((g.idle[z].id || '') + ':' + g.idle[z].name);
      parts.push([g.id, g.name, g.known ? 1 : 0, g.players, groups.join('/'), idle.join(',')].join('~'));
    }
    return parts.join(';');
  }

  function renderGames(games) {
    var host = UI.$('#games-list');
    if (!host) return;
    var filtered = filterGames(games);

    setText('#games-foot', I18N.t('lobby.foot', filtered.length, games.length,
      (App.status && App.status.stats ? App.status.stats.online : 0)));

    var sig = gamesSig(games) + '#f=' + filtered.length + '#q=' + App.search + '#s=' + App.sort +
      '#a=' + (App.onlyActive ? 1 : 0) + '#' + (IS_ADMIN ? 'a' : 'p');
    if (sig === App.gamesSig) {
      refreshLiveCounts(games);
      return;
    }
    App.gamesSig = sig;

    if (!filtered.length) {
      host.innerHTML = '<div class="empty"><i class="fas fa-ghost"></i> ' +
        (games.length ? I18N.t('lobby.empty') : I18N.t('lobby.emptyAll')) + '</div>';
      return;
    }

    var html = [];
    for (var i = 0; i < filtered.length; i++) html.push(renderGame(filtered[i]));
    host.innerHTML = html.join('');
    refreshLiveCounts(games);
  }

  function filterGames(games) {
    var q = (App.search || '').trim().toLowerCase();
    var out = [];
    for (var i = 0; i < games.length; i++) {
      var g = games[i];
      if (App.onlyActive && !g.players) continue;
      if (q && !gameMatches(g, q)) continue;
      out.push(g);
    }
    out.sort(function (a, b) {
      if (App.sort === 'name') return String(a.name).localeCompare(String(b.name), 'ru');
      if (App.sort === 'activity') return String(b.lastActivityUtc).localeCompare(String(a.lastActivityUtc));
      if (b.players !== a.players) return b.players - a.players;
      return String(a.name).localeCompare(String(b.name), 'ru');
    });
    return out;
  }

  function gameMatches(g, q) {
    if (String(g.name || '').toLowerCase().indexOf(q) >= 0) return true;
    if (String(g.id || '').toLowerCase().indexOf(q) >= 0) return true;
    var i, j;
    for (i = 0; i < (g.groups || []).length; i++) {
      if (String(g.groups[i].name || '').toLowerCase().indexOf(q) >= 0) return true;
      if (String(g.groups[i].host || '').toLowerCase().indexOf(q) >= 0) return true;
      for (j = 0; j < g.groups[i].members.length; j++) {
        var m = g.groups[i].members[j];
        if (String(m.name || '').toLowerCase().indexOf(q) >= 0) return true;
        if (String(m.mac || '').toLowerCase().indexOf(q) >= 0) return true;
        if (String(m.ip || '').toLowerCase().indexOf(q) >= 0) return true;
      }
    }
    for (i = 0; i < (g.idle || []).length; i++) {
      var p = g.idle[i];
      if (String(p.name || '').toLowerCase().indexOf(q) >= 0) return true;
      if (String(p.mac || '').toLowerCase().indexOf(q) >= 0) return true;
    }
    return false;
  }

  function renderGame(g) {
    var open = !!App.openGames[g.id];
    var html = [];
    html.push('<article class="game' + (open ? ' open' : '') + (g.players ? ' live' : '') + '" data-game="' + UI.esc(g.id) + '">');
    html.push('<button class="game-head" type="button" aria-expanded="' + (open ? 'true' : 'false') + '" data-toggle="' + UI.esc(g.id) + '">');
    html.push('<span class="chev"><i class="fas fa-chevron-right"></i></span>');
    html.push('<span class="game-main">');
    html.push('<span class="game-title' + (g.known ? '' : ' unknown') + '">' + UI.esc(g.name || g.id) +
      (g.known ? '' : ' <i class="fas fa-circle-question" title="' + I18N.t('chip.unknown') + '"></i>') + '</span>');
    html.push('<span class="game-meta-line">');
    html.push('<span class="chip id">' + UI.esc(g.id) + '</span>');
    if (g.rawId && g.rawId !== g.id) html.push('<span class="chip link" title="' + I18N.t('chip.crosslink') + '"><i class="fas fa-link"></i>' + UI.esc(g.rawId) + '</span>');
    if (!g.known && IS_ADMIN) html.push('<span class="chip" data-name-game="' + UI.esc(g.id) + '" role="button" tabindex="0" title="' + I18N.t('chip.editName') + '"><i class="fas fa-pen"></i> ' + I18N.t('chip.editName') + '</span>');
    html.push('<span>' + I18N.t('lobby.peakTotal', UI.fmtInt(g.peak), UI.fmtInt(g.totalPlayers)) + '</span>');
    html.push('<span class="game-time" data-activity="' + UI.esc(g.id) + '">' + UI.ago(g.lastActivityUtc) + '</span>');
    html.push('</span></span>');
    html.push('<span class="badges">');
    html.push('<span class="badge players' + (g.players ? ' has' : '') + '"><i class="fas fa-users"></i>' + UI.fmtInt(g.players) + '</span>');
    html.push('<span class="badge groups' + ((g.groups || []).length ? ' has' : '') + '"><i class="fas fa-layer-group"></i>' + UI.fmtInt((g.groups || []).length) + '</span>');
    html.push('</span>');
    html.push('</button>');
    html.push('<div class="game-details">');

    if (!g.players) {
      html.push('<div class="empty small">' + I18N.t('lobby.emptyGame') + '</div>');
    } else {
      for (var i = 0; i < (g.groups || []).length; i++) {
        var gr = g.groups[i];
        html.push('<div class="group">');
        html.push('<div class="group-head"><i class="fas fa-signal"></i> ' + I18N.t('lobby.group') + ' <b>' + UI.esc(gr.name) + '</b>');
        html.push('<span class="chip">' + I18N.t('lobby.players', UI.fmtInt(gr.members.length)) + '</span>');
        if (gr.host) html.push('<span class="chip" title="' + I18N.t('lobby.host') + '"><i class="fas fa-crown" style="color:var(--gold)"></i> ' + UI.esc(gr.host) + '</span>');
        html.push('</div>');
        html.push('<table class="players"><tbody>');
        for (var j = 0; j < gr.members.length; j++) html.push(playerRow(gr.members[j], gr.host === gr.members[j].mac));
        html.push('</tbody></table></div>');
      }
      if ((g.idle || []).length) {
        html.push('<div class="group">');
        html.push('<div class="group-head"><i class="fas fa-hourglass-half"></i> ' + I18N.t('lobby.waiting'));
        html.push('<span class="chip">' + UI.fmtInt(g.idle.length) + '</span></div>');
        html.push('<table class="players"><tbody>');
        for (var k = 0; k < g.idle.length; k++) html.push(playerRow(g.idle[k], false));
        html.push('</tbody></table></div>');
      }
    }

    html.push('</div></article>');
    return html.join('');
  }

  function playerRow(p, isHost) {
    var crown = isHost ? '<span class="crown" title="' + I18N.t('lobby.hostShort') + '"><i class="fas fa-crown"></i></span>' : '';
    var hairpin = (IS_ADMIN && p.hairpin)
      ? '<span class="chip hairpin" title="' + I18N.t('chip.hairpin') + '"><i class="fas fa-arrows-rotate"></i> ' + I18N.t('chip.hairpinLabel') + '</span>'
      : '';
    var transport = '';
    if (IS_ADMIN && App.status && App.status.relay && App.status.relay.enabled) {
      var relayOn = (p.relaySessions || 0) > 0;
      transport = relayOn
        ? '<span class="chip relay" title="' + I18N.t('chip.relay') + '"><i class="fas fa-tower-broadcast"></i> relay</span>'
        : '<span class="chip p2p" title="' + I18N.t('chip.p2p') + '"><i class="fas fa-diagram-project"></i> p2p</span>';
    }
    var html = '<tr>' +
      '<td class="nick">' + UI.esc(p.name) + crown + hairpin + transport + '</td>';

    if (IS_ADMIN) {
      var idleWarn = (p.idleSec || 0) > 15 ? ' warn' : '';
      html += '<td class="mac">' + UI.esc(p.mac) + '</td>' +
        '<td class="ip">' + UI.esc(p.ip) + '</td>' +
        '<td class="dur" data-dur="' + UI.esc(p.id) + '">' + UI.fmtDuration(p.connectedSec) + '</td>' +
        '<td class="idle' + idleWarn + '" data-idle="' + UI.esc(p.id) + '">idle ' + UI.fmtDuration(p.idleSec) + '</td>' +
        '<td class="act"><button class="btn ghost sm" data-kick="' + UI.esc(p.id) + '" data-kick-name="' + UI.esc(p.name) +
        '" data-kick-mac="' + UI.esc(p.mac) + '" title="' + I18N.t('chip.kick') + '"><i class="fas fa-plug-circle-xmark"></i></button></td>';
    } else {
      html += '<td class="dur" data-dur="' + UI.esc(p.name) + '">' + UI.fmtDuration(p.connectedSec) + '</td>';
    }
    html += '</tr>';
    return html;
  }

  function refreshLiveCounts(games) {
    var byKey = {};
    for (var i = 0; i < games.length; i++) {
      var g = games[i];
      for (var j = 0; j < (g.groups || []).length; j++)
        for (var k = 0; k < g.groups[j].members.length; k++) {
          var m = g.groups[j].members[k];
          byKey[m.id || m.name] = m;
        }
      for (var z = 0; z < (g.idle || []).length; z++) byKey[g.idle[z].id || g.idle[z].name] = g.idle[z];
    }

    var durs = UI.$$('[data-dur]');
    for (var a = 0; a < durs.length; a++) {
      var p = byKey[durs[a].getAttribute('data-dur')];
      if (p) durs[a].textContent = UI.fmtDuration(p.connectedSec);
    }
    var idles = UI.$$('[data-idle]');
    for (var b = 0; b < idles.length; b++) {
      var q = byKey[idles[b].getAttribute('data-idle')];
      if (q) {
        idles[b].textContent = 'idle ' + UI.fmtDuration(q.idleSec);
        idles[b].className = 'idle' + ((q.idleSec || 0) > 15 ? ' warn' : '');
      }
    }
    var acts = UI.$$('[data-activity]');
    for (var c = 0; c < acts.length; c++) {
      var gid = acts[c].getAttribute('data-activity');
      for (var d = 0; d < games.length; d++) {
        if (games[d].id === gid) { acts[c].textContent = UI.ago(games[d].lastActivityUtc); break; }
      }
    }
  }

  function renderRecent(recent) {
    var host = UI.$('#recent-list');
    if (!host) return;
    setText('#recent-count', recent.length);
    if (!recent.length) {
      host.innerHTML = '<div class="empty small">' + I18N.t('recent.empty') + '</div>';
      return;
    }
    var html = [];
    for (var i = 0; i < recent.length && i < 25; i++) {
      var r = recent[i];
      var ok = r.reason === 'peer-closed' || r.reason === 'client-request';
      html.push('<div class="li">' +
        '<span class="avatar">' + UI.esc(UI.initials(r.name)) + '</span>' +
        '<span class="who"><b>' + UI.esc(r.name) + '</b>' +
        '<span>' + UI.esc(r.gameName || r.gameId) + (r.group ? ' · ' + UI.esc(r.group) : '') + '</span></span>' +
        '<span class="when" title="' + UI.esc(UI.dateTime(r.disconnectUtc)) + '">' + UI.fmtDuration(r.durationSec) + '</span>' +
        '<span class="tag ' + (ok ? 'ok' : 'warn') + '">' + UI.esc(reasonText(r.reason)) + '</span>' +
        '</div>');
    }
    host.innerHTML = html.join('');
  }

  function reasonText(reason) {
    if (!reason) return I18N.t('reason.unknown');
    if (reason.indexOf('socket-') === 0) return I18N.t('reason.socket');
    switch (reason) {
      case 'peer-closed': return I18N.t('reason.peerClosed');
      case 'client-request': return I18N.t('reason.clientRequest');
      case 'timeout': return I18N.t('reason.timeout');
      case 'kicked': return I18N.t('reason.kicked');
      case 'shutdown': return I18N.t('reason.shutdown');
      case 'session-lifetime': return I18N.t('reason.lifetime');
      case 'protocol-error': return I18N.t('reason.protocol');
      case 'send-failed': return I18N.t('reason.sendFailed');
      case 'io-error': return I18N.t('reason.ioError');
      case 'exception': return I18N.t('reason.exception');
      case 'rx-overflow': return I18N.t('reason.overflow');
      case 'duplicate-mac': return I18N.t('reason.duplicateMac');
      case 'thread-start-failed': return I18N.t('reason.thread');
      case 'closing-sweep': return I18N.t('reason.sweep');
      default: return reason;
    }
  }

  function renderEvents(events) {
    var host = UI.$('#events-list');
    if (!host) return;
    if (!events.length) {
      host.innerHTML = '<div class="empty small">' + I18N.t('events.empty') + '</div>';
      return;
    }
    var html = [];
    var shown = 0;
    for (var i = events.length - 1; i >= 0 && shown < 40; i--, shown++) {
      var e = events[i];
      var clsType = '';
      if (e.type === 'join') clsType = 't-join';
      else if (e.type === 'leave') clsType = 't-leave';
      else if (e.type === 'game.new') clsType = 't-game new';
      else if (e.type === 'chat') clsType = 't-chat';
      else if (e.type === 'error' || e.type === 'ban' || e.type === 'kick') clsType = 't-error';
      else if (e.type === 'group.join' || e.type === 'group.leave') clsType = 't-group';
      else if (e.type === 'hint') clsType = 't-hint';

      var icon = 'fa-circle';
      if (e.type === 'join') icon = 'fa-right-to-bracket';
      else if (e.type === 'leave') icon = 'fa-right-from-bracket';
      else if (e.type === 'game.new') icon = 'fa-gamepad';
      else if (e.type === 'chat') icon = 'fa-comment';
      else if (e.type === 'error') icon = 'fa-triangle-exclamation';
      else if (e.type === 'ban') icon = 'fa-ban';
      else if (e.type === 'kick') icon = 'fa-shoe-prints';
      else if (e.type === 'group.join') icon = 'fa-layer-group';
      else if (e.type === 'group.leave') icon = 'fa-layer-minus';
      else if (e.type === 'hint') icon = 'fa-lightbulb';

      html.push('<div class="li ' + clsType + '">' +
        '<span class="ico"><i class="fas ' + icon + '"></i></span>' +
        '<span class="txt">' + UI.esc(e.text) + '<small>' + UI.clock(e.timeUtc) +
        (e.gameName ? ' · ' + UI.esc(e.gameName) : '') + '</small></span></div>');
    }
    host.innerHTML = html.join('');
    host.scrollTop = 0;
  }

  /* -------------------------------------------------------------------- logs */

  function logTick() {
    if (!IS_ADMIN || App.logPaused) return;
    if (!UI.$('#log-view')) return;
    var level = App.settings.logLevel || 'all';
    var url = '/api/logs?since=' + App.lastLogSeq + '&limit=250&level=' + encodeURIComponent(level);
    API.get(url, function (err, data) {
      if (err || !data || !data.lines || !data.lines.length) return;
      for (var i = 0; i < data.lines.length; i++) {
        App.logLines.push(data.lines[i]);
        if (data.lines[i].seq > App.lastLogSeq) App.lastLogSeq = data.lines[i].seq;
      }
      if (App.logLines.length > 400) App.logLines = App.logLines.slice(App.logLines.length - 400);
      renderLogs();
    }, 8000);
  }

  function renderLogs() {
    var view = UI.$('#log-view');
    if (!view) return;
    var nearBottom = (view.scrollHeight - view.scrollTop - view.clientHeight) < 40;
    if (!App.logLines.length) {
      view.innerHTML = '<div class="log-empty">' + I18N.t('log.empty') + '</div>';
      return;
    }
    var html = [];
    for (var i = 0; i < App.logLines.length; i++) {
      var l = App.logLines[i];
      html.push('<div class="log-line lv-' + UI.esc(l.lvl) + '">' +
        '<span class="t">' + UI.esc(l.t) + '</span>' +
        '<span class="cat">' + UI.esc(l.cat) + '</span>' +
        '<span class="m">' + UI.esc(l.msg) + '</span></div>');
    }
    view.innerHTML = html.join('');
    if (nearBottom) view.scrollTop = view.scrollHeight;
  }

  function resetLogs() {
    App.lastLogSeq = 0;
    App.logLines = [];
    var view = UI.$('#log-view');
    if (view) view.innerHTML = '<div class="log-empty">' + I18N.t('log.empty') + '</div>';
    logTick();
  }

  /* ----------------------------------------------------------------- actions */

  function bind() {
    document.addEventListener('pointerdown', unlockAudio, false);
    document.addEventListener('keydown', unlockAudio, false);

    var themeBtn = UI.$('#theme-btn');
    if (themeBtn) themeBtn.addEventListener('click', function () {
      var theme = UI.nextTheme();
      App.settings.theme = theme;
      UI.setTheme(theme);
      updateThemeButton();
      saveSettings();
      SoundFX.play('click');
      UI.toast('info', I18N.t('theme.set', theme === 'light' ? I18N.t('theme.light') : theme === 'dark' ? I18N.t('theme.dark') : 'OLED'), '', 1800);
    });

    var soundBtn = UI.$('#sound-btn');
    if (soundBtn) soundBtn.addEventListener('click', function () {
      App.settings.sound = !App.settings.sound;
      SoundFX.setEnabled(App.settings.sound);
      updateSoundButton();
      saveSettings();
      if (App.settings.sound) SoundFX.play('join');
      UI.toast(App.settings.sound ? 'ok' : 'info', App.settings.sound ? I18N.t('toast.soundOn') : I18N.t('toast.soundOff'), '', 1800);
    });

    var tabLobby = UI.$('#tab-lobby');
    if (tabLobby) tabLobby.addEventListener('click', function () {
      var el = UI.$('#lobby');
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
      setActiveTab('tab-lobby');
      SoundFX.play('click');
    });

    var tabCatalog = UI.$('#tab-catalog');
    if (tabCatalog) tabCatalog.addEventListener('click', function () { openCatalog(); });
    var tabSettings = UI.$('#tab-settings');
    if (tabSettings) tabSettings.addEventListener('click', function () { openSettings(); });
    var tabHelp = UI.$('#tab-help');
    if (tabHelp) tabHelp.addEventListener('click', function () { openHelp(); });

    var search = UI.$('#search');
    var searchTimer = null;
    if (search) search.addEventListener('input', function () {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(function () {
        App.search = search.value;
        App.gamesSig = '';
        if (App.status) renderGames(App.status.games || []);
      }, 160);
    });

    var onlyActive = UI.$('#only-active');
    if (onlyActive) onlyActive.addEventListener('change', function (e) {
      setOnlyActive(e.target.checked, false);
      SoundFX.play('chip');
    });

    var sort = UI.$('#sort');
    if (sort) sort.addEventListener('change', function (e) {
      App.sort = e.target.value;
      App.gamesSig = '';
      if (App.status) renderGames(App.status.games || []);
    });

    var refreshBtn = UI.$('#refresh-btn');
    if (refreshBtn) refreshBtn.addEventListener('click', function () {
      SoundFX.play('click');
      poll();
      logTick();
    });

    var eventsFollow = UI.$('#events-follow');
    if (eventsFollow) eventsFollow.addEventListener('change', function (e) {
      App.notify = e.target.checked;
      UI.store('dpt.notify', App.notify);
      if (App.notify) SoundFX.play('click');
    });

    var logLevel = UI.$('#log-level');
    if (logLevel) logLevel.addEventListener('change', function (e) {
      App.settings.logLevel = e.target.value;
      saveSettings();
      resetLogs();
    });

    var logPause = UI.$('#log-pause');
    if (logPause) logPause.addEventListener('click', function () {
      App.logPaused = !App.logPaused;
      logPause.innerHTML = '<i class="fas ' + (App.logPaused ? 'fa-play' : 'fa-pause') + '"></i>';
      logPause.title = App.logPaused ? I18N.t('log.resume') : I18N.t('log.pause');
      if (!App.logPaused) logTick();
    });

    var gamesList = UI.$('#games-list');
    if (gamesList) {
      gamesList.addEventListener('click', function (e) {
        var nameBtn = closest(e.target, '[data-name-game]');
        if (nameBtn) {
          e.preventDefault();
          e.stopPropagation();
          openNameGame(nameBtn.getAttribute('data-name-game'));
          return;
        }
        var kickBtn = closest(e.target, '[data-kick]');
        if (kickBtn) {
          e.preventDefault();
          e.stopPropagation();
          askKick(kickBtn.getAttribute('data-kick'), kickBtn.getAttribute('data-kick-name'), kickBtn.getAttribute('data-kick-mac'));
          return;
        }
        var head = closest(e.target, '.game-head');
        if (head) {
          var id = head.getAttribute('data-toggle');
          if (id) {
            App.openGames[id] = !App.openGames[id];
            var art = head.parentNode;
            if (art) {
              art.className = 'game' + (App.openGames[id] ? ' open' : '') + (hasPlayers(id) ? ' live' : '');
              head.setAttribute('aria-expanded', App.openGames[id] ? 'true' : 'false');
            }
            SoundFX.play('chip');
          }
        }
      });

      gamesList.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        var head = closest(e.target, '.game-head');
        if (head) { e.preventDefault(); head.click(); }
      });
    }

    document.addEventListener('keydown', function (e) {
      var tag = (e.target && e.target.tagName || '').toLowerCase();
      var typing = tag === 'input' || tag === 'textarea' || tag === 'select';
      if (e.key === '/' && !typing) {
        e.preventDefault();
        var s = UI.$('#search');
        if (s) s.focus();
      } else if ((e.key === 'r' || e.key === 'R') && !typing && !e.ctrlKey && !e.metaKey) {
        poll();
        logTick();
      }
    });

    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) poll();
    });
  }

  function hasPlayers(id) {
    if (!App.status) return false;
    for (var i = 0; i < App.status.games.length; i++) if (App.status.games[i].id === id) return App.status.games[i].players > 0;
    return false;
  }

  function closest(node, sel) {
    while (node && node !== document) {
      if (node.nodeType === 1 && matches(node, sel)) return node;
      node = node.parentNode;
    }
    return null;
  }

  function matches(node, sel) {
    var fn = node.matches || node.msMatchesSelector || node.webkitMatchesSelector;
    return fn ? fn.call(node, sel) : false;
  }

  function setActiveTab(id) {
    var tabs = UI.$$('.tab');
    for (var i = 0; i < tabs.length; i++) tabs[i].className = 'tab' + (tabs[i].id === id ? ' active' : '');
  }

  function unlockAudio() { SoundFX.unlock(); }

  /* --------------------------------------------------------------- kick flow */

  function askKick(id, name, mac) {
    if (!IS_ADMIN) return;
    UI.confirm(I18N.t('chip.kick') + '?', (name || id) + (mac ? '  (' + mac + ')' : ''),
      I18N.t('chip.kick'), function () {
        API.post('/api/kick', { id: id }, function (err, data) {
          if (err) {
            UI.toast('err', I18N.t('toast.kickFailed'), err.message, 5000);
            SoundFX.play('error');
            return;
          }
          UI.toast('ok', I18N.t('toast.kicked'), (data && data.detail) || name || id, 3500);
          SoundFX.play('leave');
          poll();
        });
      }, true);
  }

  /* -------------------------------------------------------------- name a game */

  function openNameGame(id) {
    if (!IS_ADMIN) return;
    UI.modal({
      title: I18N.t('catalog.name'),
      icon: 'fa-pen',
      narrow: true,
      html: '<div class="field"><label>Product ID</label><input type="text" id="ng-id" value="' + UI.esc(id) + '" readonly></div>' +
            '<div class="field"><label>' + I18N.t('catalog.name') + '</label><input type="text" id="ng-name" placeholder="' + I18N.t('catalog.namePlaceholder') + '"></div>' +
            '<div class="field"><label>Crosslink</label><input type="text" id="ng-link" placeholder="ULUS10084" maxlength="9">' +
            '<span class="help">' + I18N.t('catalog.linkHint') + '</span></div>',
      actions: [
        { label: I18N.t('ui.cancel'), cls: 'ghost', onClick: function (close) { close(); } },
        { label: I18N.t('ui.save'), cls: 'right', icon: 'fa-floppy-disk', onClick: function (close) { saveGame(close); } }
      ],
      onReady: function (api) {
        setTimeout(function () { var f = UI.$('#ng-name', api.body); if (f) f.focus(); }, 40);
      }
    });
  }

  function saveGame(close) {
    if (!IS_ADMIN) return;
    var id = (UI.$('#ng-id').value || '').trim().toUpperCase();
    var name = (UI.$('#ng-name').value || '').trim();
    var link = (UI.$('#ng-link').value || '').trim().toUpperCase();
    if (!name) {
      UI.toast('warn', I18N.t('toast.nameRequired'), I18N.t('catalog.name'), 3000);
      return;
    }
    API.post('/api/games', { id: id, name: name, crosslink: link }, function (err) {
      if (err) {
        UI.toast('err', I18N.t('toast.saveFailed'), err.message, 5000);
        SoundFX.play('error');
        return;
      }
      close();
      UI.toast('ok', I18N.t('ui.save'), id + ' → ' + name, 3500);
      SoundFX.play('game');
      App.gamesSig = '';
      poll();
    });
  }

  /* ------------------------------------------------------------- settings UI */

  function openSettings() {
    var s = App.settings;
    var themes = [
      { id: 'light', name: I18N.t('theme.light'), note: 'VK 2008', sw: 'sw-light' },
      { id: 'dark', name: I18N.t('theme.dark'), note: '', sw: 'sw-dark' },
      { id: 'oled', name: 'OLED', note: '', sw: 'sw-oled' }
    ];
    var html = [];
    html.push('<div class="section-title">' + I18N.t('settings.appearance') + '</div><div class="theme-picker">');
    for (var i = 0; i < themes.length; i++) {
      var t = themes[i];
      html.push('<button class="theme-card' + (s.theme === t.id ? ' active' : '') + '" data-theme="' + t.id + '">' +
        '<span class="swatch ' + t.sw + '"><i></i><i></i><i></i></span>' +
        '<b>' + t.name + '</b><small>' + t.note + '</small></button>');
    }
    html.push('</div>');

    // language picker (same list as the top bar, but discoverable inside settings)
    var langOptions = [];
    for (var li = 0; li < I18N.LANGS.length; li++) {
      var lg = I18N.LANGS[li];
      langOptions.push('<option value="' + lg.code + '"' +
        (lg.code === I18N.getLang() ? ' selected' : '') + '>' + lg.local + '</option>');
    }
    html.push('<div class="field" style="margin-top:10px"><label>' + I18N.t('settings.language') +
      '</label><select id="st-lang">' + langOptions.join('') + '</select>' +
      '<span class="help">' + I18N.t('settings.languageHint') + '</span></div>');

    html.push('<div class="section-title">' + I18N.t('settings.sounds') + '</div>');
    html.push('<div class="field"><label class="check"><input type="checkbox" id="st-sound"' + (s.sound ? ' checked' : '') + '><span></span>' + I18N.t('settings.enableSounds') + '</label></div>');
    html.push('<div class="field"><label>' + I18N.t('settings.volume') + '</label><div class="range-row">' +
      '<i class="fas fa-volume-low hint"></i><input type="range" id="st-volume" min="0" max="100" value="' + Math.round(s.volume * 100) + '">' +
      '<i class="fas fa-volume-high hint"></i><span class="tiny hint" id="st-volume-val">' + Math.round(s.volume * 100) + '%</span></div></div>');
    html.push('<div class="sound-buttons" id="st-test">' +
      '<button class="btn ghost sm" data-snd="join">join</button>' +
      '<button class="btn ghost sm" data-snd="leave">leave</button>' +
      '<button class="btn ghost sm" data-snd="game">new game</button>' +
      '<button class="btn ghost sm" data-snd="chat">chat</button>' +
      '<button class="btn ghost sm" data-snd="error">error</button>' +
      '</div>');
    html.push('<div class="field" style="margin-top:10px"><label>' + I18N.t('settings.events') + '</label><div class="sound-buttons">');
    var ev = [['join', I18N.t('ev.join')], ['leave', I18N.t('ev.leave')], ['game', I18N.t('ev.game')], ['chat', I18N.t('ev.chat')], ['error', I18N.t('ev.error')]];
    for (var j = 0; j < ev.length; j++) {
      html.push('<label class="check tiny"><input type="checkbox" data-ev="' + ev[j][0] + '"' + (s.events[ev[j][0]] ? ' checked' : '') + '><span></span>' + ev[j][1] + '</label>');
    }
    html.push('</div></div>');

    html.push('<div class="section-title">' + I18N.t('settings.refresh') + '</div>');
    html.push('<div class="field"><label>' + I18N.t('settings.refresh') + '</label><select id="st-refresh">');
    var opts = [[1000, I18N.t('settings.refreshSec', 1)], [2000, I18N.t('settings.refreshSec', 2)], [3000, I18N.t('settings.refreshSec', 3)], [5000, I18N.t('settings.refreshSec', 5)], [10000, I18N.t('settings.refreshSec', 10)], [0, I18N.t('settings.refreshOff')]];
    for (var k = 0; k < opts.length; k++) {
      html.push('<option value="' + opts[k][0] + '"' + (Number(s.refresh) === opts[k][0] ? ' selected' : '') + '>' + opts[k][1] + '</option>');
    }
    html.push('</select></div>');

    html.push('<div class="field"><label class="check"><input type="checkbox" id="st-only-active"' +
      (s.onlyActive !== false ? ' checked' : '') + '><span></span>' +
      I18N.t('settings.onlyActive') + '</label></div>');

    if (IS_ADMIN) {
      html.push('<div class="section-title">' + I18N.t('role.admin') + '</div>');
      html.push('<div class="field"><label>' + I18N.t('settings.logLevel') + '</label><select id="st-loglevel">' +
        '<option value="all"' + (s.logLevel === 'all' ? ' selected' : '') + '>' + I18N.t('settings.logAll') + '</option>' +
        '<option value="Info"' + (s.logLevel === 'Info' ? ' selected' : '') + '>' + I18N.t('settings.logInfo') + '</option>' +
        '<option value="Warn"' + (s.logLevel === 'Warn' ? ' selected' : '') + '>' + I18N.t('settings.logWarn') + '</option>' +
        '<option value="Error"' + (s.logLevel === 'Error' ? ' selected' : '') + '>' + I18N.t('settings.logError') + '</option>' +
        '</select></div>');
      html.push('<div class="field"><label>' + I18N.t('settings.broadcast') + '</label><div class="row">' +
        '<input type="text" id="st-say" placeholder="' + I18N.t('settings.broadcastPh') + '" maxlength="63">' +
        '<button class="btn ghost" id="st-say-btn"><i class="fas fa-paper-plane"></i></button></div>' +
        '<span class="help">' + I18N.t('settings.broadcastHint') + '</span></div>');
      html.push('<div class="field"><label>' + I18N.t('about.sessions') + '</label><div class="row">' +
        '<button class="btn ghost" id="st-logout"><i class="fas fa-right-from-bracket"></i> ' + I18N.t('settings.logout') + '</button>' +
        '<span class="help">' + I18N.t('settings.logoutHint') + '</span></div></div>');
    } else {
      html.push('<div class="section-title">' + I18N.t('settings.publicBoard') + '</div>');
      html.push('<div class="field"><span class="help">' + I18N.t('settings.publicNote') + '</span></div>');
    }

    UI.modal({
      title: I18N.t('settings.title'),
      icon: 'fa-sliders-h',
      html: html.join(''),
      actions: [
        { label: I18N.t('settings.reset'), cls: 'ghost', icon: 'fa-rotate-left', onClick: function (close) {
            UI.confirm(I18N.t('settings.resetTitle'), I18N.t('settings.resetText'), I18N.t('settings.resetConfirm'), function () {
              UI.store('dpt.settings', null);
              App.settings = loadSettings();
              applySettings();
              updateThemeButton();
              close();
              openSettings();
            }, true);
          } },
        { label: I18N.t('settings.done'), cls: 'right', icon: 'fa-check', onClick: function (close) { close(); } }
      ],
      onReady: function (api) { bindSettings(api); }
    });
  }

  function bindSettings(api) {
    var root = api.body;

    var cards = UI.$$('.theme-card', root);
    for (var i = 0; i < cards.length; i++) {
      cards[i].addEventListener('click', function () {
        for (var j = 0; j < cards.length; j++) cards[j].className = 'theme-card';
        this.className = 'theme-card active';
        App.settings.theme = this.getAttribute('data-theme');
        UI.setTheme(App.settings.theme);
        updateThemeButton();
        saveSettings();
        SoundFX.play('click');
      });
    }

    // language: switch immediately and rebuild the modal in the new language
    var langSel = UI.$('#st-lang', root);
    if (langSel) langSel.addEventListener('change', function () {
      I18N.setLang(this.value);
      UI.closeModal();
      openSettings();
    });

    var onlyActiveBox = UI.$('#st-only-active', root);
    if (onlyActiveBox) onlyActiveBox.addEventListener('change', function () {
      setOnlyActive(this.checked, true);
      SoundFX.play('chip');
    });

    var sound = UI.$('#st-sound', root);
    if (sound) sound.addEventListener('change', function () {
      App.settings.sound = sound.checked;
      SoundFX.setEnabled(sound.checked);
      updateSoundButton();
      saveSettings();
    });

    var vol = UI.$('#st-volume', root);
    var volVal = UI.$('#st-volume-val', root);    if (vol) {
      vol.addEventListener('input', function () {
        App.settings.volume = (Number(vol.value) || 0) / 100;
        SoundFX.setVolume(App.settings.volume);
        if (volVal) volVal.textContent = vol.value + '%';
        saveSettings();
      });
      vol.addEventListener('change', function () { SoundFX.play('click'); });
    }

    var test = UI.$('#st-test', root);
    if (test) test.addEventListener('click', function (e) {
      var b = closest(e.target, '[data-snd]');
      if (b) SoundFX.play(b.getAttribute('data-snd'));
    });

    var evBoxes = UI.$$('[data-ev]', root);
    for (var k = 0; k < evBoxes.length; k++) {
      evBoxes[k].addEventListener('change', function () {
        App.settings.events[this.getAttribute('data-ev')] = this.checked;
        SoundFX.setEventEnabled(this.getAttribute('data-ev'), this.checked);
        saveSettings();
      });
    }

    var refresh = UI.$('#st-refresh', root);
    if (refresh) refresh.addEventListener('change', function () {
      App.settings.refresh = Number(this.value) || 0;
      saveSettings();
      schedulePoll(true);
    });

    var logLevel = UI.$('#st-loglevel', root);
    if (logLevel) logLevel.addEventListener('change', function () {
      App.settings.logLevel = this.value;
      saveSettings();
      var sel = UI.$('#log-level');
      if (sel) sel.value = this.value;
      resetLogs();
    });

    var say = UI.$('#st-say', root);
    var sayBtn = UI.$('#st-say-btn', root);
    function sendSay() {
      var text = (say.value || '').trim();
      if (!text) return;
      API.post('/api/chat', { text: text, sender: 'SERVER' }, function (err, data) {
        if (err) { UI.toast('err', I18N.t('toast.sendFailed'), err.message, 4500); return; }
        UI.toast('ok', I18N.t('toast.sent'), (data && data.sent ? data.sent : I18N.t('lobby.none')), 3000);
        say.value = '';
      });
    }
    if (sayBtn) sayBtn.addEventListener('click', sendSay);
    if (say) say.addEventListener('keydown', function (e) { if (e.key === 'Enter') sendSay(); });

    var logout = UI.$('#st-logout', root);
    if (logout) logout.addEventListener('click', function () {
      API.logout();
      UI.closeModal();
      showGate(true, I18N.t('settings.logout'));
    });
  }

  /* -------------------------------------------------------------- catalog UI */

  var catalogCache = null;
  var catalogFiltered = [];
  var catalogRendered = 0;
  var catalogOnlyAdhoc = false;
  var CATALOG_PAGE = 300;

  function openCatalog() {
    setActiveTab('tab-catalog');
    var html = [];
    html.push('<div class="field"><div class="row">' +
      '<input type="text" id="cat-search" placeholder="' + I18N.t('catalog.search') + '">' +
      '<span class="chip id" id="cat-count">…</span></div>' +
      '<label class="check tiny" id="cat-only-adhoc-wrap" title="' + I18N.t('catalog.onlyAdhocHint') + '">' +
      '<input type="checkbox" id="cat-only-adhoc"><span></span>' + I18N.t('catalog.onlyAdhoc') + '</label></div>' +
      '<div class="catalog-wrap"><table class="catalog-table"><thead><tr><th>' + I18N.t('catalog.id') + '</th><th>' + I18N.t('catalog.name') + '</th><th>' + I18N.t('catalog.crosslink') + '</th>' +
      (IS_ADMIN ? '<th></th>' : '') + '</tr></thead>' +
      '<tbody id="cat-body"><tr><td colspan="4" class="empty small">' + I18N.t('catalog.loading') + '</td></tr></tbody></table></div>' +
      '<div class="catalog-note"><i class="fas fa-circle" style="color:var(--ok)"></i> ' + I18N.t('catalog.adhocNote') + '</div>' +
      '<div class="row" id="cat-more-row" style="display:none;justify-content:center;gap:8px;margin-top:8px">' +
      '<button class="btn ghost sm" id="cat-more"><i class="fas fa-chevron-down"></i> ' + I18N.t('catalog.more') + '</button>' +
      '<button class="btn ghost sm" id="cat-all"><i class="fas fa-list"></i> ' + I18N.t('catalog.all') + '</button></div>');

    if (IS_ADMIN) {
      html.push('<div class="section-title">' + I18N.t('catalog.add') + '</div>' +
        '<div class="row" style="gap:8px;align-items:flex-end;flex-wrap:wrap">' +
        '<div class="field" style="flex:1 1 120px;margin:0"><label>Product ID</label><input type="text" id="cat-id" maxlength="9" placeholder="ULUS10084"></div>' +
        '<div class="field" style="flex:2 1 220px;margin:0"><label>' + I18N.t('catalog.name') + '</label><input type="text" id="cat-name" placeholder="' + I18N.t('catalog.namePlaceholder') + '"></div>' +
        '<div class="field" style="flex:1 1 110px;margin:0"><label>Crosslink</label><input type="text" id="cat-link" maxlength="9" placeholder="—"></div>' +
        '<button class="btn" id="cat-save"><i class="fas fa-floppy-disk"></i> ' + I18N.t('catalog.save') + '</button>' +
        '</div>' +
        '<div id="cat-unknown" style="margin-top:10px"></div>');
    } else {
      html.push('<div class="section-title">' + I18N.t('nav.help') + '</div><div class="help" style="font-size:11.5px">' +
        I18N.t('catalog.help') + '</div>');
    }

    var actions = [];
    if (IS_ADMIN) {
      actions.push({ label: I18N.t('settings.reloadFromFile'), cls: 'ghost', icon: 'fa-rotate', onClick: function () {
        API.post('/api/reload', {}, function (err) {
          if (err) { UI.toast('err', I18N.t('toast.reloadFailed'), err.message, 4000); return; }
          UI.toast('ok', I18N.t('toast.reloaded'), '', 2500);
          loadCatalog();
        });
      } });
    }
    actions.push({ label: I18N.t('ui.close'), cls: 'right', onClick: function (close) { close(); } });

    UI.modal({
      title: I18N.t('catalog.title'),
      icon: 'fa-book-open',
      html: html.join(''),
      actions: actions,
      onReady: function (api) {
        var search = UI.$('#cat-search', api.body);
        if (search) search.addEventListener('input', function () { renderCatalog(search.value); });
        var onlyAdhoc = UI.$('#cat-only-adhoc', api.body);
        if (onlyAdhoc) {
          onlyAdhoc.checked = catalogOnlyAdhoc;
          onlyAdhoc.addEventListener('change', function () {
            catalogOnlyAdhoc = this.checked;
            renderCatalog(search ? search.value : '');
          });
        }
        var more = UI.$('#cat-more', api.body);
        if (more) more.addEventListener('click', function () { appendCatalogPage(); });
        var all = UI.$('#cat-all', api.body);
        if (all) all.addEventListener('click', function () { appendCatalogPage(true); });
        var saveBtn = UI.$('#cat-save', api.body);
        if (saveBtn) saveBtn.addEventListener('click', saveCatalogEntry);

        var body = UI.$('#cat-body', api.body);
        if (body) body.addEventListener('click', function (e) {
          var b = closest(e.target, '[data-edit]');
          if (!b) return;
          var id = b.getAttribute('data-edit');
          var games = (catalogCache && catalogCache.games) || [];
          for (var j = 0; j < games.length; j++) {
            if (games[j].id === id) {
              UI.$('#cat-id').value = games[j].id;
              UI.$('#cat-name').value = games[j].name;
              UI.$('#cat-link').value = games[j].crosslink || '';
              UI.$('#cat-name').focus();
              break;
            }
          }
        });
        loadCatalog();
      },
      onClose: function () { setActiveTab('tab-lobby'); }
    });
  }

  function loadCatalog() {
    API.get('/api/games', function (err, data) {
      if (err) { UI.toast('err', I18N.t('toast.catalogFailed'), err.message, 4000); return; }
      catalogCache = data;
      var search = UI.$('#cat-search');
      renderCatalog(search ? search.value : '');

      var unknown = data.unknownIds || [];
      var host = UI.$('#cat-unknown');
      if (!host) return;
      if (!unknown.length) { host.innerHTML = ''; return; }
      var html = ['<div class="section-title">' + I18N.t('catalog.unknownIds') + '</div><div class="sound-buttons">'];
      for (var i = 0; i < unknown.length; i++) {
        html.push('<button class="btn ghost sm" data-unknown="' + UI.esc(unknown[i]) + '"><i class="fas fa-pen"></i> ' + UI.esc(unknown[i]) + '</button>');
      }
      html.push('</div>');
      host.innerHTML = html.join('');
      host.addEventListener('click', function (e) {
        var b = closest(e.target, '[data-unknown]');
        if (!b) return;
        UI.$('#cat-id').value = b.getAttribute('data-unknown');
        UI.$('#cat-name').focus();
      });
    });
  }

  function renderCatalog(filter) {
    var tbody = UI.$('#cat-body');
    if (!tbody || !catalogCache) return;
    var q = String(filter || '').trim().toLowerCase();
    var games = catalogCache.games || [];

    catalogFiltered = [];
    for (var i = 0; i < games.length; i++) {
      var g = games[i];
      if (q && g.id.toLowerCase().indexOf(q) < 0 && (g.name || '').toLowerCase().indexOf(q) < 0) continue;
      if (catalogOnlyAdhoc && g.adhoc === undefined) continue;
      catalogFiltered.push(g);
    }

    var count = UI.$('#cat-count');
    if (count) {
      count.textContent = (q || catalogOnlyAdhoc)
        ? I18N.t('catalog.found', UI.fmtInt(catalogFiltered.length), UI.fmtInt(games.length))
        : I18N.t('catalog.games', UI.fmtInt(games.length));
    }

    catalogRendered = 0;
    tbody.innerHTML = '';
    if (!catalogFiltered.length) {
      tbody.innerHTML = '<tr><td colspan="4" class="empty small">' + I18N.t('catalog.nothing') + '</td></tr>';
      updateMoreRow();
      return;
    }
    appendCatalogPage();
  }

  // База большая (3900+ ID), поэтому таблица дорисовывается порциями; "Показать все" — сразу.
  function appendCatalogPage(everything) {
    var tbody = UI.$('#cat-body');
    if (!tbody) return;
    var end = everything ? catalogFiltered.length : Math.min(catalogFiltered.length, catalogRendered + CATALOG_PAGE);
    if (end <= catalogRendered) { updateMoreRow(); return; }

    var html = [];
    for (var i = catalogRendered; i < end; i++) {
      var g = catalogFiltered[i];
      var isAdhoc = g.adhoc !== undefined;
      var adhocChip = isAdhoc
        ? ' <span class="chip adhoc" title="' + UI.esc(I18N.t('catalog.adhocBadge')) +
          (g.adhoc ? ' (' + UI.esc(g.adhoc) + ')' : '') + '"><i class="fas fa-wifi"></i>' +
          (g.adhoc ? UI.esc(g.adhoc) : '') + '</span>'
        : '';
      html.push('<tr' + (isAdhoc ? ' class="adhoc"' : '') + '>' +
        '<td class="id">' + UI.esc(g.id) + '</td>' +
        '<td class="name' + (isAdhoc ? ' adhoc' : '') + '">' + UI.esc(g.name) + adhocChip + '</td>' +
        '<td class="id">' + UI.esc(g.crosslink || '') + '</td>' +
        (IS_ADMIN ? '<td class="act"><button class="mini" data-edit="' + UI.esc(g.id) + '" title="' + I18N.t('catalog.fillForm') + '"><i class="fas fa-pen-to-square"></i></button></td>' : '') +
        '</tr>');
    }
    var first = catalogRendered;
    catalogRendered = end;
    if (first === 0) tbody.innerHTML = html.join('');
    else tbody.insertAdjacentHTML('beforeend', html.join(''));
    updateMoreRow();
  }

  function updateMoreRow() {
    var row = UI.$('#cat-more-row');
    var btn = UI.$('#cat-more');
    var all = UI.$('#cat-all');
    if (!row || !btn) return;
    var left = catalogFiltered.length - catalogRendered;
    if (left <= 0) { row.style.display = 'none'; return; }
    row.style.display = 'flex';
    btn.innerHTML = '<i class="fas fa-chevron-down"></i> ' + I18N.t('catalog.more') + ' ' +
      UI.fmtInt(Math.min(left, CATALOG_PAGE)) + ' (' + UI.fmtInt(left) + ')';
    if (all) all.innerHTML = '<i class="fas fa-list"></i> ' + I18N.t('catalog.all') + ' (' + UI.fmtInt(left) + ')';
  }

  function saveCatalogEntry() {
    if (!IS_ADMIN) return;
    var id = (UI.$('#cat-id').value || '').trim().toUpperCase();
    var name = (UI.$('#cat-name').value || '').trim();
    var link = (UI.$('#cat-link').value || '').trim().toUpperCase();
    if (!/^[A-Z0-9]{9}$/.test(id)) { UI.toast('warn', I18N.t('toast.invalidId'), I18N.t('toast.invalidIdHint'), 3600); return; }
    if (!name) { UI.toast('warn', I18N.t('toast.nameRequired'), '', 3000); return; }
    API.post('/api/games', { id: id, name: name, crosslink: link }, function (err) {
      if (err) { UI.toast('err', I18N.t('toast.saveFailed'), err.message, 4500); SoundFX.play('error'); return; }
      UI.toast('ok', I18N.t('ui.save'), id + ' → ' + name, 3000);
      SoundFX.play('game');
      App.gamesSig = '';
      loadCatalog();
      poll();
    });
  }

  /* -------------------------------------------------------------------- help */

  function openHelp() {
    setActiveTab('tab-help');
    var host = window.location.hostname || '127.0.0.1';
    var adhocPort = (App.status && App.status.server ? App.status.server.adhocPort : 27312);
    var html = [];
    html.push('<div class="section-title">' + I18N.t('help.ppsspp') + '</div>');
    html.push('<ul class="help-list">' + I18N.t('help.ppssppSteps', UI.esc(host), UI.esc(adhocPort)) + '</ul>');
    html.push('<div class="section-title">' + I18N.t('help.protocol') + '</div>');
    html.push('<ul class="help-list">' + I18N.t('help.protocolSteps') + '</ul>');

    if (IS_ADMIN) {
      html.push('<div class="section-title">' + I18N.t('role.admin') + '</div>');
      html.push('<ul class="help-list">' + I18N.t('help.securitySteps') + '</ul>');
    } else {
      html.push('<div class="section-title">' + I18N.t('help.public') + '</div>');
      html.push('<ul class="help-list">' + I18N.t('help.publicSteps') + '</ul>');
    }

    html.push('<div class="section-title">' + I18N.t('help.hotkeys') + '</div>');
    html.push('<div class="field"><span class="help">' + I18N.t('help.hotkeysText') + '</span></div>');

    UI.modal({
      title: I18N.t('help.title'),
      icon: 'fa-circle-question',
      html: html.join(''),
      actions: [{ label: I18N.t('ui.done'), cls: 'right', onClick: function (close) { close(); } }],
      onClose: function () { setActiveTab('tab-lobby'); }
    });
  }

  /* -------------------------------------------------------------- only-active */

  /** Remembers "with players only" both in the toolbar and in the settings dialog. */
  function setOnlyActive(value, fromSettings) {
    App.onlyActive = !!value;
    App.settings.onlyActive = App.onlyActive;
    saveSettings();

    var box = UI.$('#only-active');
    if (box) box.checked = App.onlyActive;
    var sbox = UI.$('#st-only-active');
    if (sbox) sbox.checked = App.onlyActive;

    App.gamesSig = '';
    if (App.status) renderGames(App.status.games || []);
    if (!fromSettings) return;
  }

  /* ----------------------------------------------------------------- visits */

  var visitTimer = null;
  var visitsOff = false;

  function visitPing() {
    if (visitsOff) return;
    API.post('/api/visit', {}, function (err, data) {
      if (err) { return; }
      if (!data || data.enabled === false) { visitsOff = true; return; }
      renderVisits(data);
    }, 8000);
  }

  function renderVisits(v) {
    if (!v) return;
    setText('#v-online', UI.fmtInt(v.online));
    setText('#v-today', UI.fmtInt(v.today));
    setText('#v-total', UI.fmtInt(v.total));

    var host = UI.$('#v-bars');
    if (!host) return;
    var days = v.days || [];
    if (!days.length) { host.innerHTML = ''; return; }

    var tail = days.slice(Math.max(0, days.length - 7));
    var max = 1;
    for (var i = 0; i < tail.length; i++) if ((tail[i].v || 0) > max) max = tail[i].v;

    var html = [];
    for (var j = 0; j < tail.length; j++) {
      var d = tail[j];
      var h = Math.max(2, Math.round((d.v || 0) / max * 24));
      html.push('<i style="height:' + h + 'px" title="' + UI.esc(d.d) + ': ' + UI.fmtInt(d.v) +
                ' / ' + UI.fmtInt(d.u) + '"></i>');
    }
    host.innerHTML = html.join('');
  }

  /* ------------------------------------------------------------------- chat */

  var chat = {
    lastSeq: 0,
    items: [],
    captchaId: '',
    cooldownSec: 20,
    cooldownUntil: 0,
    timer: null,
    tickTimer: null,
    busy: false
  };

  function chatStart() {
    var panel = UI.$('#chat-panel');
    if (!panel) return;

    var nick = UI.$('#chat-nick');
    if (nick) {
      nick.value = UI.read('dpt.chatNick', '') || '';
      nick.addEventListener('change', function () { UI.store('dpt.chatNick', nick.value); });
    }

    var form = UI.$('#chat-form');
    if (form) form.addEventListener('submit', function (e) { e.preventDefault(); chatSend(); });

    var cap = UI.$('#chat-captcha');
    if (cap) cap.addEventListener('click', function () { chatLoadCaptcha(); });

    chatPoll(true);
    if (!chat.timer) chat.timer = setInterval(function () { chatPoll(false); }, 4000);
    if (!chat.tickTimer) chat.tickTimer = setInterval(chatTick, 500);
  }

  function chatPoll(initial) {
    var since = initial ? 0 : chat.lastSeq;
    API.get('/api/chat?since=' + since + '&limit=60', function (err, data) {
      if (err || !data) return;
      var panel = UI.$('#chat-panel');
      if (data.enabled === false) { if (panel) panel.hidden = true; return; }
      if (panel) panel.hidden = false;

      chat.cooldownSec = Number(data.cooldown) || 20;

      var list = data.messages || [];
      if (initial) {
        chat.items = list;
        if (list.length) chat.lastSeq = list[list.length - 1].seq;
        chatRender(true);
        chatLoadCaptcha();
        return;
      }
      if (!list.length) return;

      var fresh = 0;
      for (var i = 0; i < list.length; i++) {
        var m = list[i];
        if (m.seq <= chat.lastSeq) continue;
        chat.items.push(m);
        chat.lastSeq = m.seq;
        fresh++;
      }
      if (!fresh) return;
      if (chat.items.length > 120) chat.items = chat.items.slice(chat.items.length - 120);
      chatRender(false);

      var last = chat.items[chat.items.length - 1];
      if (last && last.nick !== (UI.$('#chat-nick') || {}).value && App.settings.events.chat) {
        SoundFX.play('chat');
      }
    }, 8000);
  }

  function chatRender(initial) {
    var host = UI.$('#chat-log');
    if (!host) return;
    if (!chat.items.length) {
      host.innerHTML = '<div class="empty small">' + I18N.t('chat.empty') + '</div>';
      return;
    }

    var nearBottom = initial || (host.scrollHeight - host.scrollTop - host.clientHeight) < 40;
    var html = [];
    for (var i = 0; i < chat.items.length; i++) {
      var m = chat.items[i];
      var src = m.src === 'game' ? 'src-game' : (m.src === 'server' ? 'src-game' : 'src-web');
      html.push('<div class="chat-line ' + src + '">' +
        '<span class="when">' + UI.clock(m.ts) + '</span>' +
        '<span class="who">' + UI.esc(m.nick) + '</span>' +
        '<span class="what">' + UI.esc(m.text) + '</span></div>');
    }
    host.innerHTML = html.join('');
    if (nearBottom) host.scrollTop = host.scrollHeight;
  }

  function chatLoadCaptcha() {
    var svgHost = UI.$('#chat-captcha-svg');
    if (!svgHost) return;
    API.get('/api/chat/captcha', function (err, data) {
      if (err || !data || !data.svg) { svgHost.innerHTML = ''; return; }
      chat.captchaId = data.id;
      svgHost.innerHTML = data.svg;
    }, 8000);
  }

  function chatStatus(text, kind) {
    var el = UI.$('#chat-status');
    if (!el) return;
    el.textContent = text || '';
    el.className = 'chat-status' + (kind ? ' ' + kind : '');
  }

  function chatSend() {
    if (chat.busy) return;
    var textEl = UI.$('#chat-text');
    var answerEl = UI.$('#chat-answer');
    var nickEl = UI.$('#chat-nick');
    if (!textEl || !answerEl) return;

    var text = (textEl.value || '').trim();
    if (!text) { chatStatus(I18N.t('chat.needText'), 'err'); return; }
    if (chat.cooldownUntil > Date.now()) {
      chatStatus(I18N.t('chat.wait', Math.ceil((chat.cooldownUntil - Date.now()) / 1000)), 'err');
      return;
    }

    var nick = (nickEl && nickEl.value || '').trim();
    if (nick) UI.store('dpt.chatNick', nick);

    chat.busy = true;
    var btn = UI.$('#chat-send');
    if (btn) btn.disabled = true;

    API.post('/api/chat/send', {
      nick: nick,
      text: text,
      captcha: chat.captchaId,
      answer: (answerEl.value || '').trim()
    }, function (err, data) {
      chat.busy = false;
      if (btn) btn.disabled = false;

      if (err) { chatStatus((err.message || I18N.t('chat.failed')), 'err'); chatLoadCaptcha(); return; }
      if (!data) { chatStatus(I18N.t('chat.failed'), 'err'); return; }

      if (!data.ok) {
        var wait = Number(data.retryAfter) || 0;
        if (data.error === 'captcha') {
          chatStatus(I18N.t('chat.wrongCode'), 'err');
          chatLoadCaptcha();
          if (answerEl) answerEl.value = '';
        } else if (data.error === 'cooldown') {
          chat.cooldownUntil = Date.now() + wait * 1000;
          chatStatus(I18N.t('chat.wait', wait), 'err');
        } else if (data.error === 'flood') {
          chat.cooldownUntil = Date.now() + wait * 1000;
          chatStatus(I18N.t('chat.flood'), 'err');
        } else if (data.error === 'duplicate') {
          chatStatus(I18N.t('chat.duplicate'), 'err');
        } else {
          chatStatus(I18N.t('chat.failed'), 'err');
        }
        return;
      }

      var m = data.message;
      if (m) {
        if (m.seq > chat.lastSeq) chat.lastSeq = m.seq;
        chat.items.push(m);
        if (chat.items.length > 120) chat.items = chat.items.slice(chat.items.length - 120);
        chatRender(false);
      }
      textEl.value = '';
      if (answerEl) answerEl.value = '';
      chatLoadCaptcha();
      chat.cooldownUntil = Date.now() + chat.cooldownSec * 1000;
      chatStatus(data.sentToGame > 0
        ? I18N.t('chat.sentGame', data.sentToGame)
        : I18N.t('chat.sent'), 'ok');
      chatTick();
    }, 10000);
  }

  function chatTick() {
    var btn = UI.$('#chat-send');
    if (!btn) return;
    var left = Math.ceil((chat.cooldownUntil - Date.now()) / 1000);
    if (left > 0) {
      btn.disabled = true;
      var label = btn.querySelector('span');
      if (label) label.textContent = I18N.t('chat.waitShort', left);
    } else {
      if (!chat.busy) btn.disabled = false;
      var l2 = btn.querySelector('span');
      if (l2) l2.textContent = I18N.t('chat.send');
    }
  }

  /* -------------------------------------------------------------------- init */

  /**
   * Called by i18n.js after the language changed: the static shell is already
   * re-translated, here we refresh the parts that are built in JS.
   */
  App.langChanged = function () {
    updateThemeButton();
    updateSoundButton();
    if (App.conn === 'ok') setConnPill('ok', I18N.t('conn.ok'));
    else if (App.conn === 'bad') setConnPill('bad', I18N.t('conn.bad'));
    if (!App.status) return;
    App.gamesSig = '';
    renderTop(App.status);
    renderStats(App.status);
    renderGames(App.status.games || []);
    renderRecent(App.status.recent || []);
    renderEvents(App.status.events || []);
    if (IS_ADMIN) renderLogs();
  };

  function start() {
    if (App.started) return;
    App.started = true;

    App.settings = loadSettings();
    UI.setTheme(App.settings.theme);
    updateThemeButton();
    updateSoundButton();

    var logLevel = UI.$('#log-level');
    if (logLevel) logLevel.value = App.settings.logLevel || 'all';

    var storedNotify = UI.read('dpt.notify', true);
    App.notify = storedNotify !== false;
    var notifyBox = UI.$('#events-follow');
    if (notifyBox) notifyBox.checked = !!App.notify;

    SoundFX.setEnabled(App.settings.sound);
    SoundFX.setVolume(App.settings.volume);
    SoundFX.setEvents(App.settings.events);

    // "with players only" is remembered between visits
    App.onlyActive = App.settings.onlyActive !== false;
    var onlyBox = UI.$('#only-active');
    if (onlyBox) onlyBox.checked = App.onlyActive;

    bind();
    poll();

    chatStart();
    visitPing();
    if (!visitTimer) visitTimer = setInterval(visitPing, 45000);

    if (IS_ADMIN) {
      App.logTimer = setInterval(logTick, 2600);
      logTick();
    }

    UI.toast('info', 'DPT.PSP AdHoc', IS_ADMIN ? I18N.t('role.adminTitle') : I18N.t('role.publicTitle'), 2400);
  }

  function init() {
    if (window.I18N) I18N.init();

    App.settings = loadSettings();
    UI.setTheme(App.settings.theme);
    updateThemeButton();
    updateSoundButton();

    if (!IS_ADMIN) {
      start();
      return;
    }

    bindGate();
    showGate(true, '');
    API.init(function (err) {
      if (err) {
        showGate(true, err.message || I18N.t('auth.required'));
        return;
      }
      showGate(false);
      start();
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

  window.AdHocBoard = App;
})();
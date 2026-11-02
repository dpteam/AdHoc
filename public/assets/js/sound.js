/* =========================================================================
   SoundFX - tiny WebAudio synth for the DPT.Agent AdHoc board.
   No audio files, no libraries: soft sine/triangle plucks with a low pass
   and a light compressor so notifications never get harsh ("math audio").
   ========================================================================= */
var SoundFX = (function () {
  'use strict';

  var ctx = null;
  var master = null;
  var enabled = true;
  var volume = 0.55;
  var events = { join: true, leave: true, game: true, chat: true, error: true };

  /* freq, delay, duration, optional gain multiplier */
  var PRESETS = {
    join:    { type: 'sine',     filter: 2600, notes: [[659.25, 0, .16, 1], [987.77, .075, .30, .85]] },
    leave:   { type: 'sine',     filter: 2000, notes: [[523.25, 0, .14, .9], [392.00, .07, .28, .8]] },
    game:    { type: 'triangle', filter: 3000, notes: [[523.25, 0, .12, .9], [659.25, .06, .12, .85], [783.99, .12, .28, .8]] },
    chat:    { type: 'sine',     filter: 3400, notes: [[880.00, 0, .09, .7], [1174.66, .05, .16, .55]] },
    error:   { type: 'triangle', filter: 900,  notes: [[196.00, 0, .22, .8], [174.61, .10, .30, .7]] },
    online:  { type: 'sine',     filter: 2600, notes: [[392.00, 0, .16, .8], [587.33, .08, .24, .8], [783.99, .16, .3, .7]] },
    offline: { type: 'sine',     filter: 1200, notes: [[392.00, 0, .2, .75], [261.63, .1, .34, .7]] },
    click:   { type: 'sine',     filter: 4200, notes: [[1046.5, 0, .05, .45]] },
    chip:    { type: 'triangle', filter: 2800, notes: [[1567.98, 0, .06, .4]] }
  };

  function ensure() {
    if (ctx) return ctx;
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    try {
      ctx = new AC();
      master = ctx.createGain();
      master.gain.value = volume * 0.5;
      if (ctx.createDynamicsCompressor) {
        var comp = ctx.createDynamicsCompressor();
        comp.threshold.value = -18;
        comp.knee.value = 24;
        comp.ratio.value = 3.5;
        comp.attack.value = 0.004;
        comp.release.value = 0.25;
        master.connect(comp);
        comp.connect(ctx.destination);
      } else {
        master.connect(ctx.destination);
      }
    } catch (e) {
      ctx = null;
      master = null;
    }
    return ctx;
  }

  function voice(c, preset, freq, delay, dur, mul) {
    var t0 = c.currentTime + 0.012 + delay;
    var peak = 0.30 * (preset.gain || 1) * (mul || 1) * 0.9;
    var osc = c.createOscillator();
    var detune = c.createOscillator();
    var filt = c.createBiquadFilter();
    var gain = c.createGain();
    var gain2 = c.createGain();

    osc.type = preset.type || 'sine';
    osc.frequency.setValueAtTime(freq, t0);

    detune.type = 'sine';
    detune.frequency.setValueAtTime(freq * 2.002, t0);

    filt.type = 'lowpass';
    filt.frequency.setValueAtTime(Math.min(preset.filter || 3000, 16000), t0);
    filt.Q.value = 0.6;

    gain.gain.setValueAtTime(0.0001, t0);
    gain.gain.exponentialRampToValueAtTime(peak, t0 + 0.018);
    gain.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);

    gain2.gain.setValueAtTime(0.0001, t0);
    gain2.gain.exponentialRampToValueAtTime(peak * 0.22, t0 + 0.03);
    gain2.gain.exponentialRampToValueAtTime(0.0001, t0 + dur * 0.8);

    osc.connect(filt);
    filt.connect(gain);
    gain.connect(master);
    detune.connect(gain2);
    gain2.connect(master);

    osc.start(t0);
    detune.start(t0);
    osc.stop(t0 + dur + 0.05);
    detune.stop(t0 + dur + 0.05);
  }

  function play(name) {
    if (!enabled) return;
    if (events[name] === false) return;
    var preset = PRESETS[name];
    if (!preset) return;
    var c = ensure();
    if (!c) return;
    if (c.state === 'suspended') {
      try { c.resume(); } catch (e) { /* ignore */ }
    }
    for (var i = 0; i < preset.notes.length; i++) {
      var n = preset.notes[i];
      try { voice(c, preset, n[0], n[1], n[2], n.length > 3 ? n[3] : 1); }
      catch (e) { /* audio is never critical */ }
    }
  }

  function unlock() {
    var c = ensure();
    if (!c) return;
    if (c.state === 'suspended') {
      try { c.resume(); } catch (e) { /* ignore */ }
    }
  }

  function setEnabled(on) {
    enabled = !!on;
    if (enabled) {
      unlock();
      play('click');
    }
    return enabled;
  }

  function isEnabled() { return enabled; }

  function setVolume(v) {
    volume = Math.max(0, Math.min(1, Number(v) || 0));
    if (master) {
      try { master.gain.value = volume * 0.5; } catch (e) { /* ignore */ }
    }
    return volume;
  }

  function getVolume() { return volume; }

  function setEventEnabled(name, on) {
    if (!events.hasOwnProperty(name)) return;
    events[name] = !!on;
  }

  function isEventEnabled(name) { return events[name] !== false; }

  function setEvents(map) {
    if (!map) return;
    for (var k in events) {
      if (events.hasOwnProperty(k) && map.hasOwnProperty(k)) events[k] = !!map[k];
    }
  }

  function getEvents() {
    var out = {};
    for (var k in events) if (events.hasOwnProperty(k)) out[k] = events[k];
    return out;
  }

  return {
    play: play,
    unlock: unlock,
    setEnabled: setEnabled,
    isEnabled: isEnabled,
    setVolume: setVolume,
    getVolume: getVolume,
    setEventEnabled: setEventEnabled,
    isEventEnabled: isEventEnabled,
    setEvents: setEvents,
    getEvents: getEvents,
    presets: PRESETS
  };
})();
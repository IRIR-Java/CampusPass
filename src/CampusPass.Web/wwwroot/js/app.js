// CampusPass: small progressive enhancements. Everything works without JavaScript except live filtering and auto-refresh.
// Served from a file (not inline) so the Content-Security-Policy can block inline scripts.
(function () {
  'use strict';
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };

  // Live search over service tiles / lists.
  $$('[data-filter]').forEach(function (input) {
    var items = $$(input.getAttribute('data-filter'));
    var empty = $('[data-empty]');
    input.addEventListener('input', function () {
      var q = input.value.trim().toLowerCase();
      var shown = 0;
      items.forEach(function (el) {
        var hit = !q || (el.getAttribute('data-text') || el.textContent).toLowerCase().indexOf(q) >= 0;
        el.classList.toggle('hide', !hit);
        if (hit) shown++;
      });
      if (empty) empty.classList.toggle('hide', shown > 0);
    });
  });

  // Join Queue vs Book Appointment: show the time picker only for appointments.
  var modes = $$('[data-mode]');
  function syncMode() {
    var m = (modes.filter(function (r) { return r.checked; })[0] || {}).value;
    $$('[data-when]').forEach(function (el) { el.classList.toggle('hide', el.getAttribute('data-when') !== m); });
  }
  modes.forEach(function (r) { r.addEventListener('change', syncMode); });
  if (modes.length) syncMode();

  // Day chips switch the visible group of time slots.
  $$('[data-day]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var day = btn.getAttribute('data-day');
      $$('[data-day]').forEach(function (b) { b.classList.toggle('on', b === btn); });
      $$('[data-day-group]').forEach(function (g) {
        var on = g.getAttribute('data-day-group') === day;
        g.classList.toggle('hide', !on);
        if (!on) $$('input', g).forEach(function (i) { i.checked = false; });
      });
    });
  });

  // Quick replies fill the note (tap again to clear).
  var note = $('#note');
  $$('[data-chip]').forEach(function (chip) {
    chip.addEventListener('click', function () {
      var text = chip.getAttribute('data-chip');
      var same = note.value === text;
      $$('[data-chip]').forEach(function (c) { c.classList.remove('on'); });
      note.value = same ? '' : text;
      if (!same) chip.classList.add('on');
    });
  });

  // Confirm before irreversible actions.
  $$('form[data-confirm]').forEach(function (f) {
    f.addEventListener('submit', function (e) {
      var msg = f.getAttribute('data-confirm');
      if (msg && !window.confirm(msg)) e.preventDefault();
    });
  });

  $$('[data-print]').forEach(function (b) { b.addEventListener('click', function () { window.print(); }); });

  // Waiting screens refresh themselves, but never while someone is typing or has a menu open.
  var r = $('[data-refresh]');
  var secs = r ? parseInt(r.getAttribute('data-refresh'), 10) : 0;
  if (secs > 0) {
    setInterval(function () {
      var busy = $$('input:not([type=hidden]):not([type=search]), textarea, select').some(function (el) {
        return el === document.activeElement || (el.type !== 'radio' && el.type !== 'checkbox' && el.tagName !== 'SELECT' && el.value);
      }) || $('details[open]');
      if (!busy && document.visibilityState === 'visible') location.reload();
    }, secs * 1000);
  }

  // "It's your turn": vibrate once per ticket.
  var buzz = $('[data-buzz]');
  var id = buzz && buzz.getAttribute('data-buzz');
  if (id && id !== '0' && navigator.vibrate) {
    try {
      if (!sessionStorage.getItem('cp_buzz_' + id)) { navigator.vibrate([300, 150, 300]); sessionStorage.setItem('cp_buzz_' + id, '1'); }
    } catch (e) { navigator.vibrate(300); }
  }
})();

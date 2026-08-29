(function () {
'use strict';

function byId(id) { return document.getElementById(id); }
var boot = window.__AIRHYTHM_BOOTSTRAP__ || {};
var settings = boot.settings || {};

function applySettings() {
  var limit = byId('limit');
  var preferred = byId('preferred');
  var excluded = byId('excluded');
  if (limit) limit.value = String(Number(settings.limit || 20));
  if (preferred) preferred.value = String(settings.preferred || '');
  if (excluded) excluded.value = String(settings.excluded || '');
}

applySettings();
}());

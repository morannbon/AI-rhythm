(function () {
'use strict';

var RUNTIME_THEME_EVENT = 'tvair-runtime-theme';
var appliedThemeGeneration = null;
var appliedThemeRevision = -1;

function safeText(value) {
  return value == null ? '' : String(value);
}

function isSafeCssValue(value) {
  var text = safeText(value).trim();
  if (!text || text.length > 96) return false;
  return /^[A-Za-z0-9#(),.%\-_ \/]+$/.test(text);
}

function normalizeContract(raw) {
  var result = Object.create(null);
  if (!raw || typeof raw !== 'object') return result;
  Object.keys(raw).forEach(function (key) {
    var value = raw[key];
    if (isSafeCssValue(value)) result[String(key).toLowerCase()] = String(value).trim();
  });
  return result;
}

function pick(contract, fallback, keys) {
  for (var i = 0; i < keys.length; i += 1) {
    var value = contract[String(keys[i]).toLowerCase()];
    if (isSafeCssValue(value)) return String(value).trim();
  }
  return fallback;
}

function buildThemeProjection(effectiveTheme, rawContract) {
  var dark = safeText(effectiveTheme).toLowerCase() === 'dark';
  var contract = normalizeContract(rawContract);
  var page = pick(contract, dark ? '#121212' : '#f5f6f7', ['pageBackground','background','backgroundColor','appBackground']);
  var surface = pick(contract, dark ? '#1e1e1e' : '#ffffff', ['surfaceBackground','surface','panelBackground','cardBackground']);
  var subtle = pick(contract, dark ? '#272727' : '#f0f1f2', ['subtleBackground','secondaryBackground','controlBackground']);
  var input = pick(contract, dark ? '#272727' : '#ffffff', ['inputBackground','fieldBackground','controlBackground']);
  var text = pick(contract, dark ? '#f2f2f2' : '#111111', ['text','foreground','textColor','foregroundColor']);
  var muted = pick(contract, dark ? '#b7b7b7' : '#5f6368', ['mutedText','secondaryText','mutedForeground']);
  var line = pick(contract, dark ? '#4b4b4b' : '#d2d5d9', ['border','borderColor','separator','line']);
  var accent = pick(contract, dark ? '#303030' : '#2f3337', ['accent','accentColor','buttonBackground']);
  var accentText = pick(contract, '#ffffff', ['accentText','accentForeground','buttonForeground']);
  var focus = pick(contract, dark ? '#7ab8f5' : '#5b9dd9', ['focus','focusColor','focusRing']);

  var controlBackground = pick(contract, input, ['controlBackground']);
  var controlText = pick(contract, text, ['controlText']);
  var controlBorder = pick(contract, line, ['controlBorder']);
  var controlHoverBackground = pick(contract, controlBackground, ['controlHoverBackground']);
  var controlHoverText = pick(contract, controlText, ['controlHoverText']);
  var controlHoverBorder = pick(contract, controlBorder, ['controlHoverBorder']);
  var selectedBackground = pick(contract, accent, ['selectedBackground']);
  var selectedText = pick(contract, accentText, ['selectedText']);
  var selectedBorder = pick(contract, selectedBackground, ['selectedBorder']);
  var selectedHoverBackground = pick(contract, selectedBackground, ['selectedHoverBackground']);
  var selectedHoverText = pick(contract, selectedText, ['selectedHoverText']);
  var selectedHoverBorder = pick(contract, selectedBorder, ['selectedHoverBorder']);
  var disabledBackground = pick(contract, subtle, ['disabledBackground']);
  var disabledText = pick(contract, muted, ['disabledText']);
  var disabledBorder = pick(contract, line, ['disabledBorder']);
  var primaryBackground = pick(contract, accent, ['primaryActionBackground']);
  var primaryText = pick(contract, accentText, ['primaryActionText']);
  var primaryBorder = pick(contract, primaryBackground, ['primaryActionBorder']);
  var primaryHoverBackground = pick(contract, primaryBackground, ['primaryActionHoverBackground']);
  var primaryHoverText = pick(contract, primaryText, ['primaryActionHoverText']);
  var primaryHoverBorder = pick(contract, primaryBorder, ['primaryActionHoverBorder']);
  var secondaryBackground = pick(contract, subtle, ['secondaryActionBackground']);
  var secondaryText = pick(contract, text, ['secondaryActionText']);
  var secondaryBorder = pick(contract, line, ['secondaryActionBorder']);
  var secondaryHoverBackground = pick(contract, secondaryBackground, ['secondaryActionHoverBackground']);
  var secondaryHoverText = pick(contract, secondaryText, ['secondaryActionHoverText']);
  var secondaryHoverBorder = pick(contract, secondaryBorder, ['secondaryActionHoverBorder']);
  var dangerBackground = pick(contract, primaryBackground, ['dangerActionBackground']);
  var dangerText = pick(contract, primaryText, ['dangerActionText']);
  var dangerBorder = pick(contract, primaryBorder, ['dangerActionBorder']);
  var dangerHoverBackground = pick(contract, dangerBackground, ['dangerActionHoverBackground']);
  var dangerHoverText = pick(contract, dangerText, ['dangerActionHoverText']);
  var dangerHoverBorder = pick(contract, dangerBorder, ['dangerActionHoverBorder']);

  return {
    name: dark ? 'dark' : 'light',
    variables: {
      '--page-bg':page, '--surface-bg':surface, '--subtle-bg':subtle, '--input-bg':input,
      '--text':text, '--muted':muted, '--chart-text':muted, '--line':line, '--accent':accent,
      '--accent-text':accentText, '--focus':focus,
      '--control-bg':controlBackground, '--control-text':controlText, '--control-border':controlBorder,
      '--control-hover-bg':controlHoverBackground, '--control-hover-text':controlHoverText, '--control-hover-border':controlHoverBorder,
      '--selected-bg':selectedBackground, '--selected-text':selectedText, '--selected-border':selectedBorder,
      '--selected-hover-bg':selectedHoverBackground, '--selected-hover-text':selectedHoverText, '--selected-hover-border':selectedHoverBorder,
      '--disabled-bg':disabledBackground, '--disabled-text':disabledText, '--disabled-border':disabledBorder,
      '--primary-bg':primaryBackground, '--primary-text':primaryText, '--primary-border':primaryBorder,
      '--primary-hover-bg':primaryHoverBackground, '--primary-hover-text':primaryHoverText, '--primary-hover-border':primaryHoverBorder,
      '--secondary-bg':secondaryBackground, '--secondary-text':secondaryText, '--secondary-border':secondaryBorder,
      '--secondary-hover-bg':secondaryHoverBackground, '--secondary-hover-text':secondaryHoverText, '--secondary-hover-border':secondaryHoverBorder,
      '--danger-bg':dangerBackground, '--danger-text':dangerText, '--danger-border':dangerBorder,
      '--danger-hover-bg':dangerHoverBackground, '--danger-hover-text':dangerHoverText, '--danger-hover-border':dangerHoverBorder,
      '--shadow':dark ? '0 10px 30px rgba(0,0,0,.28)' : '0 10px 30px rgba(15,23,42,.08)',
      '--card-shadow':dark ? '0 5px 16px rgba(0,0,0,.22)' : '0 5px 16px rgba(15,23,42,.06)',
      '--hero-a':'#2563eb', '--hero-b':'#7c3aed',
      '--chart-grid':dark ? 'rgba(255,255,255,.10)' : 'rgba(100,116,139,.18)',
      '--on-hero':'#ffffff', '--on-hero-soft':'rgba(255,255,255,.18)', '--on-hero-line':'rgba(255,255,255,.45)',
      '--viz-blue':'#5b8ff9', '--viz-blue-line':'#5b8ff966', '--viz-blue-soft':'#5b8ff914',
      '--viz-green':'#34d399', '--viz-green-line':'#34d39966', '--viz-green-soft':'#34d39914',
      '--viz-yellow':'#f6bd16', '--viz-orange':'#f59e0b', '--viz-orange-line':'#f59e0b88', '--viz-orange-soft':'#f59e0b18',
      '--viz-amber':'#f97316', '--viz-purple':'#8b5cf6', '--viz-cyan':'#06b6d4', '--viz-red':'#ef4444', '--viz-red-line':'#ef444466',
      '--viz-pink':'#ec4899', '--viz-blue2':'#3b82f6', '--viz-green2':'#22c55e', '--viz-teal':'#5ad8a6',
      '--summary-blue':'#60a5fa55', '--summary-purple':'#a78bfa22', '--summary-green':'#34d39955', '--summary-cyan':'#22d3ee22',
      '--summary-orange':'#f59e0b55', '--summary-red':'#fb718522'
    }
  };
}

function getThemeStyleElement() {
  var style = document.getElementById('airhythm-theme-vars');
  if (style) return style;
  style = document.createElement('style');
  style.id = 'airhythm-theme-vars';
  var head = document.head || document.getElementsByTagName('head')[0] || document.documentElement;
  if (head.firstChild) head.insertBefore(style, head.firstChild);
  else head.appendChild(style);
  return style;
}

function buildRootCss(variables) {
  var names = Object.keys(variables);
  var parts = [];
  for (var i = 0; i < names.length; i += 1) {
    parts.push(names[i] + ':' + variables[names[i]] + ';');
  }
  return ':root{' + parts.join('') + '}';
}

function applyProjection(generation, revision, selectedTheme, effectiveTheme, themeContract) {
  var projection = buildThemeProjection(effectiveTheme, themeContract);
  var style = getThemeStyleElement();
  var cssText = buildRootCss(projection.variables);

  // Keep Theme variables in a real stylesheet rule. The AI-rhythm SVG graphs
  // were previously validated on this path; documentElement.style custom
  // properties can leave SVG text/axes on stale/default colors in the Host browser.
  if (style.styleSheet) style.styleSheet.cssText = cssText;
  else style.textContent = cssText;

  var root = document.documentElement;
  root.setAttribute('data-theme', projection.name);
  root.setAttribute('data-tvair-theme-selected', safeText(selectedTheme));
  // Do not acknowledge a visually broken HotApply. The Host browser has already
  // shown that dynamic stylesheet paths can behave differently from initial HTML parsing.
  // Verify representative semantic + visualization variables synchronously.
  var computed = window.getComputedStyle ? window.getComputedStyle(root) : null;
  if (!computed) throw new Error('airhythm_theme_computed_style_unavailable');
  var checks = ['--page-bg', '--text', '--hero-a', '--viz-blue', '--viz-cyan', '--chart-grid'];
  for (var i = 0; i < checks.length; i += 1) {
    var expected = String(projection.variables[checks[i]] || '').replace(/\s+/g, '').toLowerCase();
    var actual = String(computed.getPropertyValue(checks[i]) || '').replace(/\s+/g, '').toLowerCase();
    if (!actual || actual !== expected) {
      throw new Error('airhythm_theme_css_variable_not_applied:' + checks[i]);
    }
  }

  appliedThemeGeneration = safeText(generation);
  appliedThemeRevision = Number(revision);
  if (!isFinite(appliedThemeRevision)) appliedThemeRevision = 0;
}

function acknowledge(detail, success, reason) {
  if (detail && typeof detail.acknowledge === 'function') {
    detail.acknowledge(!!success, String(reason || ''));
  }
}

function onRuntimeTheme(event) {
  var detail = event && event.detail ? event.detail : null;
  if (!detail) return;
  try {
    var generation = safeText(detail.generation);
    var revision = Number(detail.revision);
    if (!generation || !isFinite(revision)) {
      acknowledge(detail, false, 'airhythm_invalid_theme_generation_or_revision');
      return;
    }
    if (generation === appliedThemeGeneration && revision <= appliedThemeRevision) {
      acknowledge(detail, true, 'airhythm_theme_already_applied');
      return;
    }
    applyProjection(generation, revision, detail.selectedTheme, detail.effectiveTheme, detail.themeContract);
    acknowledge(detail, true, 'airhythm_theme_projection_applied');
  } catch (error) {
    acknowledge(detail, false, 'airhythm_theme_projection_failed');
  }
}

window.addEventListener(RUNTIME_THEME_EVENT, onRuntimeTheme, false);

// SDK 1.1.12 declared-handler transport.
// Host tvair-theme.js and window.TvAIrTheme already exist before this declared asset is loaded.
if (!window.TvAIrTheme || typeof window.TvAIrTheme.registerHotApplyClientReady !== 'function') {
  return;
}

window.TvAIrTheme.registerHotApplyClientReady();

window.__AIRHYTHM_THEME_RUNTIME__ = {
  eventName: RUNTIME_THEME_EVENT,
  transport: 'host_shell_declared_asset',
  getAppliedGeneration: function () { return appliedThemeGeneration; },
  getAppliedRevision: function () { return appliedThemeRevision; }
};
}());

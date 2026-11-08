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
  if (/(?:^|[^a-z])(?:expression|url|behavior|binding)\s*\(/i.test(text)) return false;
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

var loadedThemeSpec = null;

function getThemeSpec() {
  if (loadedThemeSpec) return loadedThemeSpec;
  var source = document.getElementById('airhythm-theme-schema');
  if (!source) throw new Error('airhythm_theme_spec_unavailable');
  var parsed = JSON.parse(source.textContent || source.innerHTML || '');
  if (!parsed || parsed.schema !== 1 || !Array.isArray(parsed.roles) || !parsed.palette || !parsed.modes || !parsed.layout)
    throw new Error('airhythm_theme_spec_invalid');
  loadedThemeSpec = parsed;
  return loadedThemeSpec;
}

function buildThemeProjection(effectiveTheme, rawContract) {
  var mode = safeText(effectiveTheme).toLowerCase() === 'dark' ? 'dark' : 'light';
  var contract = normalizeContract(rawContract);
  var spec = getThemeSpec();
  var values = Object.create(null);
  spec.roles.forEach(function (role) {
    var fallback = role.fallback ? values['--' + role.fallback] : role[mode];
    values['--' + role.name] = pick(contract, fallback, role.keys);
  });
  Object.keys(spec.modes[mode]).forEach(function (key) { values['--' + key] = spec.modes[mode][key]; });
  Object.keys(spec.palette).forEach(function (key) { values['--' + key] = spec.palette[key]; });
  Object.keys(spec.layout).forEach(function (key) { values['--' + key] = spec.layout[key]; });
  return { name: mode, variables: values };
}

var cssTemplate = null;

function getCssTemplate() {
  if (cssTemplate) return cssTemplate;
  var template = document.getElementById('airhythm-style-template');
  if (!template) throw new Error('airhythm_theme_css_template_missing');
  cssTemplate = template.textContent || template.innerHTML || '';
  if (!cssTemplate || cssTemplate.indexOf('var(--page-bg)') < 0)
    throw new Error('airhythm_theme_css_template_invalid');
  return cssTemplate;
}

function buildThemeStylesheet(template, variables) {
  var resolved = template.replace(/var\(--([a-z0-9-]+)\)/g, function (match, token) {
    var key = '--' + token;
    return Object.prototype.hasOwnProperty.call(variables, key) ? variables[key] : match;
  });
  // 処理条件 — Any remaining single-argument token means the shared schema and stylesheet diverge.
  if (/var\(--[a-z0-9-]+\)/.test(resolved))
    throw new Error('airhythm_theme_css_token_unresolved');
  var names = Object.keys(variables);
  var parts = [];
  for (var i = 0; i < names.length; i += 1) {
    parts.push(names[i] + ':' + variables[names[i]] + ';');
  }
  return ':root{' + parts.join('') + '}' + resolved;
}

function applyProjection(generation, revision, selectedTheme, effectiveTheme, themeContract) {
  var projection = buildThemeProjection(effectiveTheme, themeContract);
  var cssText = buildThemeStylesheet(getCssTemplate(), projection.variables);
  var style = document.getElementById('airhythm-theme-vars');
  if (!style) throw new Error('airhythm_theme_style_missing');

  // 表示・テーマ — MSHTML uses styleSheet.cssText; modern browser engines use textContent. Both paths replace
  // the same generated stylesheet instead of appending overrides.
  if (style.styleSheet) style.styleSheet.cssText = cssText;
  else style.textContent = cssText;
  var installed = style.styleSheet ? style.styleSheet.cssText : style.textContent;
  if (!installed || installed.indexOf('var(--page-bg)') >= 0)
    throw new Error('airhythm_theme_stylesheet_not_installed');

  var root = document.documentElement;
  root.setAttribute('data-theme', projection.name);
  root.setAttribute('data-tvair-theme-selected', safeText(selectedTheme));
  appliedThemeGeneration = safeText(generation);
  appliedThemeRevision = Number(revision);
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
    var reason = error && typeof error.message === 'string' ? error.message : '';
    acknowledge(detail, false, /^airhythm_theme_[a-z_]+$/.test(reason)
      ? reason : 'airhythm_theme_projection_failed');
  }
}

window.addEventListener(RUNTIME_THEME_EVENT, onRuntimeTheme, false);

// 表示・テーマ — Host-contract declared-handler transport. Host tvair-theme.js and window.TvAIrTheme already
// exist before this declared asset is loaded.
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

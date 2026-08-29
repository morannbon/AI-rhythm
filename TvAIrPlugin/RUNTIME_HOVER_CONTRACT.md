# TvAIr Plugin SDK 1.1.4 Runtime Hover Contract

## Purpose

`RuntimeHover` is a generic Runtime UI contract for Plugin-owned DOM elements.
The Host normalizes only mouse hover **enter / leave** detection across supported Runtime UI hosts.

The contract intentionally does **not** prescribe what the Plugin does with hover.
Popup, marquee, text expansion, highlight, preview, overflow detection, or no visual reaction at all are Plugin-owned behavior.

## Opt-in

A Plugin explicitly opts an element in by rendering the attributes returned by:

`RuntimeUiRenderContext.BuildRuntimeHoverAttributes(hoverKey)`

The Host never scans all Plugin text or automatically adds hover behavior.

The generated contract includes:

- `data-tvair-hover-key`
- `data-tvair-plugin-id`
- `data-tvair-route-segment`

`hoverKey` is Plugin-owned and identifies the logical element/action target.

## Event

The Host dispatches a bubbling DOM CustomEvent named:

`tvair-runtime-hover`

`event.detail` contains:

- `state`: `enter` or `leave`
- `hoverKey`
- `pluginId`
- `routeSegment`

The event target is the opted-in DOM element.

No HTTP request, action token, or Host mutation occurs for hover delivery.

## Ownership boundary

Host owns:

- enter / leave normalization
- supported Runtime UI host/browser differences
- delivery only for explicitly opted-in elements

Plugin owns:

- whether hover has any effect
- popup / marquee / expansion / highlight / preview
- text truncation or overflow detection
- displayed content, placement, timing, animation, and cleanup

## Example intent

AI-rhythm can opt a program-title element into RuntimeHover, test its own DOM overflow on `enter`,
show the complete title only when needed, and close or stop the presentation on `leave`.

The same Host contract may be used for non-text elements or other hover-driven Plugin behavior.

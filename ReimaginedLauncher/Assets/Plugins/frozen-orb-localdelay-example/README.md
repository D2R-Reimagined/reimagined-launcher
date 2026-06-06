# Frozen Orb Local Delay Example

A minimal plugin that demonstrates the **`dropdown`** parameter type and the **`visibleWhen`** parameter-visibility condition introduced in the launcher's plugin system.

## What it does

It edits a single column — `localdelay` on the **Frozen Orb** row of `skills.txt` — based on a dropdown the player picks:

| Dropdown value | Result |
|---|---|
| `Unchanged` *(default)* | No operation runs; `localdelay` is left exactly as the mod ships it. |
| `Fixed (300)` | `localdelay` is set to `300`. |
| `Custom` | `localdelay` is set to whatever number the player typed in the **Custom Local Delay** text box (default `25`). |

The **Custom Local Delay** text parameter only appears in the UI while the dropdown is set to `Custom`, thanks to its `visibleWhen` condition.

## How it works

- `plugininfo.json` declares a `dropdown` parameter (`frozenOrbLocalDelayMode`) with three `options`, plus a `text` parameter (`customLocalDelay`) gated by `visibleWhen`.
- `operations.json` holds two conditional `replace` operations on `skills.txt` → `Frozen Orb` → `localdelay`:
  - one writing `300`, gated by `equals: "Fixed (300)"`;
  - one writing `{{parameter:customLocalDelay}}`, gated by `equals: "Custom"`.
- When `Unchanged` is selected neither condition matches, so the launcher skips both operations — the documented "do nothing" behavior.

## File layout

```
frozen-orb-localdelay-example/
├── plugininfo.json   Manifest with a dropdown parameter and a visibleWhen text parameter.
├── operations.json   Two conditional replace ops on skills.txt → Frozen Orb → localdelay.
└── README.md         This file.
```

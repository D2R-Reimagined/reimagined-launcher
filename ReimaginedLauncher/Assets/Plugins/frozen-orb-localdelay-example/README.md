# Frozen Orb Local Delay Example

A minimal plugin that demonstrates the **`dropdown`** parameter type and the **`visibleWhen`** parameter-visibility condition introduced in the launcher's plugin system.

## What it does

A dropdown the player picks drives changes to `skills.txt`:

| Dropdown value | Result |
|---|---|
| `Unchanged` *(default)* | No operation runs; `localdelay` is left exactly as the mod ships it. |
| `Fixed (300)` | Frozen Orb `localdelay` is set to `300`, **and** Blizzard and Meteor `localdelay` are set to `150`. |
| `Custom` | Frozen Orb `localdelay` is set to the number typed in the **Custom Local Delay** box (default `25`); when **Also apply Aura Spirits (test)** is checked, the bundled Aura Spirits changes are applied too. |

The **Custom Local Delay** text box and the **Also apply Aura Spirits (test)** checkbox only appear in the UI while the dropdown is set to `Custom` (each via its own `visibleWhen` condition), and are indented under the dropdown to show they depend on it.

## How it works

- `plugininfo.json` declares a `dropdown` parameter (`frozenOrbLocalDelayMode`) with three `options`, plus two `visibleWhen`-gated subordinate parameters: a `text` parameter (`customLocalDelay`) and a `checkbox` (`applyAuraSpirits`).
- `operations.json` holds conditional `replace` operations on `skills.txt`:
  - Frozen Orb `localdelay` → `300`, gated by `equals: "Fixed (300)"`;
  - Blizzard and Meteor `localdelay` → `150`, also gated by `equals: "Fixed (300)"`;
  - Frozen Orb `localdelay` → `{{parameter:customLocalDelay}}`, gated by `equals: "Custom"`.
- `aura-spirits.json` bundles the Aura Spirits plugin's operations, each gated by an `all` condition (`frozenOrbLocalDelayMode == "Custom"` **and** `applyAuraSpirits == "true"`) so they only run for the `Custom` choice when the checkbox is on.
- When `Unchanged` is selected no condition matches, so the launcher skips every operation — the documented "do nothing" behavior.

## File layout

```
frozen-orb-localdelay-example/
├── plugininfo.json   Manifest: a dropdown plus two visibleWhen-gated subordinate parameters.
├── operations.json   Conditional localdelay ops for Frozen Orb, Blizzard and Meteor.
├── aura-spirits.json Bundled Aura Spirits ops, gated by the Custom choice + checkbox.
└── README.md         This file.
```

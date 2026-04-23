# Launcher Plugin Test Fixtures

This folder contains hand-assembled plugin packages used for regression testing
the launcher's plugin pipeline. They are **not** intended to be enabled against
a real game install — several operations change random properties on
foundational rows and will produce visibly broken behaviour in-game.

## Contents

- `AllFilesTestPlugin/` — single plugin that exercises at least one operation
  per registered `.txt` file (53 operation files, one per file type). Each
  operation file performs **two** value changes (usually one `replace` and one
  `multiplyExisting`), satisfying the "multiple values" requirement.

## Installing a test plugin in the launcher

1. Zip the chosen plugin folder so that `plugininfo.json` sits at the **root**
   of the archive (e.g. `AllFilesTestPlugin.zip > plugininfo.json` and
   `AllFilesTestPlugin.zip > operations/*.json`).
2. Open the launcher, go to the **Plugins** tab, and use
   **Install / Import plugin** to point at the zip. The launcher will run
   `PluginsService.LoadPluginImportPreviewAsync` and surface any validation
   errors before copying the archive into its managed plugin store.
3. Toggle the plugin on, open the **Test Multiplier** parameter, and launch the
   game (or use the launcher's preview machinery).

## AllFilesTestPlugin — coverage

- 1 operation JSON per registered target file.
- Each file contains exactly **two** operations — typically a `replace` on one
  column and a `multiplyExisting` on a second column driven by the shared
  `testMultiplier` parameter (default `1.0`, i.e. no change unless the user
  edits it).
- For files whose entry type exposes only a couple of writable string
  properties (`cubemod.txt`, `gamble.txt`, `gems.txt`, `skillcalc.txt`,
  `storepage.txt`, `states.txt`), both operations are `replace`-on-string
  because `multiplyExisting` only supports numeric columns.

### Known caveats

- Sample `rowIdentifier` values are taken from the first usable data row of
  the mod's excel files
  (`C:\z_GitHub\d2r-reimagined-mod\data\global\excel\*.txt`). If a tester
  points the launcher at a different mod whose first data rows have different
  identifiers, update the relevant operation file.

## Running the validator

A small PowerShell helper mirrors the launcher's own validation rules against a
test-plugin directory (structure only — it does not apply operations):

```powershell
powershell -ExecutionPolicy Bypass -File validate_test_plugin.ps1 `
    -PluginRoot .\AllFilesTestPlugin
```

The validator lives next to this README (`validate_test_plugin.ps1`). It is
kept out of the main solution on purpose — it is an authoring aid that
mirrors the launcher's own `PluginsService.ValidateOperations` checks.

### Data-aware validator

A second helper (`verify_against_mod_data.ps1`) cross-checks every operation
against the **actual** mod excel files plus the compiled entry-model property
names, catching runtime-only failures that the structural validator cannot
see:

- `column` must be a public property on the registered entry model
  (case-insensitive — matches `PluginsService.UpdateRecord` reflection).
- `rowIdentifier` must resolve either as an integer index (for RowId files)
  or to a row whose registered lookup-column value matches.
- `multiplyExisting` must target a property whose declared type is numeric
  **and** whose cell value in the data file is a parseable number (the
  launcher throws "The existing value '…' in column '…' is not numeric and
  cannot be multiplied" otherwise).

```powershell
powershell -ExecutionPolicy Bypass -File verify_against_mod_data.ps1
```

Defaults point at `C:\z_GitHub\d2r-reimagined-mod\data\global\excel` and the
model sources under `C:\z_GitHub\d2r-dotnet-tools\FileExtensions\Models`;
override `-ModExcelDir` or edit the `$modelsDir` variable if those paths
differ on your machine.

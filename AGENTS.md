These instructions apply to the entire repository.
- This repo contains an Avalonia desktop launcher in `ReimaginedLauncher/`.
- The main project file is `ReimaginedLauncher/ReimaginedLauncher.csproj`.
- The app targets `net10.0` with nullable reference types enabled.
- UI views live in `ReimaginedLauncher/Views/` with paired `.axaml` and `.axaml.cs` files.
- Keep changes focused and minimal; do not refactor unrelated code.
- Preserve the existing C# style, file-scoped namespaces, and naming patterns.
- Prefer existing services and utilities before adding new abstractions.
- Do not edit generated or build output files under `bin/` or `obj/`.
- Do not edit IDE metadata under `.idea/` unless the user explicitly asks.
- Restore packages with `dotnet restore ReimaginedLauncher.sln`.
- Build with `dotnet build ReimaginedLauncher.sln`.
- Run the app with `dotnet run --project ReimaginedLauncher/ReimaginedLauncher.csproj`.
- When making code changes, validate the smallest relevant command first before broader checks.
- Root `.gitignore` already covers standard .NET and IDE artifacts.
- `Program.cs` configures dependency injection and Avalonia startup.
- HTTP client code lives under `ReimaginedLauncher/HttpClients/`.
- Shared application helpers live under `ReimaginedLauncher/Utilities/`.
- Match the surrounding comment density. Do not add narrative or rationale comments; put rationale in the commit message or PR description instead. Keep only short comments (at most a couple of lines) that document non-obvious behavior, invariants, or external contracts.

UI manipulation rule for Avalonia templated controls (Flyout, MenuFlyout, ToolTip, ContextMenu, Window chrome, ScrollViewer, TextBox/TextPresenter, ContentPresenter, ItemsPresenter, etc.):
- Always ask first: "What does the templated parent (or theme) constrain this to?" — never start with "Which property on the child should I try next?". If a property "doesn't take", something upstream is winning; find that constraint before trying another child property.
- Trying a child-element property once or twice as a quick sanity check is fine; if it doesn't take, stop iterating on the child and move up to the templated parent / theme. Don't keep guessing at child properties past a couple of attempts.
- Before changing properties on inner elements, inspect in this order:
  1. The control's theme template in the active theme (Fluent/Simple) — find the presenter and its bindings.
  2. The theme resources it consumes (e.g. `FlyoutThemeMaxWidth`, `*ThemeMinWidth`, `*ThemeMaxWidth`, `*ThemeFontSize`, `*ThemeHeight`, `ControlContentThemeFontSize`).
  3. Any implicit styles or `*PresenterClasses` selectors the theme applies (e.g. `FlyoutPresenterClasses`).
  4. The parent layout container's measure/arrange contract (Grid star sizing, DockPanel `LastChildFill`, ScrollViewer's infinite measure, Viewbox).
- The correct fix is almost always one of:
  - Overriding a theme resource at an appropriate scope (`Application.Resources`, `Window.Resources`, or local `Resources`).
  - Adding/overriding a style targeting the presenter (e.g. `FlyoutPresenter`, `MenuFlyoutPresenter`, `ToolTip`, `TextPresenter`, `ScrollContentPresenter`).
  - Adjusting the parent container's sizing contract.
- Modifying properties on the inner content/child is rarely the right fix for templated-control sizing/styling issues and typically burns hours chasing symptoms instead of the constraint. It's not forbidden — a quick child-property attempt is a valid first probe — but if one or two tries don't move the needle, switch to inspecting the templated parent/theme rather than continuing to iterate on the child. This applies equally to popup-family sizing, window chrome/resize behavior, and text-scalar/font-size issues.

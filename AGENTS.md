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

Avalonia templated controls (Flyout, MenuFlyout, ToolTip, ContextMenu, ScrollViewer, TextBox/TextPresenter, ContentPresenter, ItemsPresenter, Window chrome, etc.):
- If a property on an inner element "doesn't take", the templated parent or theme is overriding it. Stop after 1–2 child-property probes and look upstream.
- Inspection order: (1) active theme's control template and its presenter bindings; (2) theme resources it consumes (`FlyoutThemeMaxWidth`, `*ThemeMinWidth/MaxWidth`, `*ThemeFontSize`, `*ThemeHeight`, `ControlContentThemeFontSize`); (3) implicit styles / `*PresenterClasses` selectors; (4) parent layout contract (Grid star sizing, DockPanel `LastChildFill`, ScrollViewer infinite measure, Viewbox).
- Fix is almost always one of: override a theme resource at the right scope (`Application`/`Window`/local `Resources`); add a style targeting the presenter (`FlyoutPresenter`, `MenuFlyoutPresenter`, `ToolTip`, `TextPresenter`, `ScrollContentPresenter`); or adjust the parent's sizing contract. Modifying the inner child is rarely the right fix.

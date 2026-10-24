# Patch notes reader

Patch Notes has its own navigation entry and page heading. News & Announcements shows only the announcement feed; unread tracking retains its existing behavior. Install/Update does not repeat the Patch Notes action. If the wiki has no matching version, the reader explains the mismatch and shows the newest wiki release.

The reader fetches the public [wiki version index](https://wiki.d2r-reimagined.com/en/Patch_Notes)
and the selected wiki article. It renders headings, paragraphs, nested lists,
tables and source links as Avalonia controls. Links appear once as inline,
keyboard-focusable hyperlinks in their original text position, including table
cells. HTML scripts are never executed.
Link paragraphs use automatic line height and the native available-width
measurement, so wrapped URLs are not clipped to one line when loading cached
notes or resizing the window.
Older releases that the wiki links directly to GitHub display their index
summary and a View on GitHub action; their external articles are not cached.
Images remain browser links.

The cache lives in `cache/patch-notes` beneath the existing launcher application
data directory (`%APPDATA%/ReimaginedLauncher` on Windows; the .NET per-user
ApplicationData location on Linux). It is separate from settings.json because
these are downloaded articles, not preferences. Filenames are SHA-256 keys of
the public wiki URL. No account token or user content is sent to the wiki.

The index and opened articles are reused for six hours. Refresh re-fetches the
index and selected article. Older cached data triggers an automatic refresh on
access; network or markup failures retain the last valid cached copy and show
its saved time. Articles never opened require an internet connection. Validated
responses replace cache files using a temporary file and rename. A failed cache
write still displays the live article and reports that offline saving failed.

Validation: PatchNotesTests covers version sorting/ranges, safe source links,
nested lists/tables, restart persistence, stale data, corrupt cache, changed wiki
markup, offline errors and cache write failure. The isolated Avalonia smoke
reader fetched the live index (50 releases) and 3.0.12/3.0.11, then recreated the
service and view with network disabled and retained notes on Refresh. Rendering
was checked at 1280x800 and 1500x950 on Windows. This is not a physical Steam Deck
runtime test.

The link-layout regression additionally loads a cached article before attaching
its view, then checks the actual embedded link and text bounds while resizing
between 700, 1050, 1280 and 1760 pixels. Before the fix, a wrapped link needed 44
pixels but its paragraph reserved 22; the same check now passes with the complete
URL visible. The 700-pixel case deliberately exercises a narrower layout than
the launcher's normal minimum window size.

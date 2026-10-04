# quartermaster

A Helldivers 2 mod manager written in C# with Avalonia.

Run the desktop app with `just run` or
`dotnet run --project src/Quartermaster.Gui/Quartermaster.Gui.csproj`.

Right-click a profile in the sidebar to **Export profile ZIP**. Right-click the
**+** button to **Import profile ZIP**, choose **Choose from file…** in the
create-profile dialog, or drop a profile ZIP anywhere in the sidebar.
Import creates and selects a new profile,
reusing matching mods already in the library.

Profile archives contain `profile.json` and the original files under
`mods/<id>/`. They preserve the profile name, load priority, mod order, enabled
states, option selections and groups. Disabled mods, unused option variants,
manifests and artwork are included. Importing a profile does not deploy it.

Open **Settings**, paste your personal Nexus API key, and choose **Save**.
The same Save button applies your game settings, key changes, and browser download
folder. To disconnect, select **Remove saved API key**, then Save.

In **Library**, choose **Add mod** to paste a mod link or import a local ZIP/folder.
Quartermaster identifies the provider from the link; Nexus Mods is currently the
only supported provider. For a Nexus mod page, select a file in the dialog.
Quartermaster queues it and opens its download page in your default browser.
Complete the download on Nexus; Quartermaster verifies the archive against
Nexus's MD5 lookup and imports it automatically. Browser downloads are left in
place. The sidebar's **Downloads** tab has **Open download page**, **Attach ZIP**,
**Retry**, and **Cancel** actions. Only ZIP archives are currently supported.
Use the dedicated **Search** page to find Helldivers 2 Nexus mods and add them
through the same file-selection flow. Choose a provider from the dropdown (Nexus
by default), then press Enter or click Search. The page reports loading, no
matches, and search errors, including missing API-key configuration.
Search results show provider thumbnails in larger cards. The file chooser's
**Open download page** action explains the browser download flow before opening
it. A floating activity spinner tracks pending downloads across pages; manage
requests in the **Downloads** tab. **Attach ZIP** explicitly associates an
existing archive with a request when recognition fails or you already have the
file. It skips provider recognition and uses the normal mod import and profile
upgrade flow. **Cancel** removes a pending request; **Remove** clears a completed
or failed entry without removing imported mods.

Choose **Register nxm links** in Settings to make Quartermaster your user account's
`nxm://` handler on Linux or Windows. Nexus's **Download with manager** action can
then send a signed download link directly to the running app. Free accounts need
that signed, unexpired link; a plain mod URL does not authorize a direct download.
You can also paste an nxm link into **Add mod**. Registration on macOS is not yet
implemented. Registration is explicit and changes the default nxm handler.

Nexus imports retain their mod/file identity. **Check updates** in Library checks
all tracked mods; the same button in a profile checks that profile's mods.
Checks only report updates. An **Update** button appears on mods with available
updates in both lists. Click it to open the replacement file's download page and
start watching for its download. Verified updates replace references in profiles
while preserving order, groups, enabled states, and compatible option selections.
If selected options are no longer compatible, the new mod stays in the library
and the request reports an error for manual configuration. Updates do not deploy
automatically; original library files remain.

`downloads.json` persists the queue and watched folders. Normal app exit clears
unfinished requests; pending requests left by a crash can resume at startup.
Browser files are left untouched. Signed nxm grants are not stored in the queue or library.
`temp/` holds transitional files: mod/profile extraction, deployment staging,
download copies, and metadata writes. Completed or failed operations clean up
their temporary files. Import errors appear in a floating notification that
dismisses on click or after seven seconds; download failures also remain in the
Downloads tab. Personal API keys live separately
under `credentials/` (owner-only files on Unix), outside profile exports. Keys
are local plaintext files, not an encrypted system keychain. A private
`nxm-inbox/` briefly holds protocol links forwarded by another application
instance, and `app.lock` prevents opening a second GUI for the same data folder.

Provider implementations share `DownloadScanner`: override `IsCandidate` and
`VerifyAsync` to recognize their downloads, and use `OpenDownloadPage` to open
the user's browser. `ProviderManager` coordinates the persistent queue and
library imports. Nexus routes and search follow the
[official API client](https://github.com/Nexus-Mods/node-nexus-api) and
[GraphQL documentation](https://graphql.nexusmods.com/).

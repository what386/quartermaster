# Quartermaster

Quartermaster is a Helldivers 2 mod manager written in C# with Avalonia.

It imports mods from local ZIPs, folders, Nexus Mods, and GitHub releases,
organizes them into profiles, and deploys their selected options to the game.
Repatching uses a C# port of hd2_repatcher; original library files remain unchanged.

## Installation

### Release archives

See [GitHub releases](https://github.com/what386/quartermaster/releases) for
packaged builds. Extract the archive matching your operating system and
architecture, then run `Quartermaster.Gui` (`Quartermaster.Gui.exe` on Windows).
On Unix, make it executable with `chmod +x Quartermaster.Gui` if needed.

`bundled` archives include .NET. `runtime` archives require the .NET 10 runtime.
Release builds target Linux, Windows, and macOS on x64 and ARM64.

### Run from source

Install the .NET 10 SDK, then run:

```bash
dotnet run --project src/Quartermaster.Gui
```

With [just](https://github.com/casey/just) installed, use `just run`.

## Common workflows

### Add mods

Choose **Add mod** in Library to import a ZIP, folder, or provider link.
You can also drop ZIPs or folders into Library or a profile; dropping into a
profile imports the mods and adds them to that profile.

- **Nexus Mods:** paste a mod link and choose a file. Quartermaster opens its
  download page, watches your configured browser download folder, verifies the
  archive, and imports it. The **Search** page finds Helldivers 2 Nexus mods.
- **GitHub:** paste a repository, release, or release asset link and choose an
  uploaded ZIP. Repository links use the latest stable release. Quartermaster
  downloads the asset directly; generated source-code archives are excluded.

Provider imports currently support ZIP archives. Manage requests in
**Downloads**: attach an existing ZIP when recognition is stuck, retry a failed
request, or cancel it. Removing a completed entry keeps the imported mod.
Browser files are left untouched. Import failures appear in a floating
notification and failed requests retain their error details in Downloads.

### Manage profiles

Click **+** in the sidebar to create a named profile. Click a profile to open it,
and right-click its icon to rename or delete it.

Right-click mods in Library and use **Add to** to add them to a profile.
Enable or disable mods, change their options with the sliders button, and drag
them to change load order. Right-click to create collapsible groups or group
selected mods. Load priority is configured in Settings.

### Deploy and run

Choose your Helldivers 2 game folder in Settings, or use **Find Steam installs**.
Click **Deploy** in a profile to apply its enabled mods and options. You can also
double-click its sidebar icon to open the deployment confirmation.

Settings lets you choose whether to ask before repatching, repatch automatically,
or never repatch. Repatching happens during deployment without modifying the
originals. Use **Export repatched ZIP** from a mod's Library context menu to
save a repaired copy separately.

**Run** launches the game and warns about deployment mismatches. **Purge** removes
the game's patches so you can redeploy. The deployed profile is marked green;
yellow mod outlines indicate enabled mods or changed options that need deploying.

### Check for updates

**Check updates** in Library checks all tracked mods; in a profile it checks that
profile's mods. Click a mod's **Update** button to upgrade it.

Nexus updates use the browser download flow. GitHub updates download directly,
matching the installed asset's filename or the latest release's sole ZIP.
Ambiguous replacements require choosing a file yourself.

Upgrades preserve profile order, groups, enabled states, and compatible option
selections. Incompatible options report an error for manual configuration.
Updates do not deploy automatically.

### Import and export profiles

Right-click a profile and choose **Export profile ZIP**. To import one,
right-click **+**, choose **Choose from file…** in the create-profile dialog, or
drop the archive into the sidebar.

Archives contain the profile's settings, mod order, groups, option selections,
and original mod files, including disabled mods and unused variants. Importing
creates a new profile and reuses matching mods already in Library. It does not
deploy the profile.

## API tokens

Configure provider credentials in **Settings** and use the shared **Save** button.

- **Nexus Mods:** a personal API key is needed for search, update checks, and
  browser download verification. Get it from Nexus account settings → API.
- **GitHub:** a personal access token is optional and increases the API rate
  limit. Create one under GitHub Settings → Developer settings → Personal
  access tokens. New tokens are validated before saving.

Credentials are stored separately under `credentials/` and are excluded from
settings and profile exports. Select the provider's removal checkbox and Save
to remove a credential. GitHub tokens are sent only to GitHub API requests.

Choose **Register nxm links** in Settings to handle Nexus's **Download with
manager** links on Linux or Windows. Free accounts can use signed, unexpired
`nxm://` links; a plain mod URL does not authorize a direct download. You can
also paste an nxm link into **Add mod**.

## Data storage

Quartermaster stores its data in your user's local application data directory
under `Quartermaster`. Set `QUARTERMASTER_DATA_DIRECTORY` to use another location.

- `library/`: original imported mod files.
- `library.json` and `profiles.json`: mod metadata and profiles.
- `settings.json`: application settings.
- `deployment.lock`: the current deployment state.
- `downloads.json`: download requests and watched folders.
- `credentials/`: local plaintext provider credentials, with owner-only access on Unix.
- `temp/`: transitional imports, downloads, and deployment staging.
- `log.jsonl`: application events.

Normal app exit clears unfinished download requests. Requests left by a crash
can resume at startup. Temporary files are cleaned up when operations finish.

## Development

```bash
just build
just test
just lint
just package linux-x64
```

See the [repatcher documentation](src/Quartermaster.Repatcher/README.md) for
patching details and the [changelog](CHANGELOG.md) for release changes.

## License

[MIT](LICENSE)

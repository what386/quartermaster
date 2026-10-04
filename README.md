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

# Localization

Quartermaster ships an English source catalog. Select **Settings → App settings → Language**, save, and restart to apply a language. **System default** follows the operating system's UI language; missing languages and strings fall back to English. Existing settings files keep this default.

## Adding a translation

Copy `src/Quartermaster.Gui/Localization/Catalogs/en.json`, change `language` to a .NET culture code (such as `de`, `es`, or `pt-BR`), and change `name` to the language's native name. Translate the **values** in `strings`; keep the English keys unchanged. Partial catalogs work.

Put the UTF-8 JSON file in either:

- `Localization/` next to the Quartermaster executable, for distributed language packs.
- `localization/` next to `settings.json`, for user-installed language packs.

User-installed catalogs take precedence. Available catalogs appear in the language selector after restarting. Regional cultures fall back to their parent catalog, then English. Invalid catalogs are ignored; `Localizer.LoadDirectory` returns their validation errors.

```json
{
  "language": "de",
  "name": "Deutsch",
  "strings": {
    "Settings": "Einstellungen",
    "Disable {0}": "{0} deaktivieren"
  }
}
```

`{0}`, `{1}`, etc. are .NET composite-format arguments. Preserve every argument index and any required numeric formatting, such as `{0:x16}`. Arguments may be reordered or repeated. Missing, added, or malformed placeholders cause that catalog to be rejected. Do not translate mod names, profile names, paths, URLs, provider metadata, protocol identifiers, or version strings.

## Adding UI text

Use `{loc:Loc 'English text'}` in XAML with `xmlns:loc="clr-namespace:Quartermaster.Gui.Localization"`. In C#, use `Localizer.Text("English text")` or `Localizer.Interpolate($"An update for {mod.Name} is available")`. Add the corresponding English key/value to `en.json`; interpolation uses numbered placeholders, e.g. `An update for {0} is available`.

For `ModPresentation.Count(count, "mod")`, include both `{0} mod` and `{0} mods` in the catalog. This helper currently supports singular/other forms. Languages with additional grammatical number forms may need language-specific complete phrases.

Keep complete sentences together where practical so translators can reorder their arguments. Keep state and identifiers separate from translated labels. Provider/backend error messages and descriptions supplied by mod authors retain their original text.

The localization engine has no network dependency. English is embedded in the executable; adding translations does not require recompiling it.

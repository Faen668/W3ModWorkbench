# Localisation

Every word W3 Mod Workbench shows comes from a string table, so the app can be translated. English is the source;
every other language is a copy of the English table with the values translated. The guide (F1) is translated a page at
a time beside it.

| What | Where |
|---|---|
| The English strings, the source | `W3ModWorkbench/Localisation/Strings/en.json` |
| Another language's strings | `W3ModWorkbench/Localisation/Strings/<tag>.json` (`de.json`, `pt-BR.json`) |
| The code that reads them | `W3ModWorkbench/Localisation/Loc.cs` (`L.T`) and `TExtension.cs` (`{l:T}`) |
| Translated guide pages | `W3ModWorkbench/Help/Pages/<tag>/…`, see `Help/Pages/README.md` |
| The check for text left in the code | `tools/StringCheck` |

## Why JSON tables, not .resx

- **The language changes while the app runs.** XAML binds to the table through one indexer (`{l:T Key}`), which
  updates every bound string the moment the language changes, with no restart and no generated code.
- **Placeholders by name, and plurals per language.** A value names its placeholders (`{count:N0} files in
  {folder}`), so a translator can reorder them, and can be a set of plural forms (`one`, `few`, `many`, `other`)
  chosen by the language's own rules. .resx has neither.
- **Easy to translate and to review.** One flat, sorted file per language, readable keys, diffs that show exactly
  what changed. A translator needs a text editor, not Visual Studio, and can try a translation without building the
  app (see below).

## Keys

Keys are `Window.Part.Purpose`, readable rather than numbered: `Settings.Game.WccFound`,
`MainWindow.Extract.Checked`. Strings used in many windows (OK, Cancel, Save) are under `Common.`. A key names what the
string is for, so `Settings.Game.WccMissing` stays the same key if its English wording changes.

## In XAML

```xml
<Window xmlns:l="clr-namespace:W3ModWorkbench.Localisation"
        Title="{l:T Settings.Title}">
    <TextBlock Text="{l:T Settings.General.Intro}" />
    <Button Content="{l:T Common.Save}" ToolTip="{l:T Settings.HelpTip}" />
```

`{l:T}` is a binding, so it follows the language at once. It works anywhere a binding does: `Text`, `Content`,
`Header`, `ToolTip`, `Title`, a `Run`, a `Setter`'s value.

## In C#

```csharp
using W3ModWorkbench.Localisation;

Status.Text = L.T("Settings.Game.GameFound");
Status.Text = L.T("Settings.Game.GameFoundUsed", folder);         // "Not set, so the one found is used: {folder}"
Status.Text = L.T("Settings.General.RecentCount", count);         // "{count:N0} remembered: …"
label.Bind(TextBlock.TextProperty, "Settings.Nav.General");      // an element made in code, kept live like XAML's
```

- **Placeholders** are named in the English string and filled from the arguments in the order the English names
  them. A translation uses the same names in any order. Format strings work as in C#: `{count:N0}`,
  `{date:d MMMM yyyy}`, `{size:0}`. A literal brace is doubled: `{{` and `}}`.
- **Plurals**: a value that's an object of plural forms is picked by its `{count}` argument, or else by the first
  number given. A whole sentence can be plural, so each language words all of it as it needs:

  ```json
  "Count.File": { "one": "{count:N0} file", "other": "{count:N0} files" },
  "StringsWindow.SavedCopies": { "one": "Saved {file} and {count} copy for the other languages.",
                                 "other": "Saved {file} and {count} copies for the other languages." },
  "MainWindow.ChangesLost": { "one": "{names} has changes that are lost if you don't save them.",
                              "other": "{names} have changes that are lost if you don't save them." }
  ```

  `L.T("MainWindow.ChangesLost", names, unsaved.Count)` passes the count without showing it. The `Count.` keys are
  "how many of a thing" (`L.T("Count.File", n)`), used inside other strings.

  English has `one` and `other`. Polish has `one`, `few` and `many`; Russian the same; Czech `one`, `few`, `other`;
  French counts 0 as `one`; Japanese and Chinese have only `other`. A table gives the forms its language has
  (`Loc.PluralCategory` has the rules); a form it leaves out falls back to `other`.
- **In a binding**: a bound value goes into a string with the `LocFormat` converter, in place of a `StringFormat`:
  `Text="{Binding Name, Converter={x:Static l:LocFormat.Instance}, ConverterParameter=SceneWindow.OpenCutscene}"`.
- A sentence put together from pieces in English should be one string with placeholders, so a translation can order
  it as its language needs.

Never compare or look up a translated string: a choice is known by its index, a name (`x:Name`), a tag or an enum,
not by its text, which is different in every language. Text that goes somewhere read in English, such as an issue on
EasyAssetBrowser's repository, stays English.

Text that isn't for reading (code samples, file names, the licence, a language's own name) stays in the code, marked
for the check with `// loc-ignore` on the line, or between `// loc-ignore-start` and `// loc-ignore-end`. In XAML,
put `<!-- loc-ignore -->` on the line before the element.

## Adding a language

1. Copy `en.json` to `<tag>.json` in `Localisation/Strings`, named with the language's tag: `de`, `fr`, `pl`, `ru`,
   `cs`, `pt-BR`, `zh-Hans`. Change `$language` to the language's name in English.
2. Translate the values. Keep the keys, the `{placeholder}` names and the `\n`s. For a plural string, give the forms
   your language has. Leave a string out, or empty, and the English shows.
3. To try it without building: put the file in a `Languages` folder beside `W3ModWorkbench.exe`, start the app, and
   pick the language under **Settings > Language > The app's language**. A file there is used before the built-in
   one, so you can keep editing it and restart to see the change.
4. Run the check (below): it flags keys that aren't in `en.json` and placeholders the English doesn't name.
5. Translate guide pages under `Help/Pages/<tag>/` as `Help/Pages/README.md` says, as many as you like: the rest show
   in English.
6. Open a pull request with the table (and pages). Building it in is all it takes: the language is listed in Settings
   from then on, and chosen by itself for anyone whose Windows is in that language.

When English strings are added or changed later, the other tables don't have them until they're translated; the
English shows meanwhile.

## The pseudo-locale

**Settings > Language** lists `[Ƥšéûðö ~~]` in Debug builds. It shows every string accented, about a third longer and
in brackets: `[Šéţţîñĝš ~~~]`. Text still in plain English hasn't gone through the tables, a missing `]` means the text
is clipped, and a layout that only just fits English shows where a longer language won't fit.

## The check

```cmd
dotnet run --project tools/StringCheck
```

It lists, with file and line:

- text written straight into XAML (`Text="Save"`) or C# (`"Not set."`) that a person reads, rather than taken from a
  table;
- `{l:T Key}` and `L.T("Key")` keys that aren't in `en.json`;
- in other languages' tables, keys `en.json` hasn't got, placeholders the English doesn't name, and plural forms that
  aren't CLDR's.

It exits with 0 when there's nothing to fix. `--summary` gives a count per file, `--file Settings` checks only files
with that in their path, and `--unused` lists English keys nothing uses. `--strict` also counts single words in C#
wherever they are (a heading made in code, a menu item), which lists many names and keys too: use it to sweep a new
window by eye. It skips `Core` (the game's formats) and
`CommandLine.cs` (the command line's usage, kept in English). Some text the windows show still comes from `Core` in
English: the file kinds' names and descriptions, the game's own words (class names, event kinds) and messages from
reading and compiling. Those are next.

The check tells text from code by what it looks like and where
it goes, so mark a string it gets wrong with `loc-ignore` rather than leaving the check failing.

**Every new window and message goes through the tables**: run the check before committing.

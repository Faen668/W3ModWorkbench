# EasyAssetBrowser and W3 Mod Workbench

How W3 Mod Workbench uses EasyAssetBrowser (EAB), and the one small change on EAB's side that lets the site open a file in the Workbench.

## What the Workbench does with EAB's data

- **Opt-in only.** EAB tags are off until the user turns them on and agrees to the tool going online. EAB is the only thing in the Workbench that goes online.
- **Where it reads from.** The same files the site reads: `assets/data/categoryConfig.json`, then each `file` it lists, from `https://raw.githubusercontent.com/vincocrafter/EasyAssetBrowser/main/assets/data/`. It reads them at most once a day and keeps a copy for offline use. Nothing else is fetched and nothing is sent.
- **What it relies on.** The config's `categories` (`name`, `file`, `tags`), and each asset's `name`, `category`, `origin`, `tags` and `path` (forward slashes, relative to the game's depot). If that format changes, please say so and the Workbench will follow. New categories and new tags work without any change.
- **How it's shown.** Always credited as EAB's: **EAB category** and **EAB tags** filters (include and exclude, as on the site) and columns in the Asset Browser, and an EasyAssetBrowser window (right-click > **View EAB details…**) with each file's name, category, origin, tags and path, and a button that opens the file on the site (`?cat=…&q=^name$&nR=1&p=folder`).
- **Tag suggestions** arrive as **GitHub issues** on the repository, titled `Tag suggestion: <name>` (or `Tag suggestions: <name> and N more`). Each has a table of what changed and the files in EAB's own JSON form, ready to paste into the lists. Users post them themselves, under their own GitHub accounts. Nothing is posted automatically.

## Opening a file in the Workbench from the site

The Workbench registers a link type, `w3modworkbench://`, when the user turns on **Open files from EasyAssetBrowser's site here** (ⓘ menu in its Asset Browser). A link opens the Asset Browser at the file, highlighted. If the Workbench is already running, the running copy shows it.

### The link

```
w3modworkbench://asset?path=<the asset's path, URL-encoded>
w3modworkbench://asset?path=<the asset's path, URL-encoded>&action=open
```

- `path`: the asset's `path` exactly as in EAB's JSON (forward slashes are fine), encoded with `encodeURIComponent`.
- `action=open` (optional) also opens the file in the Workbench's viewer. Without it, the file is only shown and highlighted.

For example:

```
w3modworkbench://asset?path=environment%2Farchitecture%2Fhuman%2Fkaer_morhen%2Fkaer_morhen_exterior%2Fcorner_trim_45.w2mesh
```

### What to add to the site

A button in the asset's detail view, next to the copy-path button, is enough:

```js
function workbenchLink(asset, open = false) {
    return "w3modworkbench://asset?path=" + encodeURIComponent(asset.path) + (open ? "&action=open" : "");
}

// in the detail view, for the asset shown:
const button = document.createElement("a");
button.href = workbenchLink(asset);
button.textContent = "Open in W3 Mod Workbench";
button.title = "Shows this asset in W3 Mod Workbench's Asset Browser. Needs W3 Mod Workbench, with \"Open files from EasyAssetBrowser's site here\" turned on.";
detailView.appendChild(button);
```

Notes:

- Use a plain link (`<a href>`). The browser asks the user once ("Open W3 Mod Workbench?") and can remember the answer.
- A web page can't reliably tell whether the Workbench is installed. If it isn't, or the link type isn't turned on, the click simply does nothing. That's why the tooltip says what's needed. A short line in the site's help pointing to the Workbench would help people find the setting.
- Links are only ever read as a game path to look up in the Asset Browser. Anything else (another scheme, `..`, a drive letter) is ignored.

## Paths in the lists that aren't in the game

Of EAB's 57,411 paths, 3,388 aren't in the cooked game. 3,379 of them are REDkit-only source files (in REDkit's `r4data`, such as editor templates and community action points), which makes sense for a REDkit tool. The Workbench's Asset Browser lists REDkit's files too, under a REDkit heading, once the user has set REDkit up in its Settings, so a link to one of those opens REDkit's copy. Without REDkit set up, it says the file isn't in the game's files.

Only these 9 are in neither the game nor REDkit, and may be worth checking:

| Category | Path |
| --- | --- |
| Meshes | `environment/terrain_surroundings/common/cave/cave_big/cave_stalagmite_wall_bottom_static_part_2.w2mesh` |
| Meshes | `environment/terrain_surroundings/common/cave/cave_big/cave_stalagmite_group_static_part_salt.w2mesh` |
| Meshes | `environment/textures_tileable/decals/decal_plane.w2mesh` |
| Meshes | `fx/mesh_particle/water/water_ring.w2mesh` |
| Meshes | `qa/javier/meshes/proxy_blocker_box.w2mesh` |
| Meshes | `qa/javier/meshes/proxy_blocker_cylinder.w2mesh` |
| Meshes | `qa/javier/meshes/proxy_blocker_pyramid.w2mesh` |
| Decals | `environment/textures_tileable/decals/blood_splash_vbig_m.xbm` |
| Decals | `environment/textures_tileable/decals/plain_wood_destroyed_m.xbm` |

## Credit and licence

EAB's data is used under its Apache-2.0 licence and credited in the Workbench's third-party notices, its EAB window and every place its tags show ("EAB category", "EAB tags"). None of EAB's data ships with the Workbench; it's read from the repository on the user's machine.

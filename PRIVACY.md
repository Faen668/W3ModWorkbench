# Privacy notice

W3 Mod Workbench is a free, open-source tool that runs on your own computer. Its author doesn't collect, receive or store any information about you or how you use it. There is no account, no analytics, no crash reporting and no tracking of any kind.

## The tool works offline

Everything the tool does with your game, your mods and your projects happens on your computer. It reads and writes files only where you point it.

It goes online in one case only, and only after you have turned it on: **EasyAssetBrowser tags** in the Asset Browser.

## EasyAssetBrowser tags (off unless you turn them on)

EasyAssetBrowser is a separate fan project by Vincent.02 (https://dev-cube.net/eab/). Its categories and tags for the game's assets are kept in its GitHub repository (https://github.com/vincocrafter/EasyAssetBrowser). It isn't part of W3 Mod Workbench and isn't made by CD PROJEKT RED.

When you turn EasyAssetBrowser tags on, the tool asks you first and says what follows. Then:

- **What it downloads.** EasyAssetBrowser's lists (about 12 MB of JSON files) from `raw.githubusercontent.com`, which is GitHub's. It does this when the Asset Browser opens, at most once a day, and when you choose **Read the tags again from GitHub**.
- **What GitHub sees.** As with any download, GitHub's servers see your IP address and that a program asked for those files. GitHub's own privacy statement covers that: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement. Neither W3 Mod Workbench's author nor EasyAssetBrowser's author receives it.
- **What it doesn't send.** Nothing about you, your computer, your files, your mods or what you do in the tool. The requests are plain downloads of public files, with no identifier of you or of the tool's copy.
- **What it keeps.** The lists, in `%LocalAppData%\W3ModWorkbench\EasyAssetBrowser`, so the tags work offline too. Turning EasyAssetBrowser off can delete them; you can also delete that folder yourself.
- **Your choice.** It's off until you turn it on, and you can turn it off at any time from the ⓘ button in the Asset Browser. While it's off, the tool doesn't go online at all.

### Suggesting tags

**Suggest on GitHub…** in the EasyAssetBrowser window only opens your web browser at a new issue on EasyAssetBrowser's GitHub repository, filled in with the tags you suggested. Nothing is sent by the tool. If you post the issue, you post it yourself, under your own GitHub account, and it is public, as everything on that repository is.

## Links that open files in the tool

If you turn on **Open files from EasyAssetBrowser's site here**, the tool registers a `w3modworkbench://` link type with Windows, for your Windows account only. A link like that only shows a game file in the Asset Browser, and the tool never goes online for it. Turning the option off removes the link type.

## Settings

The tool's settings (folders, recent files, bookmarks, choices you made) are kept on your computer, in `%AppData%\W3ModWorkbench\settings.json`. They're never sent anywhere.

## Other programs

When you ask it to, the tool runs other programs on your computer, such as REDkit's `wcc_lite`, Script Merger or your text editor. What those do is up to them.

## Changes and questions

If the tool ever does anything new online, this notice will say so before it does, and it will be off until you turn it on. Questions: open an issue on the tool's GitHub repository.

---
title: Updating the app
icon: E895
order: 3
summary: Check for a new version, see what's new, and install it from inside the app.
keywords: update upgrade new version download install release github latest restart skip
windows: UpdateWindow
---
The app can check for a newer version, show you what's new in it, and install it for you. It replaces its own files and starts again, so you don't need to download or unpack anything yourself.

## Checking for updates

Click the **ⓘ** button at the top right of the main window, then **Check for updates**. **Settings > General > Updates > Check now…** does the same.

The Updates window asks GitHub for the latest release and says whether you have it. Either way, it shows that release's notes.

![The Updates window with a new version ready to install](updates.jpg)

## Installing a new version

1. Save your work. The app won't update while anything has changes that aren't saved (a script, an XML or JSON file, strings, materials, item edits) or while something is still running (a build, an extraction, voice lines being packed). It lists what's holding it up; save or close those, then try again.
2. Click **Download and install**. The bar shows how far the download has got; **Cancel** stops it.
3. When it's done, the new version is put in place of the old one, in the same folder.
4. Click **Restart now** to close the app and start the new version.

The new version opens again what you had open: your project and its scripts, the bundles and folders in the file list, the files in tabs (the one in front stays in front), the viewers and editors open on a file, and the windows from **Tools**. Anything no longer there is skipped.

**Restart later** keeps this copy running as it is. The next time you start the app, it's the new version, and it starts as it usually does.

After the update, the app says once which version it was updated from. **What's new** there opens the Updates window with the notes.

> note: The download is checked against the one GitHub lists for the release. A file that doesn't match isn't used.

## Checking when the app starts

Tick **Check for updates when the app starts**, in the Updates window or in **Settings > General > Updates**. The app then asks GitHub at most once a day, and an **Update to …** button shows at the top right of the main window when there's a newer version. Click it to see what's new and install it.

It's off until you turn it on. Checking by hand works either way.

**Skip this version** stops the start-up check pointing out that version. A later one shows as usual, and checking by hand still shows the skipped one.

## When the app can't update itself

Sometimes the Updates window says the app can't update itself, and offers **Download** instead:

- **The app's folder can't be written to**, for example under `Program Files`. Move the app to a folder of your own (such as `Documents\W3 Mod Workbench`) and it can update itself from then on.
- **It's running from a build of the source code**, not the released app.

**Download** gets the zip; **Show the zip** opens its folder. Close the app and unpack the zip over the app's folder.

> tip: **Release page** opens the release on GitHub in your web browser, if you'd rather download it from there.

## What goes online

Checking only asks GitHub for the latest release, and downloading gets the release's zip from GitHub. Nothing about you, your files or your mods is sent. GitHub sees your IP address, as with any download. The privacy notice under the **ⓘ** button says more.

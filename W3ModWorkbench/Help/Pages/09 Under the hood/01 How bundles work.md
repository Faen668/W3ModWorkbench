---
title: How bundles work
icon: E7B8
order: 1
summary: What's inside a .bundle file, and why files are packed the way they are.
keywords: bundle format potato70 compression zlib lz4 snappy version
---
Every bundle starts with the text `POTATO70`, then a small header and a table of contents. Each entry in the table records a file's path, where its data sits in the bundle, its real size, its packed size, a checksum and how it's compressed.

## Two layouts

- **Version 5**: the game since the next-gen update, and REDkit. Entries are 304 bytes, with 64-bit offsets, because a few of the game's world bundles are bigger than 4 GB.
- **Version 3**: older community tools. Entries are 320 bytes, with 32-bit offsets.

## Compression

Each file is compressed on its own, so taking one out never means unpacking the rest. The game's files are stored as they are, with zlib, or with LZ4 HC; Snappy is read too, in case a mod uses it.

## Why "Packed" can equal "Size"

Formats that are compressed already, such as textures and sounds, don't shrink, so they're stored as they are.

## Beside the bundles

- `metadata.store` is an index of every bundle in a folder, which the game reads at start.
- `buffers0.bundle` holds meshes' and animations' `.buffer` files, beside `blob0.bundle`.
- `texture.cache` holds textures' pixels, `soundspc.cache` sounds, and `collision.cache` collision shapes.

When you build a mod, the app writes all of these itself, the same way REDkit does. See [[Building and installing]].

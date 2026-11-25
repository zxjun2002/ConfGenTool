#!/usr/bin/env python3
"""
Generate deterministic Unity .meta files for a UPM package directory.
All .dll files are configured as Editor-only plugins so they never enter player builds.
"""

import hashlib
import os
import sys


def make_guid(rel_path: str) -> str:
    normalized = rel_path.replace("\\", "/").lstrip("./")
    return hashlib.md5(f"com.zxjun2002.confgen:{normalized}".encode("utf-8")).hexdigest()


def folder_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def script_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "MonoImporter:\n"
        "  externalObjects: {}\n"
        "  serializedVersion: 2\n"
        "  defaultReferences: []\n"
        "  executionOrder: 0\n"
        "  icon: {instanceID: 0}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def asmdef_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "AssemblyDefinitionImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def editor_plugin_dll_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "PluginImporter:\n"
        "  externalObjects: {}\n"
        "  serializedVersion: 2\n"
        "  iconMap: {}\n"
        "  executionOrder: {}\n"
        "  defineConstraints: []\n"
        "  isPreloaded: 0\n"
        "  isOverridable: 0\n"
        "  isExplicitlyReferenced: 0\n"
        "  validateReferences: 1\n"
        "  platformData:\n"
        "  - first:\n"
        "      Any: \n"
        "    second:\n"
        "      enabled: 0\n"
        "      settings: {}\n"
        "  - first:\n"
        "      Editor: Editor\n"
        "    second:\n"
        "      enabled: 1\n"
        "      settings:\n"
        "        DefaultValueInitialized: true\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def default_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def generate_metas(root_dir: str) -> int:
    count = 0
    for dirpath, dirnames, filenames in os.walk(root_dir):
        dirnames[:] = sorted(d for d in dirnames if not d.startswith("."))
        filenames = sorted(f for f in filenames if not f.endswith(".meta") and not f.startswith("."))

        for d in dirnames:
            full = os.path.join(dirpath, d)
            meta_path = full + ".meta"
            if not os.path.exists(meta_path):
                rel = os.path.relpath(full, root_dir)
                with open(meta_path, "w", encoding="utf-8", newline="\n") as fp:
                    fp.write(folder_meta(make_guid(rel)))
                count += 1

        for f in filenames:
            full = os.path.join(dirpath, f)
            meta_path = full + ".meta"
            if not os.path.exists(meta_path):
                rel = os.path.relpath(full, root_dir)
                guid = make_guid(rel)
                ext = os.path.splitext(f)[1].lower()
                if ext == ".cs":
                    content = script_meta(guid)
                elif ext == ".asmdef":
                    content = asmdef_meta(guid)
                elif ext == ".dll":
                    content = editor_plugin_dll_meta(guid)
                else:
                    content = default_meta(guid)
                with open(meta_path, "w", encoding="utf-8", newline="\n") as fp:
                    fp.write(content)
                count += 1

    return count


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: generate-upm-meta.py <upm_dir>", file=sys.stderr)
        return 2
    root = sys.argv[1]
    if not os.path.isdir(root):
        print(f"Directory not found: {root}", file=sys.stderr)
        return 1
    created = generate_metas(root)
    print(f"Generated {created} deterministic .meta file(s) in {root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

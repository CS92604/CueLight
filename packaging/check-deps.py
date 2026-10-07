"""Fails if a published build depends on a DLL that a clean Windows 10/11 PC doesn't have.

Every native .dll/.exe under the folder is read for its import table (including delay-loaded
imports). Each imported DLL must be

  * shipped in the app folder (or next to the importing file), or
  * a Windows in-box DLL: an API-set name (api-ms-win-*, ext-ms-win-*), or a file in the Windows
    system folder that isn't one of the Visual C++ redistributable DLLs (those exist on a dev
    machine or a CI runner, but not necessarily on a customer's PC).

Usage: python check-deps.py <app-folder> [--system-dir <folder>]
"""
import os
import struct
import sys

# Present on the machines this runs on, but not guaranteed on a clean PC: ship them with the app.
REDISTRIBUTABLE = ("msvcp", "vcruntime", "vcomp", "concrt", "vccorlib", "mfc", "msvcr", "vcamp", "vcruntime140_cor3")


def imports(path):
    """Names of the DLLs a PE file imports, lowercase. Returns [] for managed-only or odd files."""
    with open(path, "rb") as f:
        d = f.read()
    if d[:2] != b"MZ":
        return []
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    if d[pe:pe + 4] != b"PE\0\0":
        return []
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    optsz = struct.unpack_from("<H", d, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", d, opt)[0]
    dirs = opt + (112 if magic == 0x20B else 96)
    first_section = opt + optsz
    sections = [struct.unpack_from("<8sIIII", d, first_section + i * 40)[1:] for i in range(nsec)]

    def offset(rva):
        for vsize, va, rsize, raw in sections:
            if va <= rva < va + max(vsize, rsize):
                return rva - va + raw
        return None

    names = []
    for table, entry in ((1, 20), (13, 32)):  # import table, delay-load import table
        rva, _ = struct.unpack_from("<II", d, dirs + table * 8)
        o = offset(rva) if rva else None
        while o is not None:
            values = struct.unpack_from("<5I" if table == 1 else "<8I", d, o)
            name_rva = values[3] if table == 1 else values[1]
            if name_rva == 0:
                break
            n = offset(name_rva)
            names.append(d[n:d.index(b"\0", n)].decode("ascii", "replace").lower())
            o += entry
    return names


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 2
    folder = args[0]
    system_dir = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "System32")
    if "--system-dir" in args:
        system_dir = args[args.index("--system-dir") + 1]

    shipped = {}
    for root, _, files in os.walk(folder):
        for name in files:
            shipped.setdefault(name.lower(), []).append(root)
    app_dir_names = {n for n, roots in shipped.items() if os.path.normpath(folder) in map(os.path.normpath, roots)}

    system = {n.lower() for n in os.listdir(system_dir)} if os.path.isdir(system_dir) else set()

    missing = {}
    checked = 0
    for root, _, files in os.walk(folder):
        for name in files:
            if not name.lower().endswith((".dll", ".exe")):
                continue
            try:
                deps = imports(os.path.join(root, name))
            except Exception as ex:  # unreadable file: report it rather than skip it silently
                print(f"warning: could not read {os.path.join(root, name)}: {ex}")
                continue
            checked += 1
            here = {n.lower() for n in os.listdir(root)}
            for dep in deps:
                if dep in app_dir_names or dep in here:
                    continue
                if dep.startswith(("api-ms-win-", "ext-ms-win-")):
                    continue
                if dep in system and not dep.startswith(REDISTRIBUTABLE):
                    continue
                missing.setdefault(dep, set()).add(os.path.relpath(os.path.join(root, name), folder))

    print(f"Checked {checked} native files in {folder}")
    if missing:
        print("These DLLs are needed but would not exist on a clean Windows PC:")
        for dep in sorted(missing):
            print(f"  {dep}  <- {', '.join(sorted(missing[dep])[:3])}")
        return 1
    print("OK: everything the app loads ships with it or comes with Windows.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

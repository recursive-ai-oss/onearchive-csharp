"""Reads every entry of each zip given, checking CRCs, with Python's zipfile (run as a black box by the raw-copy interop test).

usage: check.py PASSWORD FILE...
Prints one line per file: name, entries read, entries skipped (methods zipfile cannot decode, listed), and OK or the failure.
Exit status 1 if any file fails.
"""
import sys
import zipfile

SUPPORTED = {0, 8, 12, 14}


def check(path, password):
    read = 0
    skipped = {}
    with zipfile.ZipFile(path) as zf:
        infos = zf.infolist()
        for info in infos:
            if info.compress_type not in SUPPORTED or (info.flag_bits & 0x40):
                skipped[info.compress_type] = skipped.get(info.compress_type, 0) + 1
                continue
            data = zf.read(info, pwd=password.encode())  # checks the CRC-32 at the end of the data
            if len(data) != info.file_size:
                raise ValueError(f"{info.filename}: read {len(data)} bytes, header says {info.file_size}")
            read += 1
        if not skipped:
            bad = zf.testzip() if not any(i.flag_bits & 1 for i in infos) else None
            if bad:
                raise ValueError(f"testzip: {bad}")
    return read, skipped


def main():
    password = sys.argv[1]
    failed = False
    for path in sys.argv[2:]:
        try:
            read, skipped = check(path, password)
            print(f"{path}\t{read}\t{dict(sorted(skipped.items()))}\tOK")
        except Exception as ex:  # report every file
            failed = True
            print(f"{path}\t-\t-\tFAIL {type(ex).__name__}: {ex}")
    return 1 if failed else 0


sys.exit(main())

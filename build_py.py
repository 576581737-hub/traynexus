#!/usr/bin/env python
# -*- coding: utf-8 -*-
import subprocess
import sys

PROJ = r"F:\desktop\Projects\traynexus"
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
RSP = r"F:\desktop\Projects\traynexus\build.rsp"

def main():
    print("CWD:", PROJ)
    print("CSC:", CSC)
    print("RSP:", RSP)
    try:
        proc = subprocess.run(
            [CSC, "@" + RSP],
            cwd=PROJ,
            capture_output=True,
            text=True,
            errors='replace',
            timeout=300,
        )
    except Exception as e:
        print("LAUNCH_ERROR:", repr(e))
        return 2
    print("RETURN_CODE:", proc.returncode)
    print("---- STDOUT ----")
    print(proc.stdout or "(empty)")
    print("---- STDERR ----")
    print(proc.stderr or "(empty)")
    return proc.returncode

if __name__ == "__main__":
    sys.exit(main())

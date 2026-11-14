#!/usr/bin/env python3
"""Builds Stubs/DiagnosticDumpStub.cs: a stand-in for DiagnosticDump.cs that contains the REAL text of the helpers
other files use (FindDesignerAircraft, CraftNameOf), so they are compiled too. Run it before `dotnet build`."""
import pathlib

here = pathlib.Path(__file__).resolve().parent
source = (here / ".." / ".." / "SP2 Texture Mod" / "DiagnosticDump.cs").read_text(encoding="utf-8")
start = source.index("        /// <summary>The aircraft open in the designer")
end = source.index("        // ------------------------------------------------------------------ aircraft")
helpers = source[start:end]
text = '''using System;
using Assets.Scripts.Craft;
using UnityEngine;

namespace SP2TextureMod
{
    internal static class DiagnosticDump
    {
        private const string Tag = "[DIAG] ";

        public static void Dump() { }
        internal static AircraftScript FindAircraft(out string context) { context = "none"; return null; }
        internal static string MeshSummary(Mesh mesh) { return ""; }

''' + helpers + '''    }
}
'''
(here / "Stubs" / "DiagnosticDumpStub.cs").write_text(text, encoding="utf-8")
print("Stubs/DiagnosticDumpStub.cs written (%d characters of real helper code)" % len(helpers))

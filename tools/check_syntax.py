#!/usr/bin/env python3
"""Syntax check for the C# sources without compiling (no .NET SDK or game DLLs needed).

It parses every .cs file with tree-sitter and reports invalid structure (braces,
parentheses, semicolons...). It does NOT catch type errors or names that do not exist
in the game: those need a careful re-read (see CREATE_SP2_MOD.md, section 9).

Setup (once):
    python3 -m venv .venv && .venv/bin/pip install tree-sitter tree-sitter-c-sharp
Run:
    .venv/bin/python -I tools/check_syntax.py "SP2 Texture Mod"
"""
import sys
from pathlib import Path

import tree_sitter_c_sharp
from tree_sitter import Language, Parser


def find_errors(root):
    """Yield every ERROR or MISSING node, using an explicit stack (no recursion limit)."""
    stack = [root]
    while stack:
        node = stack.pop()
        if node.type == "ERROR" or node.is_missing:
            yield node
            continue  # children of an error node are noise
        if node.has_error:
            stack.extend(reversed(node.children))


def main(argv):
    target = Path(argv[1] if len(argv) > 1 else ".")
    parser = Parser(Language(tree_sitter_c_sharp.language()))
    files = sorted(p for p in target.rglob("*.cs") if "obj" not in p.parts and "bin" not in p.parts)
    if not files:
        print(f"no .cs files under {target}")
        return 1

    failed = 0
    for path in files:
        source = path.read_bytes()
        tree = parser.parse(source)
        errors = list(find_errors(tree.root_node))
        if not errors:
            print(f"OK    {path}")
            continue
        failed += 1
        print(f"FAIL  {path}")
        lines = source.decode("utf-8", "replace").splitlines()
        for node in errors[:10]:
            row, col = node.start_point
            what = f"missing '{node.type}'" if node.is_missing else "syntax error"
            text = lines[row].strip() if row < len(lines) else ""
            print(f"      line {row + 1}, col {col + 1}: {what}: {text}")
    print(f"\n{len(files) - failed}/{len(files)} files OK")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

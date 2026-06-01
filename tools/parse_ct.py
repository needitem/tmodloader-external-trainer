"""Parse the Cheat Engine table and emit structured JSON + a complexity report.

Outputs:
  tools/ct_entries.json  - all value entries (4 Bytes / Float / etc.) with address + offsets
  tools/ct_scripts.json  - all auto-assembler scripts with parsed [ENABLE]/[DISABLE]
  prints a categorization report for the AA scripts
"""
import json
import re
import sys
import xml.etree.ElementTree as ET

CT = "Terraria 1.4.5.5 Table Ver 2.CT"


def text(el, tag, default=None):
    c = el.find(tag)
    return c.text if c is not None and c.text is not None else default


def parse_value_entry(el):
    vt = text(el, "VariableType")
    addr = text(el, "Address")
    desc = text(el, "Description", "")
    offsets = []
    offs_el = el.find("Offsets")
    if offs_el is not None:
        offsets = [o.text for o in offs_el.findall("Offset")]
    return {
        "id": text(el, "ID"),
        "desc": desc.strip('"') if desc else "",
        "type": vt,
        "address": addr,
        "offsets": offsets,
        "length": text(el, "Length"),
        "unicode": text(el, "Unicode"),
    }


def categorize_script(script):
    """Classify an AA script by how hard it is to port externally."""
    enable = ""
    m = re.search(r"\[ENABLE\](.*?)(\[DISABLE\]|$)", script, re.S)
    if m:
        enable = m.group(1)
    has_alloc = "alloc(" in enable
    has_newmem = "newmem" in enable.lower()
    has_label = "label(" in enable
    has_regsym = "registersymbol(" in enable
    # count assembly mnemonic-ish lines in newmem region
    mnemonic_lines = 0
    for line in enable.splitlines():
        s = line.strip()
        if not s or s.startswith("//") or s.startswith("{") or ":" in s.split()[0:1][0:1] if False else False:
            pass
    # simpler: detect db-only patch
    only_db = (not has_alloc) and bool(re.search(r"\bdb\b", enable))
    if not has_alloc and not has_newmem:
        cat = "simple_patch"  # just db/byte overwrite, easy
    elif has_alloc and has_label:
        cat = "code_cave"     # needs assembler + relocation, hard
    elif has_alloc:
        cat = "alloc_simple"
    else:
        cat = "other"
    return {
        "category": cat,
        "has_alloc": has_alloc,
        "has_label": has_label,
        "has_regsym": has_regsym,
    }


def walk(el, value_entries, scripts):
    for entry in el.findall("CheatEntry"):
        vt = text(entry, "VariableType")
        desc = (text(entry, "Description", "") or "").strip('"')
        if vt == "Auto Assembler Script":
            script = text(entry, "AssemblerScript", "") or ""
            info = categorize_script(script)
            scripts.append({
                "id": text(entry, "ID"),
                "desc": desc,
                **info,
                "script": script,
            })
        elif vt in ("4 Bytes", "Float", "Byte", "Double", "String", "2 Bytes", "8 Bytes"):
            ve = parse_value_entry(entry)
            value_entries.append(ve)
        # recurse into nested entries
        nested = entry.find("CheatEntries")
        if nested is not None:
            walk(nested, value_entries, scripts)


def main():
    tree = ET.parse(CT)
    root = tree.getroot()
    entries_root = root.find("CheatEntries")
    value_entries, scripts = [], []
    walk(entries_root, value_entries, scripts)

    with open("tools/ct_entries.json", "w", encoding="utf-8") as f:
        json.dump(value_entries, f, ensure_ascii=False, indent=2)
    with open("tools/ct_scripts.json", "w", encoding="utf-8") as f:
        json.dump(scripts, f, ensure_ascii=False, indent=2)

    print(f"Value entries: {len(value_entries)}")
    print(f"AA scripts:    {len(scripts)}")
    print("\n=== AA script categories ===")
    from collections import Counter
    cats = Counter(s["category"] for s in scripts)
    for c, n in cats.most_common():
        print(f"  {c:16} {n}")

    print("\n=== Address pattern distribution (value entries) ===")
    pats = Counter()
    for v in value_entries:
        a = v["address"] or ""
        if a.startswith("[myPlayer]"):
            pats["[myPlayer]+off"] += 1
        elif "myPlayer" in a:
            pats["myPlayer-other"] += 1
        else:
            pats[a[:20]] += 1
    for p, n in pats.most_common(15):
        print(f"  {p:24} {n}")

    print("\n=== Sample simple_patch scripts (first 3) ===")
    for s in [x for x in scripts if x["category"] == "simple_patch"][:3]:
        print(f"--- {s['desc']} ---")
        print(s["script"][:400])
        print()


if __name__ == "__main__":
    main()

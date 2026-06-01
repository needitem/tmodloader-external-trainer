"""Generate app-ready data files for the trainer from the CT.

Emits:
  src/Data/values.json   - value entries grouped, with parsed base + int offsets
  src/Data/scripts.json  - AA scripts (lua / asm) with category
"""
import json
import os
import re
import xml.etree.ElementTree as ET

CT = "Terraria 1.4.5.5 Table Ver 2.CT"
OUT = "src/Data"

VALUE_TYPES = {"4 Bytes", "Float", "Byte", "Double", "String", "2 Bytes", "8 Bytes"}


def text(el, tag, default=None):
    c = el.find(tag)
    return c.text if c is not None and c.text is not None else default


def parse_base(addr):
    """Return (symbol, const_offset_hex_int, deref_base).

    "[myPlayer]+42C" -> ('myPlayer', 0x42C, True)
    "myPlayer"       -> ('myPlayer', 0, False)
    "[myPlayer]"     -> ('myPlayer', 0, True)
    Other (raw hex / mono symbol / script symbol) -> (addr, 0, None)  # unsupported by generic editor
    """
    addr = (addr or "").strip()
    m = re.match(r"^\[([A-Za-z_]\w*)\](?:\+([0-9A-Fa-f]+))?$", addr)
    if m:
        return m.group(1), int(m.group(2), 16) if m.group(2) else 0, True
    m = re.match(r"^([A-Za-z_]\w*)$", addr)
    if m and m.group(1) == "myPlayer":
        return m.group(1), 0, False
    return addr, 0, None  # unsupported / script-specific


def categorize_script(script):
    if "{$lua}" in script:
        return "lua"
    if "alloc(" in script:
        return "asm"
    if re.search(r"\bdb\b", script):
        return "patch"
    return "other"


def walk(el, out_values, out_scripts, group_stack):
    for entry in el.findall("CheatEntry"):
        vt = text(entry, "VariableType")
        desc = (text(entry, "Description", "") or "").strip('"')
        nested = entry.find("CheatEntries")

        if vt == "Auto Assembler Script":
            script = text(entry, "AssemblerScript", "") or ""
            out_scripts.append({
                "id": text(entry, "ID"),
                "desc": desc,
                "category": categorize_script(script),
                "script": script,
            })
        elif vt in VALUE_TYPES:
            addr = text(entry, "Address")
            sym, const, deref = parse_base(addr)
            offs_el = entry.find("Offsets")
            offsets = [int(o.text, 16) for o in offs_el.findall("Offset")] if offs_el is not None else []
            out_values.append({
                "id": text(entry, "ID"),
                "desc": desc,
                "type": vt,
                "group": group_stack[-1] if group_stack else "",
                "symbol": sym,
                "baseConst": const,
                "derefBase": deref,
                "offsets": offsets,           # applied last-to-first per CE semantics
                "supported": deref is not None,
                "length": int(text(entry, "Length") or 0),
            })

        # Decide group label for children: a separator/header entry (no value type,
        # has children) becomes the group name.
        if nested is not None:
            is_group_header = vt not in VALUE_TYPES and vt != "Auto Assembler Script"
            child_group = clean_group(desc) if (is_group_header and desc) else (group_stack[-1] if group_stack else "")
            walk(nested, out_values, out_scripts, group_stack + [child_group])


def clean_group(desc):
    # Strip decorative separators/emoji-only labels.
    d = desc.strip("═ ").strip()
    return d or "General"


def main():
    os.makedirs(OUT, exist_ok=True)
    tree = ET.parse(CT)
    root = tree.getroot()
    values, scripts = [], []
    walk(root.find("CheatEntries"), values, scripts, [])

    with open(os.path.join(OUT, "values.json"), "w", encoding="utf-8") as f:
        json.dump(values, f, ensure_ascii=False, indent=1)
    with open(os.path.join(OUT, "scripts.json"), "w", encoding="utf-8") as f:
        json.dump(scripts, f, ensure_ascii=False, indent=1)

    supported = sum(1 for v in values if v["supported"])
    print(f"values.json: {len(values)} entries ({supported} supported, {len(values)-supported} script-specific)")
    print(f"scripts.json: {len(scripts)} scripts")
    from collections import Counter
    print("groups:", dict(Counter(v["group"] for v in values)))
    print("script cats:", dict(Counter(s["category"] for s in scripts)))


if __name__ == "__main__":
    main()

"""Extract buff-toggle cheats from the Lua scripts into Data/buffs.json.

Each Lua buff script follows one of two shapes operating on the player's
buff arrays  aID = [player+0xC8] (types)  /  aTm = [player+0xCC] (times),
slots 0..43 at  array + 8 + i*4 :

  apply: keep buff N present (refresh time, else fill empty slot)
  clear: keep buff N absent (zero matching slots)

We read the buff id + duration straight from the script text so the data
can't drift from the CT.
"""
import json
import re

s = json.load(open("src/Data/scripts.json", encoding="utf-8"))
lua = [x for x in s if x["category"] == "lua"]

buffs = []
unmatched = []

for x in lua:
    sc = x["script"]
    # Split ENABLE section.
    m = re.search(r"\[ENABLE\](.*?)\[DISABLE\]", sc, re.S)
    enable = m.group(1) if m else sc

    # apply: writeInteger(aID+8+i*4, N) with N>0
    apply = re.search(r"writeInteger\(aID\+8\+i\*4,\s*(\d+)\)", enable)
    apply_nonzero = [int(g) for g in re.findall(r"writeInteger\(aID\+8\+i\*4,\s*(\d+)\)", enable) if int(g) != 0]
    # duration
    dur_m = re.search(r"writeInteger\(aTm\+8\+i\*4,\s*(\d+)\)", enable)
    duration = int(dur_m.group(1)) if dur_m else 219600

    is_maxstack = "maxStack" in sc or "Max Stack" in x["desc"]
    is_journey = "Journey" in x["desc"] or "aobscanregion" in sc

    if is_maxstack:
        buffs.append({"id": x["id"], "name": x["desc"], "mode": "maxstack"})
        continue
    if is_journey:
        # handled as asm cheat; skip here
        unmatched.append(("journey", x["desc"]))
        continue

    if apply_nonzero:
        buffs.append({
            "id": x["id"], "name": x["desc"], "mode": "apply",
            "buff": apply_nonzero[0], "duration": duration,
        })
    else:
        # clear-type: id is in `== N` guarding a write of 0
        clr = re.search(r"==\s*(\d+)\s*then\s*writeInteger\(aID\+8\+i\*4,\s*0\)", enable.replace("\n", " "))
        if not clr:
            clr = re.search(r"==\s*(\d+)", enable)
        if clr:
            buffs.append({"id": x["id"], "name": x["desc"], "mode": "clear", "buff": int(clr.group(1))})
        else:
            unmatched.append(("?", x["desc"]))

json.dump(buffs, open("src/Data/buffs.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)

from collections import Counter
print("buffs.json:", len(buffs), "entries")
print("modes:", dict(Counter(b["mode"] for b in buffs)))
print("unmatched:", len(unmatched))
for u in unmatched:
    print("  ", u)

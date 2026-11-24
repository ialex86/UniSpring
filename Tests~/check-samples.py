#!/usr/bin/env python3
"""Fails when Samples~/PersistentShop no longer matches the code of Documentation~/getting-started.md."""
import io, os, re, sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
doc = io.open(os.path.join(root, "Documentation~", "getting-started.md"), encoding="utf-8").read()
blocks = re.findall(r"```(\w+)\n(.*?)```", doc, re.S)
code = [body for lang, body in blocks if lang == "csharp"]
data = [body for lang, body in blocks if lang == "json"]
header = "// Kept in sync with Documentation~/getting-started.md, which shows this code step by step.\n"
expected = {
    "Runtime/ShopModels.cs": header + code[0],
    "Runtime/ShopSetup.cs": header + code[1],
    "Runtime/ShopService.cs": header + code[2],
    "Runtime/ShopDemo.cs": header + code[3],
    "Tests/ShopTests.cs": header + code[4],
    "Resources/Shop/Items/sword.json": data[0],
    "Resources/Shop/Settings.json": data[1],
}
if len(code) != 5 or len(data) != 2:
    sys.exit("getting-started.md: expected 5 C# and 2 JSON blocks, found %d and %d" % (len(code), len(data)))
stale = []
for name, text in expected.items():
    path = os.path.join(root, "Samples~", "PersistentShop", name)
    if not os.path.exists(path) or io.open(path, encoding="utf-8").read() != text:
        stale.append(name)
if stale:
    sys.exit("Samples~/PersistentShop differs from getting-started.md: " + ", ".join(stale))
print("PersistentShop sample matches getting-started.md")

# -*- coding: utf-8 -*-
import io
P = r"E:\GameClicker\src\HotkeyPromptForm.cs"
with io.open(P, "r", encoding="utf-8", newline="") as f:
    lines = f.read().split("\n")
out = []
skip_until_brace = False
for l in lines:
    if "VkFromText" in l and "private static int" in l:
        # 删到方法结束
        skip_until_brace = True
        continue
    if skip_until_brace:
        if l.strip() == "}":
            skip_until_brace = False
        continue
    if "public void SetHotkey(string describe)" in l:
        # 删这个旧方法（到配平）
        depth = 0
        started = False
        i = len(out)  # 占位
        # 直接跳过后续行直到配平——用状态机处理
        out.append("// (removed old SetHotkey(string))")
        # 标记进入跳过
        globals_hint = True
        continue
    out.append(l)
with io.open(P, "w", encoding="utf-8", newline="") as f:
    f.write("\n".join(out))
print("ok")

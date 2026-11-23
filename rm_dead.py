# -*- coding: utf-8 -*-
"""删除 Esc 对话框保护机制的死代码（字段/方法/钩子分支）。"""
import io

P = r"E:\GameClicker\src\InputHook.cs"
with io.open(P, "r", encoding="utf-8", newline="") as f:
    lines = f.read().split("\n")

out = []
removed = 0
i = 0
while i < len(lines):
    l = lines[i]
    st = l.strip()
    # 字段、方法、测试访问器
    if (st == "private volatile bool _swallowEscForDialog;" or
        st.startswith("public void SetSwallowEscForDialog(") or
        st.startswith("internal bool IsSwallowEscForDialogForTest")):
        removed += 1
        # 若上一行是注释，也删
        if out and out[-1].strip().startswith("///"):
            out.pop()
            removed += 1
        # 若是方法，还要删方法体（下一行 { 到配平 }）
        if st.startswith("public void SetSwallowEscForDialog"):
            i += 1  # 跳过 { ... } 单行实现
        i += 1
        continue
    # 钩子分支（4 行：注释 2 行 + if + return）
    if st == "if (_swallowEscForDialog && vk == 0x1B)":
        # 吞掉这一行和下一行 return，以及上面的注释
        while out and out[-1].strip().startswith("//"):
            out.pop()
            removed += 1
        i += 2   # if 行 + return 行
        removed += 2
        continue
    out.append(l)
    i += 1

with io.open(P, "w", encoding="utf-8", newline="") as f:
    f.write("\n".join(out))
print("removed lines:", removed)

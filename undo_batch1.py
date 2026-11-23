# -*- coding: utf-8 -*-
"""撤销 batch1 的插入（删除其中独特的标记行之间的内容），重新按正确锚点插入。"""
import io

P = r"E:\GameClicker\src\MainForm.cs"
with io.open(P, "r", encoding="utf-8", newline="") as f:
    lines = f.read().split("\n")

# batch1 的开头标记与结尾：找 "        // =========================================================\n        //  事件接线"
start = None
for i, l in enumerate(lines):
    if "事件接线" in l and "==========" in lines[i - 1]:
        start = i - 1
        break
if start is None:
    raise SystemExit("batch1 start not found")

# batch1 结束：Cleanup 方法的开头 "private void Cleanup()" 之前的 "        }"
end = None
for i in range(start, len(lines)):
    if "private void Cleanup()" in lines[i]:
        # 往回找 Cleanup 的注释与空行
        e = i - 1
        while e > start and (lines[e].strip() == "" or lines[e].strip().startswith("//")):
            e -= 1
        end = e + 1  # 保留空行
        break
if end is None:
    raise SystemExit("batch1 end not found")

print("removing lines %d..%d (batch1)" % (start + 1, end))
del lines[start:end]

with io.open(P, "w", encoding="utf-8", newline="") as f:
    f.write("\n".join(lines))
print("undo done, total lines =", len(lines))

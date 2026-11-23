# -*- coding: utf-8 -*-
"""恢复 MainForm.cs 丢失的方法（1129 行后插入）。

丢失区间原内容（按对话历史逐轮重建）：
- OnPointChecked（带 try/catch 防护版）
- OnPointNameEdited / OnPointModeEdited / OnPointReturnEdited
- DeleteSelectedPoint / ClearSelectedPointHotkey / MoveSelectedPoint
- OnSequenceGapEdited / UpdatePointHint / CountPointsUsingKey
- Profile 区（ActiveProfileDir/RefreshProfileList/SyncProfileEditors/
  OnProfileSwitched/CreateProfile/RenameProfile/DeleteProfile/
  OnGameProcEdited/BrowseGameExe/CheckForegroundProfile/GetProcessNameFast/
  PromptInput）
- OnBatchPointCaptured / UndoLastBatchPoint / FinishBatchRecord / StartBatchRecord
- OnPointHotkey / ApplyRecordedPoint / FindKeyConflict / IsModifierKey
- BeginPointKeyCapture / EndPointKeyCapture / OnPointKeyCaptured
- ShowStatusBanner
- WireEvents / SaveAndNotify / ApplyLiveSettings / ApplyConfig /
  CollectConfig / OnConfigChanged...
- DebouncedSaveWindowSize / DebouncedAutoSave / SaveWindowSize
- StartClicking/StopClicking/ToggleClicking/RefreshLiveStatus/DescribeTrigger
- OnKeyCaptured/OnMouseCaptured/EndCapture
- StartBatchRecord 里引用的 _batchTip、_trayBatchItem 等
- OnFormClosing / Cleanup / OnFormClosed
- RunSelfTest / DumpControls / LayoutCheck / CheckTextFit / Trim
"""
import io

P = r"E:\GameClicker\src\MainForm.cs"
with io.open(P, "r", encoding="utf-8", newline="") as f:
    lines = f.read().split("\n")

# 插入点：命名处理器方法结束后（1129 行 0 起）
INS = 1129

# 先看插入点前后确认
print("insert context:")
for k in range(INS - 6, min(len(lines), INS + 4)):
    print("%5d: %s" % (k + 1, lines[k]))

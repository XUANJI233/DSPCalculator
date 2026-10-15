using DSPCalculator.Logic;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DSPCalculator.UI
{
    /// <summary>
    /// 计算器窗口跟随游戏存档保存与读取：每个存档对应一个“存档名.dspcalc”文件，只在游戏存档时写入
    /// </summary>
    public class WindowsPersistence
    {
        static readonly object sync = new object();
        static List<CalcWindowRecord> pendingRecords = new List<CalcWindowRecord>(); // 读档后等待恢复的窗口
        static bool resetRequested = false; // 读档或开新游戏后，需要在主线程销毁旧存档的窗口
        static int readyFrames = 0; // 游戏进入运行状态后经过的帧数，稍等几帧再开始恢复

        static SaveSlotScope saveScope;
        static int saveScopeDepth = 0;

        const int framesBeforeRestore = 10;

        /// <summary>
        /// 每帧由WindowsManager.OnUpdate调用
        /// </summary>
        public static void OnUpdate()
        {
            ProcessReset();

            if (!CanRestore())
            {
                readyFrames = 0;
                return;
            }
            if (readyFrames < framesBeforeRestore)
            {
                readyFrames++;
                return;
            }

            // 每帧恢复一个窗口，避免读档时卡顿
            RestoreNext();
        }

        /// <summary>
        /// 由WindowsManager.OpenOne调用：读档后窗口还没恢复完时玩家就打开了窗口，先把剩余的窗口全部恢复，
        /// 使“重新打开最后关闭的窗口”等行为与存档前一致，也避免新开的窗口被随后恢复的窗口压在下面
        /// </summary>
        public static void FinishPendingRestore()
        {
            ProcessReset();
            if (!CanRestore())
                return;
            while (RestoreNext()) { }
        }

        static void ProcessReset()
        {
            bool doReset;
            lock (sync)
            {
                doReset = resetRequested;
                resetRequested = false;
            }
            if (doReset)
                DestroyAllWindows();
        }

        /// <summary>
        /// 恢复一个待恢复的窗口，没有待恢复的窗口时返回false
        /// </summary>
        static bool RestoreNext()
        {
            CalcWindowRecord rec = null;
            lock (sync)
            {
                if (pendingRecords.Count > 0)
                {
                    rec = pendingRecords[0];
                    pendingRecords.RemoveAt(0);
                }
            }
            if (rec == null)
                return false;
            try
            {
                RestoreWindow(rec);
            }
            catch (Exception ex)
            {
                Utils.logger.LogError("Failed to restore calculator window: " + ex.ToString());
            }
            return true;
        }

        static bool CanRestore()
        {
            return GameMain.instance != null && GameMain.isRunning && !GameMain.isLoading && !DSPGame.IsMenuDemo && GameMain.data != null && CalcDB.inited
                && WindowsManager.windows != null && WindowsManager.closedWindows != null;
        }

        static void DestroyAllWindows()
        {
            if (WindowsManager.windows != null)
            {
                foreach (UICalcWindow window in WindowsManager.windows)
                {
                    if (window?.windowObj != null)
                        GameObject.Destroy(window.windowObj);
                }
                WindowsManager.windows.Clear();
            }
            if (WindowsManager.closedWindows != null)
            {
                foreach (UICalcWindow window in WindowsManager.closedWindows)
                {
                    if (window?.windowObj != null)
                        GameObject.Destroy(window.windowObj);
                }
                WindowsManager.closedWindows.Clear();
            }
            WindowsManager.temporaryCloseBecausePasteBp = false;
        }

        static void RestoreWindow(CalcWindowRecord rec)
        {
            UIPauseBarPatcher.Init();
            UICalcWindow window = new UICalcWindow(WindowsManager.windows.Count);
            if (!window.TryPaste(rec.data))
            {
                Utils.logger.LogWarning("Failed to restore calculator window: invalid data.");
                GameObject.Destroy(window.windowObj);
                return;
            }
            if (!string.IsNullOrEmpty(rec.title))
                window.titleInput.text = rec.title;
            window.isLargeWindow = rec.isLargeWindow;

            if (rec.state == CalcWindowRecord.STATE_CLOSED)
                window.CloseWindow();
            else
                WindowsManager.windows.Add(window);

            // 在本帧内完成求解和窗口尺寸动画。放在屏幕中间进行，避免小窗口在收缩过程中被误判为贴边；
            // 暂时移到最底层，使其不是isTopAndActive，不响应快捷键也不逐帧预处理蓝图
            Vector3 pos = window.windowObj.transform.localPosition;
            window.windowObj.transform.localPosition = new Vector3(-UICalcWindow.smallWindowWidth / 2, pos.y, pos.z);
            window.windowObj.transform.SetAsFirstSibling();
            float targetWidth = rec.isLargeWindow ? UICalcWindow.largeWindowWidth : UICalcWindow.smallWindowWidth;
            for (int i = 0; i < 30 && window.windowObj != null; i++)
            {
                window.OnUpdate();
                if (window.windowObj != null && !window.nextFrameRecalc && Mathf.Abs(window.windowObj.GetComponent<RectTransform>().sizeDelta.x - targetWidth) < 0.01f)
                    break;
            }
            if (window.windowObj == null)
                return;

            float halfX = WindowsManager.UIResolutionX / 2;
            float halfY = WindowsManager.UIResolutionY / 2;
            float x = rec.x;
            float y = Mathf.Clamp(rec.y, -halfY + 40, halfY);
            if (rec.state == CalcWindowRecord.STATE_DOCK_LEFT && !rec.isLargeWindow)
                x = -halfX - targetWidth + WindowsManager.windowEdgeDockingWidth; // 按当前分辨率重新贴边
            else if (rec.state == CalcWindowRecord.STATE_DOCK_RIGHT && !rec.isLargeWindow)
                x = halfX - WindowsManager.windowEdgeDockingWidth;
            else
                x = Mathf.Clamp(x, -halfX - targetWidth + 100, halfX - 100); // 分辨率变小时，保证窗口还能被拖回来
            window.windowObj.transform.localPosition = new Vector3(x, y, pos.z);
            if (rec.state == CalcWindowRecord.STATE_OPEN)
                window.windowObj.transform.SetAsLastSibling();
        }

        static List<CalcWindowRecord> CaptureRecords()
        {
            List<CalcWindowRecord> records = new List<CalcWindowRecord>();
            string defaultTitle = "量化计算器".Translate();

            List<UICalcWindow> closed = WindowsManager.closedWindows ?? new List<UICalcWindow>();
            int maxClosed = DSPCalculatorPlugin.MaxWindowsReservedAfterClose.Value;
            int closedStart = maxClosed > 0 && closed.Count > maxClosed ? closed.Count - maxClosed : 0;
            for (int i = closedStart; i < closed.Count; i++)
            {
                CalcWindowRecord rec = CaptureWindow(closed[i], defaultTitle);
                if (rec != null)
                {
                    rec.state = CalcWindowRecord.STATE_CLOSED;
                    rec.closedOrder = i - closedStart;
                    records.Add(rec);
                }
            }

            // 按显示层级排序，读档时最上层的窗口最后创建，仍在最上层
            List<UICalcWindow> opened = (WindowsManager.windows ?? new List<UICalcWindow>())
                .Where(w => w?.windowObj != null)
                .OrderBy(w => w.windowObj.transform.GetSiblingIndex())
                .ToList();
            foreach (UICalcWindow window in opened)
            {
                CalcWindowRecord rec = CaptureWindow(window, defaultTitle);
                if (rec != null)
                {
                    if (window.WindowOnLeftEdge())
                        rec.state = CalcWindowRecord.STATE_DOCK_LEFT;
                    else if (window.WindowOnRightEdge())
                        rec.state = CalcWindowRecord.STATE_DOCK_RIGHT;
                    else
                        rec.state = CalcWindowRecord.STATE_OPEN;
                    records.Add(rec);
                }
            }

            // 还没来得及恢复的窗口也要保存，否则读档后立刻存档会丢失它们
            lock (sync)
            {
                records.AddRange(pendingRecords);
            }
            return records;
        }

        static CalcWindowRecord CaptureWindow(UICalcWindow window, string defaultTitle)
        {
            if (window?.windowObj == null)
                return null;
            string data = window.Copy();
            if (string.IsNullOrEmpty(data))
                return null;
            CalcWindowRecord rec = new CalcWindowRecord();
            Vector3 pos = window.windowObj.transform.localPosition;
            rec.x = pos.x;
            rec.y = pos.y;
            rec.isLargeWindow = window.isLargeWindow;
            string title = window.titleInput != null ? window.titleInput.text : "";
            rec.title = title == defaultTitle ? "" : (title ?? "");
            rec.data = data;
            return rec;
        }

        static byte[] CaptureCurrentData()
        {
            try
            {
                List<CalcWindowRecord> records = CaptureRecords();
                if (records.Count == 0)
                    return null;
                return CalcSaveFile.Serialize(records);
            }
            catch (Exception ex)
            {
                Utils.logger.LogError("Failed to save calculator windows: " + ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// 与GameSave.SaveCurrentGame/LoadCurrentGame中处理存档名的方式一致，得到存档文件实际使用的名字
        /// </summary>
        static string NormalizeSaveName(string saveName)
        {
            if (string.IsNullOrEmpty(saveName))
                return saveName;
            return GameSave.AllowRecursive ? CommonUtils.ValidPathedFileName(saveName) : CommonUtils.ValidFileName(saveName);
        }

        static void BeginSaveScope(params string[] saveNames)
        {
            saveScopeDepth++;
            if (saveScopeDepth > 1)
                return; // 嵌套调用（例如自动存档内部调用SaveCurrentGame），由最外层统一处理
            try
            {
                saveScope = new SaveSlotScope(GameConfig.gameSaveFolder, GameSave.saveExt, saveNames.Select(NormalizeSaveName));
            }
            catch (Exception ex)
            {
                saveScope = null;
                Utils.logger.LogError("Failed to prepare saving calculator windows: " + ex.ToString());
            }
        }

        static void EndSaveScope()
        {
            saveScopeDepth--;
            if (saveScopeDepth > 0)
                return;
            saveScopeDepth = 0;
            SaveSlotScope scope = saveScope;
            saveScope = null;
            if (scope == null)
                return;
            try
            {
                scope.Apply(CaptureCurrentData);
            }
            catch (Exception ex)
            {
                Utils.logger.LogError("Failed to save calculator windows: " + ex.ToString());
            }
        }

        static void RequestReset()
        {
            lock (sync)
            {
                resetRequested = true;
                pendingRecords.Clear();
            }
        }

        // ---------------- 存档 ----------------
        // 用Finalizer结束，保证游戏存档抛出异常时也能正确退出嵌套计数，并按磁盘上的实际结果同步.dspcalc文件。
        // SaveAsLastExit和AutoSaveAfterErrored直接调用SaveCurrentGame，不需要单独处理；
        // AutoSave先存到AutoSaveTmp再轮换_autosave_0~3，必须在外层处理，.dspcalc才能跟着轮换

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameSave), "SaveCurrentGame")]
        public static void SaveCurrentGamePrefix(string saveName)
        {
            BeginSaveScope(saveName);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(GameSave), "SaveCurrentGame")]
        public static void SaveCurrentGameFinalizer()
        {
            EndSaveScope();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameSave), "AutoSave")]
        public static void AutoSavePrefix()
        {
            BeginSaveScope(GameSave.AutoSaveTmp, GameSave.AutoSave0, GameSave.AutoSave1, GameSave.AutoSave2, GameSave.AutoSave3);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(GameSave), "AutoSave")]
        public static void AutoSaveFinalizer()
        {
            EndSaveScope();
        }

        // ---------------- 读档 / 新游戏 ----------------

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), "NewGame")]
        [HarmonyPatch(typeof(GameData), "Import")]
        public static void OnGameDataReset()
        {
            RequestReset();
        }

        static string loadingSaveName;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameSave), "LoadCurrentGame")]
        public static void LoadCurrentGamePrefix(string saveName)
        {
            loadingSaveName = NormalizeSaveName(saveName);
        }

        /// <summary>
        /// 读档时只记下待恢复的数据（GameData.Import在LoadCurrentGame内部调用，已先清空了旧窗口），游戏运行后再在OnUpdate中创建窗口
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameSave), "LoadCurrentGame")]
        public static void LoadCurrentGamePostfix(bool __result)
        {
            string saveName = loadingSaveName;
            loadingSaveName = null;
            // 主菜单背景的演示存档会被临时写成_autosave_tmp再读取，不属于玩家存档
            if (!__result || string.IsNullOrEmpty(saveName) || DSPGame.IsMenuDemo)
                return;
            try
            {
                List<CalcWindowRecord> records = CalcSaveFile.Read(CalcSaveFile.PathFor(GameConfig.gameSaveFolder, saveName));
                if (records == null)
                    return; // 没有.dspcalc的旧存档，保持空白
                List<CalcWindowRecord> ordered = records.Where(r => r.state == CalcWindowRecord.STATE_CLOSED).OrderBy(r => r.closedOrder).ToList();
                ordered.AddRange(records.Where(r => r.state != CalcWindowRecord.STATE_CLOSED));
                lock (sync)
                {
                    pendingRecords = ordered;
                }
            }
            catch (Exception ex)
            {
                Utils.logger.LogError("Failed to load calculator windows of save " + saveName + ": " + ex.ToString());
            }
        }
    }
}

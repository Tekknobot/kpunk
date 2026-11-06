using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// In-memory undo/redo for native DAW edits and live instrument controls.
    /// Pointer gestures are captured as one action, even when a knob fires hundreds
    /// of writes. Non-pointer edits are detected by idle sampling.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class ZPunkUndoHistory : MonoBehaviour
    {
        private const int MaxUndoSteps = 40;
        private const int MaxHistoryChars = 10 * 1024 * 1024;
        private const float SampleInterval = .38f;
        private const float SettleTime = .24f;
        private KMusicProjectManager projects;
        private UIDocument doc;
        private VisualElement attachedRoot;
        private readonly List<string> past = new List<string>();
        private readonly List<string> future = new List<string>();
        private string current;
        private string candidate;
        private float lastSample, changedAt, rebaseAt;
        private int currentSlot = -1;
        private bool restoring;
        // The previous version only sampled every 450ms, so a quick tap followed
        // by another edit could be grouped, skipped or appear to do nothing.
        // Commit a completed pointer gesture on the following UI frame instead.
        private bool gesturePending;
        private int gestureFinishFrame;
        private readonly HashSet<int> heldPointers = new HashSet<int>();
        // A clicked UI Toolkit button may dispatch its ClickEvent before the
        // pointer-up/capture-release reaches our root listener. History actions
        // must not be blocked by the pointer used to press them.
        private int suppressHistoryPointerThroughFrame = -1;
        private Action refreshWorkspace;
        public int UndoCount => past.Count;
        public int RedoCount => future.Count;
        public bool CanUndo => past.Count != 0;
        public bool CanRedo => future.Count != 0;
        public event Action HistoryChanged;

        public void Initialize(KMusicProjectManager manager, UIDocument ui, Action refresh)
        {
            projects = manager;
            doc = ui;
            refreshWorkspace = refresh;
            AttachInput();
            ResetToCurrent();
        }

        private void AttachInput()
        {
            var root = doc != null ? doc.rootVisualElement : null;
            if (ReferenceEquals(root, attachedRoot)) return;
            if (attachedRoot != null)
            {
                attachedRoot.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                attachedRoot.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                attachedRoot.UnregisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
            }
            attachedRoot = root;
            if (root != null)
            {
                root.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                root.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                root.RegisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
            }
        }
        private void OnDestroy()
        {
            if (attachedRoot == null) return;
            attachedRoot.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            attachedRoot.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            attachedRoot.UnregisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
        }
        private static bool IsHistoryMenuAction(UnityEngine.UIElements.EventBase evt)
        {
            var element = evt.target as VisualElement;
            while (element != null)
            {
                if (element.name == "ZPunkHistoryActions") return true;
                element = element.parent;
            }
            return false;
        }
        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || restoring || Time.frameCount <= suppressHistoryPointerThroughFrame ||
                IsHistoryMenuAction(evt)) return;
            if (heldPointers.Count == 0)
            {
                // Finish the preceding gesture BEFORE recording this gesture.
                Flush();
                // An asynchronous preset restore can still be settling. Once
                // the user begins a new action, rebase on what is actually live
                // instead of silently discarding the user's next edit.
                if (rebaseAt > 0f)
                {
                    RebaseFromLive();
                    rebaseAt = 0f;
                }
                gesturePending = false;
            }
            heldPointers.Add(evt.pointerId);
        }
        private void OnPointerUp(PointerUpEvent evt)
        {
            if (Time.frameCount <= suppressHistoryPointerThroughFrame ||
                IsHistoryMenuAction(evt)) return;
            if (!heldPointers.Remove(evt.pointerId)) return;
            QueueGestureCompletion();
        }
        private void OnPointerCancel(PointerCancelEvent evt)
        {
            if (Time.frameCount <= suppressHistoryPointerThroughFrame ||
                IsHistoryMenuAction(evt)) return;
            if (!heldPointers.Remove(evt.pointerId)) return;
            QueueGestureCompletion();
        }
        private void QueueGestureCompletion()
        {
            if (heldPointers.Count != 0) return;
            // Button.clicked and pointer-up listeners must have a chance to
            // mutate SongData before the post-gesture snapshot is taken.
            gesturePending = true;
            gestureFinishFrame = Time.frameCount + 1;
            changedAt = Time.unscaledTime;
            lastSample = -1f;
        }
        private void RebaseFromLive()
        {
            try
            {
                current = projects.CaptureUndoSnapshot();
                candidate = null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ZPUNK Undo] Rebase: " + e.Message);
            }
        }
        private void Update()
        {
            if (projects == null) return;
            if (projects.CurrentSlot != currentSlot)
            {
                ResetToCurrent();
                return;
            }
            if (restoring) return;
            if (gesturePending && heldPointers.Count == 0 &&
                Time.frameCount >= gestureFinishFrame)
            {
                gesturePending = false;
                // One history step per tap, clip drag, note drawing or dial drag.
                // No need to wait for the idle timer when a gesture has ended.
                if (rebaseAt <= 0f)
                {
                    Sample();
                    if (candidate != null) Commit(candidate);
                }
            }
            if (Time.unscaledTime < lastSample + SampleInterval) return;
            lastSample = Time.unscaledTime;
            if (rebaseAt > 0f)
            {
                if (Time.unscaledTime >= rebaseAt && heldPointers.Count == 0)
                {
                    rebaseAt = 0f;
                    RebaseFromLive();
                }
                return;
            }
            if (heldPointers.Count != 0) return;
            Sample();
            // Fallback for keyboard/programmatic edits without pointer events.
            if (candidate != null && !gesturePending &&
                Time.unscaledTime - changedAt >= SettleTime)
                Commit(candidate);
        }
        private void Sample()
        {
            try
            {
                string snapshot = projects.CaptureUndoSnapshot();
                if (snapshot == current)
                {
                    candidate = null;
                    return;
                }
                if (snapshot != candidate)
                {
                    candidate = snapshot;
                    changedAt = Time.unscaledTime;
                }
            }
            catch (Exception e) { Debug.LogWarning("[ZPUNK Undo] Capture skipped: " + e.Message); }
        }
        private void Commit(string next)
        {
            if (string.IsNullOrEmpty(next) || next == current) { candidate = null; return; }
            if (current != null) past.Add(current);
            current = next;
            candidate = null;
            future.Clear();
            Trim();
            HistoryChanged?.Invoke();
        }
        private void Trim()
        {
            while (past.Count > MaxUndoSteps) past.RemoveAt(0);
            int chars = current != null ? current.Length : 0;
            for (int i = 0; i < past.Count; i++) chars += past[i].Length;
            for (int i = 0; i < future.Count; i++) chars += future[i].Length;
            while (chars > MaxHistoryChars && past.Count > 0)
            {
                chars -= past[0].Length;
                past.RemoveAt(0);
            }
        }
        private void Flush()
        {
            if (projects == null || restoring) return;
            if (rebaseAt > 0f)
            {
                if (Time.unscaledTime >= rebaseAt)
                {
                    rebaseAt = 0f;
                    RebaseFromLive();
                }
                return;
            }
            Sample();
            if (candidate != null) Commit(candidate);
            gesturePending = false;
        }
        public void ResetToCurrent()
        {
            past.Clear(); future.Clear(); heldPointers.Clear(); candidate = null;
            suppressHistoryPointerThroughFrame = -1;
            gesturePending = false; gestureFinishFrame = 0;
            rebaseAt = 0f;
            currentSlot = projects == null ? -1 : projects.CurrentSlot;
            try { current = projects == null ? null : projects.CaptureUndoSnapshot(); }
            catch (Exception e) { current = null; Debug.LogWarning("[ZPUNK Undo] Reset: " + e.Message); }
            HistoryChanged?.Invoke();
        }
        public void Undo() { Step(false); }
        public void Redo() { Step(true); }
        private void Step(bool redo)
        {
            if (projects == null || restoring)
            {
                Debug.LogWarning("[ZPUNK Undo] Action ignored: history is not ready or is restoring.");
                return;
            }
            if (projects.CurrentSlot != currentSlot)
            {
                Debug.LogWarning("[ZPUNK Undo] Project switched: resetting history.");
                ResetToCurrent();
                return;
            }
            // Do NOT block on heldPointers. UI Toolkit ClickEvent can fire
            // before our root receives PointerUp, especially on touch devices.
            // Treat Undo/Redo as an explicit completed action and discard any
            // stale pointer/capture state left by the menu button.
            heldPointers.Clear();
            gesturePending = false;
            suppressHistoryPointerThroughFrame = Time.frameCount + 2;
            Flush();
            gesturePending = false;
            var from = redo ? future : past;
            var to = redo ? past : future;
            if (from.Count == 0)
            {
                Debug.LogWarning("[ZPUNK Undo] No " + (redo ? "redo" : "undo") +
                    " state is available; count=0.");
                return;
            }
            var target = from[from.Count - 1];
            restoring = true;
            try
            {
                if (!projects.RestoreUndoSnapshot(target))
                {
                    Debug.LogWarning("[ZPUNK Undo] Snapshot restore was rejected. " +
                        "History was left unchanged; see the Project Manager log.");
                    return;
                }
                if (current != null) to.Add(current);
                from.RemoveAt(from.Count - 1);
                current = target;
                candidate = null;
                changedAt = Time.unscaledTime;
                lastSample = Time.unscaledTime;
                // Preset restoration can take several frames. Prevent those
                // intermediate audio parameters becoming new undo entries.
                rebaseAt = Time.unscaledTime + 1.2f;
                try { refreshWorkspace?.Invoke(); }
                catch (Exception ex)
                {
                    Debug.LogError("[ZPUNK Undo] State restored, but UI refresh failed: " + ex);
                }
                Debug.Log("[ZPUNK Undo] " + (redo ? "Redo" : "Undo") +
                    " restored project snapshot. Undo=" + past.Count +
                    " Redo=" + future.Count);
                Trim();
                HistoryChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError("[ZPUNK Undo] Unexpected history action error: " + ex);
            }
            finally { restoring = false; }
        }
    }
}

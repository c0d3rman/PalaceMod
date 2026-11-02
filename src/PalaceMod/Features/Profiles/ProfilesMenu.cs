using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/*
    The Profiles screen: a "Profiles" button in the title screen's row (before Quit) opens a frame listing the profiles
    on the left and what can be done with the selected one on the right. Everything is cloned from the game's own
    title-screen buttons and Settings texts, so it looks and navigates like the rest of the menus (keyboard, mouse,
    controller). Destructive actions ask first in a Yes/No dialog built the same way. Renaming types into the
    profile's own row (Enter to keep, Esc to cancel); while typing, the game's menu input is paused.
*/
namespace PalaceMod.Profiles
{
    internal sealed class ProfilesMenu
    {
        private readonly ProfilesManager manager;

        private UIFrame titleFrame, frame, confirmFrame;
        private TFUITextButton titleButton;     // "Profiles" on the title screen
        private TFUITextButton buttonTemplate;  // a title-screen button, the model for every button here
        private TextMeshProUGUI bodyTemplate, headingTemplate;
        private RectTransform listColumn, actionColumn;
        private TextMeshProUGUI info, confirmText;
        private TFUITextButton confirmYes, confirmNo;
        private Action confirmAction;
        private readonly List<(TFUITextButton button, ProfileStore.Profile profile)> rows = [];
        private TFUITextButton newButton;
        private readonly List<TFUITextButton> actions = [];
        private ProfileStore.Profile selected;
        private bool built, failed;

        // Rename state.
        private TFUITextButton renaming;
        private string typed;
        internal static bool Typing;

        public ProfilesMenu(ProfilesManager manager) { this.manager = manager; }

        public void Update()
        {
            // The title screen may live in the menu scene, which is reloaded; build again for the new one.
            if (built && (titleFrame == null || frame == null || titleButton == null)) Remove();
            if (!built && !failed) TryBuild();
            if (!built) return;
            if (titleButton != null) titleButton.gameObject.SetActive(true);
            if (renaming != null) UpdateRename();
            else if (frame != null && UIFrameManager.instance.ActiveFrame == frame) TrackSelection();
        }

        public void Open()
        {
            if (!built) throw new InvalidOperationException("the profiles screen isn't available (see the log)");
            Refresh(manager.Store.Active.id);
            UIFrameManager.instance.ChangeActiveFrame(frame);
        }

        public void Close()
        {
            EndRename(false);
            var frames = UIFrameManager.instance;
            if (frames == null) return;
            if (confirmFrame != null && frames.ActiveFrame == confirmFrame) frames.CloseActiveFrame();
            if (frame != null && frames.ActiveFrame == frame) frames.CloseActiveFrame();
        }

        /// <summary>Takes everything out of the game's UI (feature turned off).</summary>
        public void Remove()
        {
            Close();
            foreach (var obj in new Object[] { titleButton?.gameObject, frame?.gameObject, confirmFrame?.gameObject })
                if (obj != null) Object.Destroy(obj);
            if (titleButton != null) Unsplice(titleButton);
            titleButton = null;
            frame = confirmFrame = null;
            built = false;
            failed = false;
        }

        // ---- Building -----------------------------------------------------------------------------------------------

        private void TryBuild()
        {
            var frames = UIFrameManager.instance;
            if (frames == null) return;
            titleFrame = Traverse.Create(frames).Field("titleFrame").GetValue<UIFrame>();
            if (titleFrame == null || !ProfilesManager.OnMainMenu) return;
            var created = new List<GameObject>();
            try
            {
                FindTemplates();
                BuildFrame(created);
                BuildConfirm(created);
                BuildTitleButton(created);
                built = true;
                Plugin.Logger.LogInfo("Profiles: added the Profiles screen to the title menu");
            }
            catch (Exception e)
            {
                failed = true;
                foreach (var obj in created) if (obj != null) Object.Destroy(obj);
                Plugin.Logger.LogError($"Profiles: couldn't add the Profiles screen (use `dev.py cmd profiles` to manage profiles): {e}");
            }
        }

        private void FindTemplates()
        {
            var buttons = titleFrame.GetComponentsInChildren<TFUITextButton>(true);
            Plugin.Logger.LogInfo("Profiles: title buttons: " + string.Join(", ", buttons.Select(b => $"{b.name} \"{b.GetComponent<TextMeshProUGUI>()?.text}\" [{Listeners(b.onApply)}]")));
            // Settings, not Play: Play is selected when the title opens, and a clone keeps the colour it had then.
            buttonTemplate = buttons.FirstOrDefault(b => b.name == "Settings") ?? buttons.First(b => b.name != "Play");
            // Settings' heading and body text: the big blackletter title and the italic labels.
            var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>().Where(t => t.gameObject.scene.IsValid()).ToList();
            headingTemplate = texts.Where(t => t.GetComponentInParent<SettingsUIHelper>(true) != null).OrderByDescending(t => t.fontSize).FirstOrDefault()
                ?? throw new InvalidOperationException("no heading text found");
            bodyTemplate = buttonTemplate.GetComponent<TextMeshProUGUI>();
            Plugin.Logger.LogInfo($"Profiles: templates: button {buttonTemplate.name}, heading {headingTemplate.name} \"{headingTemplate.text}\" {headingTemplate.font?.name} {headingTemplate.fontSize}");
        }

        private static string Listeners(UnityEventBase e) =>
            string.Join("/", Enumerable.Range(0, e.GetPersistentEventCount()).Select(i => e.GetPersistentMethodName(i)));

        private UIFrame NewFrame(string name, List<GameObject> created)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.SetActive(false);
            created.Add(obj);
            var rect = (RectTransform)obj.transform;
            rect.SetParent(titleFrame.transform.parent, false);
            Stretch(rect);
            var background = obj.AddComponent<Image>();
            background.color = Color.black;
            var newFrame = obj.AddComponent<UIFrame>();
            newFrame.freezePlayer = titleFrame.freezePlayer;
            newFrame.freezeTime = titleFrame.freezeTime;
            // UIFrame.Awake registers with the manager; it runs on the first activation, so wake it now and hide it.
            obj.SetActive(true);
            newFrame.Deactivate();
            return newFrame;
        }

        private void BuildFrame(List<GameObject> created)
        {
            frame = NewFrame("PalaceMod Profiles", created);
            var root = (RectTransform)frame.transform;

            var heading = Text(headingTemplate, root, "Profiles");
            Place(heading.rectTransform, new Vector2(0.1f, 0.78f), new Vector2(0.9f, 0.92f));
            heading.alignment = TextAlignmentOptions.Left;

            listColumn = Column("Profiles list", root, new Vector2(0.17f, 0.25f), new Vector2(0.45f, 0.72f));
            actionColumn = Column("Profile actions", root, new Vector2(0.55f, 0.25f), new Vector2(0.83f, 0.72f));

            info = Text(bodyTemplate, root, "");
            Place(info.rectTransform, new Vector2(0.17f, 0.08f), new Vector2(0.83f, 0.22f));
            info.alignment = TextAlignmentOptions.Top;
            info.fontSize = bodyTemplate.fontSize * 0.75f;
            info.enableWordWrapping = true;
            info.color = new Color(bodyTemplate.color.r, bodyTemplate.color.g, bodyTemplate.color.b, 0.8f);
        }

        private void BuildConfirm(List<GameObject> created)
        {
            confirmFrame = NewFrame("PalaceMod Profiles Confirm", created);
            var root = (RectTransform)confirmFrame.transform;
            confirmText = Text(bodyTemplate, root, "");
            Place(confirmText.rectTransform, new Vector2(0.2f, 0.5f), new Vector2(0.8f, 0.7f));
            confirmText.alignment = TextAlignmentOptions.Bottom;
            confirmText.enableWordWrapping = true;
            var row = new GameObject("Buttons", typeof(RectTransform));
            var rowRect = (RectTransform)row.transform;
            rowRect.SetParent(root, false);
            Place(rowRect, new Vector2(0.3f, 0.36f), new Vector2(0.7f, 0.46f));
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 120f;
            layout.childControlWidth = layout.childControlHeight = false;
            confirmYes = Button(rowRect, "Yes", () =>
            {
                var action = confirmAction;
                confirmAction = null;
                UIFrameManager.instance.CloseActiveFrame();
                Run(action);
            });
            confirmNo = Button(rowRect, "No", () => UIFrameManager.instance.CloseActiveFrame());
            Link(confirmYes, confirmNo, horizontal: true);
            confirmFrame.firstSelected = confirmNo;
        }

        private void BuildTitleButton(List<GameObject> created)
        {
            // Before Quit, the last button of the row the Settings button is in (other menus hide in the same frame).
            var row = buttonTemplate.transform.parent.GetComponentsInChildren<TFUITextButton>(true)
                .Where(b => b.transform.parent == buttonTemplate.transform.parent).ToList();
            var quit = row.FirstOrDefault(b => Listeners(b.onApply).Contains("Quit")) ?? row.Last();
            Plugin.Logger.LogInfo($"Profiles: title row {string.Join(", ", row.Select(b => b.name))}; inserting before {quit.name}");
            var obj = Object.Instantiate(buttonTemplate.gameObject, quit.transform.parent, false);
            created.Add(obj);
            obj.name = "PalaceMod Profiles Button";
            obj.transform.SetSiblingIndex(quit.transform.GetSiblingIndex());
            StripLocalization(obj);
            titleButton = obj.GetComponent<TFUITextButton>();
            titleButton.onApply = new UnityEvent();
            titleButton.onApply.AddListener(() => Run(Open));
            titleButton.SetOriginalString("Profiles");
            // Splice into the row's left/right navigation before Quit.
            var before = quit.leftNav;
            titleButton.leftNav = before;
            titleButton.rightNav = quit;
            titleButton.topNav = quit.topNav;
            titleButton.botNav = quit.botNav;
            if (before != null) before.rightNav = titleButton;
            quit.leftNav = titleButton;
            titleFrame.RefetchManagedElements();
        }

        private static void Unsplice(TFUITextButton button)
        {
            if (button.leftNav != null) button.leftNav.rightNav = button.rightNav;
            if (button.rightNav != null) button.rightNav.leftNav = button.leftNav;
        }

        // ---- Content ------------------------------------------------------------------------------------------------

        /// <summary>Rebuilds the list and selects the given profile's row.</summary>
        private void Refresh(string selectId)
        {
            foreach (Transform child in listColumn.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            rows.Clear();
            var store = manager.Store;
            foreach (var profile in store.Profiles)
            {
                var p = profile;
                bool active = p.id == store.Active.id;
                // A mouse click applies the row before the selection is polled, so show its actions first.
                var button = Button(listColumn, active ? $"{p.name}  (current)" : p.name, () => { ShowActions(p); FocusActions(); });
                rows.Add((button, p));
            }
            newButton = Button(listColumn, "+ New profile", () => Run(() =>
            {
                manager.CreateAndSwitch(null);
                Notify.Show($"Started a fresh profile, \"{manager.Store.Active.name}\". Your other profiles are kept.", NotifyKind.Success, 6f, "profiles");
            }));
            var column = rows.Select(r => r.button).Append(newButton).ToList();
            for (int i = 0; i < column.Count; i++)
            {
                column[i].topNav = column[(i + column.Count - 1) % column.Count];
                column[i].botNav = column[(i + 1) % column.Count];
            }
            var target = rows.FirstOrDefault(r => r.profile.id == selectId).button ?? rows[0].button;
            frame.firstSelected = target;
            ShowActions(rows.First(r => r.button == target).profile);
            frame.RefetchManagedElements();
            if (UIFrameManager.instance.ActiveFrame == frame) frame.Select(target);
        }

        private void TrackSelection()
        {
            var current = frame.CurrentSelection as TFUITextButton;
            var row = rows.FirstOrDefault(r => r.button == current);
            if (row.button != null && row.profile != selected) ShowActions(row.profile);
            if (current == newButton && selected != null)
            {
                selected = null;
                foreach (Transform child in actionColumn.Cast<Transform>().ToList()) Object.Destroy(child.gameObject);
                actions.Clear();
                info.text = "Start over from the tutorial in a new, empty profile. Your current progress stays in its own profile.";
            }
        }

        private void ShowActions(ProfileStore.Profile profile)
        {
            selected = profile;
            foreach (Transform child in actionColumn.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            actions.Clear();
            var store = manager.Store;
            bool active = profile.id == store.Active.id;
            if (!active) actions.Add(Button(actionColumn, "Play this profile", () => Run(() =>
            {
                manager.SwitchTo(profile);
                Notify.Show($"Now playing \"{profile.name}\"", NotifyKind.Success, 4f, "profiles");
            })));
            actions.Add(Button(actionColumn, "Rename", () => BeginRename(profile)));
            actions.Add(Button(actionColumn, "Duplicate", () => Run(() =>
            {
                var copy = manager.Duplicate(profile);
                Refresh(copy.id);
            })));
            if (active) actions.Add(Button(actionColumn, "Reset progress", () => Confirm(
                $"Reset all progress in \"{profile.name}\"? A backup is kept in the profiles folder.",
                () =>
                {
                    manager.ResetActive();
                    Notify.Show($"\"{profile.name}\" was reset. A backup was saved first.", NotifyKind.Success, 6f, "profiles");
                })));
            else actions.Add(Button(actionColumn, "Delete", () => Confirm(
                $"Delete \"{profile.name}\"? Its saves are moved to the backups in the profiles folder.",
                () =>
                {
                    manager.Delete(profile);
                    Refresh(manager.Store.Active.id);
                })));
            var row = rows.FirstOrDefault(r => r.profile.id == profile.id).button;
            for (int i = 0; i < actions.Count; i++)
            {
                actions[i].topNav = actions[(i + actions.Count - 1) % actions.Count];
                actions[i].botNav = actions[(i + 1) % actions.Count];
                actions[i].leftNav = row;
            }
            foreach (var r in rows) r.button.rightNav = actions.FirstOrDefault();
            var (level, beaten) = store.Summary(profile);
            info.text = $"{profile.name}: perk level {level}, {beaten} {(beaten == 1 ? "map" : "maps")} beaten."
                + (profile.id == ProfileStore.DefaultId ? "" : $" Created {profile.created}.")
                + (active ? "\nThis is the profile you're playing. Steam Cloud keeps the profile in use." : "");
            frame.RefetchManagedElements();
        }

        private void FocusActions()
        {
            if (actions.Count > 0) frame.Select(actions[0]);
        }

        private void Confirm(string question, Action action)
        {
            confirmText.text = question;
            confirmAction = action;
            UIFrameManager.instance.ChangeActiveFrameKeepOldVisible(confirmFrame);
        }

        // Runs a menu action, reporting failures to the player instead of throwing into the UI.
        private void Run(Action action)
        {
            if (action == null) return;
            try { action(); }
            catch (Exception e)
            {
                Notify.Show($"Profiles: {e.Message}", NotifyKind.Error, 6f, "profiles");
                if (e is not (InvalidOperationException or ArgumentException)) Plugin.Logger.LogError($"Profiles: {e}");
            }
        }

        // ---- Rename -------------------------------------------------------------------------------------------------

        private void BeginRename(ProfileStore.Profile profile)
        {
            renaming = rows.First(r => r.profile.id == profile.id).button;
            typed = profile.name;
            Typing = true;
            frame.Select(renaming);
            ShowTyped();
            info.text = "Type a new name, then press Enter to keep it or Esc to cancel.";
        }

        private void UpdateRename()
        {
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (typed.Length > 0) typed = typed.Substring(0, typed.Length - 1); }
                else if (c == '\n' || c == '\r') { EndRename(true); return; }
                else if (!char.IsControl(c) && typed.Length < ProfileStore.MaxNameLength) typed += c;
            }
            // Esc, or the game's Cancel action from a controller (which can't type) so it isn't stuck here. Only from a
            // controller: the keyboard's Cancel binding could be a key that's being typed.
            var player = Plugin.Input;
            bool padCancel = player != null && player.GetButtonDown("Cancel")
                && player.controllers.GetLastActiveController()?.type == Rewired.ControllerType.Joystick;
            if (Input.GetKeyDown(KeyCode.Escape) || padCancel) { EndRename(false); return; }
            if (Input.GetKeyDown(KeyCode.KeypadEnter)) { EndRename(true); return; }
            ShowTyped();
        }

        private void ShowTyped() => renaming.SetOriginalString(typed + (Time.unscaledTime % 1f < 0.5f ? "|" : " "));

        private void EndRename(bool keep)
        {
            Typing = false;
            if (renaming == null) { renaming = null; return; }
            var profile = rows.First(r => r.button == renaming).profile;
            renaming = null;
            Typing = false;
            if (keep && typed.Trim().Length > 0 && typed.Trim() != profile.name) Run(() => manager.Rename(profile, typed));
            Refresh(profile.id);
        }

        // ---- Widgets ------------------------------------------------------------------------------------------------

        private TFUITextButton Button(Transform parent, string label, Action onApply)
        {
            var obj = Object.Instantiate(buttonTemplate.gameObject, parent, false);
            obj.name = label;
            obj.SetActive(true);
            var rect = (RectTransform)obj.transform;
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, rect.sizeDelta.y * 0.75f); // a title button is tall for a list
            StripLocalization(obj);
            var button = obj.GetComponent<TFUITextButton>();
            button.onApply = new UnityEvent();
            button.onApply.AddListener(() => onApply());
            button.leftNav = button.rightNav = button.topNav = button.botNav = null;
            button.SetOriginalString(label);
            return button;
        }

        private static void Link(ThronefallUIElement a, ThronefallUIElement b, bool horizontal)
        {
            if (horizontal) { a.rightNav = b; b.leftNav = a; a.leftNav = b; b.rightNav = a; }
            else { a.botNav = b; b.topNav = a; a.topNav = b; b.botNav = a; }
        }

        private static TextMeshProUGUI Text(TextMeshProUGUI template, Transform parent, string text)
        {
            var obj = Object.Instantiate(template.gameObject, parent, false);
            foreach (Transform child in obj.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            foreach (var component in Enumerable.Reverse(obj.GetComponents<Component>())) // dependents (audio helper) go first
                if (component is not (RectTransform or CanvasRenderer or TextMeshProUGUI)) Object.DestroyImmediate(component);
            obj.SetActive(true);
            var tmp = obj.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static RectTransform Column(string name, Transform parent, Vector2 min, Vector2 max)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)obj.transform;
            rect.SetParent(parent, false);
            Place(rect, min, max);
            var layout = obj.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.spacing = 0f;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return rect;
        }

        private static void Place(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
        }

        private static void Stretch(RectTransform rect) => Place(rect, Vector2.zero, Vector2.one);

        private static void StripLocalization(GameObject root)
        {
            foreach (var localize in root.GetComponentsInChildren<I2.Loc.Localize>(true)) Object.DestroyImmediate(localize);
        }
    }
}

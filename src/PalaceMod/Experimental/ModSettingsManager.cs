// using System.Collections.Generic;
// using UnityEngine;
// using UnityEngine.UI;
// using TMPro;
// using HarmonyLib;
// using UnityEngine.Events;
// using Rewired;
// using I2.Loc;
// using System.Linq;
// using System.Reflection;

// namespace PalaceMod
// {
//     public static class ModSettingsManager
//     {
//         private static GameObject modSelect;
//         private static GameObject modTab;
//         private static List<GameObject> settingTypes = new List<GameObject>();
//         private static List<string> modIDs = new List<string>();
//         private static List<GameObject> modSettings = new List<GameObject>();
//         private static bool isInitialized = false;
//         private static Player rewiredPlayer;
//         private static Dictionary<string, int> modActionIds = new Dictionary<string, int>();
//         private static int nextActionId = 1000; // Start at 1000 to avoid conflicts with game actions

//         private class QueuedSetting
//         {
//             public enum Type { Toggle, Dropdown, Slider, Control }
//             public Type type;
//             public string modId;
//             public string settingId;
//             public string displayName;
//             public object defaultValue;
//             public object options;
//             public object onChange;
//         }

//         private static List<QueuedSetting> queuedSettings = new List<QueuedSetting>();

//         public static void Initialize()
//         {
//             // This will be called by our Harmony patch
//             Plugin.Logger.LogInfo("ModSettingsManager: Initializing");
//             isInitialized = false;
//             queuedSettings.Clear();
//             rewiredPlayer = ReInput.players.GetPlayer(0); // Get the main player
//         }

//         [HarmonyPatch(typeof(SettingsUIHelper))]
//         [HarmonyPatch("Awake")]
//         public static class SettingsUIHelper_Awake_Patch
//         {
//             private static void Postfix(SettingsUIHelper __instance)
//             {
//                 try
//                 {
//                     Plugin.Logger.LogInfo("ModSettingsManager: Patching SettingsUIHelper.Awake");
//                     CreateModSettingsTab(__instance);
//                     isInitialized = true;
//                     ProcessQueuedSettings();

//                     // Find and patch the Controls tab
//                     var controlsTab = GameObject.Find("UI Canvas/Settings Frame/Control Settings");
//                     if (controlsTab != null)
//                     {
//                         var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID + "_ControlsTab");
//                         var originalMethod = controlsTab.GetType().GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
//                         if (originalMethod != null)
//                         {
//                             var postfix = typeof(ControlsTab_OnEnable_Patch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
//                             harmony.Patch(originalMethod, postfix: new HarmonyMethod(postfix));
//                         }
//                     }
//                 }
//                 catch (System.Exception e)
//                 {
//                     Plugin.Logger.LogError($"ModSettingsManager: Error in Awake patch: {e}");
//                 }
//             }
//         }

//         private static class ControlsTab_OnEnable_Patch
//         {
//             private static void Postfix(MonoBehaviour __instance)
//             {
//                 try
//                 {
//                     ProcessQueuedControlSettings();
//                 }
//                 catch (System.Exception e)
//                 {
//                     Plugin.Logger.LogError($"Error in Controls tab OnEnable patch: {e}");
//                 }
//             }
//         }

//         private static void ProcessQueuedSettings()
//         {
//             if (queuedSettings.Count == 0) return;

//             // Create a copy of the queue to process
//             var settingsToProcess = queuedSettings.ToList();
//             queuedSettings.Clear();

//             // Process all settings
//             foreach (var setting in settingsToProcess)
//             {
//                 try
//                 {
//                     switch (setting.type)
//                     {
//                         case QueuedSetting.Type.Toggle:
//                             AddToggleSettingInternal(
//                                 setting.modId,
//                                 setting.settingId,
//                                 setting.displayName,
//                                 (bool)setting.defaultValue,
//                                 (UnityAction<bool>)setting.onChange,
//                                 false  // Don't update selector yet
//                             );
//                             break;
//                         case QueuedSetting.Type.Dropdown:
//                             AddDropdownSettingInternal(
//                                 setting.modId,
//                                 setting.settingId,
//                                 setting.displayName,
//                                 (List<string>)setting.options,
//                                 (int)setting.defaultValue,
//                                 (UnityAction<int>)setting.onChange,
//                                 false  // Don't update selector yet
//                             );
//                             break;
//                         case QueuedSetting.Type.Slider:
//                             var values = (Vector4)setting.options;
//                             AddSliderSettingInternal(
//                                 setting.modId,
//                                 setting.settingId,
//                                 setting.displayName,
//                                 values.x,  // minValue
//                                 values.y,  // maxValue
//                                 (float)setting.defaultValue,
//                                 values.z,  // increment
//                                 (UnityAction<float>)setting.onChange,
//                                 false  // Don't update selector yet
//                             );
//                             break;
//                         case QueuedSetting.Type.Control:
//                             AddControlSetting(
//                                 setting.modId,
//                                 setting.settingId,
//                                 setting.displayName,
//                                 (ControlBinding)setting.defaultValue,
//                                 (UnityAction<ControlBinding>)setting.onChange
//                             );
//                             break;
//                     }
//                 }
//                 catch (System.Exception e)
//                 {
//                     Plugin.Logger.LogError($"Error processing setting {setting.type} - {setting.displayName}: {e}");
//                     // Re-queue the setting if it failed
//                     queuedSettings.Add(setting);
//                 }
//             }

//             // Update the selector once at the end
//             if (modIDs.Count > 0)
//             {
//                 UpdateModSelector();
//             }

//             // If any settings were re-queued, try processing them again later
//             if (queuedSettings.Count > 0)
//             {
//                 Plugin.Logger.LogInfo($"{queuedSettings.Count} settings re-queued for later processing");
//             }
//         }

//         private static void CreateModSettingsTab(SettingsUIHelper settingsHelper)
//         {
//             // Create new tab by cloning Video tab
//             var videoTab = GameObject.Find("UI Canvas/Settings Frame/Category Tabs/Video");
//             var tabParent = GameObject.Instantiate(videoTab, GameObject.Find("UI Canvas/Settings Frame/Category Tabs").transform);
//             tabParent.name = "Mods";
//             modTab = tabParent;

//             // Create settings container by cloning Video Settings
//             var videoSettings = GameObject.Find("UI Canvas/Settings Frame/Video Settings");
//             var tabChild = GameObject.Instantiate(videoSettings, GameObject.Find("UI Canvas/Settings Frame").transform);
//             tabChild.name = "Mods Settings";

//             // Create new settings tab
//             var newTab = new SettingsUIHelper.SettingsTab();
//             newTab.parentElement = tabParent.GetComponent<TFUITextButton>();
//             newTab.childContainer = tabChild.GetComponent<VerticalLayoutGroup>();
//             newTab.contentParent = tabChild;

//             // Add to settings helper's tabs
//             var allTabs = Traverse.Create(settingsHelper).Field<Dictionary<ThronefallUIElement, SettingsUIHelper.SettingsTab>>("allTabs").Value;
//             allTabs.Add(newTab.parentElement, newTab);
//             newTab.childContainer.gameObject.SetActive(false);
//             newTab.ComputeNavigationForSettingsTab();

//             Traverse.Create(settingsHelper).Field("allTabs").SetValue(allTabs);

//             // Update tab text
//             GameObject.DestroyImmediate(tabParent.GetComponent<I2.Loc.Localize>());
//             tabParent.GetComponent<TextMeshProUGUI>().text = "Mods";
//             Traverse.Create(tabParent.GetComponent<TFUITextButton>()).Field("originalString").SetValue("Mods");

//             // Clear existing settings
//             for (int i = 0; i < tabChild.transform.childCount; i++)
//             {
//                 GameObject.Destroy(tabChild.transform.GetChild(i).gameObject);
//             }

//             // Create mod selector dropdown
//             modSelect = GameObject.Instantiate(GameObject.Find("UI Canvas/Settings Frame/Video Settings/Resolution"), tabChild.transform);
//             modSelect.name = "ModSelect";
//             GameObject.DestroyImmediate(modSelect.GetComponent<I2.Loc.Localize>());
//             GameObject.DestroyImmediate(modSelect.GetComponent<SettingsResolution>());
//             modSelect.GetComponent<TextMeshProUGUI>().text = "Selected Mod Settings";
//             var enumSelector = modSelect.GetComponent<EnumSelector>();
//             enumSelector.options = new List<string> { "No Mod Settings Found" };
//             enumSelector.delayedApply = false;
//             enumSelector.onChange.AddListener(OnModSelectChange);
//             enumSelector.SetIndex(0);  // Set index after setting options

//             // Cache setting type templates
//             CacheSettingTypes(tabChild);

//             // Set up navigation
//             SetupTabNavigation(tabParent);

//             Plugin.Logger.LogInfo("ModSettingsManager: Successfully created mod settings tab");
//         }

//         private static void CacheSettingTypes(GameObject tabChild)
//         {
//             settingTypes.Clear();  // Clear any existing templates

//             try
//             {
//                 // Cache selector template
//                 var selectorTemplate = GameObject.Instantiate(GameObject.Find("UI Canvas/Settings Frame/Video Settings/Resolution"), tabChild.transform);
//                 if (selectorTemplate != null)
//                 {
//                     GameObject.DestroyImmediate(selectorTemplate.GetComponent<I2.Loc.Localize>());
//                     GameObject.DestroyImmediate(selectorTemplate.GetComponent<SettingsResolution>());
//                     selectorTemplate.GetComponent<TextMeshProUGUI>().text = "Preset_Selector";
//                     selectorTemplate.name = "Preset_Selector";
//                     selectorTemplate.SetActive(false);
//                     selectorTemplate.GetComponent<EnumSelector>().delayedApply = false;
//                     settingTypes.Add(selectorTemplate);
//                 }

//                 // Cache toggle template
//                 var toggleTemplate = GameObject.Instantiate(GameObject.Find("UI Canvas/Settings Frame/Video Settings/Full Screen"), tabChild.transform);
//                 if (toggleTemplate != null)
//                 {
//                     GameObject.DestroyImmediate(toggleTemplate.GetComponent<I2.Loc.Localize>());
//                     GameObject.DestroyImmediate(toggleTemplate.GetComponent<SettingsFullscreen>());
//                     toggleTemplate.GetComponent<TextMeshProUGUI>().text = "Preset_Tick";
//                     toggleTemplate.name = "Preset_Tick";
//                     toggleTemplate.SetActive(false);
//                     settingTypes.Add(toggleTemplate);
//                 }

//                 // Cache slider template
//                 var sliderTemplate = GameObject.Instantiate(GameObject.Find("UI Canvas/Settings Frame/Video Settings/Render Scale"), tabChild.transform);
//                 if (sliderTemplate != null)
//                 {
//                     GameObject.DestroyImmediate(sliderTemplate.GetComponent<I2.Loc.Localize>());
//                     GameObject.DestroyImmediate(sliderTemplate.GetComponent<SettingsRenderScale>());
//                     sliderTemplate.GetComponent<TextMeshProUGUI>().text = "Preset_Slider";
//                     sliderTemplate.name = "Preset_Slider";
//                     sliderTemplate.SetActive(false);
//                     GameObject.DestroyImmediate(sliderTemplate.transform.Find("Slider Background/Tooltip").gameObject);
//                     settingTypes.Add(sliderTemplate);
//                 }
//             }
//             catch (System.Exception e)
//             {
//                 Plugin.Logger.LogError($"ModSettingsManager: Error while caching templates: {e}");
//             }
//         }

//         private static void SetupTabNavigation(GameObject tabParent)
//         {
//             var modsButton = tabParent.GetComponent<TFUITextButton>();
//             var gameplayButton = GameObject.Find("UI Canvas/Settings Frame/Category Tabs/Gameplay").GetComponent<TFUITextButton>();
//             var videoButton = GameObject.Find("UI Canvas/Settings Frame/Category Tabs/Video").GetComponent<TFUITextButton>();

//             modsButton.leftNav = gameplayButton;
//             modsButton.rightNav = videoButton;
//             videoButton.leftNav = modsButton;
//             gameplayButton.rightNav = modsButton;

//             var buttonInfo = modSelect.GetComponent<TFUITextButton>();
//             buttonInfo.topNav = modsButton;
//             modsButton.botNav = buttonInfo;
//         }

//         private static void UpdateModSelector()
//         {
//             if (!modSelect) return;

//             Plugin.Logger.LogInfo($"ModSettingsManager: Updating mod selector with {modIDs.Count} mods");

//             var enumSelector = modSelect.GetComponent<EnumSelector>();
//             if (modIDs.Count > 0)
//             {
//                 enumSelector.options = modIDs;
//                 enumSelector.SetIndex(0);
//                 // Show all settings for the selected mod
//                 ShowSettingsForMod(modIDs[0]);
//             }
//             else
//             {
//                 enumSelector.options = new List<string> { "No Mod Settings Found" };
//                 enumSelector.SetIndex(0);
//             }
//         }

//         private static void ShowSettingsForMod(string modId)
//         {
//             Plugin.Logger.LogInfo($"ModSettingsManager: Showing settings for mod {modId}");

//             List<TFUITextButton> buttons = new List<TFUITextButton>();

//             foreach (GameObject setting in modSettings)
//             {
//                 if (setting.name.StartsWith(modId))
//                 {
//                     setting.SetActive(true);
//                     buttons.Add(setting.GetComponent<TFUITextButton>());
//                     Plugin.Logger.LogInfo($"ModSettingsManager: Activated setting {setting.name}");
//                 }
//                 else
//                 {
//                     setting.SetActive(false);
//                 }
//             }

//             if (buttons.Count > 0)
//             {
//                 var buttonInfo = modSelect.GetComponent<TFUITextButton>();
//                 var tabInfo = modTab.GetComponent<TFUITextButton>();

//                 buttonInfo.botNav = buttons[0];
//                 tabInfo.topNav = buttons[buttons.Count - 1];

//                 for (int i = 0; i < buttons.Count; i++)
//                 {
//                     if (i == 0)
//                     {
//                         buttons[i].topNav = buttonInfo;
//                         buttons[i].botNav = i + 1 < buttons.Count ? buttons[i + 1] : tabInfo;
//                     }
//                     else if (i == buttons.Count - 1)
//                     {
//                         buttons[i].topNav = buttons[i - 1];
//                         buttons[i].botNav = tabInfo;
//                     }
//                     else
//                     {
//                         buttons[i].topNav = buttons[i - 1];
//                         buttons[i].botNav = buttons[i + 1];
//                     }
//                 }
//             }
//         }

//         private static void OnModSelectChange()
//         {
//             var enumSelector = modSelect.GetComponent<EnumSelector>();
//             int selectedIndex = enumSelector.Index;

//             // Handle the "No Mod Settings Found" case
//             if (modIDs.Count == 0)
//             {
//                 foreach (GameObject setting in modSettings)
//                 {
//                     setting.SetActive(false);
//                 }
//                 return;
//             }

//             if (selectedIndex < 0 || selectedIndex >= modIDs.Count) return;

//             ShowSettingsForMod(modIDs[selectedIndex]);
//         }

//         public static void AddToggleSetting(string modId, string settingId, string displayName, bool defaultValue, UnityAction<bool> onChange)
//         {
//             if (!isInitialized)
//             {
//                 Plugin.Logger.LogInfo($"ModSettingsManager: Queueing toggle setting {modId}_{settingId}");
//                 queuedSettings.Add(new QueuedSetting
//                 {
//                     type = QueuedSetting.Type.Toggle,
//                     modId = modId,
//                     settingId = settingId,
//                     displayName = displayName,
//                     defaultValue = defaultValue,
//                     onChange = onChange
//                 });
//                 return;
//             }

//             AddToggleSettingInternal(modId, settingId, displayName, defaultValue, onChange);
//         }

//         public static void AddDropdownSetting(string modId, string settingId, string displayName, List<string> options, int defaultIndex, UnityAction<int> onChange)
//         {
//             if (!isInitialized)
//             {
//                 Plugin.Logger.LogInfo($"ModSettingsManager: Queueing dropdown setting {modId}_{settingId}");
//                 queuedSettings.Add(new QueuedSetting
//                 {
//                     type = QueuedSetting.Type.Dropdown,
//                     modId = modId,
//                     settingId = settingId,
//                     displayName = displayName,
//                     defaultValue = defaultIndex,
//                     options = options,
//                     onChange = onChange
//                 });
//                 return;
//             }

//             AddDropdownSettingInternal(modId, settingId, displayName, options, defaultIndex, onChange);
//         }

//         public static void AddSliderSetting(string modId, string settingId, string displayName, float minValue, float maxValue, float defaultValue, float increment, UnityAction<float> onChange)
//         {
//             if (!isInitialized)
//             {
//                 Plugin.Logger.LogInfo($"ModSettingsManager: Queueing slider setting {modId}_{settingId}");
//                 queuedSettings.Add(new QueuedSetting
//                 {
//                     type = QueuedSetting.Type.Slider,
//                     modId = modId,
//                     settingId = settingId,
//                     displayName = displayName,
//                     defaultValue = defaultValue,
//                     options = new Vector4(minValue, maxValue, increment, 0),
//                     onChange = onChange
//                 });
//                 return;
//             }

//             AddSliderSettingInternal(modId, settingId, displayName, minValue, maxValue, defaultValue, increment, onChange);
//         }

//         public static void AddControlSetting(string modId, string settingId, string displayName, ControlBinding defaultBinding, UnityAction<ControlBinding> onChange)
//         {
//             if (!isInitialized)
//             {
//                 queuedSettings.Add(new QueuedSetting
//                 {
//                     type = QueuedSetting.Type.Control,
//                     modId = modId,
//                     settingId = settingId,
//                     displayName = displayName,
//                     defaultValue = defaultBinding,
//                     onChange = onChange
//                 });
//                 return;
//             }

//             // Create a unique action ID for this control
//             string actionKey = $"{modId}_{settingId}";
//             if (!modActionIds.ContainsKey(actionKey))
//             {
//                 modActionIds[actionKey] = nextActionId++;
//             }
//             int actionId = modActionIds[actionKey];

//             // Create the Rewired Action if it doesn't exist
//             if (ReInput.mapping.GetAction(actionId) == null)
//             {
//                 // Get the User category ID (assuming it exists in the game)
//                 int categoryId = ReInput.mapping.GetActionCategoryId("Default");

//                 // Create the Action
//                 ReInput.mapping.AddAction(categoryId, actionId, displayName, true, true);
//             }

//             // Add default keyboard binding if one is specified
//             if (defaultBinding.keyboardKey != KeyCode.None)
//             {
//                 var keyboardMap = rewiredPlayer.controllers.maps.GetMap<KeyboardMap>(
//                     ControllerType.Keyboard,
//                     0, // layout id
//                     "Default" // category
//                 );

//                 if (keyboardMap != null)
//                 {
//                     keyboardMap.AddElementMap(actionId, defaultBinding.keyboardKey);
//                 }
//             }

//             // Add default mouse binding if one is specified
//             if (defaultBinding.mouseButton != -1)
//             {
//                 var mouseMap = rewiredPlayer.controllers.maps.GetMap<MouseMap>(
//                     ControllerType.Mouse,
//                     0, // layout id
//                     "Default" // category
//                 );

//                 if (mouseMap != null)
//                 {
//                     mouseMap.AddElementMap(actionId, defaultBinding.mouseButton);
//                 }
//             }

//             // Save the mappings
//             rewiredPlayer.controllers.maps.SaveAll();

//             // Add the control to the UI
//             AddControlToControlsTab(displayName, actionId, onChange);
//         }

//         private static void AddToggleSettingInternal(string modId, string settingId, string displayName, bool defaultValue, UnityAction<bool> onChange, bool updateSelector = true)
//         {
//             Plugin.Logger.LogInfo($"ModSettingsManager: Adding toggle setting {modId}_{settingId}");

//             if (settingTypes.Count < 2 || !settingTypes[1])
//             {
//                 Plugin.Logger.LogError("ModSettingsManager: Toggle template not found");
//                 return;
//             }

//             var newSetting = GameObject.Instantiate(settingTypes[1], modSelect.transform.parent);
//             newSetting.name = $"{modId}_{settingId}";
//             newSetting.GetComponent<TextMeshProUGUI>().text = displayName;

//             var toggle = newSetting.GetComponentInChildren<Checkbox>();
//             toggle.onToggle.AddListener(() => onChange(toggle.state));
//             toggle.SetState(defaultValue);

//             newSetting.SetActive(false);  // Start inactive until selected
//             modSettings.Add(newSetting);

//             if (!modIDs.Contains(modId))
//             {
//                 modIDs.Add(modId);
//                 if (updateSelector)
//                 {
//                     UpdateModSelector();
//                 }
//             }
//         }

//         private static void AddDropdownSettingInternal(string modId, string settingId, string displayName, List<string> options, int defaultIndex, UnityAction<int> onChange, bool updateSelector = true)
//         {
//             Plugin.Logger.LogInfo($"ModSettingsManager: Adding dropdown setting {modId}_{settingId}");

//             if (settingTypes.Count < 1 || !settingTypes[0])
//             {
//                 Plugin.Logger.LogError("ModSettingsManager: Dropdown template not found");
//                 return;
//             }

//             var newSetting = GameObject.Instantiate(settingTypes[0], modSelect.transform.parent);
//             newSetting.name = $"{modId}_{settingId}";
//             newSetting.GetComponent<TextMeshProUGUI>().text = displayName;

//             var selector = newSetting.GetComponent<EnumSelector>();
//             selector.options = options;
//             selector.SetIndex(defaultIndex);
//             selector.onChange.AddListener(() => onChange(selector.Index));

//             newSetting.SetActive(false);  // Start inactive until selected
//             modSettings.Add(newSetting);

//             if (!modIDs.Contains(modId))
//             {
//                 modIDs.Add(modId);
//                 if (updateSelector)
//                 {
//                     UpdateModSelector();
//                 }
//             }
//         }

//         private static void AddSliderSettingInternal(string modId, string settingId, string displayName, float minValue, float maxValue, float defaultValue, float increment, UnityAction<float> onChange, bool updateSelector = true)
//         {
//             Plugin.Logger.LogInfo($"ModSettingsManager: Adding slider setting {modId}_{settingId}");

//             if (settingTypes.Count < 3 || !settingTypes[2])
//             {
//                 Plugin.Logger.LogError("ModSettingsManager: Slider template not found");
//                 return;
//             }

//             var newSetting = GameObject.Instantiate(settingTypes[2], modSelect.transform.parent);
//             newSetting.name = $"{modId}_{settingId}";
//             newSetting.GetComponent<TextMeshProUGUI>().text = displayName;

//             var slider = newSetting.GetComponent<TFUISlider>();
//             slider.minValue = minValue;
//             slider.maxValue = maxValue;
//             slider.increments = increment;
//             slider.SetValue(defaultValue);
//             slider.onChange.AddListener(() => onChange(slider.value));

//             newSetting.SetActive(false);  // Start inactive until selected
//             modSettings.Add(newSetting);

//             if (!modIDs.Contains(modId))
//             {
//                 modIDs.Add(modId);
//                 if (updateSelector)
//                 {
//                     UpdateModSelector();
//                 }
//             }
//         }

//         private static void AddControlToControlsTab(string displayName, int actionId, UnityAction<ControlBinding> onChange)
//         {
//             var controlsTab = GameObject.Find("UI Canvas/Settings Frame/Control Settings");
//             if (controlsTab == null)
//             {
//                 throw new System.InvalidOperationException("Could not find Controls tab");
//             }

//             var layoutGroup = controlsTab.transform.Find("Scroll/Mask/Layout Group");
//             if (layoutGroup == null)
//             {
//                 var mask = controlsTab.transform.Find("Scroll/Mask");
//                 if (mask != null)
//                 {
//                     layoutGroup = mask.GetComponentInChildren<VerticalLayoutGroup>()?.transform;
//                 }
//             }

//             if (layoutGroup == null)
//             {
//                 throw new System.InvalidOperationException("Could not find Layout Group");
//             }

//             // Get the first Control Map Button as our template
//             Transform templateButton = null;
//             for (int i = 0; i < layoutGroup.childCount; i++)
//             {
//                 var child = layoutGroup.GetChild(i);
//                 if (child.name.StartsWith("Control Map Button") && !child.name.Contains("(Mod)"))
//                 {
//                     templateButton = child;
//                     break;
//                 }
//             }

//             if (templateButton == null)
//             {
//                 throw new System.InvalidOperationException("Could not find template Control Map Button");
//             }

//             // Create new control button
//             var newControl = GameObject.Instantiate(templateButton.gameObject, layoutGroup);
//             newControl.name = $"Control Map Button (Mod) {actionId}";
//             newControl.SetActive(true);

//             // Set the display name
//             var text = newControl.GetComponent<TextMeshProUGUI>();
//             if (text != null) text.text = displayName;

//             // Configure the ControlMapButton
//             var controlMapButton = newControl.GetComponent<ControlMapButton>();
//             if (controlMapButton != null)
//             {
//                 // Update the displays with current bindings
//                 UpdateControlDisplays(controlMapButton, actionId);

//                 // Add click listener to start rebinding
//                 var button = newControl.GetComponent<Button>();
//                 if (button != null)
//                 {
//                     button.onClick.AddListener(() => StartRebinding(controlMapButton, actionId, onChange));
//                 }
//             }

//             // Force layout refresh
//             LayoutRebuilder.ForceRebuildLayoutImmediate(layoutGroup as RectTransform);

//             // Ensure parent layouts are updated
//             var parentLayouts = layoutGroup.GetComponentsInParent<LayoutGroup>();
//             foreach (var layout in parentLayouts)
//             {
//                 LayoutRebuilder.ForceRebuildLayoutImmediate(layout.transform as RectTransform);
//             }
//         }

//         private static void UpdateControlDisplays(ControlMapButton button, int actionId)
//         {
//             // Get all bindings for this action
//             var keyboard = rewiredPlayer.controllers.maps.GetFirstKeyboardMapWithAction(actionId);
//             var mouse = rewiredPlayer.controllers.maps.GetFirstMouseMapWithAction(actionId);
//             var gamepad = rewiredPlayer.controllers.maps.GetFirstJoystickMapWithAction(actionId);

//             // Update keyboard display
//             if (button.displayKeyboard != null)
//             {
//                 if (keyboard != null)
//                 {
//                     var actionElementMap = keyboard.GetElementMapsWithAction(actionId).FirstOrDefault();
//                     if (actionElementMap != null)
//                     {
//                         button.displayKeyboard.text = actionElementMap.keyCode.ToString();
//                     }
//                     else
//                     {
//                         button.displayKeyboard.text = "﹘";
//                     }
//                 }
//                 else
//                 {
//                     button.displayKeyboard.text = "﹘";
//                 }
//             }

//             // Update mouse display
//             if (button.displayMouse != null)
//             {
//                 if (mouse != null)
//                 {
//                     var actionElementMap = mouse.GetElementMapsWithAction(actionId).FirstOrDefault();
//                     if (actionElementMap != null)
//                     {
//                         button.displayMouse.text = $"Mouse {actionElementMap.elementIndex}";
//                     }
//                     else
//                     {
//                         button.displayMouse.text = "﹘";
//                     }
//                 }
//                 else
//                 {
//                     button.displayMouse.text = "﹘";
//                 }
//             }

//             // Update controller display
//             if (button.displayController != null)
//             {
//                 if (gamepad != null)
//                 {
//                     var actionElementMap = gamepad.GetElementMapsWithAction(actionId).FirstOrDefault();
//                     if (actionElementMap != null)
//                     {
//                         button.displayController.text = $"Button {actionElementMap.elementIndex}";
//                     }
//                     else
//                     {
//                         button.displayController.text = "﹘";
//                     }
//                 }
//                 else
//                 {
//                     button.displayController.text = "﹘";
//                 }
//             }
//         }

//         private static void StartRebinding(ControlMapButton button, int actionId, UnityAction<ControlBinding> onChange)
//         {
//             // Create a GameObject to handle input listening
//             var listener = new GameObject("InputListener");
//             var inputHandler = listener.AddComponent<InputListener>();
//             inputHandler.Initialize(button, actionId, onChange);
//         }

//         private class InputListener : MonoBehaviour
//         {
//             private ControlMapButton button;
//             private int actionId;
//             private UnityAction<ControlBinding> onChange;
//             private bool isListening = true;
//             private float timeoutDuration = 5f;
//             private float timer = 0f;

//             public void Initialize(ControlMapButton targetButton, int targetActionId, UnityAction<ControlBinding> callback)
//             {
//                 button = targetButton;
//                 actionId = targetActionId;
//                 onChange = callback;

//                 // Show "Press any key..." message
//                 if (button.displayKeyboard != null) button.displayKeyboard.text = "Press any key...";
//                 if (button.displayMouse != null) button.displayMouse.text = "﹘";
//                 if (button.displayController != null) button.displayController.text = "﹘";
//             }

//             private void Update()
//             {
//                 if (!isListening) return;

//                 timer += Time.deltaTime;
//                 if (timer >= timeoutDuration)
//                 {
//                     Destroy(gameObject);
//                     return;
//                 }

//                 // Check for keyboard input
//                 if (Input.anyKeyDown)
//                 {
//                     foreach (KeyCode key in System.Enum.GetValues(typeof(KeyCode)))
//                     {
//                         if (Input.GetKeyDown(key))
//                         {
//                             if (key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
//                                 key == KeyCode.LeftControl || key == KeyCode.RightControl ||
//                                 key == KeyCode.LeftShift || key == KeyCode.RightShift)
//                                 continue;

//                             if (key == KeyCode.Escape)
//                             {
//                                 Destroy(gameObject);
//                                 return;
//                             }

//                             // Clear existing keyboard bindings
//                             var keyboardMap = rewiredPlayer.controllers.maps.GetFirstKeyboardMapWithAction(actionId);
//                             if (keyboardMap != null)
//                             {
//                                 keyboardMap.DeleteElementMapsWithAction(actionId);
//                                 keyboardMap.AddElementMap(actionId, key);
//                             }

//                             // Update the control binding
//                             var newBinding = new ControlBinding
//                             {
//                                 keyboardKey = key,
//                                 mouseButton = -1,
//                                 controllerButton = -1
//                             };

//                             // Save all controller maps
//                             rewiredPlayer.controllers.maps.SaveAll();

//                             // Update displays
//                             UpdateControlDisplays(button, actionId);

//                             // Notify of change
//                             onChange(newBinding);

//                             Destroy(gameObject);
//                             return;
//                         }
//                     }
//                 }

//                 // Check for mouse input
//                 for (int i = 0; i < 3; i++)
//                 {
//                     if (Input.GetMouseButtonDown(i))
//                     {
//                         // Clear existing mouse bindings
//                         var mouseMap = rewiredPlayer.controllers.maps.GetFirstMouseMapWithAction(actionId);
//                         if (mouseMap != null)
//                         {
//                             mouseMap.DeleteElementMapsWithAction(actionId);
//                             mouseMap.AddElementMap(actionId, i);
//                         }

//                         // Update the control binding
//                         var newBinding = new ControlBinding
//                         {
//                             keyboardKey = KeyCode.None,
//                             mouseButton = i,
//                             controllerButton = -1
//                         };

//                         // Save all controller maps
//                         rewiredPlayer.controllers.maps.SaveAll();

//                         // Update displays
//                         UpdateControlDisplays(button, actionId);

//                         // Notify of change
//                         onChange(newBinding);

//                         Destroy(gameObject);
//                         return;
//                     }
//                 }
//             }
//         }

//         private static void ProcessQueuedControlSettings()
//         {
//             if (queuedSettings.Count == 0) return;

//             var controlSettings = queuedSettings.Where(s => s.type == QueuedSetting.Type.Control).ToList();
//             if (controlSettings.Count == 0) return;

//             foreach (var setting in controlSettings)
//             {
//                 try
//                 {
//                     AddControlSetting(
//                         setting.modId,
//                         setting.settingId,
//                         setting.displayName,
//                         (ControlBinding)setting.defaultValue,
//                         (UnityAction<ControlBinding>)setting.onChange
//                     );
//                     queuedSettings.Remove(setting);
//                 }
//                 catch (System.Exception e)
//                 {
//                     Plugin.Logger.LogError($"Error adding control setting {setting.displayName}: {e}");
//                 }
//             }
//         }
//     }
// }

// using UnityEngine;
// using System.Linq;
// using System.Reflection;
// using System.Collections.Generic;

// namespace PalaceMod
// {
//     public class TestManager : BaseManager
//     {
//         private HashSet<int> examinedObjects = new HashSet<int>();
//         private const int MAX_DEPTH = 10;
//         private Dictionary<string, (int count, float hp)> uniqueUnits = new Dictionary<string, (int count, float hp)>();

//         public override void Awake(Plugin plugin)
//         {
//             base.Awake(plugin);

//             Plugin.Logger.LogInfo("=== Examining Berserk Unit Properties ===");
//             var allObjects = Resources.LoadAll<GameObject>("");
//             bool found = false;
//             foreach (var obj in allObjects)
//             {
//                 if (obj != null && !found)
//                 {
//                     examinedObjects.Clear();
//                     found = ExamineBerserkUnit(obj);
//                 }
//             }
//             Plugin.Logger.LogInfo("================================");
//         }

//         private bool ExamineBerserkUnit(GameObject obj)
//         {
//             if (obj == null) return false;

//             // Check if this is a Berserk unit
//             if (obj.name == "P Berserk")
//             {
//                 Plugin.Logger.LogInfo($"\nBerserk Unit Basic Properties:");
//                 Plugin.Logger.LogInfo($"Name: {obj.name}");
//                 Plugin.Logger.LogInfo($"Layer: {obj.layer} ({LayerMask.LayerToName(obj.layer)})");
//                 Plugin.Logger.LogInfo($"Tag: {obj.tag}");
//                 Plugin.Logger.LogInfo($"Active: {obj.activeSelf}");
//                 Plugin.Logger.LogInfo($"Static: {obj.isStatic}");
//                 Plugin.Logger.LogInfo($"Position: {obj.transform.position}");
//                 Plugin.Logger.LogInfo($"Rotation: {obj.transform.rotation.eulerAngles}");
//                 Plugin.Logger.LogInfo($"Scale: {obj.transform.localScale}");

//                 Plugin.Logger.LogInfo($"\nComponents:");
//                 var components = obj.GetComponents<Component>();
//                 foreach (var component in components)
//                 {
//                     if (component == null) continue;

//                     Plugin.Logger.LogInfo($"\nComponent: {component.GetType().Name}");

//                     // Special handling for TaggedObject
//                     if (component is TaggedObject tagged)
//                     {
//                         Plugin.Logger.LogInfo($"  Tags: {string.Join(", ", tagged.Tags)}");
//                     }
//                     // Special handling for Hp
//                     else if (component is Hp hp)
//                     {
//                         Plugin.Logger.LogInfo($"  Current HP: {hp.HpValue}");
//                         Plugin.Logger.LogInfo($"  Max HP: {hp.maxHp}");
//                     }
//                     // Special handling for AutoAttack if it exists
//                     else if (component is AutoAttack autoAttack)
//                     {
//                         Plugin.Logger.LogInfo($"  AutoAttack Properties:");
//                         foreach (var field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
//                         {
//                             try
//                             {
//                                 var value = field.GetValue(component);
//                                 Plugin.Logger.LogInfo($"    {field.Name}: {value}");
//                             }
//                             catch { }
//                         }
//                     }

//                     // Get all public fields
//                     foreach (var field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
//                     {
//                         try
//                         {
//                             var value = field.GetValue(component);
//                             Plugin.Logger.LogInfo($"  Field: {field.Name} = {value}");
//                         }
//                         catch { }
//                     }

//                     // Get all public properties
//                     foreach (var prop in component.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
//                     {
//                         try
//                         {
//                             if (prop.CanRead && prop.GetMethod.GetParameters().Length == 0)
//                             {
//                                 var value = prop.GetValue(component);
//                                 Plugin.Logger.LogInfo($"  Property: {prop.Name} = {value}");
//                             }
//                         }
//                         catch { }
//                     }
//                 }

//                 Plugin.Logger.LogInfo($"\nChild Objects:");
//                 foreach (Transform child in obj.transform)
//                 {
//                     Plugin.Logger.LogInfo($"\nChild: {child.gameObject.name}");
//                     var childComponents = child.GetComponents<Component>();
//                     foreach (var component in childComponents)
//                     {
//                         if (component == null) continue;
//                         Plugin.Logger.LogInfo($"  Component: {component.GetType().Name}");
//                     }
//                 }

//                 return true;
//             }

//             // Check children
//             foreach (Transform child in obj.transform)
//             {
//                 if (ExamineBerserkUnit(child.gameObject)) return true;
//             }

//             return false;
//         }

//         private void SearchForPlayerUnits(GameObject obj, string parentPath)
//         {
//             if (obj == null) return;

//             string currentPath = parentPath + "/" + obj.name;

//             int instanceId = obj.GetInstanceID();
//             if (!examinedObjects.Add(instanceId)) return;

//             // Check this object
//             var taggedObj = obj.GetComponent<TaggedObject>();
//             if (taggedObj != null && taggedObj.Tags != null && taggedObj.Tags.Contains(TagManager.ETag.PlayerUnit))
//             {
//                 var hp = obj.GetComponent<Hp>();
//                 string unitType = currentPath.Split('/').Skip(1).Take(2).Aggregate((a, b) => $"{a}/{b}");
//                 if (!uniqueUnits.ContainsKey(unitType))
//                 {
//                     uniqueUnits[unitType] = (1, hp != null ? hp.maxHp : 0);
//                 }
//                 else
//                 {
//                     var current = uniqueUnits[unitType];
//                     uniqueUnits[unitType] = (current.count + 1, current.hp);
//                 }
//             }

//             // Check children
//             foreach (Transform child in obj.transform)
//             {
//                 SearchForPlayerUnits(child.gameObject, currentPath);
//             }
//         }

//         private void ExamineGameObjectRecursively(GameObject obj, int depth)
//         {
//             if (depth > MAX_DEPTH) return;

//             int instanceId = obj.GetInstanceID();
//             if (!examinedObjects.Add(instanceId)) return;

//             string indent = new string(' ', depth * 2);

//             // Only log if it contains "Berserk"
//             if (obj.name.Contains("Berserk"))
//             {
//                 Plugin.Logger.LogInfo($"{indent}Found Berserk object: {obj.name}");

//                 // Log components and their fields/properties
//                 var components = obj.GetComponents<Component>();
//                 foreach (var component in components)
//                 {
//                     if (component == null) continue;

//                     Plugin.Logger.LogInfo($"{indent}Component: {component.GetType().Name}");

//                     // Special handling for TaggedObject
//                     if (component is TaggedObject tagged)
//                     {
//                         Plugin.Logger.LogInfo($"{indent}  Tags: {string.Join(", ", tagged.Tags)}");
//                     }

//                     // Special handling for important components
//                     if (component is Hp hp)
//                     {
//                         Plugin.Logger.LogInfo($"{indent}  MaxHP: {hp.maxHp}");
//                     }
//                     else if (component is AutoAttack autoAttack)
//                     {
//                         Plugin.Logger.LogInfo($"{indent}  AutoAttack component found");
//                     }
//                 }
//             }

//             // Continue examining children even if this object wasn't logged
//             foreach (Transform child in obj.transform)
//             {
//                 ExamineGameObjectRecursively(child.gameObject, depth + 1);
//             }
//         }

//         // Keep existing ExamineGameObject method for later
//         private bool ExamineGameObject(GameObject obj)
//         {
//             if (obj.name.Contains("P Berserk"))
//             {
//                 Plugin.Logger.LogInfo($"Found P Berserk prefab: {obj.name}");
//                 Plugin.Logger.LogInfo($"Layer: {obj.layer}");
//                 Plugin.Logger.LogInfo($"Tag: {obj.tag}");
//                 Plugin.Logger.LogInfo($"Active: {obj.activeSelf}");
//                 Plugin.Logger.LogInfo($"Static: {obj.isStatic}");
//                 Plugin.Logger.LogInfo($"Position: {obj.transform.position}");
//                 Plugin.Logger.LogInfo($"Rotation: {obj.transform.rotation}");
//                 Plugin.Logger.LogInfo($"Scale: {obj.transform.localScale}");

//                 // Log ALL components and their public fields/properties
//                 var components = obj.GetComponents<Component>();
//                 foreach (var component in components)
//                 {
//                     if (component == null) continue;

//                     Plugin.Logger.LogInfo($"\nComponent: {component.GetType().Name}");

//                     // Get all public fields
//                     foreach (var field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
//                     {
//                         try
//                         {
//                             var value = field.GetValue(component);
//                             Plugin.Logger.LogInfo($"  Field: {field.Name} = {value}");
//                         }
//                         catch { }
//                     }

//                     // Get all public properties
//                     foreach (var prop in component.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
//                     {
//                         try
//                         {
//                             if (prop.CanRead && prop.GetMethod.GetParameters().Length == 0)
//                             {
//                                 var value = prop.GetValue(component);
//                                 Plugin.Logger.LogInfo($"  Property: {prop.Name} = {value}");
//                             }
//                         }
//                         catch { }
//                     }

//                     // Special handling for TaggedObject
//                     if (component is TaggedObject tagged)
//                     {
//                         Plugin.Logger.LogInfo($"  Tags: {string.Join(", ", tagged.Tags)}");
//                     }
//                 }

//                 // Also examine children
//                 foreach (Transform child in obj.transform)
//                 {
//                     Plugin.Logger.LogInfo($"\nChild GameObject: {child.gameObject.name}");
//                     var childComponents = child.GetComponents<Component>();
//                     foreach (var component in childComponents)
//                     {
//                         if (component == null) continue;
//                         Plugin.Logger.LogInfo($"  Component: {component.GetType().Name}");
//                     }
//                 }

//                 return true; // Stop searching
//             }

//             // Check children too
//             foreach (Transform child in obj.transform)
//             {
//                 if (ExamineGameObject(child.gameObject)) return true;
//             }

//             return false;
//         }

//         private void LogUnitHealth()
//         {
//             if (CommandUnits.instance == null) return;

//             var units = CommandUnits.instance.PlayerUnitsCommanding;
//             if (units == null || units.Count == 0)
//             {
//                 Plugin.Logger.LogInfo("No active units found");
//                 return;
//             }

//             Plugin.Logger.LogInfo($"=== Active Units Health Report ===");
//             foreach (var unit in units.Where(u => u != null))
//             {
//                 Plugin.Logger.LogInfo($"Unit: {unit.name} - HP: {unit.hp.HpValue}/{unit.hp.maxHp}");
//             }
//             Plugin.Logger.LogInfo("================================");
//         }

//         public override void Update()
//         {
//             // Log health every second
//             if (Time.frameCount % 60 == 0)
//             {
//                 // LogUnitHealth();
//             }
//         }
//     }
// }

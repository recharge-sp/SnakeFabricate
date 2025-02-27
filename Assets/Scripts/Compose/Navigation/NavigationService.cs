using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ArcCreate.Compose.Components;
using ArcCreate.Utility;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using YamlDotNet.RepresentationModel;

namespace ArcCreate.Compose.Navigation
{
    [EditorScope("Navigation")]
    public class NavigationService : MonoBehaviour, INavigationService
    {
        private readonly List<IAction> allActions = new List<IAction>();
        private readonly List<Keybind> keybinds = new List<Keybind>();
        private readonly Dictionary<Type, object> instances = new Dictionary<Type, object>();
        private readonly List<Keybind> activatingKeybinds = new List<Keybind>();
        private readonly List<Keybind> inProgressKeybinds = new List<Keybind>();

        // Last action in the list is considered top-priority, and only it will have sub-actions processed.
        // A stack was not used because lower-priority actions might exit early.
        private readonly List<EditorAction> actionsInProgress = new List<EditorAction>();

        public string ConfigFilePath
        {
            get
            {
                string path = Path.Combine(Application.streamingAssetsPath, Values.KeybindSettingsFileName + ".yaml");
                if (!File.Exists(path))
                {
                    path = Path.Combine(Application.streamingAssetsPath, Values.KeybindSettingsFileName + ".yml");
                }

                return path;
            }
        }

        public void ReloadHotkeys()
        {
            allActions.Clear();
            keybinds.Clear();
            activatingKeybinds.Clear();
            inProgressKeybinds.Clear();

            Dictionary<string, List<string>> keybindOverrides = new Dictionary<string, List<string>>();
            Dictionary<string, List<string>> keybindActions = new Dictionary<string, List<string>>();

            string configPath = ConfigFilePath;
            if (File.Exists(configPath))
            {
                using (FileStream stream = File.OpenRead(configPath))
                {
                    YamlStream yaml = new YamlStream();
                    yaml.Load(new StreamReader(stream));

                    if (yaml.Documents.Count >= 1)
                    {
                        var mapping = (YamlMappingNode)yaml.Documents[0].RootNode;

                        foreach (KeyValuePair<YamlNode, YamlNode> child in mapping.Children)
                        {
                            string nodeKey = (child.Key as YamlScalarNode).Value;
                            YamlNode val = child.Value;
                            if (!(val is YamlMappingNode valueNode))
                            {
                                continue;
                            }

                            if (nodeKey == "Override")
                            {
                                YamlExtractor.ExtractListsTo(keybindOverrides, valueNode, "");
                            }
                            else if (nodeKey == "Action")
                            {
                                YamlExtractor.ExtractListsTo(keybindActions, valueNode, "");
                            }
                        }
                    }
                }
            }

            RegisterMethods(keybindOverrides);
            RegisterCompositeActions(keybindActions);
        }

        public void StartAction(EditorAction action)
        {
            ExecuteActionTask(action).Forget();
        }

        public void StartActionsInSequence(List<IAction> actions)
        {
            ExecuteActionListTask(actions).Forget();
        }

        public void StartAction(string fullPath)
        {
            foreach (IAction action in allActions)
            {
                if (action.FullPath == fullPath)
                {
                    action.Execute();
                    return;
                }
            }

            Services.Macros.RunMacro(fullPath);
        }

        public List<IAction> GetContextMenuEntries(bool calledByAction = false)
        {
            if (calledByAction)
            {
                EditorAction caller = actionsInProgress[actionsInProgress.Count - 1];
                actionsInProgress.RemoveAt(actionsInProgress.Count - 1);
                List<IAction> result = allActions
                    .Where(action => ShouldExecute(action) && action.ShouldDisplayOnContextMenu)
                    .ToList();
                actionsInProgress.Add(caller);
                return result;
            }

            return allActions.Where(ShouldExecute).ToList();
        }

        public bool ShouldExecute(IAction action)
        {
            if (EventSystem.current.currentSelectedGameObject != null)
            {
                if (EventSystem.current.currentSelectedGameObject.GetComponent<TMP_InputField>() != null)
                {
                    return false;
                }
            }

            if (Dialog.IsAnyOpen)
            {
                return false;
            }

            EditorAction currentAction = null;
            if (actionsInProgress.Count != 0)
            {
                currentAction = actionsInProgress[actionsInProgress.Count - 1];
            }

            switch (action)
            {
                case EditorAction editorAction:
                    bool whitelisted = true;
                    if (currentAction != null
                    && !currentAction.WhitelistAll
                    && !currentAction.Whitelist.Contains(editorAction.Scope.Type))
                    {
                        whitelisted = false;
                    }

                    return whitelisted && editorAction.CheckRequirement();
                case SubAction subAction:
                    if (currentAction == null)
                    {
                        return false;
                    }

                    return currentAction.SubActions.Contains(subAction);
                case CompositeAction compositeAction:
                    return true;
                case MacroAction macroAction:
                    return true;
            }

            return false;
        }

        [EditorAction("Cancel", false, "<esc>")]
        private void CancelOngoingKeybinds()
        {
            foreach (var keybind in keybinds)
            {
                keybind.Reset();
            }
        }

        private void Awake()
        {
            ReloadHotkeys();
        }

        private void Update()
        {
            CheckKeybind();
        }

        private void OnDestroy()
        {
            keybinds.Clear();
        }

        private void RegisterMethods(Dictionary<string, List<string>> keybindOverrides)
        {
            IEnumerable<Type> types = Assembly.GetExecutingAssembly().GetTypes()
                .Where(type => type.IsDefined(typeof(EditorScopeAttribute)));

            foreach (Type type in types)
            {
                string scopeId = type.GetCustomAttribute<EditorScopeAttribute>().Id ?? type.Name;

                object instance;
                if (instances.TryGetValue(type, out object typeInstance))
                {
                    instance = typeInstance;
                }
                else if (type.IsSubclassOf(typeof(Component)))
                {
                    instance = FindComponent(type);
                    instances.Add(type, instance);
                }
                else
                {
                    instance = Activator.CreateInstance(type);
                    instances.Add(type, instance);
                }

                if (instance == null)
                {
                    throw new Exception($"Can not get an instance for type {type}");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    EditorActionAttribute editorAction = method.GetCustomAttribute(typeof(EditorActionAttribute)) as EditorActionAttribute;
                    if (editorAction == null)
                    {
                        continue;
                    }

                    string actionId = editorAction.Id ?? method.Name;
                    ContextRequirementAttribute[] contextRequirements = method.GetCustomAttributes(typeof(ContextRequirementAttribute)) as ContextRequirementAttribute[];
                    SubActionAttribute[] subActions = method.GetCustomAttributes(typeof(SubActionAttribute)) as SubActionAttribute[];
                    WhitelistScopesAttribute whitelist = method.GetCustomAttribute(typeof(WhitelistScopesAttribute)) as WhitelistScopesAttribute;

                    // Resolve subactions
                    List<SubAction> subActionInstances = new List<SubAction>();
                    if (subActions != null)
                    {
                        foreach (SubActionAttribute s in subActions)
                        {
                            SubAction subAction = new SubAction(s.Id, scopeId, actionId, s.ShouldDisplayOnContextMenu);
                            IEnumerable<string> keybindStrings = s.DefaultHotkeys;
                            if (keybindOverrides.TryGetValue(subAction.FullPath, out List<string> keybindOverride))
                            {
                                keybindStrings = keybindOverride;
                                Debug.Log(I18n.S("Compose.Navigation.KeybindOverride", subAction.FullPath));
                            }

                            foreach (string keybindString in keybindStrings)
                            {
                                if (string.IsNullOrEmpty(keybindString))
                                {
                                    continue;
                                }

                                if (KeybindUtils.TryParseKeybind(keybindString, subAction, out Keybind keybind, out string reason))
                                {
                                    keybinds.Add(keybind);
                                }
                                else
                                {
                                    Debug.LogWarning(reason);
                                }
                            }

                            subActionInstances.Add(subAction);
                            allActions.Add(subAction);
                        }
                    }

                    EditorAction action = new EditorAction(
                        id: actionId,
                        shouldDisplayOnContextMenu: editorAction.ShouldDisplayOnContextMenu,
                        contextRequirements: contextRequirements?.Cast<IContextRequirement>().ToList() ?? new List<IContextRequirement>(),
                        whitelist: whitelist?.Scopes.ToList() ?? new List<Type>(),
                        whitelistAll: whitelist?.All ?? false,
                        scope: new EditorScope(type, scopeId, instance),
                        method: method,
                        subActions: subActionInstances);

                    action.Whitelist.Add(GetType());

                    // Resolve keybinds
                    IEnumerable<string> actionKeybindStrings = editorAction.DefaultHotkeys;
                    if (keybindOverrides.TryGetValue(action.FullPath, out List<string> actionKeybindOverride))
                    {
                        actionKeybindStrings = actionKeybindOverride;
                        Debug.Log(I18n.S("Compose.Navigation.KeybindOverride", action.FullPath));
                    }

                    foreach (string keybindString in actionKeybindStrings)
                    {
                        if (string.IsNullOrEmpty(keybindString))
                        {
                            continue;
                        }

                        if (KeybindUtils.TryParseKeybind(keybindString, action, out Keybind keybind, out string reason))
                        {
                            keybinds.Add(keybind);
                        }
                        else
                        {
                            Debug.LogWarning(reason);
                        }
                    }

                    allActions.Add(action);
                }
            }
        }

        private void RegisterCompositeActions(Dictionary<string, List<string>> keybindActions)
        {
            foreach (KeyValuePair<string, List<string>> pair in keybindActions)
            {
                string keybindString = pair.Key;
                List<string> actionPaths = pair.Value;

                List<IAction> actions = new List<IAction>();
                foreach (string actionPath in actionPaths)
                {
                    bool found = false;
                    foreach (IAction registeredAction in allActions)
                    {
                        if (registeredAction.FullPath == actionPath)
                        {
                            actions.Add(registeredAction);
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        actions.Add(new MacroAction(actionPath));
                    }
                }

                CompositeAction action = new CompositeAction(keybindString, actions);
                if (string.IsNullOrEmpty(keybindString))
                {
                    continue;
                }

                if (KeybindUtils.TryParseKeybind(keybindString, action, out Keybind keybind, out string reason))
                {
                    keybinds.Add(keybind);
                }
                else
                {
                    Debug.LogWarning(reason);
                }
            }
        }

        private void CheckKeybind()
        {
            activatingKeybinds.Clear();
            inProgressKeybinds.Clear();
            for (int i = 0; i < keybinds.Count; i++)
            {
                Keybind keybind = keybinds[i];
                KeybindState state = keybind.CheckKeybind();
                switch (state)
                {
                    case KeybindState.Complete:
                        activatingKeybinds.Add(keybind);
                        break;
                    case KeybindState.InProgress:
                        inProgressKeybinds.Add(keybind);
                        break;
                }
            }

            for (int i = 0; i < activatingKeybinds.Count; i++)
            {
                Keybind keybind = activatingKeybinds[i];
                Keystroke lastKeystroke = keybind.Keystrokes[keybind.Keystrokes.Length - 1];
                int complexity = lastKeystroke.Modifiers.Length;
                KeyCode key = lastKeystroke.Key;
                bool isMaxComplexity = true;
                bool hasStartedLate = false;

                for (int j = 0; j < activatingKeybinds.Count; j++)
                {
                    Keybind otherKeybind = activatingKeybinds[j];
                    Keystroke otherLastKeystroke = otherKeybind.Keystrokes[otherKeybind.Keystrokes.Length - 1];
                    if (i == j)
                    {
                        continue;
                    }

                    if (otherLastKeystroke.Modifiers.Contains(key))
                    {
                        isMaxComplexity = false;
                    }

                    if (lastKeystroke.Key != otherLastKeystroke.Key)
                    {
                        continue;
                    }

                    int otherComplexity = otherLastKeystroke.Modifiers.Length;
                    if (complexity < otherComplexity)
                    {
                        isMaxComplexity = false;
                    }
                }

                for (int j = 0; j < inProgressKeybinds.Count; j++)
                {
                    Keybind otherKeybind = inProgressKeybinds[j];
                    Keystroke otherCurrentKeystroke = otherKeybind.Keystrokes[otherKeybind.CurrentIndex - 1];
                    if (otherCurrentKeystroke.Modifiers.Contains(key))
                    {
                        isMaxComplexity = false;
                    }

                    if (lastKeystroke.Key != otherCurrentKeystroke.Key)
                    {
                        continue;
                    }

                    int otherComplexity = otherCurrentKeystroke.Modifiers.Length;
                    if (complexity < otherComplexity)
                    {
                        isMaxComplexity = false;
                    }

                    if (keybind.Keystrokes.Length < otherKeybind.CurrentIndex)
                    {
                        hasStartedLate = true;
                    }
                }

                if (isMaxComplexity && !hasStartedLate && ShouldExecute(keybind.Action))
                {
                    keybind.Action.Execute();
                }
            }
        }

        private async UniTask ExecuteActionTask(EditorAction action)
        {
            // Ensure all keybinds that can trigger will do so first before resetting the rest
            await UniTask.WaitForEndOfFrame(this);
            CancelOngoingKeybinds();

            actionsInProgress.Add(action);

            // Without try-catch the entire navigation system stops working when any exception is thrown
            try
            {
                // This causes boxing but what else can you do
                object obj = action.Method.Invoke(action.Scope.Instance, action.ParamsToPass);
                if (obj is UniTask task)
                {
                    await task;
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }

            actionsInProgress.Remove(action);
        }

        private async UniTask ExecuteActionListTask(List<IAction> actions)
        {
            foreach (IAction action in actions)
            {
                if (!ShouldExecute(action))
                {
                    return;
                }

                if (action is EditorAction editorAction)
                {
                    await ExecuteActionTask(editorAction);
                }
                else
                {
                    action.Execute();
                }
            }
        }

        private object FindComponent(Type type)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                var allGameObjects = s.GetRootGameObjects();
                for (int j = 0; j < allGameObjects.Length; j++)
                {
                    var go = allGameObjects[j];
                    object comp = go.GetComponentInChildren(type, true);
                    if (comp != null)
                    {
                        return comp;
                    }
                }
            }

            return null;
        }
    }
}
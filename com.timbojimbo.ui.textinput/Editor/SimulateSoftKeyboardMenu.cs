using TimboJimbo.UI.TextInput;
using UnityEditor;

namespace TimboJimboEditor.UI.TextInput
{
    /// <summary>
    /// Timbo Jimbo > UI > Simulate Soft Keyboard: turns <see cref="TextInputSystem.SimulateSoftKeyboard"/> on and off,
    /// so that layouts that make room for a phone's keyboard can be tried in the editor, where there is none. The choice
    /// is kept in the editor's preferences and handed to the system after each domain reload and as play mode starts
    /// (which, with domain reload on, resets the system's statics).
    /// </summary>
    internal static class SimulateSoftKeyboardMenu
    {
        private const string MenuPath = "Timbo Jimbo/UI/Simulate Soft Keyboard";
        private const string PrefKey = "TimboJimbo.UI.TextInput.SimulateSoftKeyboard";

        private static bool Simulated
        {
            get => EditorPrefs.GetBool(PrefKey, false);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        [InitializeOnLoadMethod]
        private static void OnLoad()
        {
            Apply();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
                Apply();
        }

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            Simulated = !Simulated;
            Apply();
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Simulated);
            return true;
        }

        private static void Apply() => TextInputSystem.SimulateSoftKeyboard = Simulated;
    }
}

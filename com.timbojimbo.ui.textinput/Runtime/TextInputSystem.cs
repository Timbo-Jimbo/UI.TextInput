using System;
using System.Collections.Generic;
using TimboJimbo.UI.Layout;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// Connects one thing being edited (an <see cref="ITextInputClient"/>, such as a <see cref="TextField"/>) to the
    /// platform's own keyboard, as browsers and Flutter do: the field draws the text, and a small native input client
    /// (a hidden UITextInput view on iOS, an InputConnection on Android) is what the keyboard types into, so autocorrect,
    /// suggestions, composition (Chinese, Japanese, Korean), dictation and the system's edit menu all work. On desktop and
    /// in the editor, typing comes through the Input System and the IME.
    /// <para>
    /// The client owns the value; the native side keeps a mirror of it to answer the keyboard's questions. What the
    /// keyboard changes is handed to the client (<see cref="ITextInputClient.ApplyPlatformValue"/>) once a frame, early
    /// in it; what the client changes itself it reports with <see cref="NotifyValueChanged"/>, and the system pushes it to
    /// the mirror only if it differs from what the keyboard last saw, never echoing the keyboard's own edits back.
    /// </para>
    /// <para>
    /// It also tracks the software keyboard: <see cref="KeyboardHeight"/> follows it frame by frame as it slides, and is
    /// handed to layout (<see cref="LayoutSystem.KeyboardHeight"/>), where it is part of the safe area, as SwiftUI's
    /// keyboard safe area is: a screen's content rises above the keyboard while its background reaches under it.
    /// </para>
    /// </summary>
    public static class TextInputSystem
    {
        // How much of the screen the simulated keyboard covers, as a phone's does upright.
        private const float SimulatedKeyboardPart = 0.38f;
        private const float SimulatedKeyboardTime = 0.12f;

        private static ITextInputBackend s_backend;
        private static ITextInputClient s_client;

        // The session under way (each Begin starts a new one), the serial of the last value pushed in it, and what the
        // platform's keyboard last saw: the last value pushed, or the last one it reported.
        private static int s_session;
        private static int s_serial;
        private static TextEditingValue s_remote;
        private static TextInputConfig s_remoteConfig;

        // The geometry last sent, so it is sent only when it changes.
        private static bool s_geometrySent;
        private static Rect s_field;
        private static Rect s_caret;
        private static Rect s_composing;

        private static float s_keyboardHeight;
        private static bool s_keyboardVisible;
        private static float s_simulated;
        private static float s_simulatedVelocity;

        private static bool s_hooked;
        private static bool s_lateHooked;
        private static readonly List<TextInputEvent> s_events = new();

        /// <summary>What is being edited, or null.</summary>
        public static ITextInputClient Client => s_client;

        /// <summary>Whether something is being edited. Game code can turn its own key bindings off meanwhile.</summary>
        public static bool IsEditing => s_client != null;

        /// <summary>Raised when what is being edited changes (null when editing ends).</summary>
        public static event Action<ITextInputClient> ClientChanged;

        /// <summary>
        /// How far up from the bottom of the screen the software keyboard covers it, in screen pixels (as
        /// <see cref="Screen.safeArea"/>), following it as it slides. Only a keyboard docked at the bottom counts; a
        /// floating or split one covers nothing, as UIKit's keyboard layout guide has it.
        /// </summary>
        public static float KeyboardHeight => s_keyboardHeight;

        /// <summary>Whether a software keyboard is up, docked or not.</summary>
        public static bool KeyboardVisible => s_keyboardVisible;

        /// <summary>Raised on each frame the keyboard's height or visibility changes.</summary>
        public static event Action KeyboardChanged;

        /// <summary>
        /// Where there is no software keyboard (the editor, desktop, the Device Simulator), pretends one slides up while
        /// something is being edited, so that layouts that make room for it can be tried. Off by default; the editor's
        /// Timbo Jimbo menu turns it on.
        /// </summary>
        public static bool SimulateSoftKeyboard { get; set; }

        /// <summary>Whether the platform shows its own edit menu (copy, paste) through <see cref="ShowEditMenu"/>.</summary>
        public static bool SupportsEditMenu => Backend.SupportsEditMenu;

        // Statics outlive play mode with domain reload off. SimulateSoftKeyboard is the editor's setting, and stays.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_backend = null;
            s_client = null;
            s_session = 0;
            s_serial = 0;
            s_remote = default;
            s_remoteConfig = default;
            s_geometrySent = false;
            s_keyboardHeight = 0f;
            s_keyboardVisible = false;
            s_simulated = 0f;
            s_simulatedVelocity = 0f;
            s_hooked = false;
            s_lateHooked = false;
            s_events.Clear();
            ClientChanged = null;
            KeyboardChanged = null;
        }

        /// <summary>
        /// Starts editing <paramref name="client"/>: the keyboard comes up for it (or stays up, switched to it, when another
        /// client was being edited, which is told its session ended).
        /// </summary>
        public static void Begin(ITextInputClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (client == s_client) return;
            Hook();
            var backend = Backend;
            var previous = s_client;
            if (previous != null)
            {
                s_client = null;
                backend.Detach(s_session);
                previous.OnSessionEnded();
            }

            s_session++;
            s_client = client;
            s_serial = 0;
            s_remote = client.Value;
            s_remoteConfig = client.Config;
            s_geometrySent = false;
            backend.Attach(s_session, s_remoteConfig, s_remote, s_serial);
            ClientChanged?.Invoke(client);
        }

        /// <summary>Stops editing <paramref name="client"/>, if it is being edited: the keyboard goes down.</summary>
        public static void End(ITextInputClient client)
        {
            if (client == null || client != s_client) return;
            s_client = null;
            Backend.Detach(s_session);
            ClientChanged?.Invoke(null);
        }

        /// <summary>
        /// The client being edited changed its own value (a tap moving the caret, a dragged handle, undo, text set from
        /// code, a length limit): the platform's keyboard is told, unless it already has that value.
        /// </summary>
        public static void NotifyValueChanged(ITextInputClient client)
        {
            if (client == null || client != s_client) return;
            var value = client.Value;
            if (value == s_remote) return;
            Push(value);
        }

        /// <summary>The client being edited changed its config: the keyboard is reloaded for it.</summary>
        public static void NotifyConfigChanged(ITextInputClient client)
        {
            if (client == null || client != s_client) return;
            var config = client.Config;
            if (config.Equals(s_remoteConfig)) return;
            s_remoteConfig = config;
            Backend.SetConfig(s_session, config);
        }

        /// <summary>
        /// Shows the platform's edit menu (cut, copy, paste, select all) for the client being edited, by
        /// <paramref name="target"/> (screen pixels, y up). Its actions come back as edits. Nothing where the platform has
        /// none (<see cref="SupportsEditMenu"/>).
        /// </summary>
        public static void ShowEditMenu(ITextInputClient client, Rect target, TextEditActions actions)
        {
            if (client == null || client != s_client || !Backend.SupportsEditMenu) return;
            Backend.ShowEditMenu(s_session, target, actions);
        }

        /// <summary>Hides the platform's edit menu, if it shows.</summary>
        public static void HideEditMenu() => s_backend?.HideEditMenu();

        private static ITextInputBackend Backend => s_backend ??= CreateBackend();

        private static ITextInputBackend CreateBackend()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new IosTextInputBackend();
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidTextInputBackend();
#else
            return new DesktopTextInputBackend();
#endif
        }

        // Pushes a value the client set itself, telling the backend how it differs from what the keyboard last saw.
        private static void Push(in TextEditingValue value)
        {
            var kind = KindOf(s_remote, value);
            s_serial++;
            s_remote = value;
            Backend.SetValue(s_session, s_serial, value, kind);
        }

        private static TextChangeKind KindOf(in TextEditingValue before, in TextEditingValue after)
        {
            if (string.Equals(before.Text, after.Text, StringComparison.Ordinal))
                return before.Composing == after.Composing ? TextChangeKind.Selection : TextChangeKind.Composing;
            if (before.IsComposing || after.IsComposing)
                return TextChangeKind.Composing;
            if (before.Text.Length == 0 || after.Text.Length == 0)
                return TextChangeKind.Wholesale;
            return TextChangeKind.Text;
        }

        // ── The frame ────────────────────────────────────────────────────────────

        // Early in each frame, at the end of PreUpdate (after the Input System's update): what the platform did since the
        // last frame is handed to the client, before scripts run and before layout, so an edit shows in the frame it is
        // read in; and the keyboard's height is handed to layout. Late in each frame, once layout has run (a
        // Canvas.preWillRenderCanvases handler added after the layout system's), the client's geometry is sent on.
        private struct TextInputUpdate
        {
        }

        private static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (InsertInto(ref loop))
                PlayerLoop.SetPlayerLoop(loop);
            if (!s_lateHooked)
            {
                // Taken off first: the handler outlives play mode with domain reload off, as the loop does.
                s_lateHooked = true;
                Canvas.preWillRenderCanvases -= LateTick;
                Canvas.preWillRenderCanvases += LateTick;
            }
        }

        // Adds the update to the end of PreUpdate, unless it is there already (the loop outlives play mode with domain
        // reload off). Returns whether it changed the loop.
        private static bool InsertInto(ref PlayerLoopSystem loop)
        {
            var systems = loop.subSystemList;
            if (systems == null) return false;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i].type != typeof(PreUpdate)) continue;
                var inner = systems[i].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                foreach (var system in inner)
                {
                    if (system.type == typeof(TextInputUpdate)) return false;
                }
                var grown = new PlayerLoopSystem[inner.Length + 1];
                Array.Copy(inner, grown, inner.Length);
                grown[inner.Length] = new PlayerLoopSystem { type = typeof(TextInputUpdate), updateDelegate = Tick };
                systems[i].subSystemList = grown;
                loop.subSystemList = systems;
                return true;
            }
            return false;
        }

        private static void Tick()
        {
            if (!Application.isPlaying || s_backend == null) return;
            s_backend.Update();
            s_events.Clear();
            s_backend.DrainEvents(s_events);
            for (int i = 0; i < s_events.Count; i++)
            {
                var e = s_events[i];
                if (s_client == null || e.Session != s_session) continue;
                Handle(e);
            }
            s_events.Clear();
            UpdateKeyboard();
        }

        private static void Handle(in TextInputEvent e)
        {
            var client = s_client;
            switch (e.Kind)
            {
                case TextInputEventKind.Edit:
                    // Made before a value pushed since reached the platform, which then overwrote it: the platform holds
                    // what was pushed, so the edit is dropped and the two still agree.
                    if (e.BaseSerial < s_serial) return;
                    s_remote = e.Value;
                    client.ApplyPlatformValue(e.Value);
                    // A filter that changed it (a length limit) may not have said so.
                    if (s_client == client)
                        NotifyValueChanged(client);
                    break;
                case TextInputEventKind.Intent:
                    client.PerformIntent(e.Intent, e.Extend);
                    break;
                case TextInputEventKind.InsertText:
                    if (!string.IsNullOrEmpty(e.Text))
                        client.InsertText(e.Text);
                    break;
                case TextInputEventKind.Composing:
                    client.SetComposingText(e.Text ?? string.Empty);
                    break;
                case TextInputEventKind.SessionEnded:
                    s_client = null;
                    Backend.Detach(s_session);
                    ClientChanged?.Invoke(null);
                    client.OnSessionEnded();
                    break;
            }
        }

        // The keyboard's height this frame, from the platform, or the simulated keyboard's: handed to layout, outside any
        // change, so the layout follows the keyboard exactly rather than chasing it on springs.
        private static void UpdateKeyboard()
        {
            float height = s_backend.KeyboardHeight;
            bool visible = s_backend.KeyboardVisible;
            float goal = SimulateSoftKeyboard && s_client != null ? Screen.height * SimulatedKeyboardPart : 0f;
            if (s_simulated > 0f || goal > 0f)
            {
                s_simulated = Mathf.SmoothDamp(s_simulated, goal, ref s_simulatedVelocity, SimulatedKeyboardTime, Mathf.Infinity,
                    Time.unscaledDeltaTime);
                if (Mathf.Abs(s_simulated - goal) < 0.5f)
                {
                    s_simulated = goal;
                    s_simulatedVelocity = 0f;
                }
                height = Mathf.Max(height, s_simulated);
                visible |= s_simulated > 0f;
            }
            if (Mathf.Abs(height - s_keyboardHeight) < 0.01f && visible == s_keyboardVisible) return;
            s_keyboardHeight = height;
            s_keyboardVisible = visible;
            LayoutSystem.KeyboardHeight = height;
            KeyboardChanged?.Invoke();
        }

        private static void LateTick()
        {
            if (!Application.isPlaying || s_client == null || s_backend == null) return;
            if (!s_client.TryGetScreenGeometry(out var field, out var caret, out var composing)) return;
            if (s_geometrySent && field == s_field && caret == s_caret && composing == s_composing) return;
            s_geometrySent = true;
            s_field = field;
            s_caret = caret;
            s_composing = composing;
            s_backend.SetGeometry(s_session, field, caret, composing);
        }
    }
}

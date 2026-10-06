#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// iOS: the keyboard types into a UITextInput view of our own (the native plugin in Plugins/iOS, TJTextInput.h), as
    /// Flutter's FlutterTextInputView: a view laid over Unity's, which touches pass through, holding a mirror of the
    /// value being edited. It answers the keyboard from the mirror, reports each of the keyboard's edits as the whole new
    /// value, and is told of the client's own changes in the order UIKit's input delegate expects them, so autocorrect,
    /// predictions, marked text (Chinese, Japanese), dictation and the system edit menu all work.
    /// <para>
    /// It also follows the keyboard: native code reads how UIKit animates the keyboard into place (a damped spring) and
    /// this evaluates that move each frame, for the moment the frame will be on screen, so that layout moves with the
    /// keyboard rather than after it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Unity's player loop and UIKit share the main thread, so native code calls back synchronously, from inside UIKit's
    /// calls and between frames. The callbacks only queue what they are told; the queue is drained once a frame.
    /// </remarks>
    internal sealed unsafe class IosTextInputBackend : ITextInputBackend
    {
        // ── TJTextInput.h, field for field ───────────────────────────────────────

        private enum NativeContentType
        {
            Standard = 0,
            Email = 1,
            Url = 2,
            Number = 3,
            Decimal = 4,
            Phone = 5,
            Name = 6,
            Username = 7,
            Password = 8,
            NewPassword = 9,
            OneTimeCode = 10,
            Search = 11,
        }

        private enum NativeReturnKey
        {
            Default = 0,
            Done = 1,
            Go = 2,
            Next = 3,
            Search = 4,
            Send = 5,
        }

        private enum NativeCapitalization
        {
            Sentences = 0,
            None = 1,
            Words = 2,
            Characters = 3,
        }

        [Flags]
        private enum NativeActions
        {
            Cut = 1,
            Copy = 2,
            Paste = 4,
            SelectAll = 8,
        }

        private enum NativeIntent
        {
            Return = 0,
            MoveUp = 1,
            MoveDown = 2,
            MoveLineStart = 3,
            MoveLineEnd = 4,
            Undo = 5,
            Redo = 6,
        }

        private enum NativeCurve
        {
            None = 0,
            Spring = 1,
            Bezier = 2,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public float X;
            public float Y;
            public float Width;
            public float Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeConfig
        {
            public int ContentType;
            public int ReturnKey;
            public int Capitalization;
            public int Multiline;
            public int Secure;
            public int CorrectsWords;
            public int ReturnInsertsNewline;
            public int EnablesReturnKeyAutomatically;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeKeyboardEvent
        {
            public double StartTime;
            public double Duration;
            public float Inset;
            public float ViewHeight;
            public float Mass;
            public float Stiffness;
            public float Damping;
            public float InitialVelocity;
            public float C1X;
            public float C1Y;
            public float C2X;
            public float C2Y;
            public int Curve;
            public int Visible;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeCallbacks
        {
            public IntPtr OnEdit;
            public IntPtr OnIntent;
            public IntPtr OnEnded;
            public IntPtr OnKeyboard;
        }

        private delegate void EditCallback(int session, int baseSerial, IntPtr text, int length, int selectionBase,
            int selectionExtent, int composingStart, int composingEnd);

        private delegate void IntentCallback(int session, int intent, int extend);

        private delegate void EndedCallback(int session);

        private delegate void KeyboardCallback(IntPtr keyboard);

        [DllImport("__Internal")]
        private static extern int TJTI_Init(NativeCallbacks* callbacks, int callbacksSize, int configSize, int keyboardEventSize);

        [DllImport("__Internal")]
        private static extern void TJTI_Attach(int session, NativeConfig* config, ushort* text, int length, int selectionBase,
            int selectionExtent, int composingStart, int composingEnd, int serial);

        [DllImport("__Internal")]
        private static extern void TJTI_Detach(int session);

        [DllImport("__Internal")]
        private static extern void TJTI_SetConfig(int session, NativeConfig* config);

        [DllImport("__Internal")]
        private static extern void TJTI_SetValue(int session, int serial, ushort* text, int length, int selectionBase,
            int selectionExtent, int composingStart, int composingEnd);

        [DllImport("__Internal")]
        private static extern void TJTI_SetGeometry(int session, NativeRect caret, NativeRect composing, int hasComposing);

        [DllImport("__Internal")]
        private static extern void TJTI_ShowEditMenu(int session, NativeRect target, int actions);

        [DllImport("__Internal")]
        private static extern void TJTI_HideEditMenu();

        [DllImport("__Internal")]
        private static extern double TJTI_PresentationTime();

        [DllImport("__Internal")]
        private static extern void TJTI_GetViewSize(float* width, float* height);

        // ── Callbacks ────────────────────────────────────────────────────────────

        // What native code reported since the last drain. Native code calls back only on the main thread.
        private static readonly List<TextInputEvent> s_events = new();
        private static readonly List<NativeKeyboardEvent> s_keyboardEvents = new();

        // Held for as long as native code may call them.
        private static readonly EditCallback s_onEdit = OnEdit;
        private static readonly IntentCallback s_onIntent = OnIntent;
        private static readonly EndedCallback s_onEnded = OnEnded;
        private static readonly KeyboardCallback s_onKeyboard = OnKeyboard;

        [MonoPInvokeCallback(typeof(EditCallback))]
        private static void OnEdit(int session, int baseSerial, IntPtr text, int length, int selectionBase, int selectionExtent,
            int composingStart, int composingEnd)
        {
            // The text is native's buffer, valid only during the call: copied here.
            var copy = length > 0 ? new string((char*)text.ToPointer(), 0, length) : string.Empty;
            var composing = composingStart >= 0 ? new TextRange(composingStart, composingEnd) : TextRange.None;
            var value = new TextEditingValue(copy, new TextSelection(selectionBase, selectionExtent), composing);
            s_events.Add(TextInputEvent.Edit(session, baseSerial, value));
        }

        [MonoPInvokeCallback(typeof(IntentCallback))]
        private static void OnIntent(int session, int intent, int extend)
        {
            TextEditIntent? textIntent = (NativeIntent)intent switch
            {
                NativeIntent.Return => TextEditIntent.Return,
                NativeIntent.MoveUp => TextEditIntent.MoveUp,
                NativeIntent.MoveDown => TextEditIntent.MoveDown,
                NativeIntent.MoveLineStart => TextEditIntent.MoveLineStart,
                NativeIntent.MoveLineEnd => TextEditIntent.MoveLineEnd,
                NativeIntent.Undo => TextEditIntent.Undo,
                NativeIntent.Redo => TextEditIntent.Redo,
                _ => (TextEditIntent?)null,
            };
            if (textIntent.HasValue)
                s_events.Add(TextInputEvent.ForIntent(session, textIntent.Value, extend != 0));
        }

        [MonoPInvokeCallback(typeof(EndedCallback))]
        private static void OnEnded(int session) => s_events.Add(TextInputEvent.Ended(session));

        [MonoPInvokeCallback(typeof(KeyboardCallback))]
        private static void OnKeyboard(IntPtr keyboard) => s_keyboardEvents.Add(*(NativeKeyboardEvent*)keyboard.ToPointer());

        // ── The keyboard ─────────────────────────────────────────────────────────

        // Where the keyboard is going (how far up a docked keyboard covers the Unity view, in points), and the moves still
        // under way towards it. Each move is UIKit's animation for one notification; as UIKit's view animations do, a
        // move that starts while another is under way adds to it rather than replacing it: each fades out the difference
        // between where the keyboard was going before it and where it goes now, so a retargeted keyboard (the predictions
        // bar appearing as it rises) keeps its position and its speed.
        private readonly List<KeyboardMove> _moves = new();
        private float _keyboardTarget;
        private float _viewHeight;
        private float _keyboardHeight;
        private bool _keyboardVisible;

        public IosTextInputBackend()
        {
            s_events.Clear();
            s_keyboardEvents.Clear();
            var callbacks = new NativeCallbacks
            {
                OnEdit = Marshal.GetFunctionPointerForDelegate(s_onEdit),
                OnIntent = Marshal.GetFunctionPointerForDelegate(s_onIntent),
                OnEnded = Marshal.GetFunctionPointerForDelegate(s_onEnded),
                OnKeyboard = Marshal.GetFunctionPointerForDelegate(s_onKeyboard),
            };
            if (TJTI_Init(&callbacks, sizeof(NativeCallbacks), sizeof(NativeConfig), sizeof(NativeKeyboardEvent)) == 0)
                Debug.LogError("TextInputSystem: the iOS text input plugin's structs differ from IosTextInputBackend's; the keyboard will not work.");
        }

        public bool SupportsEditMenu => true;

        public float KeyboardHeight => _keyboardHeight;

        public bool KeyboardVisible => _keyboardVisible;

        public void Attach(int session, in TextInputConfig config, in TextEditingValue value, int serial)
        {
            var native = ToNative(config);
            var text = value.Text;
            fixed (char* chars = text)
                TJTI_Attach(session, &native, (ushort*)chars, text.Length, value.Selection.Base, value.Selection.Extent,
                    value.Composing.Start, value.Composing.End, serial);
        }

        public void Detach(int session) => TJTI_Detach(session);

        public void SetConfig(int session, in TextInputConfig config)
        {
            var native = ToNative(config);
            TJTI_SetConfig(session, &native);
        }

        // The kind is not needed here: which input delegate calls the keyboard hears follows from what differs from the
        // mirror (the text, the marked text, the selection), as in Flutter's setTextInputState.
        public void SetValue(int session, int serial, in TextEditingValue value, TextChangeKind kind)
        {
            var text = value.Text;
            fixed (char* chars = text)
                TJTI_SetValue(session, serial, (ushort*)chars, text.Length, value.Selection.Base, value.Selection.Extent,
                    value.Composing.Start, value.Composing.End);
        }

        // The field's own rect is not needed: the proxy covers the whole Unity view, and UIKit asks only where the caret
        // and the composing text are (for the candidate window, the autocorrection prompt, the loupe).
        public void SetGeometry(int session, Rect field, Rect caret, Rect composing)
        {
            if (!TryGetPointScale(out var scale)) return;
            bool hasComposing = composing.width > 0f || composing.height > 0f;
            TJTI_SetGeometry(session, ToViewPoints(caret, scale), ToViewPoints(composing, scale), hasComposing ? 1 : 0);
        }

        public void ShowEditMenu(int session, Rect target, TextEditActions actions)
        {
            if (!TryGetPointScale(out var scale)) return;
            var native = default(NativeActions);
            if ((actions & TextEditActions.Cut) != 0) native |= NativeActions.Cut;
            if ((actions & TextEditActions.Copy) != 0) native |= NativeActions.Copy;
            if ((actions & TextEditActions.Paste) != 0) native |= NativeActions.Paste;
            if ((actions & TextEditActions.SelectAll) != 0) native |= NativeActions.SelectAll;
            TJTI_ShowEditMenu(session, ToViewPoints(target, scale), (int)native);
        }

        public void HideEditMenu() => TJTI_HideEditMenu();

        public void Update()
        {
            for (int i = 0; i < s_keyboardEvents.Count; i++)
                Take(s_keyboardEvents[i]);
            s_keyboardEvents.Clear();

            float inset = _keyboardTarget;
            if (_moves.Count > 0)
            {
                double time = TJTI_PresentationTime();
                for (int i = _moves.Count - 1; i >= 0; i--)
                {
                    var move = _moves[i];
                    double elapsed = time - move.Start;
                    // Snapped to its end once its time is up, as Core Animation ends it (and when a hide arrived while the
                    // player was paused and is only read now).
                    if (elapsed >= move.Duration)
                    {
                        _moves.RemoveAt(i);
                        continue;
                    }
                    inset += move.Delta * (float)move.Remaining(elapsed);
                }
            }
            // Points to Unity's screen pixels by the view's own ratio, which holds when Unity renders below the screen's
            // resolution (as Unity's UnityKeyboard_GetRect converts).
            _keyboardHeight = _viewHeight > 0f ? Mathf.Max(0f, inset) * Screen.height / _viewHeight : 0f;
        }

        public void DrainEvents(List<TextInputEvent> into)
        {
            into.AddRange(s_events);
            s_events.Clear();
        }

        private void Take(in NativeKeyboardEvent keyboard)
        {
            _keyboardVisible = keyboard.Visible != 0;
            if (keyboard.ViewHeight > 0f)
                _viewHeight = keyboard.ViewHeight;
            float delta = _keyboardTarget - keyboard.Inset;
            _keyboardTarget = keyboard.Inset;
            if ((NativeCurve)keyboard.Curve == NativeCurve.None || keyboard.Duration <= 0)
            {
                // There at once: whatever was under way is cut short.
                _moves.Clear();
                return;
            }
            if (delta != 0f)
                _moves.Add(new KeyboardMove(keyboard, delta));
        }

        // ── Conversions ──────────────────────────────────────────────────────────

        private static NativeConfig ToNative(in TextInputConfig config) => new()
        {
            ContentType = (int)(config.ContentType switch
            {
                TextContentType.Email => NativeContentType.Email,
                TextContentType.Url => NativeContentType.Url,
                TextContentType.Number => NativeContentType.Number,
                TextContentType.Decimal => NativeContentType.Decimal,
                TextContentType.Phone => NativeContentType.Phone,
                TextContentType.Name => NativeContentType.Name,
                TextContentType.Username => NativeContentType.Username,
                TextContentType.Password => NativeContentType.Password,
                TextContentType.NewPassword => NativeContentType.NewPassword,
                TextContentType.OneTimeCode => NativeContentType.OneTimeCode,
                TextContentType.Search => NativeContentType.Search,
                _ => NativeContentType.Standard,
            }),
            ReturnKey = (int)(config.ReturnKey switch
            {
                TextReturnKey.Done => NativeReturnKey.Done,
                TextReturnKey.Go => NativeReturnKey.Go,
                TextReturnKey.Next => NativeReturnKey.Next,
                TextReturnKey.Search => NativeReturnKey.Search,
                TextReturnKey.Send => NativeReturnKey.Send,
                _ => NativeReturnKey.Default,
            }),
            Capitalization = (int)(config.Capitalization switch
            {
                TextCapitalization.None => NativeCapitalization.None,
                TextCapitalization.Words => NativeCapitalization.Words,
                TextCapitalization.Characters => NativeCapitalization.Characters,
                _ => NativeCapitalization.Sentences,
            }),
            Multiline = config.Multiline ? 1 : 0,
            Secure = config.IsSecure ? 1 : 0,
            CorrectsWords = config.CorrectsWords ? 1 : 0,
            ReturnInsertsNewline = config.ReturnInsertsNewline ? 1 : 0,
            EnablesReturnKeyAutomatically = config.EnablesReturnKeyAutomatically ? 1 : 0,
        };

        // How many of the Unity view's points one of Unity's screen pixels is, across and down.
        private static bool TryGetPointScale(out Vector2 scale)
        {
            float width, height;
            TJTI_GetViewSize(&width, &height);
            if (width <= 0f || height <= 0f || Screen.width <= 0 || Screen.height <= 0)
            {
                scale = default;
                return false;
            }
            scale = new Vector2(width / Screen.width, height / Screen.height);
            return true;
        }

        // Screen pixels with y up to the Unity view's points with y down.
        private static NativeRect ToViewPoints(Rect pixels, Vector2 scale) => new()
        {
            X = pixels.x * scale.x,
            Y = (Screen.height - pixels.yMax) * scale.y,
            Width = pixels.width * scale.x,
            Height = pixels.height * scale.y,
        };

        // ── A keyboard move ──────────────────────────────────────────────────────

        // One of UIKit's keyboard animations: from Start, for Duration seconds, the share of Delta (points) still to go
        // falls from all of it to none along the animation's curve.
        private readonly struct KeyboardMove
        {
            public readonly double Start;
            public readonly double Duration;
            public readonly float Delta;
            private readonly NativeCurve _curve;
            private readonly double _mass;
            private readonly double _stiffness;
            private readonly double _damping;
            private readonly double _initialVelocity;
            private readonly double _c1X;
            private readonly double _c1Y;
            private readonly double _c2X;
            private readonly double _c2Y;

            public KeyboardMove(in NativeKeyboardEvent keyboard, float delta)
            {
                Start = keyboard.StartTime;
                Duration = keyboard.Duration;
                Delta = delta;
                _curve = (NativeCurve)keyboard.Curve;
                _mass = keyboard.Mass;
                _stiffness = keyboard.Stiffness;
                _damping = keyboard.Damping;
                _initialVelocity = keyboard.InitialVelocity;
                _c1X = keyboard.C1X;
                _c1Y = keyboard.C1Y;
                _c2X = keyboard.C2X;
                _c2Y = keyboard.C2Y;
            }

            // The share still to go `elapsed` seconds in: 1 at the start, 0 at the end.
            public double Remaining(double elapsed)
            {
                if (elapsed <= 0) return 1;
                return _curve == NativeCurve.Spring
                    ? SpringRemaining(elapsed)
                    : 1 - BezierProgress(elapsed / Duration);
            }

            // The damped spring of a CASpringAnimation (mass, stiffness, damping), solved exactly for a displacement that
            // starts at 1 and moves towards 0, for whichever damping it has. CASpringAnimation's initialVelocity is towards
            // the target, in shares of the distance per second.
            private double SpringRemaining(double t)
            {
                double omega = Math.Sqrt(_stiffness / _mass);
                double zeta = _damping / (2 * Math.Sqrt(_stiffness * _mass));
                double velocity = -_initialVelocity;
                if (Math.Abs(zeta - 1) < 1e-6)
                    return Math.Exp(-omega * t) * (1 + (velocity + omega) * t);
                if (zeta < 1)
                {
                    double damped = omega * Math.Sqrt(1 - zeta * zeta);
                    return Math.Exp(-zeta * omega * t)
                        * (Math.Cos(damped * t) + (velocity + zeta * omega) / damped * Math.Sin(damped * t));
                }
                double root = Math.Sqrt(zeta * zeta - 1);
                double slow = -omega * (zeta - root);
                double fast = -omega * (zeta + root);
                double fastShare = (velocity - slow) / (fast - slow);
                return (1 - fastShare) * Math.Exp(slow * t) + fastShare * Math.Exp(fast * t);
            }

            // How far along a cubic Bézier timing curve (CAMediaTimingFunction's, from (0, 0) to (1, 1)) the move is at
            // `x`, the share of its time gone.
            private double BezierProgress(double x)
            {
                // The curve's parameter at x: Newton's method, then halving where the slope is too flat for it.
                double t = x;
                bool solved = false;
                for (int i = 0; i < 8; i++)
                {
                    double error = Sample(t, _c1X, _c2X) - x;
                    if (Math.Abs(error) < 1e-7)
                    {
                        solved = true;
                        break;
                    }
                    double slope = Slope(t, _c1X, _c2X);
                    if (Math.Abs(slope) < 1e-6) break;
                    t -= error / slope;
                }
                if (!solved || t < 0 || t > 1)
                {
                    double lower = 0;
                    double upper = 1;
                    t = x;
                    for (int i = 0; i < 32; i++)
                    {
                        double value = Sample(t, _c1X, _c2X);
                        if (Math.Abs(value - x) < 1e-7) break;
                        if (value < x) lower = t;
                        else upper = t;
                        t = (lower + upper) * 0.5;
                    }
                }
                return Sample(t, _c1Y, _c2Y);
            }

            // One coordinate of the curve at parameter t, its control points' coordinates p1 and p2.
            private static double Sample(double t, double p1, double p2)
            {
                double c = 3 * p1;
                double b = 3 * (p2 - p1) - c;
                double a = 1 - c - b;
                return ((a * t + b) * t + c) * t;
            }

            private static double Slope(double t, double p1, double p2)
            {
                double c = 3 * p1;
                double b = 3 * (p2 - p1) - c;
                double a = 1 - c - b;
                return (3 * a * t + 2 * b) * t + c;
            }
        }
    }
}
#endif

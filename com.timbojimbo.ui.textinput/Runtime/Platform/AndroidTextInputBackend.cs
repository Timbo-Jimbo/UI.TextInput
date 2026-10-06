#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// Typing on Android: the keyboard types into a view of our own in Unity's frame layout, whose InputConnection edits
    /// a mirror of the value (the Java side, <c>com.timbojimbo.textinput</c>, under Plugins/Android), as Flutter's text
    /// input does, so that suggestions, autocorrect, composition, voice typing and the clipboard all work, and the
    /// keyboard is restarted only where it must be.
    /// <para>
    /// The keyboard and every view live on Android's UI thread, and Unity's scripts on its own: calls go across through
    /// JNI (cached class and method IDs, one reused argument array, nothing allocated but the strings passed) and are
    /// posted to the UI thread there, never waited for. What the keyboard does comes back as events Java queues: once a
    /// frame a counter is read, and only when it has moved are the events drained, as one string. The keyboard's height
    /// is read each frame, as a fraction of Unity's surface it covers, and so is the direction its language writes in.
    /// </para>
    /// <para>
    /// Hardware keyboards type through Unity as on desktop (GameActivity hands their keys to the Input System, and the
    /// Java side leaves them alone): the Input System's keys and text are read while a session is attached
    /// (<see cref="InputSystemKeyInput"/>) and handed over after the keyboard's events.
    /// </para>
    /// <para>
    /// A session detached is let go of at the next update, unless another attaches first (moving from one field to the
    /// next), which takes the keyboard over without it going down.
    /// </para>
    /// </summary>
    internal sealed class AndroidTextInputBackend : ITextInputBackend
    {
        // What the Java side's EditorConfig reads, worked out here from the config.
        private const int MultilineFlag = 1;
        private const int CorrectsWordsFlag = 2;
        private const int ReturnInsertsNewlineFlag = 4;
        private const int SecureFlag = 8;

        // Before Android 11 the keyboard's height is known only once it has moved, as a step: it is eased here over as
        // long as Android 11 and later take to slide the keyboard, on the same curve.
        private const float SteppedKeyboardTime = 0.285f;

        // The editing keys the Java side sends (TextInputConnection's INTENT_ codes): numbered there for itself, so
        // that TextEditIntent can grow without the two falling out of step.
        private enum NativeIntent
        {
            Return = 0,
            MoveLeft = 1,
            MoveRight = 2,
            MoveUp = 3,
            MoveDown = 4,
            MoveWordLeft = 5,
            MoveWordRight = 6,
            MoveLineLeft = 7,
            MoveLineRight = 8,
            MoveDocumentStart = 9,
            MoveDocumentEnd = 10,
        }

        private static IntPtr s_class;
        private static IntPtr s_attach;
        private static IntPtr s_detach;
        private static IntPtr s_setConfig;
        private static IntPtr s_setValue;
        private static IntPtr s_setGeometry;
        private static IntPtr s_setDirection;
        private static IntPtr s_showEditMenu;
        private static IntPtr s_hideEditMenu;
        private static IntPtr s_changeCounter;
        private static IntPtr s_drain;
        private static IntPtr s_keyboardFraction;
        private static IntPtr s_keyboardVisible;
        private static IntPtr s_keyboardRightToLeft;
        private static bool s_keyboardAnimates;
        // Reused for every call: all of them are made on Unity's main thread, one at a time.
        private static readonly jvalue[] s_args = new jvalue[11];

        private readonly InputSystemKeyInput _keyInput = new(takesText: true);

        private int _session;
        // A session detached, which the Java side lets go of at the next update unless another attaches first.
        private int _detaching;
        private long _changes;

        private float _keyboardHeight;
        private bool _keyboardVisible;
        private bool _keyboardRightToLeft;

        // The keyboard's covered fraction as shown, easing from _easedFrom to _easedTo since _easedSince (stepped only).
        private float _eased;
        private float _easedFrom;
        private float _easedTo;
        private float _easedSince;

        public AndroidTextInputBackend()
        {
            Bind();
        }

        public bool SupportsEditMenu => true;

        public float KeyboardHeight => _keyboardHeight;

        public bool KeyboardVisible => _keyboardVisible;

        public bool KeyboardRightToLeft => _keyboardRightToLeft;

        public void Attach(int session, in TextInputConfig config, in TextEditingValue value, int serial)
        {
            _detaching = 0;
            _session = session;
            _keyInput.Enabled = true;
            var args = s_args;
            args[0].i = session;
            SetConfigArgs(args, 1, config);
            IntPtr text = AndroidJNI.NewString(value.Text);
            args[5].l = text;
            SetSelectionArgs(args, 6, value);
            args[10].i = serial;
            AndroidJNI.CallStaticVoidMethod(s_class, s_attach, args);
            AndroidJNI.DeleteLocalRef(text);
        }

        public void Detach(int session)
        {
            if (session != _session) return;
            _session = 0;
            _keyInput.Enabled = false;
            _detaching = session;
        }

        public void SetConfig(int session, in TextInputConfig config)
        {
            if (session != _session) return;
            var args = s_args;
            args[0].i = session;
            SetConfigArgs(args, 1, config);
            AndroidJNI.CallStaticVoidMethod(s_class, s_setConfig, args);
        }

        public void SetValue(int session, int serial, in TextEditingValue value, TextChangeKind kind)
        {
            if (session != _session) return;
            var args = s_args;
            args[0].i = session;
            args[1].i = serial;
            IntPtr text = AndroidJNI.NewString(value.Text);
            args[2].l = text;
            SetSelectionArgs(args, 3, value);
            args[7].i = (int)kind;
            AndroidJNI.CallStaticVoidMethod(s_class, s_setValue, args);
            AndroidJNI.DeleteLocalRef(text);
        }

        // Only the field and the caret are sent: the keyboard's cursor info has no use for the composition's box alone.
        public void SetGeometry(int session, Rect field, Rect caret, Rect composing)
        {
            if (session != _session) return;
            var args = s_args;
            args[0].i = session;
            SetRectArgs(args, 1, field);
            SetRectArgs(args, 5, caret);
            AndroidJNI.CallStaticVoidMethod(s_class, s_setGeometry, args);
        }

        public void SetDirection(int session, bool rightToLeft, bool caretRightToLeft)
        {
            if (session != _session) return;
            var args = s_args;
            args[0].i = session;
            args[1].z = rightToLeft;
            args[2].z = caretRightToLeft;
            AndroidJNI.CallStaticVoidMethod(s_class, s_setDirection, args);
        }

        public void ShowEditMenu(int session, Rect target, TextEditActions actions)
        {
            if (session != _session) return;
            var args = s_args;
            args[0].i = session;
            SetRectArgs(args, 1, target);
            args[5].i = (int)actions;
            AndroidJNI.CallStaticVoidMethod(s_class, s_showEditMenu, args);
        }

        public void HideEditMenu() => AndroidJNI.CallStaticVoidMethod(s_class, s_hideEditMenu, s_args);

        public void Update()
        {
            if (_detaching != 0)
            {
                s_args[0].i = _detaching;
                _detaching = 0;
                AndroidJNI.CallStaticVoidMethod(s_class, s_detach, s_args);
            }
            _keyInput.Update();

            float fraction = AndroidJNI.CallStaticFloatMethod(s_class, s_keyboardFraction, s_args);
            _keyboardVisible = AndroidJNI.CallStaticBooleanMethod(s_class, s_keyboardVisible, s_args);
            _keyboardRightToLeft = AndroidJNI.CallStaticBooleanMethod(s_class, s_keyboardRightToLeft, s_args);
            if (!s_keyboardAnimates) fraction = Ease(fraction);
            _keyboardHeight = fraction * Screen.height;
        }

        public void DrainEvents(List<TextInputEvent> into)
        {
            long changes = AndroidJNI.CallStaticLongMethod(s_class, s_changeCounter, s_args);
            if (changes != _changes)
            {
                _changes = changes;
                Parse(AndroidJNI.CallStaticStringMethod(s_class, s_drain, s_args), into);
            }
            _keyInput.Drain(into, _session);
        }

        // ── JNI ─────────────────────────────────────────────────────────────

        // The Java class and its methods, looked up once (they live as long as the process).
        private static void Bind()
        {
            if (s_class != IntPtr.Zero) return;
            IntPtr local = AndroidJNI.FindClass("com/timbojimbo/textinput/TextInputBridge");
            if (local == IntPtr.Zero)
            {
                AndroidJNI.ExceptionClear();
                throw new InvalidOperationException(
                    "The Java side of UI Text Input (com.timbojimbo.textinput.TextInputBridge) is not in the build. With " +
                    "minification on, keep it in the project's proguard-user.txt: -keep class com.timbojimbo.textinput.** { *; }");
            }
            s_class = AndroidJNI.NewGlobalRef(local);
            AndroidJNI.DeleteLocalRef(local);
            s_attach = Method("attach", "(IIIIILjava/lang/String;IIIII)V");
            s_detach = Method("detach", "(I)V");
            s_setConfig = Method("setConfig", "(IIIII)V");
            s_setValue = Method("setValue", "(IILjava/lang/String;IIIII)V");
            s_setGeometry = Method("setGeometry", "(IFFFFFFFF)V");
            s_setDirection = Method("setDirection", "(IZZ)V");
            s_showEditMenu = Method("showEditMenu", "(IFFFFI)V");
            s_hideEditMenu = Method("hideEditMenu", "()V");
            s_changeCounter = Method("changeCounter", "()J");
            s_drain = Method("drain", "()Ljava/lang/String;");
            s_keyboardFraction = Method("keyboardFraction", "()F");
            s_keyboardVisible = Method("keyboardVisible", "()Z");
            s_keyboardRightToLeft = Method("keyboardRightToLeft", "()Z");
            s_keyboardAnimates = AndroidJNI.CallStaticBooleanMethod(s_class, Method("keyboardAnimates", "()Z"), s_args);
        }

        private static IntPtr Method(string name, string signature) => AndroidJNI.GetStaticMethodID(s_class, name, signature);

        // The config as the Java side takes it: content type, return key, capitalization, flags.
        private static void SetConfigArgs(jvalue[] args, int at, in TextInputConfig config)
        {
            args[at].i = (int)config.ContentType;
            args[at + 1].i = (int)config.ReturnKey;
            args[at + 2].i = (int)config.Capitalization;
            args[at + 3].i = (config.Multiline ? MultilineFlag : 0) | (config.CorrectsWords ? CorrectsWordsFlag : 0)
                | (config.ReturnInsertsNewline ? ReturnInsertsNewlineFlag : 0) | (config.IsSecure ? SecureFlag : 0);
        }

        // The selection (base, extent) and the composing range (start, end; -1, -1 with none).
        private static void SetSelectionArgs(jvalue[] args, int at, in TextEditingValue value)
        {
            args[at].i = value.Selection.Base;
            args[at + 1].i = value.Selection.Extent;
            args[at + 2].i = value.Composing.Start;
            args[at + 3].i = value.Composing.End;
        }

        // A rect in screen pixels (y up) as fractions of the screen, left, top, right, bottom, y down: the Java side maps
        // them onto Unity's surface, which the screen fills whatever resolution Unity renders at.
        private static void SetRectArgs(jvalue[] args, int at, Rect rect)
        {
            float width = Screen.width;
            float height = Screen.height;
            args[at].f = rect.xMin / width;
            args[at + 1].f = 1f - rect.yMax / height;
            args[at + 2].f = rect.xMax / width;
            args[at + 3].f = 1f - rect.yMin / height;
        }

        // ── Events ──────────────────────────────────────────────────────────

        // The events drained, as TextInputBridge.java encodes them: a letter, then decimal numbers each ended by one
        // character; an edit's last number is the length of the text that follows it.
        private static void Parse(string events, List<TextInputEvent> into)
        {
            if (string.IsNullOrEmpty(events)) return;
            int i = 0;
            while (i < events.Length)
            {
                char kind = events[i++];
                int session = ReadInt(events, ref i);
                switch (kind)
                {
                    case 'E':
                    {
                        int baseSerial = ReadInt(events, ref i);
                        int selectionBase = ReadInt(events, ref i);
                        int selectionExtent = ReadInt(events, ref i);
                        int composingStart = ReadInt(events, ref i);
                        int composingEnd = ReadInt(events, ref i);
                        int length = ReadInt(events, ref i);
                        string text = events.Substring(i, length);
                        i += length;
                        var value = new TextEditingValue(text, new TextSelection(selectionBase, selectionExtent),
                            new TextRange(composingStart, composingEnd));
                        into.Add(TextInputEvent.Edit(session, baseSerial, value));
                        break;
                    }
                    case 'I':
                    {
                        var intent = (NativeIntent)ReadInt(events, ref i);
                        bool extend = ReadInt(events, ref i) != 0;
                        TextEditIntent? textIntent = intent switch
                        {
                            NativeIntent.Return => TextEditIntent.Return,
                            NativeIntent.MoveLeft => TextEditIntent.MoveLeft,
                            NativeIntent.MoveRight => TextEditIntent.MoveRight,
                            NativeIntent.MoveUp => TextEditIntent.MoveUp,
                            NativeIntent.MoveDown => TextEditIntent.MoveDown,
                            NativeIntent.MoveWordLeft => TextEditIntent.MoveWordLeft,
                            NativeIntent.MoveWordRight => TextEditIntent.MoveWordRight,
                            NativeIntent.MoveLineLeft => TextEditIntent.MoveLineLeft,
                            NativeIntent.MoveLineRight => TextEditIntent.MoveLineRight,
                            NativeIntent.MoveDocumentStart => TextEditIntent.MoveDocumentStart,
                            NativeIntent.MoveDocumentEnd => TextEditIntent.MoveDocumentEnd,
                            _ => (TextEditIntent?)null,
                        };
                        if (textIntent.HasValue)
                            into.Add(TextInputEvent.ForIntent(session, textIntent.Value, extend));
                        break;
                    }
                    case 'X':
                        into.Add(TextInputEvent.Ended(session));
                        break;
                }
            }
        }

        // A decimal number, perhaps negative, and the character that ends it.
        private static int ReadInt(string s, ref int i)
        {
            bool negative = s[i] == '-';
            if (negative) i++;
            int value = 0;
            while (s[i] >= '0' && s[i] <= '9')
                value = value * 10 + (s[i++] - '0');
            i++;
            return negative ? -value : value;
        }

        // ── The keyboard's height, eased where it steps ─────────────────────

        private float Ease(float target)
        {
            float now = Time.unscaledTime;
            if (target != _easedTo)
            {
                _easedFrom = _eased;
                _easedTo = target;
                _easedSince = now;
            }
            float t = (now - _easedSince) / SteppedKeyboardTime;
            _eased = t >= 1f ? _easedTo : Mathf.LerpUnclamped(_easedFrom, _easedTo, KeyboardCurve(t));
            return _eased;
        }

        // The curve Android 11 and later slide the keyboard on (InsetsController's, for apps that follow it): a cubic
        // Bézier through (0.2, 0) and (0, 1), its x solved for t by bisection (it only rises), then its y.
        private static float KeyboardCurve(float t)
        {
            float low = 0f;
            float high = 1f;
            float u = t;
            for (int step = 0; step < 16; step++)
            {
                u = (low + high) * 0.5f;
                float rest = 1f - u;
                float x = 0.6f * u * rest * rest + u * u * u;
                if (x < t)
                    low = u;
                else
                    high = u;
            }
            return u * u * (3f - 2f * u);
        }
    }
}
#endif

# Timbo Jimbo - UI Text Input

Text fields drawn by our own UI, typed into with the platform's own keyboard, as browsers and Flutter do: autocorrect, the suggestion bar, composition for Chinese, Japanese and Korean, dictation and the system's copy and paste menu all go through the platform, while the field itself is a `TextBlock`, a caret, a selection and handles drawn and animated like the rest of the UI. The keyboard's height is fed to layout as part of the safe area, as SwiftUI's keyboard safe area is, so a screen's content rises with the keyboard while its background reaches under it.

Requires Unity 6000.5 or newer, the UI and UI Text packages, and the Input System.

## How it works

`TextInputSystem` connects one thing being edited (an `ITextInputClient`, such as a `TextField`) to a small native input client:

- **iOS**: a hidden `UIView` adopting `UITextInput`, a subview of Unity's view and first responder while editing, new for each editing session. It answers the keyboard's questions (the text around the caret, the marked text, where the caret is on screen) from a mirror of the field's value, and reports the keyboard's edits back. It also tracks the keyboard's frame and the spring UIKit animates it on, so layout follows it exactly.
- **Android**: a 1×1 view in the player's layout whose `InputConnection` edits a mirror `Editable`. It works with both the GameActivity and the Activity entry points, tracks the keyboard's height frame by frame (`WindowInsetsAnimation` on Android 11 and up, the visible frame below), and hands hardware-keyboard keys to the Input System side.
- **Desktop and the editor**: the Input System's keyboard for typed text and editing keys (with the platform's shortcuts and key repeat), and the IME through `Input.imeCompositionMode`, `Input.compositionString` and `Input.compositionCursorPos`, as Unity's own UIs use it.

The field owns the value; the native side's mirror only answers the keyboard. What the keyboard changes reaches the field once a frame, early in it (`ITextInputClient.ApplyPlatformValue`); what the field changes itself (a tap moving the caret, text set from code, undo) is pushed to the mirror, with a serial, only when it differs from what the keyboard last saw, and the keyboard is told in the least disruptive way: a selection change, a text change, or, only for a composition changed from our side or text replaced as a whole, a restart. Restarting on every change, as Unity's own keyboard does, is what loses autocorrect's state.

## Usage

```csharp
var config = new TextInputConfig { ReturnKey = TextReturnKey.Send, Multiline = true };
var field = TextFieldBuilder.Create(row, "Message", config, "Message", 16f, textColour, placeholderColour,
    caretColour, selectionColour, maxLines: 5);
field.Submitted.AddListener(text => Send(text));
field.TextChanged.AddListener(text => sendButton.Display = text.Length > 0 ? DisplayMode.Visible : DisplayMode.None);
```

- **`TextField`** is a `Selectable`: focus and gamepad navigation reach it, and Submit (Enter, a gamepad's A) begins editing, as do a tap, a click, Tab or `BeginEditing()`. Escape, a gamepad's B, the keyboard being dismissed, the field being hidden and `EndEditing()` end it; with a mouse or a keyboard so does focus moving elsewhere, as a browser blurs a field. A touch elsewhere does not, as on iOS: a chat's Send button can be tapped with the keyboard up.
- **`TextInputConfig`** picks the keyboard: `ContentType` (text, email, URL, numbers, phone, name, user name, password, new password, one-time code, search), `ReturnKey`, `Multiline`, `Capitalization`, `Autocorrection`.
- A single-line field is one line high and scrolls sideways to keep the caret in view. A multi-line one grows with its lines, on a quick spring, up to `maxLines`, then scrolls.
- **Touch**: a tap places the caret, a double tap or a long press selects a word, handles adjust the selection, and the platform's edit menu does the rest. **Mouse**: click, drag, double and triple click, shift-click. **Keys**: the platform's editing shortcuts, clipboard and undo.
- `MaxLength` limits typing and pasting, after a composition is committed rather than in the middle of one.
- **Keyboard avoidance** is automatic: `TextInputSystem.KeyboardHeight` is handed to `LayoutSystem.KeyboardHeight`, which is part of every root's bottom safe area. A node that ignores the safe area reaches under the keyboard, its content staying above it, and the nearest scroll container above an editing field brings it into view as the keyboard rises.
- `TextInputSystem.IsEditing` and `ClientChanged` tell game code when to turn its own key bindings off.
- **Trying it in the editor**: *Timbo Jimbo > UI > Simulate Soft Keyboard* pretends a keyboard slides up while a field is edited, so layouts that make room for it can be tried in the Game view or the Device Simulator.

## Platforms

- **iOS**: Objective-C++ in `Runtime/Plugins/iOS`, compiled into UnityFramework. iOS 15 or newer.
- **Android**: Java in `Runtime/Plugins/Android`, compiled into the player's library. Android 8 (API 26) or newer, no extra Gradle dependencies. The classes are reached only through JNI, so a minified build must keep them: add `-keep class com.timbojimbo.textinput.** { *; }` to `proguard-user.txt`.
- Unity's own `TouchScreenKeyboard`, uGUI's `InputField` and `TMP_InputField` must not be opened while a field is being edited: each takes the keyboard for its own native field.

## Known limits

- Built without a device to hand: everything native needs trying on iOS and Android devices with real keyboards (Gboard, Samsung, SwiftKey; Japanese, Chinese, Korean; dictation; hardware keyboards).
- iOS: the keyboard's questions about where characters are on screen are answered from the caret and the composing range rather than each character, so the autocorrect highlight and the spacebar trackpad are approximate. Scribble is off. The iPad's shortcut-bar undo buttons and shake to undo don't reach the field's history (Cmd+Z does).
- Android: no rich content from the keyboard (GIFs, stickers), no stylus handwriting into the field, and the field's placeholder is not passed to the keyboard as a hint.
- Desktop: no edit menu (the shortcuts do its work); the IME's candidate window position in a scaled Game view is untested; key repeat uses fixed timings rather than the system's.
- Gamepads can begin and end editing but not move the caret.
- Secure fields show a bullet for each UTF-16 unit and never reveal the last character typed.

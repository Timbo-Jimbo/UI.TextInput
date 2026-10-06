## [Unreleased]

Needs the UI Text package's next version (`TextBlockDirection.Auto`, `NaturalAlignment` and its queries for text that reads both ways) and the UI package's (`LayoutNode.AvoidsKeyboard`).

### Added

- Right-to-left text. `TextFieldBuilder` makes a field's text (and its placeholder) read the way its first letter does and start at that side (`TextBlockDirection.Auto`, `NaturalAlignment`), as iOS and Android text views do, so Arabic and Hebrew sit at the right with the caret where the next letter goes. With no letter yet (empty, or only numbers and emoji) it reads the way the keyboard in use writes, as on iOS: `TextInputSystem.KeyboardRightToLeft` says which, from the keyboard's language on iOS and Android, and a password always reads left to right
- The arrow keys move the caret to the place beside it on screen, as on iOS, macOS and Android (`TextEditIntent.MoveLeft` and `MoveRight` are now where the arrows point, crossing a soft wrap one place a press); the word moves go towards their side; Cmd-Left and Cmd-Right on a Mac or an iPad, Alt elsewhere, and Android's Home and End, go to the line's left and right ends (`MoveLineLeft`, `MoveLineRight`); Ctrl-B and Ctrl-F stay back and on through the text (`MoveBackward`, `MoveForward`). On iOS the arrows reach the field as intents; on Android the keyboard's arrows (Gboard's space-bar cursor among them), Home and End do
- Selection handles stand at the edges of the first and last characters selected and are dragged by those edges, as on Android and iOS, so in text that reads both ways each stays beside what it selects; a tap on the selection is a tap on its highlight
- The platforms are told which way the text reads: on iOS the proxy view's base writing direction, its left and right moves and its tokenizer follow it, and the keyboard's language (and UIKit's own writing-direction calls) come back; on Android the insertion marker is flagged right to left by the character at the caret (`CursorAnchorInfo.FLAG_IS_RTL`), and the keyboard's subtype language comes back. `ITextInputClient.GetDirection` and `ITextInputBackend.SetDirection` and `KeyboardRightToLeft` carry it

### Changed

- The keyboard's height no longer moves every screen: layout's `LayoutNode.AvoidsKeyboard` keeps a page's or a sheet's content above it, as UIKit's keyboard layout guide does, and nothing else moves. Set it on the page or sheet holding a field
- A single-line field whose text reads right to left scrolls mirrored: it shows its start (its right) when not edited, and keeps the caret in view as text grows to the left

### Fixed

- Typing Arabic or Hebrew left the caret at the right end of the text: the text was laid out as a left-to-right paragraph, whose end is at its right
- iOS: a selection UIKit extended backwards lost its direction (`setSelectedTextRange:` always put its base at its start), so a Shift-move after it extended it from the wrong end; it keeps its base now when the new range does
- iOS: the plugin compiles in Xcode. The proxy view's `UITextInputTraits` properties (keyboard type, return key, autocorrection and the rest) are synthesized in its implementation rather than redeclared in a class extension, which clang rejects because adopting `UITextInput` already declares them

## [0.1.0] - 06/10/2026

### Added

- The first version: text fields drawn by the UI and UI Text packages and typed into with the platform's own keyboard, as browsers and Flutter do
- `TextInputSystem`: connects one `ITextInputClient` at a time to the platform's keyboard through a small native input client, with sessions, serials and a mirror of the value on the native side so the keyboard's own edits are never echoed back and it is restarted only when a composition is changed from our side or the text is replaced as a whole. Tracks the software keyboard's docked height frame by frame and hands it to layout (`LayoutSystem.KeyboardHeight`), as SwiftUI's keyboard safe area. `SimulateSoftKeyboard` pretends a keyboard where there is none
- iOS input client: a hidden `UITextInput` view per editing session (marked text, `inputDelegate` notifications only for our own changes, traits from the field's config, return key, grapheme-aware delete, the system edit menu with Paste that doesn't prompt, dictation's audio handed back), and a keyboard tracker that follows UIKit's own keyboard spring
- Android input client: a 1×1 view whose `InputConnection` edits a mirror `Editable` (nested batch edits, `updateSelection` after every change, extracted-text monitoring for Samsung, cursor anchor info, the floating edit toolbar, restart only when needed), keyboard height from `WindowInsetsAnimation` on Android 11 and up and the visible frame below, events handed to Unity's thread through a polled queue; GameActivity and Activity entry points
- Desktop and editor input: typed text and editing keys from the Input System with the platform's shortcuts and key repeat, and the IME through the legacy composition properties Unity's own UIs use
- `TextField`: a `Selectable` that edits a `TextBlock` in a clipping, scrolling viewport, with its own caret, selection, composing underline and selection handles; single-line fields scroll sideways, multi-line ones grow on a spring up to a number of lines; touch, mouse and keyboard editing, undo, a length limit applied on commit, secure entry; works with the focus system (Submit begins, Escape or B ends, Tab moves on) and keeps the keyboard up when something else is tapped, as iOS does
- `TextFieldBuilder.Create`: the nodes a field needs, in one call
- `TextBoundaries` (grapheme clusters, words, paragraphs, backspace's reach) and `TextEditHistory` (coalescing undo)

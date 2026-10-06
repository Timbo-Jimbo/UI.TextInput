using System;
using System.Text;
using UnityEngine;

namespace TimboJimbo.UI.TextInput
{
    // The editing keys (TextEditIntent) and what the field lets in: line breaks and the length limit.
    public sealed partial class TextField
    {
        // A key from a hardware or desktop keyboard, carried out with the field's own layout, so a word, a line and a
        // page are what it shows. Moves leave the selection's base where it is when `extend` (shift is held) and collapse
        // it otherwise; deletes take what is selected, or else from the caret to where the key reaches. A secure field
        // shows no words, so a word's moves and deletes go to the end of the text, as a browser's password field does.
        void ITextInputClient.PerformIntent(TextEditIntent intent, bool extend)
        {
            var value = _value;
            string text = value.Text;
            var selection = value.Selection;
            // Without shift, a move from a selection starts at the end of it the move goes towards.
            bool collapse = !extend && !selection.IsCollapsed;
            int start = collapse ? selection.Start : selection.Extent;
            int end = collapse ? selection.End : selection.Extent;
            int caret = selection.Extent;
            bool secure = _config.IsSecure;
            switch (intent)
            {
                case TextEditIntent.MoveLeft:
                    Move(collapse ? start : TextBoundaries.PreviousCaretStop(text, caret), extend);
                    break;
                case TextEditIntent.MoveRight:
                    Move(collapse ? end : TextBoundaries.NextCaretStop(text, caret), extend);
                    break;
                case TextEditIntent.MoveUp:
                    MoveLines(start, -1, extend);
                    break;
                case TextEditIntent.MoveDown:
                    MoveLines(end, 1, extend);
                    break;
                case TextEditIntent.MovePageUp:
                    MoveLines(start, -PageLines(), extend);
                    break;
                case TextEditIntent.MovePageDown:
                    MoveLines(end, PageLines(), extend);
                    break;
                case TextEditIntent.MoveWordLeft:
                    Move(secure ? 0 : TextBoundaries.PreviousWordStart(text, start), extend);
                    break;
                case TextEditIntent.MoveWordRight:
                    Move(secure ? text.Length : TextBoundaries.NextWordEnd(text, end), extend);
                    break;
                case TextEditIntent.MoveLineStart:
                    Move(LineStart(start), extend);
                    break;
                case TextEditIntent.MoveLineEnd:
                {
                    // The end of a line that wraps is where the next starts: the caret stays on this line, upstream.
                    int to = LineEnd(end);
                    Move(to, extend, WrapsAt(to));
                    break;
                }
                case TextEditIntent.MoveDocumentStart:
                    Move(0, extend);
                    break;
                case TextEditIntent.MoveDocumentEnd:
                    Move(text.Length, extend);
                    break;
                case TextEditIntent.DeleteBackward:
                    // The whole of an emoji, or the last code point of anything else (a Thai or Devanagari vowel sign
                    // alone), as UIKit and Flutter do.
                    DeleteBack(caret > 0 ? TextBoundaries.DeleteBackwardStart(text, caret) : caret, coalesce: true);
                    break;
                case TextEditIntent.DeleteForward:
                    DeleteOn(TextBoundaries.NextCaretStop(text, caret), coalesce: true);
                    break;
                case TextEditIntent.DeleteWordBackward:
                    DeleteBack(secure ? 0 : TextBoundaries.PreviousWordStart(text, caret), coalesce: false);
                    break;
                case TextEditIntent.DeleteWordForward:
                    DeleteOn(secure ? text.Length : TextBoundaries.NextWordEnd(text, caret), coalesce: false);
                    break;
                case TextEditIntent.DeleteToLineStart:
                {
                    // At the start of a line already, it joins the line to the one before.
                    int from = LineStart(caret);
                    if (from == caret && caret > 0)
                        from = TextBoundaries.DeleteBackwardStart(text, caret);
                    DeleteBack(from, coalesce: false);
                    break;
                }
                case TextEditIntent.DeleteToLineEnd:
                {
                    int to = LineEnd(caret);
                    if (to == caret)
                        to = TextBoundaries.NextCaretStop(text, caret);
                    DeleteOn(to, coalesce: false);
                    break;
                }
                case TextEditIntent.SelectAll:
                    SetSelection(new TextSelection(0, text.Length));
                    break;
                case TextEditIntent.Copy:
                    Copy();
                    break;
                case TextEditIntent.Cut:
                    if (Copy())
                        Delete(_value.Selection.Range, coalesce: false);
                    break;
                case TextEditIntent.Paste:
                    Put(Clean(GUIUtility.systemCopyBuffer), coalesce: false);
                    break;
                case TextEditIntent.Undo:
                    Restore(undo: true);
                    break;
                case TextEditIntent.Redo:
                    Restore(undo: false);
                    break;
                case TextEditIntent.Newline:
                    // Shift and return: a new line whatever the return key does; a single line has none, and submits.
                    if (Multiline)
                        Put("\n", coalesce: true);
                    else
                        Submit();
                    break;
                case TextEditIntent.Return:
                    if (_config.ReturnInsertsNewline)
                        Put("\n", coalesce: true);
                    else
                        Submit();
                    break;
                case TextEditIntent.Cancel:
                    EndEditing();
                    break;
            }
        }

        // ── Moving ───────────────────────────────────────────────────────────────

        // `upstream`: the caret's affinity where it lands (see _upstream).
        private void Move(int to, bool extend, bool upstream = false)
        {
            var selection = _value.Selection;
            SetSelection(extend ? new TextSelection(selection.Base, to) : TextSelection.Collapsed(to), upstream);
        }

        // Up or down `lines` lines from `from`, keeping to the x the caret had as the run of moves up and down began, so it
        // comes back to its column after a shorter line (at the end of a wrapped one past whose end that x is); past the
        // first line it goes to the start, past the last to the end, as on a Mac.
        private void MoveLines(int from, int lines, bool extend)
        {
            if (_text == null)
            {
                Move(lines < 0 ? 0 : _value.Text.Length, extend);
                return;
            }
            _text.EnsureLayout();
            bool fromUpstream = UpstreamAt(from);
            if (float.IsNaN(_goalX))
                _goalX = _text.GetCaretRect(from, fromUpstream).x;
            float goal = _goalX;
            int line = _text.GetLineAt(from, fromUpstream) + lines;
            int to;
            bool upstream = false;
            if (line < 0)
                to = 0;
            else if (line >= _text.LineCount)
                to = _value.Text.Length;
            else
                to = IndexAt(new Vector2(goal, _text.GetCaretRect(_text.GetLineStart(line)).center.y), out upstream);
            Move(to, extend, upstream);
            _goalX = goal;
        }

        // How many lines a page up or down moves: as many as the viewport shows whole, at least one.
        private int PageLines()
        {
            if (_text == null || _viewport == null || _text.LineHeight <= 0f) return 1;
            return Mathf.Max(1, Mathf.FloorToInt(((RectTransform)_viewport.transform).rect.height / _text.LineHeight));
        }

        // The start and the end of the line `index` is on: the caret's line, by its affinity, when it is the caret.
        private int LineStart(int index)
        {
            if (_text == null) return 0;
            _text.EnsureLayout();
            return _text.GetLineStart(_text.GetLineAt(index, UpstreamAt(index)));
        }

        private int LineEnd(int index)
        {
            if (_text == null) return _value.Text.Length;
            _text.EnsureLayout();
            return _text.GetLineEnd(_text.GetLineAt(index, UpstreamAt(index)));
        }

        // Whether `index` is the caret standing upstream, at the end of a wrapped line (see _upstream).
        private bool UpstreamAt(int index) => _upstream && index == _value.Selection.Extent;

        // Whether a line wraps at `index`, which then ends that line and starts the next, as the text was last laid out.
        private bool WrapsAt(int index) => _text != null && _text.GetLineAt(index, upstream: true) != _text.GetLineAt(index);

        // ── Deleting ─────────────────────────────────────────────────────────────

        // Deletes what is selected, or else from `from` up to the caret. Backspacing a character at a time is typing:
        // its steps join the run before for undo.
        private void DeleteBack(int from, bool coalesce)
        {
            var selection = _value.Selection;
            if (selection.IsCollapsed)
                Delete(new TextRange(from, selection.Extent), coalesce);
            else
                Delete(selection.Range, coalesce: false);
        }

        // Deletes what is selected, or else from the caret up to `to`.
        private void DeleteOn(int to, bool coalesce)
        {
            var selection = _value.Selection;
            if (selection.IsCollapsed)
                Delete(new TextRange(selection.Extent, to), coalesce);
            else
                Delete(selection.Range, coalesce: false);
        }

        private void Delete(TextRange range, bool coalesce)
        {
            if (!range.IsValid || range.IsEmpty) return;
            Change(_value.Replace(range, string.Empty), record: true, coalesce: coalesce, notify: true);
        }

        // ── The clipboard, undo and return ───────────────────────────────────────

        // Copies what is selected; nothing for a secure field, whose text never leaves it, as a browser's password field.
        private bool Copy()
        {
            var selection = _value.Selection;
            if (_config.IsSecure || selection.IsCollapsed) return false;
            GUIUtility.systemCopyBuffer = _value.Text.Substring(selection.Start, selection.End - selection.Start);
            return true;
        }

        // Undo or redo. Undo during a composition takes the composition back first (the history does).
        private void Restore(bool undo)
        {
            TextEditingValue restored;
            bool found = undo ? _history.TryUndo(_value, out restored) : _history.TryRedo(_value, out restored);
            if (found)
                Change(restored, record: false, coalesce: false, notify: true);
        }

        private void Submit()
        {
            if (_value.IsComposing)
                Change(Committed(_value.Selection), record: true, coalesce: false, notify: true);
            _submitted.Invoke(_value.Text);
            // A handler may have begun editing another field, which ended this one already.
            if (_editing && _config.ReturnKey != TextReturnKey.Send)
                EndEditing();
        }

        // ── What the field lets in ───────────────────────────────────────────────

        // Text from outside (pasted, inserted, set from code, typed): line breaks made "\n", as browsers make a textarea's,
        // and taken out of a single line, as HTML strips them from a text input.
        private string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.IndexOf('\r') >= 0)
                text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (!Multiline && text.IndexOf('\n') >= 0)
                text = text.Replace("\n", string.Empty);
            return text;
        }

        // A single-line field's value with any line breaks taken out (pasted through the platform's edit menu, say), its
        // selection kept on the same characters. A composition is left as it is: it is the keyboard's until committed.
        private TextEditingValue Flatten(in TextEditingValue value)
        {
            var text = value.Text;
            if (Multiline || value.IsComposing || (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)) return value;
            var kept = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (!IsBreak(text[i]))
                    kept.Append(text[i]);
            }
            var selection = value.Selection;
            return new TextEditingValue(kept.ToString(), new TextSelection(Kept(text, selection.Base), Kept(text, selection.Extent)), TextRange.None);
        }

        private static bool IsBreak(char c) => c == '\n' || c == '\r';

        // Where `index` is once the line breaks before it are taken out.
        private static int Kept(string text, int index)
        {
            int kept = index;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (IsBreak(text[i]))
                    kept--;
            }
            return kept;
        }

        // `text` cut short, at a whole character, so that putting it in place of `range` in `value` keeps the text within
        // MaxLength.
        private string Fit(in TextEditingValue value, TextRange range, string text)
        {
            if (_maxLength <= 0) return text;
            int room = _maxLength - (value.Text.Length - range.Length);
            if (text.Length <= room) return text;
            return room <= 0 ? string.Empty : text.Substring(0, TextBoundaries.SnapToCaretStop(text, room));
        }

        // What the keyboard made of `before` (or what committing its composition made of it), with what was put in cut
        // short, at a whole character, so that the text stays within MaxLength: as a browser's maxlength does with a paste,
        // and as Flutter's limit does once a composition is committed (one under way is left to the keyboard). What was
        // put in is what lies between the text the two share at the start and at the end. The start shared goes no
        // further than where the selection started, where typing puts things in, so a letter typed beside the same letter
        // is taken as typed where the caret was. A composition being committed was let through uncut while it was under
        // way, so all of it counts as put in. What follows what was put in moves back by what was cut, and a caret inside
        // what was cut comes to its end.
        private TextEditingValue Limit(in TextEditingValue before, in TextEditingValue after)
        {
            string old = before.Text, now = after.Text;
            bool committing = before.IsComposing;
            if (_maxLength <= 0 || after.IsComposing || now.Length <= _maxLength || (!committing && now.Length <= old.Length))
                return after;
            var at = committing ? before.Composing : before.Selection.Range;
            int shortest = Math.Min(old.Length, now.Length);
            int prefixMost = Math.Min(shortest, Math.Max(0, at.Start));
            int prefix = 0;
            while (prefix < prefixMost && old[prefix] == now[prefix])
                prefix++;
            int suffixMost = committing ? Math.Min(shortest - prefix, old.Length - at.End) : shortest - prefix;
            int suffix = 0;
            while (suffix < suffixMost && old[old.Length - 1 - suffix] == now[now.Length - 1 - suffix])
                suffix++;
            int end = now.Length - suffix;
            int room = Math.Max(0, _maxLength - (now.Length - (end - prefix)));
            int keptEnd = prefix + Math.Min(room, end - prefix);
            keptEnd = Math.Max(prefix, TextBoundaries.SnapToCaretStop(now, keptEnd));
            int cut = end - keptEnd;
            if (cut <= 0) return after;
            int Map(int index) => index <= keptEnd ? index : Math.Max(keptEnd, index - cut);
            var selection = after.Selection;
            return new TextEditingValue(now.Remove(keptEnd, cut), new TextSelection(Map(selection.Base), Map(selection.Extent)), TextRange.None);
        }
    }
}

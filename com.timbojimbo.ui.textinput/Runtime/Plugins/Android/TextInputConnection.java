package com.timbojimbo.textinput;

import android.text.Editable;
import android.view.KeyEvent;
import android.view.inputmethod.BaseInputConnection;
import android.view.inputmethod.ExtractedText;
import android.view.inputmethod.ExtractedTextRequest;

/**
 * What the keyboard talks to: BaseInputConnection over the session's mirror (as a full editor, so text arrives as
 * text, not key events), which already does composing, committing, deleting around the caret, selecting and the reads.
 * On top: batch edits that nest and report as the outermost ends (the base class's do nothing), the keys a soft
 * keyboard sends applied to the mirror (never passed on to Unity, which would handle them a second time), the return
 * key and editor actions sent to Unity, extracted text and cursor updates for keyboards that watch them, and the
 * clipboard actions keyboards offer.
 *
 * <p>A connection belongs to the session it was made for and works only while it is the view's newest: one replaced
 * (the input restarted, the session ended) answers nothing and changes nothing, though it still closes the batch edits
 * it opened, so that a keyboard that went away in the middle of one does not hold the session's notifications back.</p>
 */
final class TextInputConnection extends BaseInputConnection
{
    // TextEditIntent's values, for the keys handed to Unity.
    static final int INTENT_MOVE_UP = 2;
    static final int INTENT_MOVE_DOWN = 3;
    static final int INTENT_MOVE_LINE_START = 6;
    static final int INTENT_MOVE_LINE_END = 7;
    static final int INTENT_RETURN = 25;

    private final TextInputView mView;
    private final TextInputSession mSession;
    private int mBatchDepth;
    private ExtractedTextRequest mExtractRequest;
    private boolean mMonitorsCursor;

    TextInputConnection(TextInputView view, TextInputSession session)
    {
        super(view, true);
        mView = view;
        mSession = session;
    }

    TextInputSession session()
    {
        return mSession;
    }

    private boolean isActive()
    {
        return mView.isCurrent(this);
    }

    /** The request of a keyboard that watches the extracted text, or null. */
    ExtractedTextRequest extractRequest()
    {
        return mExtractRequest;
    }

    /** Whether the keyboard asked to be sent the cursor's position as it changes. */
    boolean monitorsCursor()
    {
        return mMonitorsCursor;
    }

    @Override
    public Editable getEditable()
    {
        return isActive() ? mSession.state : null;
    }

    @Override
    public boolean beginBatchEdit()
    {
        if (!isActive()) return false;
        mBatchDepth++;
        mSession.state.beginBatchEdit();
        return true;
    }

    @Override
    public boolean endBatchEdit()
    {
        if (mBatchDepth == 0) return false;
        mBatchDepth--;
        mSession.state.endBatchEdit();
        return mBatchDepth > 0 && isActive();
    }

    @Override
    public void closeConnection()
    {
        super.closeConnection();
        while (mBatchDepth > 0)
        {
            mBatchDepth--;
            mSession.state.endBatchEdit();
        }
        mExtractRequest = null;
        mMonitorsCursor = false;
    }

    @Override
    public boolean commitText(CharSequence text, int newCursorPosition)
    {
        if (!isActive()) return false;
        EditorConfig config = mSession.config;
        // The return key typed as text, as some keyboards send it: a new line only where it types one.
        if (text.length() == 1 && text.charAt(0) == '\n' && !config.returnInsertsNewline())
        {
            beginBatchEdit();
            mSession.state.finishComposing();
            mView.sendIntent(mSession, INTENT_RETURN, false);
            endBatchEdit();
            return true;
        }
        return super.commitText(config.multiline() ? text : EditingState.withoutNewlines(text), newCursorPosition);
    }

    @Override
    public boolean setComposingText(CharSequence text, int newCursorPosition)
    {
        if (!isActive()) return false;
        CharSequence composing = mSession.config.multiline() ? text : EditingState.withoutNewlines(text);
        beginBatchEdit();
        // Empty composing text commits, as Flutter has it: some keyboards end a composition so, and an empty composing
        // range would otherwise linger at the caret.
        boolean result = composing.length() == 0
            ? super.commitText(composing, newCursorPosition)
            : super.setComposingText(composing, newCursorPosition);
        endBatchEdit();
        return result;
    }

    @Override
    public boolean setSelection(int start, int end)
    {
        if (!isActive()) return false;
        beginBatchEdit();
        boolean result = super.setSelection(start, end);
        endBatchEdit();
        return result;
    }

    @Override
    public boolean sendKeyEvent(KeyEvent event)
    {
        if (!isActive()) return false;
        int action = event.getAction();
        if (action != KeyEvent.ACTION_DOWN && action != KeyEvent.ACTION_MULTIPLE)
            return handlesKey(event.getKeyCode());
        beginBatchEdit();
        boolean handled = applyKey(event);
        endBatchEdit();
        return handled;
    }

    private static boolean handlesKey(int keyCode)
    {
        switch (keyCode)
        {
            case KeyEvent.KEYCODE_DEL:
            case KeyEvent.KEYCODE_FORWARD_DEL:
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER:
            case KeyEvent.KEYCODE_DPAD_LEFT:
            case KeyEvent.KEYCODE_DPAD_RIGHT:
            case KeyEvent.KEYCODE_DPAD_UP:
            case KeyEvent.KEYCODE_DPAD_DOWN:
            case KeyEvent.KEYCODE_MOVE_HOME:
            case KeyEvent.KEYCODE_MOVE_END:
                return true;
            default:
                return false;
        }
    }

    // A key a soft keyboard sent (LatinIME sends digits and, when it has lost track of the caret, backspace as keys;
    // Gboard's cursor control sends arrows): applied to the mirror, or, for moves that need the field's lines, handed to
    // Unity. Inside a batch edit.
    private boolean applyKey(KeyEvent event)
    {
        EditingState state = mSession.state;
        boolean shift = event.isShiftPressed();
        switch (event.getKeyCode())
        {
            case KeyEvent.KEYCODE_DEL:
                state.deleteBackward();
                return true;
            case KeyEvent.KEYCODE_FORWARD_DEL:
                state.deleteForward();
                return true;
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER:
                if (mSession.config.returnInsertsNewline())
                {
                    state.replaceSelection("\n");
                }
                else
                {
                    state.finishComposing();
                    mView.sendIntent(mSession, INTENT_RETURN, false);
                }
                return true;
            case KeyEvent.KEYCODE_DPAD_LEFT:
                state.moveCaret(false, shift);
                return true;
            case KeyEvent.KEYCODE_DPAD_RIGHT:
                state.moveCaret(true, shift);
                return true;
            case KeyEvent.KEYCODE_DPAD_UP:
                mView.sendIntent(mSession, INTENT_MOVE_UP, shift);
                return true;
            case KeyEvent.KEYCODE_DPAD_DOWN:
                mView.sendIntent(mSession, INTENT_MOVE_DOWN, shift);
                return true;
            case KeyEvent.KEYCODE_MOVE_HOME:
                mView.sendIntent(mSession, INTENT_MOVE_LINE_START, shift);
                return true;
            case KeyEvent.KEYCODE_MOVE_END:
                mView.sendIntent(mSession, INTENT_MOVE_LINE_END, shift);
                return true;
            default:
                return typeKey(event);
        }
    }

    // A printable key: what it types replaces the selection. Shortcuts (Ctrl or Meta held) and control characters type
    // nothing.
    private boolean typeKey(KeyEvent event)
    {
        if (event.getAction() == KeyEvent.ACTION_MULTIPLE && event.getKeyCode() == KeyEvent.KEYCODE_UNKNOWN)
        {
            String characters = event.getCharacters();
            if (characters == null) return false;
            mSession.state.replaceSelection(mSession.config.multiline() ? characters
                : EditingState.withoutNewlines(characters));
            return true;
        }
        if (event.isCtrlPressed() || event.isMetaPressed()) return false;
        int character = event.getUnicodeChar();
        // Below a space are control characters; a dead key's accent comes with its top bit set, so it is negative.
        if (character < 0x20 || character == 0x7F) return false;
        mSession.state.replaceSelection(new String(Character.toChars(character)));
        return true;
    }

    @Override
    public boolean performEditorAction(int editorAction)
    {
        if (!isActive()) return false;
        mView.sendIntent(mSession, INTENT_RETURN, false);
        return true;
    }

    @Override
    public boolean performContextMenuAction(int id)
    {
        if (!isActive()) return false;
        beginBatchEdit();
        boolean handled = mView.applyEditAction(mSession, id);
        endBatchEdit();
        return handled;
    }

    @Override
    public ExtractedText getExtractedText(ExtractedTextRequest request, int flags)
    {
        if (!isActive()) return null;
        // A keyboard that asks to monitor it (Samsung's) is sent it again after every change.
        mExtractRequest = (flags & GET_EXTRACTED_TEXT_MONITOR) != 0 ? request : null;
        return extract();
    }

    /** The whole text and its selection, as a keyboard that watches the extracted text is sent it. */
    ExtractedText extract()
    {
        EditingState state = mSession.state;
        ExtractedText extracted = new ExtractedText();
        extracted.text = state.toString();
        extracted.startOffset = 0;
        extracted.partialStartOffset = -1;
        extracted.partialEndOffset = -1;
        extracted.selectionStart = state.selectionBase();
        extracted.selectionEnd = state.selectionExtent();
        extracted.flags = mSession.config.multiline() ? 0 : ExtractedText.FLAG_SINGLE_LINE;
        return extracted;
    }

    @Override
    public boolean requestCursorUpdates(int cursorUpdateMode)
    {
        if (!isActive()) return false;
        mMonitorsCursor = (cursorUpdateMode & CURSOR_UPDATE_MONITOR) != 0;
        if ((cursorUpdateMode & CURSOR_UPDATE_IMMEDIATE) != 0) mView.sendCursorAnchorInfo(mSession);
        return true;
    }

    // Android 13's form, whose filter (which parts of the cursor info the keyboard wants) the base class refuses.
    @Override
    public boolean requestCursorUpdates(int cursorUpdateMode, int cursorUpdateFilter)
    {
        return requestCursorUpdates(cursorUpdateMode);
    }
}

package com.timbojimbo.textinput;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.res.Configuration;
import android.graphics.Matrix;
import android.graphics.RectF;
import android.os.Build;
import android.view.MotionEvent;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewTreeObserver;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.inputmethod.CursorAnchorInfo;
import android.view.inputmethod.EditorBoundsInfo;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.ExtractedTextRequest;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputMethodManager;

import com.unity3d.player.UnityPlayer;

/**
 * The view the keyboard types into: one pixel in the corner of Unity's frame layout, drawing nothing and taking no
 * touches, which holds focus while a field is being edited and makes the keyboard's connection
 * ({@link TextInputConnection}) over the session's mirror. It must stay VISIBLE, since a view that is not cannot take
 * focus; it is focusable only while a session is under way, so it never takes focus otherwise.
 *
 * <p>The keyboard comes up as a session starts and goes down as it ends; moving from one session to the next keeps it
 * up and restarts the input. Focus is taken back when GameActivity takes it for its surface (it does so each time the
 * window regains focus), but never from another real editor, which ends the session instead. The keyboard hidden by the
 * user (back, or its own hide key) ends the session, while the window has focus and no hardware keyboard is attached.</p>
 *
 * <p>The keyboard is told of every change of the mirror, whoever made it, with {@code updateSelection} (which skips
 * what it has already been told), and is restarted only where it must be: a new session or config, a composition our
 * side changed, the text replaced as a whole or changed with the selection where it was, which updateSelection cannot
 * tell it (before Android 13, which invalidates the input instead), a hardware keyboard come or gone, and the keyboard
 * hidden while the session goes on (Samsung's keeps stale state otherwise). Restarting on every edit, as Unity's own
 * keyboard does, is what makes its autocorrect misbehave.</p>
 */
final class TextInputView extends View implements EditingState.Listener
{
    // TextChangeKind's values: how a value pushed differs from what the keyboard last saw.
    private static final int CHANGE_COMPOSING = 2;
    private static final int CHANGE_WHOLESALE = 3;

    private final UnityPlayer mPlayer;
    private final InputMethodManager mImm;
    private final KeyboardTracker mTracker;
    private final EditMenu mEditMenu;

    private TextInputSession mSession;
    // The newest connection made; any older one is inert.
    private TextInputConnection mConnection;
    // Whether the keyboard should be up: from a session's start until it ends or the user hides the keyboard.
    private boolean mWantVisible;
    private boolean mImeVisible;
    private int mHardKeyboardHidden;

    private final Runnable mShow = this::show;
    private final Runnable mReclaimFocus = this::reclaimFocus;
    private final ViewTreeObserver.OnGlobalFocusChangeListener mFocusListener = this::onGlobalFocusChanged;
    private final CursorAnchorInfo.Builder mAnchorInfo = new CursorAnchorInfo.Builder();
    private final Matrix mMatrix = new Matrix();
    private final int[] mLocation = new int[2];

    TextInputView(Activity activity, UnityPlayer player)
    {
        super(activity);
        mPlayer = player;
        mImm = (InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE);
        mTracker = KeyboardTracker.create(this);
        mEditMenu = new EditMenu(this);
        mHardKeyboardHidden = getResources().getConfiguration().hardKeyboardHidden;
        setWillNotDraw(true);
        setFocusable(false);
        setImportantForAutofill(IMPORTANT_FOR_AUTOFILL_NO);
        setDefaultFocusHighlightEnabled(false);
    }

    /** Unity's surface, which Unity's screen maps onto: the keyboard is measured against it and the edit menu anchored on it. */
    SurfaceView surface()
    {
        return mPlayer.getSurfaceView();
    }

    // ── Sessions (from TextInputBridge) ─────────────────────────────────────

    void attach(int id, EditorConfig config, String text, int base, int extent, int composingStart, int composingEnd,
                int serial)
    {
        mEditMenu.hide();
        TextInputSession session = new TextInputSession(id, config, new EditingState(this, this));
        session.state.apply(text, base, extent, composingStart, composingEnd);
        session.appliedSerial = serial;
        mSession = session;
        mWantVisible = true;
        setFocusableInTouchMode(true);
        // Already focused, moving from one session to the next: the keyboard stays up, restarted for this one.
        if (isFocused())
            mImm.restartInput(this);
        else
            requestFocus();
        // Posted, so that it comes after the input method has bound to this view on its focus.
        removeCallbacks(mShow);
        post(mShow);
    }

    void detach(int id)
    {
        TextInputSession session = mSession;
        if (session == null || session.id != id) return;
        mEditMenu.hide();
        mSession = null;
        mConnection = null;
        mWantVisible = false;
        removeCallbacks(mShow);
        removeCallbacks(mReclaimFocus);
        // Unless another editor took focus (and the keyboard with it), the keyboard goes down and focus goes back to
        // Unity's own view, which has no editor: clearing it instead would leave nothing focused in touch mode.
        if (isFocused())
        {
            hideKeyboard();
            mPlayer.getView().requestFocus();
        }
        setFocusable(false);
    }

    void setConfig(int id, EditorConfig config)
    {
        TextInputSession session = current(id);
        if (session == null) return;
        session.config = config;
        restartInput(session);
    }

    void setValue(int id, int serial, String text, int base, int extent, int composingStart, int composingEnd, int kind)
    {
        TextInputSession session = current(id);
        if (session == null) return;
        // Never in the middle of the keyboard's batch edit: put in as it ends, after its edit is reported.
        if (session.state.inBatchEdit())
            session.deferValue(serial, text, base, extent, composingStart, composingEnd, kind);
        else
            applyValue(session, serial, text, base, extent, composingStart, composingEnd, kind);
    }

    /**
     * Puts a value pushed from Unity into the mirror and tells the keyboard as {@code kind} asks, if anything changed.
     * Text changed with the selection and composing range where they were (text set from code, the caret where it
     * stood; an undo of the same length) is told as a wholesale change: {@code updateSelection}, which compares those
     * offsets alone, would tell the keyboard nothing, and it would go on suggesting from the text it had.
     */
    void applyValue(TextInputSession session, int serial, String text, int base, int extent, int composingStart,
                    int composingEnd, int kind)
    {
        int applied = session.state.apply(text, base, extent, composingStart, composingEnd);
        session.appliedSerial = serial;
        if (applied == EditingState.APPLIED_NOTHING) return;
        if (kind == CHANGE_COMPOSING)
            restartInput(session);
        else if (kind == CHANGE_WHOLESALE || applied == EditingState.APPLIED_TEXT_ONLY)
        {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
                mImm.invalidateInput(this);
            else
                restartInput(session);
        }
        else
            notifyKeyboard(session);
    }

    void setGeometry(int id, float fieldLeft, float fieldTop, float fieldRight, float fieldBottom, float caretLeft,
                     float caretTop, float caretRight, float caretBottom)
    {
        TextInputSession session = current(id);
        if (session == null) return;
        session.field.set(fieldLeft, fieldTop, fieldRight, fieldBottom);
        session.caret.set(caretLeft, caretTop, caretRight, caretBottom);
        session.hasGeometry = true;
        TextInputConnection connection = mConnection;
        if (connection != null && isCurrent(connection) && connection.monitorsCursor()) sendCursorAnchorInfo(session);
    }

    void showEditMenu(int id, float left, float top, float right, float bottom, int actions)
    {
        TextInputSession session = current(id);
        if (session != null) mEditMenu.show(session, left, top, right, bottom, actions);
    }

    void hideEditMenu()
    {
        mEditMenu.hide();
    }

    private TextInputSession current(int id)
    {
        TextInputSession session = mSession;
        return session != null && session.id == id ? session : null;
    }

    /** Whether {@code connection} is the one the keyboard should be using: the newest, made for the session under way. */
    boolean isCurrent(TextInputConnection connection)
    {
        return connection == mConnection && connection.session() == mSession;
    }

    // ── The keyboard ────────────────────────────────────────────────────────

    @Override
    public boolean onCheckIsTextEditor()
    {
        return mSession != null;
    }

    @Override
    public InputConnection onCreateInputConnection(EditorInfo info)
    {
        TextInputSession session = mSession;
        if (session == null)
        {
            mConnection = null;
            return null;
        }
        session.config.fill(info);
        TextInputConnection connection = new TextInputConnection(this, session);
        mConnection = connection;
        EditingState state = session.state;
        info.initialSelStart = state.selectionBase();
        info.initialSelEnd = state.selectionExtent();
        info.initialCapsMode = connection.getCursorCapsMode(info.inputType);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && !session.config.secure())
            info.setInitialSurroundingText(state.toString());
        return connection;
    }

    @Override
    public void onBatchEnded(EditingState state, boolean changed)
    {
        TextInputSession session = mSession;
        // The last batch edit of a session that has ended, closing late: nothing to tell anyone.
        if (session == null || session.state != state) return;
        if (changed)
        {
            notifyKeyboard(session);
            TextInputBridge.queueEdit(session.id, session.appliedSerial, state);
        }
        session.flushIntents();
        session.applyPending(this);
    }

    /** Sends Unity an editing key: at once, or, during a batch edit, after the batch's edit as it ends. */
    void sendIntent(TextInputSession session, int intent, boolean extend)
    {
        if (session.state.inBatchEdit())
            session.deferIntent(intent, extend);
        else
            TextInputBridge.queueIntent(session.id, intent, extend);
    }

    // The keyboard is told where the selection and composition now are, and, if it watches them, the text and the
    // cursor's position.
    private void notifyKeyboard(TextInputSession session)
    {
        EditingState state = session.state;
        mImm.updateSelection(this, state.selectionBase(), state.selectionExtent(), state.composingStart(),
            state.composingEnd());
        TextInputConnection connection = mConnection;
        if (connection == null || !isCurrent(connection)) return;
        ExtractedTextRequest request = connection.extractRequest();
        if (request != null) mImm.updateExtractedText(this, request.token, connection.extract());
        if (connection.monitorsCursor()) sendCursorAnchorInfo(session);
    }

    /**
     * Sends the keyboard where the caret and the field are (for its candidate window and handwriting), in Unity's
     * surface's pixels with the matrix that takes them to the screen.
     */
    void sendCursorAnchorInfo(TextInputSession session)
    {
        EditingState state = session.state;
        CursorAnchorInfo.Builder info = mAnchorInfo;
        info.reset();
        info.setSelectionRange(state.selectionMin(), state.selectionMax());
        int composingStart = state.composingStart();
        if (composingStart >= 0)
            info.setComposingText(composingStart, state.subSequence(composingStart, state.composingEnd()));
        if (session.hasGeometry)
        {
            SurfaceView surface = surface();
            float width = surface.getWidth();
            float height = surface.getHeight();
            RectF caret = session.caret;
            info.setInsertionMarkerLocation(caret.left * width, caret.top * height, caret.bottom * height,
                caret.bottom * height, CursorAnchorInfo.FLAG_HAS_VISIBLE_REGION);
            surface.getLocationOnScreen(mLocation);
            mMatrix.setTranslate(mLocation[0], mLocation[1]);
            info.setMatrix(mMatrix);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
            {
                RectF field = new RectF(session.field.left * width, session.field.top * height,
                    session.field.right * width, session.field.bottom * height);
                info.setEditorBoundsInfo(
                    new EditorBoundsInfo.Builder().setEditorBounds(field).setHandwritingBounds(field).build());
            }
        }
        mImm.updateCursorAnchorInfo(this, info.build());
    }

    /**
     * Restarts the keyboard's input on this view, which makes a new connection and gives the keyboard the mirror afresh.
     * A composition is committed first, as the keyboard starts again without one (and Unity is told).
     */
    private void restartInput(TextInputSession session)
    {
        EditingState state = session.state;
        if (state.composingStart() >= 0)
        {
            state.beginBatchEdit();
            state.finishComposing();
            state.endBatchEdit();
        }
        mImm.restartInput(this);
    }

    private void show()
    {
        if (mSession == null || !mWantVisible || !isFocused()) return;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R)
        {
            // Scheduled by the system for once the window has focus and the input method has bound to this view.
            WindowInsetsController controller = getWindowInsetsController();
            if (controller != null) controller.show(WindowInsets.Type.ime());
        }
        else if (hasWindowFocus())
        {
            // Ignored before the window has focus; onWindowFocusChanged shows it then.
            mImm.showSoftInput(this, 0);
        }
    }

    private void hideKeyboard()
    {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R)
        {
            WindowInsetsController controller = getWindowInsetsController();
            if (controller != null) controller.hide(WindowInsets.Type.ime());
        }
        else
        {
            mImm.hideSoftInputFromWindow(getWindowToken(), 0);
        }
    }

    /** The keyboard tracker saw the keyboard come up or go down. */
    void onImeVisibilityChanged(boolean visible)
    {
        boolean wasVisible = mImeVisible;
        mImeVisible = visible;
        TextInputSession session = mSession;
        if (visible || !wasVisible || session == null || !mWantVisible) return;
        // Hidden by the user while the session goes on: that ends it. Not while the window is away (the app in the
        // background, a dialog up: it comes back with the window's focus), nor with a hardware keyboard to type on.
        if (hasWindowFocus() && !hardwareKeyboardAttached())
            end(session);
        else
            restartInput(session);
    }

    private boolean hardwareKeyboardAttached()
    {
        return getResources().getConfiguration().hardKeyboardHidden == Configuration.HARDKEYBOARDHIDDEN_NO;
    }

    // The session is over on this side (the user or another editor ended it): Unity is told, and detaches it.
    private void end(TextInputSession session)
    {
        mWantVisible = false;
        TextInputBridge.queueEnded(session.id);
    }

    // ── Focus ───────────────────────────────────────────────────────────────

    @Override
    public void onWindowFocusChanged(boolean hasWindowFocus)
    {
        super.onWindowFocusChanged(hasWindowFocus);
        // Runs after GameActivity's own handler has given its surface focus, and before the input method binds to
        // whatever has focus: taking it back here is what the keyboard then serves.
        if (!hasWindowFocus || mSession == null) return;
        reclaimFocus();
    }

    private void onGlobalFocusChanged(View oldFocus, View newFocus)
    {
        if (mSession == null || oldFocus != this || newFocus == this) return;
        if (newFocus == null || isUnityView(newFocus))
            post(mReclaimFocus);
        else if (newFocus.onCheckIsTextEditor())
            end(mSession);
    }

    // Takes focus back from Unity's views (never from another editor) and shows the keyboard again if it should be up.
    private void reclaimFocus()
    {
        if (mSession == null || !hasWindowFocus()) return;
        if (!isFocused())
        {
            View focused = getRootView().findFocus();
            if (focused != null && !isUnityView(focused)) return;
            requestFocus();
        }
        if (mWantVisible)
        {
            removeCallbacks(mShow);
            post(mShow);
        }
    }

    private boolean isUnityView(View view)
    {
        return view == mPlayer.getView() || view == mPlayer.getSurfaceView() || view == mPlayer.getFrameLayout();
    }

    @Override
    protected void onConfigurationChanged(Configuration newConfig)
    {
        super.onConfigurationChanged(newConfig);
        if (newConfig.hardKeyboardHidden == mHardKeyboardHidden) return;
        mHardKeyboardHidden = newConfig.hardKeyboardHidden;
        // A hardware keyboard came or went: the keyboard starts again, and the soft one comes back if it went.
        TextInputSession session = mSession;
        if (session == null) return;
        restartInput(session);
        if (mWantVisible)
        {
            removeCallbacks(mShow);
            post(mShow);
        }
    }

    @Override
    protected void onAttachedToWindow()
    {
        super.onAttachedToWindow();
        getViewTreeObserver().addOnGlobalFocusChangeListener(mFocusListener);
        mTracker.start();
    }

    @Override
    protected void onDetachedFromWindow()
    {
        mTracker.stop();
        getViewTreeObserver().removeOnGlobalFocusChangeListener(mFocusListener);
        mEditMenu.hide();
        super.onDetachedFromWindow();
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent event)
    {
        return false;
    }

    // ── The clipboard ───────────────────────────────────────────────────────

    /**
     * Select all, cut, copy or paste on the mirror (from the edit menu or the keyboard's own), inside a batch edit, so
     * the change goes to Unity as an edit. Nothing is copied or cut from a password; the clipboard is read only on paste.
     */
    boolean applyEditAction(TextInputSession session, int id)
    {
        EditingState state = session.state;
        if (id == android.R.id.selectAll)
        {
            state.selectAll();
            return true;
        }
        if (id == android.R.id.cut || id == android.R.id.copy)
        {
            if (session.config.secure() || state.selectionMin() == state.selectionMax()) return true;
            clipboard().setPrimaryClip(ClipData.newPlainText(null, state.selectedText()));
            if (id == android.R.id.cut) state.replaceSelection("");
            return true;
        }
        if (id == android.R.id.paste || id == android.R.id.pasteAsPlainText)
        {
            ClipData clip = clipboard().getPrimaryClip();
            if (clip == null || clip.getItemCount() == 0) return true;
            CharSequence text = clip.getItemAt(0).coerceToText(getContext()).toString();
            state.replaceSelection(session.config.multiline() ? text : EditingState.withoutNewlines(text));
            return true;
        }
        return false;
    }

    /** An edit menu item chosen: applied to the mirror if its session is still the one under way. */
    void performMenuAction(TextInputSession session, int id)
    {
        if (session != mSession) return;
        session.state.beginBatchEdit();
        applyEditAction(session, id);
        session.state.endBatchEdit();
    }

    ClipboardManager clipboard()
    {
        return (ClipboardManager) getContext().getSystemService(Context.CLIPBOARD_SERVICE);
    }
}

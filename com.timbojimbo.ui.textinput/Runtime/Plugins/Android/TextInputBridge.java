package com.timbojimbo.textinput;

import android.app.Activity;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.ViewGroup;
import android.widget.FrameLayout;

import com.unity3d.player.IUnityPlayerSupport;
import com.unity3d.player.UnityPlayer;

/**
 * What Unity's AndroidTextInputBackend (C#) calls and polls: the one door between Unity's main thread, where C# runs,
 * and Android's UI thread, where the keyboard and every view live.
 *
 * <p>Calls in are made on Unity's thread and only post their work to the UI thread, never waiting for it (Unity's own
 * keyboard blocks its thread for up to 400 ms on show and hide; this never does). They carry the session they are for,
 * and the view drops what belongs to a session that is no longer its own.</p>
 *
 * <p>What the keyboard does goes the other way as events, queued on the UI thread under a lock with a counter that
 * rises with each: once a frame C# reads the counter (one call, nothing allocated) and, only when it has moved, drains
 * them all as one string. The keyboard's height and visibility are plain fields C# reads each frame.</p>
 *
 * <p>The drained string is a run of events, each a letter, then decimal numbers each ended by one character:
 * <ul>
 * <li>{@code E}session,baseSerial,selectionBase,selectionExtent,composingStart,composingEnd,length{@code :} and then
 * exactly that many UTF-16 units of text: the keyboard changed the value (no composition is -1,-1);</li>
 * <li>{@code I}session,intent,extend{@code ;}: an editing key, intent being a TextEditIntent's value;</li>
 * <li>{@code X}session{@code ;}: the user ended the session (dismissed the keyboard), or another editor took focus.</li>
 * </ul>
 * The text is counted rather than delimited, so it can hold anything. Consecutive edits of a session are merged into
 * the latest, which carries the whole value; an edit is never merged across an intent, so they stay in order.</p>
 */
public final class TextInputBridge
{
    private static final String TAG = "TextInput";

    private static final Handler sMain = new Handler(Looper.getMainLooper());

    // The view the keyboard types into, made on the first attach (UI thread only).
    private static TextInputView sView;

    private static final Object sLock = new Object();
    private static final StringBuilder sEvents = new StringBuilder();
    // Where the last queued event starts, while it is an edit (for sLastEditSession) that a newer one replaces; else -1.
    private static int sLastEditStart = -1;
    private static int sLastEditSession;
    private static volatile long sChanges;

    private static volatile float sKeyboardFraction;
    private static volatile boolean sKeyboardVisible;

    private TextInputBridge()
    {
    }

    // ── Unity's thread ──────────────────────────────────────────────────────

    /**
     * A session starts: the keyboard comes up for a field with this config, holding this value as of {@code serial}. A
     * session under way is replaced without the keyboard going down (moving from one field to the next).
     */
    public static void attach(final int session, int contentType, int returnKey, int capitalization, int flags,
                              String text, final int selectionBase, final int selectionExtent, final int composingStart,
                              final int composingEnd, final int serial)
    {
        final EditorConfig config = new EditorConfig(contentType, returnKey, capitalization, flags);
        final String value = text != null ? text : "";
        sMain.post(() ->
        {
            TextInputView view = viewForAttach();
            if (view != null)
                view.attach(session, config, value, selectionBase, selectionExtent, composingStart, composingEnd, serial);
        });
    }

    /** The session ends: the keyboard goes down and focus goes back to Unity's view. */
    public static void detach(final int session)
    {
        sMain.post(() ->
        {
            if (sView != null) sView.detach(session);
        });
    }

    /** The field's config changed while editing: the keyboard is restarted with it. */
    public static void setConfig(final int session, int contentType, int returnKey, int capitalization, int flags)
    {
        final EditorConfig config = new EditorConfig(contentType, returnKey, capitalization, flags);
        sMain.post(() ->
        {
            if (sView != null) sView.setConfig(session, config);
        });
    }

    /**
     * The field changed its value itself (serial {@code serial}); {@code kind} is a TextChangeKind's value, saying how
     * it differs from what the keyboard last saw and so how the keyboard is told.
     */
    public static void setValue(final int session, final int serial, String text, final int selectionBase,
                                final int selectionExtent, final int composingStart, final int composingEnd, final int kind)
    {
        final String value = text != null ? text : "";
        sMain.post(() ->
        {
            if (sView != null)
                sView.setValue(session, serial, value, selectionBase, selectionExtent, composingStart, composingEnd, kind);
        });
    }

    /**
     * Where the field and its caret are, as fractions of Unity's view (left, top, right, bottom; y down), for the
     * keyboard's cursor anchor info.
     */
    public static void setGeometry(final int session, final float fieldLeft, final float fieldTop, final float fieldRight,
                                   final float fieldBottom, final float caretLeft, final float caretTop,
                                   final float caretRight, final float caretBottom)
    {
        sMain.post(() ->
        {
            if (sView != null)
                sView.setGeometry(session, fieldLeft, fieldTop, fieldRight, fieldBottom, caretLeft, caretTop, caretRight,
                    caretBottom);
        });
    }

    /**
     * Shows the edit menu (Android's floating text toolbar) by a target given as fractions of Unity's view (y down),
     * offering {@code actions} (a TextEditActions value).
     */
    public static void showEditMenu(final int session, final float left, final float top, final float right,
                                    final float bottom, final int actions)
    {
        sMain.post(() ->
        {
            if (sView != null) sView.showEditMenu(session, left, top, right, bottom, actions);
        });
    }

    /** Hides the edit menu, if it shows. */
    public static void hideEditMenu()
    {
        sMain.post(() ->
        {
            if (sView != null) sView.hideEditMenu();
        });
    }

    /** Rises with each event queued: C# drains only when it has moved. */
    public static long changeCounter()
    {
        return sChanges;
    }

    /** Every event queued since the last drain, encoded (see the class), oldest first; empty when there are none. */
    public static String drain()
    {
        synchronized (sLock)
        {
            String events = sEvents.toString();
            sEvents.setLength(0);
            sLastEditStart = -1;
            return events;
        }
    }

    /**
     * How much of Unity's view, from its bottom up, a docked software keyboard covers: a fraction of its height (C#
     * multiplies it by Screen.height). A floating keyboard covers none.
     */
    public static float keyboardFraction()
    {
        return sKeyboardFraction;
    }

    /** Whether a software keyboard is up, docked or floating. */
    public static boolean keyboardVisible()
    {
        return sKeyboardVisible;
    }

    /**
     * Whether {@link #keyboardFraction} follows the keyboard as it slides, frame by frame (Android 11 and later); before,
     * it steps once the keyboard has moved, and C# eases it.
     */
    public static boolean keyboardAnimates()
    {
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.R;
    }

    // ── UI thread ───────────────────────────────────────────────────────────

    /** Queues an edit: the keyboard changed the value of {@code session}, last given serial {@code baseSerial}. */
    static void queueEdit(int session, int baseSerial, EditingState state)
    {
        String text = state.toString();
        synchronized (sLock)
        {
            if (sLastEditStart >= 0 && sLastEditSession == session)
                sEvents.setLength(sLastEditStart);
            sLastEditStart = sEvents.length();
            sLastEditSession = session;
            sEvents.append('E').append(session).append(',').append(baseSerial).append(',')
                .append(state.selectionBase()).append(',').append(state.selectionExtent()).append(',')
                .append(state.composingStart()).append(',').append(state.composingEnd()).append(',')
                .append(text.length()).append(':').append(text);
            sChanges++;
        }
    }

    /** Queues an editing key for {@code session}: a TextEditIntent's value, with shift held or not. */
    static void queueIntent(int session, int intent, boolean extend)
    {
        synchronized (sLock)
        {
            sLastEditStart = -1;
            sEvents.append('I').append(session).append(',').append(intent).append(',').append(extend ? 1 : 0)
                .append(';');
            sChanges++;
        }
    }

    /** Queues the end of {@code session}, which the user or the system ended. */
    static void queueEnded(int session)
    {
        synchronized (sLock)
        {
            sLastEditStart = -1;
            sEvents.append('X').append(session).append(';');
            sChanges++;
        }
    }

    /** What the keyboard tracker measured. */
    static void setKeyboard(float fraction, boolean visible)
    {
        sKeyboardFraction = fraction;
        sKeyboardVisible = visible;
    }

    /**
     * The view for a session to start in: in the frame layout of the current activity's Unity player (GameActivity's,
     * or the plain activity's), made there on first use, and made again if the activity was.
     */
    private static TextInputView viewForAttach()
    {
        Activity activity = UnityPlayer.currentActivity;
        UnityPlayer player = activity instanceof IUnityPlayerSupport
            ? ((IUnityPlayerSupport) activity).getUnityPlayerConnection()
            : null;
        if (player == null)
        {
            Log.w(TAG, "No Unity player in the current activity to put the text input view in; the keyboard stays down.");
            return null;
        }
        FrameLayout frame = player.getFrameLayout();
        if (sView != null && sView.getParent() == frame) return sView;
        if (sView != null && sView.getParent() instanceof ViewGroup)
            ((ViewGroup) sView.getParent()).removeView(sView);
        sView = new TextInputView(activity, player);
        frame.addView(sView, new FrameLayout.LayoutParams(1, 1));
        return sView;
    }
}

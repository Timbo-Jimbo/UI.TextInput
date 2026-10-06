package com.timbojimbo.textinput;

import android.graphics.Rect;
import android.os.Build;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewTreeObserver;
import android.view.WindowInsets;
import android.view.WindowInsetsAnimation;

import java.util.List;

/**
 * Follows the software keyboard: how much of Unity's surface it covers from the bottom up, as a fraction of the
 * surface's height (a docked keyboard's overlap with the surface as it is on screen, so a status bar or a window that
 * is not full screen is accounted for, and a floating keyboard covers none), and whether one is up at all. Installed
 * on the text input view alone: a view has one insets listener and one animation callback, and Unity's frame layout and
 * GameActivity's surface have theirs.
 */
abstract class KeyboardTracker
{
    protected final TextInputView mView;
    protected final int[] mLocation = new int[2];

    KeyboardTracker(TextInputView view)
    {
        mView = view;
    }

    static KeyboardTracker create(TextInputView view)
    {
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.R ? new Animated(view) : new Stepped(view);
    }

    /** Starts following, as the view is attached to its window. */
    abstract void start();

    /** Stops, as the view is detached. */
    abstract void stop();

    // The share of the surface's height below a keyboard whose top edge is at keyboardTop (screen pixels).
    protected float coveredBelow(int keyboardTop)
    {
        SurfaceView surface = mView.surface();
        if (surface.getHeight() <= 0) return 0f;
        surface.getLocationOnScreen(mLocation);
        float covered = (mLocation[1] + surface.getHeight() - keyboardTop) / (float) surface.getHeight();
        return Math.max(0f, Math.min(1f, covered));
    }

    protected void publish(float fraction, boolean visible)
    {
        TextInputBridge.setKeyboard(fraction, visible);
        mView.onImeVisibilityChanged(visible);
    }

    /**
     * Android 11 and later: the keyboard's insets, frame by frame as it slides (WindowInsetsAnimation), and as they
     * settle without an animation (a rotation, a hardware keyboard). While a keyboard animation runs, the insets the
     * window is laid out with are already its end state, so only the animation's own progress is followed.
     */
    private static final class Animated extends KeyboardTracker
    {
        private final ImeAnimation mAnimation = new ImeAnimation();
        private final View.OnApplyWindowInsetsListener mInsetsListener = this::onApplyWindowInsets;
        private int mRunning;
        private boolean mVisible;
        private float mFraction;

        Animated(TextInputView view)
        {
            super(view);
        }

        @Override
        void start()
        {
            mView.setWindowInsetsAnimationCallback(mAnimation);
            mView.setOnApplyWindowInsetsListener(mInsetsListener);
            mView.requestApplyInsets();
        }

        @Override
        void stop()
        {
            mView.setWindowInsetsAnimationCallback(null);
            mView.setOnApplyWindowInsetsListener(null);
            mRunning = 0;
        }

        // Left unconsumed: the insets are only read here.
        private WindowInsets onApplyWindowInsets(View view, WindowInsets insets)
        {
            mVisible = insets.isVisible(WindowInsets.Type.ime());
            if (mRunning == 0) mFraction = fractionOf(insets);
            publish(mFraction, mVisible);
            return insets;
        }

        // The keyboard's top edge is its inset up from the bottom of the window.
        private float fractionOf(WindowInsets insets)
        {
            View root = mView.getRootView();
            root.getLocationOnScreen(mLocation);
            int windowBottom = mLocation[1] + root.getHeight();
            return coveredBelow(windowBottom - insets.getInsets(WindowInsets.Type.ime()).bottom);
        }

        private static boolean isIme(WindowInsetsAnimation animation)
        {
            return (animation.getTypeMask() & WindowInsets.Type.ime()) != 0;
        }

        private final class ImeAnimation extends WindowInsetsAnimation.Callback
        {
            ImeAnimation()
            {
                super(DISPATCH_MODE_CONTINUE_ON_SUBTREE);
            }

            @Override
            public void onPrepare(WindowInsetsAnimation animation)
            {
                if (isIme(animation)) mRunning++;
            }

            @Override
            public WindowInsets onProgress(WindowInsets insets, List<WindowInsetsAnimation> runningAnimations)
            {
                if (mRunning > 0)
                {
                    mFraction = fractionOf(insets);
                    publish(mFraction, mVisible);
                }
                return insets;
            }

            @Override
            public void onEnd(WindowInsetsAnimation animation)
            {
                if (!isIme(animation) || mRunning == 0 || --mRunning > 0) return;
                WindowInsets insets = mView.getRootWindowInsets();
                if (insets == null) return;
                mVisible = insets.isVisible(WindowInsets.Type.ime());
                mFraction = fractionOf(insets);
                publish(mFraction, mVisible);
            }
        }
    }

    /**
     * Android 8 to 10: Unity sets FLAG_FULLSCREEN there, which turns adjustResize off and keeps the keyboard out of the
     * window's insets, so it is measured from the window's visible frame on each layout, as GameActivity itself does
     * there. Its height steps once the keyboard has moved; Unity's side eases it.
     *
     * <p>Whether one is up is judged against this window, as androidx's WindowInsetsCompat judges it there: a window
     * that stops short of the display's bottom (the top half of split screen, a freeform window) has nothing hidden below
     * it until a keyboard reaches it. A keyboard that does not reach the window, or one that floats, leaves the visible
     * frame whole and reads as no keyboard, so a docked keyboard switched to floating looks the same as one hidden.</p>
     */
    private static final class Stepped extends KeyboardTracker implements ViewTreeObserver.OnGlobalLayoutListener
    {
        // More of the window than this hidden at its bottom is a keyboard; less is a navigation bar.
        private static final float KEYBOARD_SHARE = 0.15f;

        private final Rect mFrame = new Rect();

        Stepped(TextInputView view)
        {
            super(view);
        }

        @Override
        void start()
        {
            mView.getViewTreeObserver().addOnGlobalLayoutListener(this);
            onGlobalLayout();
        }

        @Override
        void stop()
        {
            mView.getViewTreeObserver().removeOnGlobalLayoutListener(this);
        }

        @Override
        public void onGlobalLayout()
        {
            mView.getWindowVisibleDisplayFrame(mFrame);
            View root = mView.getRootView();
            root.getLocationOnScreen(mLocation);
            int windowBottom = mLocation[1] + root.getHeight();
            boolean visible = windowBottom - mFrame.bottom > root.getHeight() * KEYBOARD_SHARE;
            publish(coveredBelow(mFrame.bottom), visible);
        }
    }
}

// Follows the software keyboard, as Flutter's KeyboardInsetManager does: from UIKit's will-show, will-change-frame and
// will-hide notifications it works out where the keyboard is going (how far up a docked keyboard will cover the Unity
// view; a floating or split keyboard covers nothing, as UIKeyboardLayoutGuide's default has it) and how it moves there,
// by reading the animation UIKit gives a view animated inside the notification. C# evaluates that move frame by frame,
// so that layout follows the keyboard exactly.
//
// It also gives Unity its audio back after dictation, which takes the audio session; Unity's own keyboard does this for
// its own text view only.

#import "TJTextInputInternal.h"

#import <AVFoundation/AVFoundation.h>
#import <QuartzCore/QuartzCore.h>

#include <math.h>

extern "C" UIView* UnityGetGLView(void);

typedef enum
{
    TJKeyboardHidden,
    TJKeyboardDocked,
    TJKeyboardFloating,
} TJKeyboardMode;

#pragma mark - Dictation

// Dictation has been used since the audio was last given back.
static BOOL s_dictationUsed;

// Unity restarts its audio on AVAudioSessionInterruptionNotification (Ended), which iOS does not promise after dictation
// had the audio session, so Unity's own keyboard posts it itself (Trampoline Keyboard.mm keyboardDidHide:). This posts it
// once dictation is over, after UIKit's call that said so has returned.
static void TJGiveAudioBack(void)
{
    if (!s_dictationUsed)
        return;
    s_dictationUsed = NO;
    dispatch_async(dispatch_get_main_queue(), ^{
        [NSNotificationCenter.defaultCenter postNotificationName:AVAudioSessionInterruptionNotification
                                                          object:AVAudioSession.sharedInstance
                                                        userInfo:@{AVAudioSessionInterruptionTypeKey : @(AVAudioSessionInterruptionTypeEnded)}];
    });
}

void TJTIDictationRecordingDidEnd(void)
{
    s_dictationUsed = YES;
    TJGiveAudioBack();
}

#pragma mark - Where the keyboard is

// In Slide Over the keyboard's frame leaves out the screen below the app, though the keyboard sits at the bottom of the
// screen; this is how far to move it down to tell it docked (KeyboardInsetManager.swift calculateMultitaskingAdjustment).
static CGFloat TJSlideOverOffset(CGRect end, UIScreen* screen, UIView* view)
{
    UITraitCollection* traits = view.traitCollection;
    if (traits.userInterfaceIdiom != UIUserInterfaceIdiomPad || traits.horizontalSizeClass != UIUserInterfaceSizeClassCompact
        || traits.verticalSizeClass != UIUserInterfaceSizeClassRegular)
        return 0;
    CGFloat screenHeight = screen.bounds.size.height;
    // Stage Manager meets the same traits, but there the keyboard's frame reaches the bottom of the screen.
    if (screenHeight == CGRectGetMaxY(end))
        return 0;
    CGRect viewOnScreen = [view convertRect:view.bounds toCoordinateSpace:screen.coordinateSpace];
    CGFloat offset = screenHeight - CGRectGetMaxY(viewOnScreen);
    return offset > 0 ? offset : 0;
}

// Docked at the bottom of the screen, floating (undocked, split, or the iPad's shortcuts bar dragged away), or hidden
// (KeyboardInsetManager.swift calculateKeyboardAttachMode). `end` is in the screen's coordinate space.
static TJKeyboardMode TJClassify(CGRect end, UIScreen* screen, UIView* view)
{
    // A frame of all zeros is the shortcuts bar dropped after being dragged.
    if (CGRectEqualToRect(end, CGRectZero))
        return TJKeyboardFloating;
    if (CGRectIsEmpty(end) || screen == nil)
        return TJKeyboardHidden;

    CGRect screenRect = screen.bounds;
    CGRect adjusted = end;
    adjusted.origin.y += TJSlideOverOffset(end, screen, view);
    CGRect seen = CGRectIntersection(adjusted, screenRect);
    // Keyboard extensions can be off by a fraction of a point, hence the rounding.
    if (round(seen.size.height) > 0 && seen.size.width > 0)
        return round(CGRectGetMaxY(adjusted)) < screenRect.size.height ? TJKeyboardFloating : TJKeyboardDocked;
    return TJKeyboardHidden;
}

// How far up from the bottom of the Unity view a docked keyboard ending at `end` covers it, in points. The frame is in
// the screen's coordinate space, which is not the view's in Split View, Slide Over and Stage Manager.
static float TJDockedInset(CGRect end, UIScreen* screen, UIView* view)
{
    CGRect inView = [screen.coordinateSpace convertRect:end toCoordinateSpace:view];
    CGRect bounds = view.bounds;
    if (!CGRectIntersectsRect(inView, bounds))
        return 0;
    return (float)(CGRectGetMaxY(bounds) - MAX(CGRectGetMinY(inView), CGRectGetMinY(bounds)));
}

#pragma mark - The tracker

@implementation TJKeyboardTracker
{
    BOOL _known;
    float _inset;
    BOOL _visible;
    // A hidden view animated inside each notification, to read the keyboard's animation from.
    UIView* _probe;
}

- (instancetype)init
{
    self = [super init];
    if (self == nil)
        return nil;
    NSNotificationCenter* center = NSNotificationCenter.defaultCenter;
    [center addObserver:self selector:@selector(keyboardWillChange:) name:UIKeyboardWillShowNotification object:nil];
    [center addObserver:self selector:@selector(keyboardWillChange:) name:UIKeyboardWillChangeFrameNotification object:nil];
    [center addObserver:self selector:@selector(keyboardWillChange:) name:UIKeyboardWillHideNotification object:nil];
    [center addObserver:self selector:@selector(keyboardDidHide:) name:UIKeyboardDidHideNotification object:nil];
    [center addObserver:self selector:@selector(inputModeDidChange:) name:UITextInputCurrentInputModeDidChangeNotification object:nil];
    return self;
}

- (void)dealloc
{
    [NSNotificationCenter.defaultCenter removeObserver:self];
}

- (void)forget
{
    _known = NO;
}

- (void)keyboardWillChange:(NSNotification*)notification
{
    NSDictionary* info = notification.userInfo;
    BOOL willHide = [notification.name isEqualToString:UIKeyboardWillHideNotification];
    CGRect end = [info[UIKeyboardFrameEndUserInfoKey] CGRectValue];

    // A hide is always taken, wherever it comes from, so that the inset cannot be left behind.
    if (!willHide)
    {
        // A frame change to all zeros is the keyboard being dragged.
        if ([notification.name isEqualToString:UIKeyboardWillChangeFrameNotification] && CGRectEqualToRect(end, CGRectZero))
            return;
        // Another app's keyboard, beside this one in Split View or Slide Over.
        NSNumber* local = info[UIKeyboardIsLocalUserInfoKey];
        if (!CGRectIsEmpty(end) && local != nil && !local.boolValue)
            return;
    }

    UIView* view = UnityGetGLView();
    if (view == nil)
        return;
    // From iOS 16.1 the notification's object is the screen the keyboard is on.
    UIScreen* screen = [notification.object isKindOfClass:UIScreen.class] ? (UIScreen*)notification.object : view.window.windowScene.screen;
    TJKeyboardMode mode = willHide ? TJKeyboardHidden : TJClassify(end, screen, view);
    float inset = mode == TJKeyboardDocked ? TJDockedInset(end, screen, view) : 0;
    BOOL visible = mode != TJKeyboardHidden;
    double duration = [info[UIKeyboardAnimationDurationUserInfoKey] doubleValue];

    // The same place again (some systems post a frame change with every keystroke) is nothing new, unless it is to be
    // there at once: then a move under way is cut short (a password manager's prompt hides the keyboard so).
    if (_known && inset == _inset && visible == _visible && duration > 0)
        return;
    _known = YES;
    _inset = inset;
    _visible = visible;

    TJTIKeyboardEvent event = {};
    event.startTime = CACurrentMediaTime();
    event.duration = duration;
    event.inset = inset;
    event.viewHeight = (float)view.bounds.size.height;
    event.visible = visible ? 1 : 0;
    event.curve = TJTICurveNone;
    if (duration > 0)
        [self captureCurve:&event];
    TJTIReportKeyboard(&event);
}

// Reads how the keyboard moves: a view animated inside the notification's handler takes on the keyboard's animation
// (UIKit runs the handler inside it), and the animation it is given says how (KeyboardInsetManager.swift
// startKeyBoardAnimation). The keyboard's is a CASpringAnimation; anything else is read as its timing curve, and an ease
// out stands in if nothing can be read.
- (void)captureCurve:(TJTIKeyboardEvent*)event
{
    event->curve = TJTICurveBezier;
    event->c1x = 0.0f;
    event->c1y = 0.0f;
    event->c2x = 0.58f;
    event->c2y = 1.0f;

    UIView* host = TJTIHostView();
    if (host == nil)
        return;
    if (_probe == nil)
    {
        _probe = [[UIView alloc] initWithFrame:CGRectZero];
        _probe.hidden = YES;
        _probe.userInteractionEnabled = NO;
    }
    if (_probe.superview != host)
        [host addSubview:_probe];

    UIView* probe = _probe;
    [probe.layer removeAllAnimations];
    [UIView performWithoutAnimation:^{
        probe.frame = CGRectZero;
    }];
    __block CAAnimation* animation = nil;
    [UIView animateWithDuration:event->duration
                     animations:^{
                         probe.frame = CGRectMake(0, 100, 0, 0);
                         animation = [probe.layer animationForKey:@"position"];
                     }];
    [probe.layer removeAllAnimations];

    if ([animation isKindOfClass:CASpringAnimation.class])
    {
        CASpringAnimation* spring = (CASpringAnimation*)animation;
        event->curve = TJTICurveSpring;
        event->mass = (float)spring.mass;
        event->stiffness = (float)spring.stiffness;
        event->damping = (float)spring.damping;
        event->initialVelocity = (float)spring.initialVelocity;
    }
    else if (animation.timingFunction != nil)
    {
        float c1[2];
        float c2[2];
        [animation.timingFunction getControlPointAtIndex:1 values:c1];
        [animation.timingFunction getControlPointAtIndex:2 values:c2];
        event->c1x = c1[0];
        event->c1y = c1[1];
        event->c2x = c2[0];
        event->c2y = c2[1];
    }
    if (animation.duration > 0)
        event->duration = animation.duration;
}

- (void)keyboardDidHide:(NSNotification*)notification
{
    TJGiveAudioBack();
}

- (void)inputModeDidChange:(NSNotification*)notification
{
    TJTextInputView* view = TJTIActiveView();
    if (view == nil || !view.isFirstResponder)
        return;
    // UIKit reports dictation as an input mode whose language is "dictation" (Trampoline Keyboard.mm
    // textInputModeDidChange:).
    if ([view.textInputMode.primaryLanguage isEqualToString:@"dictation"])
        s_dictationUsed = YES;
    else
        TJGiveAudioBack();
}

@end

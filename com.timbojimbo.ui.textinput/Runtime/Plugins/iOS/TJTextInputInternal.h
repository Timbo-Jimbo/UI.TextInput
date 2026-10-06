// What the plugin's three parts share: the bridge (TJTextInput.mm) that C# talks to, the proxy view the keyboard types
// into (TJTextInputView.mm), and the keyboard tracker (TJKeyboardTracker.mm).

#pragma once

#import <UIKit/UIKit.h>

#include "TJTextInput.h"

@class TJTextInputView;

// ── The bridge ──────────────────────────────────────────────────────────────

// Hand what happened to C#, which only queues it. Nothing is reported before TJTI_Init.
void TJTIReportEdit(int32_t session, int32_t baseSerial, NSString* text, NSInteger selectionBase, NSInteger selectionExtent,
                    NSRange marked);
void TJTIReportIntent(int32_t session, int32_t intent, BOOL extend);
void TJTIReportEnded(int32_t session);
void TJTIReportKeyboard(const TJTIKeyboardEvent* keyboard);
void TJTIReportKeyboardDirection(BOOL rightToLeft);

// The view the proxies live in: a child of the Unity view, as large as it, that touches and VoiceOver pass through.
UIView* TJTIHostView(void);

// The proxy of the session under way, or nil.
TJTextInputView* TJTIActiveView(void);

// ── The keyboard tracker ────────────────────────────────────────────────────

// Follows the software keyboard through UIKit's notifications and reports where it is going and how it moves.
@interface TJKeyboardTracker : NSObject

// Forgets what it last reported, so that the next notification is reported whatever it says.
- (void)forget;

@end

// The proxy was told dictation stopped recording.
void TJTIDictationRecordingDidEnd(void);

// ── The proxy view ──────────────────────────────────────────────────────────

// A place in the mirrored text: a UTF-16 index, and which side of it the caret leans to where a line wraps.
@interface TJTextPosition : UITextPosition

@property (nonatomic, readonly) NSUInteger index;
@property (nonatomic, readonly) UITextStorageDirection affinity;

+ (instancetype)positionWithIndex:(NSUInteger)index affinity:(UITextStorageDirection)affinity;

@end

// A range of the mirrored text, in UTF-16 code units.
@interface TJTextRange : UITextRange <NSCopying>

@property (nonatomic, readonly) NSRange range;

+ (instancetype)rangeWithNSRange:(NSRange)range;

@end

// What the keyboard types into for one session: a UITextInput holding a mirror of the field's value, answering the
// keyboard from it, applying the keyboard's edits to it and reporting them. One is made per session, because UIKit keys
// its per-document keyboard state (predictions, the IME's state) to the view's identity.
@interface TJTextInputView : UIView <UITextInput, UIEditMenuInteractionDelegate, UIScribbleInteractionDelegate>

@property (nonatomic, readonly) int32_t session;

// Whether its session is under way: until it is detached, it may become first responder and it reports what it does.
@property (nonatomic, readonly) BOOL active;

// The mirrored text.
@property (nonatomic, readonly) NSString* text;

// Whether the text reads right to left, as C# laid it out: the base writing direction UIKit is told, and which way left
// and right go in the text when UIKit moves by them itself.
@property (nonatomic) BOOL rightToLeft;

- (instancetype)initWithSession:(int32_t)session config:(const TJTIConfig*)config;

- (void)applyConfig:(const TJTIConfig*)config;

// Takes a value C# set itself, telling the keyboard what changed. `notify`: the keyboard has seen the old value.
- (void)pushText:(NSString*)text
   selectionBase:(NSInteger)selectionBase
 selectionExtent:(NSInteger)selectionExtent
  composingStart:(NSInteger)composingStart
    composingEnd:(NSInteger)composingEnd
          serial:(int32_t)serial
          notify:(BOOL)notify;

- (void)setCaret:(CGRect)caret composing:(CGRect)composing hasComposing:(BOOL)hasComposing;

- (void)showEditMenuAt:(CGRect)target actions:(int32_t)actions;

- (void)hideEditMenu;

// Tells C# which way the keyboard in use writes, from the language of the view's input mode.
- (void)reportInputMode;

// Its session ended: it reports nothing more and cannot become first responder again.
- (void)deactivate;

@end

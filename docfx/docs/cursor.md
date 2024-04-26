# Proposed Design for a modern Cursor system in v2

## Tenets for Cursor Support (Unless you know better ones...)

1. **More GUI than Command Line**. The concept of a cursor on the command line of a terminal is intrinsically tied to enabling the user to know where keybaord import is going to impact text editing. TUI apps have many more modalities than text editing where the keyboard is used (e.g. scrolling through a `ColorPicker`). Terminal.Gui's cursor system is biased towards the broader TUI experiences.


## Lexicon & Taxonomy

- Cursor - A visual indicator to the user where keyboard input will have an impact. There is one Cursor per terminal sesssion.
- Cursor Location - The top-left corner of the Cursor. In text entry scenarios, new text will be inserted to the left/top of the Cursor Location. 
- Cursor Size - The width and height of the cursor. Currently the size is limited to 1x1.
- Cursor Style - How the cursor renders. Some terminals support various cursor styles such as Block and Underline.
- Cursor Visibilty - Whether the cursor is visible to the user or not. NOTE: Some ConsoleDrivers overload Cursor Style and Cursor Visibility, making "invisible" a style. Terminal.Gui HIDES this from developers and changing the visibilty of the cursor does NOT change the style.
- Caret - Visual indicator that  where text entry will occur. 
- Selection - A visual indicator to the user that something is selected. It is common for the Selection and Cursor to be the same. It is also common for the Selection and Cursor to be distinct. In a `ListView` the Cursor and Selection (`SelectedItem`) are the same, but the `Cursor` is not visible. In a `TextView` with text selected, the `Cursor` is at either the start or end of the `Selection`. A `TableView' supports mutliple things being selected at once.

## Requirements

- No flickering. The Cursor should blink/pulse at the rate dictated by the terminal. Typing, moving the mouse, view layout, etc... should not caue the cursor to flicker.
- By default, the Cursor should not be visible. A View or View subclass should have to do anything (this is already the case) to keep the Cursor invisible.
- Views that just want to show the cursor at a particular location in the Viewport should only have to:
  - Declare a desired Cursor Style (e.g. "CursorStyle" or equivalent should be a property on `View`).
  - Indicate the Cursor Locaiton when internal state dictates the location has changed (debatable if this should be in content or viewport-relative coords).
  - Indicate Cursor Visibility (if internal state dictates a change of visibilty).
- The Cursor should only be visible in Views where
  - `Enabled == true`
  - `Visible == true`
  - `CanFocus == true`
  - `this == SuperView.MostFocused`
- Ensuring the cursor is visible or not should be handled by `Application`, not `View`.
- General V2 Requirement: View sub-class code should NEVER call a `Driver.` API. Only `Application` and the `View` base class should call `ConsoleDriver` APIs; before we ship v2, all `ConsoleDriver` APIs will be made `internal`.

## Design

### `ConsoleDriver`s

* Remove `Refresh` and have `UpdateScreen` and `UpdateCursor` be called separately. The fact that `Refresh` in all drivers currently calls both is a source of flicker.

* Remove the `xxxCursorVisibility` APIs and replace with:
  * `internal int CursorStyle {get; internal set; }`
    - Backed with `private int _cursorStyle`
    - On set, calls `OnCursorStyleChanged()`
  * Add `internal abstract void OnCursorPositionChanged()`
    - Called by `base` whenever the cursor style changes, but ONLY if `value != _cursorStyle`.

  * Add `internal virtual (int Id, string StyleName) []  GetCursorStyles()`
    - Returns an array of styles supported by the driver, NOT including Invisible. 
    - The first item in array is always "Default".
    - Base implementation returns `{ 0, "Default" }`
    - `CursesDriver` and `WindowsDriver` will need to implement overrides.

  * Add `internal Point? CursorPosition {get; internal set; }`
    - Backed with `private Point? _cursorPosition`
    - If `!HasValue` the cursor is not visible
    - If `HasValue` the cursor is visible at the Point.
    - On set, calls `OnCursorPositionChanged` ONLY if `value != _cursorPosition`.
  * Add `internal abstract void OnCursorPositionChanged()`
    - Called by `base` whenever the cursor position changes. 
    - Depending on the value of `CursorPosition`:
        - If `!HasValue` the cursor is not visible - does whatever is needed to make the cursor invisible.
        - If `HasValue` the cursor is visible at the `CursorPosition` - does whatever is needed to make the cursor visible (using `CursorStyle`).

### `Application`

* Add `internal static View FocusdView {get; private set;}` - A cache of the view that is most focused.  

* Add `internal bool UpdateCursor ()`

Called when:

- `FocusdView`
  - Has changed to another View (should cover `FocusedView.Visible/Enable` changes)
  - Has changed layout
  - Has changeed it's `CursorPosition`

Does:

- If `FocusedView is {}` and `FocusedView.CursorPosition` is visible (e.g. w/in `FocusedView.SuperView.Viewport`) 
    - If driver cursor position has changed, call `Driver.UpdateCursor`
    - If driver CursorVisibilty has changed, call `Driver.UpdateCursor`
- Else
    - makes driver cursor invisible



# Driving the overlay

A client can operate the in-game overlay the way the user does: open it on a tab, find its elements, bring one into
view, press its controls, type into its text boxes, and change its settings. This is how an assistant checks the overlay
itself, and shows the user something in it, without asking the user to click around.

| Method | Mode | What it does |
|---|---|---|
| `overlay.state` | ReadOnly | Whether the overlay is drawn (and by which renderer), expanded or collapsed, the open tab, the visible tabs, and the text box that has the keyboard |
| `overlay.setState` | ReadOnly | Expands, collapses or hides the overlay, and opens a tab |
| `overlay.snapshot` | ReadOnly | The overlay's elements: what each shows and does, where it is on screen, and whether it's visible or scrolled away. Paged (`limit`, `cursor`); filters `onlyVisible`, `interaction`, `under` |
| `overlay.reveal` | ReadOnly | Brings an element into view: opens the panel on its tab, scrolls its list until it shows, and outlines it for a moment (`highlight`, `durationMs`) |
| `overlay.invoke` | Full | Operates a control as a click would: its command runs with the same effect and the same activity entry. `value` sets a toggle, slider or dropdown |
| `overlay.typeText` | Full | Types into a text box (a prompt's answer, …): it takes the keyboard, the text goes in at its caret (`replace` replaces it), and `submit` presses Enter. Line breaks in the text are kept |
| `overlay.settings` | ReadOnly | Every overlay setting: what it does, its value now, its saved value and default, its range, and whether a change applies at once |
| `overlay.setSettings` | Full | Changes settings. By default for this session only (nothing is saved, and only settings that apply at once can change); `persist` saves them as the Settings tab does |

Reading and showing work in every mode; operating controls, typing and changing settings need Full, like the other
actions that change what the game or the agent does.

## Element ids

Every element has an id that stays the same as long as the overlay shows the same thing:

- `panel/<tab>/…`: the open tab's content, by the ids its view file gives its nodes (or their positions, for nodes
  without one). A list's rows are named by their item's `key` or `id` when it has one, so
  `panel/settings/settings/Overlay.Edge/row/actions/next` is the Next button of the Screen Edge setting.
- `header/…`: the tabs (`header/tabs/tab-<tab>`), E-STOP and the close button.
- `cards/…`: the prompts and notifications next to the arrow (`cards/prompt-<id>/…`).
- `arrow`: the arrow itself (a press expands or collapses the overlay).

An id of another tab's content can be revealed directly: `overlay.reveal` opens that tab first.

## Visibility

`overlay.snapshot` reports each element as `visible`, `partial` (partly on screen), `clipped` (scrolled out of its list
or scroll view: lists only draw the rows in view, so such a row has no `rect`), `offscreen`, or `hidden`. A clipped
element can still be operated, but reveal it first when the user should see what happens.

## Starting a game with other settings

To start a game with different overlay settings (another edge, a start state, …), write them to the agent's configuration
file before the game starts. `overlay.setSettings` with `persist` does the same for the next start.

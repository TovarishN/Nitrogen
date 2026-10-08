# Screenshots and videos for the README

The README marks each place a capture belongs with an HTML comment naming its file, such as
`<!-- media: docs/images/datecalc-hints.png -->`. To add a capture, save it under `docs/images/` and
replace that comment with an image line, for example:

```markdown
![DateCalc's sample with each statement's value](docs/images/datecalc-hints.png)
```

For every capture: VS Code with a dark theme, the editor about 1200 px wide, the window chrome cropped,
the Nitrogen extension installed, and `examples/DateCalc` opened as the workspace folder. PNG for stills;
GIF (or MP4 under 5 MB) for the quick fix.

| File | Open | Cursor or action | Must be visible |
| --- | --- | --- | --- |
| `datecalc-hints.png` | `sample.datecalc` | none | All eight statements with their values at line ends, and the semantic coloring of dates, durations, functions and constants. |
| `datecalc-completion.png` | `sample.datecalc` | On a new line, type `wee` and wait for completion | The completion list with `weekday` and `weeks`, and the details showing types. |
| `datecalc-hover.png` | `sample.datecalc` | Hover the `*` in `3 * sprint` | The hover with `DateCalc.Duration · DateCalc.Times · DateCalc.Weekday` and `= 42 days`. |
| `datecalc-quickfix.gif` | a new `fix.datecalc` with `let d = 2026-02-30;` | Put the cursor on the date, open quick fixes (Cmd+. / Ctrl+.), apply *Change to 2026-02-28* | The squiggle and DC0001 message, the menu, and the corrected line with its value appearing. |
| `csharp-strings.png` | `Snippets.cs` | none | The tagged raw string with `= 2026-12-25 Fri`, `= 81` and `= Friday`, and `= 2026-11-16 Mon` after the `Deadline` string, with DateCalc's coloring inside the strings. |

Rider captures are welcome too: name them with a `-rider` suffix (`datecalc-hints-rider.png`) and add them beside the VS Code ones.

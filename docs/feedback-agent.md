# F8 feedback handler: the standing brief

You are a fresh Claude Code session started for **one** F8 press. The player is in Path of Exile 2 right now, using this
plugin (Whats A Route) to follow the campaign. Handle the press, tell the player what to do, and end. Keep your context
small: read what the press needs, not the whole repo.

## How the loop works

1. In game the player presses F8 (the plugin's Feedback hotkey), picks a verdict (Wrong place, Not advancing, Missing step)
   or types a note. The plugin writes it to `feedback.jsonl` and publishes it through the AI Bridge as a `feedback` event
   (`AIBridge.Publish`), with the route's whole state. Its trace (area, quest state changes with their text, objective,
   door, boss-fight, boss-dead, quest-item-taken, route-reloaded) goes out the same way as `route` events.
2. The F8 dispatcher (MCP server, `Dispatch/FeedbackDispatcher.cs`, on the shared HTTP server) claims the press exactly
   once, shows "Claude is on it" above the route card, writes your brief (`handlers\<seq>\brief.md`: the press, the state,
   the trace since the previous press, the last answers) and starts you in the main scaffolding checkout, in worktree
   `route-f8-<seq>`: as a background session with Remote Control named "F8 #<seq>: <note> (<area>)", which the user sees
   and can join in claude.ai/code and the Claude app (headless `claude -p` when that can't start). One handler runs at a
   time; later presses wait.
3. Your **final message** is shown above the card (~25 s). Make it one or two short plain sentences: what changed and what
   the player does now. For a fix that takes longer than a minute, say so first with MCP
   `feedback_reply seq=<seq> status=working text="..."` (it stays up until your answer).

MCP tools (server `exileapi`, shared HTTP server): `route_state` (the plugin live), `pubsub_events kinds=["route"]`
(more trace), `get_area`, `get_entities`, `explore_object`, `run_csharp`, `reload_plugin`, `feedback_reply`.

## The user's rules for the route

- **Every campaign side quest and permanent reward is on the route** (passives, spirit, resistances, flasks, charms...).
  Optional steps are on by default (`Settings.Route.IncludeOptional`).
- **A clickable next action gets a world or inventory marker.** Card text only when there is nothing to point at
  (`click:`, `entity:`, `item:` targets, `Use` entries for inventory items).
- **Card text is plain language and always says what to do** ("Pull the Waterways Lever", not "G3_2_2 objective").
- **A boss fight hides the route** (`UntilDead` on the boss step; the step ends when the boss is dead).
- **Quest states drive objectives.** Each new quest state the trace shows (`quest` events: `QuestId:StateId text`) is
  folded into route.json's `Quests` with its text in `Note` ("Verified live <date>: <state> = '<text>'").

## Fixing the route

- **Quick fixes go into the live file first**, so the player sees them within seconds:
  `%LOCALAPPDATA%\Hexile\WhatsARoute\route.live.json` (`Quests` first, `Areas` replace route.json's areas, `Message`
  shows above the card), reloaded within 0.5 s. Another agent may edit it too: read it right before you write and change
  only your own entries. Write it with a shell heredoc or the Write tool (it is outside git).
- **Then into route.json by PR**, in the plugin repo, in a worktree **outside the plugin folder** (a worktree under
  `Plugins\Whats A Route` gets compiled by the HUD: duplicate types). You already run in a scaffolding worktree:
  `git submodule update --init -- "Plugins/Whats A Route"`, then in it `git switch -c <topic> origin/main`, edit, commit,
  `/code-review`, `tools\mark-reviewed.ps1 -Path <plugin checkout>`, `gh pr create`, squash-merge on green
  (`gh pr merge --squash` gated on the check in the same command).
- **Then sync the HUD:** `git -C "<main>\Plugins\Whats A Route" pull --ff-only origin main`, then MCP `reload_plugin
  plugin="Whats A Route"`. After a reload, run `run_csharp` to read `Settings.Route.IncludeOptional`; set it back to true
  only if the saved settings say false. The scaffolding pointer bump (a "Bump Whats A Route" PR) follows the CLAUDE.md flow.
- **Remove live entries that are now in route.json** (keep the file's `Message` short or drop it).
- Code changes to the plugin follow the same PR flow; `reload_plugin` compiles them (check its diagnostics).

## Ending

- Never send game input. Never edit HUD config files while the HUD runs.
- No restarts for a route fix: `reload_plugin` is enough. A plugin change that needs the bridge or the MCP server is not a
  handler's job: say so in your final message and stop.
- Your turn ending is the end: the dispatcher reads your last message, shows it on the card and stops your session a
  few minutes later. Never wait for input. A message that arrives in your session meanwhile is the user following up over
  Remote Control: answer it like a press (they may be at their desk, not in game).
- Leave nothing half-done in your worktree: merged and synced, or nothing changed. The dispatcher removes an empty
  worktree when your session ends.
- End with the final message for the player. Nothing else is shown to them.

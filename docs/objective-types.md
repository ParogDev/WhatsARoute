# Objective types

The game writes what to do in every quest state (`QuestStates.dat`, read live as
`GameController.Files.QuestStates`; the open ones as `IngameUi.UncompletedQuests`). "Speak to Makoru to hire her ship."
says who, where and what. The route reads each sentence as one of a few **objective types**. Each type has one handler:
what to point at, what the card says, and when it is done. A new act or patch then needs data, not code:

- a phrase the rules don't know goes into `objective-types.json`;
- the act's quests go into route.json's `Main` (story) and `Follow` (side quests);
- per-area plans (`Areas`) and travel hints (`Quests`, `Via`, `NpcPlaces`) go into route.json.

## How a quest text is read

`Route/QuestGuide.cs` splits the text into sentences and checks them **last sentence first**, since the order to act
comes last ("You have obtained a weapon piece. Speak to Makoru to travel to a new island."). The first rule in
`objective-types.json` that matches decides two things:

- the **kind**;
- the **subject** `x`: the NPC, item, monster, object or area it names. A sentence can also name an `area`
  ("... speak to Tujen in Kingsmarch").

Two fallbacks:

- **A pronoun subject** ("Slay it", "Return it to her") is dropped. The capitalised names in the whole text stand in
  for it (`Names`: "Kaimana", "Tattoo of Hinekora").
- **No area in the sentence:** if the text says "Return to X" anywhere, X is the area.

Every `quest` trace event and the `QuestReads` in `route_state` show the kind each text got. A wrong kind is a rule to
add or fix in `objective-types.json`, which reloads within half a second.

## The kinds and their handlers

**Acting here** means the objective resolves to something in this area: an NPC to talk to, an item, a thing to click,
or a boss in sight. When it does, it beats the area's steps. Ways onward (enter, travel, explore) and a boss out of
sight lead only once the area's steps are done, or where the area has none. The steps are the plan there: optional
rewards first, then the exit.

| Kind | Example text | Points at (target spec) | Card | Acts here | Done when |
|---|---|---|---|---|---|
| Talk | "Speak to Makoru to hire her ship." / "Take a Reward from Navali." / "Show it to Makoru" | `npc:<name>`, `object:<name>` (a totem), then `NpcPlaces` | the sentence | when the NPC is in sight, seen this visit, or placed by `NpcPlaces` | the quest state changes |
| Summon | "Summon Tujen." | nothing (text only) | the sentence; route hidden | no | state changes |
| PickUp | "Pick up its Shark Fin." / "Pick them up." | `item:<name>`, `item:/QuestItems/` (the quest-item rule also leads to any quest item on the ground) | the sentence | when the item is on the ground near you | picked up (`item-gained` trace), state changes |
| Use | "Use the Tattoo of Tawhoa." / "Use the Verisium Anvil." | a carried quest item of that name, framed in the inventory ("Right-click"); else `object:<name>` | the sentence + "open your inventory and right-click it" | carried: always | the item leaves the inventory |
| Click | "Open the Fortified Door." / "Peer into the Well of Passing." | `object:<name>` (targetable, by RenderName) | the sentence | when it is in sight | state changes |
| Defeat | "Slay Krutog, the Kin Tyrant." / "Defeat the Great White One for its Shark Fin." | `monster:<name>`, else Radar's `Boss*` / `Arena` place | the sentence; the fight hides the route | only once the monster is in sight | state changes (the boss-fight trace shows it) |
| Enter | "Enter the Volcanic Warrens." | `travel:<area id>` (area named), else `transition:<name>` | "Exit to X" | no (the steps' exit) | area changes |
| Travel | "Return to Kingsmarch." / "Travel to Arastas." / "Return to Dannig" | `travel:<area id>`; a name that is no area is a person: `npc:<name>` | "Exit to X" / "Travel to X" | no (person: yes) | area changes |
| Sail | "Pick an island and set sail." / "The Karui Elder will guide your ship to ..." | the ship's captain (`npc:Makoru`, `npc:Rhodri`, `NpcPlaces`) | the sentence | yes | state changes |
| Explore | "Explore Whakapanu Island ..." / "Find the Heart of the Tribe." / "Complete Tawhoa's Test." | the area's steps; an area named elsewhere: `travel:` | the step's label | no | the steps |
| Wait | "Witness the Hooded One reforge the Weapon." / "Survive the ambush." | nothing (text only) | the sentence; route hidden | no | state changes |
| Unknown | (no rule matched) | the area's steps, else the text alone | the whole text | no | state changes |

A sentence that names another area ("Return to Kingsmarch and speak to The Hooded One") travels there first. That is
the plan, not an act here.

## Which quest leads

route.json lists the quests whose texts may lead:

- `Main`: the story (`FindTheSpearPieces`).
- `Follow`: side quests and permanent rewards (`Act4RescueMedicineWoman`, `HallsOfTheDead*`, ...). Patterns work.

Each resolve (2 Hz) goes through these, in order:

1. Use items the route lists (`Use`), and quest items on the ground (the quest-item rule).
2. **Acting here:** `Follow`, then `Main`, the first quest whose objective acts here. A side quest's NPC in town wins
   over sailing on.
3. route.json `Quests` entries, matched by quest state (`States`, `UnlessFlag`). The hand-written plan, e.g. the island
   order. An entry for the area you are in beats the planned order within the same quest.
4. **The plan:** `Main`, then `Follow`, the first quest whose text gives an objective (travel, a text-only wait).
5. The area's `Areas` steps.

A `Quests` entry with `"Target": "quest"` pins one quest's text for its states (`Area` `"*"` or the areas it applies in).

## Data that goes with the kinds

- `NpcPlaces`: where an NPC stands per area when out of sight (`"Makoru": { "G4_town": "tile:*KalguurShip_Town*" }`). The
  game only sends entities near you, so a town NPC across the map is not loaded. NPCs seen this visit are remembered on
  their own (sightings now record NPCs).
- `UnlessFlag` on a `Quests` entry: skip it once a quest flag is set. Use it for an island whose boss is dead
  (`G4_3_2SirenBossKilled`). The `flag` trace events name every flag the game sets as you play.
- Target kinds the handlers use, usable in route.json too: `npc:<name>`, `object:<name>`, `monster:<name>`, `radar:<pattern*>`.

## Mapping what the rules can't see: the trace and the heads-up key

Everything the player does is on the trace (feedback.jsonl, and `route` events through the AI Bridge):

- `quest`: a state change, with its text and the kind it was read as;
- `flag`: quest flags set (`flag-cleared` when they are cleared);
- `npc-dialog`: a dialog opened, with the NPC's name;
- `item-gained` / `item-lost`: quest items in the inventory, with name and path;
- `objective`, `area`, `door`, `boss-fight`, `boss-dead`.

**The heads-up key (F7)** is for the moments when you know a quest step is coming: talking to someone, picking up or
using an item, clicking something. Press it and pick what you are about to do (or type it), then do it. It ends a few
seconds after things settle, or when you press F7 again. Then a `heads-up` feedback event goes to Claude through the F8
dispatcher. It carries:

- the quest states before and after;
- flags set and cleared;
- quest items gained and lost;
- dialogs opened;
- things near you whose state or clickability changed, and things that appeared or went;
- the trace in between.

A fresh session folds it into the route: a new phrase into `objective-types.json`, a state into `Quests`, a place into
`Areas` or `NpcPlaces`.

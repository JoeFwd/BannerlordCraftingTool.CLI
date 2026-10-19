# Bannerlord Crafting Tool CLI

A headless Windows command-line tool for **Mount & Blade II: Bannerlord** modders that checks weapon
**crafting-piece offsets** without launching the game. It reads a module's crafting XML (resolving its
whole dependency stack from `SubModule.xml`) and optionally its FBX meshes, reproduces the **exact
assembly math from the game engine**, and emits **JSON** (and PNG renders). It is built for
batch-checking hundreds of piece combinations, diffing results in version control, and driving the tool
from a script or an AI assistant.

It depends on nothing but a public NuGet package. **No Bannerlord install, no mod, and no other project
is required to build or run it.** It works with vanilla `crafting_pieces.xml` or any mod's.

---

## What problem it solves

Bannerlord assembles a crafted weapon from pieces (Blade, Guard, Handle, Pommel) chained along the
weapon's axis. Their positions are controlled by three offset values per piece inside `<BuildData>` in
`crafting_pieces.xml`:

```xml
<BuildData
    piece_offset="-5"
    previous_piece_offset="3"
    next_piece_offset="2" />
```

Tuning these by hand means: edit XML → launch the game → check the crafting bench → repeat. This tool
replaces that loop with offline checks that reproduce the engine's piece-positioning exactly.

---

## Key concepts

### Assembly order (engine-accurate)
Pieces are placed in the **template's `build_order`**, branching on the sign of each piece's order. The
**Handle is the anchor (build_order 0), at the hand grip (Z=0)**:

```
butt  ←  Pommel(-1)      Handle(0 = grip, Z=0)      Guard(+1) →  Blade(+2)  → tip
            (−Z)                                                  (+Z)
```

- Swords / polearms / pikes / javelins: `Handle 0, Guard +1, Blade +2, Pommel −1`.
- Axes / maces: `Handle 0, Blade +1, Pommel −1` (no guard).

The tool bundles the vanilla build orders and auto-selects the right one. Load a base
`crafting_templates.xml` to override with that file's real `<PieceDatas>` (for custom templates).

### How offsets work
Spacing between two adjacent pieces is dominated by their **half-lengths** (`Length / 2` each — or the
explicit `distance_to_next_piece` / `distance_to_previous_piece`). The three `<BuildData>` offsets are
*subtracted nudges* on top:

| Attribute | Effect |
|---|---|
| `piece_offset` on the **Handle** | **Slides the whole weapon along its axis relative to the grip.** It does NOT move the handle vs the blade — it translates every piece together. The usual "weapon sits too high/low in the hand" fix. |
| `piece_offset` on other pieces | Shifts that piece (and everything beyond it on the same side). |
| `previous_piece_offset` | Tightens / loosens the joint toward the grip-side piece. |
| `next_piece_offset` | Tightens / loosens the joint toward the tip-side piece. |

The tool **faithfully reproduces** `WeaponDesign.CalculatePivotDistances()` + `CalculateWeaponLength()`
from the game engine (`TaleWorlds.Core`, v1.4.5) — including `scale_factor` — so the pivot positions and
the reported **Weapon length** match what the game computes.

### XML units
All values are raw XML units (= centimeters in Blender). The game multiplies by `0.01` internally; the
tool stays in raw units so what it reports matches what you type into the XML. The **Weapon length** readout
is in those units (≈ cm; the `reach` figure is the rounded `WeaponLength` stat the game stores).

### How `scale_factor` scales
The engine does **not** scale a piece uniformly. It applies `new Vec3(1, 1, ScaleFactor)` — length only,
leaving the piece's cross-section untouched — *unless* the piece declares `full_scale="true"`:

```xml
<CraftingPiece id="…" full_scale="true" … />
```

When the attribute is absent the default is `PieceType == Guard || PieceType == Pommel`. **Blades and
Handles therefore scale along their axis only by default** — exactly the pieces most often scaled. The
tool follows this rule, so a scaled blade renders longer but *not* fatter, matching the game.

### Scabbards (holster meshes)
A weapon's scabbard is **not a crafting piece** — it is the `holster_mesh` declared on the **Blade**:

```xml
<CraftingPiece id="my_blade" piece_type="Blade" …>
  <BladeData holster_mesh="Detail_Kit_Basic_Scabbard02_red" … />
</CraftingPiece>
```

It is placed at the **Blade's own pivot** and takes the **Blade's scale factor**, so it needs no offsets of
its own — it inherits the blade's placement. It is **not** part of the build order and never gets a seam
with its neighbours. Two template flags suppress it entirely:
`use_weapon_as_holster_mesh="true"` and `always_show_holster_with_weapon="true"`.

Two numbers describe the fit (both in XML units, from mesh bounds):

| Measure | Meaning |
|---|---|
| `mouth_offset` | Where the scabbard mouth sits relative to the blade base: **+** above it, **−** below it. Near-zero is correct. |
| `tip_coverage` | **+** the scabbard encloses the blade tip, **−** the blade **protrudes out of the closed end**. Must be ≥ 0. |

A negative `tip_coverage` is a genuine content bug — usually a long blade reusing a short shared scabbard
mesh — and shows up plainly in the render as the blade poking out the chape.

The engine has two scabbard paths and the tool reproduces both. The **drawn** path (default) is what you
see when the weapon is in hand. The **sheathed** path — the CLI's `render --holstered` — instead places the
scabbard by the template's `piece_type_to_scale_holster_with` and omits its `hidden_piece_types_on_holster`
piece types, which is what the player sees on a character's belt.

**Limitations.** Where the scabbard hangs *on the body* (`item_holsters`,
`default_item_holster_position_offset`, holster bones) lives on the human skeleton and is out of scope.
Quivers use a separate skeletal path (a `<name>_skeleton` model driving a `quiver_deformer` shader) that
cannot be reproduced offline; the tool flags these rather than drawing them wrongly.

> As with every mesh here, the tool reads your **`AssetSources` FBX** while the game reads the compiled
> `_geo.tpac`. If you edit an FBX, re-import it — a green result here is not a correct result in-game.

---

## Command-line interface

```sh
dotnet run --project BannerlordCraftingTool.Cli -- <command> [options]
# or, after a build:
BannerlordCraftingTool.Cli\bin\Release\net8.0-windows\BannerlordCraftingTool.Cli.exe <command> [options]
```

Run with no arguments (or `--help`) for the usage summary.

### Shared options

| Option | Used by | Meaning |
|---|---|---|
| `--submodule <SubModule.xml>` | `inspect` `audit` `render` | Point at **your module's** `SubModule.xml`. The tool walks its `<DependedModules>` and loads the whole stack in load order, so vanilla pieces your mod builds on are resolved automatically. Applies XSLT transforms as the game does. |
| `--modules-root <Modules>` | as above | Override the selected module's parent directory, i.e. where sibling modules are looked up. Only needed if your module lives outside the game's `Modules` folder. |
| `--fbx-folder <folder>` | `audit` `render` | Root for the FBX index. **If the folder sits directly under `AssetSources`, it silently expands to the `AssetSources` root** so sibling FBX collections come along — pass a subfolder and you still get everything. |
| `--out <dir>` | all four | An output **directory**, created if missing. Each command writes a fixed file name into it: `extract` → `pieces.json`, `inspect` → `inspect.json`, `audit` → `audit.json`, `render` → `render.png` + `render.json`. Omit it on the first three and the JSON goes to stdout; `render` requires it. A value that names an existing file, or that ends in `.json`/`.png` without already being a directory, is rejected with exit `2` instead of silently becoming a folder — pre-create the folder if you really want a dotted name. |

`extract` is the odd one out: it reads a single XML file directly and takes neither `--submodule` nor
`--fbx-folder`.

**Exit codes:** `0` success, `2` a bad argument, missing file, or malformed XML/XSLT (message on stderr).

### `extract` — dump parsed pieces

```sh
BannerlordCraftingTool.Cli extract --pieces <crafting_pieces.xml> [--out <dir>]
```

Parses one XML file (no dependency resolution, no `--submodule`) into `{ "pieces": [ … ] }`. Each entry
carries `id`, `piece_type`, `mesh`, `length`, the two `distance_to_*` values, the three `<BuildData>`
offsets, plus `holster_mesh` and `full_scale`. Use it to see what the tool actually read out of your XML —
the fastest way to confirm an attribute is spelled right and being picked up.

### `inspect` — the assembly math for one weapon

```sh
BannerlordCraftingTool.Cli inspect --submodule <SubModule.xml> --weapon <CraftedItem-id> [--weapon …] [--out <dir>]
```

(`layout` is a backwards-compatible alias.) Resolves a `<CraftedItem>` to its pieces and runs the engine's
pivot math. Per piece you get `pivot`, `start`, `end`, `length`, the raw offsets, and their `scaled_*`
counterparts; then `joints`, `weapon_length`, `hand_to_bottom`, and a `source_files` list showing exactly
which XMLs were merged.

`joints[].gap` is the **XML-derived** spacing between adjacent pieces — what the numbers *say*. It is not
the same as `render`'s `seams[].axis_gap`, which measures the **actual meshes**. A weapon can have a clean
`joints` gap and still show a visible seam; when the two disagree, the mesh is wrong, not the XML.

One `--weapon` returns a flat report; repeat `--weapon` and you get `{ weapons, source_files }` instead.

### `audit` — sweep every piece combination for bad seams

```sh
BannerlordCraftingTool.Cli audit --submodule <S.xml> --fbx-folder <dir> \
    (--all-templates | --template <id> …) \
    [--all-pairs] [--scale-percent N] [--tolerance N] [--out <dir>]
```

This is the bulk tool: it measures **real mesh bounds** across physically adjacent pieces for every
player-selectable combination a template allows, and flags the ones that don't meet.

| Option | Meaning |
|---|---|
| `--all-templates` / `--template <id>` | Exactly one of these is required; `--template` repeats. |
| `--all-pairs` | Check every grip×tip combination rather than sampling. With `--out`, also writes a PNG per pair plus per-piece galleries into that directory; without it the sweep still reports its measurements, it just renders nothing. |
| `--scale-percent N` | Scale every piece before measuring. Default `100`. |
| `--tolerance N` | Max \|gap\| still counted as aligned, in XML units. Default `0.01` — **very strict**. |

Each record reports `visual_gap`, `aligned`, the `grip`/`tip` mesh bounds, and a `correction_plan` naming
the single attribute to change (`next_piece_offset` when the grip-side piece is the candidate,
`previous_piece_offset` when it's the tip-side one) and the value to set.

**Read `correction_plan.reason` before trusting any suggestion.** Corrections are computed *relative to a
reference pair*, so if that reference is itself bad, every suggestion inherits the error — which is why
`safe_to_apply` exists:

| `reason` | Meaning |
|---|---|
| *(absent)*, `safe_to_apply: true` | The suggestion is trustworthy. |
| `reference_baseline` | This record **is** the reference — nothing to correct. |
| `reference_baseline_is_misaligned` | The reference pair doesn't meet cleanly. **Fix that pair first**, then re-run. |
| `reference_baseline_incomplete` | The reference couldn't be measured (missing mesh). |
| `missing_mesh` | This pair's own mesh wasn't in the FBX index. |

A run reporting *almost everything* as a failure usually means the baseline is off by a hair, not that your
data is entirely broken — check `summary.visual_failures` against the `reason` breakdown before reacting.

Pieces are also skipped with a stated cause (`duplicate_piece_type`, `unresolved_usable_piece`,
`missing_usable_piece_type`), and the `default_polearm_guard` / `default_polearm_pommel` placeholder slots
are collapsed rather than measured. **`audit` never edits your XML** — it only tells you what to change.

### `render` — picture + measurements for one weapon

```sh
BannerlordCraftingTool.Cli render --submodule <S.xml> --weapon <id> --fbx-folder <dir> --out <dir> [--holstered]
```

Assembles the weapon and writes **`<dir>\render.png`** (three orthographic views —
side, front, top) plus **`<dir>\render.json`**. The JSON gives per-piece `world_bounds`, `center`,
`lateral_offset` and `axis_extent`; `seams[].axis_gap` measured between adjacent meshes; a `scabbard`
block (see [Scabbards](#scabbards-holster-meshes)); and `missing_meshes`.

`--holstered` switches from the drawn path to the sheathed one. This is the command to reach for when a
weapon looks wrong in-game and you want to see *why* without launching Bannerlord.

---

## FBX mesh loading

**Two-phase** for speed: an *index* pass opens every `.fbx` with minimal processing and maps node/mesh
names to their file; the *load* pass decodes geometry only for the selected piece's mesh.

**Name matching** is case-insensitive against FBX node names and mesh names. A `.lod0` suffix is handled
(`wm_axe_blade.lod0` is also indexed as `wm_axe_blade`). **Skipped:** `_lod1`–`_lod9`, prefixes `bo_` /
`col_` / `ub_`, and a node named `armature`.

Vertices are transformed to world space down the node chain (applying the exporter's Z-up→Y-up
correction baked into the root). The export origin is used as the piece pivot — no bounding-box centering.

A Blade's `holster_mesh` is resolved by these same name-matching rules, but it is **not** part of the
build order — it is looked up separately and placed at the Blade's pivot.

---

## Build from source

Requires the **.NET 8 SDK** (`win-x64`).

```sh
# from this folder
dotnet build BannerlordCraftingTool.Cli -c Release            # compile the CLI
dotnet run --project BannerlordCraftingTool.Cli -- --help     # run it (note the -- separator)

dotnet run --project BannerlordCraftingTool.Cli.Tests -c Release   # run the tests
```

There is no `.sln`; each project builds on its own (the tests reference the CLI, so building them builds
both). The test project is a plain console app with a hand-rolled runner — no xUnit/NUnit. It calls each
test in sequence and prints `N tests passed.`; a failure throws, so you get the assertion message and a
stack trace naming the test, and a non-zero exit.

> Adding a test means two edits at the top of `BannerlordCraftingTool.Cli.Tests/Program.cs`: the call, and
> the **hard-coded count** in that final `Console.WriteLine`. Bump it or the summary line lies.

---

## Project structure

```
BannerlordCraftingTool/
├── BannerlordCraftingTool.Cli/         headless JSON/PNG tool
│   ├── Program.cs                      commands, XML/dependency resolution, assembly math, offscreen rendering
│   ├── Output/                         resolves `--out` to an output directory, or stdout
│   ├── Rendering/                      mesh catalog + world-space piece construction
│   └── Scabbard/                       holster eligibility, placement, measurement, reporting
├── BannerlordCraftingTool.Cli.Tests/   self-contained test runner (`dotnet run`, no test framework)
└── README.md                           this file
```

**Dependencies** (NuGet, restored automatically): `AssimpNet 4.1.0` (FBX loading). Nothing else.

The CLI renders offscreen with WPF's `Viewport3D` + `OrthographicCamera`, which is why it targets
`net8.0-windows` and needs no display.

---

## Credits & license

Originally built by **KEYforce** for the *Tales from the Age of Men* (TAOM) Bannerlord project; the
engine-fidelity assembly math and this headless CLI were added in a later pass. The original WPF GUI
has been removed. It is a general-purpose modding
utility — usable with any Bannerlord crafting data, not tied to any specific mod.

`TaleWorlds.*` types referenced in this document are TaleWorlds Entertainment's; this tool ships none of
their code or assets — it only reproduces the documented assembly arithmetic from your own XML/FBX inputs.

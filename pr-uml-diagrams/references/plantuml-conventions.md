# PlantUML conventions

Use these rules for every diagram. The files in `assets/` show complete examples that render without errors.

## Colors

| Meaning | Class or step fill | Arrow |
|---|---|---|
| Added | `#C8E6C9` | `-[#2E7D32]->` |
| Removed | `#FFCDD2` | `-[#C62828,dashed]->` |
| Changed (structural or behavioral) | `#FFF3C4` | not applicable |
| Unchanged, shown only as context | `#F5F5F5` | `-[#9E9E9E]->` |

Class diagrams use the hex colors directly. Flowcharts use the style classes `<<added>>`, `<<removed>>` and `<<changed>>` that the flowchart header defines.

Always add the legend from `assets/example-class.puml` to class diagrams. Do not use color as the only signal: removed members also get `<s>strikethrough</s>`, and members sit under `-- added --` and `-- removed --` separators.

## Class diagram header

```
@startuml
title Dependency changes: <branch> compared with <base>
skinparam shadowing false
skinparam classAttributeIconSize 0
skinparam defaultFontName Segoe UI
skinparam ArrowFontSize 11
hide empty members
```

## Types

- Group types with `package <Namespace> { }`.
- Show the C# kind: `class`, `abstract class`, `interface`, `enum`. Add stereotypes for `<<sealed>>`, `<<static>>`, `<<record>>`, `<<record struct>>`, `<<struct>>`.
- Generic types need a quoted name and an alias, because `<` and `>` are not valid in an alias: `class "Repository<T>" as Repository_1`. Use the alias in every arrow. The alias is the name plus `_` plus the arity.
- Context types (status unchanged) have no body. Write only the declaration line with `#F5F5F5`.
- Visibility: `+` public, `#` protected, `~` internal, `-` private. Write `{static}` or `{abstract}` before a member when the modifier applies.

## Members

Show only members that make the change clear:

- Changed types: the members with status `added` or `removed`, and members with a visibility or modifier change. Do not list members with only a body change.
- Added types: public and internal members. If there are more than 8, show the 8 most important and add `.. N more ..`.
- Removed types: no members, unless a member moved to another type in the diagram.
- A changed signature is one `removed` member and one `added` member with the same name. Show both.

## Dependency arrows

Draw one arrow for each pair of types. If the JSON has more than one kind for a pair, use only the arrow and label of the strongest kind. For example, `injects` and `has` for the same pair become one `<<inject>>` arrow, because an injected dependency is usually stored in a field.

| JSON kind | Arrow | Label |
|---|---|---|
| inherits | `-|>` | none |
| implements | `..|>` | none |
| injects | `-->` | `<<inject>>` |
| has | `-->` | `<<has>>` |
| creates | `..>` | `<<create>>` |
| uses | `..>` | `<<use>>` |
| registers | `-[<color>,dashed]->` | `registers` (plain text, no guillemets) |

Strength order: inherits, implements, injects, has, creates, uses.

### Registrations for dependency injection

A `registers` arrow is never merged with other kinds. It always has its own arrow.

- The arrow starts at the class that contains the registration code, for example `ServiceCollectionExtensions`, a module class or `Program` for top-level statements.
- The arrow points to the registered implementation class. It never points to the service interface it is registered as, and never to a class that injects it.
- Added registration: `Source -[#2E7D32,dashed]-> Implementation : registers`.
- Removed registration: `Source -[#C62828,dashed]-> Implementation : registers`.
- Unchanged registration, shown as context: `Source -[#9E9E9E,dashed]-> Implementation : registers`.
- A registration with a factory, for example `AddScoped<IReportFactory>(provider => new ReportFactory(...))`, also gives `creates` edges. Do not draw a `creates` arrow for a pair that has a `registers` arrow. Do not draw `creates` arrows to the constructor arguments of the factory.
- A generic registration such as `AddScoped(typeof(Repository<>))` points to the generic class alias, for example `Repository_1`.
- Add the arrow row from the legend in `assets/example-class.puml`.

## Flowchart header

PlantUML draws flowcharts with its activity diagram syntax. The style block below changes the default UML shapes to classic flowchart shapes. Copy the header and the legend from `assets/example-flowchart.puml` without changes.

```
@startuml
title <Type>.<Method>: changes on <branch>
skinparam shadowing false
skinparam defaultFontName Segoe UI
skinparam conditionStyle InsideDiamond
<style>
activityDiagram {
  activity { RoundCorner 0 }
  .terminal { RoundCorner 60 }
  .added { BackGroundColor #C8E6C9 }
  .removed { BackGroundColor #FFCDD2 }
  .changed { BackGroundColor #FFF3C4 }
}
</style>
```

## Flowchart shapes

| Shape | Syntax |
|---|---|
| Start (oval) | `:Start: <Method>(<parameters>); <<terminal>>` as the first line. Do not use `start`. |
| End (oval) | `:End: <result>; <<terminal>>` and then `kill` on the next line. Do not use `stop` or `end`. |
| Step (rectangle) | `:text;` |
| Decision (diamond) | `if (Question?) then (yes)` ... `else (no)` ... `endif` |

Always write the `else` branch, also when it is empty, so both labels are visible. Every path that leaves the method (return, exception, early return) ends in its own End oval.

## Flowchart diff markers

- Added step: `:text; <<added>>`.
- Removed step: `:<s>text</s>; <<removed>>`, at the position where it was in the old flow.
- Changed step: `:new text\n<s>old text</s>; <<changed>>`.
- Added decision: `if (Question?) then (yes) <<added>>`. The merge point of that decision is also green. This is a known PlantUML behavior.
- Changed condition: write the new condition and add `note right: was: <old condition>` on the first step in the branch.
- Removed decision: draw it as a removed step `:<s>Check: old question?</s>; <<removed>>`, because a decision cannot be struck through.
- Unchanged steps and decisions have no marker.
- Do not use the old color syntax `#C8E6C9:text;`. It is deprecated and shows a warning in the image.

## Flowchart content

- Show steps at the level of the business rule, not one step per code line. Name the called method in the step only when the reviewer needs it to find the code.
- Use `repeat` ... `repeat while (Question?)` or `while (Question?)` ... `endwhile` for loops.
- Use `fork`, `fork again` and `end fork` for work that runs in parallel, for example `Task.WhenAll`. Use a note for fire-and-forget work.
- Use a short `note` to give the reason for an added or removed step if the code or the commit messages give it. Do not guess a reason.

## Flowchart size

- Do not use swimlanes (`|Name|`) or class boxes (`partition`).
- Keep a flowchart at about 25 steps or fewer. Split a longer flow at a clear step and give the flowcharts consecutive numbers.

## Syntax traps

- Do not put `<<` or `>>` in a message label. Use words instead.
- Quote class names that contain spaces, dots, `<` or `>`.
- A `note` needs `end note` when it has more than one line.

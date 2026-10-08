---
name: pr-uml-diagrams
description: Creates PlantUML class diagrams and flowcharts for the changes on the current Git branch and renders them to PNG images that the user attaches to a pull request. Use only when the user runs /pr-uml-diagrams.
disable-model-invocation: true
argument-hint: "[base-branch]"
---

# PR UML diagrams

Create short-lived diagrams for one pull request:

1. One class diagram that shows how the dependencies between classes changed on this branch.
2. One flowchart for each changed logic flow. The diagram shows the difference between the old flow and the new flow.

The diagrams are for reviewers. They must show the change, not the whole code base. Leave out everything that does not help a reviewer understand the change.

The base branch is `$ARGUMENTS`. If it is empty, the extractor finds the default branch (`origin/HEAD`, then `main`, `master`, `develop`).

## Step 1: Prepare the work folder

The work folder is `<user home>/.claude/pr-uml/<repository>/<branch>`:

- `<user home>` is the `USERPROFILE` environment variable on Windows, or `HOME` on other systems. It is the global Claude folder of the user, not the `.claude` folder of the repository.
- `<repository>` is the name of the repository root folder. Find the root with `git rev-parse --show-toplevel`. The repository level keeps branches with the same name in different repositories apart.
- `<branch>` is the current branch name with `/` replaced by `-`.

Delete the work folder if it exists, then create it. Do not delete the folders of other branches or repositories. Do not write diagrams anywhere else, and never into the repository.

## Step 2: Extract the changes

Run the extractor from the repository root. The scripts are in the `scripts` folder next to this file.

```
dotnet run "<skill folder>/scripts/BranchDiff.cs" -- --base <base-branch> --out "<work folder>/branch-diff.json"
```

Omit `--base` when `$ARGUMENTS` is empty. The extractor compares the merge base with the working tree, so changes that are not committed are included. Add `--committed-only` if the user asks for committed changes only.

The first run downloads the Roslyn package from NuGet. This can take a minute.

Read `branch-diff.json`. It contains:

- `types`: every type in a changed file whose structure or behavior changed. `status` is `added`, `removed`, `structural` (members, visibility, modifiers or dependencies changed) or `behavioral` (only method bodies changed). Each type lists its changed `members` with a `location` (`path:line`, or `base:path:line` for code that exists only at the merge base).
- `changedEdges`: dependencies that were added or removed. `kind` is `inherits`, `implements`, `injects`, `has`, `creates`, `uses` or `registers`. A `registers` edge goes from the class that contains a dependency injection registration to the registered implementation class. Top-level statements in `Program.cs` appear as the type `Program`.
- `contextEdges`: unchanged dependencies of structurally changed types.
- `unchangedTypes`: types in changed files without a relevant change, for example a change to comments only.

The extractor reads syntax only. Know its limits:

- Types are matched by simple name. Two types with the same name in different namespaces are merged.
- Dependencies through static calls, `var` locals, target-typed `new()` without a declared type, and reflection are not found.
- Dependencies on types that are not declared in the repository (framework and NuGet types) are removed. Add `--include-external` only if the user asks for them.
- `registers` edges are found only for common registration calls:
  - Microsoft.Extensions.DependencyInjection: `Add`, `TryAdd` and `AddKeyed` with `Singleton`, `Scoped` or `Transient`, `TryAddEnumerable`, `AddHostedService`, `AddHttpClient`, `AddDbContext` and the `ServiceDescriptor` factory methods.
  - Simple Injector: `Register`, `RegisterSingleton`, `RegisterScoped`, `RegisterConditional`, `RegisterDecorator`, `RegisterInstance` with a `new` expression, and `Container.Collection.Register` or `Append` with explicit types. Both the generic form (`RegisterSingleton<IService, Implementation>()`) and the `typeof` form are found.
  - Autofac: `RegisterType`.

  Not found: batch registration by assemblies (for example `Register(typeof(ICommandHandler<>), assemblies)` or `Collection.Register<T>(assemblies)`), `GetTypesToRegister`, `AddRegistration` with a `Registration` object, `ResolveUnregisteredType` handlers, assembly scanning such as Scrutor or `RegisterAssemblyTypes`, registrations through variables, and `RegisterInstance(existingObject)`.

If a result looks wrong, read the code at the given location and correct it in the diagram.

### Check the registrations by hand

The extractor does not find every registration. Run `git diff <mergeBase> -- "*.cs"` and look for changed registration code: `IServiceCollection` extension methods, `Program.cs`, `Startup.cs`, the methods that configure the Simple Injector `Container`, Autofac modules, and batch or scanning calls such as `Register(typeof(...), assemblies)`, `GetTypesToRegister`, `Scan`, `RegisterAssemblyTypes` or `AddClasses`. For each registration that was added or removed and that is not in `changedEdges`, add a `registers` edge by hand. The edge goes from the class with the registration code to the implementation class. If you cannot find the implementation class (for example with assembly scanning), do not guess. Mention the registration in the report. A registration that stays but changes its lifestyle or condition, for example from `RegisterSingleton` to `RegisterConditional`, keeps an unchanged `registers` edge. Mention the change in the report.

## Step 3: Write the class diagram

Read `references/plantuml-conventions.md` and `assets/example-class.puml` before you write the diagram.

Skip the class diagram if `changedEdges` is empty and no type has the status `added`, `removed` or `structural`. Tell the user that the branch has no architectural changes.

Select the content:

1. Include every type with the status `added`, `removed` or `structural`.
2. Include every type that is a source or target of an edge in `changedEdges`. Types that did not change are context types. This includes the `registers` edges that you added by hand.
3. From `contextEdges`, include only the edges that help the reader. Include an unchanged edge if it connects two types that are already in the diagram, or if it shows the base type or the main collaborator of a changed type. Do not add new context types for `uses` edges.
4. Leave out exception types, DTOs and other leaf types if they do not change a dependency. Keep them if they are new and a reviewer must know about them.

If the diagram has more than about 15 types, split it into more class diagrams by namespace or feature area. Each type with a change appears in exactly one diagram.

Save the file as `<work folder>/01-class-<area>.puml`. Use `01-class-dependencies.puml` if there is only one class diagram.

## Step 4: Write the flow diagrams

Each flowchart shows the difference between the flow at the merge base and the flow on the branch. Draw the two versions in one merged diagram: added steps in green, removed steps in red and struck through, unchanged steps without color. Always use flowcharts with classic shapes: ovals for start and end, sharp rectangles for steps, diamonds for decisions. Do not use swimlanes or class boxes. Read `assets/example-flowchart.puml` first.

### Find the changed flows

1. Collect all members of kind `method`, `constructor` or `property` with the status `added`, `removed` or `modified` in types with the status `added`, `removed`, `structural` or `behavioral`.
2. Pair a `removed` member with an `added` member when the added member replaces it, for example `PlaceOrder` and `PlaceOrderAsync`. Treat the pair as one changed flow.
3. Find the entry points. An entry point is a changed method that no other changed method calls. Search the code to find the callers. Typical entry points are public service methods, controller actions, message handlers and event handlers.
4. Combine entry points that share almost the same flow. Skip trivial changes, for example a renamed variable or a property that only returns a field.

### Read both versions

- The branch version is at `location`.
- The merge base version is at `locationBefore`, or at `location` when it starts with `base:`. Read it with `git show <mergeBase>:<path>`, where `<mergeBase>` comes from `branch-diff.json`.

Trace each flow in both versions. Follow calls into changed methods. Show a call to unchanged code as one message, and do not trace into it. Then compare the two traces step by step.

### Select the layout

- **Every flow:** one flowchart from top to bottom, without swimlanes and without class boxes, also when the flow crosses classes. See `assets/example-flowchart.puml`.
- If one method in a flow has more than 3 changed decisions, show that method as one step in the main flowchart and add a separate flowchart for the method.
- **New flow** (no merge base version): draw the flow without diff colors and write `(new flow)` in the title. A diagram that is all green does not help the reader.
- **Removed flow** (no branch version): do not draw it. Mention it in the report.
- **Rewrite** (more than about half of the steps are added and half are removed): a merged diagram is hard to read. Write two flowcharts with the suffixes `-before` and `-after`, with the steps in the same order where possible.

Save each diagram as `<work folder>/<number>-flow-<method>.puml`. Start the numbers at `02` and use a logical review order.

## Step 5: Render and check

Render all diagrams:

```
powershell -NoProfile -ExecutionPolicy Bypass -File "<skill folder>/scripts/Render-Diagrams.ps1" -Path "<work folder>"
```

The script needs Java. It downloads `plantuml.jar` on the first run. If the script reports a syntax error, fix the `.puml` file and render again.

Open each PNG and check it:

- All text is readable and no labels overlap.
- Each type and arrow in the class diagram agrees with `branch-diff.json`.
- Each step in a flow diagram agrees with the code. Check the removed steps against the merge base version.

Fix the diagram and render again if a check fails.

## Step 6: Report

Give the user:

1. The full path of the work folder.
2. A list of the images. For the class diagram, give one sentence that describes the dependency change. For each flow diagram, give one line in the form `Before: ... After: ...`. The user can copy these lines into the pull request description.
3. The limits that affected the result, for example a type name collision, a split diagram, or a registration that you added by hand or could not resolve.

Do not commit or stage any file. Do not write files outside the work folder.

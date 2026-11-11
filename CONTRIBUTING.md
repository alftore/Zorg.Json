# Contributing

## Issues and pull requests

Issues are welcome: bug reports, feature requests and questions about the documentation. Please use one of the issue forms. A bug report with a minimal schema and input usually gets fixed fastest.

Pull requests are open to the maintainer only. That keeps one person responsible for every change, its tests and its signed history. If you have a fix in mind, describe it or paste the patch in the issue, and it will be credited in the changelog.

Report security problems privately, as [SECURITY.md](SECURITY.md) describes, not in an issue.

## Build and test

You need the .NET SDK version in `global.json`, which is 11, and the .NET 10 runtime: the libraries target .NET 10, and `Zorg.Json.AspNetCore.OpenApi` and its tests target .NET 11 too. The compliance tests need the JSON Schema Test Suite submodule.

```
git clone --recurse-submodules https://github.com/alftore/Zorg.Json.git
git submodule update --init      # if you've already cloned without it

dotnet build Zorg.Json.slnx
dotnet test --solution Zorg.Json.slnx
```

The build treats warnings as errors, so it has to finish with 0 warnings.

To collect coverage, as CI does:

```
dotnet build Zorg.Json.slnx -c Release
dotnet test --solution Zorg.Json.slnx -c Release --no-build --coverage --coverage-output-format cobertura
```

## AOT smoke test

`samples/Zorg.Json.AotSmoke` checks that the core works under Native AOT. It has a `net10.0` and a `net11.0` build, and the second also checks C# unions and the new number types; pass `-f` to pick one. CI fails on any IL2xxx or IL3xxx warning, or if a result is wrong. To run it yourself:

```
dotnet publish samples/Zorg.Json.AotSmoke -f net11.0 -r win-x64 -c Release
samples/Zorg.Json.AotSmoke/bin/Release/net11.0/win-x64/publish/Zorg.Json.AotSmoke.exe
```

`samples/Zorg.Json.AspNetCore.AotSmoke` does the same for the ASP.NET Core packages, over real HTTP, and is published and run the same way. It catches failures that a clean publish doesn't, because a missing JSON contract only shows up at run time.

Use `linux-x64` and drop `.exe` on Linux. Windows needs the Visual Studio C++ workload, with `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH` for vswhere. Ubuntu needs `clang` and `zlib1g-dev`.

## Accepting a snapshot

Snapshot tests compare output with `tests/Zorg.Json.Tests/Snapshots/*.verified.*`. On a mismatch the test fails and writes a `.received.` file next to the verified one.

1. Compare the two files.
2. If the new output is right, rename the file from `.received.` to `.verified.`, replacing the old one.
3. Run the tests again and commit the `.verified.` file.

`.received.` files are gitignored.

## Conventions

- **Keyword-first naming.** Method names are JSON Schema keywords (`MinLength`, `Pattern`, `Minimum`). Add a Zod alias only where it's unambiguous (`Optional`, `Nullable`, `Refine`, `Describe`).
- **XML docs on all public members.** Each one names the JSON Schema keyword it emits and the Zod equivalent, and has an `<example>`.

```csharp
/// <summary>Sets the minimum length. JSON Schema: <c>minLength</c>. Zod equivalent: <c>.min()</c>.</summary>
/// <param name="length">The minimum length.</param>
/// <returns>A new schema.</returns>
/// <example>
/// <code>
/// StringSchema name = Z.String().MinLength(1);
/// </code>
/// </example>
```

## Repository settings (for the owner)

GitHub can't read these from files, so the owner has to turn them on by hand.

The rulesets below are in `.github/rulesets/`. They need a public repository or GitHub Pro. Apply them with:

```bash
gh api repos/alftore/Zorg.Json/rulesets --method POST --input .github/rulesets/main.json
```

```bash
gh api repos/alftore/Zorg.Json/rulesets --method POST --input .github/rulesets/tags.json
```

**Branch protection for `main`** (Settings > Rules > Rulesets, or Branches):

- Require a pull request before merging, with at least 1 approval, dismissing stale approvals, and approval of the most recent push. A sole maintainer can't approve their own pull request, so set this to 0 until there's a second maintainer.
- Require status checks to pass, with the branch up to date. Required checks: `Build and test (windows-latest)`, `Build and test (ubuntu-latest)` and `AOT smoke publish`.
- Require conversation resolution before merging.
- Block force pushes and deletions.
- Leave the bypass list empty, so the rules apply to administrators too.

**Tags:** add a tag ruleset for `v*` that restricts creation, updates and deletions to maintainers.

**The `nuget` environment** (Settings > Environments):

- Add required reviewers. Turn on "Prevent self-review" once there's a second maintainer.
- Limit deployments to selected tags, with the rule `v*`.
- Turn off administrator bypass.
- Add the secret `NUGET_USER`, holding the nuget.org profile name (not an email address).

**nuget.org** (your username > Trusted Publishing): add a policy with the repository owner, repository `Zorg.Json`, workflow file `release.yml` and environment `nuget`.

**Actions and security** (Settings > Actions, and Advanced Security):

- Set the default `GITHUB_TOKEN` permission to read-only, and don't let Actions approve pull requests.
- Require actions to be pinned to a full-length commit SHA. The lockfile satisfies this for the workflows it covers.
- Turn on **Require lockfile** under Settings > Actions > Policies, so a workflow that isn't in `.github/workflows/actions.lock` doesn't run.
- Turn on private vulnerability reporting, Dependabot alerts, secret scanning and push protection.
- Use two-factor authentication on GitHub and nuget.org.

## Changing a workflow

The actions the workflows use are locked in `.github/workflows/actions.lock`, generated by the `gh actions-lock` extension. After adding a workflow or changing a `uses:` line, regenerate the lockfile, read the whole diff, and commit both:

```bash
gh extension install github/gh-actions-lock
```

```bash
gh actions-lock
```

Don't edit the lockfile by hand. `gh actions-lock --verify` checks it without writing anything.

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/Zorg.Json.Benchmarks -- --filter '*' --job short --inProcess
```

`--inProcess` is required: BenchmarkDotNet's generated project inherits the repo's build rules, and its code has no XML docs.

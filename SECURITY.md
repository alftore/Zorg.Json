# Security policy

## Supported versions

Zorg.Json is in early development. Only the latest published version gets security fixes.

## Reporting a vulnerability

Report it privately through the repository's **Security** tab, using **Report a vulnerability**. Don't open a public issue.

Please include the affected version, a way to reproduce it, and the impact you expect. Schemas, inputs and code samples help most.

## How releases are protected

- Nobody pushes to `main` directly, administrators included. Every change arrives through a pull request with signed commits and passing checks on Windows, Linux and Native AOT.
- A release runs only from a `v*` tag on a commit that's on `main`, and it waits for approval in the `nuget` environment.
- Packages are published with NuGet trusted publishing. There's no long-lived API key: each release gets a key that lasts one hour.
- The job that publishes doesn't run repository code. The job that builds has no access to publishing credentials.
- Every GitHub Action, and every action it uses in turn, is locked in `.github/workflows/actions.lock`: the commit that runs, and the IDs of the repository it must come from. A moved tag, or a commit that isn't on the tag in the action's own repository, is rejected at run time. Dependabot proposes updates after a 7-day cooldown.

## Verifying a package

Each release has a build provenance attestation for the packages it built. To check one, download the `packages` artifact from the release's workflow run and run:

```
gh attestation verify Zorg.Json.<version>.nupkg --repo alftore/Zorg.Json
```

This doesn't work on a `.nupkg` downloaded from nuget.org. nuget.org adds a repository signature to every package, so the file's digest no longer matches the attestation. Check those with `dotnet nuget verify` instead.

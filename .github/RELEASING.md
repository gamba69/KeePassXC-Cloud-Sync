# Maintainer release procedure

This page is for repository maintainers, **not** end-user documentation. GitHub permissions must allow Actions to write release assets (`contents: write` is granted only to the draft-preflight and attachment jobs); organization policies may additionally restrict this.

## 1. Commit and tag

1. Update `Version` in `Directory.Build.props` and commit the complete source tree to the default branch.
2. Let Windows CI (on all branches and pull requests, but not tag pushes) build and all tests pass.
3. Create and push **one** tag matching that exact version: `v7.1.15` **or** `7.1.15`. Do not push both spellings for the same version.

For example:

```shell
git tag v7.1.15
git push origin v7.1.15
```

Both version formats are accepted by the manual Action. No Action is triggered by tag creation, deletion or recreation. Always create the GitHub draft against an **existing** tag; keep the tagged source version consistent with `Directory.Build.props`.

## 2. Manually create a draft release

On GitHub open **Releases → Draft a new release**, choose the **existing** version tag and save with **Save draft**. Do **not** publish. A pushed tag alone must not create a release, a draft, an artifact or a release build. You can delete/recreate a tag before creating a draft without causing any release automation. If a draft already exists, verify that it points to the intended tag; GitHub can retain draft state independently of tag changes.

## 3. Manually build and attach the assets

Under **Actions** select **Build and attach portable ZIP to existing draft (manual)**, then choose **Run workflow** and enter the exact existing tag (`vN.N.N` or `N.N.N`). You may launch the action from the default branch; it explicitly checks out the **tagged commit**. The workflow requires an empty, unpublished draft; no pre-existing file is overwritten.

The workflow uses **three isolated jobs**: a Linux draft/tag preflight (`contents: write` is needed for draft access), a read-only Windows build, and a Linux attachment job with scoped write access. The workflow never queries the `releases/tags/{tag}` endpoint for a draft: that endpoint exposes published releases only. Instead, it searches the authenticated, paginated release listing for one exact matching unpublished draft, records its numeric ID, and uploads the two assets directly to that ID. If the automatic `GITHUB_TOKEN` cannot see the draft in the listing, configure an optional repository Actions secret named `RELEASE_DRAFT_TOKEN` containing a fine-grained GitHub personal access token scoped only to this repository with **Contents: Read and write** permission. Do not put token values in the workflow or commit them. This optional token is used only in the draft-preflight and asset-attachment jobs; the Windows build continues with a read-only token.

The workflow is always executed from the selected source branch, but the Windows build explicitly checks out the existing tag. A workflow-only fix on `main` can therefore still build a previously created tag, provided its immutable source and version validate. The build runs `build.bat -NoPause`: all source checks, all ten C# test suites and NativeAOT, then packages a portable Windows x64 ZIP and creates the matching `KeyPassXCCloudSync-N.N.N-win-x64.sha256`. It does not download or bundle Proton Drive CLI. Users obtain that optional tool from Proton separately. User passwords, authentication sessions, INI configuration, KDBX files and local state are never put into the ZIP.

Two assets are attached to the existing draft:

- `KeyPassXCCloudSync-N.N.N-win-x64.zip`
- `KeyPassXCCloudSync-N.N.N-win-x64.sha256`

A copy is also retained as a workflow artifact for review. The attachment job revalidates the SHA-256 on a separate runner and rejects changes to the tag object, tagged commit or draft release ID since preflight. Failed tests, malformed or missing tags, non-draft releases, changed draft contents, changes to the remote tag during the build, packaging or checksum failures block attachment. This action **never publishes** the release.

## 4. Review and publish manually

Inspect the draft release and its two attachments; test the ZIP on a clean Windows system and check the matching `.sha256` file. Then select **Publish release** manually on GitHub. No workflow performs this final action. Do not publish a draft with missing assets.

### Retry policy

If a build fails before upload, inspect the cause and fix it in a new commit/version/tag when appropriate. Never assume a draft belongs to a deleted or recreated tag: re-check its intended source before rerunning the Action. Never silently retag a published release. If an upload partially succeeds, inspect the draft; assets are deliberately not overwritten automatically. Resolve the draft's state deliberately before rerunning.

## Local build

`build.bat` pauses at the end (success or failure). `build.bat -NoPause` is noninteractive. Normal builds produce the same ZIP and SHA256 in `dist/`; `-SkipPackage` skips the ZIP and SHA-256 generation while keeping the checks and app build. `-SkipTests` never creates distributable assets.

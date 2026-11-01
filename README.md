# Ostriv Studio

Ostriv Studio is an independent, experimental Windows companion for inspecting and editing an Ostriv settlement. It can save local automation profiles, add supported stock once, keep supported quantities available while you play, and help inspect farm and trade settings.

**This is an early preview, not a complete replacement for the original editor.** Memory writes are enabled only for the exact game executable listed in [COMPATIBILITY.md](COMPATIBILITY.md), and only for the combinations described in [FEATURE-COVERAGE.md](FEATURE-COVERAGE.md). Back up your save before use. The editor cannot guarantee that a game will not crash or that its building scan is complete.

## Install

1. Download the latest `OstrivStudio.Editor-win-Setup.exe` from [GitHub releases](https://github.com/marwanatef24/ostriv-studio/releases).
2. Run the installer and launch **Ostriv Studio** from the Start menu. Windows may identify an unsigned preview installer as unrecognized.
3. Start Ostriv and load the settlement you want to use. In the editor select **Choose game** if it is not found automatically, then **Scan game**.

The installer includes the Windows x64 runtime. Installed copies check for updates on launch; **Updates** checks manually. When an update is available, review its changelog and choose whether to install it. Applying an update restarts the editor. Profiles are stored outside the installation folder.

## Find buildings and edit stock

Search the building list by building or stock name. Each row summarizes its observed stored resources; select a row to inspect its inventory. Ctrl-click or Shift-click to select several candidates. Candidate labels and amounts are snapshots, not persistent in-game building identifiers; verify them against Ostriv before a write and scan again after loading a settlement.

- Choose **Add once** under Resources to preview a positive addition to an existing supported stored resource in each selected building. It does not create a saved rule or add a missing stock entry.
- Choose **Keep a minimum** to save a minimum quantity in the selected profile. A rule applies to matching buildings of the chosen type; it is not pinned to a particular building instance.
- **Resources** in the top navigation shows the observed stock breakdown, session history and CSV export. Candidate totals can overlap and need not match the town total.

Before a write, choose the currently loaded save. The app copies and verifies it and the autosaves into a backup folder and asks you to check the selected candidates. It cannot prove that the selected save is the one currently loaded. If a batch reports a partial or uncertain write, do not save the game; close Ostriv and restore the backup. Ostriv can autosave even while paused.

## Saved rules and capacity

Open **Saved rules** to create or import a profile. Choose **Each building** to maintain a minimum separately in each matching building, or **Across buildings** to maintain one combined minimum for supported existing stock. Profiles, enabled switches, capacity settings and intervals persist when the editor is reopened; they do not store process addresses.

Open **Run automation**, review a complete scan, select the loaded save for backup and start the runner. Supported stock and capacity rules are reapplied while that monitor remains open. Capacity minimums currently cover granaries, warehouses and big hay barracks. Capacity can reset when the game reloads: scan, review and start the monitor again after loading. Automation does not start on its own when the editor opens, and it stops when the game session or candidate set changes, a scan is incomplete, or a write fails. Unsupported rules are reported as skipped.

Profiles live at `%LOCALAPPDATA%\OstrivStudio\profiles.json`. The optional `OSTRIV_STUDIO_DATA_DIR` environment variable changes that directory. **Import original rules…** copies supported rules from the original editor's `.userpref` file into a new profile without changing the source. Close the original editor before importing where possible; other preferences are not imported.

If an earlier preview was run inside the Codex desktop app, the first standalone launch copies its Windows-redirected profile to the normal location only if no standalone profile already exists. The earlier file is retained.

## Farms and trading

Select a grain farm and open the Farms tab, then **Open farm planner…** to review fields, reorder an existing future crop sequence, replace a supported future crop, or change ploughing for selected fields. Choose from 25 built-in crop rotation suggestions or save and load your own named presets. Suggestions rotate around the current crop and must match the field length; unsupported crop introductions are blocked. Custom plans are stored in crop-presets.json beside your profiles, with protection against stale saves from another window. The current crop and rotation length remain locked. Other farm controls are not implemented.

For deals, open the world map in Ostriv, select a city with an active deal, scan, and open the Trading tab, then **View active deals…**. A selected export deal can prepare its remaining quantity as a minimum for an existing export-stock item on a matching trading post or dock. **Allocate exports across buildings…** combines remaining exports by resource and splits the required quantities across selected route-compatible buildings, or assigns each resource to a building in turn. Higher targets stay unchanged; every allocated resource must already have an export entry. The Trading tab also opens bulk export-target controls. Existing dock broadcloth and clothes can be increased once or maintained by supported stock rules. These actions do not create a deal or export item, change deal quantities, or move stock directly; other dock stock and direct deal editing are not implemented.

## Markets

Find rowhouse shops in the building browser, open Resources and choose Clothes, Shoes or Salt. Add once or save a minimum up to the normal 100-unit retail target. These controls support existing records only. Other retail goods, market stalls and missing retail records are blocked.

## Build and test

Install the .NET 8 SDK or newer on Windows, then run:

```powershell
dotnet build .\OstrivStudio\OstrivStudio.csproj -c Release
dotnet run --project .\OstrivStudio.Tests -c Release -- C:\path\to\a\new\empty\test-folder
```

The console tests use simulated memory and an isolated profile directory. They do not exercise all behavior in the live game. See [TESTING.md](TESTING.md) for the scope of the live checks and [CONTRIBUTING.md](CONTRIBUTING.md) for the release process.

Ostriv Studio is not affiliated with Ostriv or the original editor. See [third-party notices](THIRD-PARTY-NOTICES.md) and the [changelog](CHANGELOG.md).

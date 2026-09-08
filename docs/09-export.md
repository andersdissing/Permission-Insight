# 09 — Export

Cuts across phases 2 to 5. Build the download path with the first tab that needs
it; add the OneDrive option once the rest works.

## Scope rules

**Sharing links** can always be exported in full. They come from one
site-collection call and do not depend on which libraries were scanned.

**Broken inheritance** exports per library, always. A whole-site export requires
every list and library to have been scanned; if some have not, say how many and
offer to scan them rather than refusing.

**Person results** export as they are shown, with the coverage caveat carried
into the file.

## Two destinations

> **v1 ships one.** Export is a browser download; OneDrive is deferred to
> [`backlog.md`](backlog.md), and the delegated `Files.ReadWrite` scope is
> removed from the app registration along with it. See
> [`adr/0005`](adr/0005-onedrive-export-deferred.md). The rest of this section
> stands as the specification for when it is picked up.

The user chooses download or OneDrive each time.

**Download is the default.** It needs no permission at all, and an application
that works fully without any write scope is easier to defend.

**OneDrive** uses delegated `Files.ReadWrite`, acquired incrementally at the
moment the user picks it, never at sign-in. Never `Files.ReadWrite.All` as an
application permission — that would grant write access to every user's files in
the tenant, which is worse than the problem it was meant to solve.

The OneDrive advantage is governance, not security. The file lands somewhere
covered by the organisation's own policies and sensitivity labels instead of a
downloads folder. Say that plainly rather than implying it is safer.

Disable the `beforeunload` guard before any consent redirect. Popup flow avoids
the issue; see `03-client-storage.md`.

## File format

CSV, UTF-8 with BOM so Excel opens Danish characters correctly.

Every file opens with comment lines before the header row:

```
# Permission Insight export
# Site: {title} ({url})
# Scope: {library name | whole site | person: {name}}
# Generated: {ISO 8601} by {user principal name}
# Coverage: {which libraries are included, which are not}
```

The coverage line is not optional. A file leaves the application's access
control the moment it is written and will be read by someone who never saw the
screen. Without it, a person report with no rows becomes evidence that someone
had no access, when it may only mean two libraries were never scanned.

## Columns

**Sharing links**: `Path`, `ItemType`, `LinkScope`, `LinkRole`, `Recipients`,
`External`, `Expiry`, `HasPassword`, `GroupId`, `Status`.

`Status` distinguishes resolved from orphaned. Orphaned rows stay in the file;
an orphaned group holding an external guest is a finding.

**Broken inheritance**: `Path`, `ItemType`, `List`, `Source`, `Principal`,
`PrincipalType`, `Role`, `LinkScope`, `Expiry`.

`Source` is `Direct grant` or `Sharing link`. `Limited Access` rows are excluded,
matching what is shown on screen.

One row per principal, not per item, so the file can be filtered by person in a
spreadsheet.

**Person results**: `Scope`, `Path`, `EffectiveRole`, `Deviation`, `Source`.

`Deviation` is `Baseline`, `Elevated`, `Reduced` or `No access`. `Source` carries
the ambiguity wording verbatim when elimination did not resolve it.

## Logging

Log exports separately from views, with destination, scope and row count. The
data leaves the application at this point and this is the last place it can be
recorded.

## Repository hygiene

`*.csv` and any output directory are in `.gitignore` from the first commit. An
export is a complete index of an organisation's most exposed documents and is
the worst thing that could reach a public repository. See
`04-open-source-repository.md`.

## Acceptance

1. A CSV opens in Excel with Danish characters intact.
2. Every file carries the five comment lines, including coverage.
3. Whole-site export offers to scan missing libraries rather than failing.
4. ~~OneDrive export requests consent only on first use, and the file appears
   in the signed-in user's own drive.~~ Deferred with the destination; carried
   into [`backlog.md`](backlog.md).
5. A person export with zero rows still states which libraries were covered.
6. Export writes an audit entry with destination and row count.
